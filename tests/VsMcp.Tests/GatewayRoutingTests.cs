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
    /// Wave 2 Gateway routing tests. Each simulated VS is a NamedPipeServerStream
    /// that the static <see cref="PipeRouter.ConnectAsync"/> factory dials into
    /// (VS-as-server direction is simulated; the Gateway's main register-accept
    /// path is symmetric). Tests cover:
    ///   - multi-VS registration (InstanceRegistry)
    ///   - session binding via initialize with _meta.vsPid (SessionTable)
    ///   - single-instance fallback when initialize omits vsPid
    ///   - gateway tools (list_vs_instances / select_vs_instance) handled locally,
    ///     never forwarded to a simulated VS
    ///   - tools/list SSE injection
    ///   - select_vs_instance rebind
    ///
    /// All async ops carry an 8s CancellationTokenSource — a hang fails fast.
    /// </summary>
    public class GatewayRoutingTests
    {
        private const int OpTimeoutMs = 8000;
        private readonly ITestOutputHelper _out;

        public GatewayRoutingTests(ITestOutputHelper output) => _out = output;

        private static string NewPipeName() => "vs-mcp-gw-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Stand up a simulated VS: a NamedPipeServerStream that accepts one
        /// router connection, then the test body drives it via the returned
        /// server stream. The PipeRouter owns the client end and its read loop.
        /// </summary>
        private async Task<(NamedPipeServerStream server, PipeRouter router)> ConnectSimulatedVsAsync(
            string pipeName, CancellationToken ct)
        {
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);

            var connectTask = PipeRouter.ConnectAsync(pipeName, ct);

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

        /// <summary>
        /// Helper: simulate a VS handling one request — read a PipeRequest frame,
        /// write back head (with optional Mcp-Session-Id) + data + end.
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

            // PipeRouter needs a live stream; but Snapshot only reads Info, so we
            // can use a MemoryStream-backed router via the internal ctor. Since
            // that ctor starts a read loop, we use a disposed-dummy stream that
            // immediately EOFs — the read loop exits, router still holds metadata.
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
            Assert.Null(got.VsSessionId); // cleared → forces re-initialize
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
            // A realistic tools/list SSE frame from VS (one VS-provided tool).
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
            // Gateway tools prepended.
            Assert.Equal("list_vs_instances", tools[0].GetProperty("name").GetString());
            Assert.Equal("select_vs_instance", tools[1].GetProperty("name").GetString());
            // Original VS tool preserved verbatim after.
            Assert.Equal("get_debugger_state", tools[2].GetProperty("name").GetString());
        }

        [Fact]
        public void GatewayTools_InjectIntoToolsListSse_PassesThroughNonToolsResult()
        {
            // An error envelope has no result.tools — must be passed through unchanged.
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

        // ────────────────── Initialize target resolution (Program) ──────────────

        [Fact]
        public void ResolveInitializeTarget_MetaVsPid_PrebindsExplicitly()
        {
            // Scenario 2 (pre-bind via _meta.vsPid): explicit pid wins even when
            // other instances are connected.
            int? pid = Program.ResolveInitializeTarget(metaVsPid: 5555, connectedCount: 3, singlePid: () => 1111);
            Assert.Equal(5555, pid);
        }

        [Fact]
        public void ResolveInitializeTarget_SingleInstanceFallback_BindsToTheOne()
        {
            // Scenario 3 (single-instance fallback): no vsPid, exactly 1 instance
            // → bind to it (Wave 2 form: no hint injection, that's Wave 3).
            int? pid = Program.ResolveInitializeTarget(metaVsPid: null, connectedCount: 1, singlePid: () => 9999);
            Assert.Equal(9999, pid);
        }

        [Fact]
        public void ResolveInitializeTarget_ZeroOrManyInstances_ReturnsNull()
        {
            // 0 instances → can't bind (null).
            Assert.Null(Program.ResolveInitializeTarget(null, 0, () => throw new InvalidOperationException()));
            // ≥2 instances, no explicit pid → ambiguous (null).
            Assert.Null(Program.ResolveInitializeTarget(null, 2, () => throw new InvalidOperationException()));
        }

        // ───────────────────── End-to-end demux forward ──────────────────────

        /// <summary>
        /// A simulated VS responds to a forwarded initialize with a VS-assigned
        /// Mcp-Session-Id in the head; PipeRouter.ForwardAsync surfaces it via
        /// ForwardResult.Headers (the property the Gateway's initialize handler
        /// relies on to record the vs↔client session mapping).
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
        /// Concurrent forwards over one pipe demux correctly by id: two
        /// in-flight requests, VS interleaves responses, each lands on the
        /// right caller. This is the Wave 2 capability Wave 1's serial
        /// router could not provide.
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
                    // Read both requests, respond out of order (req2 first).
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

                // Each caller got its OWN status + body, despite interleaving.
                Assert.Equal(201, resA.Status);
                Assert.Equal("first", Encoding.UTF8.GetString(outA.ToArray()));
                Assert.Equal(202, resB.Status);
                Assert.Equal("second", Encoding.UTF8.GetString(outB.ToArray()));
            }
        }
    }
}
