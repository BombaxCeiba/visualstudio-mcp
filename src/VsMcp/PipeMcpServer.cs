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
    /// VS 端的 NamedPipe 监听端：独占一条名为 <c>\\.\pipe\vs-mcp-{PID}</c> 的管道，
    /// 接受 Gateway 的连接，把收到的 <see cref="PipeRequest"/> 帧交给共享的
    /// <see cref="McpRequestProcessor"/> 处理，SDK 产出的 SSE 响应字节通过
    /// <see cref="PipeChunkSink"/> 实时封成 <see cref="PipeDataChunk"/> 帧回传。
    ///
    /// 与 <see cref="McpHttpServer"/> 共用同一个处理器，因此工具集、错误语义、
    /// 保活通知完全一致；唯一区别是传输层从独占式 HTTP 端口换成无端口冲突的
    /// NamedPipe，让多个 VS 实例可并存于单一 Gateway 之后。
    ///
    /// 连接循环：接受一个 Gateway 连接 → 服务直到断开 → 重新等待下一个连接，
    /// 这样 Gateway 抢占式重启（见设计文档 §抢占式 Gateway 拉起）后 VS 能自动重连。
    /// </summary>
    public sealed class PipeMcpServer : IAsyncDisposable, IDisposable
    {
        private readonly string _pipeName;
        private readonly McpRequestProcessor _processor;
        private readonly ILoggerFactory? _loggerFactory;

        private CancellationTokenSource? _acceptCts;
        private Task? _acceptLoop;

        private readonly SemaphoreSlim _disposeLock = new(1, 1);
        private bool _disposed;

        /// <param name="pid">VS 进程 ID，用于命名管道（vs-mcp-{pid}）。</param>
        /// <param name="callerToken">VS package 的 DisposalToken；取消时传播到处理器与 accept 循环。</param>
        public PipeMcpServer(
            int pid,
            DebuggerFacade? facade,
            SymbolFacade? symbolFacade,
            bool enableGoToDefinition,
            ILoggerFactory? loggerFactory,
            CancellationToken callerToken)
        {
            _pipeName = "vs-mcp-" + pid;
            _loggerFactory = loggerFactory;
            _processor = new McpRequestProcessor(callerToken, facade, symbolFacade, enableGoToDefinition, loggerFactory);
        }

        /// <summary>本实例监听的管道名（不含 \\.\pipe\ 前缀）。</summary>
        public string PipeName => _pipeName;

        /// <summary>
        /// 启动 accept 循环。循环在后台运行直到 <paramref name="cancellationToken"/>
        /// 取消或 <see cref="DisposeAsync"/> 被调用；方法本身在循环启动后立即返回
        /// （VS 有 package 加载超时，绝不能在 InitializeAsync 里阻塞）。
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var logger = _loggerFactory?.CreateLogger<PipeMcpServer>();
            logger?.LogInformation("VS MCP pipe server listening on \\\\.\\pipe\\{Pipe}", _pipeName);

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));
            return Task.CompletedTask;
        }

        /// <summary>
        /// 连接循环：每轮创建一个新的 NamedPipeServerStream 等待一个 Gateway 连接，
        /// 服务至连接断开，然后重新等待下一个连接（支持 Gateway 重连）。
        /// </summary>
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var logger = _loggerFactory?.CreateLogger<PipeMcpServer>();
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    // maxNumberOfServerInstances=1: 本循环串行，同一时刻只有一个
                    // pipe 实例存活，避免与下一轮的实例撞名。
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    logger?.LogInformation("Gateway connected to pipe {Pipe}", _pipeName);

                    await ServeConnectionAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown — drop the pipe and exit the loop.
                    TryDispose(pipe);
                    return;
                }
                catch (Exception ex)
                {
                    // A single connection failure must not kill the accept loop —
                    // the Gateway may reconnect. Log and retry on the next iteration.
                    logger?.LogError(ex, "Pipe accept/serve error on {Pipe}; will retry", _pipeName);
                }
                finally
                {
                    TryDispose(pipe);
                }
            }
        }

        /// <summary>
        /// 服务一个 Gateway 连接：读帧循环，按 type 分派。Wave 1 只处理
        /// <c>request</c>（register / heartbeat / solution-changed 在后续波次接入）；
        /// 未知帧类型被忽略以保持向前兼容。循环在干净断开（ReadFrameJson 返回 null）
        /// 或 shutdown 时退出，交还控制权给 AcceptLoop 等待重连。
        /// </summary>
        private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
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
                    // AcceptLoop wait for a reconnect (Gateway crash, taskkill, etc.).
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
                // Other frame types (register/heartbeat/solution-changed) are handled
                // in later waves; silently ignore here so an older/newer peer pairing
                // never breaks the request stream.
            }
        }

        /// <summary>
        /// 处理一条转发请求：发 head → 处理 JSON-RPC（SSE 经 chunkSink 实时封帧）→ 发 end。
        /// head 中的状态码反映 body 是否可解析；HTTP 层的 200/text/event-stream 由 Gateway
        /// 透传。
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
                // Synchronous path used only if the SDK ever calls Write directly;
                // PipeFraming.WriteFrame writes via Stream.Write (no GetResult),
                // so this stays VSTHRD002-clean.
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
        /// 同步停机入口：翻转 _disposed、取消 accept 循环，fire-and-forget 异步销毁。
        /// 绝不阻塞调用线程（VS exit 时 package 同步调用此路径）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _acceptCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _processor.Dispose(); } catch { /* teardown must not throw */ }
        }

        /// <summary>
        /// 异步停机：取消 accept 循环 → 观察循环退出 → dispose 处理器（SDK loop + transport）。
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

                try { _acceptCts?.Cancel(); } catch (ObjectDisposedException) { }

                if (_acceptLoop != null)
                {
                    // Background accept loop owns no unmanaged resource beyond the pipes
                    // it disposes itself; observing it just confirms it has unwound.
#pragma warning disable VSTHRD003
                    try { await _acceptLoop.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { /* teardown must not throw */ }
#pragma warning restore VSTHRD003
                }

                try { await _processor.DisposeAsync().ConfigureAwait(false); }
                catch { /* teardown must not throw */ }

                _acceptCts?.Dispose();
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
