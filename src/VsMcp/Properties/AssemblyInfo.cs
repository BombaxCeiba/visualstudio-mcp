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

// ProvideBindingRedirection 把底层 polyfill（Span/Memory/Unsafe 等）的
// 0.0.0.0-<bundled> 版本范围重定向到 VSIX 自带的版本。host 的版本可能旧，
// redirect + codeBase 让 CLR 解析到 VSIX 目录的副本。System.Text.Json /
// Microsoft.Extensions.Logging.Abstractions 等引 host SharedAssemblies /
// PrivateAssemblies（Private=false），不打包进 VSIX，无需 redirect。
// Microsoft.Bcl.AsyncInterfaces 提供 IAsyncEnumerable<T>——SymbolFacade 的
// LSP RequestAllAsync 返回值依赖它，System.Threading.Tasks.Extensions 4.5.4
// 不含此类型定义，故单独 redirect。
[assembly: ProvideBindingRedirection(AssemblyName = "System.Buffers", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.0.5.0", NewVersion = "4.0.5.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Memory", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.0.5.0", NewVersion = "4.0.5.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Numerics.Vectors", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.1.6.0", NewVersion = "4.1.6.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Runtime.CompilerServices.Unsafe", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "6.0.3.0", NewVersion = "6.0.3.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Threading.Tasks.Extensions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.2.4.0", NewVersion = "4.2.4.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Bcl.AsyncInterfaces", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.9", NewVersion = "10.0.0.9")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
