using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcpGateway
{
    /// <summary>
    /// SSE 感知的流包装器，把 VS 响应实时转发给 HTTP 客户端，同时向第一个
    /// tools/call result 注入一次性 hint。
    /// <para>
    /// 缓冲整个响应（之前的做法）会饿死像 <c>build_solution</c> 这样的长任务：
    /// 它们的 keep-alive 日志通知堆积在缓冲里直到构建结束，客户端看不到任何字节
    /// 于是超时。本做法改为在字节到达时重组每个完整的 SSE 事件（事件以一个空白
    /// <c>\n\n</c> 行结束）：日志通知立即原样透传——保住它们 2s 的 keep-alive
    /// 节奏——只有第一个携带 <c>result.content</c> 数组的事件被前置 hint。其他
    /// 一切逐字转发。
    /// </para>
    /// <para>
    /// 需要按事件边界（而非帧边界）检测，因为 VS 端的 <c>PipeChunkSink</c> 按
    /// SDK 写调用封帧，而一次 SDK 写不保证与一个 SSE 事件对齐。空白行扫描重组
    /// 事件，不管字节原本是怎么分块的。
    /// </para>
    /// </summary>
    internal sealed class HintInjectingStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _hint;
        private bool _injected;
        // 自上一个空白行分隔符以来累积的、尚未形成完整事件的字节。一旦看到完整
        // 事件的尾部 \n\n，就立即 flush（若是第一个 result，则注入 hint），所以
        // keep-alive 通知绝不会被延迟。
        private readonly MemoryStream _pending = new MemoryStream();

        /// <summary>当 hint 成功前置到某个 result 事件后为 true。转发路径只在这时
        /// 才清除 <c>HintPending</c>，所以无 result 的响应（如错误）让该标志保持
        /// 武装，等下一次调用（设计文档 MI-07 一次性语义）。</summary>
        public bool DidInject => _injected;

        public HintInjectingStream(Stream inner, string hint)
        {
            _inner = inner;
            _hint = hint;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        // 空操作：事件在 WriteAsync 内部按其空白行边界 flush。外部的 FlushAsync
        // 调用（如来自 PipeRouter）绝不能把半个事件推出去，因为它无法被注入 hint。
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count <= 0) return;
            _pending.Write(buffer, offset, count);
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            _pending.Write(buffer, offset, count);
            Drain();
        }

        /// <summary>把缓冲里仍残留的尾部字节（从未收到其空白行结束符的事件）逐字
        /// flush 出去——半个事件无法被注入 hint。在转发完成时调用一次。</summary>
        public async Task FlushRemainingAsync(CancellationToken cancellationToken)
        {
            if (_pending.Length == 0) return;
            byte[] tail = _pending.ToArray();
            _pending.SetLength(0);
            await _inner.WriteAsync(tail, 0, tail.Length, cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            while (TryExtractEvent(out byte[] evt))
            {
                byte[] toWrite = evt;
                if (!_injected)
                {
                    var (rewritten, injected) = GatewayTools.InjectHintIntoToolResultSse(evt, _hint);
                    if (injected)
                    {
                        toWrite = rewritten;
                        _injected = true;
                    }
                }
                await _inner.WriteAsync(toWrite, 0, toWrite.Length, ct).ConfigureAwait(false);
                await _inner.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        private void Drain()
        {
            while (TryExtractEvent(out byte[] evt))
            {
                byte[] toWrite = evt;
                if (!_injected)
                {
                    var (rewritten, injected) = GatewayTools.InjectHintIntoToolResultSse(evt, _hint);
                    if (injected)
                    {
                        toWrite = rewritten;
                        _injected = true;
                    }
                }
                _inner.Write(toWrite, 0, toWrite.Length);
                _inner.Flush();
            }
        }

        /// <summary>从 pending 缓冲里弹出一个完整的 SSE 事件（直到并包含空白行）。
        /// 还没有完整事件到达时返回 false，把部分字节留在缓冲里等下一次写。</summary>
        private bool TryExtractEvent(out byte[] evt)
        {
            evt = Array.Empty<byte>();
            if (_pending.Length < 2) return false;
            byte[] data = _pending.ToArray();
            int delim = IndexOfBlankLine(data);
            if (delim < 0) return false;
            int end = delim + 2; // 包含 "\n\n"
            evt = new byte[end];
            Array.Copy(data, 0, evt, 0, end);
            _pending.SetLength(0);
            if (data.Length > end)
                _pending.Write(data, end, data.Length - end);
            return true;
        }

        private static int IndexOfBlankLine(byte[] data)
        {
            for (int i = 0; i < data.Length - 1; i++)
            {
                if (data[i] == (byte)'\n' && data[i + 1] == (byte)'\n')
                    return i;
            }
            return -1;
        }
    }
}
