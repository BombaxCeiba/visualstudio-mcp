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
    /// HeartbeatClient loop logic. Every timer is ms-scale (mirrors the
    /// KeepAliveNotifierTests pattern) so each test finishes in well under a
    /// second. The send and relaunch entry points are injected delegates, so no
    /// real pipe or Gateway process is spawned.
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

            // Block until the first relaunch fires (3 misses at 20ms = 60ms window).
            await gate.Task.WaitForAsync(OpTimeoutMs);
            client.Dispose();

            Assert.True(launches >= 1, $"expected relaunch to fire, got {launches}");
        }

        [Fact]
        public async Task Mid_Sequence_Success_Resets_Miss_Count()
        {
            // Fail twice (below the 3-miss threshold of 60ms), then succeed. The
            // success must reset failCount to 0 so two subsequent failures (which
            // never happen here) would still need a full 3-miss window. No relaunch
            // should ever fire.
            int launches = 0;
            int sends = 0;
            using var done = new CancellationTokenSource(OpTimeoutMs);
            var client = new HeartbeatClient(
                pid: 3,
                sendHeartbeat: ct =>
                {
                    int n = Interlocked.Increment(ref sends);
                    if (n <= 2) throw new IOException("miss"); // first 2 fail
                    return Task.CompletedTask;                  // rest succeed
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
            client.Dispose(); // idempotent — must not throw

            int snapshot = sends;
            await Task.Delay(120);
            // At most one in-flight send may complete after Dispose (the loop
            // observes cancellation on its next Task.Delay).
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
