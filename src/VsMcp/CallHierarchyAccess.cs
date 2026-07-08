using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio.CodeStore.Internal;
using Microsoft.VisualStudio.CppSvc.Internal;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VC;

namespace VsMcp
{
    // ======================================================================
    // 两版 IVCCallHierarchyMemberItem 手写 ComImport 接口的 vtable 槽位对照表
    // （反编译 VS2022 / VS2026 的 Microsoft.VisualStudio.CppSvc.Internal.dll 精确确认）。
    // 同一 GUID（B18B081C-BB4F-44DE-8315-5A1828163F9B）、同一命名空间
    // (Microsoft.VisualStudio.CppSvc.Internal)，但两版方法数不同、参数不同、
    // 槽位布局不同。CLR 按 C# 声明顺序生成 vtable stub（IUnknown 占槽 0-2），
    // 故下面每个方法的声明顺序 = vtable 槽号，顺序错就会调错原生方法 / 崩 VS。
    //
    // 槽位    Dev17 (VS2022)                    Dev18 (VS2026)
    // ------ ---------------------------------- ----------------------------------
    //  3     GetData(7 out)                     GetData(7 out)               ← 两版同签名
    //  4     GetLocation(file + 4 int)          GetLocation(file + VCTextSpan)
    //  5     FormatSearchCategoryName           FormatSearchCategoryName     ← 同
    //  6     StartSearch(5 参, 无 pCancel)       StartSearch(6 参, 末参 pCancel)
    //  7     SuspendSearch                      StartSearchInFile(6 参)       ← VS2026 新增
    //  8     ResumeSearch                       SuspendSearch
    //  9     CancelSearch                       ResumeSearch
    // 10     ——（Dev17 无此槽）                   CancelSearch
    //
    // 关键差异：
    // - Dev17 无 StartSearchInFile，CancelSearch 落槽 9。
    // - Dev18 有 StartSearchInFile 占槽 7，把 Suspend/Resume/Cancel 各后移一格，
    //   CancelSearch 落槽 10。Dev18 接口若漏掉 StartSearchInFile，CancelSearch 会
    //   错落到槽 9（调到原生 ResumeSearch），搜索无法取消。
    // - Dev17 StartSearch 5 参（无 pCancel，签名里就不存在，不是传 null）。
    // - Dev18 StartSearch 6 参，末参 pCancel 恒传 null（传自己的 IVCCancellationToken
    //   会让原生搜索卡在 ~97% 不投递结果，已验证 1.51）。
    // - Dev18 GetLocation 行号取 pTextSpan.iStartLine；Dev17 GetLocation 行号取
    //   pStartLine（独立 int out）。
    // ======================================================================

    /// <summary>
    /// VS2022 (Dev17) 原生 IVCCallHierarchyMemberItem 的手写 ComImport 投影。
    /// GUID 与原生接口同为 B18B081C-BB4F-44DE-8315-5A1828163F9B；方法顺序严格按
    /// 反编译结论（槽 3-9，无 StartSearchInFile，CancelSearch 在槽 9）。
    /// 同一 GUID，对原生 RCW 做 as 转型即可拿到这个投影，不需要 QueryInterface。
    /// </summary>
    [ComImport]
    [Guid("B18B081C-BB4F-44DE-8315-5A1828163F9B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVCCallHierarchyMemberItemDev17
    {
        // 槽3：GetData —— 7 个 out（两版同签名， GetData 实际调用走互操作接口，此处仅占槽）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetData(
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrName,
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrNameAndParameters,
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrContainingTypeName,
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrSortText,
            [Out] out VCCallHierarchySearchCategory pSupportedSearchCategories,
            [Out] out VCImageMoniker pImageMoniker,
            [Out] out IVCCallHierarchyItemDetails[] ppItemDetails);

        // 槽4：GetLocation —— file + 4 个 int（startLine/startCol/endLine/endCol）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetLocation(
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrFile,
            [Out] out int pStartLine,
            [Out] out int pStartColumn,
            [Out] out int pEndLine,
            [Out] out int pEndColumn);

        // 槽5：FormatSearchCategoryName。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string FormatSearchCategoryName(
            VCCallHierarchySearchCategory category,
            [MarshalAs(UnmanagedType.LPWStr)] string bstrName);

        // 槽6：StartSearch —— 5 参，无 pCancel（Dev17 原生就没这个参数）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void StartSearch(
            VCCallHierarchySearchCategory category,
            VCCallHierarchySearchScope scope,
            VCCallHierarchySearchOptions options,
            VCCallHierarchySearchReason reason,
            IVCCallHierarchySearchCallback callback);

        // 槽7：SuspendSearch。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SuspendSearch(VCCallHierarchySearchCategory category);

        // 槽8：ResumeSearch。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ResumeSearch(VCCallHierarchySearchCategory category);

        // 槽9：CancelSearch（Dev17 终止槽）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void CancelSearch(VCCallHierarchySearchCategory category);
    }

    /// <summary>
    /// VS2026 (Dev18) 原生 IVCCallHierarchyMemberItem 的手写 ComImport 投影。
    /// 同 GUID；方法顺序严格按反编译结论（槽 3-10，含 StartSearchInFile 占槽 7，
    /// CancelSearch 在槽 10）。漏掉 StartSearchInFile 会让 CancelSearch 错落槽 9。
    /// </summary>
    [ComImport]
    [Guid("B18B081C-BB4F-44DE-8315-5A1828163F9B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVCCallHierarchyMemberItemDev18
    {
        // 槽3：GetData —— 7 个 out（同 Dev17）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetData(
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrName,
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrNameAndParameters,
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrContainingTypeName,
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrSortText,
            [Out] out VCCallHierarchySearchCategory pSupportedSearchCategories,
            [Out] out VCImageMoniker pImageMoniker,
            [Out] out IVCCallHierarchyItemDetails[] ppItemDetails);

        // 槽4：GetLocation —— file + VCTextSpan（行号取 pTextSpan.iStartLine）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetLocation(
            [Out] [MarshalAs(UnmanagedType.BStr)] out string pbstrFile,
            [Out] out VCTextSpan pTextSpan);

        // 槽5：FormatSearchCategoryName（同 Dev17）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string FormatSearchCategoryName(
            VCCallHierarchySearchCategory category,
            [MarshalAs(UnmanagedType.LPWStr)] string bstrName);

        // 槽6：StartSearch —— 6 参，末参 pCancel（恒传 null，见 Dev18Access 注释）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void StartSearch(
            VCCallHierarchySearchCategory category,
            VCCallHierarchySearchScope scope,
            VCCallHierarchySearchOptions options,
            VCCallHierarchySearchReason reason,
            IVCCallHierarchySearchCallback callback,
            IVCCancellationToken pCancel);

        // 槽7：StartSearchInFile —— VS2026 新增，必须声明以让后续方法落到正确槽位。
        // 本路径 get_call_graph 不调用它，仅占槽。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void StartSearchInFile(
            VCCallHierarchySearchCategory category,
            IdFile idFile,
            VCCallHierarchySearchOptions options,
            VCCallHierarchySearchReason reason,
            IVCCallHierarchySearchCallback callback,
            IVCCancellationToken pCancel);

        // 槽8：SuspendSearch。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SuspendSearch(VCCallHierarchySearchCategory category);

        // 槽9：ResumeSearch。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ResumeSearch(VCCallHierarchySearchCategory category);

        // 槽10：CancelSearch（Dev18 终止槽；StartSearchInFile 占槽 7 让它落这里）。
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void CancelSearch(VCCallHierarchySearchCategory category);
    }

    /// <summary>
    /// CallHierarchy member 的版本无关包装。持有原始 COM member（object，不绑任一
    /// 手写接口类型），暴露统一的 StartSearch / GetLocation / CancelSearch，由
    /// <see cref="CallHierarchyDev17Access"/> / <see cref="CallHierarchyDev18Access"/>
    /// 两实现按 VS 主版本分流转调对应手写接口。调用方拿包装后只调版本无关方法，
    /// 不再直调 member.StartSearch / member.GetLocation / member.CancelSearch
    /// （互操作 IVCCallHierarchyMemberItem 是 Dev18 形状，VS2022 上槽位/签名错）。
    /// GetData 两版同签名同槽3，仍由调用方直调互操作接口，不经本包装。
    /// </summary>
    internal abstract class CallHierarchyMemberAccess
    {
        /// <summary>原始 COM member RCW（object 形式持有，不绑具体接口类型）。</summary>
        protected readonly object _member;

        protected CallHierarchyMemberAccess(object member)
        {
            _member = member ?? throw new ArgumentNullException(nameof(member));
        }

        /// <summary>启动调用关系搜索。版本无关 5 参签名；pCancel 由实现内部处理
        /// （Dev17 签名里无此参；Dev18 内部传 null）。</summary>
        public abstract void StartSearch(
            VCCallHierarchySearchCategory category,
            VCCallHierarchySearchScope scope,
            VCCallHierarchySearchOptions options,
            VCCallHierarchySearchReason reason,
            IVCCallHierarchySearchCallback callback);

        /// <summary>取 member 的源位置（file + 起始行号）。
        /// Dev18 取 VCTextSpan.iStartLine；Dev17 取独立 pStartLine。</summary>
        public abstract (string File, int Line) GetLocation();

        /// <summary>取消指定 category 的搜索。</summary>
        public abstract void CancelSearch(VCCallHierarchySearchCategory category);

        /// <summary>按 VS 主版本选包装实现：devMajor &gt;= 18 → Dev18，否则 Dev17（含 0 兜底）。</summary>
        public static CallHierarchyMemberAccess Create(object member, int devMajor)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            if (devMajor >= 18) return new CallHierarchyDev18Access(member);
            return new CallHierarchyDev17Access(member);
        }

        /// <summary>探测当前 VS 主版本号（DTE.Version 主版本）。
        /// 17 = VS2022 / Dev17；18 = VS2026 / Dev18；0 = 未知（按 Dev17 兜底）。
        /// 必须在 UI 线程调（GetGlobalService 要求 UI 线程）。</summary>
        internal static int DetectDevMajor(AsyncPackage package)
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null) return 0;
                var version = dte.Version ?? "";
                if (version.Length == 0) return 0;
                var majorText = version.Split('.')[0];
                return int.TryParse(majorText, out var major) ? major : 0;
            }
            catch
            {
                // 任何 COM / DTE 抖动 → 0（按 Dev17 兜底，调用方不会崩）。
                return 0;
            }
        }
    }

    /// <summary>VS2022 (Dev17) 包装实现：转调 <see cref="IVCCallHierarchyMemberItemDev17"/>。
    /// StartSearch 走 5 参（无 pCancel），GetLocation 走 file + 4 int 取 pStartLine。</summary>
    internal sealed class CallHierarchyDev17Access : CallHierarchyMemberAccess
    {
        private readonly IVCCallHierarchyMemberItemDev17 _item;

        internal CallHierarchyDev17Access(object member) : base(member)
        {
            // 同 GUID，as 转型即拿到 Dev17 投影；转不过来说明 member 不是这个 COM 接口。
            _item = member as IVCCallHierarchyMemberItemDev17
                ?? throw new InvalidCastException("原生 member 不支持 Dev17 (VS2022) CallHierarchy 接口");
        }

        public override void StartSearch(
            VCCallHierarchySearchCategory category,
            VCCallHierarchySearchScope scope,
            VCCallHierarchySearchOptions options,
            VCCallHierarchySearchReason reason,
            IVCCallHierarchySearchCallback callback)
        {
            // Dev17 原生 StartSearch 为 5 参，签名里就没有 pCancel（不是传 null）。
            _item.StartSearch(category, scope, options, reason, callback);
        }

        public override (string File, int Line) GetLocation()
        {
            _item.GetLocation(out var file, out var startLine, out var _, out var _, out var _);
            return (file ?? "", startLine);
        }

        public override void CancelSearch(VCCallHierarchySearchCategory category)
        {
            _item.CancelSearch(category);
        }
    }

    /// <summary>VS2026 (Dev18) 包装实现：转调 <see cref="IVCCallHierarchyMemberItemDev18"/>。
    /// StartSearch 走 6 参（pCancel 恒传 null），GetLocation 走 file + VCTextSpan 取 iStartLine。
    /// 此实现即 1.51 既有逻辑搬迁，VS2026 行为零变化。</summary>
    internal sealed class CallHierarchyDev18Access : CallHierarchyMemberAccess
    {
        private readonly IVCCallHierarchyMemberItemDev18 _item;

        internal CallHierarchyDev18Access(object member) : base(member)
        {
            _item = member as IVCCallHierarchyMemberItemDev18
                ?? throw new InvalidCastException("原生 member 不支持 Dev18 (VS2026) CallHierarchy 接口");
        }

        public override void StartSearch(
            VCCallHierarchySearchCategory category,
            VCCallHierarchySearchScope scope,
            VCCallHierarchySearchOptions options,
            VCCallHierarchySearchReason reason,
            IVCCallHierarchySearchCallback callback)
        {
            // 第 6 参 pCancel 恒传 null：传自己的 IVCCancellationToken 实现会让原生搜索卡在
            // ~97% 进度不投递结果（cancel 实现不符合原生预期，已验证 1.51）。传 null 能取得
            // 符合预期的结果，取消改用 CancelSearch（由调用方在 linkedCts 触发）。
            _item.StartSearch(category, scope, options, reason, callback, null);
        }

        public override (string File, int Line) GetLocation()
        {
            _item.GetLocation(out var file, out var span);
            return (file ?? "", span.iStartLine);
        }

        public override void CancelSearch(VCCallHierarchySearchCategory category)
        {
            _item.CancelSearch(category);
        }
    }
}
