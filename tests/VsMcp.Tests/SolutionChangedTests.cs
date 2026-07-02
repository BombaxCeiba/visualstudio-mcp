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
    /// Wave 3 control-frame path: VS pushes a <c>solution-changed</c> frame (no
    /// id) over the pipe; the PipeRouter read loop recognizes it and dispatches
    /// via the constructor callback instead of dropping it (the old behavior for
    /// id-less frames). The Gateway's callback then refreshes the
    /// InstanceRegistry so the ① Header tier sees the live SolutionDir without a
    /// reconnect.
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

            // A MemoryStream with exactly one framed payload: the read loop reads
            // it, dispatches the control frame to the callback, then reads EOF
            // and exits. MemoryStream's ReadAsync completes synchronously so the
            // callback fires promptly after construction.
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
        /// The Gateway's callback merges the push into the existing InstanceEntry
        /// (preserving PipeName/VsVersion that the push DTO omits). Verifies the
        /// exact merge logic Program.OnSolutionChanged performs, against a real
        /// InstanceRegistry, so header routing after a solution change picks up
        /// the new SolutionDir.
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

            // Mirror Program.OnSolutionChanged's merge (keep PipeName/VsVersion
            // from the existing entry; take solution fields from the push).
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
            Assert.Equal("vs-mcp-gateway", after.Info.PipeName);   // preserved
            Assert.Equal("18.0", after.Info.VsVersion);             // preserved

            // And the refreshed SolutionDir is now visible to the ① Header tier.
            var match = WorkspaceResolver.Resolve("D:\\proj",
                new[] { (after.Info.Pid, after.Info.SolutionDir) });
            Assert.Equal(WorkspaceMatchKind.Single, match.Kind);
            Assert.Equal(777, match.Pid);
        }

        [Fact]
        public async Task PipeRouter_UnknownControlFrame_DoesNotInvokeCallback()
        {
            // A heartbeat (Wave 4) or any other id-less frame must NOT trigger the
            // solution-changed callback; it's silently dropped so a newer VS peer
            // advertising unknown control frames never breaks the read loop.
            var invoked = false;
            byte[] frameBytes = PipeFraming.BuildFrameBytes(new PipeHeartbeat { Pid = 1 });

            using var stream = new MemoryStream(frameBytes);
            using var router = new PipeRouter(stream, ownsStream: false, _ => invoked = true);

            // Give the read loop a moment to process the frame + EOF. The callback
            // is invoked synchronously on read if at all, so a short delay is enough.
            await Task.Delay(200);
            Assert.False(invoked);
        }

        [Fact]
        public async Task PipeRouter_DispatchesHeartbeatFrame_ToCallback()
        {
            // Wave 4: a heartbeat control frame routes to the dedicated onHeartbeat
            // callback (not the solution-changed one), so the Gateway can refresh
            // InstanceEntry.LastSeen on every VS heartbeat.
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
        /// <summary>Await with a hard timeout so a hung dispatch fails the test
        /// fast instead of stalling the runner.</summary>
        public static async Task<T> WaitForAsync<T>(this Task<T> task, int timeoutMs)
        {
            var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (winner != task)
                throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for the pipe callback.");
            return await task;
        }
    }
}
