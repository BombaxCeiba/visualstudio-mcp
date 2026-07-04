using System;
using System.IO;
using System.Text;
using System.Text.Json;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// ③ Auto-bind 层 + hint 注入（设计文档 §③）。覆盖：
    ///   - 单实例、无 header、无 session → ResolveTarget 返回实例 PID 且
    ///     HintJustAutoBound=true，新绑定已装填
    ///   - InjectHintIntoToolResultSse 在 tools/call SSE 上向 result.content
    ///     前插一个文本块，notification/error 信封原样透传
    ///   - 一次性：清掉 HintPending（如 HandleForwardAsync 所做）后，经 ② 的
    ///     第二次解析不再上报 hint
    /// </summary>
    public class AutoBindTests
    {
        /// <summary>构造一个持有单个实例（带有效 VsSessionId）的 registry。</summary>
        private static (InstanceRegistry registry, InstanceEntry entry) SingleInstanceRegistry(
            int pid = 1234, string? solutionPath = "D:\\p\\MyApp.sln", string? solutionDir = "D:\\p")
        {
            var registry = new InstanceRegistry();
            var dummy = new MemoryStream();
            var router = new PipeRouter(dummy, ownsStream: false);
            var entry = new InstanceEntry(router,
                new PipeRegister { Pid = pid, SolutionPath = solutionPath, SolutionDir = solutionDir },
                DateTime.UtcNow)
            { VsSessionId = "vs-sess-1" };
            registry.Register(entry);
            return (registry, entry);
        }

        [Fact]
        public void ResolveTarget_SingleInstance_NoHeader_NoSession_AutoBinds()
        {
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget(
                workspaceHeader: null, clientSessionId: null, sessions, registry);

            Assert.True(res.Success);
            Assert.Equal(1234, res.Pid);
            Assert.Equal("vs-sess-1", res.VsSessionId);
            Assert.True(res.HintJustAutoBound);
            Assert.NotNull(res.Binding);
            Assert.True(res.Binding!.HintPending); // 已装填一次性 hint
            Assert.Equal("auto", res.Binding.Source);
            Assert.StartsWith("sess-", res.Binding.ClientSessionId);
        }

        [Fact]
        public void ResolveTarget_AutoBind_PersistsBinding_InSessionTable()
        {
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget(null, null, sessions, registry);
            // 铸造的 client session id 下次可经 ② 解析。
            Assert.True(sessions.TryGet(res.Binding!.ClientSessionId, out var stored));
            Assert.Equal(1234, stored.Pid);
        }

        [Fact]
        public void ResolveTarget_StatelessHeaderTier_DoesNotArmHint()
        {
            // ① Header 层返回 Success 但无绑定 → 不注入 hint。
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget("D:\\p", null, sessions, registry);
            Assert.True(res.Success);
            Assert.Equal(1234, res.Pid);
            Assert.False(res.HintJustAutoBound);
            Assert.Null(res.Binding);
            // Session 表未改动（无状态 —— 决策 MI-06）。
            Assert.Equal(0, sessions.Count);
        }

        [Fact]
        public void ResolveTarget_SessionTier_DoesNotRearmHint_AfterClear()
        {
            // ③ 自动绑定并装填；转发器清掉 HintPending 后，经 ② 的第二次请求
            // 发现 HintPending=false → 无 hint。
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var first = BindingResolver.ResolveTarget(null, null, sessions, registry);
            Assert.True(first.Binding!.HintPending);

            // 模拟 HandleForwardAsync 注入后的一次性清除。
            first.Binding.HintPending = false;

            var second = BindingResolver.ResolveTarget(null, first.Binding.ClientSessionId, sessions, registry);
            Assert.True(second.Success);
            Assert.False(second.HintJustAutoBound);
            Assert.False(second.Binding!.HintPending);
        }

        [Fact]
        public void InjectHintIntoToolResultSse_PrependsTextBlock()
        {
            string sse =
                "event: message\n" +
                "data: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"break\"}]}}\n\n";
            byte[] input = Encoding.UTF8.GetBytes(sse);

            var (rewritten, injected) = GatewayTools.InjectHintIntoToolResultSse(input, "HINT TEXT");
            Assert.True(injected);

            string payload = ExtractData(Encoding.UTF8.GetString(rewritten));
            using var doc = JsonDocument.Parse(payload);
            var content = doc.RootElement.GetProperty("result").GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("HINT TEXT", content[0].GetProperty("text").GetString());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            // 原始块在 hint 之后保留。
            Assert.Equal("break", content[1].GetProperty("text").GetString());
        }

        [Fact]
        public void InjectHintIntoToolResultSse_PassesThrough_NonContentResult()
        {
            // notification / error 信封没有 content 数组 → 不注入。
            string sse =
                "event: message\n" +
                "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n";
            byte[] input = Encoding.UTF8.GetBytes(sse);

            var (rewritten, injected) = GatewayTools.InjectHintIntoToolResultSse(input, "HINT");
            Assert.False(injected);
            Assert.Equal(input, rewritten);
        }

        [Fact]
        public void InjectHintIntoToolResultSse_PassesThrough_EmptyInput()
        {
            var (rewritten, injected) = GatewayTools.InjectHintIntoToolResultSse(Array.Empty<byte>(), "HINT");
            Assert.False(injected);
        }

        [Fact]
        public void BuildAutoBindHint_ContainsPidAndSolution()
        {
            var (_, entry) = SingleInstanceRegistry(pid: 4242, solutionPath: "D:\\p\\X.sln", solutionDir: "D:\\p");
            string hint = GatewayTools.BuildAutoBindHint(entry);
            Assert.Contains("PID 4242", hint);
            Assert.Contains("D:\\p\\X.sln", hint);
            Assert.Contains("D:\\p", hint);
            Assert.Contains("list_vs_instances", hint);
        }

        private static string ExtractData(string sse)
        {
            var parts = new System.Collections.Generic.List<string>();
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
            return string.Join("\n", parts);
        }
    }
}
