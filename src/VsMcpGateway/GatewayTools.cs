using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// The two Gateway-level tools, their descriptions, their handlers, and the
    /// SSE injector that stitches them into a VS tools/list response. All
    /// Gateway-aware MCP logic lives here; Program.cs is just the HTTP/pipe
    /// plumbing that calls into this.
    /// </summary>
    public static class GatewayTools
    {
        public const string ListInstancesName = "list_vs_instances";
        public const string SelectInstanceName = "select_vs_instance";

        /// <summary>JSON-serialized tool descriptions, camelCase, matching the
        /// shape VS returns in tools/list results. Parsed once into JsonElement
        /// form so they can be spliced into a result.tools array directly.</summary>
        private static readonly Lazy<JsonElement[]> ToolElements = new Lazy<JsonElement[]>(() =>
        {
            string listJson = "{\"name\":\"list_vs_instances\",\"description\":\"Lists all running Visual Studio instances connected to the gateway. Returns PID, solution path, solution directory, and debugger state for each instance.\",\"readOnly\":true}";
            string selectJson = "{\"name\":\"select_vs_instance\",\"description\":\"Binds this MCP session to a specific VS instance. All subsequent tool calls route to it. Use list_vs_instances first to find available PIDs.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"pid\":{\"type\":\"integer\",\"description\":\"VS process ID to bind to\"}},\"required\":[\"pid\"]}}";
            using var d1 = JsonDocument.Parse(listJson);
            using var d2 = JsonDocument.Parse(selectJson);
            // Clone because the documents get disposed; GetRawText re-parse is
            // the simplest way to own the element lifetime.
            return new[]
            {
                JsonSerializer.Deserialize<JsonElement>(d1.RootElement.GetRawText()),
                JsonSerializer.Deserialize<JsonElement>(d2.RootElement.GetRawText()),
            };
        });

        /// <summary>True if <paramref name="toolName"/> is one of the Gateway-handled tools.</summary>
        public static bool IsGatewayTool(string? toolName) =>
            string.Equals(toolName, ListInstancesName, StringComparison.Ordinal) ||
            string.Equals(toolName, SelectInstanceName, StringComparison.Ordinal);

        /// <summary>
        /// Build the list_vs_instances payload from a snapshot of the registry.
        /// debuggerState comes from the register frame's snapshot (Wave 2 has no
        /// live state; Wave 3+ may refresh it). Returns the JSON text for the
        /// tool's text content.
        /// </summary>
        public static string BuildListInstancesContent(IReadOnlyCollection<InstanceEntry> instances)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("instances");
                writer.WriteStartArray();
                foreach (var inst in instances)
                {
                    var info = inst.Info;
                    writer.WriteStartObject();
                    writer.WriteNumber("pid", info.Pid);
                    writer.WriteString("solution", info.SolutionPath);
                    writer.WriteString("solutionDir", info.SolutionDir);
                    writer.WriteNull("debuggerState"); // Wave 2: register has no live state
                    writer.WriteString("vsVersion", string.IsNullOrEmpty(info.VsVersion) ? null : info.VsVersion);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// Build the JSON-RPC response for list_vs_instances (a text content
        /// block carrying the instances JSON).
        /// </summary>
        public static string BuildListInstancesResponse(object jsonRpcId, IReadOnlyCollection<InstanceEntry> instances)
        {
            string content = BuildListInstancesContent(instances);
            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                result = new
                {
                    content = new[] { new { type = "text", text = content } },
                },
            };
            return JsonSerializer.Serialize(resp);
        }

        /// <summary>
        /// Build the JSON-RPC response for select_vs_instance on success.
        /// </summary>
        public static string BuildSelectResponse(object jsonRpcId, int pid)
        {
            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                result = new
                {
                    content = new[] { new { type = "text", text = $"Session rebound to VS instance PID {pid}." } },
                },
            };
            return JsonSerializer.Serialize(resp);
        }

        /// <summary>
        /// Build a JSON-RPC error response (used for select without initialize,
        /// unknown pid, etc.).
        /// </summary>
        public static string BuildErrorResponse(object jsonRpcId, int code, string message)
        {
            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                error = new { code, message },
            };
            return JsonSerializer.Serialize(resp);
        }

        /// <summary>
        /// Build a JSON-RPC error response wrapped as an MCP tool-call IsError
        /// result (for gateway-tool failures that should surface to the agent
        /// as a tool error, not a protocol error).
        /// </summary>
        public static string BuildToolErrorResponse(object jsonRpcId, string message)
        {
            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                result = new
                {
                    isError = true,
                    content = new[] { new { type = "text", text = message } },
                },
            };
            return JsonSerializer.Serialize(resp);
        }

        /// <summary>
        /// Inject the two gateway tool descriptions into a buffered tools/list
        /// SSE response. The VS response is text/event-stream:
        /// <c>event: message\ndata: {"jsonrpc":...,"result":{"tools":[...]}}\n\n</c>
        /// We extract the data JSON, splice our tools onto the result.tools
        /// array, and re-frame as SSE. Returns the rewritten bytes.
        ///
        /// If the buffer is not a parseable tools/list response (e.g. an error
        /// envelope), it is returned unchanged so errors still reach the client.
        /// </summary>
        public static byte[] InjectIntoToolsListSse(byte[] sseBytes)
        {
            string text = Encoding.UTF8.GetString(sseBytes);

            // Find the JSON-RPC payload. SSE "data:" lines carry it; a single
            // message may split across multiple data lines that the spec joins
            // with \n. We reassemble by concatenating every "data:" payload.
            string? jsonPayload = ExtractDataPayload(text);
            if (jsonPayload == null)
                return sseBytes;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(jsonPayload);
                root = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            catch
            {
                return sseBytes; // not valid JSON-RPC — pass through
            }

            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("tools", out var toolsEl))
                return sseBytes; // not a tools result — pass through

            // Rebuild the tools array with our two prepended. We serialize the
            // original tools elements verbatim (preserving their full shape)
            // and append our constant descriptions.
            using var outBuf = new MemoryStream();
            using (var writer = new Utf8JsonWriter(outBuf))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                if (root.TryGetProperty("id", out var idEl))
                {
                    writer.WritePropertyName("id");
                    idEl.WriteTo(writer);
                }
                writer.WritePropertyName("result");
                writer.WriteStartObject();
                foreach (var prop in result.EnumerateObject())
                {
                    if (prop.NameEquals("tools"))
                    {
                        writer.WritePropertyName("tools");
                        writer.WriteStartArray();
                        // Gateway tools first so the agent sees the multiplexer
                        // surface ahead of per-instance debugger tools.
                        foreach (var gt in ToolElements.Value)
                            gt.WriteTo(writer);
                        // Then every VS-provided tool, verbatim.
                        foreach (var t in toolsEl.EnumerateArray())
                            t.WriteTo(writer);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject(); // result
                writer.WriteEndObject(); // root
            }

            string newJson = Encoding.UTF8.GetString(outBuf.ToArray());
            string newSse = "event: message\ndata: " + newJson + "\n\n";
            return Encoding.UTF8.GetBytes(newSse);
        }

        /// <summary>
        /// Extract the joined payload of every "data:" line in an SSE frame.
        /// Returns null if no data line is present. Per the SSE spec, multiple
        /// data lines are concatenated with "\n".
        /// </summary>
        private static string? ExtractDataPayload(string sseText)
        {
            var parts = new List<string>();
            foreach (string rawLine in sseText.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    string rest = line.Substring(5);
                    if (rest.StartsWith(" ")) rest = rest.Substring(1);
                    parts.Add(rest);
                }
            }
            if (parts.Count == 0) return null;
            return string.Join("\n", parts);
        }
    }
}
