using System;
using System.Collections.Generic;
using System.Linq;

namespace VsMcpGateway
{
    /// <summary>
    /// Why a <see cref="TargetResolution"/> resolved the way it did. Only the
    /// error cases carry a distinct kind — Program.cs keys hint/forward behavior
    /// on <see cref="TargetResolution.Success"/> + <see cref="TargetResolution.Binding"/>.
    /// </summary>
    public enum TargetErrorKind
    {
        None,
        /// <summary>① Header matched zero instances.</summary>
        WorkspaceMiss,
        /// <summary>① Header matched ≥2 instances.</summary>
        WorkspaceAmbiguous,
        /// <summary>②/③ No instance available / no binding and not exactly one instance (设计文档 §④).</summary>
        Intercept,
        /// <summary>② Binding exists but the target VS dropped offline.</summary>
        InstanceGone,
        /// <summary>The bound VS has no Mcp-Session-Id yet (never initialized through this Gateway).</summary>
        NeedsInitialize,
    }

    /// <summary>
    /// Result of the 4-tier binding resolution (① Header &gt; ② Session &gt;
    /// ③ Auto-bind &gt; ④ Intercept). On success <see cref="Pid"/> is the route
    /// target, <see cref="VsSessionId"/> the per-VS session id to forward, and
    /// <see cref="Binding"/> the session binding whose <c>HintPending</c> gates
    /// hint injection (null for the stateless ① tier, which never injects).
    /// On failure <see cref="ErrorText"/> carries the agent-facing message and
    /// <see cref="ErrorKind"/> classifies it.
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
    /// The Gateway's binding resolution for any non-initialize, non-gateway-tool
    /// request. Implements the 4-tier priority from 设计文档 §Session Binding
    /// (① Header &gt; ② Session &gt; ③ Auto-bind &gt; ④ Intercept) as a single
    /// method over the in-memory tables — no HTTP/pipe IO — so the full routing
    /// logic is unit-testable. The ③ auto-bind tier performs its SessionTable
    /// side effect (create-with-hint) here; ① is stateless (never touches the
    /// SessionTable, per decision MI-06).
    ///
    /// vsSessionId authority: the per-VS id captured at initialize time lives on
    /// <see cref="InstanceEntry.VsSessionId"/> (VS is single-session
    /// server-side). The ① tier reads it directly so a stateless header-routed
    /// request still reaches the right VS-side session.
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

            // ① Header tier — stateless, highest priority (设计文档 §①).
            if (!string.IsNullOrWhiteSpace(workspaceHeader))
            {
                var snap = registry.Snapshot();
                var match = WorkspaceResolver.Resolve(workspaceHeader,
                    snap.Select(e => (e.Info.Pid, e.Info.SolutionDir)));
                if (match.Kind == WorkspaceMatchKind.Single && match.Pid.HasValue)
                {
                    if (registry.TryGet(match.Pid.Value, out var entry))
                        return TargetResolution.Routed(entry.Info.Pid, entry.VsSessionId, binding: null, hintJustAutoBound: false);
                    // Raced off between Snapshot and TryGet — treat as miss.
                }
                else if (match.Kind == WorkspaceMatchKind.None)
                {
                    return TargetResolution.Failed(TargetErrorKind.WorkspaceMiss,
                        GatewayTools.BuildWorkspaceMissMessage(workspaceHeader!, snap));
                }
                else // Ambiguous
                {
                    var matchedInstances = snap.Where(e => match.MatchedPids.Contains(e.Info.Pid)).ToArray();
                    return TargetResolution.Failed(TargetErrorKind.WorkspaceAmbiguous,
                        GatewayTools.BuildAmbiguousMessage(workspaceHeader!, matchedInstances));
                }
            }

            // ② Session tier — a prior initialize / select bound this client session.
            if (!string.IsNullOrEmpty(clientSessionId) && sessions.TryGet(clientSessionId!, out var binding))
            {
                if (!registry.TryGet(binding.Pid, out var entry))
                    return TargetResolution.Failed(TargetErrorKind.InstanceGone,
                        $"Bound VS instance PID {binding.Pid} is no longer connected.");

                string? vsid = entry.VsSessionId;
                if (string.IsNullOrEmpty(vsid))
                {
                    // The target VS was never initialize'd through this Gateway
                    // (select_vs_instance switched to a fresh instance, or the
                    // Gateway restarted and lost the per-VS id). Ask for a new
                    // initialize rather than fabricate one.
                    return TargetResolution.Failed(TargetErrorKind.NeedsInitialize,
                        "VS instance switched. Call initialize again to bind the new instance.");
                }
                // Pass the binding so Program.cs can honor a leftover HintPending
                // from a prior auto-bind whose first tool call is THIS one.
                return TargetResolution.Routed(entry.Info.Pid, vsid, binding, hintJustAutoBound: false);
            }

            // ③ Auto-bind — exactly one instance online, no header, no session.
            var snapshot = registry.Snapshot();
            if (snapshot.Count == 1)
            {
                var only = snapshot.First();
                string? vsid = only.VsSessionId;
                if (string.IsNullOrEmpty(vsid))
                {
                    // Single instance but never initialized yet → the client must
                    // initialize first (the binding can't route without a VS-side
                    // session id). Don't auto-bind into a void.
                    return TargetResolution.Failed(TargetErrorKind.NeedsInitialize,
                        "No VS instance bound. Call initialize first.");
                }
                // Create the binding (stateful) and mark the one-shot hint.
                // clientSessionId is null here (no header, no session) so the
                // forward path must mint one; Program.cs handles that.
                var (_, newBinding) = sessions.CreateWithId(only.Info.Pid, vsid, "auto");
                newBinding.HintPending = true;
                return TargetResolution.Routed(only.Info.Pid, vsid, newBinding, hintJustAutoBound: true);
            }

            // ④ Intercept — 0 or ≥2 instances, no header, no session (设计文档 §④).
            return TargetResolution.Failed(TargetErrorKind.Intercept,
                BuildInterceptOrEmpty(snapshot));
        }

        /// <summary>
        /// ④ message when unbound with multiple instances; an empty registry
        /// instead yields a short "no instance" message (still ④-adjacent).
        /// </summary>
        private static string BuildInterceptOrEmpty(IReadOnlyCollection<InstanceEntry> snapshot)
        {
            if (snapshot.Count == 0)
                return "No VS instance connected. Open a Visual Studio instance with the MCP extension, then retry.";
            return GatewayTools.BuildInterceptMessage(snapshot);
        }
    }
}
