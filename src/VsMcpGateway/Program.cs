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

            // Bind HTTP first — port ownership is the distributed lock that
            // guarantees only one Gateway survives (设计文档 §抢占式 Gateway 拉起).
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/mcp/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                return 3; // another Gateway already owns :43210
            }

            // Pipe accept loop: VS instances dial in to "vs-mcp-gateway", send a
            // register frame, and the resulting stream becomes one PipeRouter
            // per VS. Server uses Asynchronous (WaitForConnectionAsync is
            // well-behaved on it; the PipeOptions.None constraint only applies
            // to the CLIENT side doing synchronous Connect — see Wave 1 notes).
            _ = Task.Run(() => PipeAcceptLoopAsync(cts.Token));

            // Self-termination watchdog: the Gateway exits on its own once no VS
            // instance is left, so it can never become a permanent orphan process
            // (设计文档 §Gateway 自杀). Both the registry-empty and devenv-scan
            // mechanisms funnel through cts.Cancel, letting the accept loops unwind
            // gracefully instead of Environment.Exit. Started after the pipe accept
            // loop is armed so the initial empty-registry grace overlaps the first
            // VS connection window.
            _scanner = new ProcessScanner(Registry, onSelfKill: () => cts.Cancel(), cts.Token);
            _scanner.Start();

            // HTTP accept loop.
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

        // ─────────────────────────── Pipe accept loop ───────────────────────────

        /// <summary>
        /// Accept VS connections on "vs-mcp-gateway", read the register frame,
        /// wrap the stream in a PipeRouter, register it. Each connection runs
        /// independently; a register-read timeout drops the connection without
        /// killing the loop.
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

                    // Read the register frame on the raw stream BEFORE handing
                    // off to PipeRouter (PipeRouter's read loop only knows how
                    // to demux response frames by id; register has no id).
                    PipeRegister? info = await ReadRegisterFrameAsync(pipe, ct).ConfigureAwait(false);
                    if (info == null || info.Pid <= 0)
                    {
                        // Bogus / no register — drop the connection.
                        TryDispose(pipe);
                        continue;
                    }

                    // If this PID already has a connection (VS reconnected
                    // without a clean disconnect), retire the old entry first.
                    if (Registry.TryGet(info.Pid, out var stale))
                    {
                        Registry.Remove(info.Pid);
                        try { await stale.Router.DisposeAsync().ConfigureAwait(false); }
                        catch { /* best-effort */ }
                    }

                    var router = new PipeRouter(pipe, ownsStream: true, OnSolutionChanged, OnHeartbeat);
                    Registry.Register(new InstanceEntry(router, info, DateTime.UtcNow));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(pipe);
                    return;
                }
                catch (Exception)
                {
                    // A single accept/register failure must not kill the loop.
                    TryDispose(pipe);
                }
            }
        }

        private static async Task<PipeRegister?> ReadRegisterFrameAsync(Stream stream, CancellationToken ct)
        {
            using var regCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            regCts.CancelAfter(RegisterReadTimeoutMs);
            try
            {
                // Deserialize through PipeFraming's camelCase options. The wire
                // frame is camelCase (pid / solutionPath / ...); a bare
                // JsonSerializer.Deserialize<PipeRegister> uses the default
                // PascalCase policy and silently binds nothing — Pid parses as 0,
                // the caller treats Pid<=0 as a bogus register, drops the
                // connection, and no VS instance ever registers.
                return await PipeFraming.ReadFrameAsync<PipeRegister>(stream, regCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (regCts.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// PipeRouter control-frame callback: a VS pushed a
        /// <c>solution-changed</c> notification (user opened/closed a solution).
        /// Refresh that instance's Info in the registry so the ① Header tier
        /// matches against the live SolutionDir without waiting for a
        /// reconnect. Solution name/dir/path come from the push; VsVersion and
        /// PipeName are preserved from the existing entry (the push DTO omits
        /// them). Runs inline on the pipe read loop, so it must be fast and
        /// never throw — InstanceRegistry.UpdateInfo is a ConcurrentDictionary
        /// field swap and satisfies both.
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
        /// PipeRouter control-frame callback: a VS heartbeat. Refreshes that
        /// instance's LastSeen so ProcessScanner mechanism 1's emptiness/staleness
        /// view stays current between pipe disconnects. Pipe disconnect is the
        /// primary VS-exit signal (OS semantics); LastSeen is the supplementary
        /// liveness heartbeat. Runs inline on the pipe read loop, so it must be
        /// fast and never throw — InstanceRegistry.TouchLastSeen is a
        /// ConcurrentDictionary field write and satisfies both.
        /// </summary>
        private static void OnHeartbeat(PipeHeartbeat beat)
        {
            if (beat == null || beat.Pid <= 0) return;
            Registry.TouchLastSeen(beat.Pid);
        }

        // ───────────────────────────── HTTP routing ──────────────────────────────

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

                // Parse once for routing decisions; forward the original body
                // text so VS's own JSON-RPC parser sees the bytes verbatim.
                string method = "";
                if (!TryGetMethod(body, out method))
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    return;
                }

                if (string.Equals(method, "initialize", StringComparison.Ordinal))
                {
                    await HandleInitializeAsync(ctx, body, headers, workspaceHeader, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    await HandleToolsListAsync(ctx, body, headers, clientSessionId, workspaceHeader, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/call", StringComparison.Ordinal) &&
                         TryGetToolName(body, out string? toolName) &&
                         GatewayTools.IsGatewayTool(toolName))
                {
                    await HandleGatewayToolCallAsync(ctx, body, clientSessionId, toolName!).ConfigureAwait(false);
                }
                else
                {
                    await HandleForwardAsync(ctx, body, headers, clientSessionId, workspaceHeader, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { ctx.Response.Close(); } catch { /* shutdown */ }
            }
            catch
            {
                try { ctx.Response.StatusCode = 502; ctx.Response.Close(); } catch { /* client gone */ }
            }
        }

        /// <summary>
        /// initialize: resolve the target VS via the priority chain
        /// (<c>_meta.vsPid</c> &gt; ① Header single &gt; single-instance fallback),
        /// forward with the client's Mcp-Session-Id stripped (so VS assigns its
        /// own), capture VS's Mcp-Session-Id from the head, and record the
        /// binding. The VS-assigned id is mirrored onto the InstanceEntry
        /// (per-VS authoritative — 设计文档 §Solution 信息动态更新) so the
        /// stateless ① Header tier can recover it later. The single-instance
        /// fallback is treated as an auto-bind (Source="auto", HintPending=true);
        /// an explicit <c>_meta.vsPid</c> or a ① Header match is a deliberate
        /// bind (HintPending=false).
        /// </summary>
        private static async Task HandleInitializeAsync(
            HttpListenerContext ctx, string body, IDictionary<string, string> headers,
            string? workspaceHeader, CancellationToken ct)
        {
            int? vsPid = TryGetMetaVsPid(body);
            bool isAutoFallback = false;

            if (!vsPid.HasValue && !string.IsNullOrWhiteSpace(workspaceHeader))
            {
                // ① Header tier at initialize: pick the VS whose SolutionDir the
                // header points at. Single → pre-bind; None/Ambiguous → fail with
                // the same agent-facing message the forward path uses.
                var snap = Registry.Snapshot();
                var match = WorkspaceResolver.Resolve(workspaceHeader,
                    snap.Select(e => (e.Info.Pid, e.Info.SolutionDir)));
                if (match.Kind == WorkspaceMatchKind.Single && match.Pid.HasValue)
                    vsPid = match.Pid;
                else if (match.Kind == WorkspaceMatchKind.None)
                {
                    // initialize failure → JSON-RPC error (NOT a tool-error). MCP
                    // clients schema-validate the initialize result; a tool-error
                    // (result.isError) has no protocolVersion/capabilities/serverInfo
                    // and trips zod, surfacing as three undefined fields to the user.
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
                // Single-instance fallback (③ at initialize time).
                var snap = Registry.Snapshot();
                vsPid = ResolveInitializeTarget(vsPid, snap.Count, () => snap.First().Info.Pid);
                if (!vsPid.HasValue)
                {
                    // initialize failure → JSON-RPC error so MCP clients parse it
                    // as an initialize failure rather than a schema-mismatched result.
                    int count = snap.Count;
                    string msg = count == 0
                        ? "No VS instance connected. Open a Visual Studio instance with the MCP extension, then retry."
                        : GatewayTools.BuildInterceptMessage(snap);
                    await WriteJsonRpcErrorAsync(ctx, body, -32001, msg).ConfigureAwait(false);
                    return;
                }
                isAutoFallback = true;
            }

            if (!Registry.TryGet(vsPid.Value, out var entry))
            {
                await WriteJsonRpcErrorAsync(ctx, body, -32002, $"VS instance {vsPid.Value} is not connected.").ConfigureAwait(false);
                return;
            }

            // Strip the client's session header so VS's SDK treats this as a new
            // session and assigns its own Mcp-Session-Id (which we capture below).
            var forwardedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            forwardedHeaders.Remove("Mcp-Session-Id");

            // Pre-allocate the client-visible session id and set it as a response
            // header BEFORE any byte reaches OutputStream. HttpListener flushes
            // the status line + headers on the first write to the response stream
            // (ForwardAsync's read loop flushes every data frame in real time),
            // so a header set only after the forward would already be too late
            // and the client would never receive its session id. VS's own session
            // id is unknown until the forward returns, so the binding is created
            // with a null placeholder and patched once the head frame is observed.
            string source = isAutoFallback ? "auto" : "initialize";
            var (clientSessionId, binding) = Sessions.CreateWithId(vsPid.Value, vsSessionId: null, source);
            // The single-instance fallback is an auto-bind: arm the one-shot hint
            // so the first tool result after this initialize carries it.
            if (isAutoFallback)
                binding.HintPending = true;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

            ForwardResult result;
            using (var output = ctx.Response.OutputStream)
            {
                result = await entry.Router.ForwardAsync(body, forwardedHeaders, output, ct).ConfigureAwait(false);
            }

            // The read loop populated result.Headers from VS's head frame; record
            // the VS-assigned session id so subsequent forwards carry it back to
            // that VS's SDK, letting it resume the right server-side session.
            // The InstanceEntry is the authoritative per-VS home for this id
            // (stateless ① routing reads it directly).
            if (result.Headers.TryGetValue("Mcp-Session-Id", out var vsSid) && !string.IsNullOrEmpty(vsSid))
            {
                binding.VsSessionId = vsSid;
                Registry.SetVsSessionId(vsPid.Value, vsSid);
            }

            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        /// <summary>
        /// tools/list: resolve the target via the 4-tier flow (①②③④), forward to
        /// the bound VS, buffer the SSE response, inject the gateway tools, and
        /// write the rewritten bytes. tools/list is not a tool CALL, so the
        /// auto-bind hint is armed (③) but not injected here — the next
        /// tools/call surfaces it. The auto-bound client session id is echoed so
        /// the client adopts it before that next call.
        /// </summary>
        private static async Task HandleToolsListAsync(
            HttpListenerContext ctx, string body, IDictionary<string, string> headers,
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

            var forwardedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(resolution.VsSessionId))
                forwardedHeaders["Mcp-Session-Id"] = resolution.VsSessionId!;
            else
                forwardedHeaders.Remove("Mcp-Session-Id");

            // Buffer the entire response: tools/list is a single small SSE
            // message, and we need the full JSON to splice in our tools.
            using var buffer = new MemoryStream();
            await entry.Router.ForwardAsync(body, forwardedHeaders, buffer, ct).ConfigureAwait(false);

            byte[] injected = GatewayTools.InjectIntoToolsListSse(buffer.ToArray());

            // Echo the session id the binding was created under (matters for the
            // ③ auto-bind case where the client sent none and we minted one).
            string? echoSessionId = resolution.Binding?.ClientSessionId ?? clientSessionId;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(echoSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = echoSessionId;

            await ctx.Response.OutputStream.WriteAsync(injected, 0, injected.Length, ct).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        /// <summary>
        /// Gateway-handled tool calls (list_vs_instances / select_vs_instance):
        /// build the JSON-RPC response locally, never forward to a VS.
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
                    responseJson = GatewayTools.BuildSelectResponse(jsonRpcId, targetPid);
                }
            }

            // Tool results are delivered over SSE so the transport stays uniform
            // with the streamed tool calls the agent will make next.
            string sse = "event: message\ndata: " + responseJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        /// <summary>
        /// Default path (tools/call etc.): resolve via the 4-tier flow (①②③④),
        /// forward to the bound VS. If the resolution armed a hint (③ auto-bind
        /// or a leftover HintPending from a prior auto-bind), buffer the SSE
        /// response, inject the hint text at the front of result.content, and
        /// clear the one-shot flag. Otherwise stream the response straight
        /// through so long tasks (build_solution) keep their real-time keep-alive
        /// notifications unbuffered.
        /// </summary>
        private static async Task HandleForwardAsync(
            HttpListenerContext ctx, string body, IDictionary<string, string> headers,
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

            var forwardedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(resolution.VsSessionId))
                forwardedHeaders["Mcp-Session-Id"] = resolution.VsSessionId!;
            else
                forwardedHeaders.Remove("Mcp-Session-Id");

            // Echo the session id the binding was created under. For the ③
            // auto-bind case the client sent none and we minted one — it MUST be
            // echoed so subsequent requests carry it (otherwise every call would
            // auto-bind again and the hint would fire forever).
            string? echoSessionId = resolution.Binding?.ClientSessionId ?? clientSessionId;

            // Hint injection is gated on HintPending. The stateless ① tier never
            // sets a binding, so it never injects (correct: header-routed calls
            // are deliberate, not auto-bound). When injecting we must buffer the
            // full response — but only then, to keep streaming for the common
            // case (the design note's hint-buffering tradeoff).
            SessionBinding? binding = resolution.Binding;
            bool needsHint = binding != null && binding.HintPending;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(echoSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = echoSessionId;

            if (needsHint)
            {
                string hint = GatewayTools.BuildAutoBindHint(entry);
                using var buffer = new MemoryStream();
                await entry.Router.ForwardAsync(body, forwardedHeaders, buffer, ct).ConfigureAwait(false);
                var (rewritten, injected) = GatewayTools.InjectHintIntoToolResultSse(buffer.ToArray(), hint);
                if (injected)
                {
                    // One-shot: only the first tools/call result that successfully
                    // carried the hint clears the flag (设计文档 MI-07).
                    binding!.HintPending = false;
                }
                await ctx.Response.OutputStream.WriteAsync(rewritten, 0, rewritten.Length, ct).ConfigureAwait(false);
            }
            else
            {
                using (var output = ctx.Response.OutputStream)
                {
                    await entry.Router.ForwardAsync(body, forwardedHeaders, output, ct).ConfigureAwait(false);
                }
            }
            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        /// <summary>
        /// Map a failed <see cref="TargetResolution"/> to the right SSE response
        /// shape. Agent-facing guidance (① miss/ambiguous, ④ intercept) goes out
        /// as a tool-error result so the agent treats it as a tool failure and
        /// relays it to the user; protocol-level routing problems (instance
        /// gone, needs re-initialize) go out as JSON-RPC errors for transport
        /// continuity with Wave 2.
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

        // ─────────────────────────── Routing helpers ────────────────────────────

        /// <summary>
        /// Resolve the target VS PID for an initialize request. Priority:
        ///   1. explicit <c>_meta.vsPid</c> (pre-bind)
        ///   2. exactly one instance connected (single-instance fallback, ③ minus
        ///      the Wave 3 hint injection)
        ///   3. otherwise null → caller returns an ambiguous/empty error.
        /// Extracted as a pure function so routing can be unit-tested without an
        /// HttpListener / live pipe.
        /// </summary>
        public static int? ResolveInitializeTarget(int? metaVsPid, int connectedCount, Func<int> singlePid)
        {
            if (metaVsPid.HasValue && metaVsPid.Value > 0)
                return metaVsPid.Value;
            if (connectedCount == 1)
                return singlePid();
            return null;
        }

        // ───────────────────────── JSON-RPC field extraction ────────────────────

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
                if (doc.RootElement.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("pid", out var pidEl) &&
                    pidEl.TryGetInt32(out int p2))
                {
                    pid = p2;
                    return pid > 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Write a JSON-RPC error response as a single SSE message. Used for
        /// routing failures (no binding, unknown pid, etc.) where there is no
        /// VS to forward to.
        /// </summary>
        private static async Task WriteJsonRpcErrorAsync(HttpListenerContext ctx, string body, int code, string message)
        {
            object? id = TryGetJsonRpcId(body) ?? (object)0;
            string errJson = GatewayTools.BuildErrorResponse(id, code, message);
            // An error mid-routing has no session to resume; deliver it as an
            // SSE error event so the client's MCP transport still parses it.
            string sse = "event: message\ndata: " + errJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        /// <summary>
        /// Write a tool-error (isError) SSE response carrying <paramref name="message"/>
        /// as the text content. Used for the ①③④ agent-facing intercept messages
        /// (设计文档 §①/§③/§④) — they surface to the agent as a tool failure so the
        /// agent relays the guidance to the user. <paramref name="clientSessionId"/>,
        /// when present, is echoed so the client keeps its session.
        /// </summary>
        private static async Task WriteToolErrorAsync(HttpListenerContext ctx, string body, string message, string? clientSessionId = null)
        {
            object? id = TryGetJsonRpcId(body) ?? (object)0;
            string respJson = GatewayTools.BuildToolErrorResponse(id, message);
            string sse = "event: message\ndata: " + respJson + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sse);

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;
            // Headers must be set before the first OutputStream write —
            // HttpListener flushes the status line + headers on first write.
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        private static void TryDispose(IDisposable? d)
        {
            try { d?.Dispose(); } catch { /* best-effort */ }
        }
    }
}
