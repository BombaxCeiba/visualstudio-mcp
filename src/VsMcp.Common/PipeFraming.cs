using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcp.Common
{
    /// <summary>
    /// 长度前缀 JSON 帧的读写，承载 Gateway 与 VS 实例之间的全部控制流。
    ///
    /// 每帧 = 4 字节 big-endian payload length + UTF-8 JSON payload。payload
    /// 上限 <see cref="MaxFrameBytes"/>（与 MCP HTTP body 上限一致），防止一个
    /// 失控的对端用超大 length 头耗尽接收方内存。读侧在 length 越界时抛
    /// <see cref="InvalidDataException"/>，在帧体被截断时抛
    /// <see cref="EndOfStreamException"/>，区分"协议错误"与"连接关闭"。
    /// </summary>
    public static class PipeFraming
    {
        /// <summary>
        /// 单帧 payload 字节上限（10MB）。与历史 HTTP 监听端的 POST body 上限
        /// 对齐 —— 任何合法的 JSON-RPC 消息或 SSE 批次都应远小于此值。
        /// </summary>
        public const int MaxFrameBytes = 10 * 1024 * 1024;

        private const int HeaderBytes = 4;

        // 协议统一 camelCase：DTO 的 PascalCase 属性（Type/Id/Body/...）序列化为
        // type/id/body/...，与所有解析端（PipeRouter/PipeMcpServer 的
        // TryGetProperty("type")）一致。不设此项时 System.Text.Json 默认保留属性原名
        // （"Type"），与解析端的小写 "type" 不匹配，帧被静默跳过、读到 end 永不命中
        // —— 曾表现为 ForwardAsync 死循环卡死。
        // Public so callers that hold a raw frame JSON string (e.g. PipeMcpServer
        // after dispatching on "type" via JsonDocument) can deserialize a DTO with
        // the identical policy — a bare JsonSerializer.Deserialize<T>(json) would
        // fall back to PascalCase and silently bind nothing on camelCase frames.
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// 构造一帧的完整字节（4 字节 BE length + UTF-8 JSON payload）。供异步与同步
        /// 写入路径复用，避免 framing 逻辑重复；也供需要一次性拿到帧字节的测试使用。
        /// </summary>
        /// <exception cref="InvalidOperationException">payload 序列化后超过上限。</exception>
        public static byte[] BuildFrameBytes(object payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), Options);
            if (body.Length > MaxFrameBytes)
            {
                throw new InvalidOperationException(
                    $"Frame payload {body.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
            }

            byte[] frame = new byte[HeaderBytes + body.Length];
            frame[0] = (byte)(body.Length >> 24);
            frame[1] = (byte)(body.Length >> 16);
            frame[2] = (byte)(body.Length >> 8);
            frame[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, frame, HeaderBytes, body.Length);
            return frame;
        }

        /// <summary>同步写一帧（供 SDK 偶发的同步 Stream.Write 路径使用）。</summary>
        public static void WriteFrame(Stream stream, object payload)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            byte[] frame = BuildFrameBytes(payload);
            // 不调用 Flush —— NamedPipe 写入即对端可见，无需 flush；且 .NET Framework
            // 的 PipeStream.Flush 行为不稳定。需要 flush 的传输（HTTP）由调用方负责。
            stream.Write(frame, 0, frame.Length);
        }

        /// <summary>
        /// 异步写一帧：4 字节 BE length + UTF-8 JSON payload。
        /// </summary>
        public static async Task WriteFrameAsync(Stream stream, object payload, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            byte[] frame = BuildFrameBytes(payload);
            // 不调用 FlushAsync —— 见 WriteFrame 的说明。NamedPipe 无需 flush；
            // PipeStream.FlushAsync 在 .NET Framework 上会阻塞/抛，曾导致 Gateway↔VS
            // 双向写读死锁。
            await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 读一帧并反序列化为 <typeparamref name="T"/>。返回 default(T) 表示
        /// 对端在帧起始处干净关闭（对 Gateway/VS 退出探测至关重要）。
        /// </summary>
        /// <exception cref="InvalidDataException">length 头越界。</exception>
        /// <exception cref="EndOfStreamException">帧体被截断（对端在写中途断开）。</exception>
        public static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct)
        {
            byte[]? body = await ReadFrameBodyAsync(stream, ct).ConfigureAwait(false);
            if (body == null)
                return default;

            return JsonSerializer.Deserialize<T>(body, Options);
        }

        /// <summary>
        /// 读一帧的原始 UTF-8 JSON 文本，供调用方先用
        /// <see cref="JsonDocument"/> 检视 <c>type</c> 字段再决定反序列化目标类型。
        /// 返回 null 表示对端干净关闭。
        /// </summary>
        public static async Task<string?> ReadFrameJsonAsync(Stream stream, CancellationToken ct)
        {
            byte[]? body = await ReadFrameBodyAsync(stream, ct).ConfigureAwait(false);
            if (body == null)
                return null;

            return Encoding.UTF8.GetString(body);
        }

        /// <summary>
        /// 读帧体字节。null = 干净 EOF（对端在尚未开始下一帧时关闭）。帧体截断
        /// 抛 <see cref="EndOfStreamException"/> —— 这是半写的损坏状态，不能与
        /// 正常关闭混淆。
        /// </summary>
        private static async Task<byte[]?> ReadFrameBodyAsync(Stream stream, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            byte[] header = new byte[HeaderBytes];
            int headerRead = await ReadAtMostAsync(stream, header, HeaderBytes, ct).ConfigureAwait(false);
            if (headerRead == 0)
                return null; // clean EOF before any frame byte
            if (headerRead < HeaderBytes)
                throw new EndOfStreamException(
                    $"Frame header truncated: read {headerRead} of {HeaderBytes} bytes.");

            int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (length <= 0 || length > MaxFrameBytes)
                throw new InvalidDataException(
                    $"Frame length {length} is out of range (1..{MaxFrameBytes}).");

            byte[] body = new byte[length];
            int bodyRead = await ReadExactAsync(stream, body, length, ct).ConfigureAwait(false);
            if (bodyRead < length)
                throw new EndOfStreamException(
                    $"Frame body truncated: read {bodyRead} of {length} bytes.");

            return body;
        }

        /// <summary>
        /// 精确读取 <paramref name="count"/> 字节；返回实际读取量（可能少于 count，
        /// 当对端提前关闭时）。调用方据此区分干净 EOF 与截断。
        /// </summary>
        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0)
                    break; // peer closed mid-frame
                offset += read;
            }
            return offset;
        }

        /// <summary>
        /// 读取至多 <paramref name="count"/> 字节（单次 Read 语义），用于探测帧头
        /// 前的干净 EOF。返回 0 表示对端在调用瞬间已无数据可读且已关闭。
        /// </summary>
        private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0)
                    return offset; // EOF at whatever we have so far (0 on first call)
                offset += read;
            }
            return offset;
        }
    }
}
