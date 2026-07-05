using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsMcp.Common;
using VsMcp.Logging;
using Task = System.Threading.Tasks.Task;

namespace VsMcp
{
    /// <summary>
    /// VSIX package 入口。在多实例架构下，本 VS 实例不再绑定 HTTP 端口 —— 它打开
    /// 一个 NamedPipe（<c>\\.\pipe\vs-mcp-{PID}</c>），把工具面交给独占 :43210 的
    /// 独立 VsMcpGateway.exe，由后者经 pipe 把 MCP 客户端路由到正确实例。同一条
    /// pipe 路径背后是同一个 <see cref="McpRequestProcessor"/>，因此工具集、错误语义
    /// 与保活行为都与早先的直连 HTTP 路径完全一致。
    ///
    /// 通过 UIContextGuids80.NoSolution 自动加载（只要没有加载 .sln 即激活 —— 即启动
    /// 时以及“打开文件夹”/CMake 项目打开时）。我们之前试过 SolutionExists，但通过
    /// File &gt; Open &gt; CMake 打开的 CMake 项目并不加载 .sln，因此 SolutionExists 永不
    /// 触发，C++ 工作流（核心用例）下服务器永远起不来。NoSolution 覆盖 CMake/文件夹
    /// 项目；代价是服务器在 VS 裸启动时也会启动（WakaTime 式），这可以接受，因为
    /// C++ 调试器控制需要在没有 .sln 的情况下让服务器就绪。VS package 加载一次且不会
    /// 卸载，所以它始终保持可用。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(McpOptionsPage), "VS MCP", "Tools", 1000, 1001, true)]
    [Guid("a3f7c5e1-8b2d-4f6a-9c0e-1d3b5a7f2e4d")]
    public sealed class VsMcpPackage : AsyncPackage, IVsPackage
    {
        private PipeMcpServer? _pipeServer;
        private HeartbeatClient? _heartbeat;
        private DebuggerFacade? _facade;
        private SymbolFacade? _symbolFacade;
#if EVAL_CSHARP
        private EvalCsharpFacade? _evalFacade;
#endif
        private SolutionEventsSubscriber? _solutionSubscriber;
        private DebuggerEventsSubscriber? _debuggerSubscriber;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // 一次性构建 ILoggerFactory。D-04：该 provider 写入 VS 输出窗口的
            // 'VS MCP' 窗格（Information+，D-05/D-07 为 Phase 5 固定了 Information），
            // 并额外写入 IVsActivityLog（Error/Critical，D-06）。
            //
            // 使用本地 SimpleLoggerFactory 而非 Microsoft.Extensions.Logging.LoggerFactory：
            // 具体的 LoggerFactory 位于本项目未引用的 NuGet 包中，而本扩展只接一个
            // provider，因此一个最小化 factory 就能覆盖契约，无需新增依赖。
            var loggerFactory = new SimpleLoggerFactory(
                new VsOutputWindowLoggerProvider(this),
                LogLevel.Information);

            _facade = new DebuggerFacade(this);
            _symbolFacade = new SymbolFacade(this);
#if EVAL_CSHARP
            _evalFacade = new EvalCsharpFacade(this, loggerFactory.CreateLogger<EvalCsharpFacade>());
#endif

            // 从 Tools → Options 读取 opt-in 工具设置。GetDialogPage 需要 UI 线程
            // （在上面的切换之后我们已在 UI 线程上）。设置只在启动时读一次；改动后
            // 需重启 VS 才生效。
            bool enableGoToDefinition = false;
            try { enableGoToDefinition = ((McpOptionsPage)GetDialogPage(typeof(McpOptionsPage))).EnableGoToDefinition; } catch { }

#if EVAL_CSHARP
            bool enableEvalCsharp = false;
            try { enableEvalCsharp = ((McpOptionsPage)GetDialogPage(typeof(McpOptionsPage))).EnableEvalCsharp; } catch { }
#endif

            // 打开到 Gateway 的每实例连接。PipeMcpServer 现在（Wave 2）以客户端身份
            // 拨入 Gateway 的 "vs-mcp-gateway" pipe 并发送一帧 register；Gateway 把
            // MCP 客户端多路复用到它上面。PipeMcpServer.StartAsync 在连接循环就绪后
            // 即返回（循环在后台运行），所以这次 await 不会让 InitializeAsync 阻塞
            // 超过 VS package 加载超时。
            int pid = Process.GetCurrentProcess().Id;
            _pipeServer = new PipeMcpServer(
                pid, _facade, _symbolFacade,
#if EVAL_CSHARP
                _evalFacade,
#endif
                enableGoToDefinition,
#if EVAL_CSHARP
                enableEvalCsharp,
#endif
                loggerFactory, this.DisposalToken);

            // 尽力而为：在 pipe 客户端启动前确保独立 Gateway exe 已起来，使首次连接
            // 尝试能命中。fire-and-forget —— 若 Gateway 无法启动（exe 还未随 Wave 5
            // 打入 VSIX 上盘），pipe 客户端会持续重试连接。
            var pkgLogger = loggerFactory.CreateLogger<VsMcpPackage>();
            // 第一行打版本号，方便排查日志对应哪个版本。
            pkgLogger.LogInformation($"VS MCP 扩展 v{ExtensionVersion.Current} 已加载");
            _ = Task.Run(async () =>
            {
                try
                {
                    await GatewayLauncher.EnsureGatewayRunningAsync(this.DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pkgLogger.LogError(ex, "Gateway launch/probe failed: {Message}", ex.Message);
                }
            });

            // 构造 solution-events 订阅者，并在启动 pipe server 之前接好它的
            // onConnected 回调。首帧 register 会触发 onConnected，重新推送当前
            // solution —— 既填补 open-before-connect 竞态（OnAfterOpenSolution 在
            // pipe 为 null 时触发而被丢弃），也在 Gateway 重连后重新同步。
            // IVsSolution 的 Advise 本身在下面 StartAsync 之后 fire-and-forget 执行。
            _solutionSubscriber = new SolutionEventsSubscriber(
                this, _pipeServer, this.DisposalToken, loggerFactory.CreateLogger<SolutionEventsSubscriber>());
            _debuggerSubscriber = new DebuggerEventsSubscriber(
                this, _facade, _pipeServer, this.DisposalToken, loggerFactory.CreateLogger<DebuggerEventsSubscriber>());
            // register 成功后同时重推 solution + debugger state，覆盖"连接前已发生的状态
            // 变化"竞态，并在 Gateway 重连后重新同步（两者都自行切 UI 线程）。
            _pipeServer.SetOnConnected(async token =>
            {
                await _solutionSubscriber.PushCurrentSolutionAsync(token);
                await _debuggerSubscriber.PushCurrentStateAsync(token);
            });

            try
            {
                await _pipeServer.StartAsync(this.DisposalToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pkgLogger.LogError(ex, "MCP pipe server failed to start: {Message}", ex.Message);
            }

            // 心跳：通过 pipe 周期性写入一帧 heartbeat 来探测 Gateway 的存活。持续的
            // miss 窗口（Gateway 崩溃 / 被 taskkill）会触发抢占式重新拉起，让 VS 无需
            // 用户介入即可自愈（设计文档 §抢占式 Gateway 拉起）。该循环在构造函数里
            // 启动、在 Dispose 时停止；它从不阻塞 VS 退出（HeartbeatClient.Dispose 是
            // 同步且非阻塞的，克隆自 KeepAliveNotifier 的停机模式）。
            _heartbeat = new HeartbeatClient(
                pid,
                sendHeartbeat: ct => _pipeServer.SendHeartbeatAsync(ct),
                ensureGatewayRunning: ct => GatewayLauncher.EnsureGatewayRunningAsync(ct),
                callerToken: this.DisposalToken,
                logger: loggerFactory.CreateLogger<HeartbeatClient>());

            // 订阅 solution 的打开/关闭，使 Gateway 的路由表（① Header 层 +
            // list_vs_instances 用的 SolutionDir）在 VS 启动后用户打开/关闭解决方案时
            // 保持最新。在 UI 线程运行（需要 IVsSolution + DTE 服务）；fire-and-forget，
            // 这样一次缓慢的 service 查询绝不会阻塞 package 加载。
            _ = Task.Run(async () =>
            {
                try
                {
                    await _solutionSubscriber.InitializeAsync(this.DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pkgLogger.LogDebug(ex, "Solution-events subscription failed; solution-changed push disabled");
                }
            });

            // 同样 fire-and-forget 订阅 DebuggerEvents：订阅成功后每次模式切换都向
            // Gateway 推送 debugger-state-changed。失败则降级为"仅 OnConnected 初值"，
            // 不影响其余功能。
            _ = Task.Run(async () =>
            {
                try
                {
                    await _debuggerSubscriber.InitializeAsync(this.DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pkgLogger.LogDebug(ex, "Debugger-events subscription failed; debugger-state-changed push disabled");
                }
            });
        }

        /// <summary>
        /// F-3 A+B 混合停机的 Path B（RESEARCH Open Question #1）。尽力而为、非阻塞。
        /// PipeMcpServer.Dispose() 是同步且非阻塞的（取消 accept 循环并对处理器的异步
        /// 停机 fire-and-forget），所以在这里调用它不会死锁 VS 退出。幂等
        /// （PipeMcpServer 守护了自身的 _disposed 标志）。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _solutionSubscriber?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
                try { _debuggerSubscriber?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
                try { _heartbeat?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
                try { _pipeServer?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
                _facade?.Dispose();
                _symbolFacade?.Dispose();
#if EVAL_CSHARP
                _evalFacade?.Dispose();
#endif
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// F-3 A+B 混合停机的 Path A（RESEARCH Pitfall #1 + Open Question #1）。
        /// IVsPackage.Close() 的显式接口实现 —— VS 在退出时调用它。与 Dispose(bool)
        /// 相同的幂等非阻塞停机。
        /// </summary>
        int Microsoft.VisualStudio.Shell.Interop.IVsPackage.Close()
        {
            try { _solutionSubscriber?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
            try { _heartbeat?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
            try { _pipeServer?.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
            _facade?.Dispose();
            _symbolFacade?.Dispose();
#if EVAL_CSHARP
            _evalFacade?.Dispose();
#endif
            return VSConstants.S_OK;
        }
    }
}
