using System.Linq;
using System.Reflection;

namespace VsMcp.Common
{
    /// <summary>
    /// 扩展版本号。真相源是 src/Directory.Build.props 的 VsMcpVersion，构建期经
    /// AssemblyInformationalVersion 注入程序集，本类运行时反射读出。VS 端日志、网关端
    /// 日志都从 <see cref="Current"/> 取。VSIX manifest 的 Identity Version 同样由
    /// VsMcpVersion 经 VsMcp.csproj 的 PatchManifestVersion target 构建时注入，故
    /// props / manifest / 代码三处共享同一真相源，不再人工同步。
    /// </summary>
    public static class ExtensionVersion
    {
        /// <summary>当前扩展版本，来自程序集的 AssemblyInformationalVersion
        ///（构建期由 Directory.Build.props 的 VsMcpVersion 注入）。读不到时回退 "0.0.0"。</summary>
        public static string Current { get; } =
            typeof(ExtensionVersion).Assembly
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .Cast<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?
                .InformationalVersion
                .Split('+')[0]
                .Trim()
            ?? "0.0.0";
    }
}
