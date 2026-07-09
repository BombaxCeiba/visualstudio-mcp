using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// 维护到单个 VS 实例的 pipe 通道，并在其上多路复用多个并发 tool-call。
    /// 每个 tool-call 获得一个新的 id；单个后台读循环按 id 把到达的 tool-result
    /// 帧分派给对应的 pending 请求。这让 Gateway 能在一条 pipe 上转发多个在途
    /// 工具调用（每个 MCP 客户端会话一条），没有队头阻塞。
    ///
    /// 读循环也识别 VS 推送的不带 id 的 CONTROL 帧（solution-changed /
    /// heartbeat / debugger-state-changed）。每种控制帧有自己的可选回调；
    /// 未识别的控制帧被忽略，所以新旧版本配对绝不会破坏流。
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

        /// <summary>VS 推送的 <c>solution-changed</c> 控制帧的可选接收端。</summary>
        private readonly Action<PipeSolutionChanged>? _onSolutionChanged;

        /// <summary>VS 推送的 <c>heartbeat</c> 控制帧的可选接收端。</summary>
        private readonly Action<PipeHeartbeat>? _onHeartbeat;

        /// <summary>VS 推送的 <c>debugger-state-changed</c> 控制帧的可选接收端。</summary>
        private readonly Action<PipeDebuggerStateChanged>? _onDebuggerStateChanged;

        /// <summary>控制帧反序列化用 camelCase（与 PipeFraming.Options 一致）。</summary>
        private static readonly JsonSerializerOptions ControlFrameJsonOptions =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public PipeRouter(Stream connectedStream, bool ownsStream = true,
            Action<PipeSolutionChanged>? onSolutionChanged = null,
            Action<PipeHeartbeat>? onHeartbeat = null,
            Action<PipeDebuggerStateChanged>? onDebuggerStateChanged = null)
        {
            _stream = connectedStream ?? throw new ArgumentNullException(nameof(connectedStream));
            _ownsStream = ownsStream;
            _onSolutionChanged = onSolutionChanged;
            _onHeartbeat = onHeartbeat;
            _onDebuggerStateChanged = onDebuggerStateChanged;
            _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
        }

        private PipeRouter(Stream connectedStream, bool ownsStream, bool startLoop)
        {
            _stream = connectedStream;
            _ownsStream = ownsStream;
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
                    break;
                }
                catch (Exception)
                {
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

                // 控制帧（无 id）是 VS 推送的通知，不是请求/响应对。
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
                        catch { /* 畸形的控制帧绝不能拖垮 pipe */ }
                    }
                    else if (string.Equals(type, "heartbeat", StringComparison.Ordinal) && _onHeartbeat != null)
                    {
                        try
                        {
                            var beat = JsonSerializer.Deserialize<PipeHeartbeat>(json, ControlFrameJsonOptions);
                            if (beat != null)
                                _onHeartbeat(beat);
                        }
                        catch { /* 畸形的 heartbeat 绝不能拖垮 pipe */ }
                    }
                    else if (string.Equals(type, "debugger-state-changed", StringComparison.Ordinal) && _onDebuggerStateChanged != null)
                    {
                        try
                        {
                            var changed = JsonSerializer.Deserialize<PipeDebuggerStateChanged>(json, ControlFrameJsonOptions);
                            if (changed != null)
                                _onDebuggerStateChanged(changed);
                        }
                        catch { /* 畸形的 debugger-state-changed 绝不能拖垮 pipe */ }
                    }
                    GatewayLogger.Log($"ignoring control frame type='{type}' — no handler, dropping");
                    continue;
                }

                if (!_pending.TryGetValue(id, out var pending))
                    continue; // 未知 id（过期/重复）——忽略

                if (string.Equals(type, "tool-result", StringComparison.Ordinal))
                {
                    try
                    {
                        using (var doc = JsonDocument.Parse(json))
                        {
                            if (doc.RootElement.TryGetProperty("content", out var c))
                                pending.Content = c.GetString() ?? "";
                            if (doc.RootElement.TryGetProperty("isError", out var e))
                                pending.IsError = e.GetBoolean();
                        }
                    }
                    catch { /* 尽力解析 */ }
                    pending.CompletionTcs.TrySetResult(true);
                    _pending.TryRemove(id, out _);
                }
                // 其他带 id 的类型（旧 head/data/end 帧）被忽略——新旧协议配对不破坏流。
            }

            // 连接丢失：让所有仍在 pending 的请求失败。
            foreach (var kv in _pending)
            {
                kv.Value.CompletionTcs.TrySetException(
                    new IOException("VS pipe disconnected before the tool-call completed."));
                _pending.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>
        /// 转发一个 tool 调用：写一个 PipeToolCall 帧（在 send 锁下，写完立即
        /// 释放），并在 pending 条目的 completion TCS 上等待，直到读循环看到匹配的
        /// tool-result 帧（或 pipe 断开）。返回工具结果文本 + 错误标志。
        /// </summary>
        public async Task<PipeToolResultData> CallToolAsync(
            string toolName, string argumentsJson, CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PipeRouter));

            string id = Guid.NewGuid().ToString("N");
            var pending = new Pending
            {
                Id = id,
                CompletionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _pending[id] = pending;

            using var reg = ct.Register(() =>
            {
                pending.CompletionTcs.TrySetCanceled();
                _pending.TryRemove(id, out _);
            });

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteFrameAsync(_stream, new PipeToolCall
                {
                    Id = id,
                    Tool = toolName,
                    Arguments = argumentsJson ?? "",
                }, ct).ConfigureAwait(false);
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
                throw new IOException("Pipe tool-call was canceled.");
            }

            return new PipeToolResultData(pending.Content, pending.IsError);
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
                    new ObjectDisposedException(nameof(PipeRouter), "Router disposed while a tool-call was in flight."));
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
            public string Content { get; set; } = "";
            public bool IsError { get; set; }
            public TaskCompletionSource<bool> CompletionTcs { get; set; } = new();
        }
    }
}
