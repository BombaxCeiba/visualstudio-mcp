using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using VsMcp.Common;

namespace VsMcp
{
    /// <summary>
    /// VS 端的 NamedPipe 客户端端：主动连到 Gateway 监听的
    /// <c>\\.\pipe\vs-mcp-gateway</c>，连接建立后先发一帧 <see cref="PipeRegister"/>
    /// （携带 PID + solution info），然后进入请求读循环，把收到的
    /// <see cref="PipeRequest"/> 帧交给共享的 <see cref="McpRequestProcessor"/> 处理。
    ///
    /// Wave 2 反转了 Wave 1 的方向：Wave 1 里 VS 做 server（等 Gateway 连
    /// <c>vs-mcp-{pid}</c>），但多实例下 Gateway 无法枚举 PID 去反向连接每个 VS。
    /// 改为 Gateway 做固定名 server，所有 VS 主动连过来，首帧 register 自报身份。
    ///
    /// Wave 3 增加了 VS 主动推送 <c>solution-changed</c> 控制帧的能力（用户打开/
    /// 关闭解决方案时通知 Gateway 刷新路由表）。该推送与请求-响应帧共用同一条
    /// pipe，因此所有写操作经 <see cref="_sendLock"/> 串行化，防止长度前缀帧在并发
    /// 写下字节交错而损坏协议。
    ///
    /// 连接循环：连 Gateway → 发 register → 服务请求直到断开 → 重连（Gateway
    /// 抢占式重启后 VS 自动重新拨入）。客户端用 <see cref="PipeOptions.None"/> +
    /// <c>Task.Run(() =&gt; Connect(timeout))</c> —— net48 上唯一不挂死的 client
    /// 连接范式（Wave 1 已验证）。VS 端请求处理是串行的（读一帧、处理、回帧），
    /// 无并发读写，故 None handle 安全。
    /// </summary>
    public sealed class PipeMcpServer : IAsyncDisposable, IDisposable
    {
        private const string GatewayPipeName = "vs-mcp-gateway";

        /// <summary>
        /// 发送心跳时等待 send lock 的最长时间。心跳绝不能排队在一处长 SSE 流写入
        /// 之后 —— 若锁被占用这么久，则视为连通性问题并计为一次 miss，而不是拖住
        /// 心跳节奏。
        /// </summary>
        private const int HeartbeatLockTimeoutMs = 2000;

        private readonly int _pid;
        private readonly DebuggerFacade? _facade;
        private readonly McpRequestProcessor _processor;
        private readonly ILoggerFactory? _loggerFactory;

        private CancellationTokenSource? _loopCts;
        private Task? _connectLoop;

        private readonly SemaphoreSlim _disposeLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// 串行化所有 pipe 写入。NamedPipe 流是全双工的（<see cref="ServeConnectionAsync"/>
        /// 中的读从不阻塞写），但两个并发写者会让两个 length-prefixed 帧的字节交错，
        /// 损坏协议。请求-响应循环与 <c>solution-changed</c> 推送都经此锁写入，
        /// 保证帧的原子性。
        /// </summary>
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>
        /// 当前连接到 Gateway 的 pipe；断开/重连期间为 null。每次写入前先捕获到
        /// 局部变量，以免写入中途的重连把一个已 dispose 的流交给封帧调用。
        /// </summary>
        private volatile Stream? _activePipe;

        /// <summary>每次成功 register 后触发，供 solution-events 订阅者重新推送当前
        /// solution。这填补了一个时序缺口：在 pipe 连接之前打开的 solution 会丢失其
        /// OnAfterOpenSolution 事件（当时 pipe 为 null，WriteLockedAsync 静默返回）——
        /// 同时也在重连后重新同步 Gateway。由调用方 fire-and-forget 触发；回调自行
        /// 切到 UI 线程。在 package 通过 <see cref="SetOnConnected"/> 接线之前为 null。
        /// </summary>
        private Func<CancellationToken, Task>? _onConnected;

        /// <summary>注册一个在每次成功 register 帧之后触发的回调，供 solution-events
        /// 订阅者重新推送当前 solution 路径（覆盖 connect 前打开的竞态 + 重连场景）。
        /// 必须在 <see cref="StartAsync"/> 之前设置，这样首次连接才会触发它。</summary>
        public void SetOnConnected(Func<CancellationToken, Task> callback) => _onConnected = callback;

        /// <param name="pid">VS 进程 ID，写入 register 帧。</param>
        /// <param name="facade">调试器 facade；非空时 register 帧携带 solution info。</param>
        /// <param name="symbolFacade">符号 facade。</param>
#if EVAL_CSHARP
        /// <param name="evalFacade">eval_csharp facade；为 null 或 enableEvalCsharp=false 则不注册。</param>
#endif
        /// <param name="enableGoToDefinition">是否注册 go_to_definition（opt-in）。</param>
#if EVAL_CSHARP
        /// <param name="enableEvalCsharp">是否注册 eval_csharp（opt-in）。</param>
#endif
        /// <param name="loggerFactory">可选日志工厂。</param>
        /// <param name="callerToken">VS package 的 DisposalToken；取消时传播到处理器与连接循环。</param>
        public PipeMcpServer(
            int pid,
            DebuggerFacade? facade,
            SymbolFacade? symbolFacade,
#if EVAL_CSHARP
            EvalCsharpFacade? evalFacade,
#endif
            bool enableGoToDefinition,
#if EVAL_CSHARP
            bool enableEvalCsharp,
#endif
            ILoggerFactory? loggerFactory,
            CancellationToken callerToken)
        {
            _pid = pid;
            _facade = facade;
            _loggerFactory = loggerFactory;
            _processor = new McpRequestProcessor(callerToken, facade, symbolFacade,
#if EVAL_CSHARP
                evalFacade,
#endif
                enableGoToDefinition,
#if EVAL_CSHARP
                enableEvalCsharp,
#endif
                loggerFactory);
        }

        /// <summary>
        /// 启动连接循环。循环在后台运行直到 <paramref name="cancellationToken"/>
        /// 取消或 <see cref="DisposeAsync"/> 被调用；方法本身在循环启动后立即返回。
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var logger = _loggerFactory?.CreateLogger<PipeMcpServer>();
            logger?.LogInformation("VS MCP pipe client connecting to \\\\.\\pipe\\{Pipe}", GatewayPipeName);

            _connectLoop = Task.Run(() => ConnectLoopAsync(_loopCts.Token));
            return Task.CompletedTask;
        }

        /// <summary>
        /// 连接循环：反复尝试拨入 Gateway 的 <c>vs-mcp-gateway</c> pipe，连上后
        /// 发 register 帧并服务请求直到连接断开，然后重试（Gateway 还没起 / 重启）。
        /// </summary>
        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            var logger = _loggerFactory?.CreateLogger<PipeMcpServer>();
            while (!ct.IsCancellationRequested)
            {
                NamedPipeClientStream? pipe = null;
                try
                {
                    // 全程使用异步（overlapped）句柄 + 异步 Connect/Read/Write。先前
                    // PipeOptions.None + 异步 ReadAsync/WriteAsync 的组合不一致：None 产生
                    // 非 overlapped 句柄，在其上做异步 IO 实际无法 flush，于是 VS 端连上了
                    // 却始终发不出 register 帧（Gateway 看到的是连接建立后立即 EOF）。
                    // overlapped 句柄也要求异步 Connect —— 在 overlapped 句柄上同步 Connect
                    // 会在 net48 上死锁（Wave 1 的教训）。
                    pipe = new NamedPipeClientStream(
                        ".", GatewayPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                    // 直接 await ConnectAsync()，不加人为超时。net48 的 ConnectAsync 在 worker
                    // thread 上跑同步 Connect()，它在网关 pipe 还没出现时会内部 WaitNamedPipe
                    // 循环、等到管道出现再建连 —— 这正是"等网关起来"的语义。
                    // 之前用 WhenAny(connectTask, Task.Delay(2s)) 加 2s 超时是 bug：net48 上
                    // ConnectAsync 从"底层 CreateFile 建连成功"到"Task 完成 return"之间有延迟，
                    // 实测经常 >2s（connectTask.Status 一直 Running），于是连接实际已建立、却被
                    // 2s 超时打断、pipe 被 Dispose，网关端 accept 到连接却读到 EOF（register 读到
                    // EOF）→ 注册表空 → ProcessScanner 自动退出网关 → 死循环。去掉人为超时让
                    // ConnectAsync 自然完成；VS 退出时 ct 取消会 Dispose pipe，ConnectAsync 随之
                    // 抛异常进入下面的 catch 退出循环。
                    using (ct.Register(() => { try { pipe.Dispose(); } catch { /* 已竞态 */ } }))
                    {
                        await pipe.ConnectAsync().ConfigureAwait(false);
                    }

                    logger?.LogInformation("Connected to gateway pipe {Pipe}", GatewayPipeName);

                    // 发布这个活跃流，供 solution-changed 推送找到它。在 register 之前
                    // 设置，这样 register 与读循环之间若发生一次快速的 open-solution 事件，
                    // 仍有 pipe 可用于发送。
                    _activePipe = pipe;

                    // 先发送 register 帧；若失败，记日志后仍进入请求循环（若 Gateway
                    // 后续恢复连接，仍可按 PID 路由）。
                    await SendRegisterAsync(ct).ConfigureAwait(false);

                    await ServeConnectionAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(pipe);
                    return;
                }
                catch (Exception ex)
                {
                    // Connect 失败（Gateway 尚未起来）或已建立的连接断开 —— 短暂退避后
                    // 重试，让 VS 在 Gateway（重）启动时自动恢复。Information 级别，使
                    // 启动期间的连接问题能显示在 VS 输出窗口（Debug 被包的 Information
                    // 过滤器隐藏）。
                    logger?.LogInformation(ex, "Gateway pipe connect/serve error; will retry");
                }
                finally
                {
                    // 拆除：下一次迭代会创建新流。
                    _activePipe = null;
                    TryDispose(pipe);
                }

                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }

        /// <summary>
        /// 发送 register 帧：PID 来自构造参数，solution info 从 facade 取
        /// （facade.GetSessionInfoAsync 会自己切到 UI 线程；这里直接调用并容忍
        /// 失败 —— solution 还没打开时返回空字段，register 仍要发出去）。
        /// </summary>
        private async Task SendRegisterAsync(CancellationToken ct)
        {
            string? solutionName = null;
            string? solutionDir = null;
            string? solutionPath = null;

            // 不在此取 solution info：GetSessionInfoAsync 会 SwitchToMainThreadAsync 切 UI，
            // 而 ConnectLoop 是 Task.Run 启动的裸后台线程（无 JoinableTask 上下文），从它
            // 切 UI 会卡死，register 帧永远发不出去、Gateway 收不到任何注册。先把 PID
            // 注册上保证连通；solution info 由 solution-changed（IVsSolutionEvents 回调本就
            // 在 UI 线程，安全）补充。
            _loggerFactory?.CreateLogger<PipeMcpServer>()
                ?.LogInformation("SendRegister: sending register frame (pid={Pid})", _pid);

            var register = new PipeRegister
            {
                Pid = _pid,
                PipeName = GatewayPipeName,
                SolutionName = solutionName,
                SolutionDir = solutionDir,
                SolutionPath = solutionPath,
                VsVersion = TryGetVsVersion(),
            };

            try
            {
                await WriteLockedAsync(register, ct).ConfigureAwait(false);

                // register 成功 —— Gateway 现在知道这个 PID 了。重新推送当前 solution，
                // 使 Gateway 的路由表（① Header 层 + list_vs_instances 用的 SolutionDir）
                // 即便在 solution 早于 pipe 连接打开（其 OnAfterOpenSolution 在 _activePipe
                // 为 null 时触发而被丢弃）或 Gateway 弹跳/重连之后仍然正确。fire-and-forget；
                // 回调自行切到 UI 线程。
                if (_onConnected != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await _onConnected(ct).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            _loggerFactory?.CreateLogger<PipeMcpServer>()
                                ?.LogDebug(ex, "onConnected solution re-push threw; ignored");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // Information 级别：register 失败意味着 VS 对 Gateway 尚不可见，正是
                // 用户需要在输出窗口看到以便诊断的那类连接问题。
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogInformation(ex, "Failed to send register frame; continuing");
            }
        }

        private static string TryGetVsVersion()
        {
            // devenv.exe 的产品版本（如 18.7.11925.98）即 VS 版本，任意线程可读，
            // 无需 DTE.Version 的 UI 线程切换。Environment.Version 返回的是 .NET
            // Runtime 版本（4.0.30319.x），不是 VS 版本 —— 之前误用导致
            // list_vs_instances 的 vsVersion 显示成 .NET 运行时版本。
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileVersionInfo?.ProductVersion ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 推送一帧 <c>solution-changed</c> 控制帧，让 Gateway 在其路由表中刷新本
        /// 实例的 SolutionDir（设计文档 §Solution 信息动态更新）。由调用方
        /// （solution-events 订阅者）fire-and-forget 触发；吞掉 IO 错误，使短暂的
        /// Gateway 断连不会让 UI 线程崩溃。下一次 register（重连时）会带上最新信息，
        /// 因此被丢弃的推送可自愈。当前未连接（<see cref="_activePipe"/> == null）时
        /// 为 no-op。
        /// </summary>
        public async Task SendSolutionChangedAsync(
            string? solutionPath, string? solutionDir, string? solutionName, CancellationToken ct)
        {
            if (_disposed) return;
            try
            {
                var frame = new PipeSolutionChanged
                {
                    Pid = _pid,
                    SolutionName = solutionName,
                    SolutionDir = solutionDir,
                    SolutionPath = solutionPath,
                };
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogInformation(
                        "SendSolutionChanged: pid={Pid} path={Path} dir={Dir} name={Name}",
                        _pid, solutionPath ?? "(null)", solutionDir ?? "(null)", solutionName ?? "(null)");
                await WriteLockedAsync(frame, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogDebug(ex, "Failed to send solution-changed frame");
            }
        }

        /// <summary>
        /// 发送一帧 <c>heartbeat</c> 控制帧（无 id），让 Gateway 知道本 VS 还活着；
        /// 同样重要的是，让 VS 端的 <see cref="HeartbeatClient"/> 能确认 pipe 仍可写
        /// （即 Gateway 进程仍在运行）。任何失败都抛异常（无活跃 pipe、写入错误、
        /// send-lock 竞争），以便 HeartbeatClient 累计连续 miss；不记日志，因为 Gateway
        /// 重启期间心跳 miss 是预期行为，每次 miss 都打日志会刷屏输出窗口。
        /// </summary>
        public async Task SendHeartbeatAsync(CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PipeMcpServer));

            Stream? pipe = _activePipe;
            if (pipe == null)
                throw new IOException("Not connected to the gateway pipe.");

            // 用短暂超时获取 send lock，而非无限等待：排队在一处长 SSE 流写入之后的
            // 心跳会扭曲存活信号。此处的超时表现为一次 miss，由 HeartbeatClient 优雅
            // 处理。
            if (!await _sendLock.WaitAsync(HeartbeatLockTimeoutMs, ct).ConfigureAwait(false))
                throw new TimeoutException("Timed out waiting for the pipe send lock to send heartbeat.");

            try
            {
                await PipeFraming.WriteFrameAsync(pipe, new PipeHeartbeat { Pid = _pid }, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>在 <see cref="_sendLock"/> 保护下写入一帧（异步路径）。</summary>
        private async Task WriteLockedAsync(object payload, CancellationToken ct)
        {
            Stream? pipe = _activePipe;
            if (pipe == null) return;
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PipeFraming.WriteFrameAsync(pipe, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>在 <see cref="_sendLock"/> 保护下写入一帧（同步路径，由 SDK 的
        /// 同步 Stream.Write 调用经 chunk sink 使用）。</summary>
        private void WriteLocked(object payload)
        {
            Stream? pipe = _activePipe;
            if (pipe == null) return;
            _sendLock.Wait();
            try
            {
                PipeFraming.WriteFrame(pipe, payload);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 服务一个 Gateway 连接：读帧循环，按 type 分派。只处理 <c>request</c>；
        /// 未知帧类型被忽略以保持向前兼容。循环在干净断开（ReadFrameJson 返回
        /// null）或 shutdown 时退出，交还控制权给 ConnectLoop 等待重连。
        /// </summary>
        private async Task ServeConnectionAsync(NamedPipeClientStream pipe, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? json;
                try
                {
                    json = await PipeFraming.ReadFrameJsonAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 截断帧 / IO 错误 → 视为连接丢失，交给 ConnectLoop 重试
                    // （Gateway 崩溃、taskkill 等）。Information 级别，使断连在诊断
                    // 时可见。
                    _loggerFactory?.CreateLogger<PipeMcpServer>()
                        ?.LogInformation(ex, "Pipe frame read failed; treating as disconnect");
                    return;
                }

                if (json == null)
                    return; // 干净断开（Gateway 关闭了 pipe）

                string type;
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        type = doc.RootElement.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // 帧格式错误 —— 跳过它而非拆除连接。
                    continue;
                }

                if (string.Equals(type, "request", StringComparison.Ordinal))
                {
                    PipeRequest? req;
                    try { req = System.Text.Json.JsonSerializer.Deserialize<PipeRequest>(json, PipeFraming.Options); }
                    catch (System.Text.Json.JsonException) { continue; }

                    if (req != null)
                    {
                        // 记录每条入站 MCP 请求体，使连接/调用问题能在 VS 输出窗口
                        // 可见 —— 包的 logger factory 运行在 Information 级别。
                        _loggerFactory?.CreateLogger<PipeMcpServer>()
                            ?.LogInformation("MCP request {Id}: {Body}", req.Id, req.Body);
                        await HandleRequestAsync(pipe, req, ct).ConfigureAwait(false);
                    }
                }
                // 其他帧类型（register 回显 / heartbeat）被静默忽略，这样新老对端
                // 配对绝不会打断流。
            }
        }

        /// <summary>
        /// 处理一条转发请求：发 head → 处理 JSON-RPC（SSE 经 chunkSink 实时封帧）→ 发 end。
        /// head 中的状态码反映 body 是否可解析。
        /// </summary>
        private async Task HandleRequestAsync(NamedPipeClientStream pipe, PipeRequest req, CancellationToken ct)
        {
            JsonRpcMessage? message = null;
            bool parsed = true;
            try
            {
                message = System.Text.Json.JsonSerializer.Deserialize<JsonRpcMessage>(req.Body, McpJsonUtilities.DefaultOptions);
                if (message == null) parsed = false;
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = false;
            }

            var head = new PipeResponseHead
            {
                Id = req.Id,
                Status = parsed ? 200 : 400,
                Headers =
                {
                    ["Content-Type"] = "text/event-stream",
                    ["Cache-Control"] = "no-cache",
                },
            };
            await WriteLockedAsync(head, ct).ConfigureAwait(false);

            // chunkSink 把每次 SDK SSE 写入转成实时 data 帧，使 build_solution 的
            // 保活通知不经缓冲即到达客户端。在这里创建（而不仅在解析成功路径），
            // 以便下面的响应日志在两个分支都有有效的字节计数。
            var sink = new PipeChunkSink(this, req.Id);

            if (!parsed)
            {
                await WriteLockedAsync(new PipeEnd { Id = req.Id }, ct).ConfigureAwait(false);
                LogResponse(req.Id, head.Status, sink.WrittenBytes);
                return;
            }

            try
            {
                await _processor.HandleAsync(message!, sink, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用中途停机 —— 发出 end 让 Gateway 干净地拆除。
            }
            catch (Exception ex)
            {
                // 工具级异常已在处理器内部（SafeCall）转成 error 结果；走到这里说明
                // 是传输层故障。
                _loggerFactory?.CreateLogger<PipeMcpServer>()
                    ?.LogError(ex, "Unexpected error handling pipe request {Id}", req.Id);
            }

            await WriteLockedAsync(new PipeEnd { Id = req.Id }, ct).ConfigureAwait(false);
            LogResponse(req.Id, head.Status, sink.WrittenBytes);
        }

        /// <summary>
        /// 记录每次调用的响应摘要。抽出此方法，使 <see cref="HandleRequestAsync"/> 中
        /// 的解析成功与解析失败两条路径都输出一行响应，给每个请求一条配对的响应轨迹。
        /// </summary>
        private void LogResponse(string id, int status, long bytes)
        {
            // 响应摘要：SSE body 以分片形式流式写入 sink，因此记录聚合字节数 + HTTP
            // 状态，而非每个分片。这与 ServeConnectionAsync 中的请求日志配对，在 VS
            // 输出窗口给出每次调用的轨迹。
            _loggerFactory?.CreateLogger<PipeMcpServer>()
                ?.LogInformation("MCP response {Id}: status {Status}, body bytes {Bytes}", id, status, bytes);
        }

        /// <summary>
        /// 把 SDK 写入的 SSE 字节流实时转成 <see cref="PipeDataChunk"/> 帧的适配 Stream。
        /// 每次写入立即封帧发出（不缓冲），保证长任务的保活通知不被吞掉。字节以 base64
        /// 承载，规避 SDK 任意写入粒度下 UTF-8 多字节字符跨块边界的解码问题。
        /// 写入经所属 <see cref="PipeMcpServer.WriteLocked(object)"/> 串行化，与
        /// solution-changed 推送互不交错。
        /// </summary>
        private sealed class PipeChunkSink : Stream
        {
            private readonly PipeMcpServer _owner;
            private readonly string _id;

            // 统计 SDK 写入的原始（base64 前）body 字节数，以便 HandleRequestAsync
            // 按每次 MCP 调用记录响应大小摘要。SSE 响应以分片流式传输；记录总字节数
            // （而非分片）给出一个有意义的数字。
            private long _written;

            public PipeChunkSink(PipeMcpServer owner, string id)
            {
                _owner = owner;
                _id = id;
            }

            /// <summary>流经此 sink 的原始 body 总字节数。</summary>
            public long WrittenBytes => Interlocked.Read(ref _written);

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                _owner.WriteLocked(new PipeDataChunk
                {
                    Id = _id,
                    Body = Convert.ToBase64String(buffer, offset, count),
                });
                Interlocked.Add(ref _written, count);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (count <= 0) return;
                await _owner.WriteLockedAsync(new PipeDataChunk
                {
                    Id = _id,
                    Body = Convert.ToBase64String(buffer, offset, count),
                }, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _written, count);
            }
        }

        /// <summary>
        /// 同步停机入口：翻转 _disposed、取消连接循环，fire-and-forget 异步销毁。
        /// 绝不阻塞调用线程（VS exit 时 package 同步调用此路径）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _loopCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _processor.Dispose(); } catch { /* 停机期间绝不能抛异常 */ }
        }

        /// <summary>
        /// 异步停机：取消连接循环 → 观察循环退出 → dispose 处理器（SDK loop + transport）。
        /// 幂等（_disposeLock + _disposed）。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            await _disposeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;
                _disposed = true;

                try { _loopCts?.Cancel(); } catch (ObjectDisposedException) { }

                if (_connectLoop != null)
                {
#pragma warning disable VSTHRD003
                    try { await _connectLoop.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { /* 停机期间绝不能抛异常 */ }
#pragma warning restore VSTHRD003
                }

                try { await _processor.DisposeAsync().ConfigureAwait(false); }
                catch { /* 停机期间绝不能抛异常 */ }

                _loopCts?.Dispose();
            }
            finally
            {
                _disposeLock.Release();
            }
        }

        private static void TryDispose(IDisposable? disposable)
        {
            try { disposable?.Dispose(); } catch { /* 尽力而为 */ }
        }
    }
}
