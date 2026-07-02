using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VsMcp.Tests
{
    public class McpIntegrationTests : IDisposable
    {
        // Use unique ports far from McpHttpServerTests to avoid conflicts
        private const int PortNoAuth = 59770;
        private const int PortWithAuth = 59771;
        private McpHttpServer? _serverNoAuth;
        private McpHttpServer? _serverWithAuth;
        private CancellationTokenSource? _ctsNoAuth;
        private CancellationTokenSource? _ctsWithAuth;

        public void Dispose()
        {
            _ctsNoAuth?.Cancel();
            _ctsWithAuth?.Cancel();
            _serverNoAuth?.Dispose();
            _serverWithAuth?.Dispose();
            _ctsNoAuth?.Dispose();
            _ctsWithAuth?.Dispose();
        }

        private async Task<McpHttpServer> StartServerAsync(int port, string? authToken, CancellationToken ct)
        {
            var server = new McpHttpServer(port: port, authToken: authToken);
            _ = server.StartAsync(ct);
            await Task.Delay(300);
            return server;
        }

        [Fact]
        public async Task ToolsList_RespondsViaMcpProtocol()
        {
            _ctsNoAuth = new CancellationTokenSource();
            _serverNoAuth = await StartServerAsync(PortNoAuth, null, _ctsNoAuth.Token);

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);

                    // Step 1: Initialize
                    var initRequest = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{PortNoAuth}/mcp/");
                    initRequest.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}",
                        Encoding.UTF8, "application/json");

                    var initResponse = await client.SendAsync(initRequest);
                    var initBody = await initResponse.Content.ReadAsStringAsync();

                    // Initialize should succeed (200 or 202)
                    Assert.True(
                        initResponse.StatusCode == HttpStatusCode.OK || initResponse.StatusCode == HttpStatusCode.Accepted,
                        $"Initialize failed: {initResponse.StatusCode} - {initBody}");

                    // Step 2: Send initialized notification
                    var notifRequest = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{PortNoAuth}/mcp/");
                    notifRequest.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                        Encoding.UTF8, "application/json");

                    await client.SendAsync(notifRequest);

                    // Step 3: List tools — verifies the full MCP round-trip
                    // (initialize -> initialized -> tools/list) without depending
                    // on any specific tool's execution, keeping the transport
                    // contract test independent of COM/DTE availability.
                    var listRequest = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{PortNoAuth}/mcp/");
                    listRequest.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                        Encoding.UTF8, "application/json");

                    var listResponse = await client.SendAsync(listRequest);
                    var listBody = await listResponse.Content.ReadAsStringAsync();

                    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
                    Assert.Contains("\"tools\"", listBody);
                }
            }
            finally
            {
                _ctsNoAuth.Cancel();
            }
        }

        [Fact]
        public async Task ToolsList_RespondsWithAuth()
        {
            _ctsWithAuth = new CancellationTokenSource();
            _serverWithAuth = await StartServerAsync(PortWithAuth, "test-token", _ctsWithAuth.Token);

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);

                    // Initialize with auth
                    var initRequest = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{PortWithAuth}/mcp/");
                    initRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
                    initRequest.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}",
                        Encoding.UTF8, "application/json");

                    var initResponse = await client.SendAsync(initRequest);
                    Assert.NotEqual(HttpStatusCode.Unauthorized, initResponse.StatusCode);

                    // Call tools/list with auth — verifies authenticated transport
                    var listRequest = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{PortWithAuth}/mcp/");
                    listRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
                    listRequest.Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                        Encoding.UTF8, "application/json");

                    var listResponse = await client.SendAsync(listRequest);
                    var listBody = await listResponse.Content.ReadAsStringAsync();

                    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
                    Assert.Contains("\"tools\"", listBody);
                }
            }
            finally
            {
                _ctsWithAuth.Cancel();
            }
        }
    }
}
