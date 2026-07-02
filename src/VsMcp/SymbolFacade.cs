using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using EnvDTE80;

namespace VsMcp
{
    /// <summary>
    /// Facade over the VS Object Manager symbol-browsing surface. Enumerates
    /// every registered <see cref="IVsLibrary2"/> (C#, VB, C++ each register
    /// their own) so <c>find_symbol</c> is language-agnostic. Source location
    /// (file + line) is obtained via <c>IVsSimpleObjectList2.GetSourceContextWithOwnership</c>
    /// when the owning library implements it; column is never available — VS
    /// libraries return file+line only.
    /// </summary>
    public sealed class SymbolFacade : IDisposable
    {
        private const int DefaultMaxResults = 200;

        private readonly AsyncPackage _package;
        private bool _disposed;

        public SymbolFacade(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        /// <summary>
        /// Acquires <see cref="IVsObjectManager2"/> from <c>SVsObjectManager</c>.
        /// Re-acquired per call rather than cached: it is a global VS service
        /// backed by a stable RCW, but keeping the pattern uniform with
        /// <see cref="DebuggerFacade"/> and avoiding any cross-call lifetime
        /// assumption.
        /// </summary>
        private async Task<IVsObjectManager2> GetObjectManagerAsync(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            return (IVsObjectManager2?)await _package.GetServiceAsync(typeof(SVsObjectManager))
                ?? throw new InvalidOperationException("IVsObjectManager2 service is not available");
        }

        private DTE2? _dte;

        /// <summary>
        /// Acquires the DTE root. Cached: unlike the debugger RCW, the DTE
        /// object is stable across debug sessions.
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
        /// Searches every registered language library for symbols whose name
        /// contains <paramref name="query"/> (case-insensitive substring).
        /// Returns matches with source file + line when the library provides
        /// them. No column information is available from the VS Object Manager.
        /// </summary>
        public async Task<SymbolSearchResult> FindSymbolAsync(string query, int maxResults, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(query))
                return new SymbolSearchResult(new List<SymbolMatch>(), Total: 0, Query: query ?? "", Truncated: false);

            int limit = maxResults > 0 ? maxResults : DefaultMaxResults;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var objManager = await GetObjectManagerAsync(ct);

            var results = new List<SymbolMatch>();
            bool truncated = false;

            // Enumerate ALL registered libraries so the search is language-
            // agnostic: C#, VB, and C++ each register their own library with
            // the Object Manager. Restricting to a single language GUID would
            // miss C++ (the project's core use case).
            if (objManager.EnumLibraries(out IVsEnumLibraries2 enumLibraries) != VSConstants.S_OK || enumLibraries == null)
                return new SymbolSearchResult(results, Total: 0, Query: query, Truncated: false);

            var libraries = new List<IVsLibrary2>();
            var buffer = new IVsLibrary2[1];
            enumLibraries.Reset();
            while (enumLibraries.Next(1, buffer, out uint fetched) == VSConstants.S_OK && fetched > 0)
            {
                if (buffer[0] != null) libraries.Add(buffer[0]);
            }

            var criteria = new VSOBSEARCHCRITERIA2[]
            {
                new VSOBSEARCHCRITERIA2
                {
                    szName = query,
                    // grfOptions = 0: no case-sensitivity, no filtering — broadest match.
                    // eSrchType = SO_SUBSTRING: matches query anywhere in the name.
                    grfOptions = 0,
                    eSrchType = VSOBSEARCHTYPE.SO_SUBSTRING,
                    dwCustom = 0,
                    pIVsNavInfo = null,
                }
            };

            foreach (var library in libraries)
            {
                if (results.Count >= limit) { truncated = true; break; }

                string libraryId = GetLibraryGuid(library);

                // Query both class and member lists; a library may populate
                // either, both, or neither. LLF_USESEARCHFILTER is required so
                // the library honors the VSOBSEARCHCRITERIA2 name filter.
                CollectFromLibrary(library, libraryId, (uint)_LIB_LISTTYPE.LLT_CLASSES, "class", criteria, results, limit, ref truncated);
                if (results.Count >= limit) { truncated = true; break; }
                CollectFromLibrary(library, libraryId, (uint)_LIB_LISTTYPE.LLT_MEMBERS, "member", criteria, results, limit, ref truncated);
            }

            return new SymbolSearchResult(results, Total: results.Count, Query: query, Truncated: truncated);
        }

        /// <summary>
        /// Resolves a type's position in the solution's type graph: full ancestor
        /// chain (recursive Bases to root), direct descendants (subtypes/
        /// implementations across the whole solution via
        /// <c>CodeType.DerivedTypes</c>), and siblings (other types sharing a
        /// base). This is the VS-exclusive view reading source cannot reproduce
        /// — reverse inheritance requires indexing every type in the solution,
        /// which the language services do but grepping source cannot.
        /// </summary>
        public async Task<TypeHierarchyResult> GetTypeHierarchyAsync(string typeName, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(typeName))
                return NotFound(typeName);

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

                // Definition file(s) for this project's view of the type.
                try { var f = type.ProjectItem?.FileNames[1]; if (!string.IsNullOrEmpty(f)) defFiles.Add(f); } catch { }

                // Ancestors: walk Bases recursively up to the root.
                try { CollectAncestors(type, ancestors, ancestorSeen, selfName); } catch { }

                // Descendants: who derives from / implements this type in this project.
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

                // Siblings: descendants of each Base, excluding self.
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
                // IVsLibrary2.GetGuid returns the library GUID via an IntPtr
                // pointing to an unmanaged GUID struct (16 bytes). Marshal it
                // back to a managed Guid.
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

        /// <summary>
        /// Runs GetList2 on one library for one list type and appends hits.
        /// Every COM call is individually guarded: a single uncooperative
        /// library (E_NOTIMPL, COM fault) must never abort the whole search.
        /// </summary>
        private static void CollectFromLibrary(
            IVsLibrary2 library,
            string? libraryId,
            uint listType,
            string kind,
            VSOBSEARCHCRITERIA2[] criteria,
            List<SymbolMatch> results,
            int limit,
            ref bool truncated)
        {
            IVsObjectList2 list;
            try
            {
                if (library.GetList2(listType, (uint)_LIB_LISTFLAGS.LLF_USESEARCHFILTER, criteria, out list) != VSConstants.S_OK)
                    return;
            }
            catch
            {
                return;
            }
            if (list == null) return;

            try
            {
                if (list.GetItemCount(out uint count) != VSConstants.S_OK) return;

                // IVsSimpleObjectList2 is the managed-friendly variant that
                // returns source location as (string, uint) without CoTaskMem
                // plumbing. Most real libraries (Roslyn, VC++) implement both
                // IVsObjectList2 and IVsSimpleObjectList2 on the same object.
                var simpleList = list as IVsSimpleObjectList2;

                for (uint i = 0; i < count; i++)
                {
                    if (results.Count >= limit) { truncated = true; return; }

                    string fullName = GetPropertyString(list, i, (int)_VSOBJLISTELEMPROPID.VSOBJLISTELEMPROPID_FULLNAME);
                    string leafName = GetPropertyString(list, i, (int)_VSOBJLISTELEMPROPID.VSOBJLISTELEMPROPID_LEAFNAME);

                    string? filePath = null;
                    int line = -1;
                    if (simpleList != null)
                    {
                        try
                        {
                            if (simpleList.GetSourceContextWithOwnership(i, out string file, out uint lineNum) == VSConstants.S_OK)
                            {
                                filePath = string.IsNullOrEmpty(file) ? null : file;
                                line = (int)lineNum;
                            }
                        }
                        catch
                        {
                            // Library doesn't implement source context — symbol
                            // is still reported by name without a location.
                        }
                    }

                    if (string.IsNullOrEmpty(fullName) && string.IsNullOrEmpty(leafName))
                        continue;

                    results.Add(new SymbolMatch(
                        Name: !string.IsNullOrEmpty(fullName) ? fullName! : leafName!,
                        LeafName: !string.IsNullOrEmpty(leafName) ? leafName : null,
                        FilePath: filePath,
                        Line: line,
                        Kind: kind,
                        Library: libraryId));
                }
            }
            catch
            {
                // Enumeration over this list failed — results gathered so far
                // from this library are kept; move on to the next library.
            }
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
        /// Drives VS's Edit.GoToDefinition command at the given source position
        /// and returns where the language service jumped to. Has editor side
        /// effects (opens the definition file, moves the cursor) — gated behind
        /// the EnableGoToDefinition option (default off) so the agent never
        /// sees this tool unless the user opted in.
        /// </summary>
        public async Task<GoToDefinitionResult> GoToDefinitionAsync(string file, int line, int column, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file) || line < 1 || column < 1)
                throw new ArgumentException("file, line (1-based), and column (1-based) are all required");

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);

            // Open the target file and position the cursor at the query location.
            dte.ItemOperations.OpenFile(file, EnvDTE.Constants.vsViewKindTextView);
            var doc = dte.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Failed to open " + file);
            var sel = (EnvDTE.TextSelection)doc.Selection;
            sel.MoveToLineAndOffset(line, column);

            // Hand off to the language service. If it resolves a definition it
            // jumps the cursor (and may switch ActiveDocument); if not, the
            // cursor stays put — detected as Found=false.
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
