using System;
using System.IO;
using System.Linq;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// 面向 Agent 的 intercept 消息（设计文档 §①/§④）。校验 Gateway 回给
    /// Agent 的错误文本包含引导文案 + 可用实例列表 + .mcp.json 示例，
    /// 以便 Agent 转达给用户。
    /// </summary>
    public class InterceptTests
    {
        private static InstanceRegistry BuildRegistry(params (int pid, string? path, string? dir)[] instances)
        {
            var registry = new InstanceRegistry();
            foreach (var (pid, path, dir) in instances)
            {
                var dummy = new MemoryStream();
                var router = new PipeRouter(dummy, ownsStream: false);
                registry.Register(new InstanceEntry(router,
                    new PipeRegister { Pid = pid, SolutionPath = path, SolutionDir = dir },
                    DateTime.UtcNow));
            }
            return registry;
        }

        // ─────────────────────────── ④ Intercept ────────────────────────────

        [Fact]
        public void ResolveTarget_MultiInstances_NoHeader_NoSession_Intercepts()
        {
            var registry = BuildRegistry(
                (1234, "D:\\projects\\MyApp\\MyApp.sln", "D:\\projects\\MyApp"),
                (5678, "D:\\projects\\Other\\Other.sln", "D:\\projects\\Other"));
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget(null, null, sessions, registry);

            Assert.False(res.Success);
            Assert.Equal(TargetErrorKind.Intercept, res.ErrorKind);
            Assert.NotNull(res.ErrorText);
            // 完整的 §④ 引导文案都在：实例列表 + .mcp.json 示例。
            Assert.Contains("Multiple VS instances are running", res.ErrorText);
            Assert.Contains("PID 1234", res.ErrorText);
            Assert.Contains("PID 5678", res.ErrorText);
            Assert.Contains("select_vs_instance", res.ErrorText);
            Assert.Contains("X-VS-Workspace", res.ErrorText);
            Assert.Contains(".mcp.json", res.ErrorText);
        }

        [Fact]
        public void ResolveTarget_ZeroInstances_NoHeader_NoSession_Intercepts()
        {
            var registry = new InstanceRegistry();
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget(null, null, sessions, registry);
            Assert.False(res.Success);
            Assert.Equal(TargetErrorKind.Intercept, res.ErrorKind);
            Assert.Contains("No VS instance connected", res.ErrorText!);
        }

        // ─────────────────────────── ① Header 未命中 ──────────────────────────

        [Fact]
        public void ResolveTarget_HeaderZeroMatches_ReturnsWorkspaceMiss()
        {
            var registry = BuildRegistry((5678, "D:\\projects\\Other\\Other.sln", "D:\\projects\\Other"));
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget("D:\\projects\\MyApp", null, sessions, registry);

            Assert.False(res.Success);
            Assert.Equal(TargetErrorKind.WorkspaceMiss, res.ErrorKind);
            Assert.Contains("No VS instance found for workspace", res.ErrorText!);
            Assert.Contains("D:\\projects\\MyApp", res.ErrorText);
            // 可用实例列表让 Agent 能告知用户当前打开了哪些。
            Assert.Contains("PID 5678", res.ErrorText);
            Assert.Contains("D:\\projects\\Other\\Other.sln", res.ErrorText);
        }

        // ───────────────────────── ① Header 歧义 ───────────────────────

        [Fact]
        public void ResolveTarget_HeaderMultiMatches_ReturnsAmbiguous()
        {
            var registry = BuildRegistry(
                (1234, "D:\\projects\\MyApp\\MyApp.sln", "D:\\projects\\MyApp"),
                (5678, "D:\\projects\\MyApp\\Other.sln", "D:\\projects\\MyApp"));
            // 两个 SolutionDir 都等于 header → 都匹配。
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget("D:\\projects\\MyApp", null, sessions, registry);

            Assert.False(res.Success);
            Assert.Equal(TargetErrorKind.WorkspaceAmbiguous, res.ErrorKind);
            Assert.Contains("matches multiple VS instances", res.ErrorText!);
            Assert.Contains("PID 1234", res.ErrorText);
            Assert.Contains("PID 5678", res.ErrorText);
        }

        // ─────────────────── ② Session / 实例离线 ───────────────────

        [Fact]
        public void ResolveTarget_BoundInstanceGone_ReturnsInstanceGone()
        {
            var sessions = new SessionTable();
            sessions.CreateWithId(9999, "vs-sess-x", "initialize"); // 9999 从未注册
            var registry = new InstanceRegistry();

            var res = BindingResolver.ResolveTarget(null, null, sessions, registry);
            // 未传 clientSessionId → 落到 ③/④（0 个实例）。
            // 用绑定的 id 重跑以命中 ② 实例已离线分支。
            var sessions2 = new SessionTable();
            var (_, binding) = sessions2.CreateWithId(9999, "vs-sess-x", "initialize");
            var res2 = BindingResolver.ResolveTarget(null, binding.ClientSessionId, sessions2, registry);

            Assert.False(res2.Success);
            Assert.Equal(TargetErrorKind.InstanceGone, res2.ErrorKind);
            Assert.Contains("no longer connected", res2.ErrorText!);
        }
    }
}
