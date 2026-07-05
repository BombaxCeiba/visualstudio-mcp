using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// net48 polyfill：C# 编译器需要 System.Runtime.CompilerServices.IsExternalInit
// 来合成 record 的 init-only 属性 setter。.NET Framework 4.8 不附带它；
// 在此定义，使 `record` 类型无需添加运行时包即可编译。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace VsMcp
{
    // ===========================================================================
    // F-6 typed result DTOs. 工具成功结果经 SafeCall → McpJson.ToTextResult 用
    // McpJson.ReadableOptions（宽松编码器，不转义中文）序列化进 TextContentBlock；
    // 错误结果（ErrorResult）同样走 ReadableOptions。不再使用 SDK 的
    // McpJsonUtilities.DefaultOptions —— 其 Encoder 为 null，会把中文等非 ASCII
    // 转义成 \uXXXX，模型最终收到的是字面转义串而非可读文本。
    // 之前手写的 JSON 层（EscapeJson/SerializeError 等）已在 DebuggerFacade.cs
    // 中删除——这些 record 全盘取代了它。
    // ===========================================================================

    /// <summary>
    /// 可读 JSON 序列化基础设施。MCP SDK 的 <see cref="McpJsonUtilities"/>.DefaultOptions
    /// 其 Encoder 为 null（等价 <see cref="JavaScriptEncoder"/>.Default），会把非 ASCII
    /// 字符（中文等）和引号转义为 \uXXXX；而 SDK 把工具返回的 DTO 自动序列化进
    /// <see cref="TextContentBlock.Text"/> 时正用该 DefaultOptions，导致模型最终收到字面
    /// 的转义串（生成 而非可读的"生成"）。该 DefaultOptions 只读且 getter 返回的实例
    /// 已被冻结、<see cref="McpServerOptions"/> 也无 JSON 注入点，无法在 SDK 层替换 ——
    /// 故工具成功结果统一经 <see cref="ToTextResult"/> 用本类的宽松选项序列化后包成
    /// <see cref="CallToolResult"/> 返回（SDK 对 CallToolResult 原样透传）。最外层 wire
    /// 上中文会被 SDK 的 DefaultOptions 合法转义为 \uXXXX，客户端 JSON 解码后还原为真实
    /// 字符，模型看到可读文本。
    /// </summary>
    internal static class McpJson
    {
        /// <summary>宽松编码器：不转义非 ASCII（中文等）与 HTML 敏感字符。MCP 的
        /// JSON-RPC 由客户端 JSON 解析器消费，非嵌入 HTML，故 UnsafeRelaxed 的 XSS
        /// 风险在此场景不适用。</summary>
        public static JsonSerializerOptions ReadableOptions { get; } = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>把任意 DTO 用 <see cref="ReadableOptions"/> 序列化进单个
        /// <see cref="TextContentBlock"/> 的 <see cref="CallToolResult"/>。供 SafeCall
        /// 成功路径与 <see cref="ErrorResult.ToCallToolResult"/> 复用。</summary>
        public static CallToolResult ToTextResult<T>(T dto) => new()
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = JsonSerializer.Serialize(dto, ReadableOptions) },
            },
        };
    }

    /// <summary><c>get_debugger_state</c> 的结果。</summary>
    public sealed record DebuggerStateResult(string State);

    /// <summary><c>set_breakpoint</c> 的结果。</summary>
    public sealed record BreakpointSetResult(string File, int Line, bool Enabled, string Condition, string Name);

    /// <summary><c>list_breakpoints</c> 中的单个断点。</summary>
    public sealed record BreakpointInfo(string File, int Line, int Column, bool Enabled, string Condition, string Name);

    /// <summary><c>list_breakpoints</c> 的结果。</summary>
    public sealed record BreakpointListResult(IReadOnlyList<BreakpointInfo> Breakpoints, int Total);

    /// <summary><c>delete_breakpoint</c> 的结果。</summary>
    public sealed record BreakpointDeleteResult(bool Deleted, string File, int Line);

    /// <summary>
    /// <c>clear_all_breakpoints</c> 的结果。默认（confirm=false）为干跑：
    /// Deleted=0、WasDryRun=true、WouldDelete=N。传 confirm=true
    /// 才真正删除。
    /// </summary>
    public sealed record BreakpointClearResult(int Deleted, bool WasDryRun, int WouldDelete, string Message);

    /// <summary><c>build_solution</c> 的结果。</summary>
    public sealed record BuildSolutionResult(string Configuration, int FailedProjects, bool Succeeded);

    /// <summary>
    /// <c>continue_execution</c>、<c>step_into/over/out</c>、
    /// <c>stop_debugging</c> 的结果。D-11 / F-15：该字段是 <c>State</c>
    /// （序列化为 <c>state</c>）——绝不是 <c>Status</c>。这合并了
    /// 之前 state/status/debugger_state 三键不一致的问题。
    /// </summary>
    public sealed record ExecutionResult(string State, string Message);

    /// <summary><c>get_call_stack</c> 中的单个栈帧。</summary>
    public sealed record StackFrameInfo(int FrameIndex, string FunctionName, string Module, string ReturnType);

    /// <summary><c>get_call_stack</c> 的结果。</summary>
    public sealed record CallStackResult(IReadOnlyList<StackFrameInfo> Frames, int Total, int Returned);

    /// <summary>
    /// 单个已求值的表达式或局部变量。可经 <see cref="Children"/> 递归展开
    /// （由 <c>get_variable_detail</c> 下钻）。<see cref="Truncated"/> +
    /// <see cref="Hint"/> 在命中字符预算时引导 LLM 下钻。
    /// </summary>
    public sealed record ExpressionInfo(
        string Name,
        string Type,
        string? Value,
        bool Error,
        bool HasChildren,
        IReadOnlyList<ExpressionInfo> Children,
        bool Truncated,
        string? Hint);

    /// <summary><c>list_local_variables</c> 的结果：当前栈帧所有 local 的浅列表
    ///（只顶层字段，children 不展开；看 <see cref="ExpressionInfo.HasChildren"/>，
    /// 需要下钻用 <c>get_variable_detail</c>）。</summary>
    public sealed record LocalsResult(IReadOnlyList<ExpressionInfo> Locals, int Total, int Returned, bool Truncated);

    /// <summary><c>evaluate_expression</c> 的结果：单个表达式求值后的
    /// <see cref="ExpressionInfo"/> 树。</summary>
    public sealed record ExpressionResult(ExpressionInfo Expression);

    /// <summary><c>get_variable_detail</c> 的结果：每个请求表达式对应一个
    /// <see cref="ExpressionInfo"/>（顺序与输入 <c>expressions</c> 一致）。
    /// 求值失败的表达式对应条目 <see cref="ExpressionInfo.Error"/> = true。</summary>
    public sealed record VariableDetailResult(IReadOnlyList<ExpressionInfo> Details);

    /// <summary>
    /// <c>get_session_info</c> 的结果。暴露 VS 已加载的解决方案（让 agent
    /// 知道它在操作哪个项目树）、其中的项目、以及一个尽力而为的调试目标。
    /// 除 <see cref="State"/> 外每个字段都可空 / 在未加载解决方案或无法解析
    /// 调试目标时可能为空。
    /// </summary>
    public sealed record SessionInfoResult(
        string? SolutionPath,
        string? SolutionDir,
        IReadOnlyList<string> Projects,
        string? DebugTarget,
        string State);

    // ===========================================================================
    // search_project / start_debugging 工具 DTO。这两个工具成对：
    // search_project 枚举已加载的解决方案，让 agent 定位拥有它要调试的
    // 可执行文件的项目（并读回尽力而为的 OutputTarget），然后 start_debugging
    // 经 VS 原生 IVsDebugger2.LaunchDebugTargets2 路径启动该 exe（不碰项目配置
    // 或 launchSettings.json）。
    // ===========================================================================

    /// <summary>
    /// <c>search_project</c> 暴露的单个项目。OutputTarget 是尽力解析到的
    /// 项目已构建可执行文件路径（C# 项目下来自 ConfigurationManager +
    /// OutputFileName）；当项目类型不暴露这些属性时（C++/vcxproj、解决方案
    /// 文件夹、杂项）为 null——start_debugging 直接接收绝对 exe 路径，
    /// 故此处为 null 非致命，仅作信息。
    /// </summary>
    public sealed record ProjectInfo(
        string Name,
        string UniqueName,
        string? FullName,
        string? Kind,
        bool IsStartupProject,
        string? OutputTarget);

    /// <summary><c>search_project</c> 的结果。</summary>
    public sealed record ProjectSearchResult(IReadOnlyList<ProjectInfo> Projects, int Total);

    /// <summary>
    /// <c>start_debugging</c> 的结果。仅当 LaunchDebugTargets2 返回成功 HRESULT
    /// 时 Started 为 true。State 是发起调用后立即观测到的调试器模式（通常为
    /// "running"；若命中启动断点则为 "break"；若发起并未真正接入调试器则为
    /// "design"）。EnvironmentVariables 回传给 CreateProcess 的有效 env（使用
    /// JSON 字符串形式时为解析后的字典，否则为原始 map）。Warnings 携带
    /// 非致命告警（如默认引擎兜底、env-block 说明）。
    /// </summary>
    public sealed record StartDebuggingResult(
        bool Started,
        string State,
        string Program,
        string? Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string>? EnvironmentVariables,
        string DebugEngine,
        IReadOnlyList<string>? Warnings);

    // ===========================================================================
    // D-12 错误契约。单个 ErrorResult 类型携带结构化错误（分类、人类可读
    // 消息、可选调试器状态），并自行序列化进一个标记 IsError=true 的
    // CallToolResult 的 TextContentBlock——这是 MCP 规范正确的方式来表示
    // LLM 可恢复的工具失败（对比旧的 200-OK-with-error-JSON）。
    // ===========================================================================

    /// <summary>
    /// 结构化工具错误。<see cref="State"/> 仅在与状态相关的错误时填充
    /// （D-11 规范化）；否则为 null，在序列化 JSON 中可省略或为 null
    /// （Web 默认行为）。<see cref="File"/> / <see cref="LoadedSolution"/>
    /// 仅由 <c>file_not_in_solution</c> 填充，使 agent 收到可操作的上下文
    /// （它要的是哪个文件 vs. 加载的是哪个解决方案）。
    /// </summary>
    public sealed record ErrorResult(
        string Error,
        string Message,
        string? State = null,
        string? File = null,
        string? LoadedSolution = null)
    {
        /// <summary>
        /// 构建一个 MCP 规范的 <see cref="CallToolResult"/>，其
        /// <see cref="CallToolResult.IsError"/> = true，含单个
        /// <see cref="TextContentBlock"/>，文本为该 ErrorResult 经
        /// <see cref="McpJson.ReadableOptions"/> 序列化的结果。
        /// </summary>
        public CallToolResult ToCallToolResult() => new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(this, McpJson.ReadableOptions)
                }
            },
        };
    }

    // ===========================================================================
    // 类型化异常。facade 抛出它们而非构造
    /// SerializeError 字符串；SafeCall 把它们转换为 ErrorResult。
    // ===========================================================================

    /// <summary>
    /// 当工具要求调试器处于 break 模式但它处于 design/run 模式时抛出。
    /// 携带当前状态字符串，使 SafeCall 能填充
    /// <see cref="ErrorResult.State"/>。
    /// </summary>
    public sealed class RequireBreakModeException : Exception
    {
        public string State { get; }

        public RequireBreakModeException(string message, string state) : base(message)
        {
            State = state;
        }
    }

    /// <summary>
    /// 当 <c>delete_breakpoint</c>（或类似工具）找不到所引用的断点时抛出。
    /// </summary>
    public sealed class BreakpointNotFoundException : Exception
    {
        public BreakpointNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// <c>set_breakpoint</c> 在 VS 已加载解决方案但所请求文件不属于它时抛出。
    /// 携带所请求的文件路径和已加载的解决方案路径，使 SafeCall 能填充
    /// <see cref="ErrorResult.File"/> + <see cref="ErrorResult.LoadedSolution"/>
    /// 以提供可操作的 agent 上下文。
    /// </summary>
    public sealed class FileNotInSolutionException : Exception
    {
        public string File { get; }
        public string LoadedSolution { get; }

        public FileNotInSolutionException(string message, string file, string loadedSolution)
            : base(message)
        {
            File = file;
            LoadedSolution = loadedSolution;
        }
    }

    // ===========================================================================
    // SafeCall——唯一的错误路由边界。每个调用 facade 的工具 lambda 注册到
    // MCP SDK 时都经此包装，使 facade 异常成为
    // CallToolResult{IsError=true}+ErrorResult。
    // OperationCanceledException 原样重抛：它表示关停，绝不能被吞进错误响应。
    // ===========================================================================

    /// <summary>
    /// 将 facade 异常路由进 D-12 结构化错误契约，并经
    /// <see cref="McpJson.ToTextResult"/> 包装每个成功结果，使 LLM 看到可读的
    /// （未转义的）JSON。放宽的 <c>Task&lt;object&gt;</c> 返回类型满足 MCP SDK
    /// 的工具调用契约：成功与错误路径都产出 <see cref="CallToolResult"/>，SDK
    /// 原样返回（REMEDIATION fact #7），而非经转义中文的
    /// <c>McpJsonUtilities.DefaultOptions</c> 重新序列化。
    /// </summary>
    public static class SafeCall
    {
        /// <summary>工具回复日志：若设置，每次 Wrap 返回前把 CallToolResult 的文本内容
        /// 打到这里（VS Output "VS MCP" 面板），便于即时观察 agent 实际收到的回复。由
        /// <see cref="McpRequestProcessor"/> 构造时注入。静态：VS 进程内通常一个 processor。</summary>
        internal static ILogger? ReplyLogger;

#pragma warning disable VSTHRD200 // "Wrap"/"WrapText" 是 REMEDIATION/PATTERNS/plan 中确立的名称；调用方把结果 Task 交给 MCP SDK 且从不直接 await，故 "Async" 后缀会误导。
        public static async Task<object> Wrap<T>(Func<Task<T>> work, CancellationToken ct)
#pragma warning restore VSTHRD200
        {
            CallToolResult result;
            try
            {
                // 用宽松编码器把 DTO 序列化进 CallToolResult 返回，绕过 SDK 对 DTO 的
                // 自动序列化（它用转义中文的 McpJsonUtilities.DefaultOptions）。SDK 对
                // CallToolResult 原样透传；最外层 wire 上的合法 \uXXXX 转义由客户端 JSON
                // 解码还原为真实字符。详见 McpJson。
                T r = await work().ConfigureAwait(false);
                result = McpJson.ToTextResult(r);
            }
            catch (OperationCanceledException)
            {
                // 关停信号——透明重抛。绝不能转为 ErrorResult
                // （会把取消掩盖为工具失败，并混淆 SDK 的拆除路径）。
                throw;
            }
            catch (Exception ex)
            {
                result = ToErrorResult(ex).ToCallToolResult();
            }
            LogReply(result);
            return result;
        }

        /// <summary>
        /// 纯文本返回路径的包装：与 <see cref="Wrap{T}"/> 完全相同的错误边界与
        /// 回复日志（<see cref="LogReply"/>），但成功路径不做 JSON 序列化——
        /// 直接把 facade 返回的 string 包进单个 <see cref="TextContentBlock"/>。
        /// 供返回日志、调用栈、变量值等"阅读型"内容的工具使用，避免 JSON 包裹
        /// 导致的换行/引号转义与 token 浪费（与 find_symbol 同一动机）。错误路径
        /// 与 Wrap 一致：facade 抛出的类型化异常经 <see cref="ToErrorResult"/> 转
        /// 成 IsError=true 的 ErrorResult，回复仍照常记进 VS Output 面板。
        /// </summary>
#pragma warning disable VSTHRD200 // 同 Wrap：结果 Task 交给 MCP SDK，从不直接 await。
        public static async Task<object> WrapText(Func<Task<string>> work, CancellationToken ct)
#pragma warning restore VSTHRD200
        {
            CallToolResult result;
            try
            {
                string text = await work().ConfigureAwait(false);
                result = new CallToolResult
                {
                    Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
                };
            }
            catch (OperationCanceledException)
            {
                // 关停信号——透明重抛（同 Wrap）。
                throw;
            }
            catch (Exception ex)
            {
                result = ToErrorResult(ex).ToCallToolResult();
            }
            LogReply(result);
            return result;
        }

        /// <summary>
        /// 把 facade 异常映射成结构化 <see cref="ErrorResult"/>。<see cref="Wrap{T}"/>
        /// 与 <see cref="WrapText"/> 共用，确保 JSON 与纯文本两条返回路径的错误契约
        /// 完全一致。<see cref="OperationCanceledException"/> 表示关停，由调用方重抛，
        /// 不在此处理。
        /// </summary>
        private static ErrorResult ToErrorResult(Exception ex)
        {
            switch (ex)
            {
                case RequireBreakModeException r:
                    return new ErrorResult("not_in_break_mode", r.Message, r.State);
                case BreakpointNotFoundException b:
                    return new ErrorResult("breakpoint_not_found", b.Message);
                case FileNotInSolutionException f:
                    // file_not_in_solution 同时携带所请求的文件和已加载的解决方案，
                    // 使 agent 能核对它本应引用哪个项目树，而非默默空操作。
                    return new ErrorResult(
                        "file_not_in_solution",
                        f.Message,
                        File: f.File,
                        LoadedSolution: f.LoadedSolution);
                default:
                    // T-05-03-01 缓解：只有 ex.Message 传给 LLM，
                    // 绝不传 StackTrace 或固定分类标签之外的内部类型名。
                    return new ErrorResult("internal_error", ex.Message);
            }
        }

        /// <summary>给手动构造 <see cref="CallToolResult"/> 的工具用的回复日志入口：
        /// find_symbol 返回纯文本 CallToolResult、不经 <see cref="Wrap{T}"/>，故 Wrap 末尾的
        /// <see cref="LogReply"/> 不会被触发；调本方法补记一次，保证所有工具的回复都打到面板。
        /// 记录后原样返回 result。</summary>
        internal static CallToolResult LogAndReturn(CallToolResult result)
        {
            LogReply(result);
            return result;
        }

        /// <summary>把 CallToolResult 的 text content 打到 <see cref="ReplyLogger"/>（VS Output
        /// 窗口 "VS MCP" 面板），方便即时观察 agent 收到的回复。原样打印，绝不截断——
        /// 面板看到的必须和 agent 收到的完全一致。打印异常绝不影响返回。</summary>
        private static void LogReply(CallToolResult result)
        {
            var logger = ReplyLogger;
            if (logger == null) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var c in result.Content)
                {
                    if (c is TextContentBlock t && !string.IsNullOrEmpty(t.Text))
                        sb.Append(t.Text);
                }
                if (sb.Length == 0) return;
                // 原样打印，绝不截断——面板看到的必须和 agent 收到的完全一致，
                // 否则失去"立即观察 agent 实际收到的内容"的意义。
                logger.LogInformation("← MCP 回复：\n{text}", sb.ToString());
            }
            catch
            {
                // 打印绝不能影响工具返回路径
            }
        }
    }
}
