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
    ///
    /// 版本适配：StartSearch / GetLocation / CancelSearch 经 <see cref="CallHierarchyMemberAccess"/>
    /// 包装层按 VS 主版本分流——Dev18 (VS2026) 走手写 6 参 StartSearch（pCancel=null）+ 2 out
    /// GetLocation（file + VCTextSpan，取 iStartLine）；Dev17 (VS2022) 走手写 5 参 StartSearch
    /// （无 pCancel）+ 5 out GetLocation（file + 4 int，取 pStartLine）。两版手写接口逐槽对照
    /// 反编译结论见 <see cref="CallHierarchyAccess"/> 顶部注释。GetData 两版同签名同槽3，仍直调
    /// 互操作接口，不经包装层。
    /// </summary>
    internal static class VcCallHierarchySearcher
    {
        private const int DefaultTimeoutMs = 30_000;

        /// <summary>查 <paramref name="query"/> 函数的调用关系。
        /// <paramref name="direction"/>："callers"（谁调用了它，CallsTo）/ "callees"（它调用了谁，CallsFrom，默认）。
        /// <paramref name="progress"/>：非 null 时（仅 callers 保活路径传），defId 循环中更新实时进度，
        /// 供调用方 KeepAliveNotifier 周期读取生成进度文本。</summary>
        public static async Task<CallGraphResult> QueryAsync(
            AsyncPackage package, string query, string direction, int maxResults, int timeoutSeconds,
            CancellationToken ct, CallGraphProgress? progress = null)
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

            // 取一次 VS 主版本号（17=Dev17/VS2022，18=Dev18/VS2026）贯穿本次查询，
            // 不每个 defId 重测（同一次 query 内 VS 版本恒定）。DTE.Version 要求 UI 线程。
            int devMajor = CallHierarchyMemberAccess.DetectDevMajor(package);

            int timeoutMs = timeoutSeconds > 0 ? timeoutSeconds * 1000 : DefaultTimeoutMs;
            return await QueryCoreAsync(mgr, factory, query, direction, category, maxResults, timeoutMs, devMajor, ct, progress)
                .ConfigureAwait(true);
        }

        private static async Task<CallGraphResult> QueryCoreAsync(
            IVCCodeStoreManager mgr, IVCCallHierarchyMemberItemFactory factory,
            string query, string direction, VCCallHierarchySearchCategory category,
            int maxResults, int timeoutMs, int devMajor, CancellationToken ct, CallGraphProgress? progress = null)
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

            // 进度状态初始化：此时已知 defId 总数，保活 timer 据此生成「搜索调用方… i/N」。
            progress?.Begin(DateTime.UtcNow, defIds.Count);

            for (int di = 0; di < defIds.Count; di++)
            {
                var defId = defIds[di];
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
                    // 经包装层按 VS 主版本分流 StartSearch / CancelSearch：
                    // Dev18 走手写 6 参 StartSearch（第 6 参 pCancel 恒传 null，传自己的 cancel token
                    // 会让原生搜索卡在 ~97% 不投递，已验证 1.51）；Dev17 走手写 5 参 StartSearch
                    // （原生签名无 pCancel）。pCancel 处理由包装层内部完成，调用方不传。
                    var access = CallHierarchyMemberAccess.Create(member, devMajor);
                    access.StartSearch(
                        category,
                        VCCallHierarchySearchScope.VCCallHierarchySearchScope_EntireSolution,
                        VCCallHierarchySearchOptions.VCCallHierarchySearchOptions_None,
                        VCCallHierarchySearchReason.VCCallHierarchySearchReason_CallHierarchy,
                        cb);

                    // UI 线程 async 等（不阻塞 UI）。linkedCts 取消 → CancelSearch + TrySetCanceled。
                    // CancelSearch 在 timer 线程跨线程调，try/catch 吞 RPC_E_WRONG_THREAD。
                    using (linkedCts.Token.Register(() =>
                    {
                        try { access.CancelSearch(category); } catch { }
                        tcs.TrySetCanceled();
                    }))
                    {
                        try { await tcs.Task.ConfigureAwait(true); }
                        catch (OperationCanceledException) { timedOut = !ct.IsCancellationRequested; break; }
                    }

                    // 合并 found（去重）
                    foreach (var node in ReadNodes(found, maxResults - allNodes.Count, devMajor))
                    {
                        if (seen.Add($"{node.Name}|{node.File}|{node.Line}")) allNodes.Add(node);
                    }
                    // 该 defId 搜索完成——更新进度（已处理 di+1/N，累计找到 allNodes.Count）。
                    progress?.Update(di + 1, allNodes.Count);
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
        private static List<CallGraphNode> ReadNodes(List<IVCCallHierarchyMemberItem> found, int limit, int devMajor)
        {
            var list = new List<CallGraphNode>();
            if (limit <= 0) return list;
            foreach (var item in found)
            {
                if (list.Count >= limit) break;
                try
                {
                    // GetData 两版同签名同槽3，仍直调互操作接口（跨版本安全）。
                    item.GetData(out var name, out var sig, out var _, out var _,
                        out var _, out var _, out var _);
                    // GetLocation 走包装层：Dev18 取 VCTextSpan.iStartLine，Dev17 取独立 pStartLine
                    // （互操作 GetLocation 是 Dev18 形状，VS2022 上槽位/签名错，必须经包装分流）。
                    var itemAccess = CallHierarchyMemberAccess.Create(item, devMajor);
                    var (file, line) = itemAccess.GetLocation();
                    list.Add(new CallGraphNode(name ?? "", sig ?? "", file, line));
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

    /// <summary>get_call_graph(callers) 反向搜索的实时进度状态，供 ToolExecutor 的
    /// KeepAliveNotifier 周期读取生成进度文本。由 ToolExecutor 创建，<see cref="VcCallHierarchySearcher.QueryCoreAsync"/>
    /// 在 defId 循环中更新；跨 UI 线程写 + 保活 timer 线程读，故计数字段用 Volatile。
    /// 进度文本仅给客户端重置超时 + 观察心跳用，容忍轻微竞态。</summary>
    public sealed class CallGraphProgress
    {
        private DateTime _startedAtUtc;
        private int _totalDefIds;
        private int _currentIndex;
        private int _foundCount;

        /// <summary>搜索正式开始（FindDefinitionIds 完成、已知 defId 总数后调用）。UI 线程。</summary>
        public void Begin(DateTime startedAtUtc, int totalDefIds)
        {
            _startedAtUtc = startedAtUtc;
            _totalDefIds = totalDefIds;
            Volatile.Write(ref _currentIndex, 0);
            Volatile.Write(ref _foundCount, 0);
        }

        /// <summary>每完成一个 defId 的搜索后更新进度（UI 线程调）。</summary>
        public void Update(int currentIndex, int foundCount)
        {
            Volatile.Write(ref _currentIndex, currentIndex);
            Volatile.Write(ref _foundCount, foundCount);
        }

        /// <summary>生成进度文本（保活 timer 线程调）。Begin 前显示「定位函数定义中」，
        /// 之后形如「搜索调用方… 3/8，已找到 12 个，用时 45s」。</summary>
        public string BuildStatus()
        {
            if (_startedAtUtc == default)
                return "搜索调用方… 正在定位函数定义…";
            int idx = Volatile.Read(ref _currentIndex);
            int found = Volatile.Read(ref _foundCount);
            int elapsed = (int)(DateTime.UtcNow - _startedAtUtc).TotalSeconds;
            string head = _totalDefIds > 0
                ? $"搜索调用方… {idx}/{_totalDefIds}"
                : $"搜索调用方… {idx}";
            return $"{head}，已找到 {found} 个，用时 {Math.Max(0, elapsed)}s";
        }
    }

    /// <param name="Found">false：CallHierarchy 不可用 / 函数未找到。</param>
    /// <param name="TimedOut">true：搜索超时（Nodes 仍返回已收集的部分结果）。</param>
    public sealed record CallGraphResult(
        bool Found, string Query, string Direction,
        List<CallGraphNode> Nodes, bool TimedOut, string? Error);
}
