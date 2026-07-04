using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcp
{
    /// <summary>
    /// 确保 standalone Gateway exe（<c>VsMcpGateway.exe</c>）在 pipe server 尝试
    /// 连接它之前已运行。Gateway 持有 <c>:43210</c> 与 <c>vs-mcp-gateway</c>
    /// pipe；若 VS 启动时它未起来，VS 会持续重试 pipe 连接（慢）。预拉起
    /// Gateway 以缩短该窗口。
    ///
    /// fire-and-forget 语义：VS 从不 await 子 Gateway 进程，也绝不在 VS Dispose
    /// 时杀它（Gateway 设计为比任何 VS 实例都长寿——见设计文档 §Gateway
    /// Lifecycle Management）。若磁盘上找不到 Gateway，本方法返回 false，pipe
    /// server 仍会监听；Gateway 可能已被另一个 VS 实例或用户启动。
    /// </summary>
    public static class GatewayLauncher
    {
        private const int GatewayPort = 43210;
        private const int ProbeTimeoutMs = 300;
        private const int RetryAttempts = 10;
        private const int RetryIntervalMs = 500;

        /// <summary>
        /// 解析 Gateway exe 路径。顺序：
        ///   1. 本程序集同目录（Wave 5 把 exe 与 package dll 一起放在 VSIX 里）。
        ///   2. <c>VS_MCP_GATEWAY_PATH</c> 环境变量（手动覆盖）。
        /// 两者都不指向已存在文件时返回 null。
        /// </summary>
        public static string? ResolveGatewayPath()
        {
            // (1) 调用程序集同目录。
            try
            {
                string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    string candidate = Path.Combine(dir, "VsMcpGateway.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { /* 某些 host 下程序集定位解析会失败 */ }

            // (2) 环境变量覆盖。
            try
            {
                string? envPath = Environment.GetEnvironmentVariable("VS_MCP_GATEWAY_PATH");
                if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
                    return envPath;
            }
            catch { /* env 访问绝不能抛 */ }

            return null;
        }

        /// <summary>
        /// 确保 Gateway 在 :43210 可达，若不可达则从解析到的 exe 路径启动它。
        /// 重试探测几次，让刚启动的 Gateway 有时间绑定端口。本方法退出时若
        /// Gateway 可达则返回 true；若无法到达或启动则返回 false（pipe server
        /// 会持续重试自己的连接，故此处为尽力而为，非致命）。
        ///
        /// 可选注入项的存在使单元测试能驱动决策而无需真正 spawn 进程或开
        /// socket。
        /// </summary>
        public static async Task<bool> EnsureGatewayRunningAsync(
            CancellationToken cancellationToken,
            Func<string?>? pathResolver = null,
            Func<bool>? probe = null,
            Func<string, bool>? launcher = null)
        {
            pathResolver ??= ResolveGatewayPath;
            probe ??= DefaultProbe;
            launcher ??= DefaultLaunch;

            // 快速路径：已经起来了。
            if (probe())
                return true;

            string? exePath = pathResolver();
            if (exePath == null)
            {
                // 磁盘上没有 exe——可能另一个 VS 已拉起它，或用户手动运行。
                // 给探测几次重试，以防它正启动到一半，但不尝试启动。
                for (int i = 0; i < RetryAttempts && !cancellationToken.IsCancellationRequested; i++)
                {
                    await Task.Delay(RetryIntervalMs, cancellationToken).ConfigureAwait(false);
                    if (probe())
                        return true;
                }
                return false;
            }

            if (!launcher(exePath))
                return false;

            // 子进程需要时间绑定 :43210；重试探测。
            for (int i = 0; i < RetryAttempts && !cancellationToken.IsCancellationRequested; i++)
            {
                await Task.Delay(RetryIntervalMs, cancellationToken).ConfigureAwait(false);
                if (probe())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 探测 Gateway 端口上是否有东西在监听。用很短的连接超时，使死端口
        /// 快速失败（我们在别处重试）。
        /// </summary>
        private static bool DefaultProbe()
        {
            try
            {
                using var client = new TcpClient();
                if (!client.ConnectAsync("127.0.0.1", GatewayPort).Wait(ProbeTimeoutMs))
                    return false;
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 隐藏地 spawn Gateway exe（无控制台窗口）。进程启动则返回 true；
        /// Start 抛异常则返回 false。从不 await 子进程——Gateway 独立于 VS
        /// 运行。
        /// </summary>
        private static bool DefaultLaunch(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var p = Process.Start(psi);
                return p != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
