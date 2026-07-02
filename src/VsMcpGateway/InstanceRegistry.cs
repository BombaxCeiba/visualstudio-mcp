using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// One connected VS instance: its pipe router (the demux channel back to
    /// that VS), the latest registration info (PID / solution / version), and
    /// the last time we saw activity from it. <see cref="LastSeen"/> is touched
    /// on register, on every heartbeat, and on every solution-changed push;
    /// Wave 4 will use it for stale eviction.
    ///
    /// <see cref="VsSessionId"/> is the authoritative per-VS Mcp-Session-Id the
    /// VS's SDK assigned the first time a client initialize reached it. VS is
    /// single-session server-side, so every route to this VS — whether the
    /// request came in via a session binding, the ① Header tier (stateless), or
    /// the ③ auto-bind tier — reuses this same id. Captured at initialize time
    /// by Program.cs and mirrored into the originating SessionBinding for
    /// convenience.
    /// </summary>
    public sealed class InstanceEntry
    {
        public PipeRouter Router { get; }
        public PipeRegister Info { get; set; }
        public DateTime LastSeen { get; set; }
        public string? VsSessionId { get; set; }

        public InstanceEntry(PipeRouter router, PipeRegister info, DateTime lastSeen)
        {
            Router = router ?? throw new ArgumentNullException(nameof(router));
            Info = info;
            LastSeen = lastSeen;
        }
    }

    /// <summary>
    /// Gateway-side registry of every connected VS instance, keyed by PID.
    /// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>. The
    /// HTTP routing layer consults this to resolve targets for initialize (via
    /// <c>_meta.vsPid</c> or the single-instance fallback) and to render
    /// <c>list_vs_instances</c>. Lifetime of an entry is tied to its pipe
    /// connection: when the read loop in <see cref="PipeRouter"/> observes a
    /// disconnect, the Program.cs accept loop removes the entry.
    /// </summary>
    public sealed class InstanceRegistry
    {
        private readonly ConcurrentDictionary<int, InstanceEntry> _byPid =
            new ConcurrentDictionary<int, InstanceEntry>();

        /// <summary>Insert or replace an entry for a PID.</summary>
        public void Register(InstanceEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            _byPid[entry.Info.Pid] = entry;
        }

        /// <summary>Refresh solution/version info for a known PID (heartbeat /
        /// solution-changed). Called with a freshly-pushed
        /// <see cref="PipeSolutionChanged"/> (or an equivalent
        /// <see cref="PipeRegister"/>) so the routing table's view of each VS's
        /// open solution stays live without a reconnect.</summary>
        public void UpdateInfo(int pid, PipeRegister info)
        {
            if (_byPid.TryGetValue(pid, out var existing))
            {
                existing.Info = info;
                existing.LastSeen = DateTime.UtcNow;
            }
        }

        /// <summary>Record the VS-assigned Mcp-Session-Id for this VS instance
        /// the first time an initialize reaches it. Per-VS authoritative (VS is
        /// single-session server-side). No-op if the PID has dropped.</summary>
        public void SetVsSessionId(int pid, string vsSessionId)
        {
            if (string.IsNullOrEmpty(vsSessionId)) return;
            if (_byPid.TryGetValue(pid, out var existing))
                existing.VsSessionId = vsSessionId;
        }

        public bool TryGet(int pid, out InstanceEntry entry) =>
            _byPid.TryGetValue(pid, out entry!);

        public bool Remove(int pid) => _byPid.TryRemove(pid, out _);

        /// <summary>Snapshot of all connected instances — used by list_vs_instances and single-instance fallback.</summary>
        public IReadOnlyCollection<InstanceEntry> Snapshot() => _byPid.Values.ToArray();

        public int Count => _byPid.Count;
    }
}
