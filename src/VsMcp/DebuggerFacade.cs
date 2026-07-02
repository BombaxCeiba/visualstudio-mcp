using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using ModelContextProtocol;

namespace VsMcp
{
    /// <summary>
    /// Facade over the VS debugger COM surface. All 14 public methods return
    /// typed record DTOs (see <see cref="DebuggerDtos"/>) and throw typed
    /// exceptions (<see cref="RequireBreakModeException"/> /
    /// <see cref="BreakpointNotFoundException"/>) instead of building JSON
    /// strings. The hand-rolled JSON layer (the four helpers previously
    /// living in this file) has been removed; error routing is centralized
    /// in <see cref="SafeCall"/>.
    /// </summary>
    public sealed class DebuggerFacade : IDisposable
    {
        private const int MaxFrames = 50;
        private const int DefaultCharBudget = 1024;

        private readonly AsyncPackage _package;
        private DTE2? _dte;
        private bool _disposed;

        public DebuggerFacade(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        private async Task<DTE2> GetDteAsync(CancellationToken ct)
        {
            if (_dte != null) return _dte;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _dte = (DTE2)await _package.GetServiceAsync(typeof(DTE));
            return _dte;
        }

        /// <summary>
        /// Acquires <see cref="IVsDebugger2"/> from the <c>SVsShellDebugger</c>
        /// service. IVsDebugger2 is preferred over IVsDebugger (v1): the v1
        /// interface's OLE marshaler is unavailable off the STA thread, causing
        /// QI to fail with E_NOINTERFACE, whereas IVsDebugger2 (shipping since
        /// VS 2005) has robust marshaling and QI succeeds. Re-acquired on every
        /// call rather than cached: switching debug sessions replaces the
        /// underlying COM object.
        /// </summary>
        private async Task<IVsDebugger2> GetVsDebugger2Async(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            return (IVsDebugger2?)await _package.GetServiceAsync(typeof(SVsShellDebugger))
                ?? throw new InvalidOperationException("IVsDebugger2 service is not available");
        }

        public async Task<DebuggerStateResult> GetDebuggerStateAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var mode = await GetDebuggerModeAsync(ct);
            return new DebuggerStateResult(MapState(mode));
        }

        /// <summary>
        /// Surfaces the solution VS has loaded plus a best-effort debug target
        /// so the agent knows which project tree it is operating on before
        /// issuing breakpoint / inspection calls. Never throws on missing
        /// solution or debug target — returns null / empty lists instead.
        /// </summary>
        public async Task<SessionInfoResult> GetSessionInfoAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            string state = await StateFromModeAsync(ct);

            // Solution.FullName is "" when no solution is loaded.
            string solutionPath = dte.Solution?.FullName ?? "";
            string? resolvedSolutionPath = string.IsNullOrWhiteSpace(solutionPath) ? null : solutionPath;
            string? solutionDir = null;
            var projects = new List<string>();

            if (resolvedSolutionPath != null)
            {
                try { solutionDir = Path.GetDirectoryName(resolvedSolutionPath); }
                catch { solutionDir = null; }

                // dte.Solution.Projects is a COM collection; guard against
                // null/empty (no solution loaded) without throwing.
                var dteProjects = dte.Solution?.Projects;
                if (dteProjects != null)
                {
                    for (int i = 1; i <= dteProjects.Count; i++)
                    {
                        try
                        {
                            var project = dteProjects.Item(i);
                            // Prefer FullName (full project path); fall back to
                            // Name (unique project name) for solution-folder /
                            // virtual projects where FullName is empty.
                            string id = !string.IsNullOrWhiteSpace(project.FullName)
                                ? project.FullName
                                : (project.Name ?? "");
                            if (!string.IsNullOrEmpty(id))
                                projects.Add(id);
                        }
                        catch
                        {
                            // A single unloadable project must not abort the
                            // whole enumeration — skip it.
                        }
                    }
                }
            }

            // Best-effort debug target: CurrentProgram is only meaningful in
            // break/run mode; in design mode it is null. Any COM hiccups are
            // swallowed — the field is informational, not load-bearing.
            string? debugTarget = null;
            try
            {
                debugTarget = dte.Debugger?.CurrentProgram?.Name;
            }
            catch
            {
                debugTarget = null;
            }

            return new SessionInfoResult(
                SolutionPath: resolvedSolutionPath,
                SolutionDir: solutionDir,
                Projects: projects,
                DebugTarget: debugTarget,
                State: state);
        }

        /// <summary>
        /// Enumerates the projects in the currently loaded solution, recursing
        /// through Solution Folders. Each project surfaces its Name, UniqueName,
        /// FullName (the .csproj/.vcxproj path), Kind (the project-type GUID —
        /// lets the caller distinguish C++ vs C# etc.), whether it is the
        /// startup project, and a best-effort <c>OutputTarget</c> (the built
        /// executable path) so <c>start_debugging</c> has something to launch.
        /// OutputTarget resolution is wrapped per-project in try/catch — a
        /// single project whose properties cannot be read (C++/vcxproj,
        /// solution folders, unloaded) contributes a null and never aborts the
        /// whole enumeration.
        /// </summary>
        /// <param name="query">Optional case-insensitive substring filter
        /// applied to Name / UniqueName / FullName. Null or empty returns all
        /// projects.</param>
        public async Task<ProjectSearchResult> SearchProjectsAsync(string? query, CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            // No solution loaded → empty result (distinct from a solution with
            // zero projects, which is effectively impossible).
            var solution = dte.Solution;
            if (solution == null || string.IsNullOrWhiteSpace(solution.FullName))
                return new ProjectSearchResult(new List<ProjectInfo>(), Total: 0);

            // Snapshot the startup-project array once. SolutionBuild returns an
            // object (a SAFEARRAY of VARIANTs in COM terms); cast each element
            // to the project's UniqueName. Wrapped in try/catch because the
            // property can throw if the solution has not finished loading its
            // build state.
            var startupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (solution.SolutionBuild?.StartupProjects is object startArr)
                {
                    // The COM collection is IEnumerable; iterate it. Each entry
                    // is the project's UniqueName (a string), not a Project.
                    foreach (var entry in (System.Collections.IEnumerable)startArr)
                    {
                        if (entry is string s && !string.IsNullOrEmpty(s))
                            startupNames.Add(s);
                    }
                }
            }
            catch
            {
                // Startup-projects enumeration is best-effort; a transient COM
                // failure must not break the full project listing.
            }

            var results = new List<ProjectInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Recursive walk: Solution Folders (vsProjectKindSolutionFolder)
            // contain child Projects via ProjectItems.SubProject. Each SubProject
            // may itself be another folder, hence the recursion.
            void Visit(Project project)
            {
                try
                {
                    string? uniqueName = null;
                    try { uniqueName = project.UniqueName; } catch { /* some project kinds throw */ }
                    string? name = project.Name;
                    string? fullName = null;
                    try { fullName = project.FullName; } catch { /* FullName empty for unloaded/misc */ }

                    // Dedup by UniqueName when present (a project can appear
                    // under multiple parents in some solution shapes); fall
                    // back to FullName/name for virtual projects without one.
                    string dedupKey = !string.IsNullOrEmpty(uniqueName)
                        ? uniqueName!
                        : (!string.IsNullOrEmpty(fullName) ? fullName! : (name ?? Guid.NewGuid().ToString()));
                    if (!seen.Add(dedupKey))
                        return;

                    string? kind = null;
                    try { kind = project.Kind; } catch { /* leave null */ }

                    bool isStartup = uniqueName != null && startupNames.Contains(uniqueName);
                    string? outputTarget = ResolveOutputTargetInline(project);

                    results.Add(new ProjectInfo(
                        Name: name ?? "",
                        UniqueName: uniqueName ?? "",
                        FullName: string.IsNullOrEmpty(fullName) ? null : fullName,
                        Kind: kind,
                        IsStartupProject: isStartup,
                        OutputTarget: outputTarget));
                }
                catch
                {
                    // A single project whose properties cannot be enumerated is
                    // skipped — never aborts the whole walk.
                }

                // Recurse into Solution Folder children.
                try
                {
                    if (string.Equals(project.Kind, SolutionFolderKindGuid,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var items = project.ProjectItems;
                        if (items != null)
                        {
                            for (int i = 1; i <= items.Count; i++)
                            {
                                try
                                {
                                    var sub = items.Item(i).SubProject;
                                    if (sub != null) Visit(sub);
                                }
                                catch { /* skip unvisitable child */ }
                            }
                        }
                    }
                }
                catch { /* folder recursion failure is non-fatal */ }
            }

            try
            {
                var dteProjects = solution.Projects;
                if (dteProjects != null)
                {
                    for (int i = 1; i <= dteProjects.Count; i++)
                    {
                        try { Visit(dteProjects.Item(i)); }
                        catch { /* skip */ }
                    }
                }
            }
            catch
            {
                // Top-level enumeration failure — return whatever we collected.
            }

            // Apply the optional query substring filter (case-insensitive)
            // against Name / UniqueName / FullName.
            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query!.Trim();
                results.RemoveAll(p =>
                    !(ContainsCi(p.Name, q) || ContainsCi(p.UniqueName, q) || ContainsCi(p.FullName, q)));
            }

            return new ProjectSearchResult(results, Total: results.Count);
        }

        /// <summary>
        /// Best-effort resolution of a project's built executable path.
        /// Strategy: pull ConfigurationManager.ActiveConfiguration, then read
        /// OutputPath (a directory, possibly relative) and the project's
        /// OutputFileName (the binary name with extension). Combine relative
        /// to the project directory. This works for C#/VB/F# managed
        /// projects. For C++/vcxproj and other kinds these properties either
        /// don't exist or throw — caught here and returns null. The caller
        /// (search_project, already on the UI thread) treats null as "no
        /// resolvable target"; start_debugging takes an absolute exe path
        /// directly so a null here is never fatal.
        /// ThreadHelper.ThrowIfNotOnUIThread is the explicit main-thread
        /// assertion the VSTHRD010 analyzer recognizes for static helpers
        /// (the call is a no-op in practice — SearchProjectsAsync has already
        /// switched to the UI thread before recursing into projects).
        /// </summary>
        private static string? ResolveOutputTargetInline(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var cfgMgr = project.ConfigurationManager;
                var activeCfg = cfgMgr?.ActiveConfiguration;
                if (activeCfg == null) return null;

                var props = activeCfg.Properties;
                if (props == null) return null;

                // OutputPath is the output directory; may be relative
                // ("bin\Release\") or absolute. OutputFileName is the binary
                // name with extension ("MyApp.exe"). Both are 1-based COM
                // Property items accessed by name.
                string? outputPath = ReadProperty(props, "OutputPath");
                string? outputFileName = null;
                try { outputFileName = project.Properties?.Item("OutputFileName")?.Value as string; }
                catch { /* C++/others don't expose OutputFileName */ }

                if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(outputFileName))
                    return null;

                // Resolve the output directory relative to the project
                // directory (FullName is the .csproj path).
                string? projectDir = null;
                try { projectDir = Path.GetDirectoryName(project.FullName); } catch { }
                if (string.IsNullOrEmpty(projectDir)) return null;

                string combinedDir = Path.IsPathRooted(outputPath!)
                    ? outputPath!
                    : Path.GetFullPath(Path.Combine(projectDir!, outputPath!));

                string target = Path.Combine(combinedDir, outputFileName!);
                return Path.GetFullPath(target);
            }
            catch
            {
                // Any COM/EnvDTE hiccup → null. The whole point of this helper
                // is that it must never throw (search_project invariants).
                return null;
            }
        }

        /// <summary>
        /// Reads a named property from an EnvDTE Properties COM collection,
        /// returning null if the property does not exist or throws (some
        /// project kinds lack OutputPath entirely).
        /// </summary>
        private static string? ReadProperty(EnvDTE.Properties props, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var val = props.Item(name)?.Value;
                return val as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsCi(string? hay, string needle)
        {
            if (string.IsNullOrEmpty(hay)) return false;
            return hay!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // =====================================================================
        // start_debugging — launches an arbitrary exe through VS's native
        // IVsDebugger2.LaunchDebugTargets2 path. Does NOT touch project config,
        // launchSettings.json, or vcxproj files. C++ defaults to the native
        // engine; pass DebugEngine="managed" for CLR/.NET debugging, or a raw
        // GUID string for any other engine. All params are used in-memory for
        // this single launch call (nothing is persisted).
        // =====================================================================

        private static readonly Guid NativeDebugEngineGuid = VSConstants.DebugEnginesGuids.NativeOnly_guid;
        private static readonly Guid ManagedDebugEngineGuid = VSConstants.DebugEnginesGuids.ManagedOnly_guid;

        /// <summary>
        /// The project Kind GUID for Solution Folders (virtual containers, not
        /// real build projects). Hardcoded because EnvDTE.Constants exposes
        /// this under different names across versions (vsProjectKindSolutionFolder
        /// in some, vsProjectKindSolutionItems in the Microsoft.VisualStudio.Interop
        /// assembly this repo references) — the GUID itself is stable.
        /// </summary>
        private const string SolutionFolderKindGuid = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        /// <summary>
        /// Launches an executable under the VS debugger via
        /// <see cref="IVsDebugger2.LaunchDebugTargets2"/>. This is VS's own
        /// "start debugging target" primitive — it feeds the exe path,
        /// arguments, working directory, environment block, and debug engine
        /// straight to CreateProcess + the selected engine, with zero
        /// dependency on project/solution configuration. Same path for native
        /// C++, managed C#, and an arbitrary standalone exe.
        /// </summary>
        /// <param name="startProgram">Absolute path to the exe to debug (the
        /// "target"). Required.</param>
        /// <param name="startArguments">Optional command-line arguments passed
        /// to the exe.</param>
        /// <param name="environmentVariablesJson">Optional JSON object string
        /// of environment variables to set on the launched process
        /// (e.g. <c>{"PATH":"...","MY_VAR":"1"}</c>). Null or empty inherits
        /// the parent (VS) environment.</param>
        /// <param name="workingDirectory">Optional working directory; defaults
        /// to the exe's directory when null/empty.</param>
        /// <param name="debugEngine">Engine selector: <c>"native"</c> (default;
        /// the C/C++ native engine), <c>"managed"</c> (the .NET CLR engine), or
        /// a raw debug-engine GUID string for any other engine.</param>
        public async Task<StartDebuggingResult> StartDebuggingAsync(
            string startProgram,
            string? startArguments,
            string? environmentVariablesJson,
            string? workingDirectory,
            string? debugEngine,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(startProgram))
                throw new ArgumentException("startProgram (exe path) is required", nameof(startProgram));

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var warnings = new List<string>();

            // --- Parse the debug engine selection -------------------------------
            // "native" → VSConstants.DebugEnginesGuids.NativeOnly_guid
            //            ({3B476D35-A401-11D2-AAD4-00C04F990171})
            // "managed" → VSConstants.DebugEnginesGuids.ManagedOnly_guid
            //            ({449EC4CC-30D2-4032-9256-EE18EB41B62B})
            // Anything else is treated as a raw GUID string and Guid.Parsed.
            string engineLabel;
            Guid engineGuid;
            string? engineInput = debugEngine;
            if (string.IsNullOrWhiteSpace(engineInput))
            {
                engineGuid = NativeDebugEngineGuid;
                engineLabel = "native";
            }
            else
            {
                string e = engineInput!.Trim();
                if (string.Equals(e, "native", StringComparison.OrdinalIgnoreCase))
                {
                    engineGuid = NativeDebugEngineGuid;
                    engineLabel = "native";
                }
                else if (string.Equals(e, "managed", StringComparison.OrdinalIgnoreCase))
                {
                    engineGuid = ManagedDebugEngineGuid;
                    engineLabel = "managed";
                }
                else
                {
                    try
                    {
                        engineGuid = Guid.Parse(e);
                        engineLabel = engineGuid.ToString();
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(
                            $"DebugEngine value '{e}' is not 'native', 'managed', or a valid GUID: {ex.Message}",
                            nameof(debugEngine));
                    }
                }
            }

            // --- Parse the environment block ------------------------------------
            // EnvironmentVariablesJson is a JSON object of string→string. Null
            // or empty whitespace inherits the parent environment (bstrEnv =
            // null). Invalid JSON throws ArgumentException (surfaces via
            // SafeCall as an internal_error — acceptable; the caller fed bad
            // input).
            Dictionary<string, string>? envVars = null;
            if (!string.IsNullOrWhiteSpace(environmentVariablesJson))
            {
                try
                {
                    envVars = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        environmentVariablesJson!, McpJsonUtilities.DefaultOptions);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        "EnvironmentVariablesJson must be a JSON object of string→string (e.g. {\"KEY\":\"VALUE\"}). "
                        + ex.Message,
                        nameof(environmentVariablesJson));
                }
                if (envVars == null)
                    envVars = new Dictionary<string, string>();
            }

            // --- Resolve working directory --------------------------------------
            string workingDir = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory!
                : (Path.GetDirectoryName(startProgram) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(workingDir))
            {
                // Path.GetDirectoryName returned empty (exe at a root / bare
                // name). Fall back to the current directory rather than crash.
                workingDir = Environment.CurrentDirectory;
                warnings.Add($"WorkingDirectory defaulted to VS current directory ({workingDir}) " +
                             "because the exe path has no directory component.");
            }

            // --- Acquire IVsDebugger2 -------------------------------------------
            var debugger = await GetVsDebugger2Async(ct);

            // --- Build VsDebugTargetInfo2 + Launch ------------------------------
            // LaunchDebugTargets2's managed signature takes (uint count,
            // IntPtr pBuffer) where pBuffer points to a buffer of
            // count contiguous VsDebugTargetInfo2 structs in unmanaged memory.
            // We allocate the buffer, marshal the struct into it, call, and
            // free in a finally (the struct embeds BSTR fields that
            // DestroyStructure releases).
            var info = new VsDebugTargetInfo2
            {
                cbSize = (uint)Marshal.SizeOf(typeof(VsDebugTargetInfo2)),
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = startProgram,
                bstrArg = startArguments ?? "",
                bstrCurDir = workingDir,
                bstrEnv = BuildEnvironmentBlock(envVars),
                guidLaunchDebugEngine = engineGuid,
                // guidLaunchDebugEngine alone is sufficient for a single-engine
                // launch (per Microsoft Learn + nodejstools production code):
                // dwDebugEngineCount stays 0 and pDebugEngines stays IntPtr.Zero.
                dwDebugEngineCount = 0,
                pDebugEngines = IntPtr.Zero,
            };

            IntPtr pBuffer = IntPtr.Zero;
            int hr;
            try
            {
                pBuffer = Marshal.AllocCoTaskMem((int)info.cbSize);
                Marshal.StructureToPtr(info, pBuffer, fDeleteOld: false);
                hr = debugger.LaunchDebugTargets2(1, pBuffer);
            }
            finally
            {
                if (pBuffer != IntPtr.Zero)
                {
                    // DestroyStructure releases the embedded BSTRs (bstrExe /
                    // bstrArg / bstrCurDir / bstrEnv). Safe even if
                    // StructureToPtr never ran (no-op on zeroed memory).
                    try { Marshal.DestroyStructure<VsDebugTargetInfo2>(pBuffer); }
                    catch { /* teardown must not throw */ }
                    Marshal.FreeCoTaskMem(pBuffer);
                }
            }

            bool started;
            if (hr < 0)
            {
                // Launch failed. Surface the HRESULT to the agent and leave the
                // debugger state as observed (likely still design). We do NOT
                // throw — the tool reports the structured failure so the agent
                // can decide whether to retry with a different engine / args.
                started = false;
                string hrMsg;
                try
                {
                    var ex = Marshal.GetExceptionForHR(hr);
                    hrMsg = ex?.Message ?? $"HRESULT 0x{hr:X8}";
                }
                catch
                {
                    hrMsg = $"HRESULT 0x{hr:X8}";
                }
                warnings.Add($"LaunchDebugTargets2 failed: {hrMsg}");
            }
            else
            {
                started = true;
            }

            // Read the resulting debugger mode. In run mode the call has
            // engaged the debugger and the process is executing; in break
            // mode a startup breakpoint was hit immediately; in design mode
            // the launch did not actually attach (e.g. bad engine for the exe
            // type, exe missing). StateFromModeAsync is the canonical mapper.
            string state;
            try
            {
                state = await StateFromModeAsync(ct);
            }
            catch
            {
                state = "unknown";
            }

            return new StartDebuggingResult(
                Started: started,
                State: state,
                Program: startProgram,
                Arguments: string.IsNullOrEmpty(startArguments) ? null : startArguments,
                WorkingDirectory: workingDir,
                EnvironmentVariables: envVars,
                DebugEngine: engineLabel,
                Warnings: warnings.Count == 0 ? null : warnings);
        }

        /// <summary>
        /// Builds the double-null-terminated multi-string environment block
        /// that VsDebugTargetInfo2.bstrEnv expects (the raw lpEnvironment
        /// CreateProcess takes). Format: <c>KEY=VALUE\0KEY2=VALUE2\0\0</c>.
        /// Returns null when <paramref name="env"/> is null or empty so the
        /// launched process inherits the parent (VS) environment — the common
        /// case for start_debugging. We assign the result directly to the
        /// bstrEnv field; the COM interop marshaller will allocate a BSTR via
        /// SysAllocStringLen (which uses the explicit length, so embedded \0
        /// survive). This works because we construct the C# string WITH the
        /// embedded nulls and trailing terminator; the marshaller copies the
        /// full Length, not up to the first null.
        /// </summary>
        private static string? BuildEnvironmentBlock(Dictionary<string, string>? env)
        {
            if (env == null || env.Count == 0)
                return null;

            // StringBuilder can't carry embedded \0 cleanly (it does, but the
            // Length-based allocations are clearer with a char[] / List<char>).
            var chars = new List<char>(capacity: env.Count * 32);
            foreach (var kv in env)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue; // a null/empty key is invalid in an env block
                foreach (char c in kv.Key) chars.Add(c);
                chars.Add('=');
                foreach (char c in kv.Value ?? "") chars.Add(c);
                chars.Add('\0');
            }
            // Trailing extra \0 marks the end of the block (double-null term).
            chars.Add('\0');
            return new string(chars.ToArray());
        }

        public async Task<BreakpointSetResult> SetBreakpointAsync(string file, int line, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("file path is required", nameof(file));
            if (line <= 0)
                throw new ArgumentOutOfRangeException(nameof(line), "line must be a positive integer");

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            // Guard: when a solution IS loaded, refuse to silently no-op on a
            // file that is not part of it. If NO solution is loaded, skip the
            // guard and let VS handle it (get_session_info surfaces the null
            // state separately). The normalization collapses .. / mixed-
            // separator / drive-letter-casing variants so FindProjectItem
            // matches the path VS indexes the item under.
            string loadedSolution = dte.Solution?.FullName ?? "";
            if (!string.IsNullOrWhiteSpace(loadedSolution))
            {
                string normalizedFile = NormalizeFilePath(file);
                ProjectItem? item = null;
                try
                {
                    item = dte.Solution.FindProjectItem(normalizedFile);
                }
                catch
                {
                    // FindProjectItem should not throw for a missing item, but
                    // a transient COM failure must not turn a guard miss into a
                    // hard tool failure — treat as "not found" and let the
                    // Breakpoints.Add path below surface a real error if any.
                    item = null;
                }
                if (item == null)
                {
                    throw new FileNotInSolutionException(
                        $"File '{file}' is not part of the loaded solution '{loadedSolution}'. " +
                        "set_breakpoint only accepts files that belong to the solution currently open in Visual Studio.",
                        file,
                        loadedSolution);
                }
            }

            var result = dte.Debugger.Breakpoints.Add(File: file, Line: line);
            if (result == null || result.Count == 0)
                throw new InvalidOperationException(
                    $"Could not set breakpoint at {file}:{line}. Verify the file path is absolute, " +
                    "the file exists in the solution, and the line contains executable code.");

            var bp = result.Item(1);
            return new BreakpointSetResult(
                File: bp.File ?? file,
                Line: bp.FileLine,
                Enabled: bp.Enabled,
                Condition: bp.Condition ?? "",
                Name: bp.Name ?? "");
        }

        public async Task<BreakpointListResult> ListBreakpointsAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var breakpoints = dte.Debugger.Breakpoints;
            var list = new List<BreakpointInfo>(breakpoints.Count);
            for (int i = 1; i <= breakpoints.Count; i++)
            {
                var bp = breakpoints.Item(i);
                list.Add(new BreakpointInfo(
                    File: bp.File ?? "",
                    Line: bp.FileLine,
                    Column: bp.FileColumn,
                    Enabled: bp.Enabled,
                    Condition: bp.Condition ?? "",
                    Name: bp.Name ?? ""));
            }

            return new BreakpointListResult(list, Total: breakpoints.Count);
        }

        public async Task<BreakpointDeleteResult> DeleteBreakpointAsync(string file, int line, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("file path is required", nameof(file));
            if (line <= 0)
                throw new ArgumentOutOfRangeException(nameof(line), "line must be a positive integer");

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var normalizedFile = Path.GetFullPath(file);
            foreach (Breakpoint bp in dte.Debugger.Breakpoints)
            {
                string bpFile = Path.GetFullPath(bp.File);
                if (string.Equals(bpFile, normalizedFile, StringComparison.OrdinalIgnoreCase)
                    && bp.FileLine == line)
                {
                    bp.Delete();
                    return new BreakpointDeleteResult(Deleted: true, File: file, Line: line);
                }
            }

            throw new BreakpointNotFoundException($"No breakpoint found at {file}:{line}");
        }

        public async Task<BreakpointClearResult> ClearAllBreakpointsAsync(bool confirm, CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            int count = dte.Debugger.Breakpoints.Count;

            if (!confirm)
            {
                // Dry-run: report what WOULD be deleted without touching
                // anything. Forces a second call with confirm=true after the
                // agent reviews the count, preventing accidental bulk delete.
                return new BreakpointClearResult(
                    Deleted: 0,
                    WasDryRun: true,
                    WouldDelete: count,
                    Message: count == 0
                        ? "No breakpoints to delete."
                        : $"Dry run: would delete {count} breakpoint(s). Pass confirm=true to actually delete.");
            }

            for (int i = count; i >= 1; i--)
            {
                dte.Debugger.Breakpoints.Item(i).Delete();
            }

            return new BreakpointClearResult(
                Deleted: count,
                WasDryRun: false,
                WouldDelete: count,
                Message: $"Deleted {count} breakpoint(s).");
        }

        /// <summary>
        /// Triggers a full solution build via
        /// <c>DTE.Solution.SolutionBuild.Build(WaitForBuildToFinish: true)</c>.
        /// Blocks until the build completes — the MCP Streamable HTTP transport
        /// keeps the SSE response stream open during the wait (no client
        /// timeout), and the single-session request gate stays held (the agent
        /// is awaiting this result anyway). Returns the active configuration
        /// name and the failed-project count (0 == success).
        /// </summary>
        public async Task<BuildSolutionResult> BuildSolutionAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var solution = dte.Solution;
            if (solution == null || string.IsNullOrWhiteSpace(solution.FullName))
                throw new InvalidOperationException("No solution is loaded");

            var solutionBuild = solution.SolutionBuild;
            if (solutionBuild == null)
                throw new InvalidOperationException("SolutionBuild is not available");

            string configuration = "Unknown";
            try { configuration = solutionBuild.ActiveConfiguration?.Name ?? "Unknown"; }
            catch { /* ActiveConfiguration can throw for odd solution states */ }

            // Build asynchronously: Build(false) returns immediately without
            // blocking the UI thread. OnBuildDone fires on the UI thread when
            // the build completes; a TaskCompletionSource bridges it to
            // async/await so this method yields the UI thread while VS builds
            // — VS stays fully responsive. Build(true) is avoided: it freezes
            // the UI for the whole build and has a known deadlock bug
            // (TcUnit-Runner issue #5).
            var buildEvents = dte.Events.BuildEvents;
            var tcs = new TaskCompletionSource<bool>();

            void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
            {
                buildEvents.OnBuildDone -= OnBuildDone;
                tcs.TrySetResult(true);
            }
            buildEvents.OnBuildDone += OnBuildDone;

            using (ct.Register(() => { buildEvents.OnBuildDone -= OnBuildDone; tcs.TrySetCanceled(); }))
            {
                solutionBuild.Build(false);

                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(15)));
                if (done != tcs.Task)
                {
                    buildEvents.OnBuildDone -= OnBuildDone;
                    throw new TimeoutException("Build did not complete within 15 minutes");
                }
                await tcs.Task;
            }

            int failedProjects = solutionBuild.LastBuildInfo;
            return new BuildSolutionResult(
                Configuration: configuration,
                FailedProjects: failedProjects,
                Succeeded: failedProjects == 0);
        }

        /// <summary>
        /// Reads the VS Output window's Build pane and returns a sliced view.
        /// The agent uses this after <see cref="BuildSolutionAsync"/> to inspect
        /// compiler diagnostics straight from the build log (more trustworthy
        /// than the Error List, which mixes in stale squiggles). <paramref name="tail"/>
        /// = true reads the most recent <paramref name="maxLines"/> lines (the
        /// errors at the end); false reads the first <paramref name="maxLines"/>.
        /// </summary>
        public async Task<BuildOutputResult> GetBuildOutputAsync(int maxLines, bool tail, CancellationToken ct)
        {
            var (paneName, allLines) = await ReadBuildPaneAllLinesAsync(ct);
            int totalLines = allLines.Length;

            int limit = maxLines > 0 ? maxLines : 200;
            IEnumerable<string> slice = tail
                ? allLines.Skip(Math.Max(0, totalLines - limit))
                : allLines.Take(limit);
            var selected = slice.ToArray();

            return new BuildOutputResult(
                PaneName: paneName,
                Output: string.Join("\n", selected),
                TotalLines: totalLines,
                ReturnedLines: selected.Length,
                Truncated: totalLines > selected.Length);
        }

        /// <summary>
        /// 读取 Build pane 自 <paramref name="fromLine"/> 起新增的行。
        /// 用于 build 保活循环：把增量构建日志通过 logging 通知转发给客户端
        /// （不暴露给模型）。无状态——偏移由调用方持有，符合单会话串行契约。
        /// </summary>
        public async Task<BuildOutputDeltaResult> GetBuildOutputDeltaAsync(int fromLine, CancellationToken ct)
        {
            var (_, allLines) = await ReadBuildPaneAllLinesAsync(ct);
            int totalLines = allLines.Length;
            // pane 被清空重置（新构建）导致 fromLine 越过当前行数时，
            // 从头重读，避免漏掉清空后的日志。
            int start = fromLine > totalLines ? 0 : Math.Max(0, fromLine);
            var newLines = new List<string>(Math.Max(0, totalLines - start));
            for (int i = start; i < totalLines; i++)
                newLines.Add(allLines[i]);
            return new BuildOutputDeltaResult(totalLines, newLines);
        }

        /// <summary>
        /// 定位 Build pane（名称按本地化模糊匹配）并返回其全文按行切分的结果
        /// 及 pane 显示名。供 <see cref="GetBuildOutputAsync"/> 与
        /// <see cref="GetBuildOutputDeltaAsync"/> 复用。需在 UI 线程执行
        /// （pane 枚举 + EditPoint 均为 COM 调用）。
        /// </summary>
        private async Task<(string PaneName, string[] Lines)> ReadBuildPaneAllLinesAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var outputWindow = dte.ToolWindows.OutputWindow;

            // The Build pane name is localized ("Build"/"生成"/"构建"/"組建"),
            // so match loosely rather than by exact string.
            OutputWindowPane? buildPane = null;
            try
            {
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    string? n = pane.Name;
                    if (n != null && (n.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
                                      || n.IndexOf("生成", StringComparison.Ordinal) >= 0
                                      || n.IndexOf("构建", StringComparison.Ordinal) >= 0
                                      || n.IndexOf("組建", StringComparison.Ordinal) >= 0))
                    {
                        buildPane = pane;
                        break;
                    }
                }
            }
            catch { /* enumeration is best-effort */ }

            if (buildPane == null)
                throw new InvalidOperationException("Build output pane not found in the Output window");

            // Read full text via EditPoint — does not touch the user's selection.
            string allText = "";
            try
            {
                var textDoc = buildPane.TextDocument;
                var ep = textDoc.StartPoint.CreateEditPoint();
                allText = ep.GetText(textDoc.EndPoint);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read Build output pane: {ex.Message}");
            }

            var lines = allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            return (buildPane.Name ?? "", lines);
        }

        private async Task<DBGMODE> GetDebuggerModeAsync(CancellationToken ct)
        {
            // IVsDebugger2 (not v1): the v1 interface's OLE marshaler fails QI
            // with E_NOINTERFACE when the cast lands off the STA thread, whereas
            // IVsDebugger2.GetInternalDebugMode marshals robustly. Same DBGMODE
            // contract, same array-out signature.
            var debugger = await GetVsDebugger2Async(ct);
            var mode = new DBGMODE[1];
            int hr = debugger.GetInternalDebugMode(mode);
            if (hr != 0)
                throw new InvalidOperationException($"GetInternalDebugMode failed with HRESULT 0x{hr:X8}");
            return mode[0] & ~DBGMODE.DBGMODE_Enc;
        }

        /// <summary>
        /// Acquires the DTE while guaranteeing the debugger is in break mode.
        /// Throws <see cref="RequireBreakModeException"/> (carrying the current
        /// state string) on any non-break mode so SafeCall can populate
        /// ErrorResult.State.
        /// </summary>
        private async Task<DTE2> RequireBreakModeAsync(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var mode = await GetDebuggerModeAsync(ct);
            if (mode != DBGMODE.DBGMODE_Break)
            {
                throw new RequireBreakModeException(
                    $"Operation requires break mode. Current state: {MapState(mode)}",
                    MapState(mode));
            }

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");
            return dte;
        }

        private async Task PollForBreakModeAsync(CancellationToken ct)
        {
            for (int i = 0; i < 25; i++)
            {
                var mode = await GetDebuggerModeAsync(ct);
                if (mode == DBGMODE.DBGMODE_Break)
                    return;
                await Task.Delay(200, ct);
            }
            // Final poll to let any state change settle.
            await GetDebuggerModeAsync(ct);
        }

        /// <summary>
        /// Continues execution from the current breakpoint.
        ///
        /// <para><b>wait_for_break=false (default)</b>: fire-and-forget. Calls
        /// <c>Go(false)</c> and returns immediately with <c>state="running"</c>.
        /// The agent can poll <c>get_debugger_state</c> afterwards to detect
        /// the next break.</para>
        ///
        /// <para><b>wait_for_break=true</b>: waits for the next break/stop
        /// event via <c>DebuggerEvents.OnEnterBreakMode</c> /
        /// <c>OnEnterDesignMode</c>, bridged to async with a
        /// <see cref="TaskCompletionSource{TResult}"/>. The HTTP response stays
        /// open as long as needed; KeepAliveNotifier periodically emits MCP
        /// logging notifications over the in-flight response stream to keep
        /// the connection alive while a tool is awaiting a break event.</para>
        ///
        /// <para><c>timeout_seconds</c> (only meaningful with wait_for_break=true):
        /// <c>0</c> (default) = wait indefinitely (relies on SSE keep-alive);
        /// <c>N&gt;0</c> = wait at most N seconds, returning the current
        /// state + a message on timeout (NOT an error — agent can retry).</para>
        /// </summary>
        public async Task<ExecutionResult> ContinueExecutionAsync(
            bool waitForBreak,
            int timeoutSeconds,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);

            // 默认模式：立即返回。Go(false) 不阻塞，调试器进入 run 模式。
            if (!waitForBreak)
            {
                dte.Debugger.Go(false);
                return new ExecutionResult(State: "running", Message: "Execution continued");
            }

            // 等待模式：订阅 OnEnterBreakMode / OnEnterDesignMode，
            // 用 TaskCompletionSource 桥接 COM 事件到 async/await。
            // 整段不阻塞 UI 线程；HTTP POST 靠 SSE 心跳保活。
            var events = dte.Events.DebuggerEvents;
            var tcs = new TaskCompletionSource<bool>();

            void OnBreak(EnvDTE.dbgEventReason reason, ref EnvDTE.dbgExecutionAction action)
            {
                events.OnEnterBreakMode -= OnBreak;
                events.OnEnterDesignMode -= OnStop;
                tcs.TrySetResult(true);
            }
            void OnStop(EnvDTE.dbgEventReason reason)
            {
                events.OnEnterBreakMode -= OnBreak;
                events.OnEnterDesignMode -= OnStop;
                tcs.TrySetResult(false);
            }

            // 超时与 shutdown / agent 取消共用一个 linked CTS：只跟随
            // 外部 ct（shutdown、idle 兜底、agent 取消），并按 timeoutSeconds
            // 启动 CancelAfter。timeout_seconds=0 时不启动 CancelAfter = 无限等。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutSeconds > 0)
                linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            events.OnEnterBreakMode += OnBreak;
            events.OnEnterDesignMode += OnStop;

            using (linkedCts.Token.Register(() =>
            {
                events.OnEnterBreakMode -= OnBreak;
                events.OnEnterDesignMode -= OnStop;
                tcs.TrySetCanceled();
            }))
            {
                dte.Debugger.Go(false);

                try
                {
                    bool hit = await tcs.Task.ConfigureAwait(false);
                    return new ExecutionResult(
                        State: hit ? "break" : "stopped",
                        Message: hit
                            ? "Breakpoint hit."
                            : "Debuggee exited without hitting a breakpoint.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 外部取消（shutdown、idle 兜底、agent 主动取消）：重抛。
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // timeoutSeconds 到期：不算错误，返回当前状态 + 提示。
                    string state = await StateFromModeAsync(ct).ConfigureAwait(false);
                    return new ExecutionResult(
                        State: state,
                        Message: $"Continue waited {timeoutSeconds}s without a break event — still {state}.");
                }
            }
        }

        public async Task<ExecutionResult> StepIntoAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);
            dte.Debugger.StepInto(true);
            await PollForBreakModeAsync(ct);
            return new ExecutionResult(State: await StateFromModeAsync(ct), Message: "Step into complete");
        }

        public async Task<ExecutionResult> StepOverAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);
            dte.Debugger.StepOver(true);
            await PollForBreakModeAsync(ct);
            return new ExecutionResult(State: await StateFromModeAsync(ct), Message: "Step over complete");
        }

        public async Task<ExecutionResult> StepOutAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);
            dte.Debugger.StepOut(true);
            await PollForBreakModeAsync(ct);
            return new ExecutionResult(State: await StateFromModeAsync(ct), Message: "Step out complete");
        }

        public async Task<ExecutionResult> StopDebuggingAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var mode = await GetDebuggerModeAsync(ct);
            if (mode == DBGMODE.DBGMODE_Design)
                throw new InvalidOperationException(
                    "Cannot stop debugging when not in a debug session (current state: design)");

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            dte.Debugger.Stop();
            return new ExecutionResult(State: "stopped", Message: "Debugging stopped");
        }

        public async Task<LocalsResult> GetLocalVariablesAsync(int maxChars, CancellationToken ct)
        {
            ThrowIfDisposed();
            int budget = maxChars > 0 ? maxChars : DefaultCharBudget;
            var dte = await RequireBreakModeAsync(ct);

            var frame = dte.Debugger.CurrentStackFrame;
            if (frame?.Locals == null)
                return new LocalsResult(
                    Locals: new List<ExpressionInfo>(), Total: 0, Returned: 0, Truncated: false);

            var locals = frame.Locals;
            var list = new List<ExpressionInfo>(locals.Count);
            int consumed = 0;
            bool truncated = false;

            for (int i = 1; i <= locals.Count; i++)
            {
                var local = locals.Item(i);
                // Estimate this local's serialized footprint before committing.
                int estimate = EstimateExpressionFootprint(local);
                if (consumed + estimate > budget && list.Count > 0)
                {
                    truncated = true;
                    break;
                }
                var info = ToExpressionInfo(local, budget - consumed, out int actual);
                consumed += actual;
                list.Add(info);
            }

            return new LocalsResult(
                Locals: list, Total: locals.Count, Returned: list.Count, Truncated: truncated);
        }

        public async Task<CallStackResult> GetCallStackAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);

            var thread = dte.Debugger.CurrentThread;
            if (thread?.StackFrames == null)
                return new CallStackResult(
                    Frames: new List<StackFrameInfo>(), Total: 0, Returned: 0);

            var frames = thread.StackFrames;
            int maxFrames = Math.Min(frames.Count, MaxFrames);
            var list = new List<StackFrameInfo>(maxFrames);
            for (int i = 1; i <= maxFrames; i++)
            {
                var frame = frames.Item(i);
                list.Add(new StackFrameInfo(
                    FrameIndex: i - 1,
                    FunctionName: frame.FunctionName ?? "",
                    Module: frame.Module ?? "",
                    ReturnType: frame.ReturnType ?? ""));
            }

            return new CallStackResult(Frames: list, Total: frames.Count, Returned: maxFrames);
        }

        public async Task<ExpressionResult> EvaluateExpressionAsync(string expression, int maxChars, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("expression is required", nameof(expression));
            int budget = maxChars > 0 ? maxChars : DefaultCharBudget;
            var dte = await RequireBreakModeAsync(ct);

            var result = dte.Debugger.GetExpression(expression, false, 5000);
            if (result == null || !result.IsValidValue)
            {
                return new ExpressionResult(new ExpressionInfo(
                    Name: expression,
                    Type: result?.Type ?? "",
                    Value: null,
                    Error: true,
                    HasChildren: false,
                    Children: new List<ExpressionInfo>(),
                    Truncated: false,
                    Hint: "Expression could not be evaluated"));
            }

            return new ExpressionResult(ToExpressionInfo(result, budget, out _));
        }

        public async Task<ExpressionResult> GetVariableDetailAsync(string variableName, int maxChars, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(variableName))
                throw new ArgumentException("variableName is required", nameof(variableName));
            int budget = maxChars > 0 ? maxChars : DefaultCharBudget;
            var dte = await RequireBreakModeAsync(ct);

            var frame = dte.Debugger.CurrentStackFrame;
            if (frame?.Locals != null)
            {
                for (int i = 1; i <= frame.Locals.Count; i++)
                {
                    var local = frame.Locals.Item(i);
                    if (string.Equals(local.Name, variableName, StringComparison.OrdinalIgnoreCase))
                        return new ExpressionResult(ToExpressionInfo(local, budget, out _));
                }
            }

            var result = dte.Debugger.GetExpression(variableName, false, 5000);
            if (result != null && result.IsValidValue)
                return new ExpressionResult(ToExpressionInfo(result, budget, out _));

            throw new BreakpointNotFoundException($"Variable '{variableName}' not found in current scope");
        }

        // --- DTO assembly helpers (char-budget truncation moved out of the
        //     deleted expression-serializer helper; the BFS + truncation +
        //     Hint semantics are preserved per CONTEXT Claude's Discretion). ---

        /// <summary>
        /// Converts an EnvDTE <see cref="Expression"/> into an
        /// <see cref="ExpressionInfo"/> tree, honoring a char budget. Children
        /// are expanded greedily; when the budget is hit the remaining
        /// children are dropped and <see cref="ExpressionInfo.Truncated"/> +
        /// a drill-down hint is set so the LLM knows to call
        /// <c>get_variable_detail</c>.
        /// </summary>
        private static ExpressionInfo ToExpressionInfo(Expression expr, int budget, out int consumed)
        {
            string name = expr.Name ?? "";
            string type = expr.Type ?? "";

            if (!expr.IsValidValue)
            {
                consumed = name.Length + type.Length + 32;
                return new ExpressionInfo(
                    Name: name, Type: type, Value: null, Error: true,
                    HasChildren: false, Children: new List<ExpressionInfo>(),
                    Truncated: false, Hint: null);
            }

            string value = expr.Value ?? "";
            // Reserve room for the top-level fields themselves.
            int used = name.Length + type.Length + value.Length + 48;
            consumed = used;

            var children = expr.DataMembers;
            if (children == null || children.Count == 0)
            {
                return new ExpressionInfo(
                    Name: name, Type: type, Value: value, Error: false,
                    HasChildren: false, Children: new List<ExpressionInfo>(),
                    Truncated: false, Hint: null);
            }

            var childList = new List<ExpressionInfo>();
            bool truncated = false;
            for (int i = 1; i <= children.Count; i++)
            {
                var child = children.Item(i);
                int childEstimate = EstimateExpressionFootprint(child);
                if (used + childEstimate > budget)
                {
                    truncated = true;
                    break;
                }

                var childInfo = ToExpressionInfo(child, budget - used, out int childActual);
                used += childActual;
                childList.Add(childInfo);
            }

            return new ExpressionInfo(
                Name: name, Type: type, Value: value, Error: false,
                HasChildren: true, Children: childList,
                Truncated: truncated,
                Hint: truncated ? "Use get_variable_detail to explore remaining children" : null);
        }

        /// <summary>
        /// Cheap footprint estimate (name + type + value + framing) used to
        /// decide whether a child fits the budget before the recursive
        /// expansion pays for it.
        /// </summary>
        private static int EstimateExpressionFootprint(Expression expr)
        {
            int n = expr.Name?.Length ?? 0;
            int t = expr.Type?.Length ?? 0;
            int v = expr.Value?.Length ?? 0;
            return n + t + v + 48;
        }

        private async Task<string> StateFromModeAsync(CancellationToken ct)
        {
            var mode = await GetDebuggerModeAsync(ct);
            return MapState(mode);
        }

        private static string MapState(DBGMODE mode)
        {
            DBGMODE baseMode = mode & ~DBGMODE.DBGMODE_Enc;
            return baseMode switch
            {
                DBGMODE.DBGMODE_Design => "design",
                DBGMODE.DBGMODE_Break  => "break",
                DBGMODE.DBGMODE_Run    => "running",
                _ => $"unknown ({mode})"
            };
        }

        /// <summary>
        /// Canonicalizes a file path for membership checks: <see cref="Path.GetFullPath"/>
        /// resolves <c>..\</c>-relative segments and normalizes separators, then the
        /// forward-slash pass + <see cref="string.ToUpperInvariant"/> collapse the
        /// remaining case/separator differences (Windows paths are case-insensitive;
        /// <c>DTE.Solution.FindProjectItem</c> matches against a backslash path it
        /// indexes). Extracted as pure so it can be unit-tested without a live VS.
        /// </summary>
        public static string NormalizeFilePath(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return file;
            string full = Path.GetFullPath(file);
            // Replace any alt-separator with backslashes so FindProjectItem's
            // index (backslash-only) matches regardless of caller convention.
            return full.Replace('/', '\\').ToUpperInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DebuggerFacade));
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
