using System;
using System.Collections.Concurrent;

namespace VsMcpGateway
{
    /// <summary>
    /// One MCP client session's binding to a VS instance. <see cref="Pid"/> is
    /// the routing target; <see cref="VsSessionId"/> is the Mcp-Session-Id the
    /// target VS assigned during initialize (forwarded back to that VS on every
    /// subsequent request so its SDK resumes the right server-side session).
    /// <see cref="VsSessionId"/> is null after a <c>select_vs_instance</c>
    /// switch — the new VS has not yet been initialized, so Wave 2 returns an
    /// "initialize again" error in that state (full lazy initialize is a later
    /// enhancement).
    /// </summary>
    public sealed class SessionBinding
    {
        public int Pid { get; set; }
        public string? VsSessionId { get; set; }
        public DateTime BoundAt { get; set; }
        public string Source { get; set; } = "";

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
            var binding = new SessionBinding(pid, vsSessionId, DateTime.UtcNow, source);
            _byClientSession[clientSessionId] = binding;
            return (clientSessionId, binding);
        }

        public bool TryGet(string clientSessionId, out SessionBinding binding) =>
            _byClientSession.TryGetValue(clientSessionId, out binding!);

        /// <summary>
        /// Re-point an existing client session at a different VS instance (select_vs_instance).
        /// Clears VsSessionId so the next non-initialize request forces a re-initialize.
        /// </summary>
        public bool Rebind(string clientSessionId, int newPid)
        {
            if (!_byClientSession.TryGetValue(clientSessionId, out var existing))
                return false;
            existing.Pid = newPid;
            existing.VsSessionId = null;
            existing.BoundAt = DateTime.UtcNow;
            existing.Source = "select";
            return true;
        }

        public bool Remove(string clientSessionId) =>
            _byClientSession.TryRemove(clientSessionId, out _);
    }
}
