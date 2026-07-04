extern alias vsmpc;
using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using vsmpc::VsMcp;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// SafeCall.Wrap 错误路由契约的单元测试。
    /// SafeCall 是唯一边界，把类型化 facade 异常转换为符合 MCP 规范的
    /// CallToolResult{IsError=true}+ErrorResult 响应，并透明地重新抛出
    /// OperationCanceledException（关闭不得被吞成错误响应）。
    /// </summary>
    public class SafeCallTests
    {
        // --- RequireBreakModeException -> not_in_break_mode ---

        [Fact]
        public async Task Wrap_RequireBreakModeException_MapsToNotInBreakModeError()
        {
            var ex = new RequireBreakModeException("need break", "design");
            object result = await SafeCall.Wrap<object>(
                () => throw ex, CancellationToken.None);

            var ctr = Assert.IsType<CallToolResult>(result);
            Assert.True(ctr.IsError);
            Assert.Equal("not_in_break_mode", ExtractErrorField(ctr));
            Assert.Equal("design", ExtractStateField(ctr));
        }

        // --- BreakpointNotFoundException -> breakpoint_not_found ---

        [Fact]
        public async Task Wrap_BreakpointNotFoundException_MapsToBreakpointNotFoundError()
        {
            var ex = new BreakpointNotFoundException("no bp at file.cs:1");
            object result = await SafeCall.Wrap<object>(
                () => throw ex, CancellationToken.None);

            var ctr = Assert.IsType<CallToolResult>(result);
            Assert.True(ctr.IsError);
            Assert.Equal("breakpoint_not_found", ExtractErrorField(ctr));
        }

        // --- FileNotInSolutionException -> file_not_in_solution（+ 上下文字段） ---

        [Fact]
        public async Task Wrap_FileNotInSolutionException_MapsToFileNotInSolutionError()
        {
            var ex = new FileNotInSolutionException(
                "not in sln", file: @"C:\src\missing.cs", loadedSolution: @"C:\src\App.sln");
            object result = await SafeCall.Wrap<object>(
                () => throw ex, CancellationToken.None);

            var ctr = Assert.IsType<CallToolResult>(result);
            Assert.True(ctr.IsError);
            Assert.Equal("file_not_in_solution", ExtractErrorField(ctr));
            Assert.Equal(@"C:\src\missing.cs", ExtractField(ctr, "file"));
            Assert.Equal(@"C:\src\App.sln", ExtractField(ctr, "loadedSolution"));
        }

        // --- 通用 Exception -> internal_error ---

        [Fact]
        public async Task Wrap_GenericException_MapsToInternalError()
        {
            object result = await SafeCall.Wrap<object>(
                () => throw new InvalidOperationException("boom"),
                CancellationToken.None);

            var ctr = Assert.IsType<CallToolResult>(result);
            Assert.True(ctr.IsError);
            Assert.Equal("internal_error", ExtractErrorField(ctr));
            Assert.Equal("boom", ExtractMessageField(ctr));
        }

        // --- OperationCanceledException 被重新抛出（关闭透明性） ---

        [Fact]
        public async Task Wrap_OperationCanceledException_IsRethrownNotSwallowed()
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SafeCall.Wrap<object>(() => throw new OperationCanceledException(),
                              CancellationToken.None));
        }

        // --- 成功路径原样返回 DTO 值 ---

        [Fact]
        public async Task Wrap_Success_ReturnsFacadeValueAsReadableJson()
        {
            // 成功路径经 McpJson.ToTextResult 包装 DTO，使 LLM 看到可读（未转义）
            // 的 JSON，而非 SDK 默认的中文转义。返回的是一个 CallToolResult，其
            // 文本内容承载序列化后的 DTO；state 值必须能往返。
            object result = await SafeCall.Wrap(
                () => Task.FromResult<object>(new DebuggerStateResult("break")),
                CancellationToken.None);

            var ctr = Assert.IsType<CallToolResult>(result);
            // 成功路径让 IsError 保持未设置（null）—— 区别于错误路径的显式 true。
            // `!= true` 同时接受 null 和 false。
            Assert.True(ctr.IsError != true);
            var block = Assert.IsType<TextContentBlock>(ctr.Content[0]);
            Assert.Contains("break", block.Text);
        }

        // --- 辅助方法：解析 TextContentBlock JSON 取 ErrorResult 字段 ---

        private static string ExtractErrorField(CallToolResult ctr)
        {
            var block = Assert.IsType<TextContentBlock>(ctr.Content[0]);
            using var doc = System.Text.Json.JsonDocument.Parse(block.Text);
            return doc.RootElement.GetProperty("error").GetString()!;
        }

        private static string? ExtractStateField(CallToolResult ctr)
        {
            var block = Assert.IsType<TextContentBlock>(ctr.Content[0]);
            using var doc = System.Text.Json.JsonDocument.Parse(block.Text);
            return doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        }

        private static string ExtractMessageField(CallToolResult ctr)
        {
            var block = Assert.IsType<TextContentBlock>(ctr.Content[0]);
            using var doc = System.Text.Json.JsonDocument.Parse(block.Text);
            return doc.RootElement.GetProperty("message").GetString()!;
        }

        private static string? ExtractField(CallToolResult ctr, string fieldName)
        {
            var block = Assert.IsType<TextContentBlock>(ctr.Content[0]);
            using var doc = System.Text.Json.JsonDocument.Parse(block.Text);
            return doc.RootElement.TryGetProperty(fieldName, out var v) ? v.GetString() : null;
        }
    }
}
