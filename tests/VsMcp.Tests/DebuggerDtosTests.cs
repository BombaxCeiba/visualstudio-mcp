extern alias vsmpc;
using System.Collections.Generic;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using vsmpc::VsMcp;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// DebuggerDtos.cs 中每个类型化 DTO 的无 COM 快照测试。
    /// 断言基于解析后的 JSON 结构（而非子串），从而解决 F-14 的子串断言
    /// 弱点：若字段名漂移或键合并（如依 D-11/F-15 的 status->state），
    /// 这些测试会以结构差异显式失败。
    /// </summary>
    public class DebuggerDtosTests
    {
        // --- camelCase 序列化（McpJsonUtilities.DefaultOptions 是 Web 默认值） ---

        [Fact]
        public void DebuggerStateResult_SerializesToCamelCaseState()
        {
            var json = JsonSerializer.Serialize(new DebuggerStateResult("break"),
                                                McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("break", doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.TryGetProperty("State", out _), "PascalCase State leaked through camelCase policy");
        }

        [Fact]
        public void ExecutionResult_UsesState_NotStatus()
        {
            // D-11 / F-15：不一致的 state/status/debugger_state 键合并为单个
            // "state" 字段。这是回归守卫。
            var json = JsonSerializer.Serialize(new ExecutionResult("running", "ok"),
                                                McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("ok", doc.RootElement.GetProperty("message").GetString());
            Assert.False(doc.RootElement.TryGetProperty("status", out _), "status key leaked (F-15 regression)");
            Assert.False(doc.RootElement.TryGetProperty("debugger_state", out _), "debugger_state key leaked (F-15 regression)");
        }

        [Fact]
        public void BreakpointClearResult_SerializesDeletedCount()
        {
            var json = JsonSerializer.Serialize(new BreakpointClearResult(5, WasDryRun: false, WouldDelete: 5, Message: "test"),
                                                McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(5, doc.RootElement.GetProperty("deleted").GetInt32());
        }

        [Fact]
        public void BreakpointSetResult_SerializesAllFieldsCamelCase()
        {
            var dto = new BreakpointSetResult(
                File: @"C:\src\app.cs", Line: 42, Enabled: true,
                Condition: "x == 1", Name: "app.cs:42");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(@"C:\src\app.cs", root.GetProperty("file").GetString());
            Assert.Equal(42, root.GetProperty("line").GetInt32());
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.Equal("x == 1", root.GetProperty("condition").GetString());
            Assert.Equal("app.cs:42", root.GetProperty("name").GetString());
        }

        [Fact]
        public void BreakpointListResult_SerializesBreakpointArrayAndTotal()
        {
            var bp = new BreakpointInfo("file.cs", 1, 5, true, "", "file.cs:1");
            var dto = new BreakpointListResult(
                new List<BreakpointInfo> { bp }, Total: 1);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("breakpoints").GetArrayLength());
        }

        [Fact]
        public void LocalsResult_SerializesTruncatedFlagAndReturnedCount()
        {
            var dto = new LocalsResult(
                new List<ExpressionInfo>(), Total: 100, Returned: 37, Truncated: true);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(100, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(37, doc.RootElement.GetProperty("returned").GetInt32());
            Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        }

        [Fact]
        public void ExpressionInfo_NullValueAndHint_OmitOrSerializeAsNull()
        {
            // RESEARCH 陷阱 #4：McpJsonUtilities 是 Web 默认值；null 引用字段
            // 可能被省略。只要不抛异常且非 null 字段能往返，两种行为都接受。
            var dto = new ExpressionInfo(
                Name: "x", Type: "int", Value: null, Error: false,
                HasChildren: false, Children: new List<ExpressionInfo>(),
                Truncated: false, Hint: null);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("x", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("int", doc.RootElement.GetProperty("type").GetString());
            // 此处主要断言是不抛异常。
        }

        // --- ErrorResult.ToCallToolResult 契约（D-12） ---

        [Fact]
        public void ErrorResult_ToCallToolResult_SetsIsError_AndSerializesErrorContract()
        {
            var result = new ErrorResult("not_in_break_mode", "msg", "design").ToCallToolResult();

            Assert.True(result.IsError);
            Assert.NotNull(result.Content);
            Assert.True(result.Content.Count >= 1);

            var block = Assert.IsType<TextContentBlock>(result.Content[0]);
            using var doc = JsonDocument.Parse(block.Text);
            Assert.Equal("not_in_break_mode", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("msg", doc.RootElement.GetProperty("message").GetString());
            Assert.Equal("design", doc.RootElement.GetProperty("state").GetString());
        }

        [Fact]
        public void ErrorResult_ToCallToolResult_StateNull_DoesNotThrowAndSetsIsError()
        {
            // State 默认为 null（ErrorResult.State = null）：序列化后的 JSON
            // 可能省略 "state" 或发为 null（Web 默认行为）。契约断言是：
            // IsError 为 true、error 字段已设置，且无论 state 是否存在都不抛异常。
            var result = new ErrorResult("internal_error", "x").ToCallToolResult();

            Assert.True(result.IsError);
            var block = Assert.IsType<TextContentBlock>(result.Content[0]);
            using var doc = JsonDocument.Parse(block.Text);
            Assert.Equal("internal_error", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("x", doc.RootElement.GetProperty("message").GetString());
            // state 可能存在也可能不存在 —— 两者都合法。不要对它断言。
        }
    }
}
