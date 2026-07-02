using System.Collections.Generic;

namespace VsMcp
{
    // ===========================================================================
    // DTOs. Same serialization contract as DebuggerDtos: sealed records
    // serialized via McpJsonUtilities.DefaultOptions (camelCase).
    // ===========================================================================

    // ----- find_symbol -----

    /// <summary>
    /// A single symbol hit from <c>find_symbol</c>. FilePath/Line are populated
    /// when the owning language library implements
    /// <c>IVsSimpleObjectList2.GetSourceContextWithOwnership</c>; otherwise
    /// FilePath is null and Line is -1. Column is never available from the
    /// VS Object Manager.
    /// </summary>
    public sealed record SymbolMatch(
        string Name,
        string? LeafName,
        string? FilePath,
        int Line,
        string Kind,
        string? Library);

    /// <summary>Result of <c>find_symbol</c>.</summary>
    public sealed record SymbolSearchResult(
        IReadOnlyList<SymbolMatch> Symbols,
        int Total,
        string Query,
        bool Truncated);

    // ----- get_type_hierarchy -----

    public sealed record TypeRelativeNode(string Name, string Kind, string? File, int Line);

    /// <summary>
    /// Result of <c>get_type_hierarchy</c>. A type's position in the type graph:
    /// <see cref="Ancestors"/> (full inheritance chain to root via recursive
    /// Bases), <see cref="Descendants"/> (direct subtypes/implementations across
    /// the whole solution via CodeType.DerivedTypes), and <see cref="Siblings"/>
    /// (other types sharing a base). This is the VS-exclusive capability that
    /// reading source files cannot replicate — reverse inheritance ("who derives
    /// from this type") requires indexing the entire solution, which the VS
    /// language services do but grepping source cannot.
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

    /// <summary>Result of <c>go_to_definition</c>. Found=false when the
    /// language service could not resolve a definition at the given position
    /// (e.g. cursor on a keyword or unresolved token).</summary>
    public sealed record GoToDefinitionResult(bool Found, string? File, int Line, int Column);

    // ----- get_build_output -----

    /// <summary>Result of <c>get_build_output</c>. A sliced view of the VS
    /// Output window's Build pane per maxLines/tail.</summary>
    public sealed record BuildOutputResult(
        string PaneName,
        string Output,
        int TotalLines,
        int ReturnedLines,
        bool Truncated);

    /// <summary>Incremental view of the Build pane since a given line offset.
    /// Used by the build keep-alive loop to forward new build-log lines to the
    /// client via logging notifications (without exposing them to the model).
    /// <c>TotalLines</c> is the offset to read from next; <c>NewLines</c> are
    /// the lines appended since <c>fromLine</c>.</summary>
    public sealed record BuildOutputDeltaResult(int TotalLines, IReadOnlyList<string> NewLines);
}
