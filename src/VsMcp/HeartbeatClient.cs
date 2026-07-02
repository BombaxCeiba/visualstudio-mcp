using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsMcp
{
    /// <summary>
    /// Probes the Gateway's liveness on a fixed cadence by writing a heartbeat
    /// frame through the VS-side pipe. A healthy write means the pipe (and thus
    /// the Gateway) is still connected; a write failure accumulates a consecutive-
    /// miss count, and once the misses span <see cref="_deadThreshold"/> the
    /// Gateway is presumed dead and a preemptive relaunch is triggered
    /// (设计文档 §抢占式 Gateway 拉起).
    /// <para>
    /// The lifecycle intentionally mirrors <see cref="KeepAliveNotifier"/>:
    /// construct-and-forget (the loop starts in the constructor), linked CTS,
    /// all loop exceptions swallowed (the heartbeat must NEVER crash VS), and a
    /// synchronous non-blocking <see cref="Dispose"/> (cancel CTS + return; the
    /// loop observes cancellation on its next delay and exits). Awaiting the
    /// loop in Dispose would be sync-over-async on the VS exit path — the known
    /// deadlock root cause this design avoids (see PipeMcpServer.Dispose notes).
    /// </para>
    /// <para>
    /// Both the heartbeat sender and the relaunch entry point are injected as
    /// delegates so the loop logic can be unit-tested with millisecond-scale
    /// intervals without spinning up a real pipe or Gateway process — the same
    /// testability philosophy KeepAliveNotifier uses for its sendKeepAlive
    /// delegate. Production wiring passes <c>ct =&gt; pipe.SendHeartbeatAsync(ct)</c>
    /// and <c>ct =&gt; GatewayLauncher.EnsureGatewayRunningAsync(ct)</c>.
    /// </para>
    /// </summary>
    public sealed class HeartbeatClient : IDisposable
    {
        private readonly int _pid;
        private readonly Func<CancellationToken, Task> _sendHeartbeat;
        private readonly Func<CancellationToken, Task<bool>> _ensureGatewayRunning;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _deadThreshold;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        private readonly ILogger? _logger;
        private int _disposed;

        /// <summary>
        /// Start the heartbeat loop. Construction begins probing immediately.
        /// </summary>
        /// <param name="pid">VS process id, surfaced in diagnostic logs.</param>
        /// <param name="sendHeartbeat">One heartbeat attempt. Completes normally
        /// if the frame reached the Gateway; throws if the pipe is disconnected
        /// or the write failed. "Gateway alive" is operationally indistinguishable
        /// from "the write succeeded", which is exactly the liveness signal we
        /// need.</param>
        /// <param name="ensureGatewayRunning">Preemptive relaunch entry point
        /// (production: <c>GatewayLauncher.EnsureGatewayRunningAsync</c>).
        /// Invoked once the cumulative miss window crosses the dead threshold.
        /// The port-bind lock guarantees that if multiple VS instances race to
        /// relaunch, only one new Gateway survives.</param>
        /// <param name="interval">Probe interval (production: 5s). Must be
        /// positive.</param>
        /// <param name="deadThreshold">Cumulative miss time that presumes the
        /// Gateway dead and triggers a relaunch (production: 15s = 3 misses at
        /// 5s each). Must be positive.</param>
        /// <param name="callerToken">VS package DisposalToken; canceling it
        /// stops the loop. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <param name="logger">Optional diagnostic logger.</param>
        public HeartbeatClient(
            int pid,
            Func<CancellationToken, Task> sendHeartbeat,
            Func<CancellationToken, Task<bool>> ensureGatewayRunning,
            TimeSpan? interval = null,
            TimeSpan? deadThreshold = null,
            CancellationToken callerToken = default,
            ILogger? logger = null)
        {
            _sendHeartbeat = sendHeartbeat ?? throw new ArgumentNullException(nameof(sendHeartbeat));
            _ensureGatewayRunning = ensureGatewayRunning ?? throw new ArgumentNullException(nameof(ensureGatewayRunning));

            _pid = pid;
            _interval = interval ?? TimeSpan.FromSeconds(5);
            _deadThreshold = deadThreshold ?? TimeSpan.FromSeconds(15);
            _logger = logger;

            if (_interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "interval must be positive");
            if (_deadThreshold <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(deadThreshold), "deadThreshold must be positive");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            int failCount = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

                    try
                    {
                        await _sendHeartbeat(ct).ConfigureAwait(false);
                        failCount = 0; // Gateway answered, clear the miss streak.
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        failCount++;
                        // The Gateway has been unreachable for failCount*interval.
                        // When that spans the dead threshold, presume it crashed /
                        // was taskkilled and preemptively relaunch.
                        if (failCount * _interval.Ticks >= _deadThreshold.Ticks)
                        {
                            _logger?.LogDebug(
                                "Heartbeat: PID {Pid} missed {Fails} consecutive probes ({Elapsed} >= {Threshold}); triggering preemptive Gateway relaunch",
                                _pid, failCount,
                                TimeSpan.FromTicks(failCount * _interval.Ticks), _deadThreshold);
                            try
                            {
                                await _ensureGatewayRunning(ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                return;
                            }
                            catch
                            {
                                // Relaunch itself failed (exe not on disk yet, etc.).
                                // The ConnectLoop keeps retrying the pipe; reset and
                                // let the next miss window re-trigger. Never propagate.
                            }
                            failCount = 0; // Give the new Gateway a fresh window.
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* VS disposing */ }
            catch
            {
                // The heartbeat loop is strictly best-effort; an unexpected fault
                // here must never bring down VS.
            }
        }

        /// <summary>
        /// Stop the loop. Synchronous and non-blocking: cancels the linked CTS
        /// and returns immediately. The loop observes cancellation on its next
        /// Task.Delay and exits; at most one in-flight heartbeat may complete
        /// after this returns (harmless). Idempotent via an interlocked guard.
        /// Awaiting <see cref="_loop"/> here would be sync-over-async on the VS
        /// exit path — the exact deadlock this avoids.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* raced */ }
            try { _cts.Dispose(); } catch { /* best-effort */ }
        }
    }
}
