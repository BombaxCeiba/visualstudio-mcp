using System.Collections.Generic;

namespace VsMcp
{
    // ===========================================================================
    // DTO。与 DebuggerDtos 同序列化契约：sealed records，经
    // McpJsonUtilities.DefaultOptions（camelCase）序列化。
    // ===========================================================================

    // ----- find_symbol -----

    /// <summary>
    /// <c>find_symbol</c> 的单个符号命中，经 NavigateTo（VS 的"转到所有" /
    /// Ctrl+T 后端）解析。FilePath/Line/Column 取自符号的 LSP location
    /// （1-based）；当提供程序未上报 Column 时为 -1。Kind 是 NavigateTo kind
    /// （Class/Method/Field/...），Language 是符号的语言
    /// （C++/CSharp/...），Container 是可获取时的外层类型/命名空间。
    /// </summary>
    public sealed record SymbolMatch(
        string Name,
        string? FilePath,
        int Line,
        int Column,
        string Kind,
        string Language,
        string? Container);

    /// <summary><c>find_symbol</c> 的结果。</summary>
    public sealed record SymbolSearchResult(
        IReadOnlyList<SymbolMatch> Symbols,
        int Total,
        string Query,
        bool Truncated);

    // ----- get_type_hierarchy -----

    public sealed record TypeRelativeNode(string Name, string Kind, string? File, int Line);

    /// <summary>
    /// <c>get_type_hierarchy</c> 的结果。类型在类型图中的位置：
    /// <see cref="Ancestors"/>（经递归 Bases 到根的完整继承链）、
    /// <see cref="Descendants"/>（经 CodeType.DerivedTypes 跨整个解决方案的直接
    /// 子类型/实现）、以及 <see cref="Siblings"/>（共享某个基类的其他类型）。
    /// 这是读源文件无法复现的 VS 独有能力——反向继承（"谁派生自该类型"）
    /// 需要索引整个解决方案，VS 语言服务做到了，但 grep 源代码做不到。
    /// </summary>
    public sealed record TypeHierarchyResult(
        bool Found,
        string Query,
        string? Name,
        string? Kind,
        string? Language,
        IReadOnlyList<string> DefinitionFiles,
        IReadOnlyList<TypeRelativeNode> Ancestors,
        IReadOnlyList<TypeRelativeNode> Descendants,
        IReadOnlyList<TypeRelativeNode> Siblings);

    // ----- go_to_definition -----

    /// <summary><c>go_to_definition</c> 的结果。当语言服务无法在给定位置
    /// 解析出定义时（如光标在关键字或未解析的 token 上）Found=false。</summary>
    public sealed record GoToDefinitionResult(bool Found, string? File, int Line, int Column);

    // ----- get_build_output -----

    /// <summary><c>get_build_output</c> 的结果。按 maxLines/tail 对 VS
    /// Output 窗口 Build 面板的切片视图。</summary>
    public sealed record BuildOutputResult(
        string PaneName,
        string Output,
        int TotalLines,
        int ReturnedLines,
        bool Truncated);

    /// <summary>自给定行偏移起 Build 面板的增量视图。供 build 保活循环经
    /// logging 通知把新的 build-log 行转发给客户端（不把它们暴露给模型）。
    /// <c>TotalLines</c> 是下次读取的偏移；<c>NewLines</c> 是自
    /// <c>fromLine</c> 起新增的行。</summary>
    public sealed record BuildOutputDeltaResult(int TotalLines, IReadOnlyList<string> NewLines);
}
