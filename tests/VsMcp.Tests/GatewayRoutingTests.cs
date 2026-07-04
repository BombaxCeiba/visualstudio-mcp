using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;
using Xunit.Abstractions;

namespace VsMcp.Tests
{
    /// <summary>
    /// Wave 2 Gateway 路由测试。每个模拟 VS 是一个 NamedPipeServerStream，由静态
    /// <see cref="PipeRouter.ConnectAsync"/> 工厂拨入（VS 作为 server 的方向是
    /// 模拟的；Gateway 的主 register-accept 路径对称）。测试覆盖：
    ///   - 多 VS 注册（InstanceRegistry）
    ///   - 经 initialize 携带 _meta.vsPid 的 session 绑定（SessionTable）
    ///   - initialize 省略 vsPid 时的单实例回退
    ///   - gateway 工具（list_vs_instances / select_vs_instance）本地处理，
    ///     绝不转发给模拟 VS
    ///   - tools/list SSE 注入
    ///   - select_vs_instance 重绑
    ///
    /// 所有异步操作带 8s CancellationTokenSource —— 挂起即快速失败。
    /// </summary>
    public class GatewayRoutingTests
    {
        private const int OpTimeoutMs = 8000;
        private readonly ITestOutputHelper _out;

        public GatewayRoutingTests(ITestOutputHelper output) => _out = output;

        private static string NewPipeName() => "vs-mcp-gw-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// 搭建一个模拟 VS：一个 NamedPipeServerStream 接受一次 router 连接，
        /// 随后测试主体通过返回的 server stream 驱动它。PipeRouter 拥有 client
        /// 端及其读循环。
        /// </summary>
        private async Task<(NamedPipeServerStream server, PipeRouter router)> ConnectSimulatedVsAsync(
            string pipeName, CancellationToken ct)
        {
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);

            var connectTask = PipeRouter.ConnectAsync(pipeName, ct);

            await Task.Run(() =>
            {
                using (ct.Register(() => { try { server.Dispose(); } catch { /* 已竞态 */ } }))
                {
                    server.WaitForConnection();
                }
            }, ct);
            var router = await connectTask;

            return (server, router);
        }

        /// <summary>
        /// 辅助方法：模拟 VS 处理一个请求 —— 读取一个 PipeRequest 帧，
        /// 回写 head（可选带 Mcp-Session-Id）+ data + end。
        /// </summary>
        private static async Task ServeOneRequestAsync(
            Stream server,
            CancellationToken ct,
            int status = 200,
            string? mcpSessionId = null,
            string? sseBody = null)
        {
            var req = await PipeFraming.ReadFrameAsync<PipeRequest>(server, ct);
            Assert.NotNull(req);

            var head = new PipeResponseHead { Id = req!.Id, Status = status };
            if (mcpSessionId != null)
                head.Headers["Mcp-Session-Id"] = mcpSessionId;
            await PipeFraming.WriteFrameAsync(server, head, ct);

            if (sseBody != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(sseBody);
                await PipeFraming.WriteFrameAsync(server,
                    new PipeDataChunk { Id = req.Id, Body = Convert.ToBase64String(bytes) }, ct);
            }
            await PipeFraming.WriteFrameAsync(server, new PipeEnd { Id = req.Id }, ct);
        }

        // ───────────────────────── InstanceRegistry ──────────────────────────

        [Fact]
        public void InstanceRegistry_RegisterAndSnapshot_ReturnsAllEntries()
        {
            var registry = new InstanceRegistry();
            var info1 = new PipeRegister { Pid = 1001, SolutionPath = "A.sln" };
            var info2 = new PipeRegister { Pid = 1002, SolutionPath = "B.sln" };

            // PipeRouter 需要活跃 stream；但 Snapshot 只读 Info，所以可以用
            // 内部 ctor 构造 MemoryStream 支撑的 router。由于该 ctor 会启动
            // 读循环，我们用一个立即 EOF 的 dummy stream —— 读循环退出，
            // router 仍持有元数据。
            using var dummyA = new MemoryStream();
            using var dummyB = new MemoryStream();
            var r1 = new PipeRouter(dummyA, ownsStream: false);
            var r2 = new PipeRouter(dummyB, ownsStream: false);

            registry.Register(new InstanceEntry(r1, info1, DateTime.UtcNow));
            registry.Register(new InstanceEntry(r2, info2, DateTime.UtcNow));

            Assert.Equal(2, registry.Count);
            var snap = registry.Snapshot();
            Assert.Contains(snap, e => e.Info.Pid == 1001);
            Assert.Contains(snap, e => e.Info.Pid == 1002);

            Assert.True(registry.TryGet(1001, out var got));
            Assert.Equal("A.sln", got!.Info.SolutionPath);

            Assert.True(registry.Remove(1001));
            Assert.Equal(1, registry.Count);
            Assert.False(registry.TryGet(1001, out _));
        }

        [Fact]
        public void InstanceRegistry_UpdateInfo_RefreshesEntry()
        {
            var registry = new InstanceRegistry();
            using var dummy = new MemoryStream();
            var router = new PipeRouter(dummy, ownsStream: false);
            registry.Register(new InstanceEntry(router,
                new PipeRegister { Pid = 2001, SolutionPath = null }, DateTime.UtcNow));

            registry.UpdateInfo(2001, new PipeRegister { Pid = 2001, SolutionPath = "later.sln" });

            Assert.True(registry.TryGet(2001, out var got));
            Assert.Equal("later.sln", got!.Info.SolutionPath);
        }

        // ───────────────────────── SessionTable ──────────────────────────────

        [Fact]
        public void SessionTable_CreateWithId_GeneratesClientSessionId()
        {
            var table = new SessionTable();
            var (id, binding) = table.CreateWithId(4242, "vs-sess-abc", "initialize");

            Assert.StartsWith("sess-", id);
            Assert.Equal(4242, binding.Pid);
            Assert.Equal("vs-sess-abc", binding.VsSessionId);
            Assert.Equal("initialize", binding.Source);

            Assert.True(table.TryGet(id, out var got));
            Assert.Equal(4242, got!.Pid);
        }

        [Fact]
        public void SessionTable_Rebind_ChangesPidAndClearsVsSession()
        {
            var table = new SessionTable();
            var (id, _) = table.CreateWithId(11, "vs-1", "initialize");

            Assert.True(table.Rebind(id, 22));

            Assert.True(table.TryGet(id, out var got));
            Assert.Equal(22, got!.Pid);
            Assert.Null(got.VsSessionId); // 已清除 → 强制重新 initialize
            Assert.Equal("select", got.Source);
        }

        // ───────────────────────── GatewayTools ──────────────────────────────

        [Fact]
        public void GatewayTools_IsGatewayTool_RecognizesBothNames()
        {
            Assert.True(GatewayTools.IsGatewayTool("list_vs_instances"));
            Assert.True(GatewayTools.IsGatewayTool("select_vs_instance"));
            Assert.False(GatewayTools.IsGatewayTool("get_debugger_state"));
            Assert.False(GatewayTools.IsGatewayTool(null));
        }

        [Fact]
        public void GatewayTools_BuildListInstancesContent_ListsEveryInstance()
        {
            using var dummy = new MemoryStream();
            var entry = new InstanceEntry(
                new PipeRouter(dummy, ownsStream: false),
                new PipeRegister { Pid = 7777, SolutionPath = @"D:\p\A.sln", SolutionDir = @"D:\p", VsVersion = "18.0" },
                DateTime.UtcNow);

            string json = GatewayTools.BuildListInstancesContent(new[] { entry });
            using var doc = JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("instances", out var arr));
            Assert.Equal(1, arr.GetArrayLength());
            Assert.Equal(7777, arr[0].GetProperty("pid").GetInt32());
            Assert.Equal(@"D:\p\A.sln", arr[0].GetProperty("solution").GetString());
        }

        [Fact]
        public void GatewayTools_InjectIntoToolsListSse_PrependsGatewayTools()
        {
            // 来自 VS 的一个真实 tools/list SSE 帧（一个 VS 提供的工具）。
            string originalSse =
                "event: message\n" +
                "data: {\"jsonrpc\":\"2.0\",\"id\":42,\"result\":{\"tools\":[{\"name\":\"get_debugger_state\",\"readOnly\":true}]}}\n\n";
            byte[] input = Encoding.UTF8.GetBytes(originalSse);

            byte[] output = GatewayTools.InjectIntoToolsListSse(input);
            string resultSse = Encoding.UTF8.GetString(output);
            string? payload = ExtractSseData(resultSse);
            Assert.NotNull(payload);

            using var doc = JsonDocument.Parse(payload!);
            var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Equal(3, tools.GetArrayLength());
            // Gateway 工具已前插。
            Assert.Equal("list_vs_instances", tools[0].GetProperty("name").GetString());
            Assert.Equal("select_vs_instance", tools[1].GetProperty("name").GetString());
            // 原始 VS 工具在后面原样保留。
            Assert.Equal("get_debugger_state", tools[2].GetProperty("name").GetString());
        }

        [Fact]
        public void GatewayTools_InjectIntoToolsListSse_PassesThroughNonToolsResult()
        {
            // error 信封没有 result.tools —— 必须原样透传。
            string originalSse =
                "event: message\n" +
                "data: {\"jsonrpc\":\"2.0\",\"id\":42,\"error\":{\"code\":-32601,\"message\":\"nope\"}}\n\n";
            byte[] input = Encoding.UTF8.GetBytes(originalSse);

            byte[] output = GatewayTools.InjectIntoToolsListSse(input);
            Assert.Equal(input, output);
        }

        private static string? ExtractSseData(string sse)
        {
            var parts = new List<string>();
            foreach (string line in sse.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.StartsWith("data:"))
                {
                    string rest = l.Substring(5);
                    if (rest.StartsWith(" ")) rest = rest.Substring(1);
                    parts.Add(rest);
                }
            }
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        // ────────────────── Initialize 目标解析（Program） ──────────────

        [Fact]
        public void ResolveInitializeTarget_MetaVsPid_PrebindsExplicitly()
        {
            // 场景 2（经 _meta.vsPid 预绑）：即使有其他实例已连接，显式 pid 仍胜出。
            int? pid = Program.ResolveInitializeTarget(metaVsPid: 5555, connectedCount: 3, singlePid: () => 1111);
            Assert.Equal(5555, pid);
        }

        [Fact]
        public void ResolveInitializeTarget_SingleInstanceFallback_BindsToTheOne()
        {
            // 场景 3（单实例回退）：无 vsPid，恰好 1 个实例 → 绑定它
            // （Wave 2 形态：不注入 hint，那是 Wave 3）。
            int? pid = Program.ResolveInitializeTarget(metaVsPid: null, connectedCount: 1, singlePid: () => 9999);
            Assert.Equal(9999, pid);
        }

        [Fact]
        public void ResolveInitializeTarget_ZeroOrManyInstances_ReturnsNull()
        {
            // 0 个实例 → 无法绑定（null）。
            Assert.Null(Program.ResolveInitializeTarget(null, 0, () => throw new InvalidOperationException()));
            // ≥2 个实例，无显式 pid → 歧义（null）。
            Assert.Null(Program.ResolveInitializeTarget(null, 2, () => throw new InvalidOperationException()));
        }

        // ───────────────────── 端到端 demux 转发 ──────────────────────

        /// <summary>
        /// 模拟 VS 用 head 中 VS 分配的 Mcp-Session-Id 响应转发的 initialize；
        /// PipeRouter.ForwardAsync 通过 ForwardResult.Headers 暴露它（Gateway 的
        /// initialize 处理器依赖此属性记录 vs↔client session 映射）。
        /// </summary>
        [Fact]
        public async Task Forward_CapturesVsSessionId_FromHead()
        {
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var (server, router) = await ConnectSimulatedVsAsync(pipeName, cts.Token);
            using (server)
            using (router)
            {
                var vsTask = Task.Run(() => ServeOneRequestAsync(server, cts.Token,
                    status: 200, mcpSessionId: "vs-sess-xyz",
                    sseBody: "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n"));

                using var output = new MemoryStream();
                var result = await router.ForwardAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
                    null, output, cts.Token);
                await vsTask;

                Assert.Equal(200, result.Status);
                Assert.True(result.Headers.TryGetValue("Mcp-Session-Id", out var sid));
                Assert.Equal("vs-sess-xyz", sid);
            }
        }

        /// <summary>
        /// 单 pipe 上的并发转发按 id 正确分派：两个在途请求，VS 交错响应，
        /// 每个落到正确调用方。这是 Wave 1 串行 router 无法提供的 Wave 2 能力。
        /// </summary>
        [Fact]
        public async Task Forward_ConcurrentRequests_DemuxById()
        {
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(OpTimeoutMs);
            var (server, router) = await ConnectSimulatedVsAsync(pipeName, cts.Token);
            using (server)
            using (router)
            {
                var vsTask = Task.Run(async () =>
                {
                    // 读取两个请求，乱序响应（req2 先）。
                    var req1 = await PipeFraming.ReadFrameAsync<PipeRequest>(server, cts.Token);
                    var req2 = await PipeFraming.ReadFrameAsync<PipeRequest>(server, cts.Token);
                    Assert.NotNull(req1);
                    Assert.NotNull(req2);

                    await PipeFraming.WriteFrameAsync(server,
                        new PipeResponseHead { Id = req2!.Id, Status = 202 }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeDataChunk { Id = req2.Id, Body = Convert.ToBase64String(Encoding.UTF8.GetBytes("second")) },
                        cts.Token);
                    await PipeFraming.WriteFrameAsync(server, new PipeEnd { Id = req2.Id }, cts.Token);

                    await PipeFraming.WriteFrameAsync(server,
                        new PipeResponseHead { Id = req1!.Id, Status = 201 }, cts.Token);
                    await PipeFraming.WriteFrameAsync(server,
                        new PipeDataChunk { Id = req1.Id, Body = Convert.ToBase64String(Encoding.UTF8.GetBytes("first")) },
                        cts.Token);
                    await PipeFraming.WriteFrameAsync(server, new PipeEnd { Id = req1.Id }, cts.Token);
                });

                using var outA = new MemoryStream();
                using var outB = new MemoryStream();

                var fwdA = router.ForwardAsync("{\"id\":1}", null, outA, cts.Token);
                var fwdB = router.ForwardAsync("{\"id\":2}", null, outB, cts.Token);

                var resA = await fwdA;
                var resB = await fwdB;
                await vsTask;

                // 每个调用方拿到各自的状态 + body，尽管响应是交错的。
                Assert.Equal(201, resA.Status);
                Assert.Equal("first", Encoding.UTF8.GetString(outA.ToArray()));
                Assert.Equal(202, resB.Status);
                Assert.Equal("second", Encoding.UTF8.GetString(outB.ToArray()));
            }
        }
    }
}
