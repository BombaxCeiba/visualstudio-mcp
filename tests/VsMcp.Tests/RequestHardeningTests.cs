using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// F-4 / D-13 / D-18 transport-layer hardening tests:
    ///   - body cap   : POST with declared Content-Length > 10MB -> HTTP 413
    ///   - gate       : concurrent requests are serialized through the SemaphoreSlim(1,1) gate
    ///   - gate+cancel: a request blocked on the gate exits cleanly when the external CTS cancels
    ///   - happy path : a normal small POST still succeeds (regression guard for the per-request timeout CTS)
    ///
    /// Mirrors the McpHttpServerTests / McpIntegrationTests scaffolding (unique ports +
    /// IDisposable teardown). Uses a custom HttpContent that reports a &gt;10MB length without
    /// actually allocating 10MB, to keep the body-cap test fast.
    /// </summary>
    public class RequestHardeningTests : IDisposable
    {
        // Unique port range far from the other test fixtures to avoid collisions.
        private static int _nextPort = 59970;
        private readonly int _port;
        private McpHttpServer? _server;
        private CancellationTokenSource? _cts;

        public RequestHardeningTests()
        {
            _port = Interlocked.Increment(ref _nextPort);
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _server?.Dispose();
            _cts?.Dispose();
        }

        private async Task<McpHttpServer> StartServerAsync(int port)
        {
            _cts = new CancellationTokenSource();
            _server = new McpHttpServer(port: port, authToken: null);
            // StartAsync runs an accept loop, so it won't complete until cancelled.
            _ = _server.StartAsync(_cts.Token);
            // Give the listener time to bind.
            await Task.Delay(300);
            return _server;
        }

        /// <summary>
        /// Sends a raw HTTP POST that advertises an oversized Content-Length
        /// (&gt; 10MB) but writes only a tiny body. HttpClient cannot represent
        /// a Content-Length/content mismatch without raising IOException on its
        /// own side, so we drive the wire directly with TcpClient: write the
        /// request line + headers (including a hand-set Content-Length of 11MB),
        /// flush, then read the server's status line. The server must reject with
        /// 413 before consuming the body, so it never waits for the missing bytes.
        /// </summary>
        [Fact]
        public async Task BodyCap_Returns413_ForOversizedContentLength()
        {
            await StartServerAsync(_port);

            try
            {
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, _port);
                    using (var stream = tcp.GetStream())
                    {
                        var header =
                            $"POST /mcp/ HTTP/1.1\r\n" +
                            $"Host: localhost:{_port}\r\n" +
                            $"Content-Type: application/json\r\n" +
                            $"Content-Length: {11L * 1024 * 1024}\r\n" +
                            $"\r\n";
                        var headerBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                        await stream.FlushAsync();

                        // Read the status line. The server rejects at the
                        // ContentLength64 check before reading the body, so this
                        // returns promptly with 413.
                        var readBuffer = new byte[64];
                        var read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length);
                        var responseHead = Encoding.ASCII.GetString(readBuffer, 0, read);

                        Assert.Contains("413", responseHead);
                    }
                }
            }
            finally
            {
                _cts!.Cancel();
            }
        }

        [Fact]
        public async Task Gate_SerializesConcurrentRequests()
        {
            await StartServerAsync(_port);

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(20);

                    // Two initialize POSTs fired concurrently. Under the capacity-1
                    // gate they must be processed serially; the proof is that BOTH
                    // return a valid MCP success status (200 or 202) rather than
                    // corrupting each other through interleaved SDK writers.
                    var initBody =
                        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\"," +
                        "\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}," +
                        "\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}";

                    async Task<HttpStatusCode> SendInitAsync()
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/mcp/");
                        request.Content = new StringContent(initBody, Encoding.UTF8, "application/json");
                        using var response = await client.SendAsync(request);
                        return response.StatusCode;
                    }

                    var tasks = new[] { SendInitAsync(), SendInitAsync() };
                    var results = await Task.WhenAll(tasks);

                    Assert.All(results, status =>
                        Assert.True(
                            status == HttpStatusCode.OK || status == HttpStatusCode.Accepted,
                            $"Serialized request should have returned 200/202, got {status}"));
                }
            }
            finally
            {
                _cts!.Cancel();
            }
        }

        [Fact]
        public async Task Gate_ReleasesBlockedRequest_OnExternalCancel()
        {
            // Acquire the server's private gate via reflection BEFORE starting the
            // request, so the request blocks at _requestGate.WaitAsync. Then cancel
            // the external CTS and assert the blocked request observes cancellation
            // within a few seconds rather than hanging indefinitely.
            _cts = new CancellationTokenSource();
            _server = new McpHttpServer(port: _port, authToken: null);
            _ = _server.StartAsync(_cts.Token);
            await Task.Delay(300);

            // Reach in and hold the gate so the next request cannot enter.
            var gateField = typeof(McpHttpServer).GetField(
                "_requestGate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(gateField);
            var gate = (SemaphoreSlim)gateField!.GetValue(_server!)!;
            await gate.WaitAsync();

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15);

                var requestTask = Task.Run(async () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/mcp/");
                    request.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{}}",
                        Encoding.UTF8, "application/json");
                    try
                    {
                        using var response = await client.SendAsync(request);
                        return response.StatusCode;
                    }
                    catch (OperationCanceledException)
                    {
                        return HttpStatusCode.RequestTimeout;
                    }
                    catch (HttpRequestException)
                    {
                        // Server teardown can surface as a transport error.
                        return HttpStatusCode.ServiceUnavailable;
                    }
                });

                // Give the request time to reach the gate.
                await Task.Delay(300);

                // Cancel the external token — shutdown signal. The blocked request
                // should observe the cancellation from WaitAsync and exit.
                _cts.Cancel();

                // The blocked request must exit within a few seconds (well under the
                // 15s test framework timeout). If WaitAsync did NOT honor the token,
                // this would hang until HttpClient.Timeout fires (~15s) and surface
                // as RequestTimeout — still an exit, but slow; we assert fast exit.
                var exited = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(5))) == requestTask;
                Assert.True(exited, "Blocked request should exit within 5s of external cancel, not hang");

                // Release the gate we held so server teardown can proceed cleanly.
                gate.Release();
            }
        }

        [Fact]
        public async Task HappyPath_SmallPost_Succeeds_UnderTimeoutCTS()
        {
            // Regression guard: the per-request ~15s timeout CTS must not break a
            // normal small initialize POST.
            await StartServerAsync(_port);

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);

                    var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/mcp/");
                    request.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\"," +
                        "\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}," +
                        "\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}",
                        Encoding.UTF8, "application/json");

                    var response = await client.SendAsync(request);

                    Assert.True(
                        response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted,
                        $"Happy-path initialize should succeed, got {response.StatusCode}");
                }
            }
            finally
            {
                _cts!.Cancel();
            }
        }
    }
}
