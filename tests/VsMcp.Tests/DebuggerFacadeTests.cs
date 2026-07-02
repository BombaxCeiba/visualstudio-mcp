using System.Collections.Generic;
using System.Text.Json;
using ModelContextProtocol;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// DTO-shape tests for the records <see cref="DebuggerFacade"/>
    /// produces. The prior version asserted on the now-deleted hand-rolled
    /// JSON string builders (F-6 removed both); this rewrite asserts on the
    /// parsed JSON structure of each DTO the facade now returns, which is
    /// the F-14 fix (no substring matching on hand-built JSON).
    ///
    /// The facade itself requires a concrete AsyncPackage (VS SDK runtime)
    /// and is exercised in the plan 05-04 VS experimental-hive smoke
    /// test; these COM-free tests cover only the DTO shapes the facade
    /// emits — same surface, no VS dependency.
    /// </summary>
    public class DebuggerFacadeTests
    {
        // --- state field (D-11 / F-15 collapse) ---

        [Fact]
        public void DebuggerStateResult_ProducesStateKeyInCamelCase()
        {
            // Facade returns DebuggerStateResult for get_debugger_state.
            // The serialized JSON must carry the normalized "state" key
            // (not "State", "status", or "debugger_state").
            var dto = new DebuggerStateResult("break");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("break", doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.TryGetProperty("State", out _));
            Assert.False(doc.RootElement.TryGetProperty("status", out _));
            Assert.False(doc.RootElement.TryGetProperty("debugger_state", out _));
        }

        [Fact]
        public void ExecutionResult_UsesStateKey_NotStatus()
        {
            // continue_execution / step_* / stop_debugging return ExecutionResult.
            // D-11: the field is "state", killing the prior status/state split.
            var dto = new ExecutionResult("running", "Execution continued");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("Execution continued", doc.RootElement.GetProperty("message").GetString());
            Assert.False(doc.RootElement.TryGetProperty("status", out _));
        }

        // --- breakpoint DTOs ---

        [Fact]
        public void BreakpointSetResult_SerializesAllFieldsCamelCase()
        {
            var dto = new BreakpointSetResult(
                File: @"C:\src\app.cs", Line: 42, Enabled: true,
                Condition: "i == 0", Name: "app.cs:42");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(@"C:\src\app.cs", root.GetProperty("file").GetString());
            Assert.Equal(42, root.GetProperty("line").GetInt32());
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.Equal("i == 0", root.GetProperty("condition").GetString());
            Assert.Equal("app.cs:42", root.GetProperty("name").GetString());
        }

        [Fact]
        public void BreakpointListResult_SerializesArrayAndTotal()
        {
            var bp = new BreakpointInfo("file.cs", 1, 5, true, "", "file.cs:1");
            var dto = new BreakpointListResult(new List<BreakpointInfo> { bp }, Total: 1);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("breakpoints").GetArrayLength());
        }

        [Fact]
        public void BreakpointDeleteResult_SerializesDeletedFlagAndLocation()
        {
            var dto = new BreakpointDeleteResult(Deleted: true, File: "f.cs", Line: 7);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
            Assert.Equal("f.cs", doc.RootElement.GetProperty("file").GetString());
            Assert.Equal(7, doc.RootElement.GetProperty("line").GetInt32());
        }

        [Fact]
        public void BreakpointClearResult_SerializesDeletedCount()
        {
            var dto = new BreakpointClearResult(Deleted: 3, WasDryRun: false, WouldDelete: 3, Message: "test");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(3, doc.RootElement.GetProperty("deleted").GetInt32());
        }

        // --- call stack DTOs ---

        [Fact]
        public void CallStackResult_SerializesFramesAndCounts()
        {
            var frame = new StackFrameInfo(0, "Main", "app.dll", "void");
            var dto = new CallStackResult(
                new List<StackFrameInfo> { frame }, Total: 1, Returned: 1);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("returned").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("frames").GetArrayLength());
        }

        // --- locals / expression DTOs ---

        [Fact]
        public void LocalsResult_SerializesTruncationFlag()
        {
            var dto = new LocalsResult(
                new List<ExpressionInfo>(), Total: 50, Returned: 12, Truncated: true);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(50, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(12, doc.RootElement.GetProperty("returned").GetInt32());
            Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        }

        [Fact]
        public void ExpressionResult_WrapsSingleExpressionInfo()
        {
            var inner = new ExpressionInfo(
                Name: "obj", Type: "MyType", Value: "{ ... }", Error: false,
                HasChildren: true, Children: new List<ExpressionInfo>(),
                Truncated: false, Hint: null);
            var dto = new ExpressionResult(inner);
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("obj", doc.RootElement.GetProperty("expression").GetProperty("name").GetString());
        }

        // --- session info DTO (get_session_info) ---

        [Fact]
        public void SessionInfoResult_SerializesAllFieldsCamelCase()
        {
            var dto = new SessionInfoResult(
                SolutionPath: @"C:\src\App\App.sln",
                SolutionDir: @"C:\src\App",
                Projects: new List<string> { @"C:\src\App\App.csproj" },
                DebugTarget: "App.exe",
                State: "design");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(@"C:\src\App\App.sln", root.GetProperty("solutionPath").GetString());
            Assert.Equal(@"C:\src\App", root.GetProperty("solutionDir").GetString());
            Assert.Equal("App.exe", root.GetProperty("debugTarget").GetString());
            Assert.Equal("design", root.GetProperty("state").GetString());
            Assert.Equal(1, root.GetProperty("projects").GetArrayLength());
            Assert.Equal(@"C:\src\App\App.csproj",
                root.GetProperty("projects")[0].GetString());
        }

        [Fact]
        public void SessionInfoResult_NoSolution_SerializesNullsAndEmptyProjects()
        {
            var dto = new SessionInfoResult(
                SolutionPath: null,
                SolutionDir: null,
                Projects: new List<string>(),
                DebugTarget: null,
                State: "design");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Null reference fields may be omitted OR null under Web-default
            // serialization — both are valid; the only hard assertion is that
            // the empty projects array survives as an array and no exception
            // is thrown.
            Assert.Equal("design", root.GetProperty("state").GetString());
            Assert.Equal(0, root.GetProperty("projects").GetArrayLength());
        }

        // --- NormalizeFilePath (pure helper extracted from SetBreakpointAsync) ---

        [Theory]
        [InlineData(@"C:\src\app.cs", @"C:\SRC\APP.CS")]
        [InlineData(@"C:\src\..\src\app.cs", @"C:\SRC\APP.CS")]
        [InlineData(@"C:/src/app.cs", @"C:\SRC\APP.CS")]
        [InlineData(@"c:\SRC\app.CS", @"C:\SRC\APP.CS")]
        public void NormalizeFilePath_ResolvesRelativeSegments_MixedSeparators_AndCase(
            string input, string expected)
        {
            Assert.Equal(expected, DebuggerFacade.NormalizeFilePath(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void NormalizeFilePath_PassesThroughBlankInputUnchanged(string input)
        {
            Assert.Equal(input, DebuggerFacade.NormalizeFilePath(input));
        }
    }
}
