using System;
using System.Collections.Concurrent;
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
    /// 转发单个请求的结果：VS head 的 HTTP 状态码加上 head 的响应 headers
    /// （让 Gateway 在 initialize 时能读到 VS 分配的 Mcp-Session-Id）。Wave 1
    /// 调用方忽略 headers，只读 <see cref="Status"/>。
    /// </summary>
    public sealed class ForwardResult
    {
        public int Status { get; }
        public Dictionary<string, string> Headers { get; }

        public ForwardResult(int status, Dictionary<string, string> headers)
        {
            Status = status;
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>隐式 int 转换让 Wave 1 的 "int status = await ForwardAsync(...)" 写法顺手。</summary>
        public static implicit operator int(ForwardResult r) => r.Status;
    }

    /// <summary>
    /// 维护到单个 VS 实例的 pipe 通道，并在其上多路复用多个并发 MCP 请求。每个
    /// 请求获得一个新的 id；单个后台读循环按 id 把到达的 head/data/end 帧分派给
    /// 对应的 pending 请求。这让 Gateway 能在一条 pipe 上转发多个在途 SSE 流
    /// （每个 MCP 客户端会话一条），没有队头阻塞。
    ///
    /// 读循环也识别 VS 推送的不带 id 的 CONTROL 帧（Wave 3 的 solution-changed；
    /// Wave 4 的 heartbeat）。每种控制帧有自己的可选回调；未识别的控制帧被忽略，
    /// 所以新旧版本配对绝不会破坏流。
    ///
    /// 两种构造模式：
    /// <list type="bullet">
    /// <item>Gateway 的 register-accept 循环传入一个已连接的
    ///     <see cref="NamedPipeServerStream"/>（VS 拨入
    ///     "vs-mcp-gateway"）；router 只拥有读循环。</item>
    /// <item>静态 <see cref="ConnectAsync"/> 工厂创建客户端并连接到
    ///     <c>vs-mcp-{pid}</c>——保留给 Wave 1 测试和模拟 VS 作服务端方向的
    ///     冒烟运行用。</item>
    /// </list>
    /// </summary>
    public sealed class PipeRouter : IDisposable, IAsyncDisposable
    {
        private Stream _stream;
        private readonly bool _ownsStream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, Pending> _pending =
            new ConcurrentDictionary<string, Pending>(StringComparer.Ordinal);
        private Task? _readLoop;
        private readonly CancellationTokenSource _loopCts = new CancellationTokenSource();
        private volatile bool _disposed;

        /// <summary>VS 推送的 <c>solution-changed</c> 控制帧的可选接收端。从读循环
        /// 内联调用；必须非阻塞且自行吞掉异常（Gateway 的处理器只是刷新
        /// InstanceRegistry，不会抛）。Null = 忽略该帧。</summary>
        private readonly Action<PipeSolutionChanged>? _onSolutionChanged;

        /// <summary>VS 推送的 <c>heartbeat</c> 控制帧的可选接收端（Wave 4）。从
        /// 读循环内联调用；Gateway 的处理器刷新 InstanceRegistry 的 LastSeen。
        /// Null = 忽略该帧。</summary>
        private readonly Action<PipeHeartbeat>? _onHeartbeat;

        /// <summary>
        /// 控制帧载荷由 PipeFraming 以 camelCase 写出（共享的 pipe 帧策略），所以
        /// 这里的反序列化必须用同一策略，否则 <c>solutionPath</c> 之类的字段会
        /// 静默绑定失败。
        /// </summary>
        private static readonly JsonSerializerOptions ControlFrameJsonOptions =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <param name="connectedStream">一个已连接的 pipe 流。router 立即启动读
        /// 循环，并在 <paramref name="ownsStream"/> 为 true 时负责该流的释放。</param>
        /// <param name="onSolutionChanged">可选回调，在 <c>solution-changed</c>
        /// 控制帧到达（无 id）时调用。Gateway 用它实时刷新 InstanceRegistry 的
        /// solution 字段。</param>
        /// <param name="onHeartbeat">可选回调，在 <c>heartbeat</c> 控制帧到达
        /// （无 id）时调用。Gateway 用它刷新 InstanceRegistry 的 LastSeen 时间戳。</param>
        public PipeRouter(Stream connectedStream, bool ownsStream = true,
            Action<PipeSolutionChanged>? onSolutionChanged = null,
            Action<PipeHeartbeat>? onHeartbeat = null)
        {
            _stream = connectedStream ?? throw new ArgumentNullException(nameof(connectedStream));
            _ownsStream = ownsStream;
            _onSolutionChanged = onSolutionChanged;
            _onHeartbeat = onHeartbeat;
            // 读循环运行至取消；在 DisposeAsync 中被观察。
            _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
        }

        private PipeRouter(Stream connectedStream, bool ownsStream, bool startLoop)
        {
            _stream = connectedStream;
            _ownsStream = ownsStream;
            // 测试辅助路径：延迟读循环，让测试先接好期望。当前未用，保留以对称。
            if (startLoop)
                _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
        }

        /// <summary>当前 pipe 连通性（粗略——读循环负责观察断连）。</summary>
        public bool IsConnected
        {
            get
            {
                if (_disposed) return false;
                if (_stream is NamedPipeServerStream ps) return ps.IsConnected;
                if (_stream is NamedPipeClientStream pc) return pc.IsConnected;
                return _stream.CanRead;
            }
        }

        /// <summary>
        /// Wave 1 / 测试工厂：创建 <c>NamedPipeClientStream</c> 并带重试连接到
        /// <c>vs-mcp-{pipeName}</c>。返回一个已拥有连接和读循环的 router。保留给
        /// Wave 1 直通测试用（它们以这种方式构造 router）。
        ///
        /// 用 <see cref="PipeOptions.Asynchronous"/> + 异步 IO（ConnectAsync /
        /// ReadAsync / WriteAsync）：多路分解模型在同一个 handle 上运行持续读循环，
        /// 与 ForwardAsync 写入并发，需要 overlapped IO。Wave 1 的 "PipeOptions.None"
        /// 注释针对的是 Wave 1 的串行模型（一把锁内写完再读）；并发多路分解需要
        /// Asynchronous。Wave 1 的卡死是 Asynchronous + 同步 IO；这里的正确搭配是
        /// 全程 Asynchronous + 异步 IO。
        /// </summary>
        public static async Task<PipeRouter> ConnectAsync(string pipeName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("pipeName is required", nameof(pipeName));

            NamedPipeClientStream? client = null;
            while (!ct.IsCancellationRequested)
            {
                client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    // ConnectAsync() 在 net48 上没有超时重载——用一个延时赛跑来模拟；
                    // 超时则释放流（这会中止进行中的 connect）并重试。在 ConnectAsync
                    // 期间 Dispose 是安全的；被放弃的流由 GC 回收。
                    Task connectTask = client.ConnectAsync();
                    Task winner = await Task.WhenAny(connectTask, Task.Delay(2000, ct)).ConfigureAwait(false);
                    if (winner != connectTask)
                    {
                        TryDispose(client);
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }
                    await connectTask.ConfigureAwait(false); // 观察 connect 错误
                    return new PipeRouter(client, ownsStream: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(client);
                    throw;
                }
                catch (Exception)
                {
                    TryDispose(client);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            throw new OperationCanceledException(ct);
        }

        /// <summary>
        /// 读循环：持续读帧，按 (type, id) 分派给对应的 pending 请求。从构造函数
        /// 起在后台运行，直到流关闭或 router 被释放。
        /// </summary>
        private async Task ReadLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? json;
                try
                {
                    json = await PipeFraming.ReadFrameJsonAsync(_stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (IsBenignDisconnect(ex))
                {
                    // Pipe 关闭 / VS 已走——往下走到 fail 所有 pending。
                    break;
                }
                catch (Exception)
                {
                    // 畸形/截断的帧视为连接丢失；Gateway 的 accept 循环会回收本条目。
                    break;
                }

                if (json == null)
                    break; // 干净的 EOF

                string type = "";
                string id = "";
                try
                {
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("type", out var t))
                            type = t.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("id", out var i))
                            id = i.GetString() ?? "";
                    }
                }
                catch (JsonException)
                {
                    continue; // 跳过无法解析的帧
                }

                // 控制帧（无 id）是 VS 推送的通知，不是请求/响应对。在下面基于 id
                // 的多路分解之前，先分派给各自的回调。solution-changed 刷新
                // InstanceRegistry；heartbeat（Wave 4）目前被识别后忽略，让新旧
                // VS 配对不破坏流。
                if (string.IsNullOrEmpty(id))
                {
                    if (string.Equals(type, "solution-changed", StringComparison.Ordinal) && _onSolutionChanged != null)
                    {
                        try
                        {
                            var changed = JsonSerializer.Deserialize<PipeSolutionChanged>(json, ControlFrameJsonOptions);
                            if (changed != null)
                                _onSolutionChanged(changed);
                        }
                        catch
                        {
                            // 畸形的控制帧绝不能拖垮 pipe——其他在途请求依赖它。
                        }
                    }
                    else if (string.Equals(type, "heartbeat", StringComparison.Ordinal) && _onHeartbeat != null)
                    {
                        try
                        {
                            var beat = JsonSerializer.Deserialize<PipeHeartbeat>(json, ControlFrameJsonOptions);
                            if (beat != null)
                                _onHeartbeat(beat);
                        }
                        catch
                        {
                            // 畸形的 heartbeat 绝不能拖垮 pipe。
                        }
                    }
                    // 未知控制帧落到这里被静默丢弃，让新旧版本配对绝不破坏流。
                    continue;
                }

                if (!_pending.TryGetValue(id, out var pending))
                    continue; // 未知 id（过期/重复）——忽略

                if (string.Equals(type, "head", StringComparison.Ordinal))
                {
                    try
                    {
                        using (var doc = JsonDocument.Parse(json))
                        {
                            if (doc.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out int st))
                                pending.Status = st;
                            if (doc.RootElement.TryGetProperty("headers", out var h))
                            {
                                foreach (var prop in h.EnumerateObject())
                                    pending.Headers[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null
                                        ? ""
                                        : prop.Value.GetString() ?? "";
                            }
                        }
                    }
                    catch { /* 尽力解析 */ }
                }
                else if (string.Equals(type, "data", StringComparison.Ordinal))
                {
                    try
                    {
                        string? bodyBase64 = "";
                        using (var doc = JsonDocument.Parse(json))
                        {
                            if (doc.RootElement.TryGetProperty("body", out var b))
                                bodyBase64 = b.GetString();
                        }
                        if (!string.IsNullOrEmpty(bodyBase64))
                        {
                            byte[] bytes = Convert.FromBase64String(bodyBase64);
                            await pending.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                            await pending.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        pending.CompletionTcs.TrySetCanceled();
                        _pending.TryRemove(id, out _);
                        throw;
                    }
                    catch (Exception ex) when (IsBenignDisconnect(ex))
                    {
                        pending.CompletionTcs.TrySetException(ex);
                        _pending.TryRemove(id, out _);
                        return;
                    }
                    catch
                    {
                        // 对已取消/已关闭的输出流的写入绝不能害死其他 pending
                        // 请求的读循环。
                    }
                }
                else if (string.Equals(type, "end", StringComparison.Ordinal))
                {
                    pending.CompletionTcs.TrySetResult(true);
                    _pending.TryRemove(id, out _);
                }
                // 其他带 id 的类型（register 回显）不属于响应流，这里忽略；Program.cs
                // 在把它包装成 router 之前已从 accept 流上读走 register 帧，所以它
                // 永远不会到达 router 的读循环。
            }

            // 连接丢失：让所有仍在 pending 的请求失败，使其 ForwardAsync 调用方
            // 解除阻塞，而不是永远挂起。
            foreach (var kv in _pending)
            {
                kv.Value.CompletionTcs.TrySetException(
                    new IOException("VS pipe disconnected before the response completed."));
                _pending.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>
        /// 转发单个 JSON-RPC 请求：写一个 PipeRequest 帧（在 send 锁下，写完立即
        /// 释放），并在 pending 条目的 completion TCS 上等待，直到读循环看到匹配的
        /// end 帧（或 pipe 断开）。返回 head 状态 + headers。
        /// </summary>
        public async Task<ForwardResult> ForwardAsync(
            string jsonRpcBody,
            IDictionary<string, string>? requestHeaders,
            Stream outputStream,
            CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PipeRouter));

            string id = Guid.NewGuid().ToString("N");
            var pending = new Pending
            {
                Id = id,
                OutputStream = outputStream,
                Status = 200,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                CompletionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _pending[id] = pending;

            // 取消时注销，让被丢弃的请求不泄漏，且迟到的 end 帧找不到匹配的 pending。
            using var reg = ct.Register(() =>
            {
                pending.CompletionTcs.TrySetCanceled();
                _pending.TryRemove(id, out _);
            });

            var headers = new Dictionary<string, string>();
            if (requestHeaders != null)
            {
                foreach (var kv in requestHeaders)
                    headers[kv.Key] = kv.Value;
            }

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteFrameAsync(_stream, new PipeRequest
                {
                    Id = id,
                    Body = jsonRpcBody,
                    Headers = headers,
                }, ct).ConfigureAwait(false);
                // 锁立即释放——并发请求现在可以写各自的帧；读循环按 id 多路分解。
            }
            finally
            {
                _sendLock.Release();
            }

            try
            {
                await pending.CompletionTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Pending 自己的 TCS 被取消而我们的 ct 没触发——视为传输故障，
                // 上报给 HTTP 层。
                throw new IOException("Pipe request was canceled.");
            }

            return new ForwardResult(pending.Status, pending.Headers);
        }

        private static bool IsBenignDisconnect(Exception ex)
        {
            return ex is IOException || ex is ObjectDisposedException;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _loopCts.Cancel(); } catch { /* 尽力而为 */ }
            if (_ownsStream) TryDispose(_stream);
            FailAllPending();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { _loopCts.Cancel(); } catch { /* 尽力而为 */ }
            if (_readLoop != null)
            {
#pragma warning disable VSTHRD003
                try { await _readLoop.ConfigureAwait(false); }
                catch { /* 收尾绝不能抛 */ }
#pragma warning restore VSTHRD003
            }
            if (_ownsStream) TryDispose(_stream);
            FailAllPending();
        }

        private void FailAllPending()
        {
            foreach (var kv in _pending)
            {
                kv.Value.CompletionTcs.TrySetException(
                    new ObjectDisposedException(nameof(PipeRouter), "Router disposed while a request was in flight."));
                _pending.TryRemove(kv.Key, out _);
            }
        }

        private static void TryDispose(IDisposable? disposable)
        {
            try { disposable?.Dispose(); } catch { /* 尽力而为 */ }
        }

        private sealed class Pending
        {
            public string Id { get; set; } = "";
            public Stream OutputStream { get; set; } = Stream.Null;
            public int Status { get; set; }
            public Dictionary<string, string> Headers { get; set; } = new();
            public TaskCompletionSource<bool> CompletionTcs { get; set; } = new();
        }
    }
}
