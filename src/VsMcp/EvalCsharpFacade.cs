#if EVAL_CSHARP
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace VsMcp
{
    /// <summary>
    /// 在 VS 进程内动态执行 C# 的 facade。
    ///
    /// 实现方式：用 VS 主进程**已加载**的 Roslyn 编译 API（CSharpCompilation）编译脚本，
    /// 而非 scripting（CSharpScript / ScriptOptions）。原因：VS 自己加载了一套 Roslyn
    /// （Microsoft.CodeAnalysis v5.7.0.0，default context，C# IDE 用）；若再 LoadFrom
    /// Desktop 那套 InteractiveHost 的 Scripting.dll（LoadFrom context）用 CSharpScript，
    /// 两套的依赖（System.Collections.Immutable 等）版本不匹配，加载 ScriptOptions 时抛
    /// TypeLoadException（其字段 ImmutableArray&lt;MetadataReference&gt; 解析失败）。net48
    /// 没有 AssemblyLoadContext，无法干净隔离两套同名程序集。CSharpCompilation 在 VS 自己的
    /// Microsoft.CodeAnalysis.dll 里，依赖完整、无冲突；编译产物引用 VS 已加载的程序集、
    /// 加载到 default context，类型完全兼容。
    ///
    /// 装一次扩展后，agent / 调用者经 eval_csharp 工具发任意 C# 探查代码，全程零编译扩展、
    /// 不重启 VS、不破坏运行时状态（如 C++ LSP 激活态），把过去"改探查点→编 vsix→
    /// 卸载→装→重启→读日志"的数分钟循环压成一次 MCP 调用。
    /// </summary>
    public sealed class EvalCsharpFacade : IDisposable
    {
        private readonly AsyncPackage _package;
        private readonly JoinableTaskFactory _jtf;
        private readonly ILogger<EvalCsharpFacade> _logger;

        // 编译器反射成员缓存（首次 EnsureCompiler 填充）。全部来自 VS 已加载的 Roslyn，
        // 非 Desktop InteractiveHost 那套。
        private int _loaded;
        private bool _loadSucceeded;
        private string? _loadError;

        private Type? _metadataReferenceType;
        private Type? _csharpCompilationType;
        private Type? _csharpSyntaxTreeType;
        private Type? _csharpCompilationOptionsType;
        private Type? _outputKindType;
        private Type? _syntaxTreeType;
        private MethodInfo? _parseText;
        private MethodInfo? _createRef;
        private MethodInfo? _compilationCreate;
        private MethodInfo? _emit;

        // 引用程序集列表缓存：VS 启动后基本不变，首次建后复用，避免每次编译都遍历 + CreateFromFile。
        private object? _cachedRefs;

        public EvalCsharpFacade(AsyncPackage package, ILogger<EvalCsharpFacade> logger)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _jtf = package.JoinableTaskFactory;
            _logger = logger;
        }

        /// <summary>
        /// 执行一段 C# 脚本。脚本经注入的 <see cref="EvalHost"/> 访问 VS：Package / JTF。
        /// 可用 <c>await JTF.SwitchToMainThreadAsync()</c> 切 UI 线程，
        /// <c>await Package.GetServiceAsync(...)</c> 拿 VS 服务，反射读非 public 字段。
        /// </summary>
        public async Task<EvalCsharpResult> EvalAsync(string code, int timeoutSeconds, CancellationToken externalCt)
        {
            var output = new StringBuilder();
            void Log(string msg)
            {
                if (output.Length > 0) output.AppendLine();
                output.Append(msg);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            if (timeoutSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var ct = cts.Token;

            if (!EnsureCompiler())
            {
                return new EvalCsharpResult("", "null",
                    "编译器初始化失败：" + _loadError);
            }

            // 把用户代码包进一个 async 方法，注入 Package/JTF/Log 局部变量。
            var wrappedCode = WrapUserCode(code);

            object? compilation;
            try
            {
                compilation = BuildCompilation(wrappedCode, out string? buildError);
                if (compilation == null)
                {
                    return new EvalCsharpResult(output.ToString(), "null", "构造 CSharpCompilation 失败：" + buildError);
                }
            }
            catch (Exception ex)
            {
                return new EvalCsharpResult(output.ToString(), "null",
                    $"构造编译失败：{ex.GetType().Name}: {ex.Message}");
            }

            // Emit 到内存流。
            Assembly? compiled;
            using (var ms = new MemoryStream())
            {
                object emitResult;
                try { emitResult = _emit!.Invoke(compilation, BuildArgs(_emit.GetParameters(), ms)); }
                catch (Exception ex)
                {
                    var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    return new EvalCsharpResult(output.ToString(), "null",
                        $"Emit 调用异常：{real.GetType().Name}: {real.Message}");
                }

                bool success = (bool)emitResult.GetType().GetProperty("Success")!.GetValue(emitResult)!;
                if (!success)
                {
                    var diags = (IEnumerable)emitResult.GetType().GetProperty("Diagnostics")!.GetValue(emitResult)!;
                    var errors = new List<string>();
                    foreach (var d in diags)
                    {
                        // Diagnostic.ToString() 形如 "(line,col): error CSxxxx: ..."，已含严重级别。
                        errors.Add(d.ToString());
                    }
                    return new EvalCsharpResult(output.ToString(), "null",
                        "脚本编译错误：\n" + string.Join("\n", errors.Take(30)));
                }
                compiled = Assembly.Load(ms.ToArray());
            }

            // 反射调 __EvalScript.Run(EvalHost)。
            var host = new EvalHost(_package, _jtf, Log);
            object? returnValue = null;
            try
            {
                var scriptType = compiled.GetType("__EvalScript")
                    ?? throw new InvalidOperationException("编译产物缺少 __EvalScript 类型");
                var instance = Activator.CreateInstance(scriptType);
                var runMethod = scriptType.GetMethod("Run")
                    ?? throw new InvalidOperationException("__EvalScript 缺少 Run 方法");
                var task = (Task<object>?)runMethod.Invoke(instance, new object[] { host });
                if (task == null)
                {
                    return new EvalCsharpResult(output.ToString(), "null", "Run 返回 null task");
                }
                returnValue = await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (externalCt.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                return new EvalCsharpResult(output.ToString(), "null",
                    $"脚本在 {timeoutSeconds}s 内未完成，已强制取消");
            }
            catch (Exception ex)
            {
                var real = ex;
                while (real is TargetInvocationException tie && tie.InnerException != null) real = tie.InnerException;
                return new EvalCsharpResult(output.ToString(), "null",
                    $"{real.GetType().Name}: {real.Message}");
            }

            return new EvalCsharpResult(output.ToString(), SerializeValue(returnValue), null);
        }

        /// <summary>把用户代码包进 async 方法，注入 Package/JTF/Log。末尾 return null 兜底
        /// （用户代码若已 return 则该行不可达；若没有则保证方法有返回值）。</summary>
        private static string WrapUserCode(string code) =>
@"using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.ComponentModelHost;
using EnvDTE;
using EnvDTE80;
using VsMcp;

public class __EvalScript
{
    public async Task<object> Run(EvalHost h)
    {
        var Package = h.Package;
        var JTF = h.JTF;
        var Log = new Action<object>(h.Log);
" + code + @"
        return null;
    }
}";

        /// <summary>
        /// 首次调用时从 VS 已加载的程序集里反射绑定 Roslyn 编译 API（CSharpCompilation 等）。
        /// 全部取自 default context（VS 自己加载的），不 LoadFrom Desktop 那套（避免依赖冲突）。
        /// </summary>
        private bool EnsureCompiler()
        {
            if (_loaded != 0) return _loadSucceeded;
            _loaded = 1;
            try
            {
                // VS 加载的 Microsoft.CodeAnalysis / .CSharp（default context）。理论上各只有一个
                // （新版不再 LoadFrom Desktop），取 First 即可。
                var ca = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis");
                var cs = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp");
                if (ca == null || cs == null)
                {
                    _loadError = $"VS 未加载 Roslyn 编译器：CodeAnalysis={ca != null}, CSharp={cs != null}";
                    return false;
                }

                _metadataReferenceType = ca.GetType("Microsoft.CodeAnalysis.MetadataReference");
                _csharpCompilationType = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
                _csharpSyntaxTreeType = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                _csharpCompilationOptionsType = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
                _outputKindType = ca.GetType("Microsoft.CodeAnalysis.OutputKind");
                _syntaxTreeType = ca.GetType("Microsoft.CodeAnalysis.SyntaxTree");

                if (_metadataReferenceType == null || _csharpCompilationType == null
                    || _csharpSyntaxTreeType == null || _csharpCompilationOptionsType == null
                    || _outputKindType == null || _syntaxTreeType == null)
                {
                    _loadError = $"反射取 Roslyn 类型失败：MR={_metadataReferenceType != null}, CC={_csharpCompilationType != null}, CST={_csharpSyntaxTreeType != null}, CCO={_csharpCompilationOptionsType != null}, OK={_outputKindType != null}, ST={_syntaxTreeType != null}";
                    return false;
                }

                // MetadataReference.CreateFromFile(string[, MetadataReferenceProperties])——取首参
                // string、参数最少的重载；调用时用 BuildArgs 填默认属性。
                _createRef = _metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "CreateFromFile"
                        && m.GetParameters().Length > 0
                        && m.GetParameters()[0].ParameterType == typeof(string))
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();

                // CSharpSyntaxTree.ParseText(string, ...)——Roslyn 的 ParseText 多带默认参数
                //（CSharpParseOptions/path/cancellationToken），故只看首参为 string、取参数最少的；
                // 调用时用 BuildArgs 填默认。
                _parseText = _csharpSyntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "ParseText"
                        && m.GetParameters().Length > 0
                        && m.GetParameters()[0].ParameterType == typeof(string))
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                _compilationCreate = _csharpCompilationType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 4);

                // CSharpCompilation.Emit(Stream, ...)——Emit 也多带默认参数，只看首参为 Stream。
                _emit = _csharpCompilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit"
                        && m.GetParameters().Length > 0
                        && m.GetParameters()[0].ParameterType == typeof(Stream))
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();

                if (_createRef == null || _parseText == null || _compilationCreate == null || _emit == null)
                {
                    _loadError = $"绑定 Roslyn 方法失败：CreateFromFile={_createRef != null}, ParseText={_parseText != null}, Create={_compilationCreate != null}, Emit={_emit != null}";
                    return false;
                }

                _loadSucceeded = true;
                _logger.LogInformation("eval_csharp 编译器已绑定（VS 自带 Roslyn）");
                return true;
            }
            catch (Exception ex)
            {
                _loadError = $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogError(ex, "绑定 Roslyn 编译器失败");
                return false;
            }
        }

        /// <summary>构造 CSharpCompilation：解析脚本 + 引用 VS 已加载程序集 + DLL 输出选项。</summary>
        private object? BuildCompilation(string wrappedCode, out string? error)
        {
            error = null;
            if (_parseText == null || _compilationCreate == null || _metadataReferenceType == null
                || _csharpCompilationOptionsType == null || _outputKindType == null || _syntaxTreeType == null)
            {
                error = "编译器成员未绑定";
                return null;
            }

            object tree;
            try { tree = _parseText.Invoke(null, BuildArgs(_parseText.GetParameters(), wrappedCode))!; }
            catch (Exception ex)
            {
                error = $"ParseText 异常：{ex.GetType().Name}: {ex.Message}";
                return null;
            }

            var trees = Array.CreateInstance(_syntaxTreeType, 1);
            trees.SetValue(tree, 0);

            // 引用：VS 已加载程序集（default context，类型兼容）。首次建后缓存。
            var refs = _cachedRefs as Array ?? BuildReferences(out error);
            if (refs == null) return null;
            _cachedRefs = refs;

            var outputKindValue = Enum.Parse(_outputKindType, "DynamicallyLinkedLibrary");
            object options;
            try
            {
                // CSharpCompilationOptions(OutputKind, ...多个默认参数)——Activator.CreateInstance
                // 按 arg 数找构造器，单 arg 找不到（实际是多参带默认）。反射取首参 OutputKind、参数
                // 最少的构造器，用 BuildArgs 填默认。
                var ctor = _csharpCompilationOptionsType.GetConstructors()
                    .Where(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == _outputKindType)
                    .OrderBy(c => c.GetParameters().Length)
                    .FirstOrDefault();
                if (ctor == null)
                {
                    error = "无 CSharpCompilationOptions(OutputKind) 构造器";
                    return null;
                }
                options = ctor.Invoke(BuildArgs(ctor.GetParameters(), outputKindValue))!;
            }
            catch (Exception ex)
            {
                error = $"构造 CSharpCompilationOptions 异常：{ex.GetType().Name}: {ex.Message}";
                return null;
            }

            try
            {
                return _compilationCreate.Invoke(null, new object[] { "__EvalScript", trees, refs, options });
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                error = $"CSharpCompilation.Create 异常：{real.GetType().Name}: {real.Message}";
                return null;
            }
        }

        /// <summary>构造 MetadataReference[]：VS 已加载程序集（含本程序集，让脚本看到 EvalHost）。</summary>
        private Array BuildReferences(out string? error)
        {
            error = null;
            var locations = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => { try { return a.Location; } catch { return null; } })
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Distinct()
                .ToList();
            // 本程序集（含 EvalHost）必须在引用里。
            var self = typeof(EvalHost).Assembly.Location;
            if (!string.IsNullOrEmpty(self) && !locations.Contains(self)) locations.Add(self);

            var refs = Array.CreateInstance(_metadataReferenceType!, locations.Count);
            int n = 0;
            var refErrors = new List<string>();
            foreach (var path in locations)
            {
                try
                {
                    object? mr = _createRef!.Invoke(null, BuildArgs(_createRef.GetParameters(), path));
                    if (mr != null) { refs.SetValue(mr, n); n++; }
                }
                catch (Exception ex) { refErrors.Add($"{Path.GetFileName(path)}:{ex.GetType().Name}"); }
            }
            if (n == 0)
            {
                error = "无可用 MetadataReference（全部 CreateFromFile 失败）";
                return Array.CreateInstance(_metadataReferenceType!, 0);
            }
            if (n < locations.Count)
            {
                var trimmed = Array.CreateInstance(_metadataReferenceType!, n);
                Array.Copy(refs, trimmed, n);
                refs = trimmed;
            }
            return refs;
        }

        /// <summary>构造反射调用的实参数组：首参用 firstArg，其余填默认（值类型 default、引用类型 null），
        /// 适配 Roslyn 带"默认参数"的方法重载（ParseText/Emit 多有默认参数，反射不自动填默认值）。</summary>
        private static object[] BuildArgs(ParameterInfo[] ps, object firstArg)
        {
            var args = new object[ps.Length];
            args[0] = firstArg;
            for (int i = 1; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                args[i] = t.IsValueType && Nullable.GetUnderlyingType(t) == null
                    ? Activator.CreateInstance(t) : null;
            }
            return args;
        }

        /// <summary>序列化脚本返回值。优先 System.Text.Json（宽松编码器，中文不转义）；
        /// 失败或为空时 fallback ToString。</summary>
        private static string SerializeValue(object? value)
        {
            if (value == null) return "null";
            try
            {
                var json = JsonSerializer.Serialize(value, value.GetType(), McpJson.ReadableOptions);
                if (!string.IsNullOrEmpty(json) && json != "{}") return json;
            }
            catch { /* 复杂/循环对象序列化失败 —— fallback */ }
            return value.ToString() ?? value.GetType().FullName ?? "null";
        }

        /// <summary>幂等空实现。无 native 资源。</summary>
        public void Dispose() { }
    }

    /// <summary>
    /// 注入脚本的 globals。脚本里直接访问其 public 字段：Package 拿 VS 服务、JTF 切 UI 线程、
    /// <see cref="Log"/> 记录输出。必须 public class + public 字段 —— 脚本编译时通过 MetadataReference
    /// 引用本程序集，要能跨程序集看到其类型。
    /// </summary>
    public sealed class EvalHost
    {
        /// <summary>VS package，脚本里 await Package.GetServiceAsync(typeof(...)) 拿任意服务。</summary>
        public readonly AsyncPackage Package;
        /// <summary>JoinableTaskFactory，脚本里 await JTF.SwitchToMainThreadAsync() 切 UI 线程。</summary>
        public readonly JoinableTaskFactory JTF;
        private readonly Action<string> _log;

        public EvalHost(AsyncPackage package, JoinableTaskFactory jtf, Action<string> log)
        {
            Package = package;
            JTF = jtf;
            _log = log;
        }

        /// <summary>记录一行输出，汇入 eval_csharp 返回值的 Output 字段。</summary>
        public void Log(object? msg) => _log(msg?.ToString() ?? "null");
    }

    /// <summary>
    /// eval_csharp 的结果。Output 是脚本里 EvalHost.Log() 的累计输出；ResultJson 是脚本
    /// return 值的 JSON（或 ToString fallback）；Error 非 null 表示编译/运行/超时错误
    /// （仍作成功 CallToolResult 返回，把详情透给 agent 而非笼统 internal_error）。
    /// </summary>
    public sealed record EvalCsharpResult(string Output, string ResultJson, string? Error);
}
#endif
