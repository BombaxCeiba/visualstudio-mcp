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

namespace VsMcp
{
    /// <summary>
    /// HTTP 监听端：用 <see cref="HttpListener"/> 接收 MCP JSON-RPC 请求，
    /// 把每条消息交给共享的 <see cref="McpRequestProcessor"/> 处理，SDK 产出的
    /// SSE 响应字节直接写入 <see cref="HttpListenerResponse.OutputStream"/>。
    ///
    /// 这是"单实例直连"传输路径（每个 VS 实例各自监听一个端口）。多实例场景下
    /// VS 端改用 <see cref="PipeMcpServer"/>，由独立 Gateway 进程独占端口 —— 但
    /// 两者共用同一个 <see cref="McpRequestProcessor"/>，工具集与错误语义完全一致。
    /// 本文件保留作回滚保险，并承载现有的 HTTP 单元/集成测试。
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

        // MCP SDK 资源（transport + server + 工具注册）由共享处理器持有。HTTP 端
        // 只负责把字节在 HTTP 流与处理器之间搬运。
        private McpRequestProcessor? _processor;

        private bool _disposed;

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
                logger?.LogError(ex,
                    "Failed to bind MCP HTTP listener on port {Port} (native error {NativeErr}). " +
                    "If 'AccessDenied', run: netsh http add urlacl url=http://127.0.0.1:{Port}/mcp/ user=Everyone",
                    _port, ex.ErrorCode, _port);
                throw;
            }

            // D-05: log the server-start event on success.
            logger?.LogInformation("MCP server listening on http://127.0.0.1:{Port}/mcp", _port);

            // Build the shared processor with the caller's token (VS package
            // DisposalToken) so cancelling EITHER the caller OR the processor's
            // own DisposeAsync propagates to the SDK RunAsync loop.
            _processor = new McpRequestProcessor(
                cancellationToken, _facade, _symbolFacade, _enableGoToDefinition, _loggerFactory);

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
        /// Handles an MCP JSON-RPC POST request by bridging the HTTP stream to the shared
        /// MCP processor. Deserializes the JSON-RPC message from the request body, hands
        /// it to <see cref="McpRequestProcessor.HandleAsync"/>, and writes the SSE response
        /// back to the HTTP response stream.
        /// </summary>
        private async Task HandleMcpRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var processor = _processor;
            if (processor == null)
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
                wroteResponse = await processor.HandleAsync(
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
        /// VS exit). Flips the _disposed flag, asks the processor to cancel
        /// (which lets the SDK self-dispose) and stops/closes the listener.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Processor cancel is non-blocking (cancels its CTS, SDK self-disposes).
            try { _processor?.Dispose(); } catch { /* teardown must not throw */ }

            // Stop the listener synchronously. Unblocks GetContextAsync on
            // most Windows builds (the processor's CTS cancellation is the
            // reliable unblocker for the SDK loop).
            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _listener.Close(); } catch { /* already closed */ }
        }

        /// <summary>
        /// Asynchronous teardown: ordered to avoid deadlocks (RESEARCH Pitfall #3):
        /// stop listener -> await processor disposal (SDK loop + transport). The
        /// processor's own DisposeAsync observes the SDK run task and disposes the
        /// transport. Idempotent via _disposeLock + _disposed.
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

                // 1. Stop the listener (best-effort).
                try { _listener.Stop(); } catch { /* already stopped */ }

                // 2. Await the shared processor's full async teardown (SDK run loop +
                //    transport). Any exception is swallowed so teardown never throws.
                if (_processor != null)
                {
                    try { await _processor.DisposeAsync().ConfigureAwait(false); }
                    catch { /* teardown must not throw */ }
                }

                // 3. Close the listener.
                try { _listener.Close(); } catch { /* already closed */ }
            }
            finally
            {
                _disposeLock.Release();
            }
        }
    }
}
