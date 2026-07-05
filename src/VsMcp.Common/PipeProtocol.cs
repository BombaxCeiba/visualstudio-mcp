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
    /// 所有帧共有的判别包装。接收方先读 <see cref="Type"/> 再反序列化为具体
    /// DTO；此类型仅用于 switch 分派，不直接承载业务字段。
    /// </summary>
    public sealed class PipeFrameEnvelope
    {
        public string Type { get; set; } = "";
    }
}
