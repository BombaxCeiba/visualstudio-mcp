using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsMcp.Logging;
using Task = System.Threading.Tasks.Task;

namespace VsMcp
{
    /// <summary>
    /// VSIX package entry point. Under the multi-instance architecture this VS instance
    /// no longer binds an HTTP port — it opens a NamedPipe (<c>\\.\pipe\vs-mcp-{PID}</c>)
    /// and hands the tool surface to a standalone VsMcpGateway.exe that owns :43210 and
    /// routes MCP clients to the right instance over the pipe. The same
    /// <see cref="McpRequestProcessor"/> backs the pipe path, so the tool set, error
    /// semantics, and keep-alive behavior are identical to the former direct-HTTP path.
    ///
    /// Auto-loads via UIContextGuids80.NoSolution (active whenever NO .sln is loaded —
    /// i.e. at startup AND when an "Open Folder"/CMake project is open). We previously
    /// tried SolutionExists, but CMake projects opened via File &gt; Open &gt; CMake do
    /// NOT load a .sln, so SolutionExists never fired and the server never started for
    /// the C++ workflow — the core use case. NoSolution covers CMake/folder projects;
    /// the trade-off is the server also starts at bare VS startup (WakaTime-style),
    /// which is acceptable because C++ debugger control needs the server up without a
    /// .sln. A VS package loads once and does not unload, so it stays available.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(McpOptionsPage), "Visual Studio MCP", "Tools", 1000, 1001, true)]
    [Guid("a3f7c5e1-8b2d-4f6a-9c0e-1d3b5a7f2e4d")]
    public sealed class VsMcpPackage : AsyncPackage, IVsPackage
    {
        private PipeMcpServer? _pipeServer;
        private HeartbeatClient? _heartbeat;
        private DebuggerFacade? _facade;
        private SymbolFacade? _symbolFacade;
        private SolutionEventsSubscriber? _solutionSubscriber;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Build the ILoggerFactory once. D-04: the provider writes to the
            // VS Output Window pane 'VS MCP' (Information+, D-05/D-07
            // fixed Information for Phase 5) and additionally to IVsActivityLog
            // (Error/Critical, D-06).
            //
            // Uses the local SimpleLoggerFactory rather than Microsoft.Extensions
            // .Logging.LoggerFactory: the concrete LoggerFactory lives in a
            // NuGet package that the project does not reference, and this
            // extension only ever wires a single provider, so a minimal factory
            // covers the contract without adding a dependency.
            var loggerFactory = new SimpleLoggerFactory(
                new VsOutputWindowLoggerProvider(this),
                LogLevel.Information);

            _facade = new DebuggerFacade(this);
            _symbolFacade = new SymbolFacade(this);

            // Read opt-in tool settings from Tools → Options. GetDialogPage
            // requires the UI thread (we're on it after the switch above).
            // Settings are read once at startup; changing them needs a VS restart.
            bool enableGoToDefinition = false;
            try { enableGoToDefinition = ((McpOptionsPage)GetDialogPage(typeof(McpOptionsPage))).EnableGoToDefinition; } catch { }

            // Open the per-instance connection to the Gateway. PipeMcpServer now
            // (Wave 2) dials the Gateway's "vs-mcp-gateway" pipe as a client and
            // sends a register frame; the Gateway multiplexes MCP clients onto it.
            // PipeMcpServer.StartAsync returns once the connect loop is armed (the
            // loop runs in the background), so this await does not block
            // InitializeAsync past the VS package-load timeout.
            int pid = Process.GetCurrentProcess().Id;
            _pipeServer = new PipeMcpServer(
                pid, _facade, _symbolFacade, enableGoToDefinition, loggerFactory, this.DisposalToken);

            // Best-effort: make sure the standalone Gateway exe is up before the
            // pipe client starts, so the first connect attempt lands. Fire-and-
            // forget — if the Gateway can't be launched (exe not on disk yet, Wave 5
            // will ship it in the VSIX), the pipe client keeps retrying connect.
            var pkgLogger = loggerFactory.CreateLogger<VsMcpPackage>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await GatewayLauncher.EnsureGatewayRunningAsync(this.DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pkgLogger.LogError(ex, "Gateway launch/probe failed: {Message}", ex.Message);
                }
            });

            try
            {
                await _pipeServer.StartAsync(this.DisposalToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pkgLogger.LogError(ex, "MCP pipe server failed to start: {Message}", ex.Message);
            }

            // Heartbeat: periodically probe the Gateway's liveness by writing a
            // heartbeat frame through the pipe. A sustained miss window (Gateway
            // crashed / taskkilled) triggers a preemptive relaunch so VS self-heals
            // without user intervention (设计文档 §抢占式 Gateway 拉起). The loop
            // starts in the constructor and stops on Dispose; it never blocks VS
            // exit (HeartbeatClient.Dispose is synchronous + non-blocking, cloning
            // KeepAliveNotifier's teardown pattern).
            _heartbeat = new HeartbeatClient(
                pid,
                sendHeartbeat: ct => _pipeServer.SendHeartbeatAsync(ct),
                ensureGatewayRunning: ct => GatewayLauncher.EnsureGatewayRunningAsync(ct),
                callerToken: this.DisposalToken,
                logger: loggerFactory.CreateLogger<HeartbeatClient>());

            // Subscribe to solution open/close so the Gateway's routing table
            // (SolutionDir used by the ① Header tier + list_vs_instances) stays
            // live as the user opens/closes solutions after VS startup. Runs on
            // the UI thread (needs IVsSolution + DTE services); fire-and-forget
            // so a slow service query never blocks package load.
            _solutionSubscriber = new SolutionEventsSubscriber(
                this, _pipeServer, this.DisposalToken, loggerFactory.CreateLogger<SolutionEventsSubscriber>());
            _ = Task.Run(async () =>
            {
                try
                {
                    await _solutionSubscriber.InitializeAsync(this.DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pkgLogger.LogDebug(ex, "Solution-events subscription failed; solution-changed push disabled");
                }
            });
        }

        /// <summary>
        /// Path B of the F-3 A+B hybrid teardown (RESEARCH Open Question #1).
        /// Best-effort, NON-blocking. PipeMcpServer.Dispose() is synchronous and
        /// non-blocking (cancels the accept loop and fire-and-forgets the processor's
        /// async teardown), so calling it here cannot deadlock VS exit. Idempotent
        /// (PipeMcpServer guards its _disposed flag).
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _solutionSubscriber?.Dispose(); } catch { /* teardown must never throw */ }
                try { _heartbeat?.Dispose(); } catch { /* teardown must never throw */ }
                try { _pipeServer?.Dispose(); } catch { /* teardown must never throw */ }
                _facade?.Dispose();
                _symbolFacade?.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Path A of the F-3 A+B hybrid teardown (RESEARCH Pitfall #1 +
        /// Open Question #1). Explicit-interface implementation of
        /// IVsPackage.Close() — VS invokes this on exit. Same idempotent
        /// non-blocking teardown as Dispose(bool).
        /// </summary>
        int Microsoft.VisualStudio.Shell.Interop.IVsPackage.Close()
        {
            try { _solutionSubscriber?.Dispose(); } catch { /* teardown must never throw */ }
            try { _heartbeat?.Dispose(); } catch { /* teardown must never throw */ }
            try { _pipeServer?.Dispose(); } catch { /* teardown must never throw */ }
            _facade?.Dispose();
            _symbolFacade?.Dispose();
            return VSConstants.S_OK;
        }
    }
}
