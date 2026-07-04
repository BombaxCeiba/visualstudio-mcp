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
    /// Wave 3 控制帧路径：VS 经 pipe 推送一个 <c>solution-changed</c> 帧（无
    /// id）；PipeRouter 读循环识别它并通过构造回调分派，而非丢弃（无 id 帧的
    /// 旧行为）。Gateway 回调随后刷新 InstanceRegistry，使 ① Header 层无需
    /// 重连即可看到最新的 SolutionDir。
    /// </summary>
    public class SolutionChangedTests
    {
        private const int OpTimeoutMs = 8000;

        [Fact]
        public async Task PipeRouter_DispatchesSolutionChangedFrame_ToCallback()
        {
            var tcs = new TaskCompletionSource<PipeSolutionChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frame = new PipeSolutionChanged
            {
                Pid = 777,
                SolutionPath = "D:\\proj\\New.sln",
                SolutionDir = "D:\\proj",
                SolutionName = "New.sln",
            };
            byte[] frameBytes = PipeFraming.BuildFrameBytes(frame);

            // 一个只含单帧 payload 的 MemoryStream：读循环读取它、把控制帧
            // 分派给回调，然后读到 EOF 退出。MemoryStream 的 ReadAsync 同步完成，
            // 所以构造后回调立即触发。
            using var stream = new MemoryStream(frameBytes);
            using var router = new PipeRouter(stream, ownsStream: false, onChange =>
            {
                tcs.TrySetResult(onChange);
            });

            PipeSolutionChanged received = await tcs.Task.WaitForAsync(OpTimeoutMs);
            Assert.Equal(777, received.Pid);
            Assert.Equal("D:\\proj\\New.sln", received.SolutionPath);
            Assert.Equal("D:\\proj", received.SolutionDir);
            Assert.Equal("New.sln", received.SolutionName);
        }

        /// <summary>
        /// Gateway 回调把推送合并进既有 InstanceEntry（保留推送 DTO 省略的
        /// PipeName/VsVersion）。针对真实 InstanceRegistry 校验
        /// Program.OnSolutionChanged 的精确合并逻辑，使 solution 切换后的
        /// header 路由能取到新的 SolutionDir。
        /// </summary>
        [Fact]
        public void SolutionChangedCallback_RefreshesRegistry_SolutionDir()
        {
            var registry = new InstanceRegistry();
            var dummy = new MemoryStream();
            var router = new PipeRouter(dummy, ownsStream: false);
            var original = new PipeRegister
            {
                Pid = 777,
                PipeName = "vs-mcp-gateway",
                SolutionPath = null,
                SolutionDir = null,
                VsVersion = "18.0",
            };
            registry.Register(new InstanceEntry(router, original, DateTime.UtcNow));

            // 镜像 Program.OnSolutionChanged 的合并（保留既有条目的
            // PipeName/VsVersion；solution 字段取自推送）。
            var pushed = new PipeSolutionChanged
            {
                Pid = 777,
                SolutionPath = "D:\\proj\\New.sln",
                SolutionDir = "D:\\proj",
                SolutionName = "New.sln",
            };
            var merged = new PipeRegister
            {
                Pid = pushed.Pid,
                PipeName = original.PipeName,
                SolutionName = pushed.SolutionName,
                SolutionDir = pushed.SolutionDir,
                SolutionPath = pushed.SolutionPath,
                VsVersion = original.VsVersion,
            };
            registry.UpdateInfo(777, merged);

            Assert.True(registry.TryGet(777, out var after));
            Assert.Equal("D:\\proj\\New.sln", after!.Info.SolutionPath);
            Assert.Equal("D:\\proj", after.Info.SolutionDir);
            Assert.Equal("vs-mcp-gateway", after.Info.PipeName);   // 已保留
            Assert.Equal("18.0", after.Info.VsVersion);             // 已保留

            // 刷新后的 SolutionDir 现在对 ① Header 层可见。
            var match = WorkspaceResolver.Resolve("D:\\proj",
                new[] { (after.Info.Pid, after.Info.SolutionDir) });
            Assert.Equal(WorkspaceMatchKind.Single, match.Kind);
            Assert.Equal(777, match.Pid);
        }

        [Fact]
        public async Task PipeRouter_UnknownControlFrame_DoesNotInvokeCallback()
        {
            // heartbeat（Wave 4）或任何其他无 id 帧都不得触发 solution-changed
            // 回调；它被静默丢弃，这样推送未知控制帧的较新 VS 对端永远不会
            // 破坏读循环。
            var invoked = false;
            byte[] frameBytes = PipeFraming.BuildFrameBytes(new PipeHeartbeat { Pid = 1 });

            using var stream = new MemoryStream(frameBytes);
            using var router = new PipeRouter(stream, ownsStream: false, _ => invoked = true);

            // 给读循环一点时间处理帧 + EOF。回调如果触发是在读取时同步执行的，
            // 所以短延迟足够。
            await Task.Delay(200);
            Assert.False(invoked);
        }

        [Fact]
        public async Task PipeRouter_DispatchesHeartbeatFrame_ToCallback()
        {
            // Wave 4：heartbeat 控制帧路由到专用 onHeartbeat 回调（而非
            // solution-changed 回调），使 Gateway 能在每次 VS 心跳时刷新
            // InstanceEntry.LastSeen。
            var tcs = new TaskCompletionSource<PipeHeartbeat>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] frameBytes = PipeFraming.BuildFrameBytes(new PipeHeartbeat { Pid = 42 });

            using var stream = new MemoryStream(frameBytes);
            using var router = new PipeRouter(stream, ownsStream: false,
                onSolutionChanged: null,
                onHeartbeat: beat => tcs.TrySetResult(beat));

            PipeHeartbeat received = await tcs.Task.WaitForAsync(OpTimeoutMs);
            Assert.Equal(42, received.Pid);
        }
    }

    internal static class TaskTestExtensions
    {
        /// <summary>带硬超时等待，使挂起的分派快速失败测试而非卡死运行器。</summary>
        public static async Task<T> WaitForAsync<T>(this Task<T> task, int timeoutMs)
        {
            var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (winner != task)
                throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for the pipe callback.");
            return await task;
        }
    }
}
