using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.CodeStore.Internal;
using Microsoft.VisualStudio.CppSvc.Internal;
using Microsoft.VisualStudio.Shell;

namespace VsMcp
{
    /// <summary>
    /// 经 VC 原生 CodeStore 的 <see cref="IVCNavigateToFactory"/> 搜索 C++ 符号。
    ///
    /// 这是 VS 的 Ctrl+T / SymbolScanner / Copilot 查 C++ 符号的**真正后端**：
    /// in-proc COM 服务（<c>CPPThreadSafeServiceClass</c>），不经 LSP broker、
    /// 不经经典 NavigateTo v1（C++ 在 VS2026 里改走新的 All-In-One Search，不实现
    /// <c>INavigateToItemProvider</c>）。LSP <c>RequestAllAsync</c> 对 C++ 永远 0，
    /// 因为 C++ 不注册 broker 的 context-free client。所以 C++ 符号必须走这条。
    ///
    /// 线程模型：UI 线程取全局服务，后台线程 CreateSession + DoSearch（VC 对象的
    /// 工作方法要求后台线程——UI 线程上调 CreateSession 会 E_FAIL）。
    /// </summary>
    internal static class VcSymbolSearcher
    {
        /// <summary>搜索 C++ 符号。返回最多 <paramref name="maxResults"/> 个命中
        /// （含文件路径 + 1-based 行/列 + 符号种类）。CodeStore 未就绪或无命中返回空列表。</summary>
        public static async Task<List<SymbolMatch>> SearchAsync(
            AsyncPackage package, string query, int maxResults, CancellationToken ct)
        {
            var empty = new List<SymbolMatch>();
            if (string.IsNullOrWhiteSpace(query)) return empty;

            // UI 线程：取全局 VC 服务（COM 单例，同时实现 IVCNavigateToFactory）。
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var svc = Package.GetGlobalService(typeof(CPPThreadSafeServiceClass));
            if (svc == null) return empty;
            var factory = svc as IVCNavigateToFactory;
            if (factory == null) return empty;

            // 后台线程：CreateSession + DoSearch（VC 工作方法要求后台线程）。
            var session = await Task.Run(() => factory.CreateSession(), ct).ConfigureAwait(true);
            if (session == null) return empty;
            try
            {
                var results = await Task.Run(() => session.DoSearch(
                    query, false, VCSearchScope.ssEntireSolution, true, new VcCancel(ct)), ct).ConfigureAwait(true);
                if (results == null) return empty;

                var hits = new List<SymbolMatch>();
                try
                {
                    while (hits.Count < maxResults && !ct.IsCancellationRequested)
                    {
                        var st = results.GetNextResult2(
                            out var idItem, out var idFile, out var name,
                            out var kind, out var lang, out var sort,
                            out var parentSort, out var posHash,
                            out var fileNameStr, out var span,
                            out var matches, out var fuzzy);
                        if (st != VCResultStatus.rsMore) break; // rsDone / rsIncomplete*

                        if (!string.IsNullOrEmpty(name))
                        {
                            hits.Add(new SymbolMatch(
                                Name: name,
                                FilePath: fileNameStr?.Value,
                                Line: span.iStartLine,      // 1-based，与 VS 行号一致
                                Column: span.iStartCol,
                                Kind: kind.ToString(),
                                Language: MapLanguage(lang),
                                Container: parentSort?.Value));
                        }
                    }
                }
                finally { try { results.Close(); } catch (Exception ex) { SymbolFacade.ProbeLog($"VC results.Close failed: {ex.GetType().Name}: {ex.Message}"); } }
                return hits;
            }
            finally { try { session.Close(); } catch (Exception ex) { SymbolFacade.ProbeLog($"VC session.Close failed: {ex.GetType().Name}: {ex.Message}"); } }
        }

        private static string MapLanguage(VCItemLang lang) => lang switch
        {
            VCItemLang.ilCPP => "C++",
            VCItemLang.ilIDL => "IDL",
            VCItemLang.ilMetadata => "Metadata",
            _ => lang.ToString(),
        };
    }

    /// <summary>
    /// <see cref="IVCCancellationToken"/> 的最小托管实现：把 .NET
    /// <see cref="CancellationToken"/> 包给 VC 的 DoSearch。RegisterCancelAction
    /// 返回 null（不做细粒度取消注册，靠 QueryCancelled 中断循环）——demo 已验证可行。
    /// </summary>
    internal sealed class VcCancel : IVCCancellationToken
    {
        private readonly CancellationToken _ct;
        public VcCancel(CancellationToken ct) { _ct = ct; }
        public bool QueryCancelled() => _ct.IsCancellationRequested;
        public IVCCancellationTokenRegistration RegisterCancelAction(IVCCancelAction a) => null!;
    }
}
