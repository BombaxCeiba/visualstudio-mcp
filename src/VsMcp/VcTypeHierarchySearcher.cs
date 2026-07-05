using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.CodeStore.Internal;
using Microsoft.VisualStudio.CppSvc.Internal;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace VsMcp
{
    /// <summary>
    /// 经 VC 原生 CodeStore 查询 C++ 类型层次（基类/派生/兄弟）。
    ///
    /// get_type_hierarchy 对 C++（尤其 Open Folder / CMake 场景）走这里：DTE CodeModel
    /// 在 Open Folder 下 <c>project.CodeModel = null</c> 完全不可用（与 find_symbol 同款根因），
    /// 只能下探 VC CodeStore 的 IVCQueryCodeStore。
    ///
    /// 查询语义（eval_csharp demo 验证）：
    /// - 祖先：<c>GetInheritanceCodeItems(IQT_Bases + idFilter=目标 id)</c>，递归到根。
    /// - 派生：<c>GetInheritanceCodeItems(IQT_Derivations + bstrQNameSuffix=目标完整限定名)</c>。
    ///   **必须用 bstrQNameSuffix（限定名），不能用 idFilter**——Derivations 模式下 idFilter
    ///   被忽略，会返回全部类型（demo 实测返回 11633 个无关类）。
    /// - 兄弟：每个直接 base 的 Derivations，排除自身。
    ///
    /// 线程模型：UI 线程取全局 VC 服务，后台线程（MTA）跑所有查询——
    /// CPPThreadSafeServiceClass 是 MTA 对象，UI 线程（STA）直接访问成员会 RPC_E_WRONG_THREAD。
    ///
    /// 这些是 VS Internal API（非公开 ABI，在 *.Internal 程序集 / PrivateAssemblies）——跨大版本
    /// 不保证稳定，靠"一个版本一个实现"分流；每个 COM 调用 try/catch 包好，接口消失时优雅降级
    /// 返回 null（调用方 fallback DTE）。
    ///
    /// COM 释放：qcs / 各 ICodeItemResults 是每次调用新建的 COM 对象（RCW）。CLR 只在 GC 回收
    /// RCW 时才 Release native 引用，时机不确定；codestore 关闭时要等所有 query session 引用归零，
    /// GC 没及时跑就死锁（关 VS hang）。故用 Com.Use 包 using 块确定性释放（见 <see cref="Com"/>）。
    /// mgr 是全局服务单例，不归我们释放。
    /// </summary>
    internal static class VcTypeHierarchySearcher
    {
        // CodeItemNameOption.CINO_FullyQualifiedName = 16（flags 枚举，反射拿的值）。
        private const CodeItemNameOption FullyQualified = (CodeItemNameOption)16;
        // 单类 relatives 防爆上限（Derivations 用 QNameSuffix 精确查询，量小，但留保险）。
        private const int MaxRelatives = 500;

        /// <summary>查 <paramref name="typeName"/> 的类型层次。
        /// 返回 null：VC CodeStore 不可用（调用方应 fallback 到 DTE CodeModel）。
        /// 返回 Found=false：CodeStore 可用但类型不存在（不应 fallback，VC 已给权威答案）。
        /// 返回 Found=true：类型层次。</summary>
        public static async Task<TypeHierarchyResult?> QueryAsync(AsyncPackage package, string typeName, CancellationToken ct)
        {
            // UI 线程取全局 VC 服务（COM 单例 CPPThreadSafeServiceClass 同时实现 IVCCodeStoreManager）。
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var svc = Package.GetGlobalService(typeof(CPPThreadSafeServiceClass));
            var mgr = svc as IVCCodeStoreManager;
            if (mgr == null) return null;
            // 不检查 IsCodeStoreReady——它是保守的"完全就绪"标志（大 solution 索引慢，标志延迟 set），
            // 但 CodeStore 数据通常已部分可用（find_symbol 同款 CodeStore 不检查它就能查到符号）。
            // 直接尝试查询，RequestQueryCodeStore==null 或类型不存在才 fallback DTE。

            // 后台线程（MTA）跑所有查询——避免 UI 线程 RPC_E_WRONG_THREAD。
            return await Task.Run(() => QueryCore(mgr, typeName), ct);
        }

        private static TypeHierarchyResult? QueryCore(IVCCodeStoreManager mgr, string typeName)
        {
            IVCQueryCodeStore qcs;
            try { qcs = mgr.RequestQueryCodeStore(); }
            catch { return null; }
            if (qcs == null) return null;

            // qcs 是每次新建的 query session（RCW）——方法级持有，方法结束（含任意 return）确定性 Release。
            using var qcsScope = Com.Use(qcs);

            // 找目标类型——只要 Class/Struct/Interface/Union/Enum，排除同名 MemberFunc/Function。
            var p = new CodeItemQueryParams { bstrName = typeName, fCaseInsensitive = true, cMaxResults = 50 };
            long targetId = 0;
            CodeItemRec targetRec = default;
            using (Com.Use(qcs.GetCodeItems(ref p), r => { r.Close(); }, out var results))
            {
                results.MoveStart();
                while (results.MoveNext())
                {
                    var r = new CodeItemRec();
                    results.GetData(ref r);
                    if (IsTypeKind(r.cik) && targetId == 0)
                    {
                        targetId = r.id.row;
                        targetRec = r;
                    }
                }
            }

            if (targetId == 0)
                return new TypeHierarchyResult(
                    Found: false, Query: typeName, Name: null, Kind: null, Language: null,
                    DefinitionFiles: new List<string>(),
                    Ancestors: new List<TypeRelativeNode>(),
                    Descendants: new List<TypeRelativeNode>(),
                    Siblings: new List<TypeRelativeNode>());

            string targetQual = FormatName(qcs, targetRec);

            // 祖先：递归 Bases（idFilter 在 Bases 模式下工作正常）。
            var ancestors = new List<TypeRelativeNode>();
            CollectBases(qcs, targetId, ancestors, new HashSet<long>());

            // 派生：Derivations(bstrQNameSuffix=限定名)。
            var descendants = QueryDerivations(qcs, targetQual);

            // 兄弟：每个直接 base 的 Derivations，排除自身。
            var siblings = new List<TypeRelativeNode>();
            var sibSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in GetDirectBases(qcs, targetId))
            {
                if (siblings.Count >= MaxRelatives) break;
                foreach (var d in QueryDerivations(qcs, FormatName(qcs, b)))
                {
                    if (d.Name == targetQual) continue;
                    if (sibSeen.Add(d.Name)) siblings.Add(d);
                    if (siblings.Count >= MaxRelatives) break;
                }
            }

            // 定义文件：目标 IdFile → FileRec.bstrName。
            var defFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var f = GetFilePath(qcs, targetRec.IdFile);
            if (!string.IsNullOrEmpty(f)) defFiles.Add(f);

            return new TypeHierarchyResult(
                Found: true,
                Query: typeName,
                Name: string.IsNullOrEmpty(targetQual) ? (targetRec.bstrName ?? typeName) : targetQual,
                Kind: MapKind(targetRec.cik),
                Language: "C++",
                DefinitionFiles: new List<string>(defFiles),
                Ancestors: ancestors,
                Descendants: descendants,
                Siblings: siblings);
        }

        /// <summary>递归收集祖先链（沿 Bases 向上到根）。visited 防环 + 去重。</summary>
        private static void CollectBases(IVCQueryCodeStore qcs, long id, List<TypeRelativeNode> result, HashSet<long> visited)
        {
            foreach (var b in GetDirectBases(qcs, id))
            {
                if (!visited.Add(b.id.row)) continue;
                result.Add(MakeNode(qcs, b));
                if (result.Count >= MaxRelatives) return;
                CollectBases(qcs, b.id.row, result, visited);
            }
        }

        /// <summary>直接基类列表（CodeItemRec，含 id 供递归）。</summary>
        private static List<CodeItemRec> GetDirectBases(IVCQueryCodeStore qcs, long id)
        {
            var list = new List<CodeItemRec>();
            var bp = new InheritanceQueryParams
            {
                iqt = InheritanceQueryType.IQT_Bases,
                idFilter = new IdItem { row = id },
                fUseIdFilter = true,
            };
            try
            {
                using (Com.Use(qcs.GetInheritanceCodeItems(ref bp), r => { r.Close(); }, out var bs))
                {
                    bs.MoveStart();
                    while (bs.MoveNext())
                    {
                        var r = new CodeItemRec();
                        bs.GetData(ref r);
                        list.Add(r);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>派生类列表（Derivations + bstrQNameSuffix=限定名）。</summary>
        private static List<TypeRelativeNode> QueryDerivations(IVCQueryCodeStore qcs, string qualName)
        {
            var list = new List<TypeRelativeNode>();
            if (string.IsNullOrEmpty(qualName)) return list;
            var dp = new InheritanceQueryParams
            {
                iqt = InheritanceQueryType.IQT_Derivations,
                bstrQNameSuffix = qualName,
            };
            try
            {
                using (Com.Use(qcs.GetInheritanceCodeItems(ref dp), r => { r.Close(); }, out var drs))
                {
                    drs.MoveStart();
                    while (drs.MoveNext() && list.Count < MaxRelatives)
                    {
                        var r = new CodeItemRec();
                        drs.GetData(ref r);
                        list.Add(MakeNode(qcs, r));
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>CodeItemRec → TypeRelativeNode。Name 用完整限定名，Line 经 GetFullCodeItem 拿。</summary>
        private static TypeRelativeNode MakeNode(IVCQueryCodeStore qcs, CodeItemRec r)
        {
            string name = FormatName(qcs, r);
            int line = -1;
            try
            {
                var fr = new FullCodeItemRec();
                qcs.GetFullCodeItem(r.id, ref fr);
                line = fr.ssItem.iStartLine;
            }
            catch { }
            return new TypeRelativeNode(
                Name: string.IsNullOrEmpty(name) ? (r.bstrName ?? "") : name,
                Kind: MapKind(r.cik),
                File: GetFilePath(qcs, r.IdFile),
                Line: line);
        }

        private static string FormatName(IVCQueryCodeStore qcs, CodeItemRec r)
        {
            try { return qcs.FormatCodeItemName(ref r, FullyQualified) ?? ""; }
            catch { return r.bstrName ?? ""; }
        }

        private static string GetFilePath(IVCQueryCodeStore qcs, IdFile idFile)
        {
            try
            {
                var fr = new FileRec();
                qcs.GetFileById(idFile, ref fr);
                return fr.bstrName ?? "";
            }
            catch { return ""; }
        }

        private static bool IsTypeKind(CodeItemKind k) =>
            k == CodeItemKind.CIK_Class || k == CodeItemKind.CIK_Struct ||
            k == CodeItemKind.CIK_Interface || k == CodeItemKind.CIK_Union ||
            k == CodeItemKind.CIK_Enum;

        private static string MapKind(CodeItemKind k) => k switch
        {
            CodeItemKind.CIK_Class => "class",
            CodeItemKind.CIK_Struct => "struct",
            CodeItemKind.CIK_Interface => "interface",
            CodeItemKind.CIK_Union => "union",
            CodeItemKind.CIK_Enum => "enum",
            CodeItemKind.CIK_BaseClass => "base",  // Bases 查询返回的继承关系项
            _ => k.ToString().Replace("CIK_", "").ToLowerInvariant(),
        };
    }
}
