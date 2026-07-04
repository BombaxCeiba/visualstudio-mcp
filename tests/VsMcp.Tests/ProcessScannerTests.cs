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
    /// ProcessScanner 双机制自毁逻辑。每个计时器都是毫秒级，devenv 探测是
    /// 可注入的 Func，所以没有测试触碰真实进程表或等待数秒。两个机制分别
    /// 隔离测试（禁用一个、演练另一个）以及一起测试。
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
            // emptyGracePeriod 极大，所以只有机制 2（devenv 扫描）能触发。
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
            // 几次扫描无 VS（远低于 200ms 宽限），然后 VS 出现。
            await Task.Delay(50);
            vsAlive = true;
            // VS 存活期间有大量扫描 —— 证明倒计时已取消。
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
            // anyDevenvAlive=true + 极大的 noVsGracePeriod 禁用机制 2，所以
            // 只有机制 1（空 registry）能触发。
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
            // 一个 dummy 条目使 Count > 0；MemoryStream 读循环立即 EOF 退出，
            // 所以无后台工作残留。
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
            // Registry 空约 60ms（低于 100ms 宽限），然后一个 VS 连接。
            await Task.Delay(60);
            registry.Register(new InstanceEntry(
                new PipeRouter(new MemoryStream(), ownsStream: false),
                new PipeRegister { Pid = 99 },
                DateTime.UtcNow));
            // 连接后足够多的扫描以证明倒计时已取消。
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
            // 循环在首次 kill 后立即返回，但多等一点时间以证明即使 scanner
            // 尚未观察到取消，互斥守卫仍把 kills 保持为 1。
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
            // 循环在取消时停止；无 kill、无抛异常。
            await Task.Delay(60);
            Assert.Equal(0, kills);
        }

        /// <summary>
        /// 一旦 VS 已连接随后其 pipe 断开（registry 变空），机制 1 必须立即
        /// 自毁且无宽限 —— pipe 断开是显式退出信号。此处 emptyGracePeriod 极大，
        /// 以证明 kill 是连接后立即路径，而非宽限期满。
        /// </summary>
        [Fact]
        public async Task VS_Connected_Then_Disconnected_Kills_Immediately()
        {
            int kills = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var registry = new InstanceRegistry();
            // anyDevenvAlive=true + 极大的宽限禁用机制 2 和启动窗口，
            // 所以只有机制 1 的连接后路径能触发。
            var scanner = new ProcessScanner(
                registry,
                onSelfKill: () => { Interlocked.Increment(ref kills); gate.TrySetResult(true); },
                cts.Token,
                anyDevenvAlive: () => true,
                scanInterval: TimeSpan.FromMilliseconds(20),
                emptyGracePeriod: TimeSpan.FromSeconds(60),
                noVsGracePeriod: TimeSpan.FromSeconds(60));

            scanner.Start();
            // 一个 VS 连接 → registry 非空 → _everPopulated 变为 true。
            registry.Register(new InstanceEntry(
                new PipeRouter(new MemoryStream(), ownsStream: false),
                new PipeRegister { Pid = 7 },
                DateTime.UtcNow));
            // 让 scanner 观察到非空 registry。
            await Task.Delay(60);
            // VS 断开 → registry 空。下一次扫描必须立即 kill，
            // 远低于 60s 宽限。
            registry.Remove(7);
            await gate.Task.WaitForAsync(OpTimeoutMs);
            cts.Cancel();
            scanner.Dispose();

            Assert.True(kills >= 1, $"expected immediate self-kill after VS disconnect, got {kills}");
        }
    }
}
