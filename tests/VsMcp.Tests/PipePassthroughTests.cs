using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;
using Xunit.Abstractions;

namespace VsMcp.Tests
{
    /// <summary>
    /// 端到端 pipe 协议透传测试：用一对 NamedPipe（server + client）模拟 VS 端写
    /// head/data/end 帧，<see cref="PipeRouter"/> 读取并把 base64 data 块重组后写出。
    /// 验证 Wave 1 最高风险点 —— pipe framing + SSE 字节透传 —— 不依赖真实 VS / MCP SDK。
    ///
    /// 所有异步操作带超时 token：任何意外挂起都会在数秒内失败而非无限卡死。
    /// server 与 PipeRouter 的 client 都用 <see cref="PipeOptions.None"/>（同步 pipe）——
    /// Asynchronous handle + 同步 IO 在 .NET Framework 上会挂死。
    /// </summary>
    public class PipePassthroughTests
    {
        private const int OpTimeoutMs = 8000;
        private readonly ITestOutputHelper _out;

        public PipePassthroughTests(ITestOutputHelper output) => _out = output;

        private static string NewPipeName() => "vs-mcp-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private async Task<(NamedPipeServerStream server, PipeRouter router)> ConnectPairAsync(
            string pipeName, CancellationToken ct)
        {
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);

            // Static factory owns the connect retry loop and returns a router
            // already running its read loop. The semantic the tests assert —
            // "server writes head/data/end, PipeRouter reassembles" — is unchanged;
            // only the construction path moved from instance ctor to factory.
            var connectTask = PipeRouter.ConnectAsync(pipeName, ct);

            // 同步 WaitForConnection() 是 .NET Framework 4.x 全版本确定可用的 API；
            // 在 Task.Run 中异步化，ct 取消时 Dispose server 解除其阻塞。
            await Task.Run(() =>
            {
                using (ct.Register(() => { try { server.Dispose(); } catch { /* raced */ } }))
                {
                    server.WaitForConnection();
                }
            }, ct);
            var router = await connectTask;

            return (server, router);
        }

        /// <summary>模拟 VS 写 head + 两块 data + end，验证 Router 重组出原始字节。</summary>
        [Fact]
        public async Task Forward_ReassemblesDataChunks_InOrder()
        {
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var (server, router) = await ConnectPairAsync(pipeName, cts.Token);
            using (server)
            using (router)
            {
                byte[] chunk1 = Encoding.UTF8.GetBytes("data: chunk1\n\n");
                byte[] chunk2 = Encoding.UTF8.GetBytes("data: chunk2\n\n");
                byte[] expected = Encoding.UTF8.GetBytes("data: chunk1\n\ndata: chunk2\n\n");

                var vsTask = Task.Run(async () =>
                {
                    var req = await PipeFraming.ReadFrameAsync<PipeRequest>(server, cts.Token);
                    Assert.NotNull(req);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeResponseHead { Id = req!.Id, Status = 200 }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeDataChunk { Id = req.Id, Body = Convert.ToBase64String(chunk1) }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeDataChunk { Id = req.Id, Body = Convert.ToBase64String(chunk2) }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeEnd { Id = req.Id }, cts.Token);
                });

                using var output = new MemoryStream();
                int status = await router.ForwardAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                    null, output, cts.Token);
                await vsTask;

                Assert.Equal(200, status);
                Assert.Equal(expected, output.ToArray());
            }
        }

        /// <summary>head 的 status 透传为 ForwardAsync 的返回值。</summary>
        [Fact]
        public async Task Forward_PropagatesStatusFromHead()
        {
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var (server, router) = await ConnectPairAsync(pipeName, cts.Token);
            using (server)
            using (router)
            {
                var vsTask = Task.Run(async () =>
                {
                    var req = await PipeFraming.ReadFrameAsync<PipeRequest>(server, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeResponseHead { Id = req!.Id, Status = 400 }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeEnd { Id = req.Id }, cts.Token);
                });

                using var output = new MemoryStream();
                int status = await router.ForwardAsync("{}", null, output, cts.Token);
                await vsTask;

                Assert.Equal(400, status);
                Assert.Empty(output.ToArray());
            }
        }

        /// <summary>空 body 的 data 帧不产生输出字节（守 0 长度 base64）。</summary>
        [Fact]
        public async Task Forward_EmptyDataChunk_WritesNothing()
        {
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var (server, router) = await ConnectPairAsync(pipeName, cts.Token);
            using (server)
            using (router)
            {
                var vsTask = Task.Run(async () =>
                {
                    var req = await PipeFraming.ReadFrameAsync<PipeRequest>(server, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeResponseHead { Id = req!.Id, Status = 200 }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeDataChunk { Id = req.Id, Body = "" }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeEnd { Id = req.Id }, cts.Token);
                });

                using var output = new MemoryStream();
                await router.ForwardAsync("{}", null, output, cts.Token);
                await vsTask;

                Assert.Empty(output.ToArray());
            }
        }
    }
}
