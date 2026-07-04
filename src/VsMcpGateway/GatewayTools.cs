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
    /// 两个 Gateway 级工具、它们的描述、它们的处理器，以及把它们缝合进 VS
    /// tools/list 响应的 SSE 注入器。所有 Gateway 感知的 MCP 逻辑都在这里；
    /// Program.cs 只是调用本类的 HTTP/pipe 管道。
    /// </summary>
    public static class GatewayTools
    {
        public const string ListInstancesName = "list_vs_instances";
        public const string SelectInstanceName = "select_vs_instance";

        /// <summary>JSON 序列化的工具描述，camelCase，匹配 VS 在 tools/list 结果里
        /// 返回的形态。一次性解析成 JsonElement 形式，可直接拼进 result.tools 数组。</summary>
        private static readonly Lazy<JsonElement[]> ToolElements = new Lazy<JsonElement[]>(() =>
        {
            string listJson = "{\"name\":\"list_vs_instances\",\"description\":\"Lists all running Visual Studio instances connected to the gateway. Returns PID, solution path, solution directory, and debugger state for each instance.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{},\"required\":[]},\"annotations\":{\"readOnlyHint\":true}}";
            string selectJson = "{\"name\":\"select_vs_instance\",\"description\":\"Binds this MCP session to a specific VS instance. All subsequent tool calls route to it. Use list_vs_instances first to find available PIDs.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"pid\":{\"type\":\"integer\",\"description\":\"VS process ID to bind to\"}},\"required\":[\"pid\"]}}";
            using var d1 = JsonDocument.Parse(listJson);
            using var d2 = JsonDocument.Parse(selectJson);
            // 克隆是因为 documents 会被释放；GetRawText 重新解析是拥有元素生命
            // 周期最简单的方式。
            return new[]
            {
                JsonSerializer.Deserialize<JsonElement>(d1.RootElement.GetRawText()),
                JsonSerializer.Deserialize<JsonElement>(d2.RootElement.GetRawText()),
            };
        });

        /// <summary>判断 <paramref name="toolName"/> 是否是 Gateway 自处理的工具之一。</summary>
        public static bool IsGatewayTool(string? toolName) =>
            string.Equals(toolName, ListInstancesName, StringComparison.Ordinal) ||
            string.Equals(toolName, SelectInstanceName, StringComparison.Ordinal);

        /// <summary>
        /// 从注册表快照构造 list_vs_instances 的载荷。debuggerState 来自 register
        /// 帧的快照（Wave 2 没有实时状态；Wave 3+ 可能刷新它）。返回该工具文本
        /// 内容的 JSON 文本。
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
                    writer.WriteNull("debuggerState"); // Wave 2：register 没有实时状态
                    writer.WriteString("vsVersion", string.IsNullOrEmpty(info.VsVersion) ? null : info.VsVersion);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// 为 list_vs_instances 构造 JSON-RPC 响应（一个携带 instances JSON 的
        /// 文本内容块）。
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
        /// 为 select_vs_instance 成功时构造 JSON-RPC 响应。
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
        /// 构造一个 JSON-RPC error 响应（用于未 initialize 就 select、未知 pid 等）。
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
        /// 构造一个包装成 MCP tool-call IsError result 的 JSON-RPC error 响应
        /// （用于应作为工具错误而非协议错误浮现给 agent 的 gateway 工具失败）。
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

        // ─────────────────────── 拦截 / hint 消息 ──────────────────────
        //
        // ①③④ 层都以 tool-error 文本形式浮现给 agent（设计文档 §①/§③/§④），让
        // agent 把引导转达给用户。消息文本在设计文档给出处逐字遵循。

        /// <summary>把一个实例渲染成列表行。仅解决方案信息（Wave 2/3 不从 register
        /// 帧携带实时调试器状态；Wave 4 的 heartbeat 可能补充它）。</summary>
        private static string FormatInstance(InstanceEntry inst)
        {
            var info = inst.Info;
            string sol = !string.IsNullOrEmpty(info.SolutionPath)
                ? info.SolutionPath!
                : "(no solution)";
            return $"  PID {info.Pid}: {sol}";
        }

        /// <summary>
        /// ① Header 未命中的拦截消息（设计文档 §① "未命中时的拦截消息"）。列出
        /// 每个可用实例，让 agent 告诉用户该打开/配置哪个。
        /// </summary>
        public static string BuildWorkspaceMissMessage(string workspace, IReadOnlyCollection<InstanceEntry> instances)
        {
            var sb = new StringBuilder();
            sb.Append("No VS instance found for workspace '")
              .Append(workspace)
              .Append("'.").Append('\n')
              .Append("Ask the user to open the project in Visual Studio first,")
              .Append('\n')
              .Append("or configure X-VS-Workspace to match an existing instance.")
              .Append('\n');
            if (instances.Count > 0)
            {
                sb.Append('\n').Append("Available instances:").Append('\n');
                foreach (var inst in instances)
                    sb.Append(FormatInstance(inst)).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// ① Header 歧义消息：超过一个 VS 的 SolutionDir 匹配到同一个 workspace
        /// header。agent 应让用户提供更具体的路径或改用 select_vs_instance。
        /// </summary>
        public static string BuildAmbiguousMessage(string workspace, IReadOnlyCollection<InstanceEntry> matched)
        {
            var sb = new StringBuilder();
            sb.Append("Workspace '")
              .Append(workspace)
              .Append("' matches multiple VS instances.")
              .Append('\n')
              .Append("Add a more specific X-VS-Workspace path, or call select_vs_instance.")
              .Append('\n')
              .Append('\n')
              .Append("Matching instances:").Append('\n');
            foreach (var inst in matched)
                sb.Append(FormatInstance(inst)).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// ④ 多实例下未绑定请求的拦截消息（设计文档 §④ "拦截未绑定请求"）。内嵌
        /// 一个可直接粘贴的 <c>.mcp.json</c> 示例；JSON 字符串里反斜杠已加倍，让
        /// agent 可直接原样引用给用户。
        /// </summary>
        public static string BuildInterceptMessage(IReadOnlyCollection<InstanceEntry> instances)
        {
            var sb = new StringBuilder();
            sb.Append("Multiple VS instances are running but none is bound to this session.")
              .Append('\n');
            if (instances.Count > 0)
            {
                sb.Append('\n').Append("Available instances:").Append('\n');
                foreach (var inst in instances)
                    sb.Append(FormatInstance(inst)).Append('\n');
            }
            sb.Append('\n')
              .Append("To resolve this, ask the user to either:").Append('\n')
              .Append("1. Tell you which project to work on, then call select_vs_instance").Append('\n')
              .Append("2. Add X-VS-Workspace to the MCP server config in their project-level .mcp.json").Append('\n')
              .Append('\n')
              .Append("Example .mcp.json the user can create:").Append('\n')
              .Append('{').Append('\n')
              .Append("  \"mcpServers\": {").Append('\n')
              .Append("    \"vs-debugger\": {").Append('\n')
              .Append("      \"url\": \"http://127.0.0.1:43210/mcp/\",").Append('\n')
              .Append("      \"headers\": { \"X-VS-Workspace\": \"D:\\\\projects\\\\MyApp\" }").Append('\n')
              .Append("    }").Append('\n')
              .Append("  }").Append('\n')
              .Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 自动绑定后注入到第一个 tool result 的 hint 文本（设计文档 §③）。
        /// 一次性——只有 ③ 之后的第一次工具调用会浮现它。
        /// </summary>
        public static string BuildAutoBindHint(InstanceEntry inst)
        {
            var info = inst.Info;
            string sol = !string.IsNullOrEmpty(info.SolutionPath)
                ? info.SolutionPath!
                : "(no solution)";
            string dir = !string.IsNullOrEmpty(info.SolutionDir)
                ? info.SolutionDir!
                : "(none)";
            return
                "[VS MCP] Auto-bound to VS instance PID " + info.Pid + ".\n" +
                "Solution: " + sol + "\n" +
                "Solution dir: " + dir + "\n" +
                "If you need to target a different VS instance, call list_vs_instances.";
        }

        /// <summary>
        /// 在缓冲的 tools/call SSE 响应的 <c>result.content</c> 数组前端注入一段
        /// hint 文本。复用 <see cref="InjectIntoToolsListSse"/> 的 SSE data 载荷
        /// 提取模式。返回改写后的字节及是否注入。非 tools/call 响应（通知、没有
        /// <c>content</c> 数组的 error 信封）原样返回且 <c>injected=false</c>，
        /// 让转发路径对它们保持流式。
        /// </summary>
        public static (byte[] rewritten, bool injected) InjectHintIntoToolResultSse(byte[] sseBytes, string hint)
        {
            if (sseBytes == null || sseBytes.Length == 0)
                return (sseBytes ?? Array.Empty<byte>(), false);

            string text = Encoding.UTF8.GetString(sseBytes);
            string? jsonPayload = ExtractDataPayload(text);
            if (jsonPayload == null)
                return (sseBytes, false);

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(jsonPayload);
                root = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            catch
            {
                return (sseBytes, false); // 不是有效的 JSON-RPC——透传
            }

            // 只有带 content 数组的 result 才是我们能前置的 tools/call 成功响应。
            // 错误/通知保持不动。
            if (!root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("content", out var contentEl) ||
                contentEl.ValueKind != JsonValueKind.Array)
            {
                return (sseBytes, false);
            }

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
                    if (prop.NameEquals("content"))
                    {
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        // hint 合并到第一个 text block 的末尾（结果在前、hint 在后），而非作为
                        // 独立 block 放 content[0]。实测后者会让 agent 把 hint 当成完整回复、
                        // 忽略 content[1] 的真实结果；合并到单 text block 后，agent 读一个 block
                        // 即看到结果 + 绑定上下文，不会漏读。结果在前保证核心信息优先。
                        bool merged = false;
                        foreach (var c in contentEl.EnumerateArray())
                        {
                            if (!merged
                                && c.TryGetProperty("type", out var ctype) && ctype.ValueKind == JsonValueKind.String && ctype.GetString() == "text"
                                && c.TryGetProperty("text", out var ctext) && ctext.ValueKind == JsonValueKind.String)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("type", "text");
                                writer.WriteString("text", ctext.GetString() + "\n\n" + hint);
                                writer.WriteEndObject();
                                merged = true;
                            }
                            else
                            {
                                c.WriteTo(writer);
                            }
                        }
                        if (!merged)
                        {
                            // 没有 text block（如图像结果），追加一个 hint text block。
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", hint);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject(); // result 结束
                writer.WriteEndObject(); // root 结束
            }

            string newJson = Encoding.UTF8.GetString(outBuf.ToArray());
            string newSse = "event: message\ndata: " + newJson + "\n\n";
            return (Encoding.UTF8.GetBytes(newSse), true);
        }

        /// <summary>
        /// 把两个 gateway 工具描述注入缓冲的 tools/list SSE 响应。VS 响应是
        /// text/event-stream：
        /// <c>event: message\ndata: {"jsonrpc":...,"result":{"tools":[...]}}\n\n</c>
        /// 我们提取 data JSON，把我们的工具拼到 result.tools 数组上，再重新
        /// 封装成 SSE。返回改写后的字节。
        ///
        /// 若缓冲不是可解析的 tools/list 响应（如 error 信封），原样返回，让错误
        /// 仍能到达客户端。
        /// </summary>
        public static byte[] InjectIntoToolsListSse(byte[] sseBytes)
        {
            string text = Encoding.UTF8.GetString(sseBytes);

            // 找 JSON-RPC 载荷。SSE 的 "data:" 行携带它；单条消息可能跨多个 data
            // 行，规范用 \n 连接。我们通过拼接每个 "data:" 载荷来重组。
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
                return sseBytes; // 不是有效的 JSON-RPC——透传
            }

            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("tools", out var toolsEl))
                return sseBytes; // 不是 tools result——透传

            // 重建 tools 数组，前置我们的两个工具。我们把原始 tools 元素逐字
            // 序列化（保留其完整形态），再追加我们的常量描述。
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
                        // Gateway 工具在前，让 agent 在各实例的调试器工具之前先
                        // 看到多路复用器界面。
                        foreach (var gt in ToolElements.Value)
                            gt.WriteTo(writer);
                        // 然后是每个 VS 提供的工具，逐字保留。
                        foreach (var t in toolsEl.EnumerateArray())
                            t.WriteTo(writer);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject(); // result 结束
                writer.WriteEndObject(); // root 结束
            }

            string newJson = Encoding.UTF8.GetString(outBuf.ToArray());
            string newSse = "event: message\ndata: " + newJson + "\n\n";
            return Encoding.UTF8.GetBytes(newSse);
        }

        /// <summary>
        /// 提取一个 SSE 帧里所有 "data:" 行的拼接载荷。没有 data 行则返回 null。
        /// 按 SSE 规范，多个 data 行用 "\n" 拼接。
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
