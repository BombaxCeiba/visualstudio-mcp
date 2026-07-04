using System;
using System.Collections.Generic;
using System.IO;

namespace VsMcpGateway
{
    /// <summary>
    /// 文件夹级 workspace 前缀匹配的结果（设计文档 §①）。
    /// </summary>
    public enum WorkspaceMatchKind
    {
        /// <summary>没有 VS 实例的 SolutionDir 与 header 匹配。</summary>
        None,
        /// <summary>恰好一个实例匹配（header == SolutionDir 或其子路径）。</summary>
        Single,
        /// <summary>多个实例匹配同一个 header 路径。</summary>
        Ambiguous,
    }

    /// <summary>
    /// <see cref="WorkspaceResolver.Resolve"/> 的结果。<see cref="Pid"/> 仅当
    /// <see cref="Kind"/>==<see cref="WorkspaceMatchKind.Single"/> 时设置。
    /// <see cref="MatchedPids"/> 列出每个匹配的 PID（Single 为 1，Ambiguous 为
    /// 全部，None 为空），让调用方能渲染歧义错误。
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
    /// 纯净、无依赖的文件夹级前缀匹配器，供绑定解析流程的 ① Header 层使用。给定
    /// <c>X-VS-Workspace</c> header 值（一个文件夹或 <c>.sln</c> 路径）和已连接
    /// VS 实例的 <c>(Pid, SolutionDir)</c> 集合，它决定 header 指向哪个（些）实例。
    ///
    /// 匹配规则（设计文档 §①）：header 路径是某个 VS 的 SolutionDir 的前缀后代
    /// ——即归一化后的 header 等于 SolutionDir，或归一化后的 header 是 SolutionDir
    /// 的子路径。边界检查（SolutionDir 前缀之后的那个字符必须是分隔符或字符串
    /// 结束）防止 <c>D:\MyApp</c> 匹配到 <c>D:\MyAppOther</c>。
    ///
    /// 无状态正是其要点：Header 层从不触碰 SessionTable，所以 agent 仅靠改变
    /// header 就能在会话中途重定向目标（设计文档决策 MI-06）。
    /// </summary>
    public static class WorkspaceResolver
    {
        /// <summary>
        /// 解析 workspace header 指向哪个（些）VS 实例。
        /// </summary>
        /// <param name="workspaceHeader">原始 <c>X-VS-Workspace</c> 值：一个文件夹
        /// 路径或 <c>.sln</c> 文件路径（会提取其目录）。Null 或空白会得到
        /// <see cref="WorkspaceMatch.None"/>。</param>
        /// <param name="instances">已连接的 VS 实例，形如
        /// <c>(Pid, SolutionDir)</c>。SolutionDir 为 null/empty 的实例不参与。</param>
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
                if (solDir == null) continue; // 没打开解决方案 → 不可匹配

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
        /// 把 header 值归一化为可比较的目录路径，无法解释为路径则返回 null。
        /// <c>.sln</c> 文件路径会转换为其所在目录（header 可能指向文件夹或解决方案
        /// 文件本身——两者都表示"这个 workspace"）。去掉尾部分隔符，让 <c>D:\x</c>
        /// 与 <c>D:\x\</c> 比较相等。
        /// </summary>
        private static string? NormalizeHeader(string? raw)
        {
            string? n = Normalize(raw);
            if (n == null) return null;
            // .sln header 等价于其所在文件夹——两种形式都常见（设计文档明确两种都接受）。
            if (n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                try { return Normalize(Path.GetDirectoryName(n)); }
                catch { return null; }
            }
            return n;
        }

        /// <summary>
        /// 归一化类路径字符串：trim，空时折叠为 null，统一 <c>/</c> 与 <c>\</c>
        /// 为反斜杠（Windows 把两者都当分隔符，且边界检查也把两者都当分隔符——所以
        /// 前缀相等也必须如此，否则 <c>D:/x</c> 会无法匹配 <c>D:\x</c>），并去掉
        /// 尾部 <c>\</c>/<c>/</c>（像 <c>D:\</c> 这种单独的根保留其分隔符，让
        /// <c>Path.GetDirectoryName</c> 语义保持正常）。大小写不动——匹配时再
        /// 大小写不敏感比较（<see cref="IsPrefixMatch"/>）。
        /// </summary>
        private static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw!.Trim().Replace('/', '\\');
            // 去掉尾部分隔符，但绝不剥到根以下（"D:\" 保留）。
            while (s.Length > 0 && IsSeparator(s[s.Length - 1]))
            {
                // 保持 "D:\" / "\\" 不变——剥掉根分隔符会变成 "D:"，
                // Path.GetDirectoryName 会把它当盘符标签。
                if (IsRoot(s)) break;
                s = s.Substring(0, s.Length - 1);
            }
            return s.Length == 0 ? null : s;
        }

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static bool IsRoot(string s)
        {
            // "D:\" 或 "D:/"（长度 3）——Windows 盘符根。像 "\\server\share" 这种
            // UNC 根在尾部分隔符去掉后就保持不动。
            return s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':' && IsSeparator(s[2]);
        }

        /// <summary>
        /// 当 <paramref name="headerDir"/> 等于 <paramref name="solutionDir"/>
        /// 或是其后代时为 true，使用大小写不敏感比较（Windows 路径）。边界检查
        /// 确保 <c>D:\MyApp</c> 不会匹配 <c>D:\MyAppOther</c>：当 header 更长时，
        /// SolutionDir 前缀之后紧接着的那个字符必须是分隔符。
        /// </summary>
        private static bool IsPrefixMatch(string headerDir, string solutionDir)
        {
            // 大小写不敏感：Windows 文件系统不区分。
            if (headerDir.Length == solutionDir.Length)
                return string.Equals(headerDir, solutionDir, StringComparison.OrdinalIgnoreCase);

            // headerDir 必须更长（它是后代），且 SolutionDir 必须是后跟分隔符的
            // 合适前缀。
            if (headerDir.Length < solutionDir.Length)
                return false;

            if (!string.Equals(
                    headerDir.Substring(0, solutionDir.Length),
                    solutionDir,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // 边界：SolutionDir 前缀之后紧接着的那个字符必须是路径分隔符，否则
            // "D:\MyApp" 会错误匹配 "D:\MyAppOther"。
            return IsSeparator(headerDir[solutionDir.Length]);
        }
    }
}
