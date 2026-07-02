using System;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcp
{
    /// <summary>
    /// 在长任务（<c>build_solution</c> / <c>continue_execution</c> 等待模式）执行期间，
    /// 周期性调用一个「发送保活」委托。
    /// <para>
    /// 该委托由调用方提供，负责构造并发送 MCP logging 通知
    /// （<c>notifications/message</c>）。这些通知走 Streamable HTTP 的 POST 响应
    /// SSE 流——客户端收到即视为响应活动，从而重置其工具调用超时（约 60s），
    /// 避免长任务被误判为断连而触发恢复。通知只显示在客户端调试面板，
    /// 不注入模型上下文、不消耗 token。
    /// </para>
    /// <para>
    /// 通知由 SDK 实时 flush（<c>SseEventWriter.WriteAsync</c> 写后立即
    /// <c>FlushAsync</c>），故不再需要自写 SSE <c>: ping</c> 注释保活。
    /// </para>
    /// <para>
    /// 保活循环与主任务并行：主任务返回后调用方 <see cref="Dispose"/> 取消循环。
    /// 把「发什么」抽成委托而非硬编码 <c>McpServer</c>，使本类可在测试中
    /// 注入计数委托验证周期行为，无需构造真实 MCP server。
    /// </para>
    /// </summary>
    public sealed class KeepAliveNotifier : IDisposable
    {
        private readonly Func<CancellationToken, Task> _sendKeepAlive;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;

        /// <summary>已成功发送的保活次数（测试/诊断用）。</summary>
        public int NotificationsSent { get; private set; }

        /// <summary>
        /// 启动保活循环。构造即开始周期性调用 <paramref name="sendKeepAlive"/>；
        /// <see cref="Dispose"/> 停止。
        /// </summary>
        /// <param name="sendKeepAlive">每次周期触发的发送委托；抛异常视为发送失败，不影响主任务。</param>
        /// <param name="interval">发送间隔，须远小于客户端的工具调用超时。</param>
        /// <param name="cancellationToken">随主任务取消；主任务结束后应 Dispose。</param>
        public KeepAliveNotifier(
            Func<CancellationToken, Task> sendKeepAlive,
            TimeSpan interval,
            CancellationToken cancellationToken)
        {
            _sendKeepAlive = sendKeepAlive ?? throw new ArgumentNullException(nameof(sendKeepAlive));
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "interval must be positive");
            _interval = interval;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = RunAsync(_cts.Token);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_interval, ct).ConfigureAwait(false);

                    try
                    {
                        await _sendKeepAlive(ct).ConfigureAwait(false);
                        NotificationsSent++;
                    }
                    catch
                    {
                        // 发送失败（客户端已断开等）不影响主任务，也不计入成功次数。
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 主任务完成 / 取消时退出循环。
            }
            catch
            {
                // 循环内的意外异常不得影响主任务。
            }
        }

        public void Dispose()
        {
            // 取消并丢弃 loop：loop 会在下一次 Task.Delay 观察到取消而退出，
            // 最多多发一条通知（无害）。不在此阻塞 await，避免在 tool 委托的
            // 同步 Dispose 路径上死锁。
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
