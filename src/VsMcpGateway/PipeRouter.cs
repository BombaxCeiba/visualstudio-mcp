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
    /// Result of forwarding one request: the VS head's HTTP status plus the
    /// head's response headers (so the Gateway can read the VS-assigned
    /// Mcp-Session-Id during initialize). Wave 1 callers ignore the headers
    /// and only read <see cref="Status"/>.
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

        /// <summary>Implicit int conversion keeps Wave 1's "int status = await ForwardAsync(...)" ergonomic.</summary>
        public static implicit operator int(ForwardResult r) => r.Status;
    }

    /// <summary>
    /// Maintains the pipe channel to one VS instance and multiplexes many
    /// concurrent MCP requests over it. Each request gets a fresh id; a single
    /// background read loop dispatches incoming head/data/end frames to the
    /// matching pending request by id. This lets the Gateway forward multiple
    /// in-flight SSE streams (one per MCP client session) over one pipe without
    /// head-of-line blocking.
    ///
    /// Two construction modes:
    /// <list type="bullet">
    /// <item>The Gateway's register-accept loop passes an ALREADY CONNECTED
    ///     <see cref="NamedPipeServerStream"/> (VS dialed in to
    ///     "vs-mcp-gateway"); the router just owns the read loop.</item>
    /// <item>The static <see cref="ConnectAsync"/> factory creates a client and
    ///     connects to <c>vs-mcp-{pid}</c> — retained for Wave 1 tests and
    ///     smoke runs that simulate the VS-as-server direction.</item>
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

        /// <param name="connectedStream">An already-connected pipe stream. The
        /// router starts the read loop immediately and owns the stream's
        /// disposal (when <paramref name="ownsStream"/> is true).</param>
        public PipeRouter(Stream connectedStream, bool ownsStream = true)
        {
            _stream = connectedStream ?? throw new ArgumentNullException(nameof(connectedStream));
            _ownsStream = ownsStream;
            // Read loop runs until cancellation; it is observed in DisposeAsync.
            _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
        }

        private PipeRouter(Stream connectedStream, bool ownsStream, bool startLoop)
        {
            _stream = connectedStream;
            _ownsStream = ownsStream;
            // Test helper path: defer the read loop so the test can wire up
            // expectations first. Currently unused but kept for symmetry.
            if (startLoop)
                _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
        }

        /// <summary>Current pipe connectivity (rough — the read loop observes disconnects).</summary>
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
        /// Wave 1 / test factory: create a <c>NamedPipeClientStream</c> and
        /// connect to <c>vs-mcp-{pipeName}</c> with retry. Returns a router
        /// that already owns the connection and read loop. Kept for the
        /// Wave 1 passthrough tests, which construct the router this way.
        ///
        /// Uses <see cref="PipeOptions.Asynchronous"/> + async IO (ConnectAsync /
        /// ReadAsync / WriteAsync): the demux model runs a continuous read loop
        /// concurrent with ForwardAsync writes on the SAME handle, which requires
        /// overlapped IO. The Wave 1 "PipeOptions.None" note applied to Wave 1's
        /// SERIAL model (write-then-read inside one lock); concurrent demux needs
        /// Asynchronous. The Wave 1 hang was Asynchronous + SYNCHRONOUS IO; the
        /// correct pairing here is Asynchronous + ASYNC IO throughout.
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
                    // ConnectAsync() has no timeout overload on net48 — emulate
                    // by racing against a delay; on timeout, dispose the stream
                    // (which aborts the in-flight connect) and retry. Disposing
                    // during ConnectAsync is safe; the abandoned stream is GC'd.
                    Task connectTask = client.ConnectAsync();
                    Task winner = await Task.WhenAny(connectTask, Task.Delay(2000, ct)).ConfigureAwait(false);
                    if (winner != connectTask)
                    {
                        TryDispose(client);
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }
                    await connectTask.ConfigureAwait(false); // observe connect errors
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
        /// Read loop: continuously reads frames, dispatching each by (type, id)
        /// to the matching pending request. Runs in the background from the
        /// constructor until the stream closes or the router is disposed.
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
                    // Pipe closed / VS gone — fall through to fail all pending.
                    break;
                }
                catch (Exception)
                {
                    // A malformed/truncated frame is treated as connection loss;
                    // the Gateway's accept loop will reap this entry.
                    break;
                }

                if (json == null)
                    break; // clean EOF

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
                    continue; // skip unparseable frame
                }

                if (string.IsNullOrEmpty(id))
                    continue;

                if (!_pending.TryGetValue(id, out var pending))
                    continue; // unknown id (stale / duplicate) — ignore

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
                    catch { /* best-effort parse */ }
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
                        // A write to a cancelled/closed output stream must not
                        // kill the read loop for OTHER pending requests.
                    }
                }
                else if (string.Equals(type, "end", StringComparison.Ordinal))
                {
                    pending.CompletionTcs.TrySetResult(true);
                    _pending.TryRemove(id, out _);
                }
                // Other types (register/heartbeat/solution-changed) don't
                // belong to the response stream and are ignored here; Program.cs
                // reads the register frame off the accept stream before wrapping
                // it in a router, so they never arrive on a router's read loop.
            }

            // Connection lost: fail every still-pending request so its
            // ForwardAsync caller unblocks instead of hanging forever.
            foreach (var kv in _pending)
            {
                kv.Value.CompletionTcs.TrySetException(
                    new IOException("VS pipe disconnected before the response completed."));
                _pending.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>
        /// Forward one JSON-RPC request: write a PipeRequest frame (under the
        /// send lock, released immediately after the write) and wait on the
        /// pending entry's completion TCS until the read loop sees the matching
        /// end frame (or the pipe drops). Returns the head status + headers.
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

            // Unregister on cancellation so a dropped request doesn't leak and
            // a late end frame finds no matching pending.
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
                // Lock released immediately — concurrent requests may now write
                // their own frames; the read loop demuxes by id.
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
                // Pending's own TCS was canceled without our ct firing — treat
                // as a transport fault and surface to the HTTP layer.
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
            try { _loopCts.Cancel(); } catch { /* best-effort */ }
            if (_ownsStream) TryDispose(_stream);
            FailAllPending();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { _loopCts.Cancel(); } catch { /* best-effort */ }
            if (_readLoop != null)
            {
#pragma warning disable VSTHRD003
                try { await _readLoop.ConfigureAwait(false); }
                catch { /* teardown must not throw */ }
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
            try { disposable?.Dispose(); } catch { /* best-effort */ }
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
