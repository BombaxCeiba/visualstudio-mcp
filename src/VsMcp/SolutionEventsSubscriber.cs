using System;
using System.IO;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsMcp
{
    /// <summary>
    /// 订阅 <see cref="IVsSolutionEvents"/> 并把打开/关闭转换以
    /// <c>solution-changed</c> pipe 推送转发给 Gateway（设计文档
    /// §Solution 信息动态更新）。若没有它，Gateway 的路由表会永远保留注册时
    /// 捕获的解决方案路径，故一旦用户在 VS 启动后打开/关闭解决方案，① Header
    /// 层与 list_vs_instances 就会变陈旧。
    ///
    /// 订阅使用 COM 连接点模式
    /// （<see cref="IConnectionPointContainer"/>/<see cref="IConnectionPoint"/>）
    /// 而非 SDK 的 <c>SolutionEvents</c> 助手：IVsSolutionEvents 对 .sln 与
    /// folder/CMake 打开都能可靠触发，而 EnvDTE 的
    /// <c>DTE.Events.SolutionEvents</c> 对 folder 项目不稳定（核心 C++ 用例——
    /// 见 package 的 NoSolution autoload 缘由）。
    ///
    /// COM 回调抵达 UI 线程。我们内联读 <c>DTE.Solution.FullName</c>（在 UI
    /// 线程上安全，无线程跳转——避免重入
    /// <see cref="DebuggerFacade.GetSessionInfoAsync"/>，后者本身也会切到 UI
    /// 线程），然后把解析到的路径交给一个 fire-and-forget 任务去写 pipe 帧。
    /// pipe 写入吞掉自己的错误，故瞬态 Gateway 断开绝不会崩 UI。
    /// </summary>
    internal sealed class SolutionEventsSubscriber : IVsSolutionEvents, IVsSolutionEvents7, IDisposable
    {
        private readonly AsyncPackage _package;
        private readonly PipeMcpServer _pipe;
        private readonly CancellationToken _callerToken;
        private readonly ILogger? _logger;

        private DTE2? _dte;
        private IVsSolution? _solution;
        // IVsSolution.AdviseSolutionEvents 返回一个 uint cookie（VSCOOKIE）；0 表示
        // "未订阅"。之前的 IConnectionPointContainer 路径静默失败，因为
        // IVsSolution RCW 在 net48 下不 QI 到 IConnectionPointContainer，
        // 故 Advise 实际从未运行——OnAfterOpenSolution 从未触发。
        private uint _cookie;

        public SolutionEventsSubscriber(AsyncPackage package, PipeMcpServer pipe, CancellationToken callerToken, ILogger? logger)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _callerToken = callerToken;
            _logger = logger;
        }

        /// <summary>
        /// 在 UI 线程上获取 <see cref="IVsSolution"/> + <see cref="DTE2"/> 并
        /// 注册 solution-events 连接点。幂等且尽力而为：若服务不可用，订阅者
        /// 保持惰性（register 帧 + 重连仍能让 Gateway 大致正确；此处只是让它
        /// 变实时）。
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct)
        {
            // SwitchToMainThreadAsync 返回的 MainThreadAwaitable 没有
            // ConfigureAwait——直接 await（DebuggerFacade 用同样形式）。
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _logger?.LogInformation("SolutionEventsSubscriber.InitializeAsync: on UI thread, acquiring DTE + IVsSolution");

            try
            {
                _dte = (DTE2?)await _package.GetServiceAsync(typeof(DTE)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogInformation("InitializeAsync: DTE acquire threw: {Message}", ex.Message);
            }

            try
            {
                _solution = (IVsSolution?)await _package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogInformation("InitializeAsync: IVsSolution acquire threw: {Message}", ex.Message);
            }

            _logger?.LogInformation(
                "InitializeAsync: dte_acquired={Dte} solution_acquired={Sol}",
                _dte != null, _solution != null);
            if (_solution == null)
            {
                _logger?.LogInformation(
                    "InitializeAsync: IVsSolution is null — subscription aborted, OnAfterOpenSolution will NOT fire");
                return;
            }

            // 经 IVsSolution.AdviseSolutionEvents（接口自身的方法）订阅——
            // 而非 IConnectionPointContainer。IVsSolution RCW 在 net48 下不 QI 到
            // IConnectionPointContainer，故 COM 连接点方式静默地从不订阅。
            // AdviseSolutionEvents 是受支持的路径，对 .sln / .slnx / Open Folder
            // 都会触发。
            try
            {
                int hr = _solution.AdviseSolutionEvents(this, out _cookie);
                if (hr == VSConstants.S_OK && _cookie != 0)
                    _logger?.LogInformation(
                        "InitializeAsync: AdviseSolutionEvents succeeded, cookie={Cookie} — OnAfterOpenSolution/Close WILL fire",
                        _cookie);
                else
                    _logger?.LogInformation(
                        "InitializeAsync: AdviseSolutionEvents failed hr=0x{Hr:X8} cookie={Cookie}", hr, _cookie);
            }
            catch (Exception ex)
            {
                _logger?.LogInformation("InitializeAsync: AdviseSolutionEvents threw: {Message}", ex.Message);
            }
        }

        // ───────────────────────── IVsSolutionEvents ──────────────────────────
        //
        // 仅打开/关闭解决方案这两个转换触发推送。其余每个回调都返回 S_OK，
        // 让 VS 按其默认行为继续。

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            _logger?.LogInformation("OnAfterOpenSolution triggered — pushing current solution");
            PushCurrentSolution();
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
        {
            _logger?.LogInformation("OnAfterCloseSolution triggered — pushing null solution");
            // 解决方案已没了 → 推送 null，让 Gateway 渲染"(no solution)"。
            PushSolutionValues(solutionPath: null, solutionDir: null, solutionName: null);
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pHierarchy, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            try
            {
                // 设置开关（默认启用；读不到也默认启用，保守弹窗）
                bool enabled = true;
                try { enabled = ((McpOptionsPage)_package.GetDialogPage(typeof(McpOptionsPage))).ConfirmCloseWithConnections; }
                catch { /* 读设置失败 → 默认启用 */ }
                if (!enabled) return VSConstants.S_OK;

                int count = _pipe.ConnectionCount;
                if (count <= 0) return VSConstants.S_OK;

                // 有 MCP 客户端在用——弹窗确认。OnQueryCloseSolution 在 UI 线程，可直接弹。
                if (!ConfirmClose(count))
                    pfCancel = 1;  // 用户选"否" → 阻止关闭 solution / 退出 VS
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "OnQueryCloseSolution confirm failed"); }
            return VSConstants.S_OK;
        }

        /// <summary>弹 VS 原生消息框问用户是否仍要关闭（OnQueryCloseSolution 在 UI 线程）。
        /// 返回 true=继续关闭，false=取消。用 IVsUIShell.ShowMessageBox 避免引入 WPF 引用。</summary>
        private bool ConfirmClose(int count)
        {
            var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
            if (uiShell == null) return true;  // 拿不到 shell → 不阻拦（别因弹窗失败卡住关闭）
            const int IDYES = 6;
            uiShell.ShowMessageBox(0, Guid.Empty, "VS MCP",
                $"检测到 {count} 个 MCP 客户端正在使用本 VS。确定要关闭吗？",
                null, 0,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND,
                OLEMSGICON.OLEMSGICON_WARNING, 0, out int result);
            return result == IDYES;
        }
        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        // ───────────────────────── IVsSolutionEvents7 (folder mode) ──────────────
        // Open Folder（CMake / 轻量打开）不触发
        // IVsSolutionEvents.OnAfterOpenSolution——它用 IVsSolutionEvents7
        // （VS 2017+）。同一个 AdviseSolutionEvents 订阅会送达这些回调：
        // VS 把 sink RCW QI 到 IVsSolutionEvents7 并为 folder 模式加载调用
        // folder 方法。folderPath 是打开的文件夹；把它同时作为解决方案路径
        // 与其目录（没有 .sln）。
        void IVsSolutionEvents7.OnAfterOpenFolder(string folderPath)
        {
            _logger?.LogInformation("IVsSolutionEvents7.OnAfterOpenFolder: {Folder}", folderPath ?? "(null)");
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                string name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                PushSolutionValues(folderPath, folderPath, name);
            }
        }

        void IVsSolutionEvents7.OnBeforeCloseFolder(string folderPath) { }

        void IVsSolutionEvents7.OnQueryCloseFolder(string folderPath, ref int pfCancel) { pfCancel = 0; }

        void IVsSolutionEvents7.OnAfterCloseFolder(string folderPath)
        {
            _logger?.LogInformation("IVsSolutionEvents7.OnAfterCloseFolder: {Folder}", folderPath ?? "(null)");
            PushSolutionValues(solutionPath: null, solutionDir: null, solutionName: null);
        }

        void IVsSolutionEvents7.OnAfterLoadAllDeferredProjects() { }

        /// <summary>
        /// 在 UI 线程上重新读取当前解决方案并推送给 Gateway。由 PipeMcpServer 在
        /// 每次成功 register 后调用，以弥合"连接前已打开"的时序缺口
        /// （OnAfterOpenSolution 在 pipe 为 null 时触发，那次推送被丢弃），并在
        /// Gateway 重连后重新同步。自行跳到 UI 线程，故调用方（一个后台
        /// connect-loop 任务）不必在 UI 线程上。
        /// </summary>
        public async Task PushCurrentSolutionAsync(CancellationToken ct)
        {
            try
            {
                // SwitchToMainThreadAsync 返回的 MainThreadAwaitable 没有
                // ConfigureAwait——直接 await（与 InitializeAsync 同形式）。
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                PushCurrentSolution();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* 关停 */ }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "PushCurrentSolutionAsync failed");
            }
        }

        /// <summary>
        /// 内联读取当前解决方案路径（我们在 UI 线程上——COM 事件把我们送到
        /// 这里）并交给 fire-and-forget 推送。这避免了为读路径做任何线程跳转：
        /// 从回调重入 <see cref="DebuggerFacade.GetSessionInfoAsync"/> 会切到
        /// 我们已在的 UI 线程，并做我们不需要的额外工作（项目枚举）。
        /// </summary>
        private void PushCurrentSolution()
        {
            // IVsSolution 对 .sln/.slnx/Open Folder 是权威；DTE.Solution.FullName
            // 对 .slnx + folder 为空（已知的自动化模型缺口）。
            var (path, dir) = DebuggerFacade.ReadSolutionPaths(_solution, _dte);
            string? name = null;
            if (path != null)
            {
                try { name = Path.GetFileName(path); } catch { }
            }
            _logger?.LogInformation(
                "PushCurrentSolution: path={Path} dir={Dir} name={Name}",
                path ?? "(null)", dir ?? "(null)", name ?? "(null)");
            PushSolutionValues(path, dir, name);
        }

        /// <summary>
        /// fire-and-forget 地推送 pipe。COM 回调必须及时返回，故实际的帧写入
        /// 在后台任务上运行。SendSolutionChanged 吞掉 IO 错误；漏掉的推送会在
        /// 下次重连时经 register 帧自愈。
        /// </summary>
        private void PushSolutionValues(string? solutionPath, string? solutionDir, string? solutionName)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pipe.SendSolutionChangedAsync(solutionPath, solutionDir, solutionName, _callerToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "SendSolutionChangedAsync threw (best-effort; ignored)");
                }
            });
        }

        public void Dispose()
        {
            if (_solution != null && _cookie != 0)
            {
                try { _solution.UnadviseSolutionEvents(_cookie); } catch { /* 尽力而为 */ }
            }
            _cookie = 0;
        }
    }
}
