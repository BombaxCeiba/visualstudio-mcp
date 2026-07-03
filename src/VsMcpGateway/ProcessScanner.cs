using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcpGateway
{
    /// <summary>
    /// Gateway self-termination watchdog. Two independent mechanisms decide when
    /// the Gateway should exit so it never lingers as an orphan process after
    /// every Visual Studio instance is gone (设计文档 §Gateway 自杀 双重防护):
    /// <list type="number">
    /// <item><b>Passive — registry-empty grace.</b> When the
    ///     <see cref="InstanceRegistry"/> holds zero connected VS instances for
    ///     longer than <c>emptyGracePeriod</c> (production 30s), self-kill. A VS
    ///     reconnecting cancels the countdown. This covers the normal case where
    ///     VS exits cleanly and its pipe disconnect clears the registry.</item>
    /// <item><b>Active — devenv scan.</b> Every <c>scanInterval</c> (production
    ///     10s), poll the process table for <c>devenv.exe</c>. If none is alive
    ///     for <c>noVsGracePeriod</c> consecutive time (production 10s),
    ///     self-kill. A devenv reappearing cancels the countdown. This catches
    ///     force-kill / crash scenarios where the pipe never cleanly closed and
    ///     a stale registry entry would otherwise defeat mechanism 1.</item>
    /// </list>
    /// Either mechanism firing invokes <c>onSelfKill</c> exactly once
    /// (production: <c>Environment.Exit(0)</c> — see Program.cs for why a
    /// graceful cancel cannot unwind the HTTP accept loop).
    /// <para>
    /// Every timer is injectable (production = seconds, tests = milliseconds)
    /// and the devenv probe is a <c>Func&lt;bool&gt;</c> so tests never touch the
    /// real process table — the static <see cref="Process.GetProcessesByName"/>
    /// cannot be mocked directly.
    /// </para>
    /// </summary>
    public sealed class ProcessScanner : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly Func<bool> _anyDevenvAlive;
        private readonly Action _onSelfKill;
        private readonly TimeSpan _scanInterval;
        private readonly TimeSpan _emptyGracePeriod;
        private readonly TimeSpan _noVsGracePeriod;
        private readonly CancellationTokenSource _cts;

        private Task? _loop;
        private int _killed;

        // Mechanism 1 state: timestamp when the registry first became empty
        // (null = registry non-empty, countdown not running).
        private DateTime? _emptySince;

        // Mechanism 2 state: timestamp when devenv was first observed gone
        // (null = devenv alive, countdown not running).
        private DateTime? _noVsSince;

        /// <param name="registry">The Gateway's instance table; <see cref="InstanceRegistry.Count"/>
        /// drives mechanism 1.</param>
        /// <param name="onSelfKill">Invoked at most once when either mechanism
        /// trips. Production calls <c>Environment.Exit(0)</c> (the HTTP accept
        /// loop cannot be cancelled). Must not throw.</param>
        /// <param name="cancellationToken">The Main CTS's token; canceling it
        /// (Gateway shutdown) stops the scan loop.</param>
        /// <param name="anyDevenvAlive">Injectable devenv probe. Defaults to a
        /// real <see cref="Process.GetProcessesByName"/> scan.</param>
        /// <param name="scanInterval">Time between scans (production 10s).</param>
        /// <param name="emptyGracePeriod">How long the registry may stay empty
        /// before self-kill (production 30s). This also gives freshly-started
        /// Gateways a window for the first VS to connect.</param>
        /// <param name="noVsGracePeriod">How long zero devenv processes may
        /// persist before self-kill (production 10s).</param>
        public ProcessScanner(
            InstanceRegistry registry,
            Action onSelfKill,
            CancellationToken cancellationToken,
            Func<bool>? anyDevenvAlive = null,
            TimeSpan? scanInterval = null,
            TimeSpan? emptyGracePeriod = null,
            TimeSpan? noVsGracePeriod = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _onSelfKill = onSelfKill ?? throw new ArgumentNullException(nameof(onSelfKill));
            _anyDevenvAlive = anyDevenvAlive ?? DefaultAnyDevenvAlive;
            _scanInterval = scanInterval ?? TimeSpan.FromSeconds(10);
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
        /// Start the scan loop in the background. Fire-and-forget; the loop runs
        /// until <see cref="Dispose"/> or the linked cancellation token fires.
        /// Separated from the constructor so Program.cs can order it after the
        /// pipe accept loop is armed.
        /// </summary>
        public void Start()
        {
            _loop = Task.Run(() => ScanLoopAsync(_cts.Token));
        }

        /// <summary>
        /// Production devenv probe. Disposes every <see cref="Process"/> handle
        /// (the design doc's example does the same) so the scanner never leaks
        /// OS handles over a long Gateway lifetime. On any failure assumes VS is
        /// alive: a false "alive" merely delays self-kill, while a false "dead"
        /// would kill the Gateway while VS is still running — the far worse
        /// failure mode.
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

        private async Task ScanLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(_scanInterval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

                    DateTime now = DateTime.UtcNow;

                    // Mechanism 1: registry empty grace.
                    if (_registry.Count == 0)
                    {
                        // First time empty → arm the countdown. Subsequent scans
                        // keep the original timestamp until either the grace
                        // elapses (kill) or a VS reconnects (cancel).
                        _emptySince ??= now;
                        if (now - _emptySince >= _emptyGracePeriod)
                        {
                            TriggerSelfKill();
                            return;
                        }
                    }
                    else
                    {
                        _emptySince = null; // VS present — cancel the countdown.
                    }

                    // Mechanism 2: devenv scan. Runs every iteration regardless of
                    // mechanism 1 so it independently guards against stale registry
                    // entries left by a force-killed VS.
                    bool anyVs;
                    try { anyVs = _anyDevenvAlive(); }
                    catch
                    {
                        // A probe failure is treated as "VS alive" — see the
                        // DefaultAnyDevenvAlive rationale.
                        anyVs = true;
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
                        _noVsSince = null; // devenv running — cancel the countdown.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Gateway shutting down (onSelfKill cancelled the Main CTS, or VS
                // triggered a normal stop) — exit cleanly.
            }
            catch
            {
                // The scanner must never crash the Gateway. A fault here just
                // means we lose self-termination protection; the user can still
                // kill the process manually.
            }
        }

        /// <summary>
        /// Fire <see cref="_onSelfKill"/> exactly once via an interlocked guard,
        /// so the brief window between the kill tripping and the loop observing
        /// cancellation cannot double-fire.
        /// </summary>
        private void TriggerSelfKill()
        {
            if (Interlocked.CompareExchange(ref _killed, 1, 0) != 0)
                return;
            try { _onSelfKill(); } catch { /* cancel must not throw */ }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* raced */ }
            try { _cts.Dispose(); } catch { /* best-effort */ }
        }
    }
}
