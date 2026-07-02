using System;
using System.IO;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsMcp
{
    /// <summary>
    /// Subscribes to <see cref="IVsSolutionEvents"/> and forwards open/close
    /// transitions to the Gateway as <c>solution-changed</c> pipe pushes (设计文档
    /// §Solution 信息动态更新). Without this, the Gateway's routing table would
    /// keep the solution path captured at register time forever, so the ① Header
    /// tier and list_vs_instances would go stale whenever the user opens/closes a
    /// solution after VS startup.
    ///
    /// Subscription uses the COM connection-point pattern
    /// (<see cref="IConnectionPointContainer"/>/<see cref="IConnectionPoint"/>)
    /// rather than the SDK's <c>SolutionEvents</c> helper: IVsSolutionEvents
    /// fires reliably for both .sln and folder/CMake opens, whereas the EnvDTE
    /// <c>DTE.Events.SolutionEvents</c> is brittle for folder projects (the core
    /// C++ use case — see the package's NoSolution autoload rationale).
    ///
    /// The COM callbacks arrive on the UI thread. We read
    /// <c>DTE.Solution.FullName</c> inline (safe on the UI thread, no thread hop
    /// — avoids reentering <see cref="DebuggerFacade.GetSessionInfoAsync"/> which
    /// would itself switch to the UI thread) and then hand the resolved path to
    /// a fire-and-forget task that writes the pipe frame. The pipe write swallows
    /// its own errors, so a transient Gateway disconnect never crashes the UI.
    /// </summary>
    internal sealed class SolutionEventsSubscriber : IVsSolutionEvents, IDisposable
    {
        private readonly AsyncPackage _package;
        private readonly PipeMcpServer _pipe;
        private readonly CancellationToken _callerToken;
        private readonly ILogger? _logger;

        private DTE2? _dte;
        private IVsSolution? _solution;
        private IConnectionPoint? _connectionPoint;
        // VSCOOKIE_NIL is 0; System.Runtime.InteropServices.ComTypes.Advise/Unadvise
        // use int cookies on this surface, so we track an int and treat 0 as "no advise".
        private int _cookie;

        public SolutionEventsSubscriber(AsyncPackage package, PipeMcpServer pipe, CancellationToken callerToken, ILogger? logger)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _callerToken = callerToken;
            _logger = logger;
        }

        /// <summary>
        /// Acquire <see cref="IVsSolution"/> + <see cref="DTE2"/> on the UI
        /// thread and advise the solution-events connection point. Idempotent
        /// and best-effort: if the services are unavailable, the subscriber
        /// stays inert (the register frame + reconnects still keep the Gateway
        /// roughly correct; this just makes it live).
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct)
        {
            // SwitchToMainThreadAsync returns MainThreadAwaitable which has no
            // ConfigureAwait — await it directly (DebuggerFacade uses the same form).
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            try
            {
                _dte = (DTE2?)await _package.GetServiceAsync(typeof(DTE)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to acquire DTE for solution-events subscription");
            }

            try
            {
                _solution = (IVsSolution?)await _package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to acquire IVsSolution for solution-events subscription");
            }

            if (_solution == null) return;

            // Cast the RCW to the COM connection-point container and find the
            // outbound interface for IVsSolutionEvents. Advise registers `this`
            // as the sink (the runtime builds a CCW exposing IVsSolutionEvents).
            if (_solution is IConnectionPointContainer cpc)
            {
                Guid iid = typeof(IVsSolutionEvents).GUID;
                try
                {
                    cpc.FindConnectionPoint(ref iid, out _connectionPoint);
                    if (_connectionPoint != null)
                        _connectionPoint.Advise(this, out _cookie);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "IVsSolutionEvents Advise failed; solution-changed push disabled");
                }
            }
        }

        // ───────────────────────── IVsSolutionEvents ──────────────────────────
        //
        // Only the two open/close solution transitions trigger a push. Every
        // other callback returns S_OK so VS proceeds with its default behavior.

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            PushCurrentSolution();
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
        {
            // Solution is gone → push nulls so the Gateway renders "(no solution)".
            PushSolutionValues(solutionPath: null, solutionDir: null, solutionName: null);
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pHierarchy, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        /// <summary>
        /// Read the current solution path inline (we're on the UI thread — the COM
        /// event delivered us here) and hand it to the fire-and-forget push. This
        /// avoids any thread hop to read the path: reentering
        /// <see cref="DebuggerFacade.GetSessionInfoAsync"/> from the callback would
        /// switch to the UI thread we're already on and do extra work (project
        /// enumeration) we don't need.
        /// </summary>
        private void PushCurrentSolution()
        {
            string? path = null, dir = null, name = null;
            try
            {
                string fullName = _dte?.Solution?.FullName ?? "";
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    path = fullName;
                    try { dir = Path.GetDirectoryName(fullName); } catch { }
                    try { name = Path.GetFileName(fullName); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read DTE.Solution.FullName in solution-event callback");
            }
            PushSolutionValues(path, dir, name);
        }

        /// <summary>
        /// Fire-and-forget the pipe push. The COM callback must return promptly,
        /// so the actual frame write runs on a background task. SendSolutionChanged
        /// swallows IO errors; a missed push self-heals on the next reconnect via
        /// the register frame.
        /// </summary>
        private void PushSolutionValues(string? solutionPath, string? solutionDir, string? solutionName)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pipe.SendSolutionChangedAsync(solutionPath, solutionDir, solutionName, _callerToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "SendSolutionChangedAsync threw (best-effort; ignored)");
                }
            });
        }

        public void Dispose()
        {
            if (_connectionPoint != null && _cookie != 0)
            {
                try { _connectionPoint.Unadvise(_cookie); } catch { /* best-effort */ }
            }
            _cookie = 0;
            _connectionPoint = null;
        }
    }
}
