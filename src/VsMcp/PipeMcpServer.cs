using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
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
    /// 连接循环：连 Gateway → 发 register → 服务请求直到断开 → 重连（Gateway
    /// 抢占式重启后 VS 自动重新拨入）。客户端用 <see cref="PipeOptions.None"/> +
    /// <c>Task.Run(() =&gt; Connect(timeout))</c> —— net48 上唯一不挂死的 client
    /// 连接范式（Wave 1 已验证）。VS 端请求处理是串行的（读一帧、处理、回帧），
    /// 无并发读写，故 None handle 安全。
    /// </summary>
    public sealed class PipeMcpServer : IAsyncDisposable, IDisposable
    {
        private const string GatewayPipeName = "vs-mcp-gateway";

        private readonly int _pid;
        private readonly DebuggerFacade? _facade;
        private readonly McpRequestProcessor _processor;
        private readonly ILoggerFactory? _loggerFactory;

        private CancellationTokenSource? _loopCts;
        private Task? _connectLoop;

        private readonly SemaphoreSlim _disposeLock = new(1, 1);
        private bool _disposed;

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
                    // PipeOptions.None + 同步 Connect in Task.Run：net48 上唯一不挂死的
                    // client 连接范式（Asynchronous handle + 同步 Connect 行为未定义）。
                    pipe = new NamedPipeClientStream(
                        ".", GatewayPipeName, PipeDirection.InOut, PipeOptions.None);

                    using (ct.Register(() => { try { pipe.Dispose(); } catch { /* raced */ } }))
                    {
                        await Task.Run(() => pipe.Connect(2000), ct).ConfigureAwait(false);
                    }

                    logger?.LogInformation("Connected to gateway pipe {Pipe}", GatewayPipeName);

                    // Send the register frame first; if it fails, log and still
                    // enter the request loop (the Gateway can still route by PID
                    // if it recovers the connection).
                    await SendRegisterAsync(pipe, ct).ConfigureAwait(false);

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
                    // auto-recovers when the Gateway (re)starts.
                    logger?.LogDebug(ex, "Gateway pipe connect/serve error; will retry");
                }
                finally
                {
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
        private async Task SendRegisterAsync(Stream pipe, CancellationToken ct)
        {
            string? solutionName = null;
            string? solutionDir = null;
            string? solutionPath = null;

            if (_facade != null)
            {
                try
                {
                    var info = await _facade.GetSessionInfoAsync(ct).ConfigureAwait(false);
                    solutionPath = info.SolutionPath;
                    solutionDir = info.SolutionDir;
                    if (!string.IsNullOrEmpty(solutionPath))
                    {
                        try { solutionName = Path.GetFileName(solutionPath); }
                        catch { solutionName = null; }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Solution not open yet / DTE not ready — leave fields null;
                    // the register still goes out so the Gateway knows we exist.
                    // Wave 3's solution-changed notification will refresh it.
                }
            }

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
                await PipeFraming.WriteFrameAsync(pipe, register, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogDebug(ex, "Failed to send register frame; continuing");
            }
        }

        private static string TryGetVsVersion()
        {
            // DTE.Version would require a UI-thread hop; the register frame is
            // informational only, so a best-effort constant is acceptable. Wave 3
            // can enrich it if a live version turns out to matter for routing.
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
                    _loggerFactory?.CreateLogger<PipeMcpServer>()
                        ?.LogDebug(ex, "Pipe frame read failed; treating as disconnect");
                    return;
                }

                if (json == null)
                    return; // clean disconnect (Gateway closed the pipe)

                string type;
                try
                {
                    using (var doc = JsonDocument.Parse(json))
                    {
                        type = doc.RootElement.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                    }
                }
                catch (JsonException)
                {
                    // Malformed frame — skip it rather than tear down the connection.
                    continue;
                }

                if (string.Equals(type, "request", StringComparison.Ordinal))
                {
                    PipeRequest? req;
                    try { req = JsonSerializer.Deserialize<PipeRequest>(json); }
                    catch (JsonException) { continue; }

                    if (req != null)
                        await HandleRequestAsync(pipe, req, ct).ConfigureAwait(false);
                }
                // Other frame types (register echo / heartbeat) are silently
                // ignored so an older/newer peer pairing never breaks the stream.
            }
        }

        /// <summary>
        /// 处理一条转发请求：发 head → 处理 JSON-RPC（SSE 经 chunkSink 实时封帧）→ 发 end。
        /// head 中的状态码反映 body 是否可解析。
        /// </summary>
        private async Task HandleRequestAsync(Stream pipe, PipeRequest req, CancellationToken ct)
        {
            JsonRpcMessage? message = null;
            bool parsed = true;
            try
            {
                message = JsonSerializer.Deserialize<JsonRpcMessage>(req.Body, McpJsonUtilities.DefaultOptions);
                if (message == null) parsed = false;
            }
            catch (JsonException)
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
            await PipeFraming.WriteFrameAsync(pipe, head, ct).ConfigureAwait(false);

            if (!parsed)
            {
                await PipeFraming.WriteFrameAsync(pipe, new PipeEnd { Id = req.Id }, ct).ConfigureAwait(false);
                return;
            }

            // chunkSink turns every SDK SSE write into a real-time data frame so
            // build_solution keep-alive notifications reach the client unbuffered.
            var sink = new PipeChunkSink(pipe, req.Id);
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

            await PipeFraming.WriteFrameAsync(pipe, new PipeEnd { Id = req.Id }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 把 SDK 写入的 SSE 字节流实时转成 <see cref="PipeDataChunk"/> 帧的适配 Stream。
        /// 每次写入立即封帧发出（不缓冲），保证长任务的保活通知不被吞掉。字节以 base64
        /// 承载，规避 SDK 任意写入粒度下 UTF-8 多字节字符跨块边界的解码问题。
        /// </summary>
        private sealed class PipeChunkSink : Stream
        {
            private readonly Stream _pipe;
            private readonly string _id;

            public PipeChunkSink(Stream pipe, string id)
            {
                _pipe = pipe;
                _id = id;
            }

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
                PipeFraming.WriteFrame(_pipe, new PipeDataChunk
                {
                    Id = _id,
                    Body = Convert.ToBase64String(buffer, offset, count),
                });
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (count <= 0) return;
                await PipeFraming.WriteFrameAsync(_pipe, new PipeDataChunk
                {
                    Id = _id,
                    Body = Convert.ToBase64String(buffer, offset, count),
                }, cancellationToken).ConfigureAwait(false);
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
