using System;
using System.Collections.Concurrent;

namespace VsMcpGateway
{
    /// <summary>
    /// One MCP client session's binding to a VS instance. <see cref="Pid"/> is
    /// the routing target; <see cref="VsSessionId"/> is the Mcp-Session-Id the
    /// target VS assigned during initialize (kept here as a per-session cache —
    /// the authoritative per-VS value lives on
    /// <see cref="InstanceEntry.VsSessionId"/> so stateless Header-tier routing
    /// can recover it). <see cref="VsSessionId"/> is null after a
    /// <c>select_vs_instance</c> switch — the new VS has not yet been
    /// initialized, so Wave 2 returns an "initialize again" error in that state
    /// (full lazy initialize is a later enhancement).
    /// <see cref="HintPending"/>, set by the ③ auto-bind path (and the
    /// initialize single-instance fallback), gates the one-shot hint injected
    /// into the next tool-call result (设计文档 §③ MI-07).
    /// </summary>
    public sealed class SessionBinding
    {
        public int Pid { get; set; }
        public string? VsSessionId { get; set; }
        public DateTime BoundAt { get; set; }
        public string Source { get; set; } = "";
        /// <summary>
        /// True after an auto-bind until the next forwarded tool result has the
        /// hint prepended; cleared once (one-shot — 设计文档 decision MI-07).
        /// Explicit binds (_meta.vsPid / select_vs_instance) leave this false so
        /// the user/agent never sees the auto-bind hint on a deliberate choice.
        /// </summary>
        public bool HintPending { get; set; }
        /// <summary>
        /// The client-visible session id (<c>sess-...</c>) this binding is keyed
        /// under. Set when the binding is created so the auto-bind path can echo
        /// a freshly minted id back to a client that sent no Mcp-Session-Id.
        /// </summary>
        public string ClientSessionId { get; set; } = "";

        public SessionBinding(int pid, string? vsSessionId, DateTime boundAt, string source)
        {
            Pid = pid;
            VsSessionId = vsSessionId;
            BoundAt = boundAt;
            Source = source;
        }
    }

    /// <summary>
    /// Gateway-side mapping of client-visible MCP session ids (the
    /// <c>sess-{Guid:N}</c> values the Gateway puts in the Mcp-Session-Id
    /// response header) to the VS instance actually serving the session. This
    /// is the ② Session tier of the binding resolution flow.
    /// </summary>
    public sealed class SessionTable
    {
        private readonly ConcurrentDictionary<string, SessionBinding> _byClientSession =
            new ConcurrentDictionary<string, SessionBinding>(StringComparer.Ordinal);

        /// <summary>
        /// Create a binding and return both the generated client session id and
        /// the binding — callers (initialize handler) need the id to write the
        /// response header.
        /// </summary>
        public (string ClientSessionId, SessionBinding Binding) CreateWithId(int pid, string? vsSessionId, string source)
        {
            string clientSessionId = "sess-" + Guid.NewGuid().ToString("N");
            var binding = new SessionBinding(pid, vsSessionId, DateTime.UtcNow, source)
            {
                ClientSessionId = clientSessionId,
            };
            _byClientSession[clientSessionId] = binding;
            return (clientSessionId, binding);
        }

        public bool TryGet(string clientSessionId, out SessionBinding binding) =>
            _byClientSession.TryGetValue(clientSessionId, out binding!);

        /// <summary>
        /// Re-point an existing client session at a different VS instance (select_vs_instance).
        /// Clears VsSessionId so the next non-initialize request forces a re-initialize.
        /// Also clears HintPending — a deliberate select is never an auto-bind.
        /// </summary>
        public bool Rebind(string clientSessionId, int newPid)
        {
            if (!_byClientSession.TryGetValue(clientSessionId, out var existing))
                return false;
            existing.Pid = newPid;
            existing.VsSessionId = null;
            existing.BoundAt = DateTime.UtcNow;
            existing.Source = "select";
            existing.HintPending = false;
            return true;
        }

        public bool Remove(string clientSessionId) =>
            _byClientSession.TryRemove(clientSessionId, out _);

        /// <summary>Number of bindings currently held. Used by tests to assert
        /// statelessness of the ① Header tier (it must not create bindings).</summary>
        public int Count => _byClientSession.Count;
    }
}
