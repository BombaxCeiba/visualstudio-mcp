using System.Collections.Generic;

namespace VsMcp.Common
{
    // ─────────────────────────────────────────────────────────────────────
    // Gateway ↔ VS 帧协议。每帧由 PipeFraming 的长度前缀封装，payload 是这些
    // 类型之一的 JSON。帧的判别字段是 Type；接收方先读 Type 再反序列化为具体
    // DTO（见 PipeFraming.ReadFrameJsonAsync + JsonDocument 的用法）。
    //
    // request/head/data/end 四帧用 Id 配对，支持 Gateway 向同一 VS 并发转发
    // 多个 MCP session 的请求；register/solution-changed/heartbeat 是 VS 主动
    // 推送的控制帧，无配对 Id。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Gateway → VS：转发一个 MCP HTTP 请求。</summary>
    public sealed class PipeRequest
    {
        /// <summary>固定 "request"。Gateway 生成，用于配对响应。</summary>
        public string Type { get; set; } = "request";
        public string Id { get; set; } = "";
        public string Method { get; set; } = "POST";
        public string Path { get; set; } = "/mcp/";
        /// <summary>转发自 MCP 客户端的请求头（含 Accept / Mcp-Session-Id 等）。</summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        /// <summary>原始 JSON-RPC 请求体文本。</summary>
        public string Body { get; set; } = "";
    }

    /// <summary>VS → Gateway：对某请求的响应头。</summary>
    public sealed class PipeResponseHead
    {
        public string Type { get; set; } = "head";
        public string Id { get; set; } = "";
        public int Status { get; set; } = 200;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// VS → Gateway：一块 SSE 响应字节，实时转发以保长任务（build_solution）
    /// 的保活通知不被缓冲。<see cref="Body"/> 是该块字节的 base64 编码 —— SSE
    /// 帧理论上都是文本，但用 base64 承载原始字节可彻底规避 UTF-8 多字节字符
    /// 跨块边界的解码问题，且无需关心 SDK 写入粒度。
    /// </summary>
    public sealed class PipeDataChunk
    {
        public string Type { get; set; } = "data";
        public string Id { get; set; } = "";
        public string Body { get; set; } = "";
    }

    /// <summary>VS → Gateway：该 Id 的响应流结束。</summary>
    public sealed class PipeEnd
    {
        public string Type { get; set; } = "end";
        public string Id { get; set; } = "";
    }

    /// <summary>VS → Gateway：实例注册（连接建立后首帧）。</summary>
    public sealed class PipeRegister
    {
        public string Type { get; set; } = "register";
        public int Pid { get; set; }
        public string PipeName { get; set; } = "";
        public string? SolutionName { get; set; }
        public string? SolutionDir { get; set; }
        public string? SolutionPath { get; set; }
        public string VsVersion { get; set; } = "";
    }

    /// <summary>VS → Gateway：solution 打开/关闭后的信息更新。</summary>
    public sealed class PipeSolutionChanged
    {
        public string Type { get; set; } = "solution-changed";
        public int Pid { get; set; }
        public string? SolutionName { get; set; }
        public string? SolutionDir { get; set; }
        public string? SolutionPath { get; set; }
    }

    /// <summary>VS → Gateway：调试器模式切换（design/break/running）后的状态推送。
    /// 与 solution-changed 同为无 id 控制帧；State 字符串与 DebuggerFacade.MapState
    /// 输出一致。让 gateway 缓存的 InstanceEntry.DebuggerState 实时刷新，供
    /// list_vs_instances 返回真实值而非恒定 null。</summary>
    public sealed class PipeDebuggerStateChanged
    {
        public string Type { get; set; } = "debugger-state-changed";
        public int Pid { get; set; }
        public string? State { get; set; }
    }

    /// <summary>VS → Gateway：周期心跳（VS 存活探测）。</summary>
    public sealed class PipeHeartbeat
    {
        public string Type { get; set; } = "heartbeat";
        public int Pid { get; set; }
    }

    /// <summary>
    /// Gateway → VS：转发一个 tool 调用。Gateway 解析 MCP tools/call 后，
    /// 把工具名和原始参数 JSON 透传给 VS 端的 ToolExecutor。Id 复用现有
    /// head/data/end 的 Guid 配对机制，支持并发 tool-call。
    /// </summary>
    public sealed class PipeToolCall
    {
        /// <summary>固定 "tool-call"。Gateway 生成，用于配对响应。</summary>
        public string Type { get; set; } = "tool-call";
        public string Id { get; set; } = "";
        /// <summary>工具名（find_symbol / set_breakpoint 等）。</summary>
        public string Tool { get; set; } = "";
        /// <summary>MCP 原始参数 JSON 文本（params.arguments 的 GetRawText()）。
        /// VS 端用 JsonDocument.Parse 再按工具签名反序列化各参数。</summary>
        public string Arguments { get; set; } = "";
    }

    /// <summary>
    /// VS → Gateway：对某 tool-call 的结果。该帧结束对应 Id 的配对。
    /// Content 是 MCP content 的文本（find_symbol 返回纯文本，其它工具返回
    /// JSON 序列化的 DTO）。IsError=true 时 Content 是错误 JSON。
    /// </summary>
    public sealed class PipeToolResult
    {
        public string Type { get; set; } = "tool-result";
        public string Id { get; set; } = "";
        /// <summary>工具结果文本（纯文本或 JSON）。</summary>
        public string Content { get; set; } = "";
        /// <summary>true 时 Content 是错误 JSON，对应 MCP result.isError=true。</summary>
        public bool IsError { get; set; }
    }

    /// <summary>
    /// CallToolAsync 的返回值（Gateway 侧）。与帧 DTO PipeToolResult 区分：
    /// PipeToolResult 是 wire 帧类型，PipeToolResultData 是 PipeRouter
    /// 返回给 HTTP 处理器的内存类型。两端共享避免重复定义。
    /// 用 sealed class 而非 record——PipeProtocol.cs 源链接进 VsMcpGateway，
    /// 后者无 IsExternalInit polyfill，record 会编译失败。
    /// </summary>
    public sealed class PipeToolResultData
    {
        public string Content { get; }
        public bool IsError { get; }
        public PipeToolResultData(string content, bool isError) { Content = content; IsError = isError; }
    }

    /// <summary>
    /// 所有帧共有的判别包装。接收方先读 <see cref="Type"/> 再反序列化为具体
    /// DTO；此类型仅用于 switch 分派，不直接承载业务字段。
    /// </summary>
    public sealed class PipeFrameEnvelope
    {
        public string Type { get; set; } = "";
    }
}
