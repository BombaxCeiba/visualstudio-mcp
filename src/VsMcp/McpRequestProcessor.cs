using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace VsMcp
{
    /// <summary>
    /// 拥有 MCP SDK 资源（<see cref="StreamableHttpServerTransport"/> +
    /// <see cref="McpServer"/>）和全部工具注册逻辑，与具体的传输监听方式
    /// （当前为 NamedPipe <see cref="PipeMcpServer"/>；多实例架构下 VS 端不再
    /// 直接监听 HTTP，而由独立 Gateway 进程独占端口）解耦。
    ///
    /// 任一监听端收到一条 JSON-RPC 消息后，调用 <see cref="HandleAsync"/> 把 SDK
    /// 产生的 SSE 响应字节写到它提供的输出流上 —— HTTP 端是
    /// <c>HttpListenerResponse.OutputStream</c>，pipe 端是一个把每次写入实时封帧
    /// 回传 Gateway 的 <see cref="Stream"/>。两种监听端因此共享完全相同的工具集、
    /// <see cref="SafeCall"/> 错误边界与 <see cref="KeepAliveNotifier"/> 保活语义。
    /// </summary>
    public sealed class McpRequestProcessor : IAsyncDisposable, IDisposable
    {
        private readonly StreamableHttpServerTransport _transport;
        private readonly McpServer _server;
        private readonly Task _serverRunTask;
        private readonly DebuggerFacade? _facade;
        private readonly SymbolFacade? _symbolFacade;
#if EVAL_CSHARP
        private readonly EvalCsharpFacade? _evalFacade;
#endif
        private readonly bool _enableGoToDefinition;
#if EVAL_CSHARP
        private readonly bool _enableEvalCsharp;
#endif
        private readonly ILoggerFactory? _loggerFactory;

        // D-01: 处理器拥有自己的 linked CTS —— 取消调用方 token（VS package
        // DisposalToken）或处理器自身 DisposeAsync 任一，都会传播到 RunAsync。
        private readonly CancellationTokenSource _externalCts;

        private readonly SemaphoreSlim _disposeLock = new(1, 1);
        private bool _disposed;

        /// <param name="callerToken">监听端的停机 token（VS package DisposalToken）。
        /// 取消时传播到 SDK RunAsync 循环。</param>
        /// <param name="facade">调试器 facade；为 null 则不注册调试工具。</param>
        /// <param name="symbolFacade">符号 facade；为 null 则不注册符号工具。</param>
#if EVAL_CSHARP
        /// <param name="evalFacade">eval_csharp facade；为 null 或 <paramref name="enableEvalCsharp"/>=false 则不注册。</param>
#endif
        /// <param name="enableGoToDefinition">是否注册 go_to_definition（有副作用，opt-in）。</param>
#if EVAL_CSHARP
        /// <param name="enableEvalCsharp">是否注册 eval_csharp（任意代码执行，opt-in）。</param>
#endif
        /// <param name="loggerFactory">可选日志工厂。</param>
        public McpRequestProcessor(
            CancellationToken callerToken,
            DebuggerFacade? facade,
            SymbolFacade? symbolFacade,
#if EVAL_CSHARP
            EvalCsharpFacade? evalFacade,
#endif
            bool enableGoToDefinition,
#if EVAL_CSHARP
            bool enableEvalCsharp,
#endif
            ILoggerFactory? loggerFactory)
        {
            _facade = facade;
            _symbolFacade = symbolFacade;
#if EVAL_CSHARP
            _evalFacade = evalFacade;
#endif
            _enableGoToDefinition = enableGoToDefinition;
#if EVAL_CSHARP
            _enableEvalCsharp = enableEvalCsharp;
#endif
            _loggerFactory = loggerFactory;

            _transport = new StreamableHttpServerTransport(loggerFactory);
            _externalCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

            _server = McpServer.Create(
                _transport,
                new McpServerOptions
                {
                    ToolCollection = BuildToolCollection()
                },
                loggerFactory);

            // 启动 SDK 消息处理循环（从 transport 的 MessageReader 读取）。用处理器
            // 自有的 linked token，DisposeAsync 可独立于原 caller token 停止循环。
            _serverRunTask = _server.RunAsync(_externalCts.Token);
        }

        /// <summary>
        /// 处理一条 JSON-RPC 消息，把 SSE 响应帧实时写入 <paramref name="outputStream"/>。
        /// 返回 SDK 是否写出了响应（通知类消息不产生响应）。
        /// </summary>
        public Task<bool> HandleAsync(JsonRpcMessage message, Stream outputStream, CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(McpRequestProcessor));
            return _transport.HandlePostRequestAsync(message, outputStream, cancellationToken);
        }

        private McpServerPrimitiveCollection<McpServerTool> BuildToolCollection()
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>();

            if (_facade != null)
            {
                // F-6 / D-10：每个调用 facade 的 lambda 都经 SafeCall.Wrap 包装，
                // 这样 facade 抛出的异常会变成 CallToolResult{IsError=true} +
                // ErrorResult (D-12)。Name/Description/ReadOnly/Destructive 标志
                // 以及 delegate 的输入参数保持不变，使 MCP SDK 派生出的输入
                // schema 在字节级保持完全一致。
                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.GetDebuggerStateAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_debugger_state",
                        Description = "Returns the current debugger mode:\n" +
                                      "- design:  no debug session active\n" +
                                      "- break:   paused at a breakpoint; can read variables / call stack / step\n" +
                                      "- running: executing; cannot inspect until next break\n\n" +
                                      "Typically the first call in a debug workflow to determine which tools are available.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.GetSessionInfoAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_session_info",
                        Description = "Returns the solution currently loaded in Visual Studio (path, directory, project list), a best-effort debug target, and the current debugger state. Fields are null/empty if no solution is loaded.\n\n" +
                                      "Typically the first call in a debug workflow — confirms VS is operating on the expected project tree before set_breakpoint / start_debugging.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    SearchProjectToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "search_project",
                        Description = "Enumerates projects in the loaded Visual Studio solution (recursing Solution Folders) and returns each project's Name, UniqueName, FullName (.csproj/.vcxproj path), Kind (project-type GUID), whether it is the startup project, and a best-effort OutputTarget (built executable path). Use this before start_debugging to locate the project owning the executable you want to launch and read back its output path. Optional 'query' is a case-insensitive substring filter on Name/UniqueName/FullName; omit to list every project.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    StartDebuggingToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "start_debugging",
                        Description = "Launches an executable under the Visual Studio debugger. Takes an absolute exe path (startProgram, required) plus optional arguments, environment variables (as a JSON object string like {\"KEY\":\"VALUE\"}), working directory, and debugEngine selector.\n\n" +
                                      "Does NOT touch project configuration, launchSettings.json, or vcxproj files — params are used for this single launch only and nothing is persisted.\n\n" +
                                      "C++ targets default to the native debug engine; pass debugEngine=\"managed\" for .NET/CLR debugging, or a raw debug-engine GUID string for any other engine. The executable must already be built (call build_solution first if unsure).",
                        Destructive = true
                    }));

                tools.Add(McpServerTool.Create(
                    (string file, int line, CancellationToken ct) => SafeCall.Wrap(() => _facade.SetBreakpointAsync(file, line, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "set_breakpoint",
                        Description = "Sets a breakpoint at the specified file path and line number. The file path must be absolute and part of the currently loaded solution (returns file_not_in_solution otherwise — call get_session_info / search_project first to discover valid paths)."
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.ListBreakpointsAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "list_breakpoints",
                        Description = "Lists all current breakpoints with file, line, condition, and enabled status.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    (string file, int line, CancellationToken ct) => SafeCall.Wrap(() => _facade.DeleteBreakpointAsync(file, line, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "delete_breakpoint",
                        Description = "Deletes the breakpoint at the specified file path and line number. Deletion is immediate (no confirmation dialog). Returns breakpoint_not_found error if no breakpoint exists at the given location.",
                        Destructive = true
                    }));

                tools.Add(McpServerTool.Create(
                    ClearAllBreakpointsToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "clear_all_breakpoints",
                        Description = "Clears all breakpoints. Two-step guard: confirm=false (default) is a dry-run that reports the count without deleting; pass confirm=true to actually delete after reviewing the count.",
                        Destructive = true
                    }));

                tools.Add(McpServerTool.Create(
                    (McpServer server, CancellationToken ct) => SafeCall.Wrap(() => RunBuildWithKeepAliveAsync(server, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "build_solution",
                        Description = "Triggers a full solution build and waits for it to complete. Long-running — safe for multi-minute builds: while building, the server streams incremental build-log lines as logging notifications (visible in the client's debug panel, NOT injected into the model context) so the tool-call never times out.\n\n" +
                                      "Returns the active build configuration name and the failed-project count (0 means success). Use after editing C++/C# code to verify it compiles before running or debugging. Follow with get_build_output to read the full compiler diagnostics."
                    }));

                tools.Add(McpServerTool.Create(
                    GetBuildOutputToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_build_output",
                        Description = "Reads the VS Output window's Build pane and returns a sliced view of the build log. Authoritative source for compiler diagnostics (the in-IDE error list may include stale squiggles from incomplete IntelliSense). maxLines caps the returned line count (default 200); tail=true (default) reads the most recent lines where errors usually appear, tail=false reads from the top.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    ContinueExecutionToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "continue_execution",
                        Description = "Continues execution from the current breakpoint. Only works when debugger is in break mode.\n\n" +
                                      "wait_for_break (default=false):\n" +
                                      "  false — fire-and-forget; returns immediately with state=\"running\".\n" +
                                      "  true  — waits for the next breakpoint hit OR debuggee exit. While waiting, the server emits periodic logging notifications (visible in the client's debug panel, NOT the model context) to keep the tool-call alive.\n\n" +
                                      "timeout_seconds (only with wait_for_break=true; default=0):\n" +
                                      "  0    — wait indefinitely (logging keep-alive; safe — agent disconnect, server shutdown, and debuggee exit all unblock it).\n" +
                                      "  N>0  — wait at most N seconds; on timeout returns the current state with a message (NOT an error, agent may retry)."
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.StepIntoAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "step_into",
                        Description = "Steps into the next statement. Waits up to ~5s for break-mode re-entry (polls internally), then returns the new state. Prerequisite: break mode (returns not_in_break_mode otherwise)."
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.StepOverAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "step_over",
                        Description = "Steps over the next statement. Waits up to ~5s for break-mode re-entry (polls internally), then returns the new state. Prerequisite: break mode (returns not_in_break_mode otherwise)."
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.StepOutAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "step_out",
                        Description = "Steps out of the current function. Waits up to ~5s for break-mode re-entry (polls internally), then returns the new state. Prerequisite: break mode (returns not_in_break_mode otherwise)."
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.StopDebuggingAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "stop_debugging",
                        Description = "Stops the current debugging session. Immediate (no confirmation dialog). Works in break or running mode. In design mode (no active session), returns internal_error rather than no-op — call get_debugger_state first to check the mode.",
                        Destructive = true
                    }));

                tools.Add(McpServerTool.Create(
                    ListLocalVariablesToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "list_local_variables",
                        Description = "Lists all local variables at the current stack frame, SHALLOW: each entry shows name/type/value/hasChildren only, children are NOT expanded. Use get_variable_detail to drill into any with hasChildren=true. maxLocals (default 200) and maxChars (default 4096) cap the output so a frame with many/deep locals can't blow up the context. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    (CancellationToken ct) => SafeCall.Wrap(() => _facade.GetCallStackAsync(ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_call_stack",
                        Description = "Returns the call stack with function names, module names, and return types. At most 50 frames (deepest frames dropped if the stack is deeper). Prerequisite: break mode (returns not_in_break_mode otherwise).",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    EvaluateExpressionToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "evaluate_expression",
                        Description = "Evaluates an arbitrary expression (e.g., obj.Property.Method()) and returns the result with type and value. Expression syntax follows the active debug engine: C#-like for managed, C/C++-like for native. May modify state if the expression has side effects. 5-second timeout. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                        Destructive = true
                    }));

                tools.Add(McpServerTool.Create(
                    GetVariableDetailToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_variable_detail",
                        Description = "Expands one or more variables/expressions to a limited depth, tree-style. 'expressions' is an array of debugger expressions — each can be a local name OR any C++/C# expression (obj.member, arr[3], ptr->next), evaluated by the debug engine. 'depth' (default 1) controls how many layers of children are expanded; deeper layers stay hasChildren=true for follow-up calls, keeping each response bounded. Use after list_local_variables to drill into hasChildren=true entries. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                        ReadOnly = true
                    }));
            }

            if (_symbolFacade != null)
            {
                // find_symbol 注册到具名方法（而非 lambda）上，这样 maxResults 就能
                // 带 C# 默认值 —— MCP SDK 通过反射方法的 ParameterInfo.HasDefaultValue
                // 在 schema 中将其标为可选。lambda 的参数无法声明默认值，因此会被
                // 强制设为必填（早先的 int? 尝试已证明这点）。
                tools.Add(McpServerTool.Create(
                    FindSymbolToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "find_symbol",
                        Description = "Searches symbols across every language (C#, VB, C++) via NavigateTo — VS's 'Go To All' (Ctrl+T) backend — so accuracy matches in-IDE symbol search, including for C++ (served by the VC Language Server over LSP workspace/symbol). Returns the most relevant matches: default 16, capped by the optional maxResults. Each match carries source file path, 1-based line, and column when the provider reports it, plus the symbol kind and language. Far more accurate than grep because it uses the language services' semantic model, not text matching.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    (string typeName, CancellationToken ct) => SafeCall.Wrap(() => _symbolFacade.GetTypeHierarchyAsync(typeName, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_type_hierarchy",
                        Description = "Returns a type's position in the solution's type graph: full ancestor chain (to root), direct descendants (who derives from / implements this type across the whole solution), and siblings (other types sharing a base). VS-exclusive — reverse inheritance requires indexing every type in the solution, which reading source files cannot replicate. Use for impact analysis before modifying a class/interface. Found=false if no type matches.",
                        ReadOnly = true
                    }));

                if (_enableGoToDefinition)
                {
                    tools.Add(McpServerTool.Create(
                        (string file, int line, int column, CancellationToken ct) => SafeCall.Wrap(() => _symbolFacade.GoToDefinitionAsync(file, line, column, ct), ct),
                        new McpServerToolCreateOptions
                        {
                            Name = "go_to_definition",
                            Description = "Resolves the definition of the symbol at the given source position (file:line:column) using VS's language service and returns the definition location. Has editor side effects: opens the definition file and moves the cursor. Only registered when the user enables it in Tools → Options → Visual Studio MCP → Tools (EnableGoToDefinition), so it stays invisible to the agent unless opted in.",
                            ReadOnly = true
                        }));
                }
            }

#if EVAL_CSHARP
            if (_enableEvalCsharp && _evalFacade != null)
            {
                // eval_csharp：VS 进程内动态执行 C#（Roslyn scripting）。装一次扩展后
                // 零编译探查 VS 内部状态，把过去"改探查点→编 vsix→卸载→装→重启→读日志"
                // 的数分钟循环压成一次 MCP 调用。任意代码执行，安全敏感 —— 仅 Tools→Options
                // 开启 EnableEvalCsharp 时注册（默认关，对 agent 不可见）。
                tools.Add(McpServerTool.Create(
                    EvalCsharpToolAsync,
                    new McpServerToolCreateOptions
                    {
                        Name = "eval_csharp",
                        Description = "在 VS 进程内用 Roslyn scripting 动态执行一段 C# 脚本（顶层语句 + await），用于实时探查/操作 VS 内部状态。脚本经注入的 globals 访问 VS：Package(AsyncPackage)、JTF(JoinableTaskFactory) —— 用 `await JTF.SwitchToMainThreadAsync()` 切 UI 线程，用 `await Package.GetServiceAsync(typeof(...))` 拿任意 VS 服务，可反射读非 public 字段。用 `Log(obj)` 记录输出；末尾 `return` 一个值。返回 Output(Log 累计) + ResultJson(return 值的 JSON) + Error(编译/运行/超时错误，非 null 表示失败)。任意代码执行，仅在 Tools→Options 开启 EnableEvalCsharp 时可用。",
                        Destructive = true
                    }));
            }
#endif

            return tools;
        }

        /// <summary>
        /// 下面的工具处理器都是具名方法（而非 lambda），这样它们的“可选”参数就能
        /// 带 C# 默认值 —— MCP SDK 读取 ParameterInfo.HasDefaultValue 来在 schema 中
        /// 把参数标为可选。lambda 的参数无法声明默认值，所以用 lambda 注册工具会把
        /// 每个参数都强制设为必填，与文档声明的默认值相矛盾。CancellationToken（以及
        /// McpServer）由 SDK 注入，不出现在 schema 中。
        /// </summary>
#pragma warning disable VSTHRD200 // 作为工具处理器交给 MCP SDK；从不按名字 await
        public async Task<object> FindSymbolToolAsync(string query, int maxResults = 16, int maxChars = 8000, CancellationToken ct = default)
        {
            try
            {
                string text = await _symbolFacade!.FindSymbolAsync(query, maxResults > 0 ? maxResults : 16, maxChars > 0 ? maxChars : 8000, ct).ConfigureAwait(false);
                // 纯文本输出（不经 JSON 序列化）：find_symbol 返回的是带行号的源码上下文，
                // JSON 包裹既费 token 又把换行转义成 \n。手动构造 CallToolResult 直接透传文本。
                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SymbolFacade.ProbeLog($"find_symbol failed: {ex.GetType().Name}: {ex.Message}");
                return new ErrorResult("internal_error", ex.Message).ToCallToolResult();
            }
        }

        public Task<object> SearchProjectToolAsync(string? query = null, CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.SearchProjectsAsync(query, ct), ct);

        public Task<object> StartDebuggingToolAsync(
            string startProgram,
            string? startArguments = null,
            string? environmentVariablesJson = null,
            string? workingDirectory = null,
            string? debugEngine = null,
            CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.StartDebuggingAsync(
                startProgram, startArguments, environmentVariablesJson, workingDirectory, debugEngine, ct), ct);

        public Task<object> ClearAllBreakpointsToolAsync(bool confirm = false, CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.ClearAllBreakpointsAsync(confirm, ct), ct);

        public Task<object> GetBuildOutputToolAsync(int maxLines = 200, bool tail = true, CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.GetBuildOutputAsync(maxLines > 0 ? maxLines : 200, tail, ct), ct);

        // McpServer（同 CancellationToken 一样）由 SDK 注入，不在 schema 中；
        // 真正的可选 schema 参数是 waitForBreak / timeoutSeconds。server 必须排在
        // 它们前面，因为它没有默认值。
        public Task<object> ContinueExecutionToolAsync(McpServer server, bool waitForBreak = false, int timeoutSeconds = 0, CancellationToken ct = default)
            => SafeCall.Wrap(() => RunContinueWithKeepAliveAsync(waitForBreak, timeoutSeconds, server, ct), ct);

        // list_local_variables：浅列当前栈帧所有 local（只顶层 name/type/value/hasChildren，
        // 不展开 children）。maxLocals/maxChars 双保险防 local 极多爆上下文。
        public Task<object> ListLocalVariablesToolAsync(int maxLocals = 200, int maxChars = 4096, CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.ListLocalVariablesAsync(maxLocals, maxChars, ct), ct);

        public Task<object> EvaluateExpressionToolAsync(string expression, int maxChars = 1024, CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.EvaluateExpressionAsync(expression, maxChars > 0 ? maxChars : 1024, ct), ct);

#if EVAL_CSHARP
        // eval_csharp：动态执行 C# 脚本。timeoutSeconds 默认 30（防死循环；<=0 不限）。
        // 经 SafeCall.Wrap —— 成功路径 EvalCsharpResult 走 McpJson.ReadableOptions 序列化
        // （中文不转义）。脚本编译/运行/超时错误封装进 EvalCsharpResult.Error 正常返回
        // （非 IsError），把详情透给 agent 而非笼统 internal_error；仅 VS 关停级取消透明重抛。
        public Task<object> EvalCsharpToolAsync(string code, int timeoutSeconds = 30, CancellationToken ct = default)
            => SafeCall.Wrap(() => _evalFacade!.EvalAsync(code, timeoutSeconds, ct), ct);
#endif

        // get_variable_detail：按一组调试器表达式批量取变量详情，每个展开 depth 层。
        // expressions 元素是任意 C++/C# 表达式（local 名、obj.member、arr[3]、ptr->next），
        // 由调试引擎求值，工具里不造路径语法。depth=1 默认只展开直接 children，防爆上下文。
        public Task<object> GetVariableDetailToolAsync(string[] expressions, int depth = 1, int maxChars = 4096, CancellationToken ct = default)
            => SafeCall.Wrap(() => _facade!.GetVariableDetailAsync(expressions, depth, maxChars, ct), ct);
#pragma warning restore VSTHRD200

        /// <summary>
        /// build_solution 的保活包装：构建期间每 2 秒把 Build pane 的新增日志行
        /// 通过 logging 通知转发给客户端（不进模型上下文），无新增时发心跳。
        /// 既保活（重置客户端工具调用超时）又让调试面板可见真实进度。
        /// </summary>
        private async Task<BuildSolutionResult> RunBuildWithKeepAliveAsync(McpServer server, CancellationToken ct)
        {
            var facade = _facade ?? throw new InvalidOperationException("Debugger facade is not available");
            int nextLine = 0;
            using var keepAlive = new KeepAliveNotifier(
                sendKeepAlive: async token =>
                {
                    var delta = await facade.GetBuildOutputDeltaAsync(nextLine, token).ConfigureAwait(false);
                    nextLine = delta.TotalLines;
                    string message = delta.NewLines.Count > 0
                        ? string.Join("\n", delta.NewLines)
                        : "Building solution...";
                    await SendLogNotificationAsync(server, "vs-debugger:build", message, token).ConfigureAwait(false);
                },
                interval: TimeSpan.FromSeconds(2),
                cancellationToken: ct);

            return await facade.BuildSolutionAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// continue_execution 的保活包装：仅 wait_for_break=true（等待模式）时，
        /// 每 10 秒发一条 logging 心跳通知，重置客户端工具调用超时。
        /// wait_for_break=false 立即返回，无需保活。
        /// </summary>
        private async Task<ExecutionResult> RunContinueWithKeepAliveAsync(
            bool waitForBreak, int timeoutSeconds, McpServer server, CancellationToken ct)
        {
            var facade = _facade ?? throw new InvalidOperationException("Debugger facade is not available");

            if (!waitForBreak)
                return await facade.ContinueExecutionAsync(waitForBreak, timeoutSeconds, ct).ConfigureAwait(false);

            using var keepAlive = new KeepAliveNotifier(
                sendKeepAlive: token => SendLogNotificationAsync(server, "vs-debugger:continue", "Continuing execution...", token),
                interval: TimeSpan.FromSeconds(10),
                cancellationToken: ct);

            return await facade.ContinueExecutionAsync(waitForBreak, timeoutSeconds, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 发送一条 MCP logging 通知（<c>notifications/message</c>）。走响应 SSE 流，
        /// SDK 实时 flush，客户端立即收到并据此重置工具调用超时。日志通知只显示在客户端
        /// 调试面板，不注入模型上下文、不消耗 token。
        /// </summary>
        private static Task SendLogNotificationAsync(McpServer server, string logger, string message, CancellationToken ct)
            => server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = LoggingLevel.Info,
                    Logger = logger,
                    Data = JsonSerializer.SerializeToElement(message),
                },
                cancellationToken: ct);

        /// <summary>
        /// 同步停机入口。翻转 _disposed、取消 CTS，fire-and-forget 异步销毁，
        /// 绝不在调用线程上 sync-over-async（F-3 死锁根因）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _externalCts.Cancel(); } catch (ObjectDisposedException) { }

            _ = DisposeAsyncFireAndForget();
        }

        /// <summary>
        /// 异步停机：cancel CTS → 观察 SDK run 循环 → dispose transport。顺序
        /// 复用自原 HTTP 监听端的防死锁经验（RESEARCH Pitfall #3）。
        /// 幂等（_disposeLock + _disposed）。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            await _disposeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;
                _disposed = true;

                try { _externalCts.Cancel(); } catch (ObjectDisposedException) { }

                if (_serverRunTask != null)
                {
                    // SDK run 循环；可安全取消 —— SDK 在 finally 中自行释放
#pragma warning disable VSTHRD003
                    try { await _serverRunTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { /* 停机期间绝不能抛异常 */ }
#pragma warning restore VSTHRD003
                }

                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _disposeLock.Release();
            }
        }

        /// <summary>
        /// fire-and-forget 包装，供同步 Dispose() 在不阻塞调用线程的前提下触发异步销毁。
        /// 吞掉所有异常以防 unobserved task exception。方法名省略 Async 后缀因为返回值
        /// 被刻意丢弃（VSTHRD200 无法表达，局部 suppress）。
        /// </summary>
#pragma warning disable VSTHRD200
        private async Task DisposeAsyncFireAndForget()
#pragma warning restore VSTHRD200
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 已观察 —— 绝不让 unobserved exception 崩溃进程。
            }
        }
    }
}
