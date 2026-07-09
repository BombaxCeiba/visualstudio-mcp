namespace VsMcp
{
    // ===========================================================================
    // VC / LSP 符号种类（kind）的统一友好化归一。三路 kind 来源、三套原始枚举，
    // ToString() 出来形态各异、非人话，统一成全小写友好标签（class/method/field/...），
    // 供显示（header 的 [kind] 一眼可读，非 ikMethod/CIK_MemberFunc）与过滤：
    //
    // 1. find_symbol 的 C++ 命中：VCItemKind（ik* 前缀，ikClass/ikMethod/ikEnumItem...，
    //    IVCSearchResults.GetNextResult2 的 out pik，CppSvc.Internal.dll）。完整 15 值：
    //    ikUnknown/ikOther/ikClass/ikStruct/ikInterface/ikDelegate/ikEnum/ikModule/ikConst/
    //    ikEnumItem/ikField/ikMethod/ikProperty/ikEvent/ikNamespace。
    // 2. get_type_hierarchy 的 C++ 节点：CodeItemKind（CIK_* 前缀，CIK_Class/CIK_MemberFunc/
    //    CIK_BaseClass/CIK_Template/CIK_Macro...，CodeStore.Internal.dll）。60+ 值。
    // 3. find_symbol 的 C#/VB 命中：LSP SymbolKind（PascalCase，Class/Method/Constructor/
    //    Operator/TypeParameter/EnumMember...）。
    //
    // 归一策略：小写化 + 去 ik / CIK_ 前缀，再按特异性从高到低 Contains 关键词匹配。
    // 顺序很关键——子串关系（baseclass 含 class、constructor 含 const、enumitem 含 enum、
    // templateparameter 含 template）必须先匹配更特异的，否则误归。未命中 ProbeLog 原始名
    // （便于发现新枚举值补映射），并返回去前缀的小写串兜底（可读）。
    //
    // find_symbol 的 kind= 过滤是封闭集合 = 本函数对 VCItemKind ∪ LSP SymbolKind 的输出域，
    // 不含 template/macro/typedef/union/base/concept（这些只在 CodeItemKind 出现，仅
    // get_type_hierarchy 显示）。故 find_symbol 传 kind="template" 匹配空——工具 description
    // 已明示，避免 LLM 猜测 kind 值。
    // ===========================================================================
    internal static class VcKindLabels
    {
        /// <summary>把任意原始 kind 串（ik*/CIK_*/PascalCase）归一到全小写友好标签。
        /// 未识别返回去前缀的小写串兜底，并 ProbeLog 原始名（便于后续补映射）。</summary>
        internal static string ToFriendly(string? raw)
        {
            if (raw is null || raw.Length == 0) return "other";
            string s = raw.ToLowerInvariant();

            // 去前缀：CodeItemKind 的 CIK_（4 字符，须先判，否则被 ik 抢），再 VCItemKind 的 ik（2 字符）；LSP 无前缀。
            if (s.StartsWith("cik_")) s = s.Substring(4);
            else if (s.StartsWith("ik")) s = s.Substring(2);

            // 关键词归一，特异性从高到低（子串陷阱靠顺序解决）。
            if (s.Contains("enummember") || s.Contains("enumitem")) return "enumitem";   // CIK_EnumMember / ikEnumItem（须先于 enum）
            if (s.Contains("memberfunc")) return "method";                               // CIK_MemberFunc（成员函数）
            if (s.Contains("membervar")) return "field";                                 // CIK_MemberVar（成员变量）
            if (s.Contains("baseclass")) return "base";                                  // CIK_BaseClass（继承关系项，须先于 class）
            if (s.Contains("constructor")) return "constructor";                         // LSP Constructor（须先于 const）
            if (s.Contains("destructor")) return "destructor";
            if (s.Contains("operator")) return "operator";                               // LSP Operator
            if (s.Contains("typeparameter") || s.Contains("templateparameter") ||
                s.Contains("genericconstraint")) return "typeparameter";                 // LSP TypeParameter / CIK_*Parameter（须先于 template）
            if (s.Contains("concept")) return "concept";                                 // CIK_Concept（C++20 concept）
            if (s.Contains("namespace")) return "namespace";
            if (s.Contains("typedef")) return "typedef";
            if (s.Contains("macro")) return "macro";                                     // CIK_Macro
            if (s.Contains("template")) return "template";                               // CIK_Template（*parameter 已上面处理）
            if (s.Contains("enum")) return "enum";                                       // ikEnum / LSP Enum（enumitem/member 已上面处理）
            if (s.Contains("property")) return "property";
            if (s.Contains("event")) return "event";
            if (s.Contains("delegate")) return "delegate";
            if (s.Contains("interface")) return "interface";
            if (s.Contains("struct")) return "struct";
            if (s.Contains("union")) return "union";
            if (s.Contains("class")) return "class";                                     // baseclass 已上面处理
            if (s.Contains("field")) return "field";
            if (s.Contains("variable")) return "variable";
            if (s.Contains("method")) return "method";                                   // memberfunc 已上面处理
            if (s.Contains("function")) return "function";                               // CIK_Function / LSP Function
            if (s.Contains("module")) return "module";
            if (s.Contains("const")) return "constant";                                  // ikConst / LSP Constant（constructor 已上面处理）
            if (s.Contains("base")) return "base";                                       // baseclass 已上面处理，此处兜底
            if (s.Contains("alias")) return "alias";                                     // CIK_UsingAlias / CIK_NamespaceAlias
            if (s.Contains("parameter")) return "parameter";                             // CIK_Parameter（type*parameter 已上面处理）
            if (s.Contains("unknown") || s.Contains("undef") || s.Contains("error") || s.Contains("other"))
                return "other";                                                          // ikUnknown/ikOther/CIK_Unknown/CIK_Error...

            // 未识别：记原始名便于发现新枚举值，返回去前缀小写串兜底（多数情况下本身就可读）。
            SymbolFacade.ProbeLog($"FIND unknownKind={raw}");
            return s;
        }
    }
}
