using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// Gateway 进程入口：独占 <c>http://127.0.0.1:43210/mcp/</c>，接受 VS 实例经
    /// NamedPipe "vs-mcp-gateway" 的反向连接（每条连接首帧 register 携带 PID +
    /// solution info），把 MCP 客户端请求按 session 绑定路由到对应 VS 实例。
    ///
    /// Wave 2: MCP 感知 multiplexer。Session 绑定解析顺序：
    ///   initialize (_meta.vsPid 或唯一实例 fallback) → 记录 clientSessionId↔(pid,vsSessionId)
    ///   tools/list → 转发 + 注入 gateway 工具
    ///   tools/call gateway 工具 → 自处理（不转发）
    ///   其他 → 按 SessionTable 路由（② Session tier）
    ///
    /// 抢占语义不变：HttpListener bind 失败 = 另一 Gateway 已存活，exit 3。
    /// </summary>
    public static class Program
    {
        private const int Port = 43210;
        private const string GatewayPipeName = "vs-mcp-gateway";
        private const int RegisterReadTimeoutMs = 5000;

        private static readonly InstanceRegistry Registry = new InstanceRegistry();
        private static readonly SessionTable Sessions = new SessionTable();
        private static ProcessScanner? _scanner;

        private static async Task<int> Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            // 第一行打版本号，方便排查日志对应哪个版本。
            GatewayLogger.Log($"VsMcp gateway v{ExtensionVersion.Current} 启动");

            // 先绑定 HTTP——端口所有权是分布式锁，保证只有一个 Gateway 存活
            // （设计文档 §抢占式 Gateway 拉起）。
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/mcp/");
            try
            {
                listener.Start();
                GatewayLogger.Log($"Gateway listening on http://127.0.0.1:{Port}/mcp/");
            }
            catch (HttpListenerException ex)
            {
                GatewayLogger.Log("bind failed — another Gateway owns the port", ex);
                return 3; // 另一个 Gateway 已占用 :43210
            }

            // Pipe accept 循环：VS 实例拨入 "vs-mcp-gateway"，发送 register 帧，
            // 得到的流为每个 VS 包装成一个 PipeRouter。服务端用 Asynchronous
            // （WaitForConnectionAsync 在它上面行为良好；PipeOptions.None 约束
            // 只适用于做同步 Connect 的客户端——见 Wave 1 笔记）。
            _ = Task.Run(() => PipeAcceptLoopAsync(cts.Token));

            // 自终止看门狗：一旦没有 VS 实例存活，Gateway 自行退出，绝不成为永久
            // 残留进程（设计文档 §Gateway 自动退出）。注册表清空与 devenv 扫描两种机制
            // 都汇聚到 onSelfKill，由它直接 Environment.Exit(0) 进程。这里无法
            // 优雅地 cts.Cancel 收尾：HTTP accept 循环的 listener.GetContextAsync()
            // 不响应取消，取消会让进程卡在阻塞的 accept 上。自终止是受认可的关闭
            // 路径——进行中的请求按设计被放弃。上面的 Ctrl+C 路径仍用 cts.Cancel
            // 处理用户主动停止。本看门狗在 pipe accept 循环就绪后才启动，让初始的
            // 空注册表宽限窗口与首个 VS 连接窗口重叠。
            _scanner = new ProcessScanner(Registry, onSelfKill: () => Environment.Exit(0), cts.Token,
                aliveDevenvPids: ProcessScanner.DefaultAliveDevenvPids);
            _scanner.Start();

            // HTTP accept 循环。
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cts.Token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cts.Token.IsCancellationRequested) { break; }

                _ = HandleRequestAsync(ctx, cts.Token);
            }

            _scanner?.Dispose();
            return 0;
        }

        // ─────────────────────────── Pipe accept 循环 ───────────────────────────

        /// <summary>
        /// 在 "vs-mcp-gateway" 上 accept VS 连接，读取 register 帧，把流包装成
        /// PipeRouter 并注册。每条连接独立运行；register 读超时会丢弃该连接但
        /// 不会终止循环。
        /// </summary>
        private static async Task PipeAcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        GatewayPipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    // 在交给 PipeRouter 之前，先在原始流上读 register 帧
                    // （PipeRouter 的读循环只懂按 id 多路分解响应帧；register 没有 id）。
                    PipeRegister? info = await ReadRegisterFrameAsync(pipe, ct).ConfigureAwait(false);
                    if (info == null || info.Pid <= 0)
                    {
                        GatewayLogger.Log("pipe connected but register frame missing/invalid — dropping");
                        // 无效/缺失 register——丢弃连接。
                        TryDispose(pipe);
                        continue;
                    }

                    // 若该 PID 已有连接（VS 没有干净断开就重连），先淘汰旧条目。
                    if (Registry.TryGet(info.Pid, out var stale))
                    {
                        GatewayLogger.Log($"PID={info.Pid} reconnected — retiring stale entry");
                        Registry.Remove(info.Pid);
                        try { await stale.Router.DisposeAsync().ConfigureAwait(false); }
                        catch { /* 尽力而为 */ }
                    }

                    var router = new PipeRouter(pipe, ownsStream: true, OnSolutionChanged, OnHeartbeat, OnDebuggerStateChanged);
                    Registry.Register(new InstanceEntry(router, info, DateTime.UtcNow));
                    GatewayLogger.Log($"registered VS PID={info.Pid} solution={info.SolutionPath ?? "<none>"} v{info.VsVersion}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(pipe);
                    return;
                }
                catch (Exception)
                {
                    // 单次 accept/register 失败绝不能终止循环。
                    TryDispose(pipe);
                }
            }
        }

        private static async Task<PipeRegister?> ReadRegisterFrameAsync(Stream stream, CancellationToken ct)
        {
            using var regCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            regCts.CancelAfter(RegisterReadTimeoutMs);
            PipeRegister? info;
            try
            {
                info = await PipeFraming.ReadFrameAsync<PipeRegister>(stream, regCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (regCts.IsCancellationRequested)
            {
                // 诊断：区分超时 vs 异常 vs 干净 EOF。超时 = VS 连上了但 5s 内没发出 register 帧。
                GatewayLogger.Log($"register 读超时（{RegisterReadTimeoutMs}ms 内没收到 register 帧）");
                return null;
            }
            catch (Exception ex)
            {
                // 诊断：读到一半异常（帧长度越界 / 反序列化失败等），打详情定位。
                GatewayLogger.Log("register 读异常", ex);
                return null;
            }
            if (info == null)
            {
                // ReadFrameAsync 返回 null = 对端在帧起始处干净关闭（读了 0 字节）。
                // 这条单独打出来：它不进上面的 catch，否则只会落到调用方笼统的
                // "missing/invalid"，无法与超时/异常区分。典型成因是 VS 误判 connect
                // 超时后 Dispose 了实际已连上的管道，网关这端只看到一个空连接。
                GatewayLogger.Log("register 读到 EOF（VS 连上后未发任何帧即断开）");
            }
            return info;
        }

        /// <summary>
        /// PipeRouter 控制帧回调：VS 推送了 <c>solution-changed</c> 通知
        /// （用户打开/关闭了解决方案）。刷新注册表中该实例的 Info，让 ① Header
        /// 层能匹配到最新的 SolutionDir，无需等重连。解决方案名/目录/路径来自推送；
        /// VsVersion 和 PipeName 沿用既有条目（推送 DTO 不带这两项）。在 pipe 读
        /// 循环上内联执行，必须快且绝不抛异常——InstanceRegistry.UpdateInfo 是一次
        /// ConcurrentDictionary 字段交换，满足这两点。
        /// </summary>
        private static void OnSolutionChanged(PipeSolutionChanged changed)
        {
            if (changed == null || changed.Pid <= 0) return;
            if (!Registry.TryGet(changed.Pid, out var existing)) return;

            var merged = new PipeRegister
            {
                Pid = changed.Pid,
                PipeName = existing.Info.PipeName,
                SolutionName = changed.SolutionName,
                SolutionDir = changed.SolutionDir,
                SolutionPath = changed.SolutionPath,
                VsVersion = existing.Info.VsVersion,
            };
            Registry.UpdateInfo(changed.Pid, merged);
        }

        /// <summary>
        /// PipeRouter 控制帧回调：VS 心跳。刷新该实例的 LastSeen，让 ProcessScanner
        /// 机制 1 的"空/过期"判断在 pipe 断连之间保持最新。Pipe 断连是主要的 VS
        /// 退出信号（OS 语义）；LastSeen 是补充的存活心跳。在 pipe 读循环上内联
        /// 执行，必须快且绝不抛异常——InstanceRegistry.TouchLastSeen 是一次
        /// ConcurrentDictionary 字段写入，满足这两点。
        /// </summary>
        private static void OnHeartbeat(PipeHeartbeat beat)
        {
            if (beat == null || beat.Pid <= 0) return;
            Registry.TouchLastSeen(beat.Pid);
        }

        /// <summary>
        /// PipeRouter 控制帧回调：VS 推送了 <c>debugger-state-changed</c> 通知
        ///（调试器进入 design/break/running）。刷新注册表中该实例的 DebuggerState，
        /// 让 list_vs_instances 无需重连即可返回实时状态。在 pipe 读循环上内联执行，
        /// 必须快且绝不抛异常——InstanceRegistry.UpdateDebuggerState 是一次
        /// ConcurrentDictionary 字段写入，满足这两点。
        /// </summary>
        private static void OnDebuggerStateChanged(PipeDebuggerStateChanged changed)
        {
            if (changed == null || changed.Pid <= 0) return;
            Registry.UpdateDebuggerState(changed.Pid, changed.State);
        }

        // ───────────────────────────── HTTP 路由 ──────────────────────────────

        private static async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 405;
                    ctx.Response.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                string? clientSessionId = ctx.Request.Headers["Mcp-Session-Id"];
                // X-VS-Workspace drives the stateless ① Header tier (设计文档 §①).
                // Empty/missing header falls through to ②③④.
                string? workspaceHeader = ctx.Request.Headers["X-VS-Workspace"];
                if (string.IsNullOrWhiteSpace(workspaceHeader)) workspaceHeader = null;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? key in ctx.Request.Headers.AllKeys)
                {
                    if (key != null)
                        headers[key] = ctx.Request.Headers[key] ?? string.Empty;
                }

                // 只解析一次用于路由决策；转发原始 body 文本，让 VS 自己的
                // JSON-RPC 解析器看到逐字字节。
                string method = "";
                if (!TryGetMethod(body, out method))
                {
                    GatewayLogger.Log("REQ: unparseable JSON-RPC (no method) — 400");
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    return;
                }
                GatewayLogger.Log($"REQ {method} session={clientSessionId ?? "<none>"} workspace={workspaceHeader ?? "<none>"}");

                // notification（无 id）直接 202，不转发到 VS。
                object? jsonRpcId = TryGetJsonRpcId(body);
                if (jsonRpcId == null)
                {
                    ctx.Response.StatusCode = 202;
                    ctx.Response.Close();
                    return;
                }

                if (string.Equals(method, "initialize", StringComparison.Ordinal))
                {
                    await HandleInitializeAsync(ctx, body, workspaceHeader, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    await HandleToolsListAsync(ctx, body, clientSessionId, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/call", StringComparison.Ordinal) &&
                         TryGetToolName(body, out string? toolName) &&
                         GatewayTools.IsGatewayTool(toolName))
                {
                    await HandleGatewayToolCallAsync(ctx, body, clientSessionId, toolName!).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/call", StringComparison.Ordinal))
                {
                    await HandleForwardAsync(ctx, body, clientSessionId, workspaceHeader, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "ping", StringComparison.Ordinal))
                {
                    await WriteSimpleResultAsync(ctx, body, new { }).ConfigureAwait(false);
                }
                else
                {
                    await WriteJsonRpcErrorAsync(ctx, body, -32601, $"Method not found: {method}").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { ctx.Response.Close(); } catch { /* 关闭中 */ }
            }
            catch (Exception ex)
            {
                GatewayLogger.Log("HandleRequestAsync unhandled exception", ex);
                try { ctx.Response.StatusCode = 502; ctx.Response.Close(); } catch { /* 客户端已断开 */ }
            }
        }

        /// <summary>
        /// initialize：通过优先级链（<c>_meta.vsPid</c> &gt; ① Header 唯一 &gt;
        /// 单实例 fallback）解析目标 VS，本地构造 initialize 响应（不转发到 VS）。
        /// Gateway 自己分配 Mcp-Session-Id，不再从 VS head 帧捕获。单实例
        /// fallback 视为自动绑定（Source="auto"，HintPending=true）。
        /// </summary>
        private static async Task HandleInitializeAsync(
            HttpListenerContext ctx, string body,
            string? workspaceHeader, CancellationToken ct)
        {
            int? vsPid = TryGetMetaVsPid(body);
            bool isAutoFallback = false;

            if (!vsPid.HasValue && !string.IsNullOrWhiteSpace(workspaceHeader))
            {
                var snap = Registry.Snapshot();
                var match = WorkspaceResolver.Resolve(workspaceHeader,
                    snap.Select(e => (e.Info.Pid, e.Info.SolutionDir)));
                if (match.Kind == WorkspaceMatchKind.Single && match.Pid.HasValue)
                    vsPid = match.Pid;
                else if (match.Kind == WorkspaceMatchKind.None)
                {
                    await WriteJsonRpcErrorAsync(ctx, body, -32001,
                        GatewayTools.BuildWorkspaceMissMessage(workspaceHeader!, snap)).ConfigureAwait(false);
                    return;
                }
                else
                {
                    var matchedInstances = snap.Where(e => match.MatchedPids.Contains(e.Info.Pid)).ToArray();
                    await WriteJsonRpcErrorAsync(ctx, body, -32001,
                        GatewayTools.BuildAmbiguousMessage(workspaceHeader!, matchedInstances)).ConfigureAwait(false);
                    return;
                }
            }

            if (!vsPid.HasValue)
            {
                var snap = Registry.Snapshot();
                vsPid = ResolveInitializeTarget(vsPid, snap.Count, () => snap.First().Info.Pid);
                if (!vsPid.HasValue)
                {
                    if (snap.Count == 0)
                    {
                        await WriteJsonRpcErrorAsync(ctx, body, -32001,
                            "No VS instance connected. Open a Visual Studio instance with the MCP extension, then retry.").ConfigureAwait(false);
                        return;
                    }
                    vsPid = snap.First().Info.Pid;
                }
                isAutoFallback = true;
            }

            if (!Registry.TryGet(vsPid.Value, out _))
            {
                await WriteJsonRpcErrorAsync(ctx, body, -32002, $"VS instance {vsPid.Value} is not connected.").ConfigureAwait(false);
                return;
            }

            // Gateway 自己分配 session id，不依赖 VS 的 SDK 分配。
            string source = isAutoFallback ? "auto" : "initialize";
            var (clientSessionId, binding) = Sessions.CreateWithId(vsPid.Value, vsSessionId: null, source);
            if (isAutoFallback)
                binding.HintPending = true;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

            // 本地构造 initialize 响应（不转发到 VS）。
            string respJson = GatewayTools.BuildInitializeResponse(TryGetJsonRpcId(body) ?? 0);
            string sse = "event: message\ndata: " + respJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            GatewayLogger.Log($"initialize OK: client session {clientSessionId} → VS PID={vsPid.Value}");

            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        /// <summary>
        /// tools/list：本地构造工具列表响应（不转发到 VS）。Gateway 固定列出全部
        /// 工具（含 go_to_definition / eval_csharp）。回显客户端 session id。
        /// </summary>
        private static async Task HandleToolsListAsync(
            HttpListenerContext ctx, string body,
            string? clientSessionId, CancellationToken ct)
        {
            string respJson = GatewayTools.BuildToolsListResponse(
                TryGetJsonRpcId(body) ?? 0, includeGoToDefinition: true, includeEvalCsharp: true);
            string sse = "event: message\ndata: " + respJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        /// <summary>
        /// Gateway 自处理的工具调用（list_vs_instances / select_vs_instance）：
        /// 本地构造 JSON-RPC 响应，绝不转发到 VS。
        /// </summary>
        private static async Task HandleGatewayToolCallAsync(
            HttpListenerContext ctx, string body, string? clientSessionId, string toolName)
        {
            object? jsonRpcId = TryGetJsonRpcId(body) ?? (object)0;
            string responseJson;
            if (string.Equals(toolName, GatewayTools.ListInstancesName, StringComparison.Ordinal))
            {
                responseJson = GatewayTools.BuildListInstancesResponse(jsonRpcId, Registry.Snapshot());
            }
            else // select_vs_instance
            {
                if (string.IsNullOrEmpty(clientSessionId))
                {
                    responseJson = GatewayTools.BuildToolErrorResponse(jsonRpcId,
                        "select_vs_instance requires an initialized session. Call initialize first.");
                }
                else if (!TryGetSelectPid(body, out int targetPid))
                {
                    responseJson = GatewayTools.BuildToolErrorResponse(jsonRpcId,
                        "select_vs_instance requires a 'pid' integer parameter.");
                }
                else if (!Registry.TryGet(targetPid, out _))
                {
                    responseJson = GatewayTools.BuildToolErrorResponse(jsonRpcId,
                        $"VS instance PID {targetPid} is not connected. Call list_vs_instances for available PIDs.");
                }
                else
                {
                    Sessions.Rebind(clientSessionId!, targetPid);
                    GatewayLogger.Log($"select_vs_instance: session {clientSessionId} → PID={targetPid}");
                    responseJson = GatewayTools.BuildSelectResponse(jsonRpcId, targetPid);
                }
            }

            // tool result 通过 SSE 下发，让传输方式与 agent 接下来要发起的流式
            // 工具调用保持一致。
            string sse = "event: message\ndata: " + responseJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        /// <summary>
        /// tools/call（非 gateway 工具）：通过 4 层流程（①②③④）解析目标 VS，经**流式**
        /// pipe tool-call 转发——执行期间 VS 推来的 tool-progress 帧（build 增量日志 /
        /// callers 搜索进度）实时转为 MCP logging notification（notifications/message）
        /// 逐条 flush 给客户端，最终 tool-result 作为最后一条 SSE 事件。若有 hint pending，
        /// 把 hint 文本追加到 content 末尾。回显 session id。
        /// </summary>
        private static async Task HandleForwardAsync(
            HttpListenerContext ctx, string body,
            string? clientSessionId, string? workspaceHeader, CancellationToken ct)
        {
            var resolution = BindingResolver.ResolveTarget(workspaceHeader, clientSessionId, Sessions, Registry);
            if (!resolution.Success)
            {
                await RespondToResolutionFailureAsync(ctx, body, clientSessionId, resolution).ConfigureAwait(false);
                return;
            }

            if (!Registry.TryGet(resolution.Pid!.Value, out var entry))
            {
                await WriteJsonRpcErrorAsync(ctx, body, -32003,
                    $"Bound VS instance PID {resolution.Pid} is no longer connected.").ConfigureAwait(false);
                return;
            }

            string? echoSessionId = resolution.Binding?.ClientSessionId ?? clientSessionId;

            // hint 注入由 HintPending 门控。原子 claim 防止并发重复注入。
            SessionBinding? binding = resolution.Binding;
            bool needsHint = binding != null && binding.ClaimHint();

            // 提取工具名和参数 JSON（params.arguments 的 GetRawText）。
            TryGetToolName(body, out string? toolName);
            TryGetToolArguments(body, out string? argumentsJson);

            // 流式响应头：必须在写任何 body 前设好——HttpListener 首次写 OutputStream
            // 时刷新状态行 + headers。这样进度 SSE 能在工具执行期间逐条 flush，客户端
            // 实时收到 build 增量日志 / callers 进度，而非缓冲到结尾一次性返回。
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(echoSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = echoSessionId;

            // VS 端 KeepAliveNotifier 推来的 tool-progress 文本，包成 MCP logging
            // notification 实时下发 + flush。level=info：纯进度信息；data 为进度文本。
            async Task SendProgressAsync(string text)
            {
                try
                {
                    var notify = new
                    {
                        jsonrpc = "2.0",
                        method = "notifications/message",
                        @params = new { level = "info", data = text },
                    };
                    string j = JsonSerializer.Serialize(notify);
                    byte[] b = Encoding.UTF8.GetBytes("event: message\ndata: " + j + "\n\n");
                    await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
                    await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 客户端已断开等——进度丢失不影响最终结果回传。
                }
            }

            // 调用 VS 端 ToolExecutor，经流式 pipe tool-call/tool-result 帧 + tool-progress。
            PipeToolResultData toolResult;
            try
            {
                toolResult = await entry.Router.CallToolStreamingAsync(
                    toolName ?? "", argumentsJson ?? "{}", SendProgressAsync, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 流式期间 pipe 断连 / router disposed。headers 可能已随进度 SSE flush，
                // 不能再设 status（否则抛 "headers already sent"，被外层吞成 502）；
                // 直接写一条 JSON-RPC error SSE 后关闭。
                GatewayLogger.Log($"streaming tool-call error: {ex.Message}");
                try
                {
                    object? id = TryGetJsonRpcId(body) ?? 0;
                    string errJson = GatewayTools.BuildErrorResponse(id, -32003,
                        $"VS pipe error during tool-call: {ex.Message}");
                    byte[] b = Encoding.UTF8.GetBytes("event: message\ndata: " + errJson + "\n\n");
                    await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
                }
                catch { /* 客户端已断开 */ }
                try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
                return;
            }

            // 构造最终 tool-result（最后一条 SSE 事件）。hint 追加到 content 末尾（\n\n 分隔）。
            string contentText = toolResult.Content;
            if (needsHint)
            {
                string hint = GatewayTools.BuildAutoBindHint(entry);
                contentText = contentText + "\n\n" + hint;
            }

            object? jsonRpcId = TryGetJsonRpcId(body) ?? 0;
            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                result = new
                {
                    content = new[] { new { type = "text", text = contentText } },
                    isError = toolResult.IsError,
                },
            };
            string respJson = JsonSerializer.Serialize(resp);
            byte[] bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + respJson + "\n\n");

            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        /// <summary>
        /// 写一个携带简单 result 的 SSE 响应（用于 ping 等）。
        /// </summary>
        private static async Task WriteSimpleResultAsync(HttpListenerContext ctx, string body, object result)
        {
            object? id = TryGetJsonRpcId(body) ?? 0;
            var resp = new { jsonrpc = "2.0", id, result };
            string respJson = JsonSerializer.Serialize(resp);
            string sse = "event: message\ndata: " + respJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        /// <summary>
        /// 把失败的 <see cref="TargetResolution"/> 映射到正确的 SSE 响应形态。面向
        /// agent 的引导（① 未命中/歧义、④ 拦截）以 tool-error result 形式发出，让
        /// agent 当作工具失败并转达给用户；协议层路由问题（实例已下线、需要重新
        /// initialize）以 JSON-RPC error 形式发出，保持与 Wave 2 的传输连续性。
        /// </summary>
        private static async Task RespondToResolutionFailureAsync(
            HttpListenerContext ctx, string body, string? clientSessionId, TargetResolution resolution)
        {
            switch (resolution.ErrorKind)
            {
                case TargetErrorKind.WorkspaceMiss:
                case TargetErrorKind.WorkspaceAmbiguous:
                case TargetErrorKind.Intercept:
                    await WriteToolErrorAsync(ctx, body, resolution.ErrorText ?? "Routing failed.", clientSessionId).ConfigureAwait(false);
                    return;
                case TargetErrorKind.NeedsInitialize:
                    await WriteJsonRpcErrorAsync(ctx, body, -32004, resolution.ErrorText ?? "Call initialize first.").ConfigureAwait(false);
                    return;
                default: // InstanceGone / None
                    await WriteJsonRpcErrorAsync(ctx, body, -32003, resolution.ErrorText ?? "No VS instance bound.").ConfigureAwait(false);
                    return;
            }
        }

        // ─────────────────────────── 路由辅助 ────────────────────────────

        /// <summary>
        /// 为 initialize 请求解析目标 VS PID。优先级：
        ///   1. 显式 <c>_meta.vsPid</c>（预绑定）
        ///   2. 恰好一个实例已连接（单实例 fallback，即 ③ 减去 Wave 3 的 hint 注入）
        ///   3. 否则返回 null→调用方返回歧义/空错误。
        /// 抽成纯函数，让路由可不带 HttpListener / 实时 pipe 做单元测试。
        /// </summary>
        public static int? ResolveInitializeTarget(int? metaVsPid, int connectedCount, Func<int> singlePid)
        {
            if (metaVsPid.HasValue && metaVsPid.Value > 0)
                return metaVsPid.Value;
            if (connectedCount == 1)
                return singlePid();
            return null;
        }

        // ───────────────────────── JSON-RPC 字段提取 ────────────────────

        private static bool TryGetMethod(string body, out string method)
        {
            method = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("method", out var m))
                    method = m.GetString() ?? "";
            }
            catch { return false; }
            return !string.IsNullOrEmpty(method);
        }

        private static bool TryGetToolName(string body, out string? name)
        {
            name = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("name", out var n))
                {
                    name = n.GetString();
                    return !string.IsNullOrEmpty(name);
                }
            }
            catch { }
            return false;
        }

        /// <summary>提取 params.arguments 的原始 JSON 文本（GetRawText），
        /// 透传给 VS 端 ToolExecutor 经 JsonDocument.Parse 再按工具签名反序列化。</summary>
        private static bool TryGetToolArguments(string body, out string? argumentsJson)
        {
            argumentsJson = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("arguments", out var a))
                {
                    argumentsJson = a.GetRawText();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static int? TryGetMetaVsPid(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("_meta", out var meta) &&
                    meta.TryGetProperty("vsPid", out var pidEl) &&
                    pidEl.TryGetInt32(out int pid) && pid > 0)
                {
                    return pid;
                }
            }
            catch { }
            return null;
        }

        private static object? TryGetJsonRpcId(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out long l))
                        return l;
                    if (idEl.ValueKind == JsonValueKind.String)
                        return idEl.GetString();
                }
            }
            catch { }
            return null;
        }

        private static bool TryGetSelectPid(string body, out int pid)
        {
            pid = 0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("params", out var p)) return false;
                // 标准 MCP tools/call 把参数放在 params.arguments 下；遗留/直接形式
                // 把 pid 直接放在 params 下。两种都接受，让任何客户端（包括 Claude
                // Code 的标准 tools/call）都能绑定。
                JsonElement pidEl;
                if ((p.TryGetProperty("arguments", out var args) && args.TryGetProperty("pid", out pidEl))
                    || p.TryGetProperty("pid", out pidEl))
                {
                    if (pidEl.TryGetInt32(out int p2))
                    {
                        pid = p2;
                        return pid > 0;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 以单条 SSE 消息写一个 JSON-RPC error 响应。用于路由失败（无绑定、未知
        /// pid 等）且没有 VS 可转发的场景。
        /// </summary>
        private static async Task WriteJsonRpcErrorAsync(HttpListenerContext ctx, string body, int code, string message)
        {
            object? id = TryGetJsonRpcId(body) ?? (object)0;
            GatewayLogger.Log($"← JSON-RPC error {code}: {message}");
            string errJson = GatewayTools.BuildErrorResponse(id, code, message);
            // 路由中途的错误没有 session 可恢复；以 SSE error 事件下发，让客户端的
            // MCP 传输层仍能解析它。
            string sse = "event: message\ndata: " + errJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        /// <summary>
        /// 写一个 tool-error（isError）SSE 响应，把 <paramref name="message"/> 作为
        /// 文本内容。用于 ①③④ 面向 agent 的拦截消息（设计文档 §①/§③/§④）——它们
        /// 以工具失败形式浮现给 agent，让 agent 把引导转达给用户。
        /// <paramref name="clientSessionId"/> 存在时回显，让客户端保留其会话。
        /// </summary>
        private static async Task WriteToolErrorAsync(HttpListenerContext ctx, string body, string message, string? clientSessionId = null)
        {
            object? id = TryGetJsonRpcId(body) ?? (object)0;
            GatewayLogger.Log($"← tool-error: {message}");
            string respJson = GatewayTools.BuildToolErrorResponse(id, message);
            string sse = "event: message\ndata: " + respJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;
            // headers 必须在第一次 OutputStream 写之前设好——HttpListener 在第一次
            // 写时刷新状态行 + headers。
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* 客户端已断开 */ }
        }

        private static void TryDispose(IDisposable? d)
        {
            try { d?.Dispose(); } catch { /* 尽力而为 */ }
        }
    }
}
