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
    ///
    /// COM 释放：session / results 是每次调用新建的 COM 对象（RCW），Close() 只清内部资源，
    /// native 引用要靠 GC finalizer 减（时机不确定）；codestore 关闭时等引用归零，GC 没及时跑
    /// 就死锁（关 VS hang）。故用 Com.Use 包 using 块：Close + Marshal.ReleaseComObject 确定性释放
    /// （见 <see cref="Com"/>）。factory 是全局服务（GetGlobalService 拿的 CPPThreadSafeServiceClass
    /// 单例），不归我们释放。
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
            if (svc == null) { SymbolFacade.ProbeLog("VC svc=NULL (CPPThreadSafeServiceClass 全局服务拿不到)"); return empty; }
            var factory = svc as IVCNavigateToFactory;
            if (factory == null) { SymbolFacade.ProbeLog($"VC factory=NULL (svc type={svc.GetType().Name} 不实现 IVCNavigateToFactory)"); return empty; }
            SymbolFacade.ProbeLog($"VC factory OK (svc type={svc.GetType().Name})");

            // 后台线程：CreateSession + DoSearch（VC 工作方法要求后台线程）。
            var session = await Task.Run(() => factory.CreateSession(), ct).ConfigureAwait(true);
            if (session == null) { SymbolFacade.ProbeLog("VC session=NULL (factory.CreateSession 返回 null)"); return empty; }
            // session 是每次新建的 RCW——方法级持有，方法结束（含任意 return）确定性 Close + Release。
            // Close 失败只记日志，不影响 Release（Com.Use 对 onDispose 和 Release 都 try/catch）。
            using var sessionScope = Com.Use(session, s =>
            {
                try { s.Close(); }
                catch (Exception ex) { SymbolFacade.ProbeLog($"VC session.Close failed: {ex.GetType().Name}: {ex.Message}"); }
            });

            var results = await Task.Run(() => session.DoSearch(
                query, false, VCSearchScope.ssEntireSolution, true, new VcCancel(ct)), ct).ConfigureAwait(true);
            if (results == null) { SymbolFacade.ProbeLog("VC results=NULL (session.DoSearch 返回 null)"); return empty; }

            var hits = new List<SymbolMatch>();
            using (Com.Use(results, r =>
            {
                try { r.Close(); }
                catch (Exception ex) { SymbolFacade.ProbeLog($"VC results.Close failed: {ex.GetType().Name}: {ex.Message}"); }
            }))
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
                            Kind: VcKindLabels.ToFriendly(kind.ToString()),
                            Language: MapLanguage(lang),
                            Container: parentSort?.Value));
                    }
                }
            }
            return hits;
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
