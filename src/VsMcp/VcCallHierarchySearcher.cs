using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.CodeStore.Internal;
using Microsoft.VisualStudio.CppSvc.Internal;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace VsMcp
{
    /// <summary>
    /// 经 VC 原生 CallHierarchy 查询 C++ 函数调用关系（callers 谁调用了它 / callees 它调用了谁）。
    /// 这是 VS「调用层次结构」窗口的后端（右键函数 → 调用层次结构）。
    ///
    /// API 路径（eval_csharp demo 验证）：
    /// - factory = CPPThreadSafeServiceClass as IVCCallHierarchyMemberItemFactory（与 IVCNavigateToFactory /
    ///   IVCCodeStoreManager 同一个 COM 单例）。
    /// - defId：用 IVCQueryCodeStore.GetCodeItems 找函数。**一个函数名通常匹配多个符号**——重载、
    ///   接口声明（.h）+ 实现（.cpp）、不同类的同名方法。CallsTo/CallsFrom 按符号查，只取一个 defId
    ///   会漏（典型：GetUtf8 取实现 defId，callers 因 C++ 多态全 0；取接口声明才查到调用者）。故遍历
    ///   所有匹配 defId，对每个 CreateMemberItem + StartSearch，合并去重。
    /// - member = factory.CreateMemberItem(defId)：**用 IdItem，不要用 CreateMemberItemByPos**——后者
    ///   对大 solution 同步建整张调用图，DAS-claude 这种直接 OOM；IdItem 路径不建图、不 OOM。
    /// - member 是 **STA COM**（绑定创建线程）——CreateMemberItem / StartSearch / GetData 必须同线程
    ///   （demo 验证后台 Task.Run 创建 + 另一 Task.Run 读 GetData 会 RPC_E_WRONG_THREAD）。故 member
    ///   操作全留 UI 线程；qcs 是 MTA 可后台（FindDefinitionIds）。
    /// - member.StartSearch(CallsTo|CallsFrom, ...) 异步启动，结果经 callback 投递，SearchSucceeded/
    ///   SearchFailed 通知完成。**不调 WaitSearch**（阻塞）——用 TaskCompletionSource 等 callback，await，
    ///   不卡 UI。SearchFailed（如 .h 声明的 CallsFrom「未能定位函数定义」）当该 defId 无结果跳过，
    ///   不算异常（.h 声明无函数体，callees 必失败）。
    ///
    /// 这些是 VS Internal API（非公开 ABI，*.Internal 程序集 / PrivateAssemblies），跨大版本不保证
    /// 稳定；COM 对象用 Com.Use / 显式 ReleaseComObject 确定性释放（见 <see cref="Com"/>）。
    /// </summary>
    internal static class VcCallHierarchySearcher
    {
        private const int DefaultTimeoutMs = 30_000;

        /// <summary>查 <paramref name="query"/> 函数的调用关系。
        /// <paramref name="direction"/>："callers"（谁调用了它，CallsTo）/ "callees"（它调用了谁，CallsFrom，默认）。</summary>
        public static async Task<CallGraphResult> QueryAsync(
            AsyncPackage package, string query, string direction, int maxResults, int timeoutSeconds, CancellationToken ct)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var svc = Package.GetGlobalService(typeof(CPPThreadSafeServiceClass));
            var factory = svc as IVCCallHierarchyMemberItemFactory;
            var mgr = svc as IVCCodeStoreManager;
            if (factory == null || mgr == null)
                return new CallGraphResult(Found: false, Query: query, Direction: direction,
                    Nodes: new List<CallGraphNode>(), TimedOut: false, Error: "VC CallHierarchy 不可用");

            var category = string.Equals(direction, "callers", StringComparison.OrdinalIgnoreCase)
                ? VCCallHierarchySearchCategory.VCCallHierarchySearchCategory_CallsTo
                : VCCallHierarchySearchCategory.VCCallHierarchySearchCategory_CallsFrom;

            int timeoutMs = timeoutSeconds > 0 ? timeoutSeconds * 1000 : DefaultTimeoutMs;
            return await QueryCoreAsync(mgr, factory, query, direction, category, maxResults, timeoutMs, ct)
                .ConfigureAwait(true);
        }

        private static async Task<CallGraphResult> QueryCoreAsync(
            IVCCodeStoreManager mgr, IVCCallHierarchyMemberItemFactory factory,
            string query, string direction, VCCallHierarchySearchCategory category,
            int maxResults, int timeoutMs, CancellationToken ct)
        {
            // 后台找所有匹配的函数 IdItem（qcs 是 MTA，后台安全）。
            var defIds = await Task.Run(() => FindDefinitionIds(mgr, query), ct).ConfigureAwait(true);
            if (defIds.Count == 0)
                return new CallGraphResult(Found: false, Query: query, Direction: direction,
                    Nodes: new List<CallGraphNode>(), TimedOut: false, Error: "未找到函数");

            var allNodes = new List<CallGraphNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);  // name|file|line 去重（多个 defId 结果会重叠）
            bool timedOut = false;

            // 总超时跨所有 defId（共享 linkedCts）。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);

            foreach (var defId in defIds)
            {
                if (linkedCts.IsCancellationRequested) { timedOut = !ct.IsCancellationRequested; break; }
                if (allNodes.Count >= maxResults) break;

                var found = new List<IVCCallHierarchyMemberItem>();
                // SearchSucceeded → true；SearchFailed（.h 声明的 CallsFrom）→ false，该 defId 跳过，不抛。
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cb = new CallHierarchySearchCallback(found,
                    () => tcs.TrySetResult(true),
                    err => tcs.TrySetResult(false));

                IVCCallHierarchyMemberItem? member = null;
                try
                {
                    // member 是 STA COM——CreateMemberItem / StartSearch / GetData 必须 UI 线程（同线程）。
                    member = factory.CreateMemberItem(defId);
                    if (member == null) continue;
                    using var memberScope = Com.Use(member);
                    // pCancel 传 null：传自己的 IVCCancellationToken 实现会让原生搜索卡在 ~97% 进度
                    // 不投递结果（cancel 实现不正确，猜测是 RegisterCancelAction 返回的 registration
                    // 不符合原生预期）。传 null 能取得符合预期的结果，取消改用 member.CancelSearch（见
                    // 下方 Register）。
                    member.StartSearch(
                        category,
                        VCCallHierarchySearchScope.VCCallHierarchySearchScope_EntireSolution,
                        VCCallHierarchySearchOptions.VCCallHierarchySearchOptions_None,
                        VCCallHierarchySearchReason.VCCallHierarchySearchReason_CallHierarchy,
                        cb, null);

                    // UI 线程 async 等（不阻塞 UI）。linkedCts 取消 → CancelSearch + TrySetCanceled。
                    // CancelSearch 在 timer 线程跨线程调，try/catch 吞 RPC_E_WRONG_THREAD。
                    using (linkedCts.Token.Register(() =>
                    {
                        try { member.CancelSearch(category); } catch { }
                        tcs.TrySetCanceled();
                    }))
                    {
                        try { await tcs.Task.ConfigureAwait(true); }
                        catch (OperationCanceledException) { timedOut = !ct.IsCancellationRequested; break; }
                    }

                    // 合并 found（去重）
                    foreach (var node in ReadNodes(found, maxResults - allNodes.Count))
                    {
                        if (seen.Add($"{node.Name}|{node.File}|{node.Line}")) allNodes.Add(node);
                    }
                }
                finally
                {
                    // found items 是 callback 跨线程收集的 COM RCW 列表，逐个 Release；
                    // member 已由 memberScope（Com.Use）在 using 块结束时释放。
                    foreach (var item in found) { try { Marshal.ReleaseComObject(item); } catch { } }
                }
            }

            return new CallGraphResult(Found: true, Query: query, Direction: direction,
                Nodes: allNodes, TimedOut: timedOut, Error: null);
        }

        /// <summary>找所有匹配的函数 IdItem（MemberFunc/Function），按 id 去重，.cpp 定义优先排序
        ///（callees 要函数体，.cpp 先查到才有结果）。</summary>
        private static List<IdItem> FindDefinitionIds(IVCCodeStoreManager mgr, string query)
        {
            var cppIds = new List<IdItem>();
            var hIds = new List<IdItem>();
            var seenRows = new HashSet<long>();

            IVCQueryCodeStore qcs;
            try { qcs = mgr.RequestQueryCodeStore(); }
            catch { return cppIds; }
            if (qcs == null) return cppIds;

            using (Com.Use(qcs))
            {
                var p = new CodeItemQueryParams { bstrName = query, fCaseInsensitive = true, cMaxResults = 100 };
                using (Com.Use(qcs.GetCodeItems(ref p), r => { r.Close(); }, out var results))
                {
                    results.MoveStart();
                    while (results.MoveNext() && (cppIds.Count + hIds.Count) < 50)
                    {
                        var r = new CodeItemRec();
                        results.GetData(ref r);
                        if (r.cik != CodeItemKind.CIK_MemberFunc && r.cik != CodeItemKind.CIK_Function) continue;
                        if (!seenRows.Add(r.id.row)) continue;
                        var fr = new FileRec();
                        qcs.GetFileById(r.IdFile, ref fr);
                        if ((fr.bstrName ?? "").EndsWith(".cpp", StringComparison.OrdinalIgnoreCase))
                            cppIds.Add(r.id);
                        else
                            hIds.Add(r.id);
                    }
                }
            }

            // .cpp 实现优先（callees 要函数体），其次 .h 声明。不截断：同名符号（重载/
            // 各类实现）的 callers 分散在不同 defId，典型如 GetUtf8 的 6 个 callers 只在
            // DasReadOnlyString::GetUtf8 这一个 defId 上，Take(N) 截断会漏掉它。遍历全部、
            // 合并去重；总时长由 timeoutSeconds + keepalive 控制（callers 反向搜全 solution 慢）。
            return cppIds.Concat(hIds).ToList();
        }

        /// <summary>把 callback 收集的 IVCCallHierarchyMemberItem 读成 CallGraphNode（名字 + 签名 + 文件 + 行）。</summary>
        private static List<CallGraphNode> ReadNodes(List<IVCCallHierarchyMemberItem> found, int limit)
        {
            var list = new List<CallGraphNode>();
            if (limit <= 0) return list;
            foreach (var item in found)
            {
                if (list.Count >= limit) break;
                try
                {
                    item.GetData(out var name, out var sig, out var _, out var _,
                        out var _, out var _, out var _);
                    item.GetLocation(out var file, out var span);
                    list.Add(new CallGraphNode(name ?? "", sig ?? "", file ?? "", span.iStartLine));
                }
                catch { }
            }
            return list;
        }

        /// <summary>IVCCallHierarchySearchCallback 实现：收集 AddMemberItemResult，SearchSucceeded/Failed 驱动 TCS。</summary>
        internal sealed class CallHierarchySearchCallback : IVCCallHierarchySearchCallback
        {
            private readonly List<IVCCallHierarchyMemberItem> _found;
            private readonly Action _onSuccess;
            private readonly Action<string> _onFail;
            public CallHierarchySearchCallback(List<IVCCallHierarchyMemberItem> found, Action onSuccess, Action<string> onFail)
            { _found = found; _onSuccess = onSuccess; _onFail = onFail; }
            public void ReportProgress(int done, int total) { }
            public void AddMemberItemResult(IVCCallHierarchyMemberItem item) => _found.Add(item);
            public void AddSymbolItemResult(IVCCallHierarchySymbolItem item) { }
            public void AddScopeItemResult(IVCCallHierarchyScopeItem item) { }
            public void InvalidateResults() { }
            public void SearchSucceeded() => _onSuccess();
            public void SearchFailed(string err) => _onFail(err);
        }
    }

    public sealed record CallGraphNode(string Name, string Signature, string File, int Line);

    /// <param name="Found">false：CallHierarchy 不可用 / 函数未找到。</param>
    /// <param name="TimedOut">true：搜索超时（Nodes 仍返回已收集的部分结果）。</param>
    public sealed record CallGraphResult(
        bool Found, string Query, string Direction,
        List<CallGraphNode> Nodes, bool TimedOut, string? Error);
}
