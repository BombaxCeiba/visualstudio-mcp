using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using VsMcp.Common;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// PipeFraming 的纯 IO 单元测试 —— 长度前缀 JSON 帧的往返、超限拒绝、
    /// 截断/越界检测、干净 EOF。用 MemoryStream，无需真实 NamedPipe。
    /// </summary>
    public class PipeFramingTests
    {
        [Fact]
        public async Task WriteThenRead_RoundTripsDto()
        {
            using var ms = new MemoryStream();
            var req = new PipeRequest
            {
                Id = "abc",
                Method = "POST",
                Path = "/mcp/",
                Body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
            };

            await PipeFraming.WriteFrameAsync(ms, req, default);
            ms.Position = 0;

            var read = await PipeFraming.ReadFrameAsync<PipeRequest>(ms, default);
            Assert.NotNull(read);
            Assert.Equal("abc", read!.Id);
            Assert.Equal("POST", read.Method);
            Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}", read.Body);
        }

        [Fact]
        public async Task MultipleFrames_StreamAndReadConsecutively()
        {
            using var ms = new MemoryStream();
            await PipeFraming.WriteFrameAsync(ms, new PipeHeartbeat { Pid = 111 }, default);
            await PipeFraming.WriteFrameAsync(ms, new PipeHeartbeat { Pid = 222 }, default);
            ms.Position = 0;

            var a = await PipeFraming.ReadFrameAsync<PipeHeartbeat>(ms, default);
            var b = await PipeFraming.ReadFrameAsync<PipeHeartbeat>(ms, default);

            Assert.Equal(111, a!.Pid);
            Assert.Equal(222, b!.Pid);
        }

        [Fact]
        public async Task ReadFrame_ReturnsDefault_OnCleanEof()
        {
            using var ms = new MemoryStream();
            var read = await PipeFraming.ReadFrameAsync<PipeRequest>(ms, default);
            Assert.Null(read);
        }

        [Fact]
        public async Task ReadFrame_Throws_OnTruncatedBody()
        {
            using var ms = new MemoryStream();
            // 头部声明 12 字节帧体……
            ms.Write(new byte[] { 0, 0, 0, 12 }, 0, 4);
            // ……但只写入了 2 字节（对端在帧中途关闭）。
            ms.Write(new byte[] { (byte)'{', (byte)'}' }, 0, 2);
            ms.Position = 0;

            await Assert.ThrowsAsync<EndOfStreamException>(
                () => PipeFraming.ReadFrameAsync<PipeRequest>(ms, default));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)] // length 为 0 本身就非法；length>Max 同样非法
        public async Task ReadFrame_Throws_OnOutOfRangeLength(int bytesOverZero)
        {
            using var ms = new MemoryStream();
            // 编码一个刚好超过 MaxFrameBytes 的长度以触发越界保护。用精确
            // 边界值可保持测试快速（无需巨大分配）。
            int len = PipeFraming.MaxFrameBytes + 1;
            ms.Write(new byte[]
            {
                (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len,
            }, 0, 4);
            ms.Write(new byte[bytesOverZero], 0, bytesOverZero);
            ms.Position = 0;

            await Assert.ThrowsAsync<InvalidDataException>(
                () => PipeFraming.ReadFrameAsync<PipeRequest>(ms, default));
        }

        [Fact]
        public async Task WriteFrame_RejectsOversizedPayload()
        {
            using var ms = new MemoryStream();
            var huge = new PipeDataChunk { Body = new string('x', PipeFraming.MaxFrameBytes + 1) };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PipeFraming.WriteFrameAsync(ms, huge, default));
        }

        [Fact]
        public void BuildFrameBytes_HasBigEndianLengthHeader()
        {
            byte[] frame = PipeFraming.BuildFrameBytes(new PipeEnd { Id = "x" });

            Assert.True(frame.Length > 4);
            int declared = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            Assert.Equal(frame.Length - 4, declared);
        }

        [Fact]
        public void BuildFrameBytes_RoundTripsViaSynchronousWriteFrame()
        {
            using var ms = new MemoryStream();
            PipeFraming.WriteFrame(ms, new PipeResponseHead { Id = "h1", Status = 200 });

            ms.Position = 0;
            // 同步 WriteFrame 产生的帧必须能被异步读取端接受。
            var head = PipeFraming.ReadFrameAsync<PipeResponseHead>(ms, default).GetAwaiter().GetResult();
            Assert.NotNull(head);
            Assert.Equal("h1", head!.Id);
            Assert.Equal(200, head.Status);
        }
    }
}
