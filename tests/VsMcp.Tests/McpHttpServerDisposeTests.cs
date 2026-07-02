using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// Deadlock / disposal regression guards for the F-3 async-teardown fix.
    /// Covers: non-blocking sync Dispose, idempotent dispose, DisposeAsync
    /// observes cancellation cleanly.
    /// </summary>
    public class McpHttpServerDisposeTests : IDisposable
    {
        // Unique ports in the dynamic range to avoid collisions with other tests.
        private static int _nextPort = 61000;
        private readonly int _port;
        private McpHttpServer? _server;
        private CancellationTokenSource? _cts;

        public McpHttpServerDisposeTests()
        {
            _port = Interlocked.Increment(ref _nextPort);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _server?.Dispose(); } catch { /* best-effort */ }
            _cts?.Dispose();
        }

        private async Task<McpHttpServer> StartServerAsync(int port)
        {
            _cts = new CancellationTokenSource();
            _server = new McpHttpServer(port: port);

            // StartAsync runs an accept loop, so it won't complete until cancelled.
            _ = _server.StartAsync(_cts.Token);

            // Give the listener time to bind.
            await Task.Delay(300);

            return _server;
        }

        /// <summary>
        /// Test 1 (deadlock guard): the synchronous Dispose() entry point must
        /// return within 2 seconds. Regression guard for F-3 — if the teardown
        /// ever blocks the calling thread on async disposal (e.g. via
        /// .GetAwaiter().GetResult() inside Dispose), VS exit would hang.
        /// </summary>
        [Fact]
        public void Dispose_ReturnsWithinTwoSeconds_NoDeadlock()
        {
            // Start then synchronously Dispose. Do NOT pre-cancel — the server
            // owns its own CTS and Dispose must cancel + observe without blocking.
            var server = StartServerAsync(_port).GetAwaiter().GetResult();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            server.Dispose();
            sw.Stop();

            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(2),
                $"Synchronous Dispose took {sw.Elapsed.TotalMilliseconds:F0}ms — exceeds the 2s deadlock guard. F-3 regression.");
        }

        /// <summary>
        /// Test 2 (idempotent dispose): calling Dispose() twice in succession
        /// must neither throw nor attempt to re-Stop a disposed listener.
        /// </summary>
        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var server = StartServerAsync(_port).GetAwaiter().GetResult();

            server.Dispose();

            // Second call must be a clean no-op.
            var ex = Record.Exception(() => server.Dispose());
            Assert.Null(ex);
        }

        /// <summary>
        /// Test 3 (DisposeAsync observes cancellation): DisposeAsync must
        /// complete without throwing; a subsequent synchronous Dispose on the
        /// same instance must also be a no-op (does not throw, does not
        /// re-attempt listener operations).
        /// </summary>
        [Fact]
        public async Task DisposeAsync_CompletesCleanly_AndSubsequentDisposeIsNoOp()
        {
            var server = StartServerAsync(_port).GetAwaiter().GetResult();

            await server.DisposeAsync().AsTask();

            // Subsequent sync Dispose must not throw or re-enter teardown.
            var ex = Record.Exception(() => server.Dispose());
            Assert.Null(ex);
        }
    }
}
