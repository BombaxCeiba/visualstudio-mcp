using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using VsMcp.Common;

namespace VsMcp
{
    /// <summary>
    /// VS 端的 NamedPipe 客户端端：主动连到 Gateway 监听的
    /// <c>\\.\pipe\vs-mcp-gateway</c>，连接建立后先发一帧 <see cref="PipeRegister"/>
    /// （携带 PID + solution info），然后进入请求读循环，把收到的
    /// <see cref="PipeRequest"/> 帧交给共享的 <see cref="McpRequestProcessor"/> 处理。
    ///
    /// Wave 2 反转了 Wave 1 的方向：Wave 1 里 VS 做 server（等 Gateway 连
    /// <c>vs-mcp-{pid}</c>），但多实例下 Gateway 无法枚举 PID 去反向连接每个 VS。
    /// 改为 Gateway 做固定名 server，所有 VS 主动连过来，首帧 register 自报身份。
    ///
    /// Wave 3 增加了 VS 主动推送 <c>solution-changed</c> 控制帧的能力（用户打开/
    /// 关闭解决方案时通知 Gateway 刷新路由表）。该推送与请求-响应帧共用同一条
    /// pipe，因此所有写操作经 <see cref="_sendLock"/> 串行化，防止长度前缀帧在并发
    /// 写下字节交错而损坏协议。
    ///
    /// 连接循环：连 Gateway → 发 register → 服务请求直到断开 → 重连（Gateway
    /// 抢占式重启后 VS 自动重新拨入）。客户端用 <see cref="PipeOptions.None"/> +
    /// <c>Task.Run(() =&gt; Connect(timeout))</c> —— net48 上唯一不挂死的 client
    /// 连接范式（Wave 1 已验证）。VS 端请求处理是串行的（读一帧、处理、回帧），
    /// 无并发读写，故 None handle 安全。
    /// </summary>
    public sealed class PipeMcpServer : IAsyncDisposable, IDisposable
    {
        private const string GatewayPipeName = "vs-mcp-gateway";

        /// <summary>
        /// Max wait for the send lock when emitting a heartbeat. A heartbeat must
        /// never queue behind a long SSE stream write — if the lock is held this
        /// long, treat it as a connectivity problem and count a miss rather than
        /// stalling the heartbeat cadence.
        /// </summary>
        private const int HeartbeatLockTimeoutMs = 2000;

        private readonly int _pid;
        private readonly DebuggerFacade? _facade;
        private readonly McpRequestProcessor _processor;
        private readonly ILoggerFactory? _loggerFactory;

        private CancellationTokenSource? _loopCts;
        private Task? _connectLoop;

        private readonly SemaphoreSlim _disposeLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Serializes every pipe write. NamedPipe streams are full-duplex (a
        /// read in <see cref="ServeConnectionAsync"/> never blocks a write), but
        /// two concurrent WRITERS would interleave the bytes of two
        /// length-prefixed frames and corrupt the protocol. The request-response
        /// loop and the <c>solution-changed</c> push both write through this
        /// lock so frames stay atomic.
        /// </summary>
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>
        /// The pipe currently connected to the Gateway, or null while
        /// disconnected/reconnecting. Captured into a local before each write so
        /// a reconnect mid-write can't hand a disposed stream to a framing call.
        /// </summary>
        private volatile Stream? _activePipe;

        /// <param name="pid">VS 进程 ID，写入 register 帧。</param>
        /// <param name="facade">调试器 facade；非空时 register 帧携带 solution info。</param>
        /// <param name="callerToken">VS package 的 DisposalToken；取消时传播到处理器与连接循环。</param>
        public PipeMcpServer(
            int pid,
            DebuggerFacade? facade,
            SymbolFacade? symbolFacade,
            bool enableGoToDefinition,
            ILoggerFactory? loggerFactory,
            CancellationToken callerToken)
        {
            _pid = pid;
            _facade = facade;
            _loggerFactory = loggerFactory;
            _processor = new McpRequestProcessor(callerToken, facade, symbolFacade, enableGoToDefinition, loggerFactory);
        }

        /// <summary>
        /// 启动连接循环。循环在后台运行直到 <paramref name="cancellationToken"/>
        /// 取消或 <see cref="DisposeAsync"/> 被调用；方法本身在循环启动后立即返回。
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var logger = _loggerFactory?.CreateLogger<PipeMcpServer>();
            logger?.LogInformation("VS MCP pipe client connecting to \\\\.\\pipe\\{Pipe}", GatewayPipeName);

            _connectLoop = Task.Run(() => ConnectLoopAsync(_loopCts.Token));
            return Task.CompletedTask;
        }

        /// <summary>
        /// 连接循环：反复尝试拨入 Gateway 的 <c>vs-mcp-gateway</c> pipe，连上后
        /// 发 register 帧并服务请求直到连接断开，然后重试（Gateway 还没起 / 重启）。
        /// </summary>
        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            var logger = _loggerFactory?.CreateLogger<PipeMcpServer>();
            while (!ct.IsCancellationRequested)
            {
                NamedPipeClientStream? pipe = null;
                try
                {
                    // Asynchronous (overlapped) handle + async Connect/Read/Write
                    // throughout. The prior PipeOptions.None + async ReadAsync/WriteAsync
                    // was an inconsistent pairing: None makes a non-overlapped handle,
                    // and async IO on it failed to actually flush, so the VS side
                    // connected but never sent its register frame (the Gateway saw
                    // connection-then-immediate-EOF). An overlapped handle requires
                    // async Connect too — synchronous Connect on an overlapped handle
                    // deadlocks on net48 (Wave 1 lesson) — so ConnectAsync is raced
                    // against a 2s timeout.
                    pipe = new NamedPipeClientStream(
                        ".", GatewayPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                    using (ct.Register(() => { try { pipe.Dispose(); } catch { /* raced */ } }))
                    {
                        Task connectTask = pipe.ConnectAsync();
                        Task winner = await Task.WhenAny(connectTask, Task.Delay(2000, ct)).ConfigureAwait(false);
                        if (winner != connectTask)
                            throw new TimeoutException("Timed out connecting to the gateway pipe.");
                        await connectTask.ConfigureAwait(false); // observe connect errors
                    }

                    logger?.LogInformation("Connected to gateway pipe {Pipe}", GatewayPipeName);

                    // Publish the live stream so solution-changed pushes can find it.
                    // Set BEFORE register so a rapid open-solution event between
                    // register and the read loop still has a pipe to send on.
                    _activePipe = pipe;

                    // Send the register frame first; if it fails, log and still
                    // enter the request loop (the Gateway can still route by PID
                    // if it recovers the connection).
                    await SendRegisterAsync(ct).ConfigureAwait(false);

                    await ServeConnectionAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(pipe);
                    return;
                }
                catch (Exception ex)
                {
                    // Connect failed (Gateway not up yet) or the established
                    // connection dropped — retry after a short backoff so VS
                    // auto-recovers when the Gateway (re)starts. Information-level
                    // so connection problems surface in the VS Output window during
                    // startup (Debug is hidden by the package's Information filter).
                    logger?.LogInformation(ex, "Gateway pipe connect/serve error; will retry");
                }
                finally
                {
                    // Tear down: a fresh stream is created on the next iteration.
                    _activePipe = null;
                    TryDispose(pipe);
                }

                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }

        /// <summary>
        /// 发送 register 帧：PID 来自构造参数，solution info 从 facade 取
        /// （facade.GetSessionInfoAsync 会自己切到 UI 线程；这里直接调用并容忍
        /// 失败 —— solution 还没打开时返回空字段，register 仍要发出去）。
        /// </summary>
        private async Task SendRegisterAsync(CancellationToken ct)
        {
            string? solutionName = null;
            string? solutionDir = null;
            string? solutionPath = null;

            // 不在此取 solution info：GetSessionInfoAsync 会 SwitchToMainThreadAsync 切 UI，
            // 而 ConnectLoop 是 Task.Run 启动的裸后台线程（无 JoinableTask 上下文），从它
            // 切 UI 会卡死，register 帧永远发不出去、Gateway 收不到任何注册。先把 PID
            // 注册上保证连通；solution info 由 solution-changed（IVsSolutionEvents 回调本就
            // 在 UI 线程，安全）补充。
            _loggerFactory?.CreateLogger<PipeMcpServer>()
                ?.LogInformation("SendRegister: sending register frame (pid={Pid})", _pid);

            var register = new PipeRegister
            {
                Pid = _pid,
                PipeName = GatewayPipeName,
                SolutionName = solutionName,
                SolutionDir = solutionDir,
                SolutionPath = solutionPath,
                VsVersion = TryGetVsVersion(),
            };

            try
            {
                await WriteLockedAsync(register, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Information-level: a register failure means VS isn't visible to
                // the Gateway yet, which is exactly the kind of connection problem
                // users need to see in the Output window to diagnose.
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogInformation(ex, "Failed to send register frame; continuing");
            }
        }

        private static string TryGetVsVersion()
        {
            // DTE.Version would require a UI-thread hop; the register frame is
            // informational only, so a best-effort constant is acceptable.
            try
            {
                return Environment.Version?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Push a <c>solution-changed</c> control frame so the Gateway refreshes
        /// this instance's SolutionDir in its routing table (设计文档 §Solution
        /// 信息动态更新). Fire-and-forget by the caller (solution-events
        /// subscriber); swallows IO errors so a transient Gateway disconnect
        /// never crashes the UI thread. The next register (on reconnect) carries
        /// fresh info, so a dropped push self-heals. No-op when not currently
        /// connected (<see cref="_activePipe"/> == null).
        /// </summary>
        public async Task SendSolutionChangedAsync(
            string? solutionPath, string? solutionDir, string? solutionName, CancellationToken ct)
        {
            if (_disposed) return;
            try
            {
                var frame = new PipeSolutionChanged
                {
                    Pid = _pid,
                    SolutionName = solutionName,
                    SolutionDir = solutionDir,
                    SolutionPath = solutionPath,
                };
                await WriteLockedAsync(frame, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogDebug(ex, "Failed to send solution-changed frame");
            }
        }

        /// <summary>
        /// Send a <c>heartbeat</c> control frame (no id) so the Gateway knows
        /// this VS is alive, and — equally important — so the VS-side
        /// <see cref="HeartbeatClient"/> can confirm the pipe is still writable
        /// (i.e. the Gateway process is still up). Throws on any failure (no
        /// active pipe, write error, send-lock contention) so HeartbeatClient
        /// can count a consecutive miss; does NOT log, because heartbeat misses
        /// are expected during a Gateway restart and a log line per miss would
        /// spam the output window.
        /// </summary>
        public async Task SendHeartbeatAsync(CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PipeMcpServer));

            Stream? pipe = _activePipe;
            if (pipe == null)
                throw new IOException("Not connected to the gateway pipe.");

            // Acquire the send lock with a short timeout rather than an indefinite
            // wait: a heartbeat that queues behind a long SSE stream write would
            // skew the liveness signal. A timeout here surfaces as a miss, which
            // HeartbeatClient handles gracefully.
            if (!await _sendLock.WaitAsync(HeartbeatLockTimeoutMs, ct).ConfigureAwait(false))
                throw new TimeoutException("Timed out waiting for the pipe send lock to send heartbeat.");

            try
            {
                await PipeFraming.WriteFrameAsync(pipe, new PipeHeartbeat { Pid = _pid }, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>Write one frame under <see cref="_sendLock"/> (async path).</summary>
        private async Task WriteLockedAsync(object payload, CancellationToken ct)
        {
            Stream? pipe = _activePipe;
            if (pipe == null) return;
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteFrameAsync(pipe, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>Write one frame under <see cref="_sendLock"/> (sync path,
        /// used by the SDK's synchronous Stream.Write calls via the chunk sink).</summary>
        private void WriteLocked(object payload)
        {
            Stream? pipe = _activePipe;
            if (pipe == null) return;
            _sendLock.Wait();
            try
            {
                PipeFraming.WriteFrame(pipe, payload);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 服务一个 Gateway 连接：读帧循环，按 type 分派。只处理 <c>request</c>；
        /// 未知帧类型被忽略以保持向前兼容。循环在干净断开（ReadFrameJson 返回
        /// null）或 shutdown 时退出，交还控制权给 ConnectLoop 等待重连。
        /// </summary>
        private async Task ServeConnectionAsync(NamedPipeClientStream pipe, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? json;
                try
                {
                    json = await PipeFraming.ReadFrameJsonAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Truncated frame / IO error → treat as connection loss and let
                    // ConnectLoop retry (Gateway crash, taskkill, etc.).
                    // Information-level so disconnects are visible during diagnosis.
                    _loggerFactory?.CreateLogger<PipeMcpServer>()
                        ?.LogInformation(ex, "Pipe frame read failed; treating as disconnect");
                    return;
                }

                if (json == null)
                    return; // clean disconnect (Gateway closed the pipe)

                string type;
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        type = doc.RootElement.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Malformed frame — skip it rather than tear down the connection.
                    continue;
                }

                if (string.Equals(type, "request", StringComparison.Ordinal))
                {
                    PipeRequest? req;
                    try { req = System.Text.Json.JsonSerializer.Deserialize<PipeRequest>(json, PipeFraming.Options); }
                    catch (System.Text.Json.JsonException) { continue; }

                    if (req != null)
                    {
                        // Log each incoming MCP request body so connection / call
                        // issues are visible in the VS Output window — the package
                        // logger factory runs at Information level.
                        _loggerFactory?.CreateLogger<PipeMcpServer>()
                            ?.LogInformation("MCP request {Id}: {Body}", req.Id, req.Body);
                        await HandleRequestAsync(pipe, req, ct).ConfigureAwait(false);
                    }
                }
                // Other frame types (register echo / heartbeat) are silently
                // ignored so an older/newer peer pairing never breaks the stream.
            }
        }

        /// <summary>
        /// 处理一条转发请求：发 head → 处理 JSON-RPC（SSE 经 chunkSink 实时封帧）→ 发 end。
        /// head 中的状态码反映 body 是否可解析。
        /// </summary>
        private async Task HandleRequestAsync(NamedPipeClientStream pipe, PipeRequest req, CancellationToken ct)
        {
            JsonRpcMessage? message = null;
            bool parsed = true;
            try
            {
                message = System.Text.Json.JsonSerializer.Deserialize<JsonRpcMessage>(req.Body, McpJsonUtilities.DefaultOptions);
                if (message == null) parsed = false;
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = false;
            }

            var head = new PipeResponseHead
            {
                Id = req.Id,
                Status = parsed ? 200 : 400,
                Headers =
                {
                    ["Content-Type"] = "text/event-stream",
                    ["Cache-Control"] = "no-cache",
                },
            };
            await WriteLockedAsync(head, ct).ConfigureAwait(false);

            // chunkSink turns every SDK SSE write into a real-time data frame so
            // build_solution keep-alive notifications reach the client unbuffered.
            // Created up here (not just in the parsed path) so the response log
            // below has a valid byte count on both branches.
            var sink = new PipeChunkSink(this, req.Id);

            if (!parsed)
            {
                await WriteLockedAsync(new PipeEnd { Id = req.Id }, ct).ConfigureAwait(false);
                LogResponse(req.Id, head.Status, sink.WrittenBytes);
                return;
            }

            try
            {
                await _processor.HandleAsync(message!, sink, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown mid-call — emit end so the Gateway can tear down cleanly.
            }
            catch (Exception ex)
            {
                // Tool-level exceptions are already converted to error results inside
                // the processor (SafeCall); reaching here means a transport-layer fault.
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogError(ex, "Unexpected error handling pipe request {Id}", req.Id);
            }

            await WriteLockedAsync(new PipeEnd { Id = req.Id }, ct).ConfigureAwait(false);
            LogResponse(req.Id, head.Status, sink.WrittenBytes);
        }

        /// <summary>
        /// Log the per-call response summary. Extracted so both the parsed and
        /// parse-failure paths in <see cref="HandleRequestAsync"/> emit one
        /// response line, giving every request a matched response trace.
        /// </summary>
        private void LogResponse(string id, int status, long bytes)
        {
            // Response summary: the SSE body was streamed to the sink in fragments,
            // so log the aggregated byte count + HTTP status rather than each chunk.
            // This pairs with the request log in ServeConnectionAsync to give a
            // per-call trace in the VS Output window.
            _loggerFactory?.CreateLogger<PipeMcpServer>()
                ?.LogInformation("MCP response {Id}: status {Status}, body bytes {Bytes}", id, status, bytes);
        }

        /// <summary>
        /// 把 SDK 写入的 SSE 字节流实时转成 <see cref="PipeDataChunk"/> 帧的适配 Stream。
        /// 每次写入立即封帧发出（不缓冲），保证长任务的保活通知不被吞掉。字节以 base64
        /// 承载，规避 SDK 任意写入粒度下 UTF-8 多字节字符跨块边界的解码问题。
        /// 写入经所属 <see cref="PipeMcpServer.WriteLocked(object)"/> 串行化，与
        /// solution-changed 推送互不交错。
        /// </summary>
        private sealed class PipeChunkSink : Stream
        {
            private readonly PipeMcpServer _owner;
            private readonly string _id;

            // Counts original (pre-base64) body bytes written by the SDK so
            // HandleRequestAsync can log a response-size summary per MCP call.
            // SSE responses are streamed in fragments; recording total bytes
            // (rather than the fragmented chunks) gives one meaningful number.
            private long _written;

            public PipeChunkSink(PipeMcpServer owner, string id)
            {
                _owner = owner;
                _id = id;
            }

            /// <summary>Total original body bytes streamed through this sink.</summary>
            public long WrittenBytes => Interlocked.Read(ref _written);

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                _owner.WriteLocked(new PipeDataChunk
                {
                    Id = _id,
                    Body = Convert.ToBase64String(buffer, offset, count),
                });
                Interlocked.Add(ref _written, count);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (count <= 0) return;
                await _owner.WriteLockedAsync(new PipeDataChunk
                {
                    Id = _id,
                    Body = Convert.ToBase64String(buffer, offset, count),
                }, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _written, count);
            }
        }

        /// <summary>
        /// 同步停机入口：翻转 _disposed、取消连接循环，fire-and-forget 异步销毁。
        /// 绝不阻塞调用线程（VS exit 时 package 同步调用此路径）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _loopCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _processor.Dispose(); } catch { /* teardown must not throw */ }
        }

        /// <summary>
        /// 异步停机：取消连接循环 → 观察循环退出 → dispose 处理器（SDK loop + transport）。
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

                try { _loopCts?.Cancel(); } catch (ObjectDisposedException) { }

                if (_connectLoop != null)
                {
#pragma warning disable VSTHRD003
                    try { await _connectLoop.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { /* teardown must not throw */ }
#pragma warning restore VSTHRD003
                }

                try { await _processor.DisposeAsync().ConfigureAwait(false); }
                catch { /* teardown must not throw */ }

                _loopCts?.Dispose();
            }
            finally
            {
                _disposeLock.Release();
            }
        }

        private static void TryDispose(IDisposable? disposable)
        {
            try { disposable?.Dispose(); } catch { /* best-effort */ }
        }
    }
}
