using Microsoft.VisualStudio.Shell;

namespace VsMcp
{
    /// <summary>
    /// Tools → Options page for the Visual Studio MCP extension. Controls
    /// opt-in tools that have side effects the user may want to disable.
    /// Settings are read once at MCP server startup (in
    /// <see cref="VsMcpPackage.InitializeAsync"/>); changing them
    /// requires a VS restart to take effect on the registered tool set.
    /// </summary>
    public class McpOptionsPage : DialogPage
    {
        /// <summary>
        /// Enables the <c>go_to_definition</c> tool. Default false: the tool
        /// drives VS via <c>Edit.GoToDefinition</c> which moves the editor
        /// cursor and may open files — opt in only if that side effect is
        /// acceptable. When false the tool is not registered and stays
        /// invisible to the agent.
        /// </summary>
        public bool EnableGoToDefinition { get; set; } = false;
    }
}
