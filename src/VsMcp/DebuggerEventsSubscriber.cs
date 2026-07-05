using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;

namespace VsMcp
{
    /// <summary>
    /// 订阅 <see cref="DebuggerEvents"/> 的模式切换事件，把 design/break/running
    /// 转换以 <c>debugger-state-changed</c> pipe 推送转发给 Gateway。若没有它，
    /// Gateway 缓存的 <c>InstanceEntry.DebuggerState</c> 永远停在 register 后
    /// OnConnected 推送的初值，list_vs_instances 返回的状态在用户启动/停止调试、
    /// 命中/离开断点、单步时都不会刷新。
    ///
    /// 三个事件直接映射到 <see cref="DebuggerFacade.MapState"/> 的状态字符串：
    /// OnEnterBreakMode → "break"，OnEnterRunMode → "running"，
    /// OnEnterDesignMode → "design"。不在回调里重入 GetDebuggerStateAsync 做额外
    /// 查询——事件本身就携带状态语义（与 SolutionEventsSubscriber 内联读路径的取舍
    /// 一致）。不去重：单步会触发 break→run→break，简单去重会丢掉最终 break 态，
    /// 让缓存错误地停在 running；pipe 帧廉价，每个事件都推。
    ///
    /// COM 回调抵达 UI 线程。我们把映射出的状态字符串交给一个 fire-and-forget
    /// 任务去写 pipe 帧。pipe 写入吞掉自己的错误，故瞬态 Gateway 断开绝不会崩 UI。
    ///
    /// <b>事件源保活</b>：<see cref="DebuggerEvents"/>（来自
    /// <c>dte.Events.DebuggerEvents</c>）必须字段持有。COM 互操作的事件订阅底层是
    /// IConnectionPoint；若让事件源被 GC，订阅会静默失效。
    /// <see cref="DebuggerFacade.ContinueExecutionAsync"/> 用局部变量保活能工作仅
    /// 因为 await 期间栈保活；本类是长生命周期订阅，必须字段保存。
    /// </summary>
    internal sealed class DebuggerEventsSubscriber : IDisposable
    {
        private readonly AsyncPackage _package;
        private readonly DebuggerFacade _facade;
        private readonly PipeMcpServer _pipe;
        private readonly CancellationToken _callerToken;
        private readonly ILogger? _logger;

        private DebuggerEvents? _debuggerEvents;

        public DebuggerEventsSubscriber(
            AsyncPackage package, DebuggerFacade facade, PipeMcpServer pipe,
            CancellationToken callerToken, ILogger? logger)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _facade = facade ?? throw new ArgumentNullException(nameof(facade));
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _callerToken = callerToken;
            _logger = logger;
        }

        /// <summary>
        /// 在 UI 线程上获取 DTE 并订阅 DebuggerEvents。幂等且尽力而为：若 DTE 不可用，
        /// 订阅者保持惰性（register 帧 + 重连时 OnConnected 仍能让 Gateway 拿到初值；
        /// 此处只是让它变实时）。
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _logger?.LogInformation("DebuggerEventsSubscriber.InitializeAsync: on UI thread, acquiring DebuggerEvents");

            DTE2? dte = null;
            try
            {
                dte = (DTE2?)await _package.GetServiceAsync(typeof(DTE)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogInformation("InitializeAsync: DTE acquire threw: {Message}", ex.Message);
            }

            if (dte == null)
            {
                _logger?.LogInformation("InitializeAsync: DTE is null — subscription aborted");
                return;
            }

            try
            {
                // 关键：dte.Events.DebuggerEvents 返回事件源 RCW，必须存字段保活——
                // 否则 GC 回收它时 IConnectionPoint 订阅静默失效（事件再不触发）。
                _debuggerEvents = dte.Events.DebuggerEvents;
                _debuggerEvents.OnEnterBreakMode += OnBreak;
                _debuggerEvents.OnEnterRunMode += OnRun;
                _debuggerEvents.OnEnterDesignMode += OnDesign;
                _logger?.LogInformation("InitializeAsync: subscribed to DebuggerEvents — state changes WILL push");
            }
            catch (Exception ex)
            {
                _logger?.LogInformation("InitializeAsync: DebuggerEvents subscription threw: {Message}", ex.Message);
            }
        }

        // COM 事件回调在 UI 线程触发。ref 参数签名必须精确匹配 EnvDTE 委托；
        // 不读 reason/action，仅用事件类型本身映射状态。
        private void OnBreak(EnvDTE.dbgEventReason reason, ref EnvDTE.dbgExecutionAction action)
            => PushState("break");

        private void OnRun(EnvDTE.dbgEventReason reason)
            => PushState("running");

        private void OnDesign(EnvDTE.dbgEventReason reason)
            => PushState("design");

        /// <summary>
        /// 在 UI 线程上读取当前调试器模式并推送给 Gateway。由 PipeMcpServer 在每次
        /// 成功 register 后调用（与 SolutionEventsSubscriber.PushCurrentSolutionAsync
        /// 同一 OnConnected 回调），弥合"连接前已发生的状态变化"时序缺口，并在 Gateway
        /// 重连后重新同步。自行跳到 UI 线程，故调用方（一个后台 connect-loop 任务）不必
        /// 在 UI 线程上。
        /// </summary>
        public async Task PushCurrentStateAsync(CancellationToken ct)
        {
            try
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                // GetDebuggerStateAsync 会再次切到 UI 线程（我们已在上面切过，幂等），
                // 取 MapState 规范化后的 state 字符串。失败则不推（OnConnected 自愈 +
                // 后续 DebuggerEvents 仍会刷新）。
                var result = await _facade.GetDebuggerStateAsync(ct).ConfigureAwait(false);
                PushState(result.State);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* 关停 */ }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "PushCurrentStateAsync failed");
            }
        }

        /// <summary>
        /// fire-and-forget 地推送 pipe。COM 回调必须及时返回，故实际的帧写入在后台
        /// 任务上运行。SendDebuggerStateChangedAsync 吞掉 IO 错误；漏掉的推送会在下次
        /// 重连时经 OnConnected 回调自愈。
        /// </summary>
        private void PushState(string? state)
        {
            _logger?.LogInformation("DebuggerEvents push: state={State}", state ?? "(null)");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pipe.SendDebuggerStateChangedAsync(state, _callerToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "SendDebuggerStateChangedAsync threw (best-effort; ignored)");
                }
            });
        }

        public void Dispose()
        {
            var events = _debuggerEvents;
            if (events != null)
            {
                try
                {
                    events.OnEnterBreakMode -= OnBreak;
                    events.OnEnterRunMode -= OnRun;
                    events.OnEnterDesignMode -= OnDesign;
                }
                catch { /* 尽力而为 */ }
            }
            _debuggerEvents = null;
        }
    }
}
