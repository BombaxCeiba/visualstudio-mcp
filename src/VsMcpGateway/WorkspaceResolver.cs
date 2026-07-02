using System;
using System.Collections.Generic;
using System.IO;

namespace VsMcpGateway
{
    /// <summary>
    /// Outcome of folder-level workspace prefix matching (设计文档 §①).
    /// </summary>
    public enum WorkspaceMatchKind
    {
        /// <summary>No VS instance's SolutionDir matched the header.</summary>
        None,
        /// <summary>Exactly one instance matched (header == SolutionDir or a sub-path).</summary>
        Single,
        /// <summary>Multiple instances matched the same header path.</summary>
        Ambiguous,
    }

    /// <summary>
    /// Result of <see cref="WorkspaceResolver.Resolve"/>. <see cref="Pid"/> is set
    /// only when <see cref="Kind"/>==<see cref="WorkspaceMatchKind.Single"/>.
    /// <see cref="MatchedPids"/> lists every matching PID (1 for Single, all for
    /// Ambiguous, empty for None) so callers can render ambiguity errors.
    /// </summary>
    public sealed class WorkspaceMatch
    {
        public WorkspaceMatchKind Kind { get; }
        public int? Pid { get; }
        public IReadOnlyList<int> MatchedPids { get; }

        public WorkspaceMatch(WorkspaceMatchKind kind, int? pid, IReadOnlyList<int> matchedPids)
        {
            Kind = kind;
            Pid = pid;
            MatchedPids = matchedPids ?? Array.Empty<int>();
        }

        public static readonly WorkspaceMatch None =
            new WorkspaceMatch(WorkspaceMatchKind.None, null, Array.Empty<int>());
    }

    /// <summary>
    /// Pure, dependency-free folder-level prefix matcher used by the ① Header
    /// tier of the binding resolution flow. Given the <c>X-VS-Workspace</c>
    /// header value (a folder or <c>.sln</c> path) and the set of connected VS
    /// instances' <c>(Pid, SolutionDir)</c>, it decides which instance(s) the
    /// header points at.
    ///
    /// Matching rule (设计文档 §①): the header path is a prefix-descendant of a
    /// VS's SolutionDir — i.e. the normalized header EQUALS the SolutionDir, or
    /// the normalized header is a CHILD path of the SolutionDir. The boundary
    /// check (the char after the SolutionDir prefix must be a separator or
    /// end-of-string) prevents <c>D:\MyApp</c> from matching <c>D:\MyAppOther</c>.
    ///
    /// Statelessness is the whole point: the Header tier never touches the
    /// SessionTable, so an agent can retarget mid-session just by changing the
    /// header (设计文档 decision MI-06).
    /// </summary>
    public static class WorkspaceResolver
    {
        /// <summary>
        /// Resolve which VS instance(s) the workspace header points at.
        /// </summary>
        /// <param name="workspaceHeader">Raw <c>X-VS-Workspace</c> value: a
        /// folder path or a <c>.sln</c> file path (the dir is extracted). Null
        /// or whitespace yields <see cref="WorkspaceMatch.None"/>.</param>
        /// <param name="instances">Connected VS instances as
        /// <c>(Pid, SolutionDir)</c>. Instances whose SolutionDir is null/empty
        /// do not participate.</param>
        public static WorkspaceMatch Resolve(string? workspaceHeader, IEnumerable<(int Pid, string? SolutionDir)> instances)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));

            string? headerDir = NormalizeHeader(workspaceHeader);
            if (headerDir == null)
                return WorkspaceMatch.None;

            var matched = new List<int>();
            foreach (var (pid, solutionDir) in instances)
            {
                if (pid <= 0) continue;
                string? solDir = Normalize(solutionDir);
                if (solDir == null) continue; // no solution open → not matchable

                if (IsPrefixMatch(headerDir, solDir))
                    matched.Add(pid);
            }

            if (matched.Count == 0)
                return WorkspaceMatch.None;
            if (matched.Count == 1)
                return new WorkspaceMatch(WorkspaceMatchKind.Single, matched[0], matched);
            return new WorkspaceMatch(WorkspaceMatchKind.Ambiguous, null, matched);
        }

        /// <summary>
        /// Normalize a header value to a comparable directory path, or null if
        /// it cannot be interpreted as a path. A <c>.sln</c> file path is
        /// converted to its containing directory (the header may point at either
        /// the folder or the solution file itself — both mean "this workspace").
        /// Trailing separators are stripped so <c>D:\x</c> and <c>D:\x\</c>
        /// compare equal.
        /// </summary>
        private static string? NormalizeHeader(string? raw)
        {
            string? n = Normalize(raw);
            if (n == null) return null;
            // A .sln header is equivalent to its containing folder — both forms
            // appear in the wild (the design doc explicitly accepts either).
            if (n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                try { return Normalize(Path.GetDirectoryName(n)); }
                catch { return null; }
            }
            return n;
        }

        /// <summary>
        /// Normalize a path-like string: trim, collapse to null when empty,
        /// unify <c>/</c> and <c>\</c> to backslashes (Windows treats both as
        /// separators, and the boundary check already treats both as separators —
        /// so the prefix equality must too, otherwise <c>D:/x</c> would fail to
        /// match <c>D:\x</c>), and strip trailing <c>\</c>/<c>/</c> (a lone root
        /// like <c>D:\</c> keeps its separator so <c>Path.GetDirectoryName</c>
        /// semantics stay sane). Casing is left untouched — comparison is
        /// case-insensitive at match time (<see cref="IsPrefixMatch"/>).
        /// </summary>
        private static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw!.Trim().Replace('/', '\\');
            // Trim trailing separators but never strip past a root ("D:\" stays).
            while (s.Length > 0 && IsSeparator(s[s.Length - 1]))
            {
                // Keep "D:\" / "\\" intact — stripping the root separator turns
                // it into "D:" which Path.GetDirectoryName treats as a label.
                if (IsRoot(s)) break;
                s = s.Substring(0, s.Length - 1);
            }
            return s.Length == 0 ? null : s;
        }

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static bool IsRoot(string s)
        {
            // "D:\" or "D:/" (length 3) — the Windows drive root. A UNC root like
            // "\\server\share" is left alone once trailing separators are gone.
            return s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':' && IsSeparator(s[2]);
        }

        /// <summary>
        /// True iff <paramref name="headerDir"/> is the same as
        /// <paramref name="solutionDir"/> or is a descendant of it, using
        /// case-insensitive comparison (Windows paths). The boundary check
        /// ensures <c>D:\MyApp</c> does NOT match <c>D:\MyAppOther</c>: when the
        /// header is longer, the char immediately after the SolutionDir prefix
        /// must be a separator.
        /// </summary>
        private static bool IsPrefixMatch(string headerDir, string solutionDir)
        {
            // Case-insensitive: Windows file system does not distinguish.
            if (headerDir.Length == solutionDir.Length)
                return string.Equals(headerDir, solutionDir, StringComparison.OrdinalIgnoreCase);

            // headerDir must be LONGER (it's a descendant) and the SolutionDir
            // must be a proper prefix followed by a separator.
            if (headerDir.Length < solutionDir.Length)
                return false;

            if (!string.Equals(
                    headerDir.Substring(0, solutionDir.Length),
                    solutionDir,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // Boundary: the char right after the SolutionDir prefix has to be a
            // path separator, otherwise "D:\MyApp" wrongly matches "D:\MyAppOther".
            return IsSeparator(headerDir[solutionDir.Length]);
        }
    }
}
