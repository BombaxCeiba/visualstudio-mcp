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
    /// Unit tests for the SafeCall.Wrap error-routing contract.
    /// SafeCall is the single boundary that converts typed facade
    /// exceptions into MCP-spec CallToolResult{IsError=true}+ErrorResult
    /// responses, and transparently rethrows OperationCanceledException
    /// (shutdown must not be swallowed into an error response).
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

        // --- FileNotInSolutionException -> file_not_in_solution (+ context fields) ---

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

        // --- Generic Exception -> internal_error ---

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

        // --- OperationCanceledException is rethrown (shutdown transparency) ---

        [Fact]
        public async Task Wrap_OperationCanceledException_IsRethrownNotSwallowed()
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SafeCall.Wrap<object>(() => throw new OperationCanceledException(),
                              CancellationToken.None));
        }

        // --- Success path returns the DTO value unchanged ---

        [Fact]
        public async Task Wrap_Success_ReturnsFacadeValueAsReadableJson()
        {
            // The success path wraps the DTO via McpJson.ToTextResult so the LLM
            // sees readable (unescaped) JSON rather than the SDK's Chinese-
            // escaping default. The return is a CallToolResult whose text content
            // carries the serialized DTO; the state value must round-trip.
            object result = await SafeCall.Wrap(
                () => Task.FromResult<object>(new DebuggerStateResult("break")),
                CancellationToken.None);

            var ctr = Assert.IsType<CallToolResult>(result);
            // Success path leaves IsError unset (null) — distinct from the error
            // path's explicit true. `!= true` accepts both null and false.
            Assert.True(ctr.IsError != true);
            var block = Assert.IsType<TextContentBlock>(ctr.Content[0]);
            Assert.Contains("break", block.Text);
        }

        // --- helpers: parse the TextContentBlock JSON for an ErrorResult field ---

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
