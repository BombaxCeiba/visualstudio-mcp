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

                    var router = new PipeRouter(pipe, ownsStream: true);
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
                string? json = await PipeFraming.ReadFrameJsonAsync(stream, regCts.Token).ConfigureAwait(false);
                if (json == null) return null;
                return JsonSerializer.Deserialize<PipeRegister>(json);
            }
            catch
            {
                return null;
            }
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
                    await HandleInitializeAsync(ctx, body, headers, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    await HandleToolsListAsync(ctx, body, headers, clientSessionId, ct).ConfigureAwait(false);
                }
                else if (string.Equals(method, "tools/call", StringComparison.Ordinal) &&
                         TryGetToolName(body, out string? toolName) &&
                         GatewayTools.IsGatewayTool(toolName))
                {
                    await HandleGatewayToolCallAsync(ctx, body, clientSessionId, toolName!).ConfigureAwait(false);
                }
                else
                {
                    await HandleForwardAsync(ctx, body, headers, clientSessionId, ct).ConfigureAwait(false);
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
        /// initialize: generate a client session id, resolve target VS (via
        /// _meta.vsPid, else single-instance fallback), forward with the
        /// client's Mcp-Session-Id stripped (so VS assigns its own), capture
        /// VS's Mcp-Session-Id from the head, and record the binding.
        /// </summary>
        private static async Task HandleInitializeAsync(
            HttpListenerContext ctx, string body, IDictionary<string, string> headers, CancellationToken ct)
        {
            int? vsPid = TryGetMetaVsPid(body);
            if (!vsPid.HasValue)
            {
                // Single-instance fallback (③, minus the Wave 3 hint injection).
                var snap = Registry.Snapshot();
                vsPid = ResolveInitializeTarget(vsPid, snap.Count, () => snap.First().Info.Pid);
                if (!vsPid.HasValue)
                {
                    // 0 or ≥2 instances and no explicit pid → can't pick. Wave 2
                    // returns a simple error; Wave 3 will add the full intercept
                    // message guiding the user to configure/select.
                    int count = snap.Count;
                    string msg = count == 0
                        ? "No VS instance bound. Open a VS instance or use select_vs_instance."
                        : $"Multiple VS instances connected ({count}). Pass _meta.vsPid in initialize or call select_vs_instance.";
                    await WriteJsonRpcErrorAsync(ctx, body, -32001, msg).ConfigureAwait(false);
                    return;
                }
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
            // and the client would never receive its session id — breaking every
            // subsequent request. VS's own session id is unknown until the
            // forward returns, so the binding is created with a null placeholder
            // and patched once the head frame is observed.
            var (clientSessionId, binding) = Sessions.CreateWithId(vsPid.Value, vsSessionId: null, "initialize");

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
            if (result.Headers.TryGetValue("Mcp-Session-Id", out var vsSid) && !string.IsNullOrEmpty(vsSid))
                binding.VsSessionId = vsSid;

            try { ctx.Response.Close(); } catch { /* client gone */ }
        }

        /// <summary>
        /// tools/list: route to the bound VS, buffer the SSE response, inject the
        /// gateway tools, and write the rewritten bytes.
        /// </summary>
        private static async Task HandleToolsListAsync(
            HttpListenerContext ctx, string body, IDictionary<string, string> headers,
            string? clientSessionId, CancellationToken ct)
        {
            var (router, vsSessionId, error) = ResolveRouter(clientSessionId);
            if (router == null)
            {
                await WriteJsonRpcErrorAsync(ctx, body, -32003, error ?? "No VS instance bound.").ConfigureAwait(false);
                return;
            }

            var forwardedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(vsSessionId))
                forwardedHeaders["Mcp-Session-Id"] = vsSessionId;
            else
                forwardedHeaders.Remove("Mcp-Session-Id");

            // Buffer the entire response: tools/list is a single small SSE
            // message, and we need the full JSON to splice in our tools.
            using var buffer = new MemoryStream();
            await router.ForwardAsync(body, forwardedHeaders, buffer, ct).ConfigureAwait(false);

            byte[] injected = GatewayTools.InjectIntoToolsListSse(buffer.ToArray());

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

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
        /// Default path: route to the bound VS and stream the SSE response
        /// straight through.
        /// </summary>
        private static async Task HandleForwardAsync(
            HttpListenerContext ctx, string body, IDictionary<string, string> headers,
            string? clientSessionId, CancellationToken ct)
        {
            var (router, vsSessionId, error) = ResolveRouter(clientSessionId);
            if (router == null)
            {
                await WriteJsonRpcErrorAsync(ctx, body, -32003, error ?? "No VS instance bound.").ConfigureAwait(false);
                return;
            }

            var forwardedHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(vsSessionId))
                forwardedHeaders["Mcp-Session-Id"] = vsSessionId;
            else
            {
                // Session exists but VsSessionId is null (select switched to a
                // fresh VS). Wave 2's simplified contract: ask the client to
                // re-initialize rather than fabricate an initialize ourselves.
                await WriteJsonRpcErrorAsync(ctx, body, -32004,
                    "VS instance switched. Call initialize again to bind the new instance.").ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            if (!string.IsNullOrEmpty(clientSessionId))
                ctx.Response.Headers["Mcp-Session-Id"] = clientSessionId;

            using (var output = ctx.Response.OutputStream)
            {
                await router.ForwardAsync(body, forwardedHeaders, output, ct).ConfigureAwait(false);
            }
            try { ctx.Response.Close(); } catch { /* client gone */ }
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

        /// <summary>
        /// Resolve a router + VS session id for a non-initialize request via the
        /// ② Session tier. Returns null router + error message when unbound.
        /// </summary>
        private static (PipeRouter? Router, string? VsSessionId, string? Error) ResolveRouter(string? clientSessionId)
        {
            if (string.IsNullOrEmpty(clientSessionId) ||
                !Sessions.TryGet(clientSessionId!, out var binding))
            {
                return (null, null, "No VS instance bound. Call initialize first.");
            }

            if (!Registry.TryGet(binding.Pid, out var entry))
            {
                return (null, null, $"Bound VS instance PID {binding.Pid} is no longer connected.");
            }

            // entry.Router is non-null; tuple slot is nullable, assignment is safe.
            return (entry.Router, binding.VsSessionId, null);
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

        private static void TryDispose(IDisposable? d)
        {
            try { d?.Dispose(); } catch { /* best-effort */ }
        }
    }
}
