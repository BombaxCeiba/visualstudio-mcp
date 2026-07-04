extern alias vsmpc;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vsmpc::VsMcp;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// HeartbeatClient 循环逻辑。每个计时器都是毫秒级（沿用 KeepAliveNotifierTests
    /// 的模式），使每个测试在远不到一秒内完成。发送和重启入口是注入的委托，
    /// 所以不会启动真实 pipe 或 Gateway 进程。
    /// </summary>
    public class HeartbeatClientTests
    {
        private const int OpTimeoutMs = 8000;

        [Fact]
        public async Task All_Successes_Never_Triggers_Relaunch()
        {
            int launches = 0;
            int sends = 0;
            using var done = new CancellationTokenSource(OpTimeoutMs);
            var client = new HeartbeatClient(
                pid: 1,
                sendHeartbeat: ct => { Interlocked.Increment(ref sends); return Task.CompletedTask; },
                ensureGatewayRunning: ct => { Interlocked.Increment(ref launches); return Task.FromResult(true); },
                interval: TimeSpan.FromMilliseconds(20),
                deadThreshold: TimeSpan.FromMilliseconds(60),
                callerToken: done.Token);

            var sw = Stopwatch.StartNew();
            while (sends < 5 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);
            client.Dispose();

            Assert.True(sends >= 5, $"expected >=5 sends, got {sends}");
            Assert.Equal(0, launches);
        }

        [Fact]
        public async Task Sustained_Failure_Triggers_Relaunch_At_Threshold()
        {
            int launches = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var done = new CancellationTokenSource(OpTimeoutMs);
            var client = new HeartbeatClient(
                pid: 2,
                sendHeartbeat: ct => throw new IOException("pipe gone"),
                ensureGatewayRunning: ct =>
                {
                    Interlocked.Increment(ref launches);
                    gate.TrySetResult(true);
                    return Task.FromResult(true);
                },
                interval: TimeSpan.FromMilliseconds(20),
                deadThreshold: TimeSpan.FromMilliseconds(60),
                callerToken: done.Token);

            // 阻塞直到首次重启触发（20ms 下 3 次 miss = 60ms 窗口）。
            await gate.Task.WaitForAsync(OpTimeoutMs);
            client.Dispose();

            Assert.True(launches >= 1, $"expected relaunch to fire, got {launches}");
        }

        [Fact]
        public async Task Mid_Sequence_Success_Resets_Miss_Count()
        {
            // 失败两次（低于 60ms 的 3-miss 阈值），然后成功。成功必须把
            // failCount 重置为 0，这样后续两次失败（此处不会发生）仍需完整
            // 3-miss 窗口。不应触发任何重启。
            int launches = 0;
            int sends = 0;
            using var done = new CancellationTokenSource(OpTimeoutMs);
            var client = new HeartbeatClient(
                pid: 3,
                sendHeartbeat: ct =>
                {
                    int n = Interlocked.Increment(ref sends);
                    if (n <= 2) throw new IOException("miss"); // 前 2 次失败
                    return Task.CompletedTask;                  // 其余成功
                },
                ensureGatewayRunning: ct => { Interlocked.Increment(ref launches); return Task.FromResult(true); },
                interval: TimeSpan.FromMilliseconds(20),
                deadThreshold: TimeSpan.FromMilliseconds(60),
                callerToken: done.Token);

            var sw = Stopwatch.StartNew();
            while (sends < 6 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);
            client.Dispose();

            Assert.True(sends >= 6, $"expected >=6 sends, got {sends}");
            Assert.Equal(0, launches);
        }

        [Fact]
        public async Task Dispose_Is_Idempotent_And_Stops_Loop()
        {
            int sends = 0;
            var client = new HeartbeatClient(
                pid: 4,
                sendHeartbeat: ct => { Interlocked.Increment(ref sends); return Task.CompletedTask; },
                ensureGatewayRunning: ct => Task.FromResult(true),
                interval: TimeSpan.FromMilliseconds(20),
                deadThreshold: TimeSpan.FromMilliseconds(60));

            var sw = Stopwatch.StartNew();
            while (sends < 2 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);

            client.Dispose();
            client.Dispose(); // 幂等 —— 不得抛异常

            int snapshot = sends;
            await Task.Delay(120);
            // Dispose 后最多一次在途发送可能完成（循环在下一次 Task.Delay
            // 时观察到取消）。
            Assert.True(sends - snapshot <= 1, $"loop kept running after Dispose: {sends - snapshot} extra sends");
        }

        [Fact]
        public async Task External_Cancellation_Stops_Loop()
        {
            using var cts = new CancellationTokenSource();
            int sends = 0;
            var client = new HeartbeatClient(
                pid: 5,
                sendHeartbeat: ct => { Interlocked.Increment(ref sends); return Task.CompletedTask; },
                ensureGatewayRunning: ct => Task.FromResult(true),
                interval: TimeSpan.FromMilliseconds(20),
                deadThreshold: TimeSpan.FromMilliseconds(60),
                callerToken: cts.Token);

            var sw = Stopwatch.StartNew();
            while (sends < 2 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);

            cts.Cancel();
            int snapshot = sends;
            await Task.Delay(120);
            client.Dispose();

            Assert.True(snapshot >= 2, $"expected >=2 sends before cancel, got {snapshot}");
            Assert.True(sends - snapshot <= 1, $"loop kept running after cancellation: {sends - snapshot} extra sends");
        }

        [Fact]
        public void Constructor_Rejects_NonPositive_Timers()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new HeartbeatClient(1, ct => Task.CompletedTask, ct => Task.FromResult(true),
                    interval: TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new HeartbeatClient(1, ct => Task.CompletedTask, ct => Task.FromResult(true),
                    interval: TimeSpan.FromMilliseconds(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new HeartbeatClient(1, ct => Task.CompletedTask, ct => Task.FromResult(true),
                    deadThreshold: TimeSpan.Zero));
        }

        [Fact]
        public void Constructor_Rejects_Null_Delegates()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new HeartbeatClient(1, null!, ct => Task.FromResult(true)));
            Assert.Throws<ArgumentNullException>(() =>
                new HeartbeatClient(1, ct => Task.CompletedTask, null!));
        }
    }
}
