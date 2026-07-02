using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VsMcp.Tests
{
    public class McpHttpServerTests : IDisposable
    {
        // Use unique ports in the dynamic range to avoid conflicts
        private static int _nextPort = 59870;
        private readonly int _port;
        private McpHttpServer? _server;
        private CancellationTokenSource? _cts;

        public McpHttpServerTests()
        {
            _port = Interlocked.Increment(ref _nextPort);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _server?.Dispose();
            _cts?.Dispose();
        }

        private async Task<McpHttpServer> StartServerAsync(int port, string? authToken = null)
        {
            _cts = new CancellationTokenSource();
            _server = new McpHttpServer(port: port, authToken: authToken);

            // StartAsync runs an accept loop, so it won't complete until cancelled.
            // Fire and forget the server task.
            _ = _server.StartAsync(_cts.Token);

            // Give the server time to start listening
            await Task.Delay(300);

            return _server;
        }

        [Fact]
        public async Task ServerStarts_OnConfiguredPort()
        {
            await StartServerAsync(_port);

            // Verify server is listening by attempting a TCP connection
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, _port);
                Assert.True(client.Connected, "Should be able to connect to the server port");
            }
        }

        [Fact]
        public async Task ServerStops_OnCancellation()
        {
            await StartServerAsync(_port + 100);

            // Cancel should allow the server task to complete
            _cts!.Cancel();
            await Task.Delay(200);

            // Dispose should not throw
            _server!.Dispose();
        }

        [Fact]
        public async Task ServerRejects_Unauthorized_WhenAuthEnabled()
        {
            await StartServerAsync(_port + 200, "test-secret");

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);

                    var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port + 200}/mcp/");
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    var response = await client.SendAsync(request);

                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
            finally
            {
                _cts!.Cancel();
            }
        }

        [Fact]
        public async Task ServerAccepts_Authorized_WhenAuthEnabled()
        {
            await StartServerAsync(_port + 300, "test-secret");

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);

                    var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port + 300}/mcp/");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-secret");
                    request.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}",
                        Encoding.UTF8, "application/json");

                    var response = await client.SendAsync(request);

                    // Should not be 401 -- it may be 200, 202, or another valid MCP response
                    Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
            finally
            {
                _cts!.Cancel();
            }
        }
    }
}
