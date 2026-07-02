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

// The extension bundles newer NuGet versions of the Microsoft.Extensions.* /
// Microsoft.Bcl.* infrastructure assemblies (pulled in by ModelContextProtocol),
// while VS2026 ships its own older copies (10.0.0.2) in its Private/Public
// probing paths. Without redirection the CLR may bind the extension's
// higher-versioned ref (e.g. 10.0.0.9) against VS's 10.0.0.2 copy and fail with
// REF_DEF_MISMATCH (0x80131040). Each ProvideBindingRedirection below pins the
// whole 0.0.0.0-<bundled> range onto the version we actually ship in the VSIX
// and, via the default GenerateCodeBase=true, emits a codeBase entry pointing at
// $PackageFolder$\<asm>.dll. The CLR therefore resolves these to the extension's
// own matching copies sitting next to VsMcp.dll, sidestepping VS's
// older runtime copies entirely.
//
// NOTE on version numbers: NewVersion is the version each bundled DLL carries
// (verified from the built bin), NOT VS2026's 10.0.0.2. CreatePkgDef rejects a
// redirect whose OldVersionUpperBound exceeds NewVersion ("OldVersion 高于
// NewVersion"), and a CLR binding redirect cannot map a higher-versioned request
// onto a lower-versioned assembly anyway, so redirecting to 10.0.0.2 is neither
// buildable nor semantically valid. Pinning to the bundled version is the
// correct resolution.
// ProvideBindingRedirection is [AttributeUsage(AllowMultiple = true)].
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Bcl.AsyncInterfaces", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.9", NewVersion = "10.0.0.9")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Bcl.Memory", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.DependencyInjection.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Logging.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Options", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Primitives", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Configuration.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Hosting.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Diagnostics.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.FileProviders.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.Caching.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "Microsoft.Extensions.AI.Abstractions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.4.0.0", NewVersion = "10.4.0.0")]

// Same binding-redirect + codeBase treatment for the remaining assemblies bundled
// in the VSIX (every dependency pulled in by ModelContextProtocol that the
// extension ships next to VsMcp.dll). The extension install folder is
// not on the CLR's probing path (PrivatePath is NULL), so without a codeBase
// entry pointing back at $PackageFolder$\<asm>.dll the CLR cannot locate these
// copies and fails with FileNotFoundException at server start (e.g.
// ModelContextProtocol.Core 1.2.0.0). NewVersion == OldVersionUpperBound ==
// the version each built DLL actually carries.
[assembly: ProvideBindingRedirection(AssemblyName = "ModelContextProtocol", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "1.2.0.0", NewVersion = "1.2.0.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "ModelContextProtocol.Core", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "1.2.0.0", NewVersion = "1.2.0.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Net.ServerSentEvents", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Threading.Channels", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Text.Json", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.9", NewVersion = "10.0.0.9")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Text.Encodings.Web", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.9", NewVersion = "10.0.0.9")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.IO.Pipelines", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.9", NewVersion = "10.0.0.9")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Buffers", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.0.5.0", NewVersion = "4.0.5.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Memory", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.0.5.0", NewVersion = "4.0.5.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Numerics.Vectors", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.1.6.0", NewVersion = "4.1.6.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Runtime.CompilerServices.Unsafe", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "6.0.3.0", NewVersion = "6.0.3.0")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Diagnostics.DiagnosticSource", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "10.0.0.5", NewVersion = "10.0.0.5")]
[assembly: ProvideBindingRedirection(AssemblyName = "System.Threading.Tasks.Extensions", OldVersionLowerBound = "0.0.0.0", OldVersionUpperBound = "4.2.4.0", NewVersion = "4.2.4.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
