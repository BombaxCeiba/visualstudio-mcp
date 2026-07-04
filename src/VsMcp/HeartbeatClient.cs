using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsMcp
{
    /// <summary>
    /// 按固定节拍经 VS 侧 pipe 写入 heartbeat 帧来探测 Gateway 存活。写入成功
    /// 意味着 pipe（亦即 Gateway）仍连接；写入失败累积连续未命中计数，一旦
    /// 未命中时长跨过 <see cref="_deadThreshold"/> 即视 Gateway 已死并触发
    /// 抢占式重新拉起（设计文档 §抢占式 Gateway 拉起）。
    /// <para>
    /// 生命周期刻意镜像 <see cref="KeepAliveNotifier"/>：构造即忘（循环在构造
    /// 函数里启动）、linked CTS、所有循环异常被吞（heartbeat 绝不能崩 VS）、
    /// 以及同步非阻塞的 <see cref="Dispose"/>（取消 CTS + 返回；循环在下一次
    /// 延迟时观察到取消并退出）。在 Dispose 里 await 循环会在 VS 退出路径上
    /// 变成 sync-over-async——正是本设计规避的已知死锁根因（见
    /// PipeMcpServer.Dispose 注释）。
    /// </para>
    /// <para>
    /// heartbeat 发送方与重新拉起入口都以委托注入，使循环逻辑可用毫秒级
    /// 间隔做单元测试，无需拉起真实 pipe 或 Gateway 进程——与 KeepAliveNotifier
    /// 对其 sendKeepAlive 委托所用的同一可测性理念。生产接线传
    /// <c>ct =&gt; pipe.SendHeartbeatAsync(ct)</c> 与
    /// <c>ct =&gt; GatewayLauncher.EnsureGatewayRunningAsync(ct)</c>。
    /// </para>
    /// </summary>
    public sealed class HeartbeatClient : IDisposable
    {
        private readonly int _pid;
        private readonly Func<CancellationToken, Task> _sendHeartbeat;
        private readonly Func<CancellationToken, Task<bool>> _ensureGatewayRunning;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _deadThreshold;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        private readonly ILogger? _logger;
        private int _disposed;

        /// <summary>
        /// 启动 heartbeat 循环。构造即刻开始探测。
        /// </summary>
        /// <param name="pid">VS 进程 id，出现在诊断日志中。</param>
        /// <param name="sendHeartbeat">一次 heartbeat 尝试。若帧送达 Gateway 则
        /// 正常完成；若 pipe 断开或写入失败则抛异常。"Gateway 存活"在操作上
        /// 与"写入成功"不可区分，这正是我们需要的存活信号。</param>
        /// <param name="ensureGatewayRunning">抢占式重新拉起入口
        /// （生产：<c>GatewayLauncher.EnsureGatewayRunningAsync</c>）。
        /// 在累计未命中窗口跨过死亡阈值时调用。port-bind 锁保证若多个 VS 实例
        /// 竞争重新拉起，只有一个新 Gateway 存活。</param>
        /// <param name="interval">探测间隔（生产：5s）。必须为正。</param>
        /// <param name="deadThreshold">视 Gateway 已死并触发重新拉起的累计
        /// 未命中时长（生产：15s = 每次 5s 共 3 次）。必须为正。</param>
        /// <param name="callerToken">VS package 的 DisposalToken；取消它即停止
        /// 循环。默认 <see cref="CancellationToken.None"/>。</param>
        /// <param name="logger">可选诊断 logger。</param>
        public HeartbeatClient(
            int pid,
            Func<CancellationToken, Task> sendHeartbeat,
            Func<CancellationToken, Task<bool>> ensureGatewayRunning,
            TimeSpan? interval = null,
            TimeSpan? deadThreshold = null,
            CancellationToken callerToken = default,
            ILogger? logger = null)
        {
            _sendHeartbeat = sendHeartbeat ?? throw new ArgumentNullException(nameof(sendHeartbeat));
            _ensureGatewayRunning = ensureGatewayRunning ?? throw new ArgumentNullException(nameof(ensureGatewayRunning));

            _pid = pid;
            _interval = interval ?? TimeSpan.FromSeconds(5);
            _deadThreshold = deadThreshold ?? TimeSpan.FromSeconds(15);
            _logger = logger;

            if (_interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "interval must be positive");
            if (_deadThreshold <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(deadThreshold), "deadThreshold must be positive");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            int failCount = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

                    try
                    {
                        await _sendHeartbeat(ct).ConfigureAwait(false);
                        failCount = 0; // Gateway 应答了，清零未命中计数。
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        failCount++;
                        // Gateway 已不可达 failCount*interval 之久。
                        // 当该时长跨过死亡阈值时，视其已崩溃 / 被 taskkill，
                        // 抢占式重新拉起。
                        if (failCount * _interval.Ticks >= _deadThreshold.Ticks)
                        {
                            _logger?.LogDebug(
                                "Heartbeat: PID {Pid} missed {Fails} consecutive probes ({Elapsed} >= {Threshold}); triggering preemptive Gateway relaunch",
                                _pid, failCount,
                                TimeSpan.FromTicks(failCount * _interval.Ticks), _deadThreshold);
                            try
                            {
                                await _ensureGatewayRunning(ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                return;
                            }
                            catch
                            {
                                // 重新拉起本身失败（exe 尚未落盘等）。
                                // ConnectLoop 会持续重试 pipe；重置并让下个未命中
                                // 窗口再次触发。绝不向上传播。
                            }
                            failCount = 0; // 给新 Gateway 一个全新窗口。
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* VS 正在释放 */ }
            catch
            {
                // heartbeat 循环严格尽力而为；此处的意外故障绝不能搞垮 VS。
            }
        }

        /// <summary>
        /// 停止循环。同步且非阻塞：取消 linked CTS 并立即返回。循环在下一次
        /// Task.Delay 时观察到取消并退出；此返回后最多有一个在途 heartbeat
        /// 可能完成（无害）。经 interlocked 守卫实现幂等。在此 await
        /// <see cref="_loop"/> 会在 VS 退出路径上变成 sync-over-async——正是
        /// 本方法规避的死锁。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* 竞态 */ }
            try { _cts.Dispose(); } catch { /* 尽力而为 */ }
        }
    }
}
