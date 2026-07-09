using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsMcp
{
    /// <summary>
    /// VS Object Manager 符号浏览表面的封装。枚举每个已注册的
    /// <see cref="IVsLibrary2"/>（C#、VB、C++ 各自注册自己的），使
    /// <c>find_symbol</c> 与语言无关。源位置（file + line）通过
    /// <c>IVsSimpleObjectList2.GetSourceContextWithOwnership</c> 获取
    /// （当所属库实现了该方法时）；column 永远拿不到——VS 库只返回 file+line。
    /// </summary>
    public sealed class SymbolFacade : IDisposable
    {
        private const int DefaultMaxResults = 16;

        /// <summary>find_symbol 源码上下文默认行数：符号行上下各多少行。0 表示只看符号所在行。
        /// 默认 5：短符号 ±10 行占满半屏费 token，±5 行够看签名与紧邻上下文（要看更多显式调大 contextLines）。</summary>
        private const int DefaultContextLines = 5;

        private readonly AsyncPackage _package;
        private bool _disposed;

        public SymbolFacade(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        /// <summary>
        /// 从 <c>SVsObjectManager</c> 获取 <see cref="IVsObjectManager2"/>。
        /// 每次调用重新获取而非缓存：它是全局 VS 服务，由稳定的 RCW 支撑，
        /// 但保持与 <see cref="DebuggerFacade"/> 一致的模式，并避免任何
        /// 跨调用的生命周期假设。
        /// </summary>
        private async Task<IVsObjectManager2> GetObjectManagerAsync(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            return (IVsObjectManager2?)await _package.GetServiceAsync(typeof(SVsObjectManager))
                ?? throw new InvalidOperationException("IVsObjectManager2 service is not available");
        }

        private DTE2? _dte;

        /// <summary>
        /// 获取 DTE 根对象。缓存：与调试器 RCW 不同，DTE 对象在跨调试会话间稳定。
        /// </summary>
        private async Task<DTE2> GetDteAsync(CancellationToken ct)
        {
            if (_dte != null) return _dte;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _dte = (DTE2?)await _package.GetServiceAsync(typeof(DTE))
                ?? throw new InvalidOperationException("DTE service is not available");
            return _dte;
        }

        /// <summary>
        /// 通过 NavigateTo——VS 的"转到所有"(Ctrl+, / Ctrl+T) 后端——跨所有
        /// 语言搜索符号。NavigateTo 通过 LSP <c>workspace/symbol</c> 能力聚合
        /// 每个已注册的语言提供程序，因此它能以与 Ctrl+T（最初的精度目标）相同
        /// 的精度找到 C++ 符号（经 VC Language Server）；而 Object Manager
        /// （Object Browser 表面）只服务于通用的 Assembly/TypeLib/Folder 库，
        /// 且经 <c>IVsLibrary2.GetList2</c> 在 Folder 库上破坏了堆。NavigateTo
        /// 还给出带 <b>column</b> 精度的源位置，这是 Object Manager 从未暴露的。
        ///
        /// 每个已注册的 <see cref="INavigateToItemProviderFactory"/> 被并行查询；
        /// 提供程序异步运行其真实搜索，并经
        /// <see cref="INavigateToCallback.Done"/> 发出完成信号。每提供程序
        /// 超时阻止单个卡住的语言服务器拖垮整个调用。
        /// </summary>
        public async Task<string> FindSymbolAsync(string query, int maxResults, int maxChars, int contextLines, string? language, string? kind, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(query))
                return "查询为空，请提供符号名。";

            int limit = maxResults > 0 ? maxResults : DefaultMaxResults;
            // 过滤激活时 VC searcher 会在 facade 前按上限截断（SearchAsync 的 while hits.Count < maxResults），
            // 过滤后剩余不足。故过滤时放大收集、facade 过滤后再 take(limit)。系数依据：
            // 4× —— 假设过滤保留率 ≥25%（同类/同语言符号通常占命中 1/4 以上），收 4 倍量→过滤后大概率仍 ≥ limit；
            // 64  —— limit 小时 4× 太少、过滤空间不足，垫到 64；200 —— limit 大时 4× 可能到几百，热门符号（命中上千）
            // 收集几百个会拖到数秒，封顶 200 保响应。无过滤仍传 limit（现状，不变慢）。
            bool filtering = !string.IsNullOrEmpty(language) || !string.IsNullOrEmpty(kind);
            int vcBuffer = filtering ? Math.Min(Math.Max(limit * 4, 64), 200) : limit;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            ProbeLog($"FIND START query='{query}' limit={limit} vcBuffer={vcBuffer} lang='{language}' kind='{kind}' thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");

            var hits = new List<SymbolMatch>();

            // C++：经 VC 原生 CodeStore 的 IVCNavigateToFactory（Ctrl+T 的真正后端，in-proc COM）。
            // LSP RequestAllAsync 对 C++ 永远 0（C++ 不注册 broker client），经典 NavigateTo v1 也
            // 拿不到（C++ 在 VS2026 走新 All-In-One Search，不实现 INavigateToItemProvider）。
            // 所以 C++ 必须直接调 IVCNavigateToFactory。
            try
            {
                var vcHits = await VcSymbolSearcher.SearchAsync(_package, query, vcBuffer, ct).ConfigureAwait(true);
                ProbeLog($"FIND VC hits={vcHits.Count}");
                hits.AddRange(vcHits);
            }
            catch (Exception ex) { ProbeLog($"FIND VC threw {ex.GetType().Name}: {ex.Message}"); }

            // C#/VB 等：经 LSP workspace/symbol（ILanguageServiceBroker2 聚合已注册 LSP client）。
            var broker = await GetBrokerAsync(ct);
            if (broker == null)
            {
                ProbeLog("FIND broker=NULL (ILanguageServiceBroker2 不可用)");
            }
            else
            {
                ProbeLog($"FIND broker type={broker.GetType().Name}");
                var request = new GeneralRequest<WorkspaceSymbolParams, SymbolInformation[]>
                {
                    Method = "workspace/symbol",
                    Request = new WorkspaceSymbolParams { Query = query },
                };
                int clients = 0;
                try
                {
                    await foreach (var (clientName, response) in broker
                        .RequestAllAsync<WorkspaceSymbolParams, SymbolInformation[]>(request, ct)
                        .WithCancellation(ct).ConfigureAwait(true))
                    {
                        clients++;
                        int n = response?.Length ?? 0;
                        ProbeLog($"FIND LSP client='{clientName}' symbols={n}");
                        if (n > 0)
                        {
                            foreach (var si in response!)
                            {
                                var hit = ExtractFromSymbolInformation(si);
                                if (hit != null) hits.Add(hit);
                            }
                        }
                    }
                }
                catch (Exception ex) { ProbeLog($"FIND LSP RequestAllAsync threw {ex.GetType().Name}: {ex.Message}"); }
                ProbeLog($"FIND LSP clients={clients}");
            }

            ProbeLog($"FIND END total hits={hits.Count}");

            // 1. 补全 Language：LSP 的 C# 命中 Language=""（ExtractFromSymbolInformation 写死空串），
            //    按扩展名补回（InferLanguage），否则 language 过滤对 C# 失效。
            // 2. 过滤：language / kind 大小写不敏感子串，非空才套（kind 对友好标签匹配，源头已 VcKindLabels 归一）。
            // 3. 去重（VC + LSP 可能重叠）：按 (Name, FilePath, Line)。
            // 4. 排序：按文件聚合——FilePath OrdinalIgnoreCase（无 FilePath 用 "￿" 占位排末）→ 行号。比"有源位置优先 +
            //    名字序"直观：同名符号的 .h 声明 + .cpp 实现聚一起；同名符号按名字序基本乱序，无意义。
            IEnumerable<SymbolMatch> processed = hits
                .Select(h => string.IsNullOrEmpty(h.Language) ? h with { Language = InferLanguage(h.FilePath) } : h);
            if (!string.IsNullOrEmpty(language))
            {
                var langKey = NormalizeLanguageKey(language);
                processed = processed.Where(h => NormalizeLanguageKey(h.Language) == langKey);
            }
            if (!string.IsNullOrEmpty(kind))
            {
                var kindKey = kind!.ToLowerInvariant();
                processed = processed.Where(h => h.Kind.ToLowerInvariant().Contains(kindKey));
            }
            var ordered = processed
                .GroupBy(h => (h.Name ?? "", h.FilePath ?? "", h.Line)).Select(g => g.First())
                .OrderBy(h => h.FilePath ?? "￿", StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Line)
                .ToList();
            int total = ordered.Count;
            bool truncated = total > limit;
            if (truncated) ordered = ordered.Take(limit).ToList();

            ProbeLog($"FIND filter lang='{language}' kind='{kind}' vcBuffer={vcBuffer} postFilter={total} shown={ordered.Count}");
            return await BuildSymbolTextAsync(ordered, query, total, truncated, maxChars, contextLines, ct).ConfigureAwait(true);
        }

        /// <summary>把符号命中渲染为文本：每个符号头（名/种类/语言/位置）+ 源文件上下文
        /// （符号行 ±contextLines，带行号、▶ 标记符号所在行）。读文件经 FileStream+StreamReader
        /// 逐行跳到目标范围（不读全文）；后台线程跑避免阻塞 UI。输出是纯文本而非 JSON——agent
        /// 直接看到代码上下文，省 token 且无需二次解析。</summary>
        private async Task<string> BuildSymbolTextAsync(List<SymbolMatch> symbols, string query, int total, bool truncated, int maxChars, int contextLines, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                int half = contextLines > 0 ? contextLines : DefaultContextLines;
                var sb = new StringBuilder();
                sb.AppendLine($"找到 {total} 个匹配 '{query}' 的符号" + (truncated ? $"（仅显示前 {symbols.Count}）" : "") + "：");
                foreach (var s in symbols)
                {
                    sb.AppendLine();
                    sb.AppendLine($"── {s.Name} [{s.Kind}, {s.Language}] ── {s.FilePath}:{s.Line} ──");
                    if (string.IsNullOrEmpty(s.FilePath) || s.Line <= 0) continue;
                    try
                    {
                        int start = Math.Max(1, s.Line - half);
                        var lines = new List<string>();
                        using (var fs = new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            for (int i = 1; i < start; i++) { if (sr.ReadLine() == null) break; }
                            for (int i = 0; i < half * 2 + 1; i++) { var l = sr.ReadLine(); if (l == null) break; lines.Add(l); }
                        }
                        for (int i = 0; i < lines.Count; i++)
                        {
                            int ln = start + i;
                            string mark = ln == s.Line ? "▶" : " ";
                            sb.AppendLine($"{mark}{ln,5} │ {lines[i]}");
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"  (读取文件失败: {ex.Message})"); ProbeLog($"FIND 读取上下文失败 {s.FilePath}: {ex.GetType().Name}: {ex.Message}"); }
                }
                string result = sb.ToString();
                if (maxChars > 0 && result.Length > maxChars)
                    result = result.Substring(0, maxChars) + $"\n...（输出已截断至 {maxChars} 字符，完整 {result.Length} 字符；调高 maxChars、减小 contextLines 或收窄 query）";
                return result;
            }, ct);
        }

        /// <summary>
        /// 获取 LSP 公共 broker（<see cref="ILanguageServiceBroker2"/>，MEF 导出）。经
        /// <c>SComponentModel</c> 拿，避免直接 new 缺依赖；切 UI 线程（MEF 解析要求）。
        /// </summary>
        private async Task<ILanguageServiceBroker2?> GetBrokerAsync(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            try
            {
                var cm = (IComponentModel?)await _package.GetServiceAsync(typeof(SComponentModel));
                return cm?.GetService<ILanguageServiceBroker2>();
            }
            catch (Exception ex)
            {
                ProbeLog($"FIND GetBroker threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从 LSP <see cref="SymbolInformation"/> 直接提取符号 + 源位置（file/1-based line/column）。
        /// LSP 位置 0-based，+1 转 1-based。字段全 public，无需反射。
        /// </summary>
        private static SymbolMatch? ExtractFromSymbolInformation(SymbolInformation si)
        {
            if (si == null) return null;

            string? filePath = si.Location?.Uri?.LocalPath;
            int line = -1, column = -1;
            var start = si.Location?.Range?.Start;
            if (start != null)
            {
                line = start.Line + 1;       // LSP 0-based → 1-based
                column = start.Character + 1;
            }

            if (string.IsNullOrEmpty(si.Name) && filePath == null)
                return null;

            return new SymbolMatch(
                Name: si.Name ?? "",
                FilePath: filePath,
                Line: line,
                Column: column,
                Kind: VcKindLabels.ToFriendly(si.Kind.ToString()),
                Language: "",
                Container: si.ContainerName);
        }

        /// <summary>按文件扩展名推断语言串（补全 LSP C# 命中缺失的 Language，供 language 过滤）。</summary>
        private static string InferLanguage(string? filePath)
        {
            switch (Path.GetExtension(filePath ?? "").ToLowerInvariant())
            {
                case ".cs": return "C#";
                case ".vb": return "VB";
                case ".cpp": case ".cc": case ".cxx": case ".c":
                case ".h": case ".hpp": case ".hh": case ".hxx": case ".inl":
                case ".ixx": case ".cppm": case ".mpp": case ".mxx":   // C++ 模块（MSVC .ixx / 标准 .cppm 等）
                    return "C++";
                default: return "";
            }
        }

        /// <summary>把语言标签/用户输入归一到统一 key，供 language 过滤等值匹配。直接 Contains
        /// 子串匹配不了缩写（"cpp" 非 "C++" 的子串），故双方都过此归一后等值比较：
        /// c++/cpp/cxx→cpp；c#/csharp/cs→csharp；vb/visualbasic→vb；其余原样小写。</summary>
        private static string NormalizeLanguageKey(string? s)
        {
            var x = (s ?? "").ToLowerInvariant().Replace(" ", "");
            switch (x)
            {
                case "c++":
                case "cpp":
                case "cxx":
                    return "cpp";
                case "c#":
                case "csharp":
                case "cs":
                    return "csharp";
                case "vb":
                case "visualbasic":
                    return "vb";
                default:
                    return x;
            }
        }

        /// <summary>
        /// 解析类型在解决方案类型图中的位置：完整祖先链（递归 Bases 到根）、直接后代
        /// （跨整个解决方案的派生/实现）、以及兄弟类型（共享某个基类的其他类型）。这是
        /// 读源代码无法复现的 VS 独有视图——反向继承需要索引整个解决方案，VS 语言服务
        /// 做到了，但 grep 源代码做不到。
        ///
        /// 两条实现路径：<see cref="VcTypeHierarchySearcher"/>（C++，经 VC CodeStore，含 Open Folder /
        /// CMake——DTE 在这些场景下 project.CodeModel=null 不可用）优先；VC 不可用时 fallback
        /// 到 DTE CodeModel（C#/VB 传统 sln）。
        /// </summary>
        public async Task<TypeHierarchyResult> GetTypeHierarchyAsync(string typeName, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(typeName))
                return NotFound(typeName);

            // 优先 VC CodeStore（C++，含 Open Folder）。find_symbol 同款 in-proc COM；
            // null 表示 VC 不可用才 fallback DTE；Found=false（含 Found=true）是 VC 权威答案，直接用。
            try
            {
                var vc = await VcTypeHierarchySearcher.QueryAsync(_package, typeName, ct).ConfigureAwait(true);
                if (vc != null) return vc;
            }
            catch (Exception ex) { ProbeLog($"TYPEHIER VC threw {ex.GetType().Name}: {ex.Message}"); }

            return await GetTypeHierarchyViaDteAsync(typeName, ct).ConfigureAwait(true);
        }

        /// <summary>get_call_graph：C++ 函数调用关系（callers 谁调用了它 / callees 它调用了谁），
        /// 经 VC CallHierarchy（VS「调用层次结构」窗口后端）。仅 C++；其它语言无对应 API。
        /// direction: "callers"（CallsTo）/ "callees"（CallsFrom，默认）。</summary>
        public async Task<CallGraphResult> GetCallGraphAsync(string query, string direction, int maxResults, int timeoutSeconds, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(query))
                return new CallGraphResult(Found: false, Query: query, Direction: direction,
                    Nodes: new List<CallGraphNode>(), TimedOut: false, Error: "查询为空");
            return await VcCallHierarchySearcher.QueryAsync(_package, query, direction, maxResults, timeoutSeconds, ct)
                .ConfigureAwait(true);
        }

        /// <summary>DTE CodeModel 路径（C#/VB 传统 sln）。Open Folder 下 project.CodeModel=null
        /// 走不到这里——GetTypeHierarchyAsync 会先经 VC CodeStore 返回。</summary>
        private async Task<TypeHierarchyResult> GetTypeHierarchyViaDteAsync(string typeName, CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            var solution = dte.Solution;
            if (solution == null || string.IsNullOrWhiteSpace(solution.FullName))
                return NotFound(typeName);

            string? foundName = null, foundKind = null, foundLang = null;
            var defFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ancestors = new List<TypeRelativeNode>();
            var ancestorSeen = new HashSet<string>(StringComparer.Ordinal);
            var descendantNames = new HashSet<string>(StringComparer.Ordinal);
            var descendants = new List<TypeRelativeNode>();
            var siblingNames = new HashSet<string>(StringComparer.Ordinal);
            var siblings = new List<TypeRelativeNode>();

            foreach (Project project in solution.Projects)
            {
                CodeModel? codeModel = null;
                try { codeModel = project.CodeModel; } catch { /* folder/unloaded projects throw */ }
                if (codeModel == null) continue;

                CodeType? type = null;
                try { type = codeModel.CodeTypeFromFullName(typeName); } catch { }
                if (type == null) continue;

                if (foundName == null)
                {
                    foundName = type.FullName ?? type.Name ?? typeName;
                    try { foundKind = type.Kind.ToString(); } catch { }
                    try { foundLang = codeModel.Language; } catch { }
                }

                var selfName = type.FullName ?? type.Name ?? typeName;

                // 该项目视角下该类型的定义文件。
                try { var f = type.ProjectItem?.FileNames[1]; if (!string.IsNullOrEmpty(f)) defFiles.Add(f); } catch { }

                // 祖先：递归沿 Bases 向上走到根。
                try { CollectAncestors(type, ancestors, ancestorSeen, selfName); } catch { }

                // 后代：在该项目中谁派生自 / 实现了该类型。
                try
                {
                    foreach (CodeElement de in type.DerivedTypes)
                        if (de is CodeType d)
                        {
                            var dn = d.FullName ?? d.Name ?? "";
                            if (!string.IsNullOrEmpty(dn) && descendantNames.Add(dn))
                                descendants.Add(MakeNode(d));
                        }
                }
                catch { }

                // 兄弟：每个 Base 的后代，排除自身。
                try
                {
                    foreach (CodeElement be in type.Bases)
                    {
                        if (!(be is CodeType b)) continue;
                        foreach (CodeElement se in b.DerivedTypes)
                        {
                            if (!(se is CodeType s)) continue;
                            var sn = s.FullName ?? s.Name ?? "";
                            if (string.IsNullOrEmpty(sn) || sn == selfName) continue;
                            if (siblingNames.Add(sn))
                                siblings.Add(MakeNode(s));
                        }
                    }
                }
                catch { }
            }

            if (foundName == null)
                return NotFound(typeName);

            return new TypeHierarchyResult(
                Found: true,
                Query: typeName,
                Name: foundName,
                Kind: foundKind,
                Language: foundLang,
                DefinitionFiles: defFiles.ToList(),
                Ancestors: ancestors,
                Descendants: descendants,
                Siblings: siblings);
        }

        private static void CollectAncestors(CodeType type, List<TypeRelativeNode> result, HashSet<string> seen, string skipName)
        {
            CodeElements? bases = null;
            try { bases = type.Bases; } catch { }
            if (bases == null) return;
            foreach (CodeElement be in bases)
            {
                if (!(be is CodeType b)) continue;
                var bn = b.FullName ?? b.Name ?? "";
                if (string.IsNullOrEmpty(bn) || bn == skipName) continue;
                if (!seen.Add(bn)) continue;
                result.Add(MakeNode(b));
                CollectAncestors(b, result, seen, skipName);
            }
        }

        private static TypeRelativeNode MakeNode(CodeType t)
        {
            string? file = null;
            int line = -1;
            try { file = t.ProjectItem?.FileNames[1]; } catch { }
            try { line = t.StartPoint?.Line ?? -1; } catch { }
            return new TypeRelativeNode(
                Name: t.FullName ?? t.Name ?? "",
                Kind: t.Kind.ToString(),
                File: file,
                Line: line);
        }

        private static TypeHierarchyResult NotFound(string query) =>
            new TypeHierarchyResult(
                Found: false, Query: query, Name: null, Kind: null, Language: null,
                DefinitionFiles: new List<string>(),
                Ancestors: new List<TypeRelativeNode>(),
                Descendants: new List<TypeRelativeNode>(),
                Siblings: new List<TypeRelativeNode>());

        private static string? GetLibraryGuid(IVsLibrary2 library)
        {
            try
            {
                // IVsLibrary2.GetGuid 经指向非托管 GUID 结构体（16 字节）的
                // IntPtr 返回库 GUID。将其封送回托管 Guid。
                if (library.GetGuid(out IntPtr guidPtr) == VSConstants.S_OK && guidPtr != IntPtr.Zero)
                {
                    return Marshal.PtrToStructure<Guid>(guidPtr).ToString("B");
                }
            }
            catch
            {
            }
            return null;
        }

        private static string GetPropertyString(IVsObjectList2 list, uint index, int propertyId)
        {
            try
            {
                if (list.GetProperty(index, propertyId, out object value) == VSConstants.S_OK && value is string s)
                    return s;
            }
            catch
            {
            }
            return "";
        }

        /// <summary>
        /// 在给定源位置驱动 VS 的 Edit.GoToDefinition 命令，并返回语言服务跳转
        /// 到的位置。有编辑器副作用（打开定义文件、移动光标）——由
        /// EnableGoToDefinition 选项（默认关闭）把关，故除非用户主动开启，
        /// agent 永远看不到此工具。
        /// </summary>
        public async Task<GoToDefinitionResult> GoToDefinitionAsync(string file, int line, int column, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file) || line < 1 || column < 1)
                throw new ArgumentException("file, line (1-based), and column (1-based) are all required");

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);

            // 打开目标文件并将光标定位到查询位置。
            dte.ItemOperations.OpenFile(file, EnvDTE.Constants.vsViewKindTextView);
            var doc = dte.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Failed to open " + file);
            var sel = (EnvDTE.TextSelection)doc.Selection;
            sel.MoveToLineAndOffset(line, column);

            // 交给语言服务。若它解析到定义就跳转光标（可能切换
            // ActiveDocument）；否则光标不动——被检测为 Found=false。
            string beforeDoc = doc.FullName;
            int beforeLine = sel.ActivePoint.Line;
            try { dte.ExecuteCommand("Edit.GoToDefinition"); } catch { }

            var afterDoc = dte.ActiveDocument;
            var afterSel = (EnvDTE.TextSelection)afterDoc.Selection;
            bool found = !string.Equals(afterDoc.FullName, beforeDoc, StringComparison.OrdinalIgnoreCase)
                         || afterSel.ActivePoint.Line != beforeLine;

            return new GoToDefinitionResult(
                Found: found,
                File: found ? afterDoc.FullName : null,
                Line: found ? afterSel.ActivePoint.Line : -1,
                Column: found ? afterSel.ActivePoint.LineCharOffset : -1);
        }

        private static readonly string ProbeLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vs-mcp", "vs-probe.log");

        /// <summary>每步的 VS 侧探针日志，写入 ~/.vs-mcp/vs-probe.log。
        /// COM 调用期间的原生访问违教会直接干掉 VS 进程（SafeCall 抓不到），
        /// 故每步在可能崩溃的调用之前记录——文件最后一行就是崩溃点。
        /// internal：VcSymbolSearcher 等同类内工具也用它记录错误路径。</summary>
        internal static void ProbeLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProbeLogPath) ?? "");
                File.AppendAllText(ProbeLogPath, $"{DateTime.Now:HH:mm:ss.fff} [t{System.Threading.Thread.CurrentThread.ManagedThreadId}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed) _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SymbolFacade));
        }
    }
}
