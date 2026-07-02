using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcpGateway
{
    /// <summary>
    /// Gateway 进程入口：独占 <c>http://127.0.0.1:43210/mcp/</c>，把每个 MCP 客户端
    /// POST 经 <see cref="PipeRouter"/> 透传到目标 VS 实例的 NamedPipe。
    ///
    /// Wave 1：单实例转发。目标 VS 的 PID 从环境变量 <c>VS_MCP_TARGET_PID</c> 读取
    /// ——这是手动/Smoke 验证用的最简发现机制。正式的多实例注册路由表（VS 启动时
    /// 主动 register、Gateway 维护 PID→pipe 映射）是 Wave 2 的工作。
    ///
    /// 打包为 WinExe（<c>OutputType=WinExe</c>），进程不创建控制台窗口
    /// （CREATE_NO_WINDOW 的等价物），对用户完全无感。
    /// </summary>
    public static class Program
    {
        private const int Port = 43210;

        private static async Task<int> Main(string[] args)
        {
            string? pidEnv = Environment.GetEnvironmentVariable("VS_MCP_TARGET_PID");
            if (!int.TryParse(pidEnv, out int targetPid) || targetPid <= 0)
            {
                // WinExe 无控制台，直接退出（Wave 2 的 VS 端 GatewayLauncher 会显式
                // 传入 PID；此分支只在不规范的手动启动时命中）。
                return 2;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var router = new PipeRouter("vs-mcp-" + targetPid);
            await router.ConnectAsync(cts.Token).ConfigureAwait(false);

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/mcp/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                // 端口已被占用 —— 另一个 Gateway 已抢先 bind 成功（设计文档 §抢占式
                // Gateway 拉起：端口绑定即分布式锁）。本进程优雅退出。
                return 3;
            }

            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cts.Token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cts.Token.IsCancellationRequested) { break; }

                _ = HandleRequestAsync(ctx, router, cts.Token);
            }

            return 0;
        }

        private static async Task HandleRequestAsync(HttpListenerContext ctx, PipeRouter router, CancellationToken ct)
        {
            try
            {
                if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 405;
                    ctx.Response.StatusDescription = "Method Not Allowed";
                    ctx.Response.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                // 转发请求头（含 Accept / Mcp-Session-Id / X-VS-Workspace 等），
                // 让 VS 端 / Wave 3 的 Header 匹配可见原始客户端意图。
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? key in ctx.Request.Headers.AllKeys)
                {
                    if (key != null)
                        headers[key] = ctx.Request.Headers[key] ?? string.Empty;
                }

                // SSE 流式响应：状态/Content-Type 固定（VS 端 SDK 永远写 SSE 帧）。
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";

                int status = await router.ForwardAsync(body, headers, ctx.Response.OutputStream, ct).ConfigureAwait(false);
                if (status != 200)
                    ctx.Response.StatusCode = status;

                ctx.Response.Close();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { ctx.Response.Close(); } catch { /* shutdown */ }
            }
            catch
            {
                try { ctx.Response.StatusCode = 502; ctx.Response.Close(); } catch { /* client gone */ }
            }
        }
    }
}
