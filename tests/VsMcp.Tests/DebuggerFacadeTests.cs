extern alias vsmpc;
using System.Collections.Generic;
using System.Text.Json;
using ModelContextProtocol;
using vsmpc::VsMcp;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// <see cref="DebuggerFacade"/> 产生的 record 的 DTO 形态测试。旧版本
    /// 断言已删除的手工 JSON 字符串构建器（F-6 移除了两者）；此重写断言
    /// facade 现在每个 DTO 返回的解析后 JSON 结构，即 F-14 修复（不对
    /// 手工 JSON 做子串匹配）。
    ///
    /// facade 本身需要具体的 AsyncPackage（VS SDK 运行时），在 plan 05-04
    /// VS 实验 hive 冒烟测试中演练；这些无 COM 测试只覆盖 facade 发出的
    /// DTO 形态 —— 同样的表面，无 VS 依赖。
    /// </summary>
    public class DebuggerFacadeTests
    {
        // --- state 字段（D-11 / F-15 合并） ---

        [Fact]
        public void DebuggerStateResult_ProducesStateKeyInCamelCase()
        {
            // Facade 为 get_debugger_state 返回 DebuggerStateResult。
            // 序列化后的 JSON 必须带规范化的 "state" 键
            // （而非 "State"、"status" 或 "debugger_state"）。
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
            // continue_execution / step_* / stop_debugging 返回 ExecutionResult。
            // D-11：字段是 "state"，消除此前的 status/state 分裂。
            var dto = new ExecutionResult("running", "Execution continued");
            var json = JsonSerializer.Serialize(dto, McpJsonUtilities.DefaultOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("Execution continued", doc.RootElement.GetProperty("message").GetString());
            Assert.False(doc.RootElement.TryGetProperty("status", out _));
        }

        // --- 断点 DTO ---

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

        // --- 调用栈 DTO ---

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

        // --- 局部变量 / 表达式 DTO ---

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

        // --- session 信息 DTO（get_session_info） ---

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
            // Null 引用字段在 Web 默认序列化下可能被省略或为 null —— 两者都
            // 合法；唯一的硬断言是空 projects 数组仍以数组形式存活且不抛异常。
            Assert.Equal("design", root.GetProperty("state").GetString());
            Assert.Equal(0, root.GetProperty("projects").GetArrayLength());
        }

        // --- NormalizeFilePath（从 SetBreakpointAsync 提取的纯辅助方法） ---

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
