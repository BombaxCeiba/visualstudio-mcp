using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// Gateway 级工具描述、hint 消息构造。MCP 协议处理（initialize/tools-list）
    /// 由 SDK 自动完成——不再需要手工构造 JSON-RPC 响应。
    /// </summary>
    public static class GatewayTools
    {
        public const string ListInstancesName = "list_vs_instances";
        public const string SelectInstanceName = "select_vs_instance";

        /// <summary>ServerInstructions 常量（从 ToolSchema.ServerInstructions 搬过来）。</summary>
        public const string ServerInstructions =
            "This server provides Visual Studio integration tools (debugger, build, symbol navigation). " +
            "Tools route to a specific VS instance based on session binding or X-VS-Workspace header. " +
            "Use list_vs_instances to see available instances and select_vs_instance to bind.";

        /// <summary>判断 <paramref name="toolName"/> 是否是 Gateway 自处理的工具之一。</summary>
        public static bool IsGatewayTool(string? toolName) =>
            string.Equals(toolName, ListInstancesName, StringComparison.Ordinal) ||
            string.Equals(toolName, SelectInstanceName, StringComparison.Ordinal);

        // ─────────────────────── 本地 MCP 响应 ──────────────────────
        // 以下响应构造方法已废弃——SDK 自动处理 initialize/tools-list。
        // 临时删除 ToolSchema 引用；后续 SDK 重写完成后删除这些方法。

        /// <summary>
        /// 构造 initialize 的 JSON-RPC 响应（临时实现）。
        /// </summary>
        public static string BuildInitializeResponse(object jsonRpcId)
        {
            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                result = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new
                    {
                        tools = new { listChanged = false },
                    },
                    serverInfo = new
                    {
                        name = "vs-debugger-mcp",
                        version = ExtensionVersion.Current,
                    },
                    instructions = ServerInstructions,
                },
            };
            return JsonSerializer.Serialize(resp);
        }

        /// <summary>
        /// 构造 tools/list 的 JSON-RPC 响应（临时实现——仅返回 gateway 工具）。
        /// </summary>
        public static string BuildToolsListResponse(object jsonRpcId, bool includeGoToDefinition, bool includeEvalCsharp)
        {
            var allTools = new List<object>();
            allTools.Add(new
            {
                name = ListInstancesName,
                description = "Lists all running Visual Studio instances connected to the gateway. Returns PID, solution path, solution directory, and debugger state for each instance.",
                inputSchema = new { type = "object", properties = new { }, required = new string[] { } },
            });
            allTools.Add(new
            {
                name = SelectInstanceName,
                description = "Binds this MCP session to a specific VS instance. All subsequent tool calls route to it. Use list_vs_instances first to find available PIDs.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { pid = new { type = "integer", description = "VS process ID to bind to" } },
                    required = new[] { "pid" },
                },
            });
            // TODO: SDK 重写后移除——SDK 自动生成完整工具列表

            var resp = new
            {
                jsonrpc = "2.0",
                id = jsonRpcId,
                result = new { tools = allTools },
            };
            return JsonSerializer.Serialize(resp);
        }

        // ─────────────────────── gateway 工具响应 ──────────────────────

        /// <summary>
        /// 从注册表快照构造 list_vs_instances 的载荷。debuggerState 来自
        /// InstanceEntry.DebuggerState（由 debugger-state-changed 控制帧实时刷新；
        /// register 后尚未收到推送时为 null）。返回该工具文本内容的 JSON 文本。
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
                    if (string.IsNullOrEmpty(inst.DebuggerState))
                        writer.WriteNull("debuggerState");
                    else
                        writer.WriteString("debuggerState", inst.DebuggerState);
                    writer.WriteString("vsVersion", string.IsNullOrEmpty(info.VsVersion) ? null : info.VsVersion);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// 为 list_vs_instances 构造 JSON-RPC 响应。
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
        /// 构造一个包装成 MCP tool-call IsError result 的 JSON-RPC error 响应。
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

        /// <summary>把一个实例渲染成列表行（用于拦截/hint 消息）。</summary>
        private static string FormatInstance(InstanceEntry inst)
        {
            var info = inst.Info;
            string sol = !string.IsNullOrEmpty(info.SolutionPath)
                ? info.SolutionPath!
                : "(no solution)";
            return $"  PID {info.Pid}: {sol}";
        }

        /// <summary>
        /// ① Header 未命中的拦截消息。列出每个可用实例。
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
        /// ① Header 歧义消息：超过一个 VS 的 SolutionDir 匹配到同一个 workspace。
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
        /// ④ 多实例下未绑定请求的拦截消息。
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
    }
}
