using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

[assembly: AssemblyTitle("VS MCP")]
[assembly: AssemblyDescription("VS MCP - Embeds an MCP server exposing Visual Studio capabilities (build, debugger, symbol navigation) to AI agents")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("VS MCP")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("e8f2a4b6-1c3d-4e5f-8a7b-9c0d2e4f6a8b")]
// Emit a CodeBase entry in the generated pkgdef so VS can locate this
// package assembly in the extension install folder. The assembly is now
// strong-named, so the codeBase entry carries a real publicKeyToken and
// loads cleanly even though it sits outside the VS application base.
[assembly: ProvideCodeBase]

// 架构反转后 VS 包的所有 polyfill（System.Text.Json / Microsoft.Bcl.AsyncInterfaces /
// System.Memory / System.Buffers / System.Runtime.CompilerServices.Unsafe /
// System.Numerics.Vectors / System.Threading.Tasks.Extensions / System.Text.Encodings.Web /
// Microsoft.Extensions.Logging.Abstractions）都引 host PublicAssemblies（Private=false，
// 不 copy-local、不打包进 VSIX），运行时直接用 host 加载的版本。host 自身已对这些
// polyfill 做 bindingRedirect（VS 扩展生态共用，range 宽），VS 包无需再
// ProvideBindingRedirection——旧 redirect 的 NewVersion 指向 VSIX 自带副本版本，现在
// VSIX 不带副本（Private=false），CreatePkgDef 处理 redirect 时加载 $(TargetDir) 副本
// 读版本会失败（FileNotFound），且运行时 redirect 指向不存在的版本致 FileLoadException。
// 唯一例外 System.Text.Json 引 NuGet 10.0.0（assembly 10.0.0.0 ≤ 18.5.2 host redirect
// range 10.0.0.2），运行时 host redirect 10.0.0.0→host 版本，亦无需 VS 包 redirect。
// ProvideCodeBase 保留：VsMcp.dll 自身的 codeBase 条目（VSIX 安装目录定位）。
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
