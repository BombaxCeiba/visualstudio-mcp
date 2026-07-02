using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// 维护到单个 VS 实例的 NamedPipe 连接，把 HTTP 层的 JSON-RPC 请求转发过去，
    /// 再把 VS 返回的 SSE 字节流（head/data/end 帧）透传回 HTTP 响应流。
    ///
    /// Wave 1：单实例、单连接、串行（<see cref="_sendLock"/> 保证同一时刻只有一条
    /// 请求在 pipe 上飞行）。多实例路由、session 绑定、并发 demux 留待 Wave 2。
    /// </summary>
    public sealed class PipeRouter : IDisposable, IAsyncDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream? _client;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public PipeRouter(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("pipeName is required", nameof(pipeName));
            _pipeName = pipeName;
        }

        /// <summary>当前是否已连上一个 VS 实例。</summary>
        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>
        /// 连接目标 VS 的 pipe，重试直到成功或取消。VS 可能在 Gateway 之后启动，
        /// 故轮询重试（每 500ms）而非立即失败。
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // PipeOptions.None（同步 pipe）：Asynchronous flag 使 handle 成为
                // FILE_FLAG_OVERLAPPED，在其上做同步 Connect/Read 行为未定义（实测会
                // 挂死）。同步 pipe + Task.Run 异步化是 .NET Framework 上最确定的用法。
                var client = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.InOut, PipeOptions.None);

                try
                {
                    // 用同步 Connect(int timeout) —— 它是 .NET Framework 4.x 全版本
                    // 确定可用的连接 API（异步重载 ConnectAsync(CancellationToken) 与
                    // ConnectAsync(int) 的可用性随 4.8 子版本不一致，testhost 运行时
                    // 可能缺失）。在 Task.Run 中异步化，2s 超时让 VS pipe 一就绪即被
                    // 发现；失败则短暂退避重试。
                    await Task.Run(() => client.Connect(2000), ct).ConfigureAwait(false);
                    _client = client;
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(client);
                    throw;
                }
                catch (Exception)
                {
                    // Pipe 还没创建（VS 未启动）或 2s 超时 —— 释放本次 client，等 500ms 再试。
                    TryDispose(client);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 转发一条 JSON-RPC 请求：封 <see cref="PipeRequest"/> 发往 VS → 读取
        /// head/data/end 帧序列 → data 帧的 base64 body 解码后实时写入
        /// <paramref name="outputStream"/>（保活通知不被缓冲）。返回 VS head 中的
        /// HTTP 状态码（用于写 HTTP 响应行）。
        /// </summary>
        public async Task<int> ForwardAsync(
            string jsonRpcBody,
            IDictionary<string, string>? requestHeaders,
            Stream outputStream,
            CancellationToken ct)
        {
            var client = _client ?? throw new InvalidOperationException("PipeRouter is not connected.");
            string id = Guid.NewGuid().ToString("N");

            var headers = new Dictionary<string, string>();
            if (requestHeaders != null)
            {
                foreach (var kv in requestHeaders)
                    headers[kv.Key] = kv.Value;
            }

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteFrameAsync(client, new PipeRequest
                {
                    Id = id,
                    Body = jsonRpcBody,
                    Headers = headers,
                }, ct).ConfigureAwait(false);

                // head —— 拿到 VS 决定的 HTTP 状态码（200 / 400 / ...）。
                PipeResponseHead? head = await PipeFraming.ReadFrameAsync<PipeResponseHead>(client, ct).ConfigureAwait(false);
                int status = head?.Status ?? 200;

                // data* —— 逐帧透传，base64 解码后写 HTTP body 并 flush（实时性）。
                while (true)
                {
                    string? json;
                    try
                    {
                        json = await PipeFraming.ReadFrameJsonAsync(client, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsBenignDisconnect(ex))
                    {
                        // VS 断开 → 提前结束。
                        break;
                    }

                    if (json == null)
                        break; // clean disconnect

                    // 单次 Parse：取 type 分派；data 帧直接从同一 document 取 body
                    // （协议统一 camelCase，见 PipeFraming.Options；不复用 Deserialize
                    // 以避免 options 不一致导致字段丢失）。
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                            continue;
                        string type = typeEl.GetString() ?? "";

                        if (string.Equals(type, "data", StringComparison.Ordinal))
                        {
                            if (doc.RootElement.TryGetProperty("body", out var bodyEl))
                            {
                                string? bodyBase64 = bodyEl.GetString();
                                if (!string.IsNullOrEmpty(bodyBase64))
                                {
                                    byte[] bytes = Convert.FromBase64String(bodyBase64);
                                    await outputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                                    await outputStream.FlushAsync(ct).ConfigureAwait(false);
                                }
                            }
                        }
                        else if (string.Equals(type, "end", StringComparison.Ordinal))
                        {
                            break;
                        }
                        // 其他类型在 Wave 1 不应出现在响应流中，忽略。
                    }
                }

                return status;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static bool IsBenignDisconnect(Exception ex)
        {
            return ex is IOException || ex is ObjectDisposedException;
        }

        /// <summary>同步停机：非阻塞地释放底层 pipe 连接（Gateway 进程退出路径）。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _client?.Dispose(); } catch { /* best-effort */ }
            _client = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                TryDispose(_client);
                _client = null;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static void TryDispose(IDisposable? disposable)
        {
            try { disposable?.Dispose(); } catch { /* best-effort */ }
        }
    }
}
