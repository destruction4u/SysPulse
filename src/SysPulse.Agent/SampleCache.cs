using SysPulse.Core;

namespace SysPulse.Agent;

/// <summary>
/// Samples this PC once a second and keeps the latest reading for viewers to fetch, so any
/// number of viewers costs the same as one. Sampling pauses when no viewer has asked for
/// 30 seconds, so an idle agent uses almost no CPU.
/// </summary>
public sealed class SampleCache(ILogger<SampleCache> log) : BackgroundService
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);

    private volatile StaticInfo? _info;
    private volatile Sample? _latest;
    private long _lastRequest = Environment.TickCount64;

    public StaticInfo? Info => _info;

    /// <summary>Latest reading, or null while warming up after an idle period.</summary>
    public Sample? Latest => _latest;

    /// <summary>Called on every viewer request: keeps (or starts) sampling.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _info = await Task.Run(StaticInfo.Collect, ct);
        using var monitor = await Task.Run(() => new SystemMonitor(_info), ct);
        log.LogInformation("Monitoring started for {Host}", _info.Host);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                bool watched = Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < IdleAfter.TotalMilliseconds;
                if (!watched)
                {
                    _latest = null; // next viewer gets 503 for a moment instead of a stale reading
                    continue;
                }
                try { _latest = monitor.Sample(includeProcesses: true); }
                catch (Exception ex) { log.LogWarning(ex, "Sampling failed"); }
            }
        }
        catch (OperationCanceledException) { /* service stopping */ }
    }
}
