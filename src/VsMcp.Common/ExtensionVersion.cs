namespace VsMcp.Common
{
    /// <summary>
    /// 扩展版本号单一来源。VS 端日志、网关端日志都从 <see cref="Current"/> 取，
    /// 避免版本号散落多处、改一处漏一处。VSIX manifest（source.extension.vsixmanifest）
    /// 是 XML，无法引用 C# 常量，其 Identity Version 仍需手动与这里同步。
    /// </summary>
    public static class ExtensionVersion
    {
        /// <summary>当前扩展版本，与 source.extension.vsixmanifest 的 Identity Version 保持一致。</summary>
        public const string Current = "1.36";
    }
}
