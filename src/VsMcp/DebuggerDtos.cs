using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// net48 polyfill: the C# compiler needs System.Runtime.CompilerServices.IsExternalInit
// to synthesize record init-only property setters. .NET Framework 4.8 does not ship it;
// define it here so `record` types compile without adding a runtime package.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace VsMcp
{
    // ===========================================================================
    // F-6 typed result DTOs. All serialize via McpJsonUtilities.DefaultOptions
    // (camelCase web defaults) so the LLM sees stable, schema-friendly JSON.
    // The previous hand-built JSON layer (EscapeJson/SerializeError/etc.) is
    // deleted in DebuggerFacade.cs — these records replace it wholesale.
    // ===========================================================================

    /// <summary>Result of <c>get_debugger_state</c>.</summary>
    public sealed record DebuggerStateResult(string State);

    /// <summary>Result of <c>set_breakpoint</c>.</summary>
    public sealed record BreakpointSetResult(string File, int Line, bool Enabled, string Condition, string Name);

    /// <summary>A single breakpoint in <c>list_breakpoints</c>.</summary>
    public sealed record BreakpointInfo(string File, int Line, int Column, bool Enabled, string Condition, string Name);

    /// <summary>Result of <c>list_breakpoints</c>.</summary>
    public sealed record BreakpointListResult(IReadOnlyList<BreakpointInfo> Breakpoints, int Total);

    /// <summary>Result of <c>delete_breakpoint</c>.</summary>
    public sealed record BreakpointDeleteResult(bool Deleted, string File, int Line);

    /// <summary>
    /// Result of <c>clear_all_breakpoints</c>. Default (confirm=false) is a
    /// dry run: Deleted=0, WasDryRun=true, WouldDelete=N. Pass confirm=true
    /// to actually delete.
    /// </summary>
    public sealed record BreakpointClearResult(int Deleted, bool WasDryRun, int WouldDelete, string Message);

    /// <summary>Result of <c>build_solution</c>.</summary>
    public sealed record BuildSolutionResult(string Configuration, int FailedProjects, bool Succeeded);

    /// <summary>
    /// Result of <c>continue_execution</c>, <c>step_into/over/out</c>,
    /// and <c>stop_debugging</c>. D-11 / F-15: the field is <c>State</c>
    /// (serializes to <c>state</c>) — NEVER <c>Status</c>. This collapses
    /// the prior state/status/debugger_state three-key inconsistency.
    /// </summary>
    public sealed record ExecutionResult(string State, string Message);

    /// <summary>A single frame in <c>get_call_stack</c>.</summary>
    public sealed record StackFrameInfo(int FrameIndex, string FunctionName, string Module, string ReturnType);

    /// <summary>Result of <c>get_call_stack</c>.</summary>
    public sealed record CallStackResult(IReadOnlyList<StackFrameInfo> Frames, int Total, int Returned);

    /// <summary>
    /// A single evaluated expression or local variable. Recursively
    /// expandable via <see cref="Children"/> (drilled by
    /// <c>get_variable_detail</c>). <see cref="Truncated"/> + <see cref="Hint"/>
    /// guide the LLM to drill when the char budget is hit.
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

    /// <summary>Result of <c>get_local_variables</c>.</summary>
    public sealed record LocalsResult(IReadOnlyList<ExpressionInfo> Locals, int Total, int Returned, bool Truncated);

    /// <summary>
    /// Result of <c>evaluate_expression</c> and <c>get_variable_detail</c>.
    /// Wrapper so both tools share a stable top-level shape carrying a single
    /// <see cref="Expression"/>.
    /// </summary>
    public sealed record ExpressionResult(ExpressionInfo Expression);

    /// <summary>
    /// Result of <c>get_session_info</c>. Surfaces the solution VS has loaded
    /// (so the agent knows which project tree it is operating on), the
    /// projects inside it, and a best-effort debug target. Every field except
    /// <see cref="State"/> is nullable / may be empty when no solution is
    /// loaded or when the debug target cannot be resolved.
    /// </summary>
    public sealed record SessionInfoResult(
        string? SolutionPath,
        string? SolutionDir,
        IReadOnlyList<string> Projects,
        string? DebugTarget,
        string State);

    // ===========================================================================
    // search_project / start_debugging tool DTOs. These two tools form a pair:
    // search_project enumerates the loaded solution so the agent can locate the
    // project that owns the executable it wants to debug (and read back the
    // best-effort OutputTarget), then start_debugging launches that exe through
    // VS's native IVsDebugger2.LaunchDebugTargets2 path (no project config or
    // launchSettings.json touched).
    // ===========================================================================

    /// <summary>
    /// A single project surfaced by <c>search_project</c>. OutputTarget is a
    /// best-effort resolved path to the project's built executable (for C#
    /// projects, derived from ConfigurationManager + OutputFileName); null when
    /// the project type does not expose those properties (C++/vcxproj, solution
    /// folders, misc) — start_debugging takes an absolute exe path directly so
    /// a null here is non-fatal, just informational.
    /// </summary>
    public sealed record ProjectInfo(
        string Name,
        string UniqueName,
        string? FullName,
        string? Kind,
        bool IsStartupProject,
        string? OutputTarget);

    /// <summary>Result of <c>search_project</c>.</summary>
    public sealed record ProjectSearchResult(IReadOnlyList<ProjectInfo> Projects, int Total);

    /// <summary>
    /// Result of <c>start_debugging</c>. Started is true only when
    /// LaunchDebugTargets2 returned a success HRESULT. State is the debugger
    /// mode observed right after the launch call (typically "running" or
    /// "break" if a startup breakpoint was hit; "design" if the launch did not
    /// actually engage the debugger). EnvironmentVariables echoes the effective
    /// env that was passed to CreateProcess (the parsed dictionary when the
    /// JSON-string form was used, or the original map). Warnings carries
    /// non-fatal advisories (e.g. default engine fallback, env-block notes).
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
    // D-12 error contract. A single ErrorResult type carries the structured
    // error (classification, human message, optional debugger state) and
    // serializes itself into a TextContentBlock on a CallToolResult flagged
    // IsError=true — the MCP-spec-correct way to signal a tool failure the
    // LLM can recover from (vs the old 200-OK-with-error-JSON).
    // ===========================================================================

    /// <summary>
    /// Structured tool error. <see cref="State"/> is populated only when the
    /// error is state-related (D-11 normalization); it is null otherwise and
    /// may be omitted or null in the serialized JSON (Web-default behavior).
    /// <see cref="File"/> / <see cref="LoadedSolution"/> are populated only by
    /// <c>file_not_in_solution</c> so the agent receives the actionable
    /// context (which file it asked for vs. which solution is loaded).
    /// </summary>
    public sealed record ErrorResult(
        string Error,
        string Message,
        string? State = null,
        string? File = null,
        string? LoadedSolution = null)
    {
        /// <summary>
        /// Builds an MCP-spec <see cref="CallToolResult"/> with
        /// <see cref="CallToolResult.IsError"/> = true and a single
        /// <see cref="TextContentBlock"/> whose text is this ErrorResult
        /// serialized via <see cref="McpJsonUtilities.DefaultOptions"/>.
        /// </summary>
        public CallToolResult ToCallToolResult() => new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(this, McpJsonUtilities.DefaultOptions)
                }
            },
        };
    }

    // ===========================================================================
    // Typed exceptions. The facade throws these instead of building
    /// SerializeError strings; SafeCall converts them to ErrorResult.
    // ===========================================================================

    /// <summary>
    /// Thrown when a tool requires the debugger to be in break mode but it
    /// is in design/run mode. Carries the current state string so SafeCall
    /// can populate <see cref="ErrorResult.State"/>.
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
    /// Thrown when <c>delete_breakpoint</c> (or similar) cannot locate the
    /// referenced breakpoint.
    /// </summary>
    public sealed class BreakpointNotFoundException : Exception
    {
        public BreakpointNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown by <c>set_breakpoint</c> when a solution IS loaded in VS but the
    /// requested file is not a member of it. Carries the requested file path
    /// and the loaded solution path so SafeCall can populate
    /// <see cref="ErrorResult.File"/> + <see cref="ErrorResult.LoadedSolution"/>
    /// for actionable agent context.
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
    // SafeCall — the single error-routing boundary. Every facade-calling
    // tool lambda in McpHttpServer.BuildToolCollection is wrapped here so a
    // facade exception becomes a CallToolResult{IsError=true}+ErrorResult.
    // OperationCanceledException is rethrown unchanged: it signals shutdown
    // and must NEVER be swallowed into an error response.
    // ===========================================================================

    /// <summary>
    /// Routes facade exceptions into the D-12 structured-error contract.
    /// The widened <c>Task&lt;object&gt;</c> return type satisfies the MCP
    /// SDK's tool-invocation contract: the SDK serializes a plain DTO via
    /// McpJsonUtilities, but returns a CallToolResult verbatim (REMEDIATION
    /// fact #7), so both success DTOs and ErrorResult.ToCallToolResult()
    /// results flow through one delegate type.
    /// </summary>
    public static class SafeCall
    {
#pragma warning disable VSTHRD200 // "Wrap" is the established name across REMEDIATION/PATTERNS/plan; callers hand the resulting Task to the MCP SDK and never await it directly, so the "Async" suffix would mislead. Mirrors the sanctioned VSTHRD200 suppression on McpHttpServer.DisposeAsyncFireAndForget.
        public static async Task<object> Wrap<T>(Func<Task<T>> work, CancellationToken ct)
#pragma warning restore VSTHRD200
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown signal — transparent rethrow. Must NOT be
                // converted to an ErrorResult (would mask cancellation
                // as a tool failure and confuse the SDK teardown path).
                throw;
            }
            catch (RequireBreakModeException ex)
            {
                return new ErrorResult("not_in_break_mode", ex.Message, ex.State).ToCallToolResult();
            }
            catch (BreakpointNotFoundException ex)
            {
                return new ErrorResult("breakpoint_not_found", ex.Message).ToCallToolResult();
            }
            catch (FileNotInSolutionException ex)
            {
                // file_not_in_solution carries both the requested file and the
                // loaded solution so the agent can reconcile which project tree
                // it should be referencing instead of silently no-op'ing.
                return new ErrorResult(
                    "file_not_in_solution",
                    ex.Message,
                    File: ex.File,
                    LoadedSolution: ex.LoadedSolution).ToCallToolResult();
            }
            catch (Exception ex)
            {
                // T-05-03-01 mitigation: only ex.Message crosses to the LLM,
                // never the StackTrace or internal type names beyond the
                // fixed classification label.
                return new ErrorResult("internal_error", ex.Message).ToCallToolResult();
            }
        }
    }
}
