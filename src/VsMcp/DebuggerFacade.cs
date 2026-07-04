using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using ModelContextProtocol;

namespace VsMcp
{
    /// <summary>
    /// VS 调试器 COM 层的 Facade。全部 14 个公开方法返回
    /// 有类型的 record DTO（见 <see cref="DebuggerDtos"/>），并抛出有类型的
    /// 异常（<see cref="RequireBreakModeException"/> /
    /// <see cref="BreakpointNotFoundException"/>），而非拼装 JSON
    /// 字符串。手写的 JSON 层（原先位于本文件的四个辅助方法）
    /// 已移除；错误路由统一集中在 <see cref="SafeCall"/>。
    /// </summary>
    public sealed class DebuggerFacade : IDisposable
    {
        private const int MaxFrames = 50;
        private const int DefaultCharBudget = 1024;
        // list_local_variables 返回 local 数量的上限，超过则标 Truncated——
        // 防止上千 local 的栈帧即便浅层模式也撑爆上下文。
        private const int DefaultLocalCap = 200;

        private readonly AsyncPackage _package;
        private DTE2? _dte;
        private bool _disposed;

        public DebuggerFacade(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        private async Task<DTE2> GetDteAsync(CancellationToken ct)
        {
            if (_dte != null) return _dte;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _dte = (DTE2)await _package.GetServiceAsync(typeof(DTE));
            return _dte;
        }

        /// <summary>
        /// 读取当前解决方案的（路径、目录）。优先使用
        /// <see cref="IVsSolution"/>（更底层的 SDK 接口，支撑
        /// .sln、.slnx 以及 Open Folder / CMake），并以
        /// <c>DTE.Solution.FullName</c> 作为边缘情况的兜底。DTE 的自动化
        /// 模型对 <c>.slnx</c>（新的 XML 格式）和 Open Folder 返回空
        /// ——这是一个已知缺口——因此 IVsSolution 必须作为权威来源。对于 Open Folder，
        /// GetSolutionInfo 只返回目录而无解决方案文件；此时以目录
        /// 作为路径标识。
        /// </summary>
        internal static (string? Path, string? Dir) ReadSolutionPaths(IVsSolution? vsSolution, DTE2? dte)
        {
            if (vsSolution != null)
            {
                try
                {
                    vsSolution.GetSolutionInfo(out string sDir, out string sFile, out string _);
                    if (!string.IsNullOrWhiteSpace(sFile))
                        return (sFile, string.IsNullOrWhiteSpace(sDir) ? null : sDir);
                    if (!string.IsNullOrWhiteSpace(sDir))
                        return (sDir, sDir); // Open Folder：无 .sln，以文件夹作为标识
                }
                catch { /* 尽力而为，忽略错误 */ }
            }
            if (dte != null)
            {
                try
                {
                    string full = dte.Solution?.FullName ?? "";
                    if (!string.IsNullOrWhiteSpace(full))
                        return (full, Path.GetDirectoryName(full));
                }
                catch { /* 尽力而为，忽略错误 */ }
            }
            return (null, null);
        }

        /// <summary>
        /// 从 <c>SVsShellDebugger</c> 服务获取 <see cref="IVsDebugger2"/>。
        /// 优先选用 IVsDebugger2 而非 IVsDebugger（v1）：v1
        /// 接口的 OLE marshaler 在离开 STA 线程时不可用，会导致
        /// QI 以 E_NOINTERFACE 失败；而 IVsDebugger2（自
        /// VS 2005 起即存在）拥有健壮的封送，QI 可成功。每次调用都重新获取而非缓存：
        /// 切换调试会话会替换底层的 COM 对象。
        /// </summary>
        private async Task<IVsDebugger2> GetVsDebugger2Async(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            return (IVsDebugger2?)await _package.GetServiceAsync(typeof(SVsShellDebugger))
                ?? throw new InvalidOperationException("IVsDebugger2 service is not available");
        }

        public async Task<DebuggerStateResult> GetDebuggerStateAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var mode = await GetDebuggerModeAsync(ct);
            return new DebuggerStateResult(MapState(mode));
        }

        /// <summary>
        /// 暴露 VS 已加载的解决方案以及尽力而为的调试目标，
        /// 让 agent 在发起断点/检视调用之前知道它正在操作哪个项目树。
        /// 缺少解决方案或调试目标时绝不抛异常——而是返回 null / 空列表。
        /// </summary>
        public async Task<SessionInfoResult> GetSessionInfoAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            string state = await StateFromModeAsync(ct);

            // IVsSolution 是 .sln/.slnx/Open Folder 的权威来源；DTE.Solution.FullName
            // 对 .slnx + 文件夹为空（已知的自动化模型缺口）。
            var vsSolution = await _package.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            var (resolvedSolutionPath, solutionDir) = ReadSolutionPaths(vsSolution, dte);
            var projects = new List<string>();

            if (resolvedSolutionPath != null)
            {
                if (solutionDir == null)
                {
                    try { solutionDir = Path.GetDirectoryName(resolvedSolutionPath); }
                    catch { solutionDir = null; }
                }

                // dte.Solution.Projects 是一个 COM 集合；防范
                // null/空（未加载解决方案）而不抛异常。
                var dteProjects = dte.Solution?.Projects;
                if (dteProjects != null)
                {
                    for (int i = 1; i <= dteProjects.Count; i++)
                    {
                        try
                        {
                            var project = dteProjects.Item(i);
                            // 优先用 FullName（完整项目路径）；对于解决方案文件夹 /
                            // FullName 为空的虚拟项目，回退到
                            // Name（唯一项目名）。
                            string id = !string.IsNullOrWhiteSpace(project.FullName)
                                ? project.FullName
                                : (project.Name ?? "");
                            if (!string.IsNullOrEmpty(id))
                                projects.Add(id);
                        }
                        catch
                        {
                            // 单个无法加载的项目不得中止
                            // 整个枚举——跳过它。
                        }
                    }
                }
            }

            // 尽力而为的调试目标：CurrentProgram 仅在
            // 断点/运行模式下有意义；设计模式下为 null。任何 COM 抖动都被
            // 吞掉——该字段是信息性的，非关键路径。
            string? debugTarget = null;
            try
            {
                debugTarget = dte.Debugger?.CurrentProgram?.Name;
            }
            catch
            {
                debugTarget = null;
            }

            return new SessionInfoResult(
                SolutionPath: resolvedSolutionPath,
                SolutionDir: solutionDir,
                Projects: projects,
                DebugTarget: debugTarget,
                State: state);
        }

        /// <summary>
        /// 枚举当前已加载解决方案中的项目，递归遍历
        /// Solution Folders。每个项目暴露其 Name、UniqueName、
        /// FullName（.csproj/.vcxproj 路径）、Kind（项目类型 GUID——
        /// 让调用方区分 C++ 还是 C# 等）、是否为
        /// 启动项目，以及尽力而为的 <c>OutputTarget</c>（构建出的
        /// 可执行文件路径），让 <c>start_debugging</c> 有东西可启动。
        /// OutputTarget 解析按项目包裹在 try/catch 中——
        /// 单个无法读取属性的项目（C++/vcxproj、
        /// 解决方案文件夹、已卸载）贡献 null，绝不中止
        /// 整个枚举。
        /// </summary>
        /// <param name="query">可选的不区分大小写的子串过滤器，
        /// 作用于 Name / UniqueName / FullName。为 null 或空时返回全部
        /// 项目。</param>
        public async Task<ProjectSearchResult> SearchProjectsAsync(string? query, CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            // 未加载解决方案 → 空结果（区别于
            // 零项目的解决方案，后者实际上不可能）。
            var solution = dte.Solution;
            if (solution == null || string.IsNullOrWhiteSpace(solution.FullName))
                return new ProjectSearchResult(new List<ProjectInfo>(), Total: 0);

            // 一次性快照启动项目数组。SolutionBuild 返回一个
            // object（COM 术语中的 VARIANT SAFEARRAY）；将每个元素
            // 转为项目的 UniqueName。包裹在 try/catch 中，因为
            // 解决方案尚未加载完其构建状态时该
            // 属性可能抛异常。
            var startupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (solution.SolutionBuild?.StartupProjects is object startArr)
                {
                    // COM 集合是 IEnumerable；直接遍历。每个条目
                    // 是项目的 UniqueName（字符串），而非 Project。
                    foreach (var entry in (System.Collections.IEnumerable)startArr)
                    {
                        if (entry is string s && !string.IsNullOrEmpty(s))
                            startupNames.Add(s);
                    }
                }
            }
            catch
            {
                // 启动项目枚举是尽力而为的；偶发的 COM
                // 失败不得破坏完整的项目列表。
            }

            var results = new List<ProjectInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 递归遍历：Solution Folders（vsProjectKindSolutionFolder）
            // 通过 ProjectItems.SubProject 包含子项目。每个 SubProject
            // 本身可能又是另一个文件夹，因此需要递归。
            void Visit(Project project)
            {
                try
                {
                    string? uniqueName = null;
                    try { uniqueName = project.UniqueName; } catch { /* 某些项目类型会抛异常 */ }
                    string? name = project.Name;
                    string? fullName = null;
                    try { fullName = project.FullName; } catch { /* 已卸载/杂项项目 FullName 为空 */ }

                    // 存在 UniqueName 时按其去重（某些解决方案形态下
                    // 同一项目会出现在多个父节点下）；对于没有 UniqueName 的
                    // 虚拟项目，回退到 FullName/name。
                    string dedupKey = !string.IsNullOrEmpty(uniqueName)
                        ? uniqueName!
                        : (!string.IsNullOrEmpty(fullName) ? fullName! : (name ?? Guid.NewGuid().ToString()));
                    if (!seen.Add(dedupKey))
                        return;

                    string? kind = null;
                    try { kind = project.Kind; } catch { /* 留空为 null */ }

                    bool isStartup = uniqueName != null && startupNames.Contains(uniqueName);
                    string? outputTarget = ResolveOutputTargetInline(project);

                    results.Add(new ProjectInfo(
                        Name: name ?? "",
                        UniqueName: uniqueName ?? "",
                        FullName: string.IsNullOrEmpty(fullName) ? null : fullName,
                        Kind: kind,
                        IsStartupProject: isStartup,
                        OutputTarget: outputTarget));
                }
                catch
                {
                    // 单个无法枚举属性的项目被
                    // 跳过——绝不中止整个遍历。
                }

                // 递归进入 Solution Folder 的子项目。
                try
                {
                    if (string.Equals(project.Kind, SolutionFolderKindGuid,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var items = project.ProjectItems;
                        if (items != null)
                        {
                            for (int i = 1; i <= items.Count; i++)
                            {
                                try
                                {
                                    var sub = items.Item(i).SubProject;
                                    if (sub != null) Visit(sub);
                                }
                                catch { /* 跳过无法访问的子项 */ }
                            }
                        }
                    }
                }
                catch { /* 文件夹递归失败非致命 */ }
            }

            try
            {
                var dteProjects = solution.Projects;
                if (dteProjects != null)
                {
                    for (int i = 1; i <= dteProjects.Count; i++)
                    {
                        try { Visit(dteProjects.Item(i)); }
                        catch { /* 跳过 */ }
                    }
                }
            }
            catch
            {
                // 顶层枚举失败——返回已收集到的内容。
            }

            // 应用可选的查询子串过滤器（不区分大小写），
            // 作用于 Name / UniqueName / FullName。
            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query!.Trim();
                results.RemoveAll(p =>
                    !(ContainsCi(p.Name, q) || ContainsCi(p.UniqueName, q) || ContainsCi(p.FullName, q)));
            }

            return new ProjectSearchResult(results, Total: results.Count);
        }

        /// <summary>
        /// 尽力而为地解析项目构建出的可执行文件路径。
        /// 策略：取 ConfigurationManager.ActiveConfiguration，再读取
        /// OutputPath（一个目录，可能是相对路径）以及项目的
        /// OutputFileName（带扩展名的二进制名）。相对于
        /// 项目目录进行组合。这对 C#/VB/F# 托管
        /// 项目有效。对于 C++/vcxproj 及其他类型，这些属性要么
        /// 不存在要么会抛异常——在此捕获并返回 null。调用方
        ///（search_project，已在 UI 线程上）将 null 视为"无可解析
        /// 目标"；start_debugging 直接接收绝对 exe 路径，
        /// 因此此处为 null 永远不会致命。
        /// ThreadHelper.ThrowIfNotOnUIThread 是 VSTHRD010 分析器针对静态辅助方法
        /// 识别的显式主线程断言
        ///（该调用实际是空操作——SearchProjectsAsync 在递归进入项目之前
        /// 已切换到 UI 线程）。
        /// </summary>
        private static string? ResolveOutputTargetInline(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var cfgMgr = project.ConfigurationManager;
                var activeCfg = cfgMgr?.ActiveConfiguration;
                if (activeCfg == null) return null;

                var props = activeCfg.Properties;
                if (props == null) return null;

                // OutputPath 是输出目录；可能是相对路径
                //（"bin\Release\"）或绝对路径。OutputFileName 是带扩展名的
                // 二进制名（"MyApp.exe"）。两者都是按名称访问的、
                // 从 1 开始计数的 COM Property 项。
                string? outputPath = ReadProperty(props, "OutputPath");
                string? outputFileName = null;
                try { outputFileName = project.Properties?.Item("OutputFileName")?.Value as string; }
                catch { /* C++/其他类型不暴露 OutputFileName */ }

                if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(outputFileName))
                    return null;

                // 相对于项目目录解析输出目录
                //（FullName 是 .csproj 路径）。
                string? projectDir = null;
                try { projectDir = Path.GetDirectoryName(project.FullName); } catch { }
                if (string.IsNullOrEmpty(projectDir)) return null;

                string combinedDir = Path.IsPathRooted(outputPath!)
                    ? outputPath!
                    : Path.GetFullPath(Path.Combine(projectDir!, outputPath!));

                string target = Path.Combine(combinedDir, outputFileName!);
                return Path.GetFullPath(target);
            }
            catch
            {
                // 任何 COM/EnvDTE 抖动 → null。这个辅助方法的全部意义
                // 就在于它绝不抛异常（search_project 的不变式）。
                return null;
            }
        }

        /// <summary>
        /// 从 EnvDTE Properties COM 集合中读取命名属性，
        /// 属性不存在或抛异常时返回 null（某些
        /// 项目类型完全没有 OutputPath）。
        /// </summary>
        private static string? ReadProperty(EnvDTE.Properties props, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var val = props.Item(name)?.Value;
                return val as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsCi(string? hay, string needle)
        {
            if (string.IsNullOrEmpty(hay)) return false;
            return hay!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // =====================================================================
        // start_debugging —— 通过 VS 原生的
        // IVsDebugger2.LaunchDebugTargets2 路径启动任意 exe。不触碰项目配置、
        // launchSettings.json 或 vcxproj 文件。C++ 默认使用本机
        // 引擎；传入 DebugEngine="managed" 进行 CLR/.NET 调试，或传入原始
        // GUID 字符串指定其他引擎。所有参数仅用于本次内存中的
        // 单次启动调用（不持久化任何内容）。
        // =====================================================================

        private static readonly Guid NativeDebugEngineGuid = VSConstants.DebugEnginesGuids.NativeOnly_guid;
        private static readonly Guid ManagedDebugEngineGuid = VSConstants.DebugEnginesGuids.ManagedOnly_guid;

        /// <summary>
        /// Solution Folders（虚拟容器，并非
        /// 真正的构建项目）的项目 Kind GUID。硬编码是因为 EnvDTE.Constants 在
        /// 不同版本下以不同名称暴露它（某些版本为 vsProjectKindSolutionFolder，
        /// 本仓库引用的 Microsoft.VisualStudio.Interop
        /// 程序集中为 vsProjectKindSolutionItems）——而 GUID 本身是稳定的。
        /// </summary>
        private const string SolutionFolderKindGuid = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        /// <summary>
        /// 通过 <see cref="IVsDebugger2.LaunchDebugTargets2"/> 在 VS 调试器下
        /// 启动可执行文件。这是 VS 自己的
        /// "启动调试目标"原语——它把 exe 路径、
        /// 参数、工作目录、环境块和调试引擎
        /// 直接送进 CreateProcess + 所选引擎，零
        /// 依赖项目/解决方案配置。原生 C++、
        /// 托管 C# 以及任意独立 exe 走同一路径。
        /// </summary>
        /// <param name="startProgram">要调试的 exe 的绝对路径（即
        /// "目标"）。必填。</param>
        /// <param name="startArguments">可选的命令行参数，传给
        /// 该 exe。</param>
        /// <param name="environmentVariablesJson">可选的 JSON 对象字符串，
        /// 表示要在启动的进程上设置的环境变量
        ///（例如 <c>{"PATH":"...","MY_VAR":"1"}</c>）。为 null 或空时
        /// 继承父进程（VS）的环境。</param>
        /// <param name="workingDirectory">可选的工作目录；为 null/空时
        /// 默认为 exe 所在目录。</param>
        /// <param name="debugEngine">引擎选择器：<c>"native"</c>（默认；
        /// C/C++ 本机引擎）、<c>"managed"</c>（.NET CLR 引擎），或
        /// 任意其他引擎的原始调试引擎 GUID 字符串。</param>
        public async Task<StartDebuggingResult> StartDebuggingAsync(
            string startProgram,
            string? startArguments,
            string? environmentVariablesJson,
            string? workingDirectory,
            string? debugEngine,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(startProgram))
                throw new ArgumentException("startProgram (exe path) is required", nameof(startProgram));

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var warnings = new List<string>();

            // --- 解析调试引擎选择 -----------------------------------------------
            // "native" → VSConstants.DebugEnginesGuids.NativeOnly_guid
            //            ({3B476D35-A401-11D2-AAD4-00C04F990171})
            // "managed" → VSConstants.DebugEnginesGuids.ManagedOnly_guid
            //            ({449EC4CC-30D2-4032-9256-EE18EB41B62B})
            // 其他任何值都当作原始 GUID 字符串处理并 Guid.Parse。
            string engineLabel;
            Guid engineGuid;
            string? engineInput = debugEngine;
            if (string.IsNullOrWhiteSpace(engineInput))
            {
                engineGuid = NativeDebugEngineGuid;
                engineLabel = "native";
            }
            else
            {
                string e = engineInput!.Trim();
                if (string.Equals(e, "native", StringComparison.OrdinalIgnoreCase))
                {
                    engineGuid = NativeDebugEngineGuid;
                    engineLabel = "native";
                }
                else if (string.Equals(e, "managed", StringComparison.OrdinalIgnoreCase))
                {
                    engineGuid = ManagedDebugEngineGuid;
                    engineLabel = "managed";
                }
                else
                {
                    try
                    {
                        engineGuid = Guid.Parse(e);
                        engineLabel = engineGuid.ToString();
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(
                            $"DebugEngine value '{e}' is not 'native', 'managed', or a valid GUID: {ex.Message}",
                            nameof(debugEngine));
                    }
                }
            }

            // --- 解析环境块 ----------------------------------------------------
            // EnvironmentVariablesJson 是一个 string→string 的 JSON 对象。Null
            // 或空白时继承父进程环境（bstrEnv =
            // null）。非法 JSON 抛 ArgumentException（经
            // SafeCall 作为 internal_error 暴露——可接受；调用方喂了
            // 错误输入）。
            Dictionary<string, string>? envVars = null;
            if (!string.IsNullOrWhiteSpace(environmentVariablesJson))
            {
                try
                {
                    envVars = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        environmentVariablesJson!, McpJsonUtilities.DefaultOptions);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        "EnvironmentVariablesJson must be a JSON object of string→string (e.g. {\"KEY\":\"VALUE\"}). "
                        + ex.Message,
                        nameof(environmentVariablesJson));
                }
                if (envVars == null)
                    envVars = new Dictionary<string, string>();
            }

            // --- 解析工作目录 --------------------------------------------------
            string workingDir = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory!
                : (Path.GetDirectoryName(startProgram) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(workingDir))
            {
                // Path.GetDirectoryName 返回空（exe 位于根目录 / 仅为
                // 裸文件名）。回退到当前目录而不是崩溃。
                workingDir = Environment.CurrentDirectory;
                warnings.Add($"WorkingDirectory defaulted to VS current directory ({workingDir}) " +
                             "because the exe path has no directory component.");
            }

            // --- 获取 IVsDebugger2 ---------------------------------------------
            var debugger = await GetVsDebugger2Async(ct);

            // --- 构造 VsDebugTargetInfo2 并启动 ---------------------------------
            // LaunchDebugTargets2 的托管签名为 (uint count,
            // IntPtr pBuffer)，其中 pBuffer 指向一块非托管内存缓冲区，
            // 内含 count 个连续的 VsDebugTargetInfo2 结构体。
            // 我们分配缓冲区、把结构体封送进去、调用，
            // 并在 finally 中释放（该结构体内嵌 BSTR 字段，由
            // DestroyStructure 释放）。
            var info = new VsDebugTargetInfo2
            {
                cbSize = (uint)Marshal.SizeOf(typeof(VsDebugTargetInfo2)),
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = startProgram,
                bstrArg = startArguments ?? "",
                bstrCurDir = workingDir,
                bstrEnv = BuildEnvironmentBlock(envVars),
                guidLaunchDebugEngine = engineGuid,
                // 单引擎启动时 guidLaunchDebugEngine 单独一项即可
                //（依据 Microsoft Learn + nodejstools 生产代码）：
                // dwDebugEngineCount 保持 0，pDebugEngines 保持 IntPtr.Zero。
                dwDebugEngineCount = 0,
                pDebugEngines = IntPtr.Zero,
            };

            IntPtr pBuffer = IntPtr.Zero;
            int hr;
            try
            {
                pBuffer = Marshal.AllocCoTaskMem((int)info.cbSize);
                Marshal.StructureToPtr(info, pBuffer, fDeleteOld: false);
                hr = debugger.LaunchDebugTargets2(1, pBuffer);
            }
            finally
            {
                if (pBuffer != IntPtr.Zero)
                {
                    // DestroyStructure 释放内嵌的 BSTR（bstrExe /
                    // bstrArg / bstrCurDir / bstrEnv）。即使
                    // StructureToPtr 从未执行也安全（对清零内存是空操作）。
                    try { Marshal.DestroyStructure<VsDebugTargetInfo2>(pBuffer); }
                    catch { /* 清理阶段不得抛异常 */ }
                    Marshal.FreeCoTaskMem(pBuffer);
                }
            }

            bool started;
            if (hr < 0)
            {
                // 启动失败。把 HRESULT 暴露给 agent，调试器
                // 状态保持观察到的那样（很可能仍是 design）。我们不
                // 抛异常——工具上报结构化的失败，让 agent
                // 可以决定是否换用其他引擎/参数重试。
                started = false;
                string hrMsg;
                try
                {
                    var ex = Marshal.GetExceptionForHR(hr);
                    hrMsg = ex?.Message ?? $"HRESULT 0x{hr:X8}";
                }
                catch
                {
                    hrMsg = $"HRESULT 0x{hr:X8}";
                }
                warnings.Add($"LaunchDebugTargets2 failed: {hrMsg}");
            }
            else
            {
                started = true;
            }

            // 读取启动后的调试器模式。run 模式表示调用已
            // 接入调试器、进程正在执行；break 模式表示
            // 启动断点立即命中；design 模式表示
            // 启动并未真正附加（例如引擎与 exe
            // 类型不匹配、exe 缺失）。StateFromModeAsync 是规范的映射器。
            string state;
            try
            {
                state = await StateFromModeAsync(ct);
            }
            catch
            {
                state = "unknown";
            }

            return new StartDebuggingResult(
                Started: started,
                State: state,
                Program: startProgram,
                Arguments: string.IsNullOrEmpty(startArguments) ? null : startArguments,
                WorkingDirectory: workingDir,
                EnvironmentVariables: envVars,
                DebugEngine: engineLabel,
                Warnings: warnings.Count == 0 ? null : warnings);
        }

        /// <summary>
        /// 构造双 null 结尾的多字符串环境块，
        /// 即 VsDebugTargetInfo2.bstrEnv 所期望的格式（也就是
        /// CreateProcess 接收的原始 lpEnvironment）。
        /// 格式：<c>KEY=VALUE\0KEY2=VALUE2\0\0</c>。
        /// 当 <paramref name="env"/> 为 null 或空时返回 null，让
        /// 启动的进程继承父进程（VS）环境——start_debugging 的常见
        /// 情形。我们把结果直接赋给
        /// bstrEnv 字段；COM 互操作封送器会通过
        /// SysAllocStringLen 分配 BSTR（它使用显式长度，因此内嵌的 \0
        /// 得以保留）。这之所以可行，是因为我们构造 C# 字符串时就把
        /// 内嵌 null 和结尾终止符一并放入；封送器复制的是
        /// 完整 Length，而非到第一个 null 为止。
        /// </summary>
        private static string? BuildEnvironmentBlock(Dictionary<string, string>? env)
        {
            if (env == null || env.Count == 0)
                return null;

            // StringBuilder 不能干净地承载内嵌 \0（其实可以，但
            // 用 char[] / List<char> 时基于 Length 的分配更清晰）。
            var chars = new List<char>(capacity: env.Count * 32);
            foreach (var kv in env)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue; // 环境块中 null/空键是非法的
                foreach (char c in kv.Key) chars.Add(c);
                chars.Add('=');
                foreach (char c in kv.Value ?? "") chars.Add(c);
                chars.Add('\0');
            }
            // 结尾额外的 \0 标记块结束（双 null 结尾）。
            chars.Add('\0');
            return new string(chars.ToArray());
        }

        public async Task<BreakpointSetResult> SetBreakpointAsync(string file, int line, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("file path is required", nameof(file));
            if (line <= 0)
                throw new ArgumentOutOfRangeException(nameof(line), "line must be a positive integer");

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            // 守卫：当确实加载了解决方案时，对于不属于它的
            // 文件，拒绝静默无操作。若未加载解决方案，则跳过
            // 守卫交给 VS 处理（get_session_info 单独暴露 null
            // 状态）。规范化会折叠 .. / 混合
            // 分隔符 / 盘符大小写变体，使 FindProjectItem
            // 能匹配 VS 索引该项目时所用的路径。
            string loadedSolution = dte.Solution?.FullName ?? "";
            if (!string.IsNullOrWhiteSpace(loadedSolution))
            {
                string normalizedFile = NormalizeFilePath(file);
                ProjectItem? item = null;
                try
                {
                    item = dte.Solution.FindProjectItem(normalizedFile);
                }
                catch
                {
                    // FindProjectItem 对缺失项不应抛异常，但
                    // 偶发的 COM 失败不得把守卫未命中变成
                    // 工具硬故障——按"未找到"处理，让下方
                    // Breakpoints.Add 路径在确有错误时再暴露。
                    item = null;
                }
                if (item == null)
                {
                    throw new FileNotInSolutionException(
                        $"File '{file}' is not part of the loaded solution '{loadedSolution}'. " +
                        "set_breakpoint only accepts files that belong to the solution currently open in Visual Studio.",
                        file,
                        loadedSolution);
                }
            }

            var result = dte.Debugger.Breakpoints.Add(File: file, Line: line);
            if (result == null || result.Count == 0)
                throw new InvalidOperationException(
                    $"Could not set breakpoint at {file}:{line}. Verify the file path is absolute, " +
                    "the file exists in the solution, and the line contains executable code.");

            var bp = result.Item(1);
            return new BreakpointSetResult(
                File: bp.File ?? file,
                Line: bp.FileLine,
                Enabled: bp.Enabled,
                Condition: bp.Condition ?? "",
                Name: bp.Name ?? "");
        }

        public async Task<BreakpointListResult> ListBreakpointsAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var breakpoints = dte.Debugger.Breakpoints;
            var list = new List<BreakpointInfo>(breakpoints.Count);
            for (int i = 1; i <= breakpoints.Count; i++)
            {
                var bp = breakpoints.Item(i);
                list.Add(new BreakpointInfo(
                    File: bp.File ?? "",
                    Line: bp.FileLine,
                    Column: bp.FileColumn,
                    Enabled: bp.Enabled,
                    Condition: bp.Condition ?? "",
                    Name: bp.Name ?? ""));
            }

            return new BreakpointListResult(list, Total: breakpoints.Count);
        }

        public async Task<BreakpointDeleteResult> DeleteBreakpointAsync(string file, int line, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("file path is required", nameof(file));
            if (line <= 0)
                throw new ArgumentOutOfRangeException(nameof(line), "line must be a positive integer");

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var normalizedFile = Path.GetFullPath(file);
            foreach (Breakpoint bp in dte.Debugger.Breakpoints)
            {
                string bpFile = Path.GetFullPath(bp.File);
                if (string.Equals(bpFile, normalizedFile, StringComparison.OrdinalIgnoreCase)
                    && bp.FileLine == line)
                {
                    bp.Delete();
                    return new BreakpointDeleteResult(Deleted: true, File: file, Line: line);
                }
            }

            throw new BreakpointNotFoundException($"No breakpoint found at {file}:{line}");
        }

        public async Task<BreakpointClearResult> ClearAllBreakpointsAsync(bool confirm, CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            int count = dte.Debugger.Breakpoints.Count;

            if (!confirm)
            {
                // 干跑：报告"将要删除什么"而不触碰
                // 任何内容。强制 agent 审核计数后再用 confirm=true 发起
                // 第二次调用，防止误批量删除。
                return new BreakpointClearResult(
                    Deleted: 0,
                    WasDryRun: true,
                    WouldDelete: count,
                    Message: count == 0
                        ? "No breakpoints to delete."
                        : $"Dry run: would delete {count} breakpoint(s). Pass confirm=true to actually delete.");
            }

            for (int i = count; i >= 1; i--)
            {
                dte.Debugger.Breakpoints.Item(i).Delete();
            }

            return new BreakpointClearResult(
                Deleted: count,
                WasDryRun: false,
                WouldDelete: count,
                Message: $"Deleted {count} breakpoint(s).");
        }

        /// <summary>
        /// 通过 <c>DTE.Solution.SolutionBuild.Build(WaitForBuildToFinish: true)</c>
        /// 触发整个解决方案构建。
        /// 阻塞直到构建完成——MCP Streamable HTTP 传输在
        /// 等待期间保持 SSE 响应流开启（无客户端
        /// 超时），单会话请求闸门保持持有（agent
        /// 反正也在等待这个结果）。返回活动配置
        /// 名称和失败项目数（0 == 成功）。
        /// </summary>
        public async Task<BuildSolutionResult> BuildSolutionAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var solution = dte.Solution;
            if (solution == null || string.IsNullOrWhiteSpace(solution.FullName))
                throw new InvalidOperationException("No solution is loaded");

            var solutionBuild = solution.SolutionBuild;
            if (solutionBuild == null)
                throw new InvalidOperationException("SolutionBuild is not available");

            string configuration = "Unknown";
            try { configuration = solutionBuild.ActiveConfiguration?.Name ?? "Unknown"; }
            catch { /* 异常解决方案状态下 ActiveConfiguration 可能抛异常 */ }

            // 异步构建：Build(false) 立即返回，不
            // 阻塞 UI 线程。构建完成时 OnBuildDone 在 UI 线程上触发；
            // 一个 TaskCompletionSource 把它桥接到
            // async/await，使本方法在 VS 构建期间让出 UI 线程
            // —— VS 保持完全响应。避免使用 Build(true)：它会
            // 在整个构建期间冻结 UI，并且存在已知的死锁 bug
            //（TcUnit-Runner issue #5）。
            var buildEvents = dte.Events.BuildEvents;
            var tcs = new TaskCompletionSource<bool>();

            void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
            {
                buildEvents.OnBuildDone -= OnBuildDone;
                tcs.TrySetResult(true);
            }
            buildEvents.OnBuildDone += OnBuildDone;

            using (ct.Register(() => { buildEvents.OnBuildDone -= OnBuildDone; tcs.TrySetCanceled(); }))
            {
                solutionBuild.Build(false);

                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(15)));
                if (done != tcs.Task)
                {
                    buildEvents.OnBuildDone -= OnBuildDone;
                    throw new TimeoutException("Build did not complete within 15 minutes");
                }
                await tcs.Task;
            }

            int failedProjects = solutionBuild.LastBuildInfo;
            return new BuildSolutionResult(
                Configuration: configuration,
                FailedProjects: failedProjects,
                Succeeded: failedProjects == 0);
        }

        /// <summary>
        /// 读取 VS 输出窗口的 Build 窗格并返回切片视图。
        /// agent 在 <see cref="BuildSolutionAsync"/> 之后用它直接从
        /// 构建日志检视编译诊断（比错误列表更可信，后者混有
        /// 过时的波浪线）。<paramref name="tail"/>
        /// = true 读取最近的 <paramref name="maxLines"/> 行
        ///（末尾的错误）；false 读取前 <paramref name="maxLines"/> 行。
        /// </summary>
        public async Task<BuildOutputResult> GetBuildOutputAsync(int maxLines, bool tail, CancellationToken ct)
        {
            var (paneName, allLines) = await ReadBuildPaneAllLinesAsync(ct);
            int totalLines = allLines.Length;

            int limit = maxLines > 0 ? maxLines : 200;
            IEnumerable<string> slice = tail
                ? allLines.Skip(Math.Max(0, totalLines - limit))
                : allLines.Take(limit);
            var selected = slice.ToArray();

            return new BuildOutputResult(
                PaneName: paneName,
                Output: string.Join("\n", selected),
                TotalLines: totalLines,
                ReturnedLines: selected.Length,
                Truncated: totalLines > selected.Length);
        }

        /// <summary>
        /// 读取 Build pane 自 <paramref name="fromLine"/> 起新增的行。
        /// 用于 build 保活循环：把增量构建日志通过 logging 通知转发给客户端
        /// （不暴露给模型）。无状态——偏移由调用方持有，符合单会话串行契约。
        /// </summary>
        public async Task<BuildOutputDeltaResult> GetBuildOutputDeltaAsync(int fromLine, CancellationToken ct)
        {
            var (_, allLines) = await ReadBuildPaneAllLinesAsync(ct);
            int totalLines = allLines.Length;
            // pane 被清空重置（新构建）导致 fromLine 越过当前行数时，
            // 从头重读，避免漏掉清空后的日志。
            int start = fromLine > totalLines ? 0 : Math.Max(0, fromLine);
            var newLines = new List<string>(Math.Max(0, totalLines - start));
            for (int i = start; i < totalLines; i++)
                newLines.Add(allLines[i]);
            return new BuildOutputDeltaResult(totalLines, newLines);
        }

        /// <summary>
        /// 定位 Build pane（名称按本地化模糊匹配）并返回其全文按行切分的结果
        /// 及 pane 显示名。供 <see cref="GetBuildOutputAsync"/> 与
        /// <see cref="GetBuildOutputDeltaAsync"/> 复用。需在 UI 线程执行
        /// （pane 枚举 + EditPoint 均为 COM 调用）。
        /// </summary>
        private async Task<(string PaneName, string[] Lines)> ReadBuildPaneAllLinesAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            var outputWindow = dte.ToolWindows.OutputWindow;

            // Build 窗格名称是本地化的（"Build"/"生成"/"构建"/"組建"），
            // 所以按模糊匹配而非精确字符串匹配。
            OutputWindowPane? buildPane = null;
            try
            {
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    string? n = pane.Name;
                    if (n != null && (n.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
                                      || n.IndexOf("生成", StringComparison.Ordinal) >= 0
                                      || n.IndexOf("构建", StringComparison.Ordinal) >= 0
                                      || n.IndexOf("組建", StringComparison.Ordinal) >= 0))
                    {
                        buildPane = pane;
                        break;
                    }
                }
            }
            catch { /* 枚举是尽力而为的 */ }

            if (buildPane == null)
                throw new InvalidOperationException("Build output pane not found in the Output window");

            // 通过 EditPoint 读取全文——不会触碰用户的选择。
            string allText = "";
            try
            {
                var textDoc = buildPane.TextDocument;
                var ep = textDoc.StartPoint.CreateEditPoint();
                allText = ep.GetText(textDoc.EndPoint);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read Build output pane: {ex.Message}");
            }

            var lines = allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            return (buildPane.Name ?? "", lines);
        }

        private async Task<DBGMODE> GetDebuggerModeAsync(CancellationToken ct)
        {
            // IVsDebugger2（而非 v1）：v1 接口的 OLE marshaler 在
            // 转换落到 STA 线程之外时 QI 会以
            // E_NOINTERFACE 失败，而
            // IVsDebugger2.GetInternalDebugMode 封送健壮。相同的 DBGMODE
            // 契约，相同的数组输出签名。
            var debugger = await GetVsDebugger2Async(ct);
            var mode = new DBGMODE[1];
            int hr = debugger.GetInternalDebugMode(mode);
            if (hr != 0)
                throw new InvalidOperationException($"GetInternalDebugMode failed with HRESULT 0x{hr:X8}");
            return mode[0] & ~DBGMODE.DBGMODE_Enc;
        }

        /// <summary>
        /// 获取 DTE，同时保证调试器处于断点模式。
        /// 在任何非断点模式下抛出 <see cref="RequireBreakModeException"/>（携带当前
        /// 状态字符串），以便 SafeCall 填充
        /// ErrorResult.State。
        /// </summary>
        private async Task<DTE2> RequireBreakModeAsync(CancellationToken ct)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var mode = await GetDebuggerModeAsync(ct);
            if (mode != DBGMODE.DBGMODE_Break)
            {
                throw new RequireBreakModeException(
                    $"Operation requires break mode. Current state: {MapState(mode)}",
                    MapState(mode));
            }

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");
            return dte;
        }

        private async Task PollForBreakModeAsync(CancellationToken ct)
        {
            for (int i = 0; i < 25; i++)
            {
                var mode = await GetDebuggerModeAsync(ct);
                if (mode == DBGMODE.DBGMODE_Break)
                    return;
                await Task.Delay(200, ct);
            }
            // 最后再轮询一次，让任何状态变更稳定下来。
            await GetDebuggerModeAsync(ct);
        }

        /// <summary>
        /// 从当前断点继续执行。
        ///
        /// <para><b>wait_for_break=false（默认）</b>：即发即忘。调用
        /// <c>Go(false)</c> 并立即返回 <c>state="running"</c>。
        /// agent 之后可轮询 <c>get_debugger_state</c> 来检测
        /// 下一次中断。</para>
        ///
        /// <para><b>wait_for_break=true</b>：通过 <c>DebuggerEvents.OnEnterBreakMode</c> /
        /// <c>OnEnterDesignMode</c> 等待下一次中断/停止
        /// 事件，并用
        /// <see cref="TaskCompletionSource{TResult}"/> 桥接到 async。HTTP 响应
        /// 按需保持开启；KeepAliveNotifier 周期性地在途响应流上发送 MCP
        /// logging 通知，在工具等待中断事件期间保持
        /// 连接存活。</para>
        ///
        /// <para><c>timeout_seconds</c>（仅在 wait_for_break=true 时有意义）：
        /// <c>0</c>（默认）= 无限等待（依赖 SSE 心跳保活）；
        /// <c>N&gt;0</c> = 至多等待 N 秒，超时时返回当前
        /// 状态 + 一条消息（不是错误——agent 可重试）。</para>
        /// </summary>
        public async Task<ExecutionResult> ContinueExecutionAsync(
            bool waitForBreak,
            int timeoutSeconds,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);

            // 默认模式：立即返回。Go(false) 不阻塞，调试器进入 run 模式。
            if (!waitForBreak)
            {
                dte.Debugger.Go(false);
                return new ExecutionResult(State: "running", Message: "Execution continued");
            }

            // 等待模式：订阅 OnEnterBreakMode / OnEnterDesignMode，
            // 用 TaskCompletionSource 桥接 COM 事件到 async/await。
            // 整段不阻塞 UI 线程；HTTP POST 靠 SSE 心跳保活。
            var events = dte.Events.DebuggerEvents;
            var tcs = new TaskCompletionSource<bool>();

            void OnBreak(EnvDTE.dbgEventReason reason, ref EnvDTE.dbgExecutionAction action)
            {
                events.OnEnterBreakMode -= OnBreak;
                events.OnEnterDesignMode -= OnStop;
                tcs.TrySetResult(true);
            }
            void OnStop(EnvDTE.dbgEventReason reason)
            {
                events.OnEnterBreakMode -= OnBreak;
                events.OnEnterDesignMode -= OnStop;
                tcs.TrySetResult(false);
            }

            // 超时与 shutdown / agent 取消共用一个 linked CTS：只跟随
            // 外部 ct（shutdown、idle 兜底、agent 取消），并按 timeoutSeconds
            // 启动 CancelAfter。timeout_seconds=0 时不启动 CancelAfter = 无限等。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutSeconds > 0)
                linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            events.OnEnterBreakMode += OnBreak;
            events.OnEnterDesignMode += OnStop;

            using (linkedCts.Token.Register(() =>
            {
                events.OnEnterBreakMode -= OnBreak;
                events.OnEnterDesignMode -= OnStop;
                tcs.TrySetCanceled();
            }))
            {
                dte.Debugger.Go(false);

                try
                {
                    bool hit = await tcs.Task.ConfigureAwait(false);
                    return new ExecutionResult(
                        State: hit ? "break" : "stopped",
                        Message: hit
                            ? "Breakpoint hit."
                            : "Debuggee exited without hitting a breakpoint.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 外部取消（shutdown、idle 兜底、agent 主动取消）：重抛。
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // timeoutSeconds 到期：不算错误，返回当前状态 + 提示。
                    string state = await StateFromModeAsync(ct).ConfigureAwait(false);
                    return new ExecutionResult(
                        State: state,
                        Message: $"Continue waited {timeoutSeconds}s without a break event — still {state}.");
                }
            }
        }

        public async Task<ExecutionResult> StepIntoAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);
            dte.Debugger.StepInto(true);
            await PollForBreakModeAsync(ct);
            return new ExecutionResult(State: await StateFromModeAsync(ct), Message: "Step into complete");
        }

        public async Task<ExecutionResult> StepOverAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);
            dte.Debugger.StepOver(true);
            await PollForBreakModeAsync(ct);
            return new ExecutionResult(State: await StateFromModeAsync(ct), Message: "Step over complete");
        }

        public async Task<ExecutionResult> StepOutAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);
            dte.Debugger.StepOut(true);
            await PollForBreakModeAsync(ct);
            return new ExecutionResult(State: await StateFromModeAsync(ct), Message: "Step out complete");
        }

        public async Task<ExecutionResult> StopDebuggingAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var mode = await GetDebuggerModeAsync(ct);
            if (mode == DBGMODE.DBGMODE_Design)
                throw new InvalidOperationException(
                    "Cannot stop debugging when not in a debug session (current state: design)");

            var dte = await GetDteAsync(ct);
            if (dte == null)
                throw new InvalidOperationException("DTE service is not available");

            dte.Debugger.Stop();
            return new ExecutionResult(State: "stopped", Message: "Debugging stopped");
        }

        public async Task<LocalsResult> ListLocalVariablesAsync(int maxLocals, int maxChars, CancellationToken ct)
        {
            ThrowIfDisposed();
            int budget = maxChars > 0 ? maxChars : DefaultCharBudget;
            int cap = maxLocals > 0 ? maxLocals : DefaultLocalCap;
            var dte = await RequireBreakModeAsync(ct);

            var frame = dte.Debugger.CurrentStackFrame;
            if (frame?.Locals == null)
                return new LocalsResult(
                    Locals: new List<ExpressionInfo>(), Total: 0, Returned: 0, Truncated: false);

            var locals = frame.Locals;
            int total = locals.Count;
            int limit = Math.Min(total, cap);
            var list = new List<ExpressionInfo>(limit);
            int consumed = 0;
            bool budgetTruncated = false;

            // depth 0 = 只顶层（name/type/value/hasChildren，不展开 children）。
            // 让 list_local_variables 保持浅层，local 多或对象深的栈帧也不会爆上下文——
            // agent 用 get_variable_detail 下钻特定变量。
            for (int i = 1; i <= limit; i++)
            {
                var local = locals.Item(i);
                int estimate = EstimateExpressionFootprint(local);
                if (consumed + estimate > budget && list.Count > 0)
                {
                    budgetTruncated = true;
                    break;
                }
                var info = ToExpressionInfo(local, budget - consumed, depth: 0, out int actual);
                consumed += actual;
                list.Add(info);
            }

            return new LocalsResult(
                Locals: list, Total: total, Returned: list.Count,
                Truncated: budgetTruncated || total > cap);
        }

        public async Task<CallStackResult> GetCallStackAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            var dte = await RequireBreakModeAsync(ct);

            var thread = dte.Debugger.CurrentThread;
            if (thread?.StackFrames == null)
                return new CallStackResult(
                    Frames: new List<StackFrameInfo>(), Total: 0, Returned: 0);

            var frames = thread.StackFrames;
            int maxFrames = Math.Min(frames.Count, MaxFrames);
            var list = new List<StackFrameInfo>(maxFrames);
            for (int i = 1; i <= maxFrames; i++)
            {
                var frame = frames.Item(i);
                list.Add(new StackFrameInfo(
                    FrameIndex: i - 1,
                    FunctionName: frame.FunctionName ?? "",
                    Module: frame.Module ?? "",
                    ReturnType: frame.ReturnType ?? ""));
            }

            return new CallStackResult(Frames: list, Total: frames.Count, Returned: maxFrames);
        }

        public async Task<ExpressionResult> EvaluateExpressionAsync(string expression, int maxChars, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("expression is required", nameof(expression));
            int budget = maxChars > 0 ? maxChars : DefaultCharBudget;
            var dte = await RequireBreakModeAsync(ct);

            var result = dte.Debugger.GetExpression(expression, false, 5000);
            if (result == null || !result.IsValidValue)
            {
                return new ExpressionResult(new ExpressionInfo(
                    Name: expression,
                    Type: result?.Type ?? "",
                    Value: null,
                    Error: true,
                    HasChildren: false,
                    Children: new List<ExpressionInfo>(),
                    Truncated: false,
                    Hint: "Expression could not be evaluated"));
            }

            return new ExpressionResult(ToExpressionInfo(result, budget, int.MaxValue, out _));
        }

        public async Task<VariableDetailResult> GetVariableDetailAsync(string[] expressions, int depth, int maxChars, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (expressions == null || expressions.Length == 0)
                throw new ArgumentException("expressions is required and must be non-empty", nameof(expressions));
            int budget = maxChars > 0 ? maxChars : DefaultCharBudget;
            // depth 1 = 节点 + 直接 children（浅）；每加一层 depth 多展开一层。
            // 下限 1，传 0/负数也至少返回一层。
            int d = depth > 0 ? depth : 1;
            var dte = await RequireBreakModeAsync(ct);

            var details = new List<ExpressionInfo>(expressions.Length);
            // 每个表达式由调试引擎独立求值——它接受任意 C++/C# 表达式
            //（obj.member、arr[3]、ptr->next），所以工具里不自己造路径语法。
            foreach (var expression in expressions)
            {
                if (string.IsNullOrWhiteSpace(expression))
                {
                    details.Add(new ExpressionInfo(
                        Name: expression ?? "", Type: "", Value: null, Error: true,
                        HasChildren: false, Children: new List<ExpressionInfo>(),
                        Truncated: false, Hint: "empty expression"));
                    continue;
                }

                var result = dte.Debugger.GetExpression(expression, false, 5000);
                if (result == null || !result.IsValidValue)
                {
                    details.Add(new ExpressionInfo(
                        Name: expression, Type: result?.Type ?? "", Value: null, Error: true,
                        HasChildren: false, Children: new List<ExpressionInfo>(),
                        Truncated: false, Hint: "Expression could not be evaluated"));
                    continue;
                }

                details.Add(ToExpressionInfo(result, budget, d, out _));
            }

            return new VariableDetailResult(details);
        }

        // --- DTO 装配辅助方法（字符预算截断逻辑已从
        //     已删除的表达式序列化辅助方法中移出；BFS + 截断 +
        //     Hint 语义按 CONTEXT Claude 的 Discretion 保留）。---

        /// <summary>
        /// 把 EnvDTE <see cref="Expression"/> 转成 <see cref="ExpressionInfo"/> 树。
        /// 受 char budget 和 depth 双重限制：展开到第 depth 层就停（更深层标
        /// hasChildren 不展开）；budget 耗尽则该层剩余 children 丢弃并置
        /// <see cref="ExpressionInfo.Truncated"/> + 下钻提示，提示 LLM 调
        /// <c>get_variable_detail</c>。
        /// </summary>
        private static ExpressionInfo ToExpressionInfo(Expression expr, int budget, int depth, out int consumed)
        {
            string name = expr.Name ?? "";
            string type = expr.Type ?? "";

            if (!expr.IsValidValue)
            {
                consumed = name.Length + type.Length + 32;
                return new ExpressionInfo(
                    Name: name, Type: type, Value: null, Error: true,
                    HasChildren: false, Children: new List<ExpressionInfo>(),
                    Truncated: false, Hint: null);
            }

            string value = expr.Value ?? "";
            // 为顶层字段自身预留空间。
            int used = name.Length + type.Length + value.Length + 48;
            consumed = used;

            var children = expr.DataMembers;
            bool hasChildren = children != null && children.Count > 0;

            // depth <= 0：停止展开。返回 hasChildren 让调用方知道可下钻，但不返回
            // children——这是 list_local_variables 的浅层模式（depth 0），也是
            // get_variable_detail 的展开底线（每层 depth-1，所以 depth 1 的 children
            // 在 depth 0 = 浅，不再有 grandchildren）。
            if (depth <= 0 || !hasChildren)
            {
                return new ExpressionInfo(
                    Name: name, Type: type, Value: value, Error: false,
                    HasChildren: hasChildren, Children: new List<ExpressionInfo>(),
                    Truncated: false,
                    Hint: hasChildren ? "Use get_variable_detail to expand" : null);
            }

            var childList = new List<ExpressionInfo>();
            bool truncated = false;
            for (int i = 1; i <= children.Count; i++)
            {
                var child = children.Item(i);
                int childEstimate = EstimateExpressionFootprint(child);
                if (used + childEstimate > budget)
                {
                    truncated = true;
                    break;
                }

                var childInfo = ToExpressionInfo(child, budget - used, depth - 1, out int childActual);
                used += childActual;
                childList.Add(childInfo);
            }

            return new ExpressionInfo(
                Name: name, Type: type, Value: value, Error: false,
                HasChildren: true, Children: childList,
                Truncated: truncated,
                Hint: truncated ? "Use get_variable_detail to explore remaining children" : null);
        }

        /// <summary>
        /// 廉价的足迹估算（name + type + value + 框架开销），用于
        /// 在付出递归展开代价之前判断
        /// 某个子项是否还在预算之内。
        /// </summary>
        private static int EstimateExpressionFootprint(Expression expr)
        {
            int n = expr.Name?.Length ?? 0;
            int t = expr.Type?.Length ?? 0;
            int v = expr.Value?.Length ?? 0;
            return n + t + v + 48;
        }

        private async Task<string> StateFromModeAsync(CancellationToken ct)
        {
            var mode = await GetDebuggerModeAsync(ct);
            return MapState(mode);
        }

        private static string MapState(DBGMODE mode)
        {
            DBGMODE baseMode = mode & ~DBGMODE.DBGMODE_Enc;
            return baseMode switch
            {
                DBGMODE.DBGMODE_Design => "design",
                DBGMODE.DBGMODE_Break  => "break",
                DBGMODE.DBGMODE_Run    => "running",
                _ => $"unknown ({mode})"
            };
        }

        /// <summary>
        /// 将文件路径规范化用于成员判定：<see cref="Path.GetFullPath"/>
        /// 解析 <c>..\</c> 相对段并规范化分隔符，随后正斜杠处理 +
        /// <see cref="string.ToUpperInvariant"/> 折叠掉
        /// 剩余的大小写/分隔符差异（Windows 路径不区分大小写；
        /// <c>DTE.Solution.FindProjectItem</c> 按它所索引的
        /// 反斜杠路径匹配）。提取为纯方法，便于脱离真实 VS
        /// 进行单元测试。
        /// </summary>
        public static string NormalizeFilePath(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return file;
            string full = Path.GetFullPath(file);
            // 将任何其他分隔符替换为反斜杠，使 FindProjectItem 的
            // 索引（仅反斜杠）无论调用方约定如何都能匹配。
            return full.Replace('/', '\\').ToUpperInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DebuggerFacade));
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
