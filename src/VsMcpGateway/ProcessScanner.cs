using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcpGateway
{
    /// <summary>
    /// Gateway 自终止看门狗。两套独立机制决定 Gateway 何时退出，让它绝不致在
    /// 所有 Visual Studio 实例都消失后仍作为孤儿进程残留（设计文档 §Gateway 自杀
    /// 双重防护）：
    /// <list type="number">
    /// <item><b>被动——注册表清空。</b>一旦某个 VS 连接过然后其 pipe 断开
    /// （干净的 VS 退出会清空注册表），立即自终止——断连是显式退出信号，无宽限。
    /// <c>emptyGracePeriod</c>（生产 30s）只守护首个 VS 连接前的启动窗口，让刚
    /// 拉起的 Gateway 不会在任何 VS 到来前就被杀。</item>
    /// <item><b>主动——devenv 扫描。</b>每个 <c>scanInterval</c>（生产 2s）轮询
    ///     一次进程表找 <c>devenv.exe</c>。若连续 <c>noVsGracePeriod</c>（生产 10s）
    ///     都没有存活的，自终止。devenv 重新出现会取消倒计时。这能兜住强杀/崩溃
    ///     场景——那种场景下 pipe 从未干净关闭，否则残留的注册表条目会让机制 1
    ///     失效。</item>
    /// </list>
    /// 任一机制触发都精确调用一次 <c>onSelfKill</c>（生产：
    /// <c>Environment.Exit(0)</c>——见 Program.cs 说明为何无法用优雅 cancel 回卷
    /// HTTP accept 循环）。
    /// <para>
    /// 每个计时器都可注入（生产=秒，测试=毫秒），devenv 探测是
    /// <c>Func&lt;bool&gt;</c>，让测试绝不触碰真实进程表——静态
    /// <see cref="Process.GetProcessesByName"/> 无法直接 mock。
    /// </para>
    /// </summary>
    public sealed class ProcessScanner : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly Func<bool> _anyDevenvAlive;
        private readonly Func<IReadOnlyCollection<int>?>? _aliveDevenvPids;
        private readonly Action _onSelfKill;
        private readonly TimeSpan _scanInterval;
        private readonly TimeSpan _emptyGracePeriod;
        private readonly TimeSpan _noVsGracePeriod;
        private readonly CancellationTokenSource _cts;

        private Task? _loop;
        private int _killed;

        // 注册表是否曾持有过 VS 实例。用于区分机制 1 的两种情形：
        // 启动期（从未连接过——保留宽限窗口，让刚拉起的 Gateway 不在首个 VS 到来前
        // 被杀）vs. 连接后（某个 VS 连接过然后其 pipe 断了——立即杀；断连是显式退出
        // 信号，无需宽限）。
        private bool _everPopulated;

        // 机制 1 状态：注册表首次变空的时刻（null = 注册表非空，倒计时未运行）。
        private DateTime? _emptySince;

        // 机制 2 状态：首次观察到 devenv 不存在的时刻（null = devenv 存活，倒计时
        // 未运行）。
        private DateTime? _noVsSince;

        /// <param name="registry">Gateway 的实例表；<see cref="InstanceRegistry.Count"/>
        /// 驱动机制 1。</param>
        /// <param name="onSelfKill">任一机制触发时最多调用一次。生产环境调用
        /// <c>Environment.Exit(0)</c>（HTTP accept 循环无法取消）。不得抛异常。</param>
        /// <param name="cancellationToken">Main CTS 的 token；取消它（Gateway 关闭）
        /// 会停止扫描循环。</param>
        /// <param name="anyDevenvAlive">可注入的 devenv 探测。默认为真实
        /// <see cref="Process.GetProcessesByName"/> 扫描。</param>
        /// <param name="scanInterval">两次扫描之间的间隔（生产 2s；同时限制过期实例
        /// 淘汰延迟，让死掉的 VS 在约 2s 内被清除）。</param>
        /// <param name="emptyGracePeriod">注册表可保持空多久才自终止（生产 30s）。
        /// 也给刚启动的 Gateway 一个等待首个 VS 连接的窗口。</param>
        /// <param name="noVsGracePeriod">0 个 devenv 进程可持续多久才自终止
        /// （生产 10s）。</param>
        public ProcessScanner(
            InstanceRegistry registry,
            Action onSelfKill,
            CancellationToken cancellationToken,
            Func<bool>? anyDevenvAlive = null,
            TimeSpan? scanInterval = null,
            TimeSpan? emptyGracePeriod = null,
            TimeSpan? noVsGracePeriod = null,
            Func<IReadOnlyCollection<int>?>? aliveDevenvPids = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _onSelfKill = onSelfKill ?? throw new ArgumentNullException(nameof(onSelfKill));
            _anyDevenvAlive = anyDevenvAlive ?? DefaultAnyDevenvAlive;
            _aliveDevenvPids = aliveDevenvPids;
            _scanInterval = scanInterval ?? TimeSpan.FromSeconds(2);
            _emptyGracePeriod = emptyGracePeriod ?? TimeSpan.FromSeconds(30);
            _noVsGracePeriod = noVsGracePeriod ?? TimeSpan.FromSeconds(10);

            if (_scanInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(scanInterval), "scanInterval must be positive");
            if (_emptyGracePeriod <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(emptyGracePeriod), "emptyGracePeriod must be positive");
            if (_noVsGracePeriod <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(noVsGracePeriod), "noVsGracePeriod must be positive");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        /// <summary>
        /// 在后台启动扫描循环。Fire-and-forget；循环运行到 <see cref="Dispose"/>
        /// 或链接的取消 token 触发为止。与构造函数分离，让 Program.cs 可把它排在
        /// pipe accept 循环就绪之后。
        /// </summary>
        public void Start()
        {
            _loop = Task.Run(() => ScanLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 生产环境 devenv 探测。释放每个 <see cref="Process"/> 句柄（设计文档的
        /// 示例同样如此），让扫描器在长 Gateway 生命周期里绝不泄漏 OS 句柄。任何
        /// 失败都假定 VS 存活：误判"存活"只是推迟自终止，而误判"已死"会在 VS 仍在
        /// 运行时杀掉 Gateway——严重得多的失败模式。
        /// </summary>
        private static bool DefaultAnyDevenvAlive()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("devenv");
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                return any;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 生产环境 devenv-PID 探测：返回存活的 devenv 进程 ID 集合（每个句柄都
        /// 释放，扫描器零泄漏），探测本身失败则返回 null——null 结果让调用方跳过
        /// 淘汰，而不是冒风险淘汰存活的 VS。同时驱动自终止倒计时和 accept 循环那套
        /// 只在断连时清理的逻辑从未做过的逐 PID 过期淘汰（僵尸实例缺口）。
        /// </summary>
        internal static IReadOnlyCollection<int>? DefaultAliveDevenvPids()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("devenv");
                var ids = new HashSet<int>();
                foreach (var p in procs) { ids.Add(p.Id); p.Dispose(); }
                return ids;
            }
            catch
            {
                return null;
            }
        }

        private async Task ScanLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(_scanInterval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

                    DateTime now = DateTime.UtcNow;

                    // 机制 1：注册表清空自终止。一旦某个 VS 连接过，空的注册表意味
                    // 着该 VS 的 pipe 断了——显式退出信号——所以立即杀，无宽限
                    // （仅 scanInterval 延迟限制它）。在任何 VS 连接之前，保留宽限
                    // 窗口让刚拉起的 Gateway 撑到首个 VS 到来。
                    if (_registry.Count == 0)
                    {
                        if (_everPopulated)
                        {
                            TriggerSelfKill();
                            return;
                        }
                        _emptySince ??= now;
                        if (now - _emptySince >= _emptyGracePeriod)
                        {
                            TriggerSelfKill();
                            return;
                        }
                    }
                    else
                    {
                        _everPopulated = true;
                        _emptySince = null; // VS 存在——取消倒计时。
                    }

                    // 机制 2：devenv 扫描。每次迭代都运行，与机制 1 无关，独立防范
                    // 被强杀的 VS 留下的过期注册表条目。
                    bool anyVs;
                    if (_aliveDevenvPids != null)
                    {
                        // 全 PID 探测路径：存活的 devenv PID 集合同时驱动自终止倒计时
                        // 和逐 PID 过期淘汰。accept 循环只在观察到 pipe 断连时清理，而
                        // 被强杀/崩溃的 VS 永远不会产生断连——所以死掉的 VS 条目会
                        // 永远残留（在 list_vs_instances 里浮现成幽灵实例并造成路由歧义）。
                        // 淘汰任何已不在存活集合里的已注册 PID。
                        IReadOnlyCollection<int>? alive;
                        try { alive = _aliveDevenvPids(); }
                        catch { alive = null; }

                        if (alive == null)
                        {
                            // 探测失败——保守假定 VS 存活并跳过淘汰（绝不在探测失败时
                            // 淘汰）。
                            anyVs = true;
                        }
                        else
                        {
                            anyVs = alive.Count > 0;
                            foreach (var entry in _registry.Snapshot())
                            {
                                if (!alive.Contains(entry.Info.Pid))
                                {
                                    _registry.Remove(entry.Info.Pid);
                                    GatewayLogger.Log($"evicted stale VS PID={entry.Info.Pid} (devenv process no longer alive)");
                                }
                            }
                        }
                    }
                    else
                    {
                        try { anyVs = _anyDevenvAlive(); }
                        catch
                        {
                            // 探测失败被视为"VS 存活"——见 DefaultAnyDevenvAlive 的
                            // 理由。
                            anyVs = true;
                        }
                    }

                    if (!anyVs)
                    {
                        _noVsSince ??= now;
                        if (now - _noVsSince >= _noVsGracePeriod)
                        {
                            TriggerSelfKill();
                            return;
                        }
                    }
                    else
                    {
                        _noVsSince = null; // devenv 运行中——取消倒计时。
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Gateway 关闭中（onSelfKill 取消了 Main CTS，或 VS 触发了正常停止）
                // ——干净退出。
            }
            catch
            {
                // 扫描器绝不能搞崩 Gateway。这里出故障只是意味着失去自终止保护；
                // 用户仍可手动杀进程。
            }
        }

        /// <summary>
        /// 通过 interlocked 守卫精确触发一次 <see cref="_onSelfKill"/>，让从杀进程
        /// 触发到循环观察到取消之间的短暂窗口不会重复触发。
        /// </summary>
        private void TriggerSelfKill()
        {
            if (Interlocked.CompareExchange(ref _killed, 1, 0) != 0)
                return;
            try { _onSelfKill(); } catch { /* 取消绝不能抛 */ }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* 竞态 */ }
            try { _cts.Dispose(); } catch { /* 尽力而为 */ }
        }
    }
}
