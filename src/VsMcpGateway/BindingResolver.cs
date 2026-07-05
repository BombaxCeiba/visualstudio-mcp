using System;
using System.Collections.Generic;
using System.Linq;

namespace VsMcpGateway
{
    /// <summary>
    /// 说明 <see cref="TargetResolution"/> 为何得到该结果。只有错误情形才带
    /// 区分性的 kind——Program.cs 根据 <see cref="TargetResolution.Success"/> +
    /// <see cref="TargetResolution.Binding"/> 决定 hint/forward 行为。
    /// </summary>
    public enum TargetErrorKind
    {
        None,
        /// <summary>① Header 匹配到 0 个实例。</summary>
        WorkspaceMiss,
        /// <summary>① Header 匹配到 ≥2 个实例。</summary>
        WorkspaceAmbiguous,
        /// <summary>②/③ 没有可用实例 / 无绑定且不恰好一个实例（设计文档 §④）。</summary>
        Intercept,
        /// <summary>② 绑定存在但目标 VS 已下线。</summary>
        InstanceGone,
        /// <summary>绑定的 VS 尚无 Mcp-Session-Id（从未通过本 Gateway initialize 过）。</summary>
        NeedsInitialize,
    }

    /// <summary>
    /// 4 层绑定解析的结果（① Header &gt; ② Session &gt; ③ 自动绑定 &gt;
    /// ④ 拦截）。成功时 <see cref="Pid"/> 是路由目标，<see cref="VsSessionId"/>
    /// 是要转发的每个 VS 的 session id，<see cref="Binding"/> 是其
    /// <c>HintPending</c> 门控 hint 注入的会话绑定（无状态的 ① 层为 null，从不
    /// 注入）。失败时 <see cref="ErrorText"/> 携带面向 agent 的消息，
    /// <see cref="ErrorKind"/> 对其分类。
    /// </summary>
    public sealed class TargetResolution
    {
        public bool Success { get; }
        public int? Pid { get; }
        public string? VsSessionId { get; }
        public SessionBinding? Binding { get; }
        public bool HintJustAutoBound { get; }
        public TargetErrorKind ErrorKind { get; }
        public string? ErrorText { get; }

        private TargetResolution(bool success, int? pid, string? vsSessionId,
            SessionBinding? binding, bool hintJustAutoBound,
            TargetErrorKind errorKind, string? errorText)
        {
            Success = success;
            Pid = pid;
            VsSessionId = vsSessionId;
            Binding = binding;
            HintJustAutoBound = hintJustAutoBound;
            ErrorKind = errorKind;
            ErrorText = errorText;
        }

        public static TargetResolution Routed(int pid, string? vsSessionId, SessionBinding? binding, bool hintJustAutoBound) =>
            new TargetResolution(true, pid, vsSessionId, binding, hintJustAutoBound, TargetErrorKind.None, null);

        public static TargetResolution Failed(TargetErrorKind kind, string text) =>
            new TargetResolution(false, null, null, null, false, kind, text);
    }

    /// <summary>
    /// Gateway 对任何非 initialize、非 gateway 工具请求的绑定解析。在设计文档
    /// §Session Binding 的 4 层优先级（① Header &gt; ② Session &gt; ③ 自动绑定
    /// &gt; ④ 拦截）上实现，是内存表上的单个方法——无 HTTP/pipe IO——让完整路由
    /// 逻辑可单元测试。③ 自动绑定层在此执行其 SessionTable 副作用（带 hint 创建）；
    /// ① 无状态（按决策 MI-06 从不触碰 SessionTable）。
    ///
    /// vsSessionId 权威来源：initialize 时捕获的每个 VS 的 id 存放在
    /// <see cref="InstanceEntry.VsSessionId"/>（VS 服务端是单会话）。① 层直接读它，
    /// 让无状态的 header 路由请求仍能到达正确的 VS 端会话。
    /// </summary>
    public static class BindingResolver
    {
        public static TargetResolution ResolveTarget(
            string? workspaceHeader,
            string? clientSessionId,
            SessionTable sessions,
            InstanceRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));

            // ① Header 层——无状态，最高优先级（设计文档 §①）。
            if (!string.IsNullOrWhiteSpace(workspaceHeader))
            {
                var snap = registry.Snapshot();
                var match = WorkspaceResolver.Resolve(workspaceHeader,
                    snap.Select(e => (e.Info.Pid, e.Info.SolutionDir)));
                if (match.Kind == WorkspaceMatchKind.Single && match.Pid.HasValue)
                {
                    if (registry.TryGet(match.Pid.Value, out var entry))
                        return TargetResolution.Routed(entry.Info.Pid, entry.VsSessionId, binding: null, hintJustAutoBound: false);
                    // Snapshot 与 TryGet 之间发生竞态——视为未命中。
                }
                else if (match.Kind == WorkspaceMatchKind.None)
                {
                    return TargetResolution.Failed(TargetErrorKind.WorkspaceMiss,
                        GatewayTools.BuildWorkspaceMissMessage(workspaceHeader!, snap));
                }
                else // 歧义（Ambiguous）
                {
                    var matchedInstances = snap.Where(e => match.MatchedPids.Contains(e.Info.Pid)).ToArray();
                    return TargetResolution.Failed(TargetErrorKind.WorkspaceAmbiguous,
                        GatewayTools.BuildAmbiguousMessage(workspaceHeader!, matchedInstances));
                }
            }

            // ② Session 层——之前的 initialize / select 绑定了这个客户端会话。
            if (!string.IsNullOrEmpty(clientSessionId) && sessions.TryGet(clientSessionId!, out var binding))
            {
                if (!registry.TryGet(binding.Pid, out var entry))
                    return TargetResolution.Failed(TargetErrorKind.InstanceGone,
                        $"Bound VS instance PID {binding.Pid} is no longer connected.");

                // vsSessionId 可能为 null：VS 的 pipe 模式 SDK 从不在 head 帧里发
                // Mcp-Session-Id，Gateway 无从捕获。这没问题——VS 端跑单个进程内会话
                // 并忽略该 header，所以不带它转发即可，而不是阻塞每次 initialize 之后
                // 的调用（之前这会在每次 tools/call 上以 -32004 "VS instance switched"
                // 形式浮现）。
                return TargetResolution.Routed(entry.Info.Pid, entry.VsSessionId, binding, hintJustAutoBound: false);
            }

            // ③ 自动绑定——恰好一个实例在线，无 header，无 session（② 已 miss）。
            var snapshot = registry.Snapshot();
            if (snapshot.Count == 1)
            {
                var only = snapshot.First();
                // VS pipe 模式从不捕获 Mcp-Session-Id（head 帧不带），所以
                // only.VsSessionId 为 null。这没问题——VS 端跑单个进程内会话并忽略该 header。
                SessionBinding newBinding;
                bool justCreated;
                if (!string.IsNullOrEmpty(clientSessionId))
                {
                    // 客户端带了 session id（即使非 Gateway 分配——Claude Code 等客户端带自己
                    // 缓存/生成的 id）——用它作 binding key，让后续 ② 层命中，避免每次 auto-bind
                    // + hint 循环。复用现有 binding 不重设 HintPending（已注入过就不再注入）。
                    var (b, c) = sessions.GetOrAdd(clientSessionId!, only.Info.Pid, only.VsSessionId, "auto");
                    newBinding = b; justCreated = c;
                }
                else
                {
                    // 客户端没带 id——Gateway 铸造一个，转发路径回显给客户端。
                    var (_, b) = sessions.CreateWithId(only.Info.Pid, only.VsSessionId, "auto");
                    newBinding = b; justCreated = true;
                }
                if (justCreated) newBinding.HintPending = true;
                return TargetResolution.Routed(only.Info.Pid, only.VsSessionId, newBinding, hintJustAutoBound: justCreated);
            }

            // ④ 拦截——0 或 ≥2 个实例，无 header，无 session（设计文档 §④）。
            return TargetResolution.Failed(TargetErrorKind.Intercept,
                BuildInterceptOrEmpty(snapshot));
        }

        /// <summary>
        /// 多实例未绑定时的 ④ 消息；空注册表则给出一条短的"无实例"消息
        /// （仍属 ④ 邻近情形）。
        /// </summary>
        private static string BuildInterceptOrEmpty(IReadOnlyCollection<InstanceEntry> snapshot)
        {
            if (snapshot.Count == 0)
                return "No VS instance connected. Open a Visual Studio instance with the MCP extension, then retry.";
            return GatewayTools.BuildInterceptMessage(snapshot);
        }
    }
}
