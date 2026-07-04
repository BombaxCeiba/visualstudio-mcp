using System;
using System.IO;
using System.Threading;

namespace VsMcpGateway
{
    /// <summary>
    /// 朴素的文件日志，写到 <c>~/.vs-mcp/gateway.log</c>（<c>%USERPROFILE%\.vs-mcp</c>）。
    /// Gateway 是无控制台的后台进程，没有日志时连接/路由/僵尸等问题全靠猜，
    /// 此类把关键事件（启动、pipe accept/register、路由、select、错误）落到磁盘。
    /// 线程安全：单把锁串行化追加。Gateway 事件频率低，每次 <c>File.AppendAllText</c>
    /// 的 open/write/close 开销可忽略，换来的是无需管理 writer 生命周期与缓冲刷新。
    /// 任何写入异常都被吞掉——日志绝不能拖垮 Gateway。
    /// </summary>
    internal static class GatewayLogger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vs-mcp");
        private static readonly string LogPath = Path.Combine(LogDir, "gateway.log");
        private static readonly object Lock = new object();

        /// <summary>追加一行日志。message 已含上下文；调用方不必自己拼时间戳。</summary>
        public static void Log(string message)
        {
            try
            {
                string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [t{Thread.CurrentThread.ManagedThreadId}] {message}{Environment.NewLine}";
                lock (Lock)
                {
                    Directory.CreateDirectory(LogDir);
                    File.AppendAllText(LogPath, line);
                }
            }
            catch
            {
                // 日志失败绝不能影响 Gateway 运行。
            }
        }

        /// <summary>记录异常（类型 + 消息），用于 catch 块。</summary>
        public static void Log(string context, Exception ex) =>
            Log($"{context}: {ex.GetType().Name}: {ex.Message}");
    }
}
