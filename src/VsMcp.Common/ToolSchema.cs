using System.Collections.Generic;

namespace VsMcp.Common
{
    /// <summary>
    /// 工具 schema 表 + initialize instructions。Gateway 本地构造 tools/list
    /// 响应时直接读这里，不再转发到 VS。VS 端的 ToolExecutor 按工具名分派，
    /// 不需要此 schema（它只按参数名从 JsonElement 读取值）。
    ///
    /// 工具列表与原 McpRequestProcessor.BuildToolCollection() 完全一致；
    /// description 文本原样搬过来。inputSchema 的 properties/required 从
    /// 各工具方法签名推导（参数名、类型、是否可选）。
    /// </summary>
    public static class ToolSchema
    {
        /// <summary>
        /// initialize 响应的 instructions 文本，引导 agent 优先用 VS 工具
        /// 而非 grep 查代码结构（C++ 多态/重载/模板让 grep 不可靠）。
        /// 原样搬自 McpRequestProcessor 的 ServerInstructions。
        /// </summary>
        public const string ServerInstructions = @"This server drives Visual Studio — the debugger plus C++ code intelligence.

When querying code STRUCTURE in this solution (locating symbols, call graphs, inheritance), PREFER these tools over grep or reading files. They use VS's full semantic index and stay correct for C++ overloads, virtual dispatch, and templates — exactly where grep goes wrong:
  - find_symbol        locate a symbol's definitions/declarations
  - get_call_graph     who calls a function (callers) / what it calls (callees)
  - get_type_hierarchy inheritance / implementation relationships
  - go_to_definition   resolve a symbol at a source position

Reach for grep only for plain-text / literal searches. Use the debugger tools (set_breakpoint, step_over/step_into/step_out, continue_execution, list_local_variables, get_variable_detail, evaluate_expression) when diagnosing runtime behavior; they require break mode.";

        // ─────────────────── 属性 schema 构造辅助 ───────────────────

        private static object Str(string desc) => new { type = "string", description = desc };
        private static object StrDefault(string desc, string def) => new { type = "string", description = desc, @default = def };
        private static object Int(string desc) => new { type = "integer", description = desc };
        private static object IntDefault(string desc, int def) => new { type = "integer", description = desc, @default = def };
        private static object BoolDefault(string desc, bool def) => new { type = "boolean", description = desc, @default = def };
        private static object StrArray(string desc) => new { type = "array", items = new { type = "string" }, description = desc };
        private static object ReadOnly() => new { readOnlyHint = true, destructiveHint = false };
        private static object Destructive() => new { readOnlyHint = false, destructiveHint = true };
        private static object Neutral() => new { readOnlyHint = false, destructiveHint = false };

        /// <summary>
        /// 返回全部 VS 工具的 schema 列表。每个元素是匿名对象，序列化为
        /// MCP tools/list result 中的单个 tool 描述。
        /// go_to_definition 仅 includeGoToDefinition=true 时加入；
        /// eval_csharp 仅 includeEvalCsharp=true 时加入。
        /// </summary>
        public static List<object> GetAllTools(bool includeGoToDefinition, bool includeEvalCsharp)
        {
            var tools = new List<object>();

            // ─────────── 调试器工具（_facade != null 时注册） ───────────

            tools.Add(new
            {
                name = "get_debugger_state",
                description = "Returns the current debugger mode:\n" +
                              "- design:  no debug session active\n" +
                              "- break:   paused at a breakpoint; can read variables / call stack / step\n" +
                              "- running: executing; cannot inspect until next break\n\n" +
                              "Typically the first call in a debug workflow to determine which tools are available.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "get_session_info",
                description = "Returns the solution currently loaded in Visual Studio (path, directory, project list), a best-effort debug target, and the current debugger state. Fields are null/empty if no solution is loaded.\n\n" +
                              "Typically the first call in a debug workflow — confirms VS is operating on the expected project tree before set_breakpoint / start_debugging.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "search_project",
                description = "Enumerates projects in the loaded Visual Studio solution (recursing Solution Folders) and returns each project's Name, UniqueName, FullName (.csproj/.vcxproj path), Kind (project-type GUID), whether it is the startup project, and a best-effort OutputTarget (built executable path). Use this before start_debugging to locate the project owning the executable you want to launch and read back its output path. Optional 'query' is a case-insensitive substring filter on Name/UniqueName/FullName; omit to list every project.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { query = Str("Case-insensitive substring filter on Name/UniqueName/FullName; omit to list every project.") },
                    required = new string[] { },
                },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "start_debugging",
                description = "Launches an executable under the Visual Studio debugger. Takes an absolute exe path (startProgram, required) plus optional arguments, environment variables (as a JSON object string like {\"KEY\":\"VALUE\"}), working directory, and debugEngine selector.\n\n" +
                              "Does NOT touch project configuration, launchSettings.json, or vcxproj files — params are used for this single launch only and nothing is persisted.\n\n" +
                              "C++ targets default to the native debug engine; pass debugEngine=\"managed\" for .NET/CLR debugging, or a raw debug-engine GUID string for any other engine. The executable must already be built (call build_solution first if unsure).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        startProgram = Str("Absolute path to the executable to launch under the debugger."),
                        startArguments = Str("Command-line arguments to pass to the executable."),
                        environmentVariablesJson = Str("JSON object string of environment variables, e.g. {\"KEY\":\"VALUE\"}."),
                        workingDirectory = Str("Working directory for the debugged process."),
                        debugEngine = Str("Debug engine selector: 'managed' for .NET/CLR, a raw debug-engine GUID string, or omit for native (C++ default)."),
                    },
                    required = new[] { "startProgram" },
                },
                annotations = Destructive(),
            });

            tools.Add(new
            {
                name = "set_breakpoint",
                description = "Sets a breakpoint at the specified file path and line number. The file path must be absolute and part of the currently loaded solution (returns file_not_in_solution otherwise — call get_session_info / search_project first to discover valid paths).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = Str("Absolute path to the source file (must be part of the loaded solution)."),
                        line = Int("1-based line number for the breakpoint."),
                    },
                    required = new[] { "file", "line" },
                },
                annotations = Neutral(),
            });

            tools.Add(new
            {
                name = "list_breakpoints",
                description = "Lists all current breakpoints with file, line, condition, and enabled status.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "delete_breakpoint",
                description = "Deletes the breakpoint at the specified file path and line number. Deletion is immediate (no confirmation dialog). Returns breakpoint_not_found error if no breakpoint exists at the given location.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        file = Str("Absolute path to the source file."),
                        line = Int("1-based line number of the breakpoint to delete."),
                    },
                    required = new[] { "file", "line" },
                },
                annotations = Destructive(),
            });

            tools.Add(new
            {
                name = "clear_all_breakpoints",
                description = "Clears all breakpoints. Two-step guard: confirm=false (default) is a dry-run that reports the count without deleting; pass confirm=true to actually delete after reviewing the count.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { confirm = BoolDefault("false (default) = dry-run reporting count; true = actually delete all breakpoints.", false) },
                    required = new string[] { },
                },
                annotations = Destructive(),
            });

            tools.Add(new
            {
                name = "build_solution",
                description = "Triggers a full solution build and waits for it to complete. Long-running — safe for multi-minute builds: while building, the server streams incremental build-log lines as logging notifications (visible in the client's debug panel, NOT injected into the model context) so the tool-call never times out.\n\n" +
                              "Returns the active build configuration name and the failed-project count (0 means success). Use after editing C++/C# code to verify it compiles before running or debugging. Follow with get_build_output to read the full compiler diagnostics.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = Neutral(),
            });

            tools.Add(new
            {
                name = "get_build_output",
                description = "Reads the VS Output window's Build pane and returns PLAIN TEXT (not JSON): one header line (pane name, head/tail direction, returned/total line count, truncation flag) followed by the raw build log — newlines preserved, no escaping. Authoritative source for compiler diagnostics (the in-IDE error list may include stale squiggles from incomplete IntelliSense). maxLines caps the returned line count (default 200); tail=true (default) reads the most recent lines where errors usually appear, tail=false reads from the top.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        maxLines = IntDefault("Maximum number of lines to return.", 200),
                        tail = BoolDefault("true (default) reads most recent lines; false reads from the top.", true),
                    },
                    required = new string[] { },
                },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "continue_execution",
                description = "Continues execution from the current breakpoint. Only works when debugger is in break mode.\n\n" +
                              "wait_for_break (default=false):\n" +
                              "  false — fire-and-forget; returns immediately with state=\"running\".\n" +
                              "  true  — waits for the next breakpoint hit OR debuggee exit. While waiting, the server emits periodic logging notifications (visible in the client's debug panel, NOT the model context) to keep the tool-call alive.\n\n" +
                              "timeout_seconds (only with wait_for_break=true; default=0):\n" +
                              "  0    — wait indefinitely (logging keep-alive; safe — agent disconnect, server shutdown, and debuggee exit all unblock it).\n" +
                              "  N>0  — wait at most N seconds; on timeout returns the current state with a message (NOT an error, agent may retry).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        waitForBreak = BoolDefault("false = fire-and-forget (returns immediately); true = waits for next breakpoint or exit.", false),
                        timeoutSeconds = IntDefault("Only with waitForBreak=true. 0 = wait indefinitely; N>0 = wait at most N seconds.", 0),
                    },
                    required = new string[] { },
                },
                annotations = Neutral(),
            });

            tools.Add(new
            {
                name = "step_into",
                description = "Steps into the next statement. Waits up to ~5s for break-mode re-entry (polls internally), then returns the new state. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = Neutral(),
            });

            tools.Add(new
            {
                name = "step_over",
                description = "Steps over the next statement. Waits up to ~5s for break-mode re-entry (polls internally), then returns the new state. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = Neutral(),
            });

            tools.Add(new
            {
                name = "step_out",
                description = "Steps out of the current function. Waits up to ~5s for break-mode re-entry (polls internally), then returns the new state. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = Neutral(),
            });

            tools.Add(new
            {
                name = "stop_debugging",
                description = "Stops the current debugging session. Immediate (no confirmation dialog). Works in break or running mode. In design mode (no active session), returns internal_error rather than no-op — call get_debugger_state first to check the mode.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = Destructive(),
            });

            tools.Add(new
            {
                name = "list_local_variables",
                description = "Lists all local variables at the current stack frame as PLAIN TEXT (not JSON), one per line `name (type) = value`; `{…}` marks entries with children (use get_variable_detail to expand). SHALLOW — children are NOT expanded. maxLocals (default 200) and maxChars (default 4096) cap the output so a frame with many/deep locals can't blow up the context. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        maxLocals = IntDefault("Maximum number of locals to return.", 200),
                        maxChars = IntDefault("Maximum total output characters.", 4096),
                    },
                    required = new string[] { },
                },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "get_call_stack",
                description = "Returns the call stack as PLAIN TEXT (not JSON), one frame per line: `#idx  module!Function  → RetType`. At most 50 frames (deepest dropped if the stack is deeper). Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "evaluate_expression",
                description = "Evaluates an arbitrary expression (e.g., obj.Property.Method()) and returns the result as PLAIN TEXT (not JSON): `expr (type) = value`, with object members indented underneath (tree-style). Expression syntax follows the active debug engine: C#-like for managed, C/C++-like for native. May modify state if the expression has side effects. 5-second timeout. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        expression = Str("Expression to evaluate (e.g. obj.Property.Method())."),
                        maxChars = IntDefault("Maximum total output characters.", 1024),
                    },
                    required = new[] { "expression" },
                },
                annotations = Destructive(),
            });

            tools.Add(new
            {
                name = "get_variable_detail",
                description = "Expands one or more variables/expressions to a limited depth, tree-style. 'expressions' is an array of debugger expressions — each can be a local name OR any C++/C# expression (obj.member, arr[3], ptr->next), evaluated by the debug engine. 'depth' (default 1) controls how many layers of children are expanded; deeper layers stay hasChildren=true for follow-up calls, keeping each response bounded. Use after list_local_variables to drill into hasChildren=true entries. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        expressions = StrArray("Array of debugger expressions to expand (local names or any C++/C# expression)."),
                        depth = IntDefault("How many layers of children to expand.", 1),
                        maxChars = IntDefault("Maximum total output characters.", 4096),
                    },
                    required = new[] { "expressions" },
                },
                annotations = ReadOnly(),
            });

            // ─────────── 符号工具（_symbolFacade != null 时注册） ───────────

            tools.Add(new
            {
                name = "find_symbol",
                description = "Use when you need to locate where a symbol (function/class/variable/typedef) is defined or declared across the solution. Searches every language: C++ via the VC CodeStore (IVCNavigateToFactory, the same in-proc backend behind Ctrl+T) and C#/VB via LSP workspace/symbol — accuracy matches in-IDE 'Go To All'. Prefer over grep — C++ overloads, namespaces, and macros make plain-text search ambiguous; this uses the language services' semantic model. Returns PLAIN TEXT, not JSON: each match shows a header (name/kind/language/file:line) followed by source context — contextLines above and below the symbol line (default 5, set 0 for just the symbol line), with line numbers and a ▶ marker on the symbol line. Results are grouped by file (a same-named symbol's .h declaration and .cpp implementation land together). Optional maxResults (default 16) caps the match count; maxChars (default 8000) caps total output (truncated with a notice if exceeded — raise it to see full context). Optional filters, both OMITTED by default (= no filter): language — normalized match on the language label (cpp/c++/cxx, csharp/c#/cs, vb all work, case-insensitive); kind — case-insensitive substring of a LOWERCASE friendly type tag. Valid kind values (closed set — use lowercase, e.g. 'method' not 'Method'): class, struct, interface, enum, enumitem, delegate, module, namespace, constant, field, method, function, property, event, variable, constructor, operator, typeparameter, other. Any other value (e.g. 'template', 'lambda', 'global', 'abstract') is NOT valid and matches nothing — a C++ template class filters as kind='class', a template function as kind='function' (or 'method' if a member).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = Str("Symbol name to search for."),
                        maxResults = IntDefault("Maximum number of matches.", 16),
                        maxChars = IntDefault("Maximum total output characters.", 8000),
                        contextLines = IntDefault("Lines of source context above and below; 0 for just the symbol line.", 5),
                        language = Str("Filter by language label (cpp/csharp/vb, case-insensitive). Omit for no filter."),
                        kind = Str("Filter by lowercase kind tag (class/struct/method/function/etc.). Omit for no filter."),
                    },
                    required = new[] { "query" },
                },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "get_type_hierarchy",
                description = "Use when you need a type's inheritance chain, who derives from / implements it, or sibling types sharing a base — for impact analysis before modifying a class/interface. Returns the full ancestor chain (to root), direct descendants across the whole solution, and siblings. Prefer over grep/reading files — reverse inheritance requires indexing every type in the solution, which source-grepping cannot replicate. Found=false if no type matches.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { typeName = Str("Type name to search for.") },
                    required = new[] { "typeName" },
                },
                annotations = ReadOnly(),
            });

            tools.Add(new
            {
                name = "get_call_graph",
                description = "Use when you need who calls a C++ function (direction='callers') or what it calls (direction='callees', default) — refactor impact analysis, dead-code hunting, or tracing control flow. The same backend behind VS's Call Hierarchy window (VC CallHierarchy API via IVCCallHierarchyMemberItemFactory). Prefer over grep — C++ virtual dispatch, overloads, and same-named methods across classes make text search wrong; this uses VS's full-solution call graph. Each node carries name + signature + file:line. A name usually matches multiple symbols (overloads, .h declaration + .cpp implementation, same-named methods across classes); all are enumerated, searched, and de-duplicated. callers reverse-search the whole solution per symbol and accumulate across matches, so a popular function can take 1-3 minutes (default timeoutSeconds=180); a logging heartbeat keeps the client alive meanwhile, and TimedOut=true means it didn't finish in time (Nodes still carries whatever was found). Found=false if no symbol matches. Pure virtual interface declarations are not in the C++ symbol index, so an interface's callers are reported at its concrete implementations.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = Str("Function name to search for."),
                        direction = StrDefault("'callers' (who calls it) or 'callees' (what it calls, default).", "callees"),
                        maxResults = IntDefault("Maximum number of nodes.", 50),
                        timeoutSeconds = IntDefault("Search timeout in seconds.", 180),
                    },
                    required = new[] { "query" },
                },
                annotations = ReadOnly(),
            });

            if (includeGoToDefinition)
            {
                tools.Add(new
                {
                    name = "go_to_definition",
                    description = "Resolves the definition of the symbol at the given source position (file:line:column) using VS's language service and returns the definition location. Has editor side effects: opens the definition file and moves the cursor. Only registered when the user enables it in Tools → Options → VS MCP → Tools (EnableGoToDefinition), so it stays invisible to the agent unless opted in.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            file = Str("Absolute path to the source file."),
                            line = Int("1-based line number."),
                            column = Int("1-based column number."),
                        },
                        required = new[] { "file", "line", "column" },
                    },
                    annotations = ReadOnly(),
                });
            }

            if (includeEvalCsharp)
            {
                tools.Add(new
                {
                    name = "eval_csharp",
                    description = "Executes C# dynamically inside the VS process (Roslyn CSharpCompilation, compiled then invoked via reflection) for live introspection/manipulation of VS internals. Two mutually exclusive inputs: (1) code — an inline snippet (top-level statements + await) for short probes; using directives may appear at the top and are auto-hoisted to the compilation unit, sidestepping the fact that using is illegal inside a method body. (2) filePath — a local code file (any extension, as long as it is text that compiles), read in full and executed; recommended when the eval needs a lot of code, since editing a giant inline script is awkward. The file may contain full using directives and helper types; if its content includes the `__EvalScript` marker it runs in full-file mode, where you provide `public class __EvalScript { public async Task<object> Run(VsMcp.EvalHost h){...} }`, otherwise it is wrapped as a method body. The script reaches VS through injected globals — Package (AsyncPackage) and JTF (JoinableTaskFactory): use `await JTF.SwitchToMainThreadAsync()` to hop to the UI thread, `await Package.GetServiceAsync(typeof(...))` to obtain any VS service, and reflection to read non-public fields. Call `Log(obj)` to emit output and `return` a value at the end. Returns PLAIN TEXT (not JSON) in labeled sections: an Output section with the accumulated Log, and on success a Return-value section carrying the clean JSON of the return value (NOT double-escaped by an outer JSON layer); on compile/run/timeout failure an Error section carries the detail (returned normally, NOT IsError, so the agent can fix the script from it). Arbitrary code Execution — only available when EnableEvalCsharp is on in Tools → Options.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            code = Str("Inline C# snippet (top-level statements + await). Mutually exclusive with filePath."),
                            filePath = Str("Path to a local code file to execute. Mutually exclusive with code."),
                            timeoutSeconds = IntDefault("Execution timeout in seconds; <=0 = no limit.", 30),
                        },
                        required = new string[] { },
                    },
                    annotations = Destructive(),
                });
            }

            return tools;
        }
    }
}
