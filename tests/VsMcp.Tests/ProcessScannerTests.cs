using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// ProcessScanner double-mechanism self-kill logic. Every timer is ms-scale
    /// and the devenv probe is an injectable Func, so no test touches the real
    /// process table or waits seconds. The two mechanisms are tested in
    /// isolation (disable one, exercise the other) and together.
    /// </summary>
    public class ProcessScannerTests
    {
        private const int OpTimeoutMs = 8000;

        [Fact]
        public async Task No_Devenv_Past_Grace_Triggers_SelfKill()
        {
            int kills = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            // emptyGracePeriod is huge so only mechanism 2 (devenv scan) can fire.
            var scanner = new ProcessScanner(
                new InstanceRegistry(),
                onSelfKill: () => { Interlocked.Increment(ref kills); gate.TrySetResult(true); },
                cts.Token,
                anyDevenvAlive: () => false,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromSeconds(60),
                noVsGracePeriod: TimeSpan.FromMilliseconds(40));

            scanner.Start();
            await gate.Task.WaitForAsync(OpTimeoutMs);
            cts.Cancel();
            scanner.Dispose();

            Assert.True(kills >= 1, $"expected self-kill from no devenv, got {kills}");
        }

        [Fact]
        public async Task Devenv_Reappears_Cancels_Countdown()
        {
            int kills = 0;
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            bool vsAlive = false;
            var scanner = new ProcessScanner(
                new InstanceRegistry(),
                onSelfKill: () => Interlocked.Increment(ref kills),
                cts.Token,
                anyDevenvAlive: () => vsAlive,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromSeconds(60),
                noVsGracePeriod: TimeSpan.FromMilliseconds(200));

            scanner.Start();
            // No VS for a few scans (well under the 200ms grace), then VS appears.
            await Task.Delay(50);
            vsAlive = true;
            // Plenty of scans while VS is alive — proves the countdown was cancelled.
            await Task.Delay(200);
            cts.Cancel();
            scanner.Dispose();

            Assert.Equal(0, kills);
        }

        [Fact]
        public async Task Empty_Registry_Past_Grace_Triggers_SelfKill()
        {
            int kills = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            // anyDevenvAlive=true + huge noVsGracePeriod disable mechanism 2, so
            // only mechanism 1 (empty registry) can fire.
            var scanner = new ProcessScanner(
                new InstanceRegistry(),
                onSelfKill: () => { Interlocked.Increment(ref kills); gate.TrySetResult(true); },
                cts.Token,
                anyDevenvAlive: () => true,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromMilliseconds(40),
                noVsGracePeriod: TimeSpan.FromSeconds(60));

            scanner.Start();
            await gate.Task.WaitForAsync(OpTimeoutMs);
            cts.Cancel();
            scanner.Dispose();

            Assert.True(kills >= 1, $"expected self-kill from empty registry, got {kills}");
        }

        [Fact]
        public async Task NonEmpty_Registry_Does_Not_Trigger()
        {
            int kills = 0;
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var registry = new InstanceRegistry();
            // A dummy entry makes Count > 0; the MemoryStream read loop exits on
            // immediate EOF so no background work lingers.
            registry.Register(new InstanceEntry(
                new PipeRouter(new MemoryStream(), ownsStream: false),
                new PipeRegister { Pid = 1 },
                DateTime.UtcNow));

            var scanner = new ProcessScanner(
                registry,
                onSelfKill: () => Interlocked.Increment(ref kills),
                cts.Token,
                anyDevenvAlive: () => true,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromMilliseconds(40),
                noVsGracePeriod: TimeSpan.FromSeconds(60));

            scanner.Start();
            await Task.Delay(200);
            cts.Cancel();
            scanner.Dispose();

            Assert.Equal(0, kills);
        }

        [Fact]
        public async Task Empty_Then_Instance_Connects_Cancels_Countdown()
        {
            int kills = 0;
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var registry = new InstanceRegistry();
            var scanner = new ProcessScanner(
                registry,
                onSelfKill: () => Interlocked.Increment(ref kills),
                cts.Token,
                anyDevenvAlive: () => true,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromMilliseconds(100),
                noVsGracePeriod: TimeSpan.FromSeconds(60));

            scanner.Start();
            // Registry empty for ~60ms (under the 100ms grace), then a VS connects.
            await Task.Delay(60);
            registry.Register(new InstanceEntry(
                new PipeRouter(new MemoryStream(), ownsStream: false),
                new PipeRegister { Pid = 99 },
                DateTime.UtcNow));
            // Enough scans post-connect to prove the countdown was cancelled.
            await Task.Delay(200);
            cts.Cancel();
            scanner.Dispose();

            Assert.Equal(0, kills);
        }

        [Fact]
        public async Task Both_Mechanisms_Satisfied_Fires_SelfKill_Only_Once()
        {
            int kills = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var scanner = new ProcessScanner(
                new InstanceRegistry(),
                onSelfKill: () => { Interlocked.Increment(ref kills); gate.TrySetResult(true); },
                cts.Token,
                anyDevenvAlive: () => false,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromMilliseconds(40),
                noVsGracePeriod: TimeSpan.FromMilliseconds(40));

            scanner.Start();
            await gate.Task.WaitForAsync(OpTimeoutMs);
            // The loop returns immediately after the first kill, but let extra
            // time pass to prove the interlocked guard keeps kills at 1 even if
            // the scanner hadn't yet observed cancellation.
            await Task.Delay(120);
            cts.Cancel();
            scanner.Dispose();

            Assert.Equal(1, kills);
        }

        [Fact]
        public async Task Cancellation_Stops_Loop_Cleanly()
        {
            int kills = 0;
            using var cts = new CancellationTokenSource();
            var scanner = new ProcessScanner(
                new InstanceRegistry(),
                onSelfKill: () => Interlocked.Increment(ref kills),
                cts.Token,
                anyDevenvAlive: () => true,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromSeconds(60),
                noVsGracePeriod: TimeSpan.FromSeconds(60));

            scanner.Start();
            await Task.Delay(60);
            cts.Cancel();
            scanner.Dispose();
            // Loop stops on cancellation; no kill, no throw.
            await Task.Delay(60);
            Assert.Equal(0, kills);
        }
    }
}
