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
    /// Ensures the standalone Gateway exe (<c>VsMcpGateway.exe</c>) is running
    /// before the pipe server tries to connect to it. The Gateway owns
    /// <c>:43210</c> and the <c>vs-mcp-gateway</c> pipe; if it is not up when VS
    /// starts, VS will keep retrying the pipe connect (slow). Pre-launching the
    /// Gateway shortens that window.
    ///
    /// Fire-and-forget semantics: VS never awaits the child Gateway process and
    /// never kills it on VS Dispose (the Gateway is designed to outlive any VS
    /// instance — see 设计文档 §Gateway Lifecycle Management). If the Gateway
    /// cannot be found on disk, this method returns false and the pipe server
    /// still listens; the Gateway may have been started by another VS instance
    /// or by the user.
    /// </summary>
    public static class GatewayLauncher
    {
        private const int GatewayPort = 43210;
        private const int ProbeTimeoutMs = 300;
        private const int RetryAttempts = 10;
        private const int RetryIntervalMs = 500;

        /// <summary>
        /// Resolve the Gateway exe path. Order:
        ///   1. Same directory as this assembly (Wave 5 ships the exe next to
        ///      the package dll in the VSIX).
        ///   2. <c>VS_MCP_GATEWAY_PATH</c> environment variable (manual override).
        /// Returns null if neither yields an existing file.
        /// </summary>
        public static string? ResolveGatewayPath()
        {
            // (1) Same directory as the calling assembly.
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
            catch { /* assembly location resolution can fail in some hosts */ }

            // (2) Environment override.
            try
            {
                string? envPath = Environment.GetEnvironmentVariable("VS_MCP_GATEWAY_PATH");
                if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
                    return envPath;
            }
            catch { /* env access must never throw */ }

            return null;
        }

        /// <summary>
        /// Ensure the Gateway is reachable on :43210, launching it from the
        /// resolved exe path if not. Retries the probe a few times so the
        /// freshly-started Gateway has time to bind the port. Returns true if
        /// the Gateway is reachable when this method exits; false if the Gateway
        /// could not be reached or launched (the pipe server will keep retrying
        /// its own connect, so this is best-effort, not fatal).
        ///
        /// The optional injectables exist so unit tests can drive the decision
        /// without actually spawning a process or opening a socket.
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

            // Fast path: already up.
            if (probe())
                return true;

            string? exePath = pathResolver();
            if (exePath == null)
            {
                // No exe on disk — another VS may have launched it, or the user
                // runs it manually. Give the probe a few retries in case it is
                // mid-startup, but don't attempt to launch.
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

            // The child needs time to bind :43210; retry the probe.
            for (int i = 0; i < RetryAttempts && !cancellationToken.IsCancellationRequested; i++)
            {
                await Task.Delay(RetryIntervalMs, cancellationToken).ConfigureAwait(false);
                if (probe())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Probe whether anything is listening on the Gateway port. Uses a very
        /// short connect timeout so a dead port fails fast (we retry elsewhere).
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
        /// Spawn the Gateway exe hidden (no console window). Returns true if the
        /// process started; false if Start threw. Never awaits the child — the
        /// Gateway runs independently of VS.
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
