using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsMcp
{
    /// <summary>
    /// 工具执行器：按工具名分派到各 facade 方法，返回 <see cref="ToolResult"/>。
    /// 取代已删除的 McpRequestProcessor——VS Package 不再持有 MCP server，
    /// Gateway 发来 tool-call 帧，PipeMcpServer 调本类执行后回 tool-result 帧。
    ///
    /// 方法体从 McpRequestProcessor 搬过来，但：
    /// - 去掉 McpServer 参数（keep-alive 通知通过 onProgress 委托回调）。
    /// - 参数从 JsonElement args 读取（args.TryGetProperty）。
    /// </summary>
    public sealed class ToolExecutor
    {
        private readonly DebuggerFacade? _facade;
        private readonly SymbolFacade? _symbolFacade;
#if EVAL_CSHARP
        private readonly EvalCsharpFacade? _evalFacade;
#endif
        private readonly bool _enableGoToDefinition;
#if EVAL_CSHARP
        private readonly bool _enableEvalCsharp;
#endif

        public ToolExecutor(
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
            // 注入工具回复日志：SafeCall.Wrap 每次返回前把 ToolResult 文本打到
            // VS Output "VS MCP" 面板，便于即时观察 agent 实际收到的回复。
            SafeCall.ReplyLogger = loggerFactory?.CreateLogger("McpReply");
        }

        /// <summary>
        /// 按工具名分派执行。args 是 MCP params.arguments 的 JsonElement
        ///（Gateway 透传 GetRawText()，VS 端 JsonDocument.Parse 后传入）。
        /// onProgress 为非 null 时，长工具执行期间周期调用它推送进度文本（如 build log）。
        /// 返回 ToolResult（Content 是文本/JSON，IsError 标记错误）。
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            string toolName,
            JsonElement args,
            Func<string, CancellationToken, Task>? onProgress,
            CancellationToken ct)
        {
            try
            {
                switch (toolName)
                {
                    // ─────────── 调试器工具 ───────────
                    case "get_debugger_state":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.GetDebuggerStateAsync(ct), ct).ConfigureAwait(false);

                    case "get_session_info":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.GetSessionInfoAsync(ct), ct).ConfigureAwait(false);

                    case "search_project":
                        if (_facade == null) goto NotFound;
                        {
                            string? query = GetOptionalString(args, "query");
                            return await SafeCall.Wrap(() => _facade.SearchProjectsAsync(query, ct), ct).ConfigureAwait(false);
                        }

                    case "start_debugging":
                        if (_facade == null) goto NotFound;
                        {
                            string startProgram = GetRequiredString(args, "startProgram");
                            string? startArguments = GetOptionalString(args, "startArguments");
                            string? environmentVariablesJson = GetOptionalString(args, "environmentVariablesJson");
                            string? workingDirectory = GetOptionalString(args, "workingDirectory");
                            string? debugEngine = GetOptionalString(args, "debugEngine");
                            return await SafeCall.Wrap(() => _facade.StartDebuggingAsync(
                                startProgram, startArguments, environmentVariablesJson, workingDirectory, debugEngine, ct), ct).ConfigureAwait(false);
                        }

                    case "set_breakpoint":
                        if (_facade == null) goto NotFound;
                        {
                            string file = GetRequiredString(args, "file");
                            int line = GetRequiredInt(args, "line");
                            return await SafeCall.Wrap(() => _facade.SetBreakpointAsync(file, line, ct), ct).ConfigureAwait(false);
                        }

                    case "list_breakpoints":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.ListBreakpointsAsync(ct), ct).ConfigureAwait(false);

                    case "delete_breakpoint":
                        if (_facade == null) goto NotFound;
                        {
                            string file = GetRequiredString(args, "file");
                            int line = GetRequiredInt(args, "line");
                            return await SafeCall.Wrap(() => _facade.DeleteBreakpointAsync(file, line, ct), ct).ConfigureAwait(false);
                        }

                    case "clear_all_breakpoints":
                        if (_facade == null) goto NotFound;
                        {
                            bool confirm = GetOptionalBool(args, "confirm", false);
                            return await SafeCall.Wrap(() => _facade.ClearAllBreakpointsAsync(confirm, ct), ct).ConfigureAwait(false);
                        }

                    case "build_solution":
                        if (_facade == null) goto NotFound;
                        return onProgress != null
                            ? await RunBuildWithKeepAliveAsync(onProgress, ct).ConfigureAwait(false)
                            : await SafeCall.Wrap(() => _facade.BuildSolutionAsync(ct), ct).ConfigureAwait(false);

                    case "get_build_output":
                        if (_facade == null) goto NotFound;
                        {
                            int maxLines = GetOptionalInt(args, "maxLines", 200);
                            bool tail = GetOptionalBool(args, "tail", true);
                            return await SafeCall.WrapText(() => _facade.GetBuildOutputAsTextAsync(maxLines > 0 ? maxLines : 200, tail, ct), ct).ConfigureAwait(false);
                        }

                    case "continue_execution":
                        if (_facade == null) goto NotFound;
                        {
                            bool waitForBreak = GetOptionalBool(args, "waitForBreak", false);
                            int timeoutSeconds = GetOptionalInt(args, "timeoutSeconds", 0);
                            if (waitForBreak && onProgress != null)
                                return await RunContinueWithKeepAliveAsync(timeoutSeconds, onProgress, ct).ConfigureAwait(false);
                            return await SafeCall.Wrap(() => _facade.ContinueExecutionAsync(waitForBreak, timeoutSeconds, ct), ct).ConfigureAwait(false);
                        }

                    case "step_into":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.StepIntoAsync(ct), ct).ConfigureAwait(false);

                    case "step_over":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.StepOverAsync(ct), ct).ConfigureAwait(false);

                    case "step_out":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.StepOutAsync(ct), ct).ConfigureAwait(false);

                    case "stop_debugging":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.Wrap(() => _facade.StopDebuggingAsync(ct), ct).ConfigureAwait(false);

                    case "list_local_variables":
                        if (_facade == null) goto NotFound;
                        {
                            int maxLocals = GetOptionalInt(args, "maxLocals", 200);
                            int maxChars = GetOptionalInt(args, "maxChars", 4096);
                            return await SafeCall.WrapText(() => _facade.ListLocalVariablesAsTextAsync(maxLocals, maxChars, ct), ct).ConfigureAwait(false);
                        }

                    case "get_call_stack":
                        if (_facade == null) goto NotFound;
                        return await SafeCall.WrapText(() => _facade.GetCallStackAsTextAsync(ct), ct).ConfigureAwait(false);

                    case "evaluate_expression":
                        if (_facade == null) goto NotFound;
                        {
                            string expression = GetRequiredString(args, "expression");
                            int maxChars = GetOptionalInt(args, "maxChars", 1024);
                            return await SafeCall.WrapText(() => _facade.EvaluateExpressionAsTextAsync(expression, maxChars > 0 ? maxChars : 1024, ct), ct).ConfigureAwait(false);
                        }

                    case "get_variable_detail":
                        if (_facade == null) goto NotFound;
                        {
                            string[] expressions = GetStringArray(args, "expressions");
                            int depth = GetOptionalInt(args, "depth", 1);
                            int maxChars = GetOptionalInt(args, "maxChars", 4096);
                            return await SafeCall.Wrap(() => _facade.GetVariableDetailAsync(expressions, depth, maxChars, ct), ct).ConfigureAwait(false);
                        }

                    // ─────────── 符号工具 ───────────
                    case "find_symbol":
                        if (_symbolFacade == null) goto NotFound;
                        {
                            string query = GetRequiredString(args, "query");
                            int maxResults = GetOptionalInt(args, "maxResults", 16);
                            int maxChars = GetOptionalInt(args, "maxChars", 8000);
                            int contextLines = GetOptionalInt(args, "contextLines", 5);
                            string? language = GetOptionalString(args, "language");
                            string? kind = GetOptionalString(args, "kind");
                            try
                            {
                                string text = await _symbolFacade.FindSymbolAsync(
                                    query,
                                    maxResults > 0 ? maxResults : 16,
                                    maxChars > 0 ? maxChars : 8000,
                                    contextLines > 0 ? contextLines : 5,
                                    language, kind, ct).ConfigureAwait(false);
                                return SafeCall.LogReply(new ToolResult(text, false));
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                SymbolFacade.ProbeLog($"find_symbol failed: {ex.GetType().Name}: {ex.Message}");
                                var err = new ErrorResult("internal_error", ex.Message);
                                return SafeCall.LogReply(new ToolResult(
                                    JsonSerializer.Serialize(err, SafeCall.ReadableOptions), true));
                            }
                        }

                    case "get_type_hierarchy":
                        if (_symbolFacade == null) goto NotFound;
                        {
                            string typeName = GetRequiredString(args, "typeName");
                            return await SafeCall.Wrap(() => _symbolFacade.GetTypeHierarchyAsync(typeName, ct), ct).ConfigureAwait(false);
                        }

                    case "get_call_graph":
                        if (_symbolFacade == null) goto NotFound;
                        {
                            string query = GetRequiredString(args, "query");
                            string direction = GetOptionalString(args, "direction") ?? "callees";
                            int maxResults = GetOptionalInt(args, "maxResults", 50);
                            int timeoutSeconds = GetOptionalInt(args, "timeoutSeconds", 180);
                            if (onProgress != null && direction == "callers")
                                return await RunCallGraphWithKeepAliveAsync(query, direction, maxResults, timeoutSeconds, onProgress, ct).ConfigureAwait(false);
                            return await SafeCall.Wrap(() => _symbolFacade.GetCallGraphAsync(query, direction, maxResults, timeoutSeconds, ct), ct).ConfigureAwait(false);
                        }

                    case "go_to_definition":
                        if (!_enableGoToDefinition || _symbolFacade == null)
                            return ToolNotAvailable("go_to_definition", "enable it in Tools → Options → VS MCP → Tools (EnableGoToDefinition).");
                        {
                            string file = GetRequiredString(args, "file");
                            int line = GetRequiredInt(args, "line");
                            int column = GetRequiredInt(args, "column");
                            return await SafeCall.Wrap(() => _symbolFacade.GoToDefinitionAsync(file, line, column, ct), ct).ConfigureAwait(false);
                        }

#if EVAL_CSHARP
                    case "eval_csharp":
                        if (!_enableEvalCsharp || _evalFacade == null)
                            return ToolNotAvailable("eval_csharp", "enable it in Tools → Options → VS MCP → Tools (EnableEvalCsharp).");
                        {
                            string? code = GetOptionalString(args, "code");
                            string? filePath = GetOptionalString(args, "filePath");
                            int timeoutSeconds = GetOptionalInt(args, "timeoutSeconds", 30);
                            return await SafeCall.WrapText(() => _evalFacade.EvalAsTextAsync(code, filePath, timeoutSeconds, ct), ct).ConfigureAwait(false);
                        }
#else
                    case "eval_csharp":
                        return ToolNotAvailable("eval_csharp", "not compiled into this build (EVAL_CSHARP is off).");
#endif

                    default:
                        return ToolNotAvailable(toolName, "unknown tool name.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 兜底：ToolExecutor 自身（非 facade）的异常也变成错误 ToolResult，
                // 绝不让未处理异常崩溃 pipe 读循环。
                var err = new ErrorResult("internal_error", ex.Message);
                return new ToolResult(JsonSerializer.Serialize(err, SafeCall.ReadableOptions), true);
            }

        NotFound:
            return ToolNotAvailable(toolName, "the required facade is not initialized.");
        }

        /// <summary>
        /// 带 KeepAliveNotifier 的 build_solution：周期读取增量 build log
        /// 并通过 onProgress 推送给客户端。
        /// </summary>
        private async Task<ToolResult> RunBuildWithKeepAliveAsync(
            Func<string, CancellationToken, Task> onProgress,
            CancellationToken ct)
        {
            if (_facade == null) return ToolNotAvailable("build_solution", "facade not initialized.");

            int nextLine = 0;
            using var notifier = new KeepAliveNotifier(
                async token =>
                {
                    var delta = await _facade.GetBuildOutputDeltaAsync(nextLine, token).ConfigureAwait(false);
                    nextLine = delta.TotalLines;
                    foreach (var line in delta.NewLines)
                        await onProgress(line, token).ConfigureAwait(false);
                },
                TimeSpan.FromSeconds(2),
                ct);

            return await SafeCall.Wrap(() => _facade.BuildSolutionAsync(ct), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 带 KeepAliveNotifier 的 continue_execution（waitForBreak=true）：
        /// 周期发送保活心跳。
        /// </summary>
        private async Task<ToolResult> RunContinueWithKeepAliveAsync(
            int timeoutSeconds,
            Func<string, CancellationToken, Task> onProgress,
            CancellationToken ct)
        {
            if (_facade == null) return ToolNotAvailable("continue_execution", "facade not initialized.");

            using var notifier = new KeepAliveNotifier(
                token => onProgress("Continuing execution...", token),
                TimeSpan.FromSeconds(10),
                ct);

            return await SafeCall.Wrap(() => _facade.ContinueExecutionAsync(waitForBreak: true, timeoutSeconds, ct), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 带 KeepAliveNotifier 的 get_call_graph（callers 反向搜索）：
        /// 周期发送保活心跳。
        /// </summary>
        private async Task<ToolResult> RunCallGraphWithKeepAliveAsync(
            string query,
            string direction,
            int maxResults,
            int timeoutSeconds,
            Func<string, CancellationToken, Task> onProgress,
            CancellationToken ct)
        {
            if (_symbolFacade == null) return ToolNotAvailable("get_call_graph", "symbol facade not initialized.");

            using var notifier = new KeepAliveNotifier(
                token => onProgress("Searching call graph...", token),
                TimeSpan.FromSeconds(10),
                ct);

            return await SafeCall.Wrap(() => _symbolFacade.GetCallGraphAsync(query, direction, maxResults, timeoutSeconds, ct), ct).ConfigureAwait(false);
        }

        /// <summary>构造"工具不可用"错误 ToolResult。</summary>
        private static ToolResult ToolNotAvailable(string toolName, string reason)
        {
            var err = new ErrorResult("tool_not_available", $"{toolName} is not available: {reason}");
            return new ToolResult(JsonSerializer.Serialize(err, SafeCall.ReadableOptions), true);
        }

        // ─────────────────── 参数读取辅助 ───────────────────

        private static string? GetOptionalString(JsonElement args, string name)
        {
            if (args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
            return null;
        }

        private static string GetRequiredString(JsonElement args, string name)
        {
            if (args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString() ?? "";
            return "";
        }

        private static int GetRequiredInt(JsonElement args, string name)
        {
            if (args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v))
                return v;
            return 0;
        }

        private static int GetOptionalInt(JsonElement args, string name, int defaultValue)
        {
            if (args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v))
                return v;
            return defaultValue;
        }

        private static bool GetOptionalBool(JsonElement args, string name, bool defaultValue)
        {
            if (args.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
                return p.GetBoolean();
            return defaultValue;
        }

        private static string[] GetStringArray(JsonElement args, string name)
        {
            if (args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString() ?? "");
                }
                return list.ToArray();
            }
            return Array.Empty<string>();
        }
    }
}
