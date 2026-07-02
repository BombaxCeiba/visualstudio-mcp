using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsMcp.Logging
{
    /// <summary>
    /// <see cref="ILoggerProvider"/> that writes log events to the Visual Studio
    /// Output Window pane named <see cref="PaneName"/> ("VS MCP") for
    /// <see cref="LogLevel.Information"/> and above, and additionally records
    /// <see cref="LogLevel.Error"/> and <see cref="LogLevel.Critical"/> events
    /// to <see cref="IVsActivityLog"/> (the durable Activity Log that VS only
    /// populates when launched with the <c>/log</c> switch; otherwise it is a
    /// silent no-op).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pane and the Activity Log service are acquired lazily on the UI
    /// thread on the first log write (mirroring the acquire-on-demand pattern
    /// used by <c>DebuggerFacade.GetVsDebuggerAsync</c>). Once acquired they
    /// are cached, so subsequent writes incur no thread hops.
    /// </para>
    /// <para>
    /// Per D-06: <see cref="IVsActivityLog"/> only persists to disk when VS is
    /// started with <c>/log</c>; without that switch its <see cref="IVsActivityLog.LogEntry"/>
    /// calls are silent. Real-time diagnostics always go to the Output Window
    /// pane; the Activity Log is purely a forensic channel.
    /// </para>
    /// </remarks>
    internal sealed class VsOutputWindowLoggerProvider : ILoggerProvider
    {
        // Typed as AsyncPackage (not IAsyncServiceProvider) so we can reach
        // JoinableTaskFactory.SwitchToMainThreadAsync, mirroring the
        // DebuggerFacade.GetDteAsync acquire-on-UI-thread pattern exactly.
        // AsyncPackage implements IAsyncServiceProvider, so the package passes
        // itself ("this") directly.
        private readonly AsyncPackage _package;

        private IVsOutputWindowPane? _pane;
        private IVsActivityLog? _activityLog;
        private bool _paneAcquired;

        /// <summary>
        /// Name of the Output Window pane this provider writes to. Visible to
        /// the user in the VS Output Window pane picker.
        /// </summary>
        internal const string PaneName = "VS MCP";

        /// <summary>
        /// Source string recorded against each <see cref="IVsActivityLog"/> entry.
        /// </summary>
        internal const string ActivityLogSource = "VSDebuggerMcp";

        public VsOutputWindowLoggerProvider(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public ILogger CreateLogger(string name) => new PaneLogger(this, name);

        /// <summary>
        /// Lazily acquires <see cref="IVsOutputWindow"/> (creating the pane) and
        /// <see cref="IVsActivityLog"/> on the UI thread. Idempotent: the first
        /// caller does the work; subsequent callers return immediately.
        /// </summary>
        private async ValueTask EnsureAcquiredAsync()
        {
            if (_paneAcquired)
                return;

            // Mark acquired BEFORE awaiting so reentrant log writes do not
            // re-enter this method while the first caller is on the UI thread
            // awaiting GetServiceAsync. Any write that arrives mid-acquire
            // will observe _pane/_activityLog possibly null and safely no-op.
            _paneAcquired = true;

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var outputWindow = await _package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow != null)
            {
                try
                {
                    var paneGuid = Guid.NewGuid();
                    // fInitVisible=1 (visible), fClearWithSolution=0 (persist across solution loads)
                    outputWindow.CreatePane(ref paneGuid, PaneName, 1, 0);
                    outputWindow.GetPane(ref paneGuid, out _pane);
                }
                catch
                {
                    // Pane creation failure must never crash logging.
                    _pane = null;
                }
            }

            _activityLog = await _package.GetServiceAsync(typeof(SVsActivityLog)) as IVsActivityLog;
            // _activityLog is non-null even without /log; LogEntry is a silent
            // no-op in that case (D-06).
        }

        /// <summary>
        /// No-op. The Output Window pane is VS-owned and outlives this provider;
        /// the Activity Log has no handle to release.
        /// </summary>
        public void Dispose()
        {
            // Intentionally empty: pane + Activity Log are VS-owned services.
        }

        /// <summary>
        /// <see cref="ILogger"/> implementation that formats each event and
        /// routes it to the Output Window pane (Information+) and, for Error+,
        /// also to the Activity Log. Writes happen off the UI thread via a
        /// continuation; the only UI-thread work is the one-time service
        /// acquisition in <see cref="EnsureAcquiredAsync"/>.
        /// </summary>
        private sealed class PaneLogger : ILogger
        {
            private readonly VsOutputWindowLoggerProvider _owner;
            private readonly string _category;

            internal PaneLogger(VsOutputWindowLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                // Format eagerly so the off-thread continuation only does IO.
                string message = $"[{_category}] {logLevel}: {formatter(state, exception)}";
                if (exception != null)
                {
                    message += Environment.NewLine + exception.GetType().FullName + ": " + exception.Message;
                }

                // Acquire-on-demand then write off-thread. Never block the
                // caller (loggers are called from request hot paths and from
                // the SDK transport).
                _ = _owner.EnsureAcquiredAsync().AsTask().ContinueWith(
                    _ =>
                    {
                        try
                        {
                            _owner._pane?.OutputStringThreadSafe(message + Environment.NewLine);
                        }
                        catch
                        {
                            // Pane write failure (disposed, race) must never throw.
                        }

                        if (logLevel >= LogLevel.Error && _owner._activityLog != null)
                        {
                            try
                            {
                                // Verified signature (Microsoft.VisualStudio.Interop.dll,
                                // VS2022 17.0.0.0): LogEntry(uint actType, string, string).
                                // ALE_ERROR = 1; both Error and Critical map to ALE_ERROR
                                // (no dedicated critical entry type).
                                const uint ALE_ERROR = (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR;
                                _owner._activityLog.LogEntry(ALE_ERROR, ActivityLogSource, message);
                            }
                            catch
                            {
                                // Activity Log write failure must never throw.
                            }
                        }
                    },
                    TaskScheduler.Default);
            }

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => NullScope.Instance;
        }
    }

    /// <summary>
    /// Empty disposable scope. Logging scopes are not surfaced in the Output
    /// Window, so all <c>BeginScope</c> calls in this extension return this
    /// shared no-op instance. Lives at namespace level so multiple logger
    /// implementations (provider, factory wrapper) can share it.
    /// </summary>
    internal sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();

        private NullScope() { }

        public void Dispose() { /* no-op */ }
    }
}
