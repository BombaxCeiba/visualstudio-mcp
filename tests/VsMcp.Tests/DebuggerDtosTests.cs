using System.Collections.Generic;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// COM-free snapshot tests for every typed DTO in DebuggerDtos.cs.
    /// Asserts on the parsed JSON structure (not substrings) so the
    /// F-14 substring-assertion weakness is resolved: if a field name
    /// drifts or a key collapses (e.g. status->state per D-11/F-15),
    /// these tests fail loudly with a structural diff.
    /// </summary>
    public class DebuggerDtosTests
    {
        // --- camelCase serialization (McpJsonUtilities.DefaultOptions is Web defaults) ---

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
            // D-11 / F-15: the inconsistent state/status/debugger_state keys
            // collapse to a single "state" field. This is the regression guard.
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
            // RESEARCH Pitfall #4: McpJsonUtilities is Web defaults; null
            // reference fields may be omitted. Either behavior is acceptable
            // as long as no exception is thrown and the non-null fields
            // survive the round-trip.
            var dto = new ExpressionInfo(
                Name: "x", Type: "int", Value: null, Error: false,
                HasChildren: false, Children: new List<ExpressionInfo>(),
                Truncated: false, Hint: null);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("x", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("int", doc.RootElement.GetProperty("type").GetString());
            // No exception thrown is the primary assertion here.
        }

        // --- ErrorResult.ToCallToolResult contract (D-12) ---

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
            // Default-null State (ErrorResult.State = null): the serialized
            // JSON may omit "state" OR emit it as null (Web-default behavior).
            // The contract assertion is: IsError is true, error field is set,
            // and no exception propagates regardless of state presence.
            var result = new ErrorResult("internal_error", "x").ToCallToolResult();

            Assert.True(result.IsError);
            var block = Assert.IsType<TextContentBlock>(result.Content[0]);
            using var doc = JsonDocument.Parse(block.Text);
            Assert.Equal("internal_error", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("x", doc.RootElement.GetProperty("message").GetString());
            // state may or may not be present — both are valid. Do not assert on it.
        }
    }
}
