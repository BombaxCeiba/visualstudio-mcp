using System;
using System.IO;
using System.Net;
using System.Text;
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
    /// HTTP server that bridges HttpListener to the MCP SDK's StreamableHttpServerTransport.
    /// Listens for JSON-RPC requests over HTTP and processes them through the MCP server.
    /// Binds to localhost only for security (never 0.0.0.0).
    /// </summary>
    public sealed class McpHttpServer : IAsyncDisposable, IDisposable
    {
        private readonly HttpListener _listener;
        private readonly McpAuthMiddleware _auth;
        private readonly int _port;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly DebuggerFacade? _facade;
        private readonly SymbolFacade? _symbolFacade;
        private readonly bool _enableGoToDefinition;

        private McpServer? _mcpServer;
        private StreamableHttpServerTransport? _transport;
        private Task? _serverRunTask;
        private bool _disposed;

        // D-01: the server owns its own CancellationTokenSource. StartAsync
        // builds a linked CTS from the caller's token so that cancelling
        // EITHER the caller (e.g. package DisposalToken) OR the server's own
        // internal cancel propagates to RunAsync. This is the field the
        // A+B-hybrid teardown in the package cancels.
        private CancellationTokenSource? _externalCts;

        // Guards the DisposeAsync body so concurrent IVsPackage.Close() and
        // Dispose(bool) paths from the package cannot race into the teardown.
        private readonly SemaphoreSlim _disposeLock = new(1, 1);

        // F-4 / D-13: single-session serialization gate. Acquired BEFORE the
        // request body is read in ProcessRequestAsync so that at most one tool
        // call is in flight at a time (v1 single-session contract, D-14).
        // Shutdown cancels the external CTS first; a waiter blocked here
        // observes the cancellation and exits cleanly rather than racing
        // transport disposal (T-05-04-03 / T-05-04-04).
        private readonly SemaphoreSlim _requestGate = new(1, 1);

        // Long-task keep-alive is handled at the tool layer (KeepAliveNotifier),
        // which emits MCP logging notifications over the POST SSE stream rather
        // than SSE comment pings. The SDK flushes each SSE frame immediately and
        // serializes writes internally, so no manual heartbeat is needed here.

        /// <summary>
        /// Creates a new MCP HTTP server.
        /// </summary>
        /// <param name="port">The port to listen on (default 3001).</param>
        /// <param name="authToken">Bearer token for authentication, or null to disable auth (default).</param>
        /// <param name="loggerFactory">Optional logger factory for diagnostics.</param>
        public McpHttpServer(int port = 3001, string? authToken = null, ILoggerFactory? loggerFactory = null, DebuggerFacade? facade = null, SymbolFacade? symbolFacade = null, bool enableGoToDefinition = false)
        {
            _port = port;
            _auth = new McpAuthMiddleware(authToken);
            _loggerFactory = loggerFactory;
            _facade = facade;
            _symbolFacade = symbolFacade;
            _enableGoToDefinition = enableGoToDefinition;
            _listener = new HttpListener();
        }

        /// <summary>
        /// Starts the HTTP listener and MCP server.
        /// The server processes JSON-RPC messages received via HTTP POST requests.
        /// </summary>
        /// <param name="cancellationToken">Token to signal server shutdown.</param>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // D-20 (F-11): bind to explicit IPv4 loopback. Avoids the ambiguity
            // of "localhost" (which can resolve to ::1 on IPv6-only configs
            // and confuse urlacl / firewall state). Never 0.0.0.0 or '+'.
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/mcp/");

            var logger = _loggerFactory?.CreateLogger<McpHttpServer>();

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // D-08: actionable diagnostics. The two common bind failures are
                //   - port already in use (native errorCode 32 / 183)
                //   - missing URL ACL (errorCode 5 AccessDenied) -> needs netsh.
                // The netsh hint uses 127.0.0.1 (matches the new prefix, not
                // the old "localhost" form). Rethrow so the package-level
                // try/catch (VsMcpPackage) also observes the failure.
                logger?.LogError(ex,
                    "Failed to bind MCP HTTP listener on port {Port} (native error {NativeErr}). " +
                    "If 'AccessDenied', run: netsh http add urlacl url=http://127.0.0.1:{Port}/mcp/ user=Everyone",
                    _port, ex.ErrorCode, _port);
                throw;
            }

            // D-05: log the server-start event on success.
            logger?.LogInformation("MCP server listening on http://127.0.0.1:{Port}/mcp", _port);

            // D-01: build the server-owned linked CTS. Cancelled by EITHER the
            // caller's token OR the server's own DisposeAsync. Passed to
            // RunAsync below; the accept loop still uses the raw caller token
            // for its own while/GetContextAsync checks (linked CTS propagates).
            _externalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Create the Streamable HTTP transport
            _transport = new StreamableHttpServerTransport(_loggerFactory);

            // Create the MCP server.
            _mcpServer = McpServer.Create(
                _transport,
                new McpServerOptions
                {
                    ToolCollection = BuildToolCollection()
                },
                _loggerFactory);

            // Start the server's message processing loop (reads from transport's MessageReader).
            // Uses _externalCts.Token (server-owned) so DisposeAsync can stop the SDK loop
            // independently of the original caller token.
            _serverRunTask = _mcpServer.RunAsync(_externalCts.Token);

            // Accept loop: receive HTTP requests and bridge them to the MCP transport
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Process each request on a separate task to allow concurrent handling
                _ = ProcessRequestAsync(context, cancellationToken);
            }
        }

        private McpServerPrimitiveCollection<McpServerTool> BuildToolCollection()
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>();

            if (_facade != null)
            {
                // F-6 / D-10: every facade-calling lambda is wrapped via
                // SafeCall.Wrap so a facade exception becomes a
                // CallToolResult{IsError=true} + ErrorResult (D-12). The
                // Name/Description/ReadOnly/Destructive flags and the
                // delegate input parameters (file, line, maxChars,
                // expression, variableName, ct) are UNCHANGED so the
                // MCP SDK's derived input schemas stay byte-identical.
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
                    (string? query, CancellationToken ct) => SafeCall.Wrap(() => _facade.SearchProjectsAsync(query, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "search_project",
                        Description = "Enumerates projects in the loaded Visual Studio solution (recursing Solution Folders) and returns each project's Name, UniqueName, FullName (.csproj/.vcxproj path), Kind (project-type GUID), whether it is the startup project, and a best-effort OutputTarget (built executable path). Use this before start_debugging to locate the project owning the executable you want to launch and read back its output path. Optional 'query' is a case-insensitive substring filter on Name/UniqueName/FullName; omit to list every project.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    (string startProgram,
                     string? startArguments,
                     string? environmentVariablesJson,
                     string? workingDirectory,
                     string? debugEngine,
                     CancellationToken ct) => SafeCall.Wrap(
                        () => _facade.StartDebuggingAsync(
                            startProgram, startArguments, environmentVariablesJson,
                            workingDirectory, debugEngine, ct),
                        ct),
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
                    (bool confirm, CancellationToken ct) => SafeCall.Wrap(() => _facade.ClearAllBreakpointsAsync(confirm, ct), ct),
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
                    (int maxLines, bool tail, CancellationToken ct) => SafeCall.Wrap(() => _facade.GetBuildOutputAsync(maxLines > 0 ? maxLines : 200, tail, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_build_output",
                        Description = "Reads the VS Output window's Build pane and returns a sliced view of the build log. Authoritative source for compiler diagnostics (the in-IDE error list may include stale squiggles from incomplete IntelliSense). maxLines caps the returned line count (default 200); tail=true (default) reads the most recent lines where errors usually appear, tail=false reads from the top.",
                        ReadOnly = true
                    }));

                tools.Add(McpServerTool.Create(
                    (bool waitForBreak, int timeoutSeconds, McpServer server, CancellationToken ct) => SafeCall.Wrap(
                        () => RunContinueWithKeepAliveAsync(waitForBreak, timeoutSeconds, server, ct), ct),
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
                    (int maxChars, CancellationToken ct) => SafeCall.Wrap(() => _facade.GetLocalVariablesAsync(maxChars > 0 ? maxChars : 1024, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_local_variables",
                        Description = "Returns all local variables at the current stack frame with names, types, and values. Output may be truncated if total size exceeds maxChars (default 1024) — drill into a specific variable with get_variable_detail when hasChildren=true. Prerequisite: break mode (returns not_in_break_mode otherwise).",
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
                    (string expression, int maxChars, CancellationToken ct) => SafeCall.Wrap(() => _facade.EvaluateExpressionAsync(expression, maxChars > 0 ? maxChars : 1024, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "evaluate_expression",
                        Description = "Evaluates an arbitrary expression (e.g., obj.Property.Method()) and returns the result with type and value. Expression syntax follows the active debug engine: C#-like for managed, C/C++-like for native. May modify state if the expression has side effects. 5-second timeout. Prerequisite: break mode (returns not_in_break_mode otherwise).",
                        Destructive = true
                    }));

                tools.Add(McpServerTool.Create(
                    (string variableName, int maxChars, CancellationToken ct) => SafeCall.Wrap(() => _facade.GetVariableDetailAsync(variableName, maxChars > 0 ? maxChars : 1024, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "get_variable_detail",
                        Description = "Drills into a specific variable's children/properties. Use when get_local_variables shows truncated output (hasChildren: true). Prerequisite: break mode (returns not_in_break_mode otherwise).",
                        ReadOnly = true
                    }));
            }

            if (_symbolFacade != null)
            {
                tools.Add(McpServerTool.Create(
                    (string query, int maxResults, CancellationToken ct) => SafeCall.Wrap(() => _symbolFacade.FindSymbolAsync(query, maxResults > 0 ? maxResults : 200, ct), ct),
                    new McpServerToolCreateOptions
                    {
                        Name = "find_symbol",
                        Description = "Searches all loaded language libraries (C#, VB, C++) for symbols whose name contains the query (case-insensitive substring). Returns matches with source file path and line number when available; column is never available. Far more accurate than grep because it uses the language services' semantic model, not text matching.",
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

            return tools;
        }

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
        /// 发送一条 MCP logging 通知（<c>notifications/message</c>）。走 POST 响应 SSE 流，
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
        /// Processes a single HTTP request, bridging it to the MCP transport.
        /// </summary>
        private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            // F-4 / D-13: the single-session gate serializes POST/DELETE so at most
            // one tool call is in flight at a time (v1 single-session contract, D-14).
            // The GET SSE long-poll is routed OUTSIDE the gate (see below) so a
            // long-lived SSE subscriber does not block POST requests.
            //
            // gateAcquired tracks whether WaitAsync succeeded so the finally only
            // releases when the semaphore was actually acquired (WaitAsync does NOT
            // acquire on cancellation; an unconditional Release would over-count).
            bool gateAcquired = false;
            try
            {
                // Auth check FIRST — before the gate — so all methods (including the
                // ungated GET SSE path) are protected by the bearer-token middleware.
                // _auth.ValidateRequest is a stateless header read; safe outside the gate.
                if (!_auth.ValidateRequest(context.Request))
                {
                    await WriteUnauthorizedAsync(context.Response).ConfigureAwait(false);
                    return;
                }

                // GET long-poll SSE is not supported: all notifications (including
                // long-task keep-alive) travel inside the POST response stream, so
                // nothing depends on a GET stream. Reject GET so clients fall back
                // to POST-only. Routed outside the gate since it does no work.
                if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 405;
                    context.Response.StatusDescription = "Method Not Allowed";
                    context.Response.Close();
                    return;
                }

                // POST/DELETE/default: acquire the single-session gate (D-13).
                try
                {
                    await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown / external cancel while queued on the gate. The gate
                    // was NOT acquired, so do not release. Close the response so the
                    // HTTP client does not hang waiting on a response that will never
                    // come — a 503 signals the shutdown path without invoking tooling.
                    try
                    {
                        context.Response.StatusCode = 503;
                        context.Response.StatusDescription = "Service Unavailable";
                        context.Response.Close();
                    }
                    catch
                    {
                        // Client disconnected or response already closed.
                    }
                    return;
                }
                gateAcquired = true;

                try
                {
                    switch (context.Request.HttpMethod)
                    {
                        case "POST":
                            await HandleMcpRequestAsync(context, cancellationToken).ConfigureAwait(false);
                            break;

                        case "DELETE":
                            // D-32: conservative 202 Accepted. SDK 1.2.0 has no public
                            // HandleDeleteRequestAsync; v1 is single-session and VS exit
                            // tears down via VsMcpPackage Dispose. No transport
                            // rebuild here. DELETE stays inside the gate (no concurrency issue).
                            context.Response.StatusCode = 202;
                            context.Response.StatusDescription = "Accepted";
                            context.Response.Close();
                            break;

                        default:
                            context.Response.StatusCode = 405;
                            context.Response.StatusDescription = "Method Not Allowed";
                            context.Response.Close();
                            break;
                    }
                }
                catch (Exception)
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch
                    {
                        // Response already closed or client disconnected
                    }
                }
            }
            finally
            {
                if (gateAcquired)
                {
                    _requestGate.Release();
                }
            }
        }

        /// <summary>
        /// Handles an MCP JSON-RPC POST request by bridging the HTTP stream to the MCP transport.
        /// Deserializes the JSON-RPC message from the request body, passes it through the
        /// StreamableHttpServerTransport, and writes the response back to the HTTP response stream.
        /// </summary>
        private async Task HandleMcpRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            if (_transport == null)
            {
                context.Response.StatusCode = 503;
                context.Response.StatusDescription = "Service Unavailable";
                context.Response.Close();
                return;
            }

            // Reject oversized POST bodies BEFORE reading the stream, so an
            // unbounded Content-Length cannot exhaust server memory. ContentLength64
            // is 0/-1 for unknown-length bodies; that case is not capped here
            // (acceptable for v1 single-user, single-session transport).
            if (context.Request.ContentLength64 > 10L * 1024 * 1024)
            {
                context.Response.StatusCode = 413;
                context.Response.StatusDescription = "Payload Too Large";
                context.Response.Close();
                return;
            }

            // Read the request body. net48's StreamReader.ReadToEndAsync() has no
            // CancellationToken overload, so cancellation is enforced by closing the
            // request input stream when the caller token (shutdown) fires — the read
            // then throws and the gate's outer catch-all converts it to a 500.
            string requestBody;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, true, 4096, false))
            {
                using (cancellationToken.Register(() =>
                {
                    try { context.Request.InputStream.Close(); } catch { /* already closed */ }
                }))
                {
                    requestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }

            // Deserialize to JsonRpcMessage using the SDK's JSON options
            JsonRpcMessage message;
            try
            {
                message = JsonSerializer.Deserialize<JsonRpcMessage>(requestBody, McpJsonUtilities.DefaultOptions)
                    ?? throw new JsonException("Deserialized message is null");
            }
            catch (JsonException)
            {
                context.Response.StatusCode = 400;
                context.Response.StatusDescription = "Bad Request";
                context.Response.Close();
                return;
            }

            // SDK HandlePostRequestAsync ALWAYS writes SSE frames
            // (event: message\ndata: {json}\n\n) regardless of the Accept header.
            // The Content-Type must match the actual wire format or Streamable-HTTP
            // clients fail to parse the response.
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";

            // The SDK flushes each SSE frame immediately (SseEventWriter.WriteAsync →
            // FlushAsync) and serializes all writes via postTransport._messageLock,
            // so logging notifications emitted by tools (KeepAliveNotifier) during a
            // long call reach the client in real time — no manual heartbeat or
            // stream wrapper is needed. Passing the raw OutputStream is correct.
            bool wroteResponse;
            try
            {
                wroteResponse = await _transport.HandlePostRequestAsync(
                    message,
                    context.Response.OutputStream,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown in flight — no well-formed response to send.
                try { context.Response.Close(); } catch { /* already closed / client gone */ }
                return;
            }

            if (!wroteResponse)
            {
                // Some messages (notifications) don't produce a response
                context.Response.StatusCode = 202;
            }
            context.Response.Close();
        }

        /// <summary>
        /// Writes a 401 Unauthorized response with a JSON-RPC error body.
        /// </summary>
        private static async Task WriteUnauthorizedAsync(HttpListenerResponse response)
        {
            response.StatusCode = 401;
            response.ContentType = "application/json";

            var errorJson = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Unauthorized\"}}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            response.Close();
        }

        /// <summary>
        /// Synchronous teardown entry point. NEVER blocks on async disposal
        /// (that was the F-3 root cause: .GetAwaiter().GetResult() deadlocked
        /// VS exit). Instead this flips the _disposed flag, cancels + stops
        /// what can be done synchronously, and fire-and-forgets DisposeAsync
        /// so the SDK transport/run-loop actually gets disposed without
        /// pinning the calling (often UI) thread.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Cancel the server-owned CTS first (signals RunAsync + the
            // accept loop). Guard against the CTS being null (server never
            // started) or already disposed.
            try { _externalCts?.Cancel(); } catch (ObjectDisposedException) { }

            // Stop the listener synchronously. Unblocks GetContextAsync on
            // most Windows builds (see RESEARCH Pitfall #3 caveat — the
            // linked CTS cancellation above is the reliable unblocker).
            try { _listener.Stop(); } catch { /* already stopped */ }

            // Fire-and-forget the async teardown so the SDK gets disposed
            // without pinning this thread. Idempotent via _disposeLock + _disposed.
            _ = DisposeAsyncFireAndForget();

            try { _listener.Close(); } catch { /* already closed */ }
        }

        /// <summary>
        /// Asynchronous teardown: the real disposal path. Ordered to avoid
        /// deadlocks (RESEARCH Pitfall #3): cancel CTS -> Stop listener ->
        /// await run task -> dispose transport -> close listener. Idempotent
        /// via _disposeLock + _disposed.
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

                // 1. Cancel the server-owned CTS (signals RunAsync + accept loop).
                try { _externalCts?.Cancel(); } catch (ObjectDisposedException) { }

                // 2. Stop the listener (best-effort).
                try { _listener.Stop(); } catch { /* already stopped */ }

                // 3. Observe the SDK run loop. The SDK self-disposes in its
                //    finally (REMEDIATION F-3 fact #4), so this await is the
                //    signal that the loop has torn down — it does not perform
                //    disposal itself. OCE is the expected normal-shutdown path;
                //    any other exception is swallowed so teardown never throws.
                if (_serverRunTask != null)
                {
                    // server-owned run loop; cancellation-safe — SDK self-disposes in finally
#pragma warning disable VSTHRD003
                    try { await _serverRunTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { /* teardown must not throw */ }
#pragma warning restore VSTHRD003
                }

                // 4. Dispose the transport (StreamableHttpServerTransport is IAsyncDisposable).
                if (_transport != null)
                {
                    await _transport.DisposeAsync().ConfigureAwait(false);
                }

                // 5. Close the listener.
                try { _listener.Close(); } catch { /* already closed */ }
            }
            finally
            {
                _disposeLock.Release();
            }
        }

        /// <summary>
        /// Fire-and-forget wrapper used by the synchronous Dispose() path so
        /// the SDK transport/run-loop actually gets disposed without blocking
        /// the caller. Swallows everything — must never become an unobserved
        /// task exception (T-05-02-02). Name intentionally omits the "Async"
        /// suffix because the result is discarded by design (fire-and-forget),
        /// which VSTHRD200's blanket "all Task-returning methods must end in
        /// Async" rule cannot express — suppressed locally at the declaration.
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
                // Observed — never crash the process via unobserved exception.
            }
        }
    }
}
