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
    /// ③ Auto-bind tier + hint injection (设计文档 §③). Covers:
    ///   - single instance, no header, no session → ResolveTarget returns the
    ///     instance PID with HintJustAutoBound, and the new binding is armed
    ///   - InjectHintIntoToolResultSse prepends a text block to result.content
    ///     on a tools/call SSE, leaves notification/error envelopes untouched
    ///   - one-shot: clearing HintPending (as HandleForwardAsync does) means a
    ///     second resolve via ② no longer reports hint
    /// </summary>
    public class AutoBindTests
    {
        /// <summary>Build a registry holding one instance with a live VsSessionId.</summary>
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
            Assert.True(res.Binding!.HintPending); // armed for the one-shot hint
            Assert.Equal("auto", res.Binding.Source);
            Assert.StartsWith("sess-", res.Binding.ClientSessionId);
        }

        [Fact]
        public void ResolveTarget_AutoBind_PersistsBinding_InSessionTable()
        {
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget(null, null, sessions, registry);
            // The minted client session id is now resolvable via ② next time.
            Assert.True(sessions.TryGet(res.Binding!.ClientSessionId, out var stored));
            Assert.Equal(1234, stored.Pid);
        }

        [Fact]
        public void ResolveTarget_StatelessHeaderTier_DoesNotArmHint()
        {
            // ① Header tier returns Success but no binding → no hint injection.
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget("D:\\p", null, sessions, registry);
            Assert.True(res.Success);
            Assert.Equal(1234, res.Pid);
            Assert.False(res.HintJustAutoBound);
            Assert.Null(res.Binding);
            // Session table untouched (stateless — decision MI-06).
            Assert.Equal(0, sessions.Count);
        }

        [Fact]
        public void ResolveTarget_SessionTier_DoesNotRearmHint_AfterClear()
        {
            // ③ auto-binds and arms; after the forwarder clears HintPending, a
            // second request via ② finds HintPending=false → no hint.
            var (registry, _) = SingleInstanceRegistry();
            var sessions = new SessionTable();

            var first = BindingResolver.ResolveTarget(null, null, sessions, registry);
            Assert.True(first.Binding!.HintPending);

            // Simulate HandleForwardAsync's one-shot clear after injecting.
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
            // Original block preserved after the hint.
            Assert.Equal("break", content[1].GetProperty("text").GetString());
        }

        [Fact]
        public void InjectHintIntoToolResultSse_PassesThrough_NonContentResult()
        {
            // A notification / error envelope has no content array → no injection.
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
