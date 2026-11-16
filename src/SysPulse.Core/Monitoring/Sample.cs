namespace SysPulse.Core;

public sealed record ProcSample(string Name, int Pid, double Cpu, long Memory);
public sealed record DriveSample(string Name, string Label, long Total, long Free);
public sealed record BatterySample(int Percent, bool Charging, bool OnAc, int SecondsLeft);

/// <summary>One reading of live load. Same shape on every platform (it's also the agent's wire format).</summary>
/// <remarks>
/// CommitTotal/CommitUsed: Windows commit charge; on Linux, swap (see <see cref="StaticInfo.Platform"/>).
/// </remarks>
public sealed record Sample(
    double CpuPercent, double? CpuMhz, double[] CorePercents,
    ulong MemTotal, ulong MemUsed, ulong CommitTotal, ulong CommitUsed,
    double NetDown, double NetUp,
    double? DiskActive, double? DiskRead, double? DiskWrite,
    double? GpuPercent,
    IReadOnlyList<ProcSample>? TopProcesses, int? ProcessCount, // null when skipped (window hidden)
    IReadOnlyList<DriveSample> Drives,
    BatterySample? Battery, TimeSpan Uptime);

/// <summary>Takes one reading per call. Not thread-safe: call from one thread at a time.</summary>
public interface ISystemMonitor : IDisposable
{
    /// <param name="includeProcesses">Walking every process is the most expensive part; skip it when nobody sees it.</param>
    Sample Sample(bool includeProcesses = true);
}

public static class SystemMonitors
{
    /// <summary>The monitor for the platform this code runs on.</summary>
    public static ISystemMonitor Create(StaticInfo info) =>
        StaticInfo.FakeLinuxRoot is { } fake
            ? new LinuxMonitor(new LinuxFs(fake), () => Environment.TickCount64, _ => (512L << 30, 200L << 30), 4096, allowTools: false)
        : OperatingSystem.IsWindows() ? new WindowsMonitor(info)
        : OperatingSystem.IsLinux() ? new LinuxMonitor(info)
        : throw new PlatformNotSupportedException("SysPulse supports Windows and Linux.");
}
