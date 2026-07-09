using System.Collections.Generic;

namespace VsMcp.Common
{
    // ─────────────────────────────────────────────────────────────────────
    // Gateway ↔ VS 帧协议。每帧由 PipeFraming 的长度前缀封装，payload 是这些
    // 类型之一的 JSON。帧的判别字段是 Type；接收方先读 Type 再反序列化为具体
    // DTO（见 PipeFraming.ReadFrameJsonAsync + JsonDocument 的用法）。
    //
    // tool-call / tool-result / tool-progress 三帧用 Id 配对，支持 Gateway 向
    // 同一 VS 并发转发多个 MCP session 的请求。register / solution-changed /
    // debugger-state-changed / heartbeat 是 VS 主动推送的控制帧，无配对 Id。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>VS → Gateway：实例注册（连接建立后首帧）。</summary>
    public sealed class PipeRegister
    {
        /// <summary>固定 "register"。</summary>
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
        /// <summary>固定 "solution-changed"。</summary>
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
        /// <summary>固定 "debugger-state-changed"。</summary>
        public string Type { get; set; } = "debugger-state-changed";
        public int Pid { get; set; }
        public string? State { get; set; }
    }

    /// <summary>VS → Gateway：周期心跳（VS 存活探测）。</summary>
    public sealed class PipeHeartbeat
    {
        /// <summary>固定 "heartbeat"。</summary>
        public string Type { get; set; } = "heartbeat";
        public int Pid { get; set; }
    }

    /// <summary>
    /// Gateway → VS：转发一个 tool 调用。Gateway 解析 MCP tools/call 后，
    /// 把工具名和原始参数 JSON 透传给 VS 端的 ToolExecutor。Id 用于配对
    /// tool-result / tool-progress 响应帧，支持并发 tool-call。
    /// </summary>
    public sealed class PipeToolCall
    {
        /// <summary>固定 "tool-call"。</summary>
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
        /// <summary>固定 "tool-result"。</summary>
        public string Type { get; set; } = "tool-result";
        public string Id { get; set; } = "";
        /// <summary>工具结果文本（纯文本或 JSON）。</summary>
        public string Content { get; set; } = "";
        /// <summary>true 时 Content 是错误 JSON，对应 MCP result.isError=true。</summary>
        public bool IsError { get; set; }
    }

    /// <summary>
    /// VS → Gateway：工具执行过程中的进度通知（如 build_solution 的构建日志）。
    /// 该帧不结束 Id 的配对——继续等待 tool-result 帧。Id 与配对的 tool-call /
    /// tool-result 同值，用于 Gateway 侧把进度通知路由到正确的 MCP session。
    /// Text 是进度文本（一行 log 或保活心跳），由 Gateway 转发为 MCP
    /// notifications/message 到客户端。
    /// </summary>
    public sealed class PipeToolProgress
    {
        /// <summary>固定 "tool-progress"。</summary>
        public string Type { get; set; } = "tool-progress";
        /// <summary>配对 Id（与 tool-call / tool-result 同值）。</summary>
        public string Id { get; set; } = "";
        /// <summary>进度文本（build log 行或保活心跳）。</summary>
        public string Text { get; set; } = "";
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
