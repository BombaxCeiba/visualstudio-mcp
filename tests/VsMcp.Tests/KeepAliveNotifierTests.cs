extern alias vsmpc;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using vsmpc::VsMcp;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// 验证 KeepAliveNotifier 的周期发送、异常容忍、取消与 Dispose 行为。
    /// 用 Stopwatch 轮询等待阈值而非固定 Delay，降低 CI 上的 flaky。
    /// </summary>
    public class KeepAliveNotifierTests
    {
        [Fact]
        public async Task Sends_At_Interval_Until_Disposed()
        {
            int calls = 0;
            var notifier = new KeepAliveNotifier(
                sendKeepAlive: ct => { calls++; return Task.CompletedTask; },
                interval: TimeSpan.FromMilliseconds(20),
                cancellationToken: CancellationToken.None);

            var sw = Stopwatch.StartNew();
            while (calls < 3 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);

            Assert.True(calls >= 3, $"expected >=3 sends, got {calls}");
            Assert.Equal(calls, notifier.NotificationsSent);

            notifier.Dispose();
            int snapshot = calls;

            // Dispose 后循环应停止（最多一次在途发送）。
            await Task.Delay(120);
            Assert.True(calls - snapshot <= 1, $"loop kept running after Dispose: {calls - snapshot} extra sends");
        }

        [Fact]
        public async Task Send_Failure_Does_Not_Stop_Loop()
        {
            int calls = 0;
            var notifier = new KeepAliveNotifier(
                sendKeepAlive: ct => { calls++; throw new InvalidOperationException("boom"); },
                interval: TimeSpan.FromMilliseconds(20),
                cancellationToken: CancellationToken.None);

            var sw = Stopwatch.StartNew();
            while (calls < 3 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);
            notifier.Dispose();

            Assert.True(calls >= 3, $"loop stopped on send exception after {calls} calls");
            Assert.Equal(0, notifier.NotificationsSent);
        }

        [Fact]
        public async Task External_Cancellation_Stops_Loop()
        {
            using var cts = new CancellationTokenSource();
            int calls = 0;
            var notifier = new KeepAliveNotifier(
                sendKeepAlive: ct => { calls++; return Task.CompletedTask; },
                interval: TimeSpan.FromMilliseconds(20),
                cancellationToken: cts.Token);

            var sw = Stopwatch.StartNew();
            while (calls < 2 && sw.ElapsedMilliseconds < 2000)
                await Task.Delay(5);

            cts.Cancel();
            int snapshot = calls;
            await Task.Delay(120);
            notifier.Dispose();

            Assert.True(snapshot >= 2, $"expected >=2 sends before cancel, got {snapshot}");
            Assert.True(calls - snapshot <= 1, $"loop kept running after cancellation: {calls - snapshot} extra sends");
        }

        [Fact]
        public void Constructor_Rejects_NonPositive_Interval()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new KeepAliveNotifier(ct => Task.CompletedTask, TimeSpan.Zero, CancellationToken.None));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new KeepAliveNotifier(ct => Task.CompletedTask, TimeSpan.FromMilliseconds(-1), CancellationToken.None));
        }

        [Fact]
        public void Constructor_Rejects_Null_SendKeepAlive()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new KeepAliveNotifier(null!, TimeSpan.FromSeconds(1), CancellationToken.None));
        }
    }
}
