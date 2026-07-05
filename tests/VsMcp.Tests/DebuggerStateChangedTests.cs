using System;
using System.IO;
using System.Threading.Tasks;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// debugger-state-changed 控制帧路径：VS 经 pipe 推送调试器模式切换（无 id）；
    /// PipeRouter 读循环识别它并通过构造回调分派。Gateway 回调随后刷新
    /// InstanceRegistry.DebuggerState，使 list_vs_instances 无需重连即可返回实时状态。
    /// </summary>
    public class DebuggerStateChangedTests
    {
        private const int OpTimeoutMs = 8000;

        [Fact]
        public async Task PipeRouter_DispatchesDebuggerStateChangedFrame_ToCallback()
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<PipeDebuggerStateChanged>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var frame = new PipeDebuggerStateChanged
            {
                Pid = 4242,
                State = "break",
            };
            byte[] frameBytes = PipeFraming.BuildFrameBytes(frame);

            // 一个只含单帧 payload 的 MemoryStream：读循环读取它、把控制帧分派给回调，
            // 然后读到 EOF 退出。
            using var stream = new MemoryStream(frameBytes);
            using var router = new PipeRouter(stream, ownsStream: false,
                onDebuggerStateChanged: changed => tcs.TrySetResult(changed));

            PipeDebuggerStateChanged received = await tcs.Task.WaitForAsync(OpTimeoutMs);
            Assert.Equal(4242, received.Pid);
            Assert.Equal("break", received.State);
        }

        /// <summary>
        /// Gateway 回调把推送的 state 写进 InstanceEntry.DebuggerState。校验
        /// InstanceRegistry.UpdateDebuggerState 的精确语义——刷新后 list_vs_instances
        /// 读到的就是实时值，而非恒定 null。
        /// </summary>
        [Fact]
        public void DebuggerStateChangedCallback_RefreshesRegistry_State()
        {
            var registry = new InstanceRegistry();
            var dummy = new MemoryStream();
            var router = new PipeRouter(dummy, ownsStream: false);
            registry.Register(new InstanceEntry(router,
                new PipeRegister { Pid = 4242, PipeName = "vs-mcp-gateway", VsVersion = "18.0" },
                DateTime.UtcNow));

            // 镜像 Program.OnDebuggerStateChanged：直接调 registry.UpdateDebuggerState。
            registry.UpdateDebuggerState(4242, "running");

            Assert.True(registry.TryGet(4242, out var after));
            Assert.Equal("running", after!.DebuggerState);
        }

        /// <summary>
        /// 收到首次推送前 DebuggerState 保持 null（register 后尚未有 state）。
        /// list_vs_instances 在此期间对 debuggerState 写 null。
        /// </summary>
        [Fact]
        public void InstanceEntry_DebuggerState_Null_UntilFirstPush()
        {
            var registry = new InstanceRegistry();
            var dummy = new MemoryStream();
            var router = new PipeRouter(dummy, ownsStream: false);
            registry.Register(new InstanceEntry(router, new PipeRegister { Pid = 1 }, DateTime.UtcNow));

            Assert.True(registry.TryGet(1, out var entry));
            Assert.Null(entry!.DebuggerState);
        }
    }
}
