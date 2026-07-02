using System;
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
    /// VSIX package entry point that starts the MCP HTTP server when the extension loads.
    /// Uses AsyncPackage for background initialization and automatic background loading.
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
        private McpHttpServer? _mcpServer;
        private DebuggerFacade? _facade;
        private SymbolFacade? _symbolFacade;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Build the ILoggerFactory once. D-04: the provider writes to the
            // VS Output Window pane 'VS MCP' (Information+, D-05/D-07
            // fixed Information for Phase 5) and additionally to IVsActivityLog
            // (Error/Critical, D-06). Passing the factory into McpHttpServer
            // also lets the MCP SDK surface its own transport/server log lines
            // into the same pane.
            //
            // Uses the local SimpleLoggerFactory rather than Microsoft.Extensions
            // .Logging.LoggerFactory: the concrete LoggerFactory lives in a
            // NuGet package that the project does not reference, and this
            // extension only ever wires a single provider, so a minimal factory
            // covers the contract without adding a dependency.
            var loggerFactory = new SimpleLoggerFactory(
                new VsOutputWindowLoggerProvider(this),
                LogLevel.Information);

            // D-01: the package no longer owns a CancellationTokenSource.
            // McpHttpServer creates its own linked CTS internally and tears
            // itself down via DisposeAsync. The cancellation token we pass
            // here is AsyncPackage.DisposalToken (RESEARCH Pitfall #1) — when
            // VS disposes the package, this token cancels and the linked CTS
            // inside the server propagates cancellation to RunAsync.
            _facade = new DebuggerFacade(this);
            _symbolFacade = new SymbolFacade(this);

            // Read opt-in tool settings from Tools → Options. GetDialogPage
            // requires the UI thread (we're on it after the switch above).
            // Settings are read once at startup; changing them needs a VS restart.
            bool enableGoToDefinition = false;
            try { enableGoToDefinition = ((McpOptionsPage)GetDialogPage(typeof(McpOptionsPage))).EnableGoToDefinition; } catch { }

            // Port 43210 (not 3001): see McpHttpServer ctor doc. 3001 landed in a
            // Windows TCP excluded range on this machine (WSAEACCES/10013); 43210
            // sits above the dynamic port range and binds cleanly.
            _mcpServer = new McpHttpServer(port: 43210, authToken: null, loggerFactory: loggerFactory, facade: _facade, symbolFacade: _symbolFacade, enableGoToDefinition: enableGoToDefinition);

            // Observable startup (D-08 + REMEDIATION F-5 §2 Step B): the
            // previously-silent fire-and-forget startup now captures any
            // exception (e.g. bind failure) and logs it via the factory
            // before it propagates. Never await the server loop inside
            // InitializeAsync -- VS has a package load timeout.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _mcpServer.StartAsync(this.DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger<VsMcpPackage>()
                                 .LogError(ex, "MCP server failed to start: {Message}", ex.Message);
                }
            }, this.DisposalToken);
        }

        /// <summary>
        /// Path B of the F-3 A+B hybrid teardown (RESEARCH Open Question #1).
        /// Best-effort, NON-blocking: fire-and-forget the server's async
        /// disposal. NEVER call .GetAwaiter().GetResult() here — that was the
        /// F-3 deadlock (sync-over-async on the UI thread during VS exit).
        /// McpHttpServer.DisposeAsync is idempotent (guarded by _disposeLock +
        /// _disposed), so the same fire-and-forget is safe to invoke again
        /// from IVsPackage.Close() below.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_mcpServer != null)
                        {
                            await _mcpServer.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Observed — teardown must never throw.
                    }
                });
                _facade?.Dispose();
                _symbolFacade?.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Path A of the F-3 A+B hybrid teardown (RESEARCH Pitfall #1 +
        /// Open Question #1). Explicit-interface implementation of
        /// IVsPackage.Close() — VS invokes this on exit. Same idempotent
        /// non-blocking fire-and-forget as Dispose(bool). Note: AsyncPackage
        /// has NO DisposeAsync(CancellationToken) virtual hook (verified by
        /// reflection on VS2022 17.0.0.0 and VS2026 18.0.0.0 shells); the
        /// forbidden override signature would fail to compile.
        /// </summary>
        int Microsoft.VisualStudio.Shell.Interop.IVsPackage.Close()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_mcpServer != null)
                    {
                        await _mcpServer.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Observed — teardown must never throw.
                }
            });
            _facade?.Dispose();
            _symbolFacade?.Dispose();
            return VSConstants.S_OK;
        }
    }
}
