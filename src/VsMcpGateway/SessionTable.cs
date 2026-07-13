using System;
using System.Collections.Concurrent;
using System.Threading;

namespace VsMcpGateway
{
    /// <summary>
    /// 一个 MCP 客户端会话到 VS 实例的绑定。<see cref="Pid"/> 是路由目标；
    /// <see cref="VsSessionId"/> 是目标 VS 在 initialize 时分配的 Mcp-Session-Id
    ///（这里作为每会话缓存——权威的每 VS 值存放在
    /// <see cref="InstanceEntry.VsSessionId"/>，让无状态的 Header 层路由能恢复它）。
    /// <see cref="VsSessionId"/> 在 <c>select_vs_instance</c> 切换后为 null——新 VS
    /// 尚未 initialize，所以 Wave 2 在该状态下返回"再次 initialize"错误（完整的懒
    /// initialize 是后续增强）。
    /// <see cref="HintPending"/> 由 ③ 自动绑定路径（及 initialize 单实例 fallback）
    /// 设置，门控注入到下一次 tool-call result 的一次性 hint（设计文档 §③ MI-07）。
    /// <see cref="Alive"/> 标记 GET SSE 长连接是否在——只有维持长连接的客户端才计入
    /// <see cref="SessionTable.CountActiveFor"/>（关闭确认弹窗的"正在使用"判定）。这是存活的
    /// **唯一**权威信号，取代不可靠的"上次活动时间"老化（idle 误判 / 关闭漏报，已弃用）。
    /// 不维持 GET 的客户端（不支持 SSE 订阅）Alive 永远 false → 不阻止 VS 退出（功能对其失效）。
    /// </summary>
    public sealed class SessionBinding
    {
        public int Pid { get; set; }
        public string? VsSessionId { get; set; }
        public DateTime BoundAt { get; set; }
        public string Source { get; set; } = "";

        private int _aliveRaw;
        /// <summary>GET SSE 长连接在 = 客户端在线且能接收 server 推送。只有维持 GET 长连接的客户端
        /// 才计入 <see cref="SessionTable.CountActiveFor"/>（关闭确认弹窗的"正在使用"判定）；不维持
        /// GET 的客户端（不支持 SSE 订阅 / 订阅后断开）→ Alive=false → 不阻止 VS 退出——功能对这类
        /// 客户端视为不存在，符合"不阻止用户退出"的设计。</summary>
        public bool Alive
        {
            get => _aliveRaw != 0;
            set => Interlocked.Exchange(ref _aliveRaw, value ? 1 : 0);
        }

        // int 后备 + Interlocked：让 ClaimHint 能原子占有 hint，杜绝多个并发 tools/call 都读到
        // HintPending=true 而重复注入。并发下两个请求都合法看到 true（不是时序竞态，是共享 flag
        // 缺乏互斥），故 claim 必须用 CompareExchange 原子地把 1 翻 0，让其余并发请求拿到 false。
        private int _hintPendingRaw;
        /// <summary>
        /// 自动绑定后为 true，门控注入一次性 hint（设计文档 MI-07）。显式绑定（_meta.vsPid /
        /// select_vs_instance）让此值保持 false，使用户/agent 在刻意选择时永远看不到自动绑定 hint。
        /// </summary>
        public bool HintPending
        {
            get => _hintPendingRaw != 0;
            set => Interlocked.Exchange(ref _hintPendingRaw, value ? 1 : 0);
        }
        /// <summary>原子占有 hint：若当前为 true 则置 false 并返回 true。多个并发请求只一个拿到
        /// true，杜绝重复注入。注入方拿到 true 后若注入失败应归还（HintPending=true）。</summary>
        public bool ClaimHint() => Interlocked.CompareExchange(ref _hintPendingRaw, 0, 1) == 1;

        /// <summary>
        /// 本绑定所键于的客户端可见 session id（<c>sess-...</c>）。在绑定时设置，让
        /// 自动绑定路径能把新铸造的 id 回显给没发 Mcp-Session-Id 的客户端。
        /// </summary>
        public string ClientSessionId { get; set; } = "";

        public SessionBinding(int pid, string? vsSessionId, DateTime boundAt, string source)
        {
            Pid = pid;
            VsSessionId = vsSessionId;
            BoundAt = boundAt;
            Source = source;
        }
    }

    /// <summary>
    /// Gateway 侧的映射：客户端可见的 MCP session id（Gateway 放进 Mcp-Session-Id
    /// 响应 header 的 <c>sess-{Guid:N}</c> 值）→ 实际服务该会话的 VS 实例。这是
    /// 绑定解析流程的 ② Session 层。
    /// </summary>
    public sealed class SessionTable
    {
        private readonly ConcurrentDictionary<string, SessionBinding> _byClientSession =
            new ConcurrentDictionary<string, SessionBinding>(StringComparer.Ordinal);

        /// <summary>
        /// 创建绑定，并返回生成的客户端 session id 和该绑定——调用方（initialize
        /// 处理器）需要该 id 写入响应 header。
        /// </summary>
        public (string ClientSessionId, SessionBinding Binding) CreateWithId(int pid, string? vsSessionId, string source)
        {
            string clientSessionId = "sess-" + Guid.NewGuid().ToString("N");
            var binding = new SessionBinding(pid, vsSessionId, DateTime.UtcNow, source)
            {
                ClientSessionId = clientSessionId,
            };
            _byClientSession[clientSessionId] = binding;
            return (clientSessionId, binding);
        }

        /// <summary>用客户端自带的 session id 建绑定（客户端带 Mcp-Session-Id 但不在表里时，
        /// 替代 <see cref="CreateWithId"/> 生成新 id）。返回 binding + 是否本次新建。
        /// 复用现有 binding 时不改状态（<see cref="SessionBinding.HintPending"/> 已清除的保持清除,
        /// 避免每次重复注入 auto-bind hint——客户端带自己缓存/生成的 id 时的常见情形）。</summary>
        public (SessionBinding Binding, bool Created) GetOrAdd(string clientSessionId, int pid, string? vsSessionId, string source)
        {
            if (_byClientSession.TryGetValue(clientSessionId, out var existing))
                return (existing, false);
            var created = new SessionBinding(pid, vsSessionId, DateTime.UtcNow, source)
            {
                ClientSessionId = clientSessionId,
            };
            // GetOrAdd 处理并发：另一线程可能同时建了同名 binding，此时返回那个、Created=false。
            var actual = _byClientSession.GetOrAdd(clientSessionId, created);
            return (actual, ReferenceEquals(actual, created));
        }

        /// <summary>标记客户端断开（GET keep-alive 写失败 / POST 响应写失败 / DELETE 前置）。返回是否
        /// 状态翻转（Alive true→false）——调用方据此推送连接数（仅翻转时推，避免重复推送）。</summary>
        public bool MarkGone(string clientSessionId)
        {
            if (_byClientSession.TryGetValue(clientSessionId, out var b) && b.Alive)
            {
                b.Alive = false;
                return true;
            }
            return false;
        }

        /// <summary>GET subscribe：客户端打开 SSE 长连接 = 在线。返回是否状态翻转（Alive false→true）
        /// ——调用方据此推送连接数（仅翻转时推）。存活判定靠 GET 长连接，POST 不触发此方法：POST 是
        /// 短暂请求、不维持长连接，若让 POST 复活 Alive，不维持 GET 的客户端就会因发请求而意外阻止
        /// VS 退出，违背"不维持长连接的客户端视为功能不存在"的设计。</summary>
        public bool MarkAlive(string clientSessionId)
        {
            if (_byClientSession.TryGetValue(clientSessionId, out var b) && !b.Alive)
            {
                b.Alive = true;
                return true;
            }
            return false;
        }

        /// <summary>绑到指定 VS 且当前维持 GET SSE 长连接（Alive）的会话数。用于关闭确认弹窗的"正在使用"
        /// 判定——只数维持长连接的客户端；不维持 GET 的客户端（不支持 SSE 订阅 / 已断开）不计入，功能
        /// 对其失效、不阻止退出。不靠时间老化。</summary>
        public int CountActiveFor(int pid)
        {
            int n = 0;
            foreach (var kv in _byClientSession)
            {
                if (kv.Value.Pid == pid && kv.Value.Alive) n++;
            }
            return n;
        }

        public bool TryGet(string clientSessionId, out SessionBinding binding) =>
            _byClientSession.TryGetValue(clientSessionId, out binding!);

        /// <summary>
        /// 把既有客户端会话重新指向另一个 VS 实例（select_vs_instance）。清空
        /// VsSessionId，让下一次非 initialize 请求强制重新 initialize。同时清空
        /// HintPending——刻意的 select 绝不是自动绑定。
        /// </summary>
        public bool Rebind(string clientSessionId, int newPid)
        {
            if (!_byClientSession.TryGetValue(clientSessionId, out var existing))
                return false;
            existing.Pid = newPid;
            existing.VsSessionId = null;
            existing.BoundAt = DateTime.UtcNow;
            existing.Source = "select";
            existing.HintPending = false;
            return true;
        }

        public bool Remove(string clientSessionId) =>
            _byClientSession.TryRemove(clientSessionId, out _);

        /// <summary>当前持有的绑定数。供测试断言 ① Header 层的无状态性（它不得
        /// 创建绑定）。</summary>
        public int Count => _byClientSession.Count;
    }
}
