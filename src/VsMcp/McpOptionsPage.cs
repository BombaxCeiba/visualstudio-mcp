using Microsoft.VisualStudio.Shell;

namespace VsMcp
{
    /// <summary>
    /// VS MCP 扩展的 Tools → Options 页。控制那些有副作用、
    /// 用户可能想禁用的 opt-in 工具。
    /// 设置在 MCP server 启动时读取一次（在
    /// <see cref="VsMcpPackage.InitializeAsync"/> 中）；改完要重启 VS 才对
    /// 已注册工具集生效。
    /// </summary>
    public class McpOptionsPage : DialogPage
    {
        /// <summary>
        /// 启用 <c>go_to_definition</c> 工具。默认 false：该工具经
        /// <c>Edit.GoToDefinition</c> 驱动 VS，会移动编辑器光标并可能打开
        /// 文件——仅在该副作用可接受时 opt in。为 false 时该工具不注册，
        /// 对 agent 不可见。
        /// </summary>
        public bool EnableGoToDefinition { get; set; } = false;

#if EVAL_CSHARP
        /// <summary>
        /// 启用 <c>eval_csharp</c> 工具。默认 true：该工具在 VS 进程内用
        /// Roslyn CSharpCompilation 动态执行任意 C#，是强力的实时调试手段（探查 VS
        /// 内部状态、反射读非 public 字段，无需重编重装扩展）。等价于任意代码
        /// 执行——默认开是为了装上即用、省去手动开启+重启；仅在自己受控的开发机
        /// 上可接受。若要关掉，取消勾选并重启 VS（设置只在启动时读）。
        /// 注意：发布构建（<c>EvalCsharpEnabled=false</c>）整个 eval_csharp 会被
        /// 编译移除，届时此开关不存在。
        /// </summary>
        public bool EnableEvalCsharp { get; set; } = true;
#endif

        /// <summary>关闭 solution / 退出 VS 时，若有 MCP 客户端正在使用本 VS，弹窗确认。
        /// 默认 true：防止误关 VS 打断 MCP 会话。读不到/异常时默认启用（保守弹窗）。
        /// 设置只在 VS 启动时读一次（GetDialogPage 在 OnQueryCloseSolution 里即时读，
        /// 改完立即生效，无需重启）。</summary>
        public bool ConfirmCloseWithConnections { get; set; } = true;
    }
}
