using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VsMcp.Common;

namespace VsMcpGateway
{
    /// <summary>
    /// 一个已连接的 VS 实例：它的 pipe router（回到该 VS 的多路分解通道）、最新
    /// 注册信息（PID / 解决方案 / 版本），以及我们最后一次看到它活动的时间。
    /// <see cref="LastSeen"/> 在 register、每次 heartbeat、每次 solution-changed
    /// 推送时都会更新；Wave 4 会用它做过期淘汰。
    ///
    /// <see cref="VsSessionId"/> 是 VS 的 SDK 在客户端首次 initialize 到达它时
    /// 分配的权威每 VS Mcp-Session-Id。VS 服务端是单会话，所以每条到本 VS 的路由
    /// ——无论请求是经会话绑定、① Header 层（无状态）还是 ③ 自动绑定层进来——都
    /// 复用这同一个 id。由 Program.cs 在 initialize 时捕获，并同步到发起的
    /// SessionBinding 以方便使用。
    /// </summary>
    public sealed class InstanceEntry
    {
        public PipeRouter Router { get; }
        public PipeRegister Info { get; set; }
        public DateTime LastSeen { get; set; }
        public string? VsSessionId { get; set; }

        /// <summary>本实例最新已知的调试器模式（"design"/"break"/"running"），由
        /// debugger-state-changed 控制帧实时刷新。null = register 后尚未收到推送。</summary>
        public string? DebuggerState { get; set; }

        public InstanceEntry(PipeRouter router, PipeRegister info, DateTime lastSeen)
        {
            Router = router ?? throw new ArgumentNullException(nameof(router));
            Info = info;
            LastSeen = lastSeen;
        }
    }

    /// <summary>
    /// Gateway 侧每个已连接 VS 实例的注册表，以 PID 为键。通过
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> 保证线程安全。HTTP 路由层
    /// 查询本表以解析 initialize 的目标（经 <c>_meta.vsPid</c> 或单实例 fallback）
    /// 并渲染 <c>list_vs_instances</c>。条目的生命周期与其 pipe 连接绑定：当
    /// <see cref="PipeRouter"/> 的读循环观察到断连时，Program.cs 的 accept 循环
    /// 移除该条目。
    /// </summary>
    public sealed class InstanceRegistry
    {
        private readonly ConcurrentDictionary<int, InstanceEntry> _byPid =
            new ConcurrentDictionary<int, InstanceEntry>();

        /// <summary>为某个 PID 插入或替换条目。</summary>
        public void Register(InstanceEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            _byPid[entry.Info.Pid] = entry;
        }

        /// <summary>刷新已知 PID 的 solution/version 信息（heartbeat /
        /// solution-changed）。传入刚推送的 <see cref="PipeSolutionChanged"/>
        /// （或等价的 <see cref="PipeRegister"/>），让路由表对每个 VS 打开的
        /// 解决方案的视图保持实时，无需重连。</summary>
        public void UpdateInfo(int pid, PipeRegister info)
        {
            if (_byPid.TryGetValue(pid, out var existing))
            {
                existing.Info = info;
                existing.LastSeen = DateTime.UtcNow;
            }
        }

        /// <summary>heartbeat 时为已知 PID 刷新 <see cref="InstanceEntry.LastSeen"/>
        /// （Wave 4）。PID 已下线则空操作——来自过期 pipe 的 heartbeat 无害。从
        /// PipeRouter 读循环内联调用，必须快且不抛（ConcurrentDictionary 字段写入
        /// 满足这两点）。Pipe 断连仍是主要的 VS 退出信号（OS 语义）；LastSeen 是
        /// 补充的存活信号，让 ProcessScanner 机制 1 的"空"判断更精确。</summary>
        public void TouchLastSeen(int pid)
        {
            if (_byPid.TryGetValue(pid, out var existing))
                existing.LastSeen = DateTime.UtcNow;
        }

        /// <summary>debugger-state-changed 推送时刷新本实例的 DebuggerState（并更新
        /// LastSeen，与 solution-changed/heartbeat 一致地标志"最近见过"）。PID 已下线
        /// 则空操作。从 PipeRouter 读循环内联调用，必须快且不抛——ConcurrentDictionary
        /// 字段写入满足这两点。</summary>
        public void UpdateDebuggerState(int pid, string? state)
        {
            if (_byPid.TryGetValue(pid, out var existing))
            {
                existing.DebuggerState = state;
                existing.LastSeen = DateTime.UtcNow;
            }
        }

        /// <summary>首次 initialize 到达本 VS 实例时记录 VS 分配的 Mcp-Session-Id。
        /// 每 VS 权威（VS 服务端是单会话）。PID 已下线则空操作。</summary>
        public void SetVsSessionId(int pid, string vsSessionId)
        {
            if (string.IsNullOrEmpty(vsSessionId)) return;
            if (_byPid.TryGetValue(pid, out var existing))
                existing.VsSessionId = vsSessionId;
        }

        public bool TryGet(int pid, out InstanceEntry entry) =>
            _byPid.TryGetValue(pid, out entry!);

        public bool Remove(int pid) => _byPid.TryRemove(pid, out _);

        /// <summary>所有已连接实例的快照——供 list_vs_instances 和单实例 fallback 使用。</summary>
        public IReadOnlyCollection<InstanceEntry> Snapshot() => _byPid.Values.ToArray();

        public int Count => _byPid.Count;
    }
}
