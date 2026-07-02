using System;
using System.IO;
using System.Linq;
using VsMcp.Common;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// Agent-facing intercept messages (设计文档 §①/§④). Verifies the error
    /// text the Gateway hands back to the agent contains the guidance + the
    /// available-instances list + the .mcp.json example, so the agent can relay
    /// it to the user.
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
            // The full §④ guidance is present: instance list + .mcp.json example.
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

        // ─────────────────────────── ① Header miss ──────────────────────────

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
            // The available-instances list lets the agent tell the user what's open.
            Assert.Contains("PID 5678", res.ErrorText);
            Assert.Contains("D:\\projects\\Other\\Other.sln", res.ErrorText);
        }

        // ───────────────────────── ① Header ambiguity ───────────────────────

        [Fact]
        public void ResolveTarget_HeaderMultiMatches_ReturnsAmbiguous()
        {
            var registry = BuildRegistry(
                (1234, "D:\\projects\\MyApp\\MyApp.sln", "D:\\projects\\MyApp"),
                (5678, "D:\\projects\\MyApp\\Other.sln", "D:\\projects\\MyApp"));
            // Two SolutionDirs equal the header → both match.
            var sessions = new SessionTable();

            var res = BindingResolver.ResolveTarget("D:\\projects\\MyApp", null, sessions, registry);

            Assert.False(res.Success);
            Assert.Equal(TargetErrorKind.WorkspaceAmbiguous, res.ErrorKind);
            Assert.Contains("matches multiple VS instances", res.ErrorText!);
            Assert.Contains("PID 1234", res.ErrorText);
            Assert.Contains("PID 5678", res.ErrorText);
        }

        // ─────────────────── ② Session / instance offline ───────────────────

        [Fact]
        public void ResolveTarget_BoundInstanceGone_ReturnsInstanceGone()
        {
            var sessions = new SessionTable();
            sessions.CreateWithId(9999, "vs-sess-x", "initialize"); // 9999 never registered
            var registry = new InstanceRegistry();

            var res = BindingResolver.ResolveTarget(null, null, sessions, registry);
            // No clientSessionId passed → falls through to ③/④ (0 instances).
            // Re-run with the bound id to hit the ② instance-gone branch.
            var sessions2 = new SessionTable();
            var (_, binding) = sessions2.CreateWithId(9999, "vs-sess-x", "initialize");
            var res2 = BindingResolver.ResolveTarget(null, binding.ClientSessionId, sessions2, registry);

            Assert.False(res2.Success);
            Assert.Equal(TargetErrorKind.InstanceGone, res2.ErrorKind);
            Assert.Contains("no longer connected", res2.ErrorText!);
        }
    }
}
