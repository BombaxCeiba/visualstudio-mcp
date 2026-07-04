using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsMcp.Logging
{
    /// <summary>
    /// <see cref="ILoggerProvider"/>，把日志事件写入名为 <see cref="PaneName"/>
    /// （"VS MCP"）的 Visual Studio Output Window 面板（<see cref="LogLevel.Information"/>
    /// 及以上），并把 <see cref="LogLevel.Error"/> 与 <see cref="LogLevel.Critical"/>
    /// 事件额外记录到 <see cref="IVsActivityLog"/>（持久化的 Activity Log，VS 仅
    /// 在以 <c>/log</c> 开关启动时才写入；否则为静默空操作）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 面板与 Activity Log 服务在首次日志写入时于 UI 线程惰性获取（镜像
    /// <c>DebuggerFacade.GetVsDebuggerAsync</c> 用的按需获取模式）。一旦获取即
    /// 缓存，故后续写入不产生线程跳转。
    /// </para>
    /// <para>
    /// 按 D-06：<see cref="IVsActivityLog"/> 仅在 VS 以 <c>/log</c> 启动时持久化
    /// 到磁盘；无该开关时其 <see cref="IVsActivityLog.LogEntry"/> 调用静默。实时
    /// 诊断总是去 Output Window 面板；Activity Log 纯粹是取证通道。
    /// </para>
    /// </remarks>
    internal sealed class VsOutputWindowLoggerProvider : ILoggerProvider
    {
        // 类型为 AsyncPackage（而非 IAsyncServiceProvider），以便能拿到
        // JoinableTaskFactory.SwitchToMainThreadAsync，精确镜像
        // DebuggerFacade.GetDteAsync 的 UI 线程获取模式。
        // AsyncPackage 实现 IAsyncServiceProvider，故 package 把自身
        // （"this"）直接传入。
        private readonly AsyncPackage _package;

        private IVsOutputWindowPane? _pane;
        private IVsActivityLog? _activityLog;
        private bool _paneAcquired;

        /// <summary>
        /// 本 provider 写入的 Output Window 面板名。在 VS Output Window 面板
        /// 选择器中对用户可见。
        /// </summary>
        internal const string PaneName = "VS MCP";

        /// <summary>
        /// 记录在每个 <see cref="IVsActivityLog"/> 条目上的 source 字符串。
        /// </summary>
        internal const string ActivityLogSource = "VSDebuggerMcp";

        public VsOutputWindowLoggerProvider(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public ILogger CreateLogger(string name) => new PaneLogger(this, name);

        /// <summary>
        /// 在 UI 线程上惰性获取 <see cref="IVsOutputWindow"/>（创建面板）与
        /// <see cref="IVsActivityLog"/>。幂等：首个调用者干活；后续调用者立即
        /// 返回。
        /// </summary>
        private async ValueTask EnsureAcquiredAsync()
        {
            if (_paneAcquired)
                return;

            // 在 await 之前标记已获取，使重入的日志写入不会在首个调用者于
            // UI 线程 await GetServiceAsync 时重入本方法。获取中途到达的任何
            // 写入会观察到 _pane/_activityLog 可能为 null 并安全地空操作。
            _paneAcquired = true;

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var outputWindow = await _package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow != null)
            {
                try
                {
                    var paneGuid = Guid.NewGuid();
                    // fInitVisible=1（可见），fClearWithSolution=0（跨解决方案加载持久化）
                    outputWindow.CreatePane(ref paneGuid, PaneName, 1, 0);
                    outputWindow.GetPane(ref paneGuid, out _pane);
                }
                catch
                {
                    // 面板创建失败绝不能搞垮日志。
                    _pane = null;
                }
            }

            _activityLog = await _package.GetServiceAsync(typeof(SVsActivityLog)) as IVsActivityLog;
            // _activityLog 即使没有 /log 也非 null；此时 LogEntry 为静默
            // 空操作（D-06）。
        }

        /// <summary>
        /// 空操作。Output Window 面板为 VS 拥有，比本 provider 长寿；
        /// Activity Log 没有可释放的句柄。
        /// </summary>
        public void Dispose()
        {
            // 刻意为空：pane + Activity Log 是 VS 拥有的服务。
        }

        /// <summary>
        /// <see cref="ILogger"/> 实现，格式化每个事件并路由到 Output Window
        /// 面板（Information+），Error+ 还额外去 Activity Log。写入经
        /// continuation 在 UI 线程之外发生；唯一的 UI 线程工作是
        /// <see cref="EnsureAcquiredAsync"/> 中的一次性服务获取。
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

                // 及早格式化，使离线程 continuation 只做 IO。
                // 本地时间 + thread-id 前缀镜像 gateway.log 的 [tN]，故同一事件
                // 的 VS Output Window 行与 gateway.log 行在两个面上对齐。
                string message = $"{DateTime.Now:HH:mm:ss.fff} [t{Thread.CurrentThread.ManagedThreadId}] [{_category}] {logLevel}: {formatter(state, exception)}";
                if (exception != null)
                {
                    message += Environment.NewLine + exception.GetType().FullName + ": " + exception.Message;
                }

                // 按需获取然后离线程写入。绝不阻塞调用方（logger 从请求热路径
                // 与 SDK transport 调用）。
                _ = _owner.EnsureAcquiredAsync().AsTask().ContinueWith(
                    _ =>
                    {
                        try
                        {
                            _owner._pane?.OutputStringThreadSafe(message + Environment.NewLine);
                        }
                        catch
                        {
                            // 面板写入失败（已释放、竞态）绝不能抛。
                        }

                        if (logLevel >= LogLevel.Error && _owner._activityLog != null)
                        {
                            try
                            {
                                // 已验证签名（Microsoft.VisualStudio.Interop.dll，
                                // VS2022 17.0.0.0）：LogEntry(uint actType, string, string)。
                                // ALE_ERROR = 1；Error 与 Critical 都映射到 ALE_ERROR
                                // （没有专门的 critical 条目类型）。
                                const uint ALE_ERROR = (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR;
                                _owner._activityLog.LogEntry(ALE_ERROR, ActivityLogSource, message);
                            }
                            catch
                            {
                                // Activity Log 写入失败绝不能抛。
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
    /// 空的可释放 scope。日志 scope 不在 Output Window 中呈现，故本扩展中所有
    /// <c>BeginScope</c> 调用都返回此共享空操作实例。位于命名空间级别，使多个
    /// logger 实现（provider、factory 包装）能共享它。
    /// </summary>
    internal sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();

        private NullScope() { }

        public void Dispose() { /* 空操作 */ }
    }
}
