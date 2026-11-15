using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace SysPulse.Services;

public sealed record ProcSample(string Name, int Pid, double Cpu, long Memory);
public sealed record DriveSample(string Name, string Label, long Total, long Free);
public sealed record BatterySample(int Percent, bool Charging, bool OnAc, int SecondsLeft);

public sealed record Sample(
    double CpuPercent, double? CpuMhz, double[] CorePercents,
    ulong MemTotal, ulong MemUsed, ulong CommitTotal, ulong CommitUsed,
    double NetDown, double NetUp,
    double? DiskActive, double? DiskRead, double? DiskWrite,
    double? GpuPercent,
    IReadOnlyList<ProcSample> TopProcesses, int ProcessCount,
    IReadOnlyList<DriveSample> Drives,
    BatterySample? Battery, TimeSpan Uptime);

/// <summary>
/// Takes one snapshot of live system load per call to <see cref="Sample"/>.
/// Uses the same performance counters as Task Manager where possible, with fallbacks.
/// Not thread-safe: call from one thread at a time.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly StaticInfo _info;

    private readonly PerformanceCounter? _cpuTotal, _cpuPerf, _diskIdle, _diskRead, _diskWrite;
    private readonly PerformanceCounter[] _cores = [];
    private readonly Dictionary<string, PerformanceCounter> _gpu = [];
    private bool _gpuSupported = true;

    private long _lastIdle, _lastKernel, _lastUser;           // CPU fallback
    private long _lastRx, _lastTx; private long _lastNetTicks; // network
    private Dictionary<int, (TimeSpan Cpu, string Name)> _lastProc = [];
    private long _lastProcTicks;
    private IReadOnlyList<DriveSample> _drives = [];
    private int _tick;

    public SystemMonitor(StaticInfo info)
    {
        _info = info;
        _cpuTotal = TryCounter("Processor Information", "% Processor Utility", "_Total");
        _cpuPerf = TryCounter("Processor Information", "% Processor Performance", "_Total");
        _diskIdle = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
        _diskRead = TryCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _diskWrite = TryCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");

        try
        {
            // Instances look like "0,0", "0,1" ... "0,_Total", "_Total"
            _cores = new PerformanceCounterCategory("Processor Information").GetInstanceNames()
                .Select(n => n.Split(','))
                .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
                .OrderBy(p => int.Parse(p[0])).ThenBy(p => int.Parse(p[1]))
                .Select(p => TryCounter("Processor Information", "% Processor Utility", $"{p[0]},{p[1]}"))
                .OfType<PerformanceCounter>()
                .ToArray();
        }
        catch { /* per-core view just stays empty */ }

        Native.GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        (_lastRx, _lastTx) = ReadNetTotals();
        _lastNetTicks = _lastProcTicks = Stopwatch.GetTimestamp();
        RefreshGpuInstances();
        Thread.Sleep(500); // give rate counters a baseline interval before the first sample
    }

    public Sample Sample()
    {
        _tick++;

        // ---- CPU
        double cpu = Read(_cpuTotal) ?? FallbackCpu();
        double? mhz = Read(_cpuPerf) is double perf && _info.CpuBaseMhz > 0 ? perf / 100 * _info.CpuBaseMhz : null;
        double[] cores = _cores.Select(c => Math.Clamp(Read(c) ?? 0, 0, 100)).ToArray();

        // ---- Memory
        var m = Native.Memory();
        ulong commitUsed = m.ullTotalPageFile - m.ullAvailPageFile;

        // ---- Network
        long now = Stopwatch.GetTimestamp();
        var (rx, tx) = ReadNetTotals();
        double netSecs = Math.Max(Stopwatch.GetElapsedTime(_lastNetTicks, now).TotalSeconds, 0.001);
        double down = Math.Max(0, (rx - _lastRx) / netSecs), up = Math.Max(0, (tx - _lastTx) / netSecs);
        (_lastRx, _lastTx, _lastNetTicks) = (rx, tx, now);

        // ---- Disk
        double? diskActive = Read(_diskIdle) is double idle ? Math.Clamp(100 - idle, 0, 100) : null;

        // ---- GPU (instances come and go with processes; resync every 5 s)
        if (_tick % 5 == 0) RefreshGpuInstances();
        double? gpu = ReadGpu();

        // ---- Processes, drives, battery
        var (top, count) = SampleProcesses();
        if (_tick % 5 == 1) _drives = ReadDrives();

        return new Sample(
            Math.Clamp(cpu, 0, 100), mhz, cores,
            m.ullTotalPhys, m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPageFile, commitUsed,
            down, up,
            diskActive, Read(_diskRead), Read(_diskWrite),
            gpu, top, count, _drives, ReadBattery(),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private double FallbackCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long dIdle = idle - _lastIdle, dTotal = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle
        (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);
        return dTotal > 0 ? (1.0 - (double)dIdle / dTotal) * 100 : 0;
    }

    private static (long Rx, long Tx) ReadNetTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try { var s = ni.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; } catch { }
            }
        }
        catch { }
        return (rx, tx);
    }

    private void RefreshGpuInstances()
    {
        if (!_gpuSupported) return;
        try
        {
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                .ToHashSet();
            foreach (var gone in _gpu.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _gpu[gone].Dispose();
                _gpu.Remove(gone);
            }
            foreach (var n in names.Where(n => !_gpu.ContainsKey(n)))
                if (TryCounter("GPU Engine", "Utilization Percentage", n) is { } c) _gpu[n] = c;
        }
        catch { _gpuSupported = false; }
    }

    private double? ReadGpu()
    {
        if (!_gpuSupported) return null;
        double sum = 0;
        foreach (var (name, c) in _gpu.ToList())
        {
            try { sum += c.NextValue(); }
            catch { c.Dispose(); _gpu.Remove(name); } // process exited
        }
        return Math.Clamp(sum, 0, 100);
    }

    private (IReadOnlyList<ProcSample> Top, int Count) SampleProcesses()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = Stopwatch.GetElapsedTime(_lastProcTicks, now).TotalMilliseconds;
        var current = new Dictionary<int, (TimeSpan, string)>();
        var list = new List<ProcSample>();
        var procs = Process.GetProcesses();

        foreach (var p in procs)
        {
            using (p)
            {
                if (p.Id == 0) continue; // "Idle"
                string name = p.ProcessName;
                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }
                double cpu = 0;
                try
                {
                    var t = p.TotalProcessorTime; // fails for protected/elevated processes
                    current[p.Id] = (t, name);
                    if (_lastProc.TryGetValue(p.Id, out var prev) && prev.Name == name && elapsedMs > 0)
                        cpu = (t - prev.Cpu).TotalMilliseconds / elapsedMs / _info.LogicalProcessors * 100;
                }
                catch { }
                list.Add(new ProcSample(name, p.Id, Math.Clamp(cpu, 0, 100), mem));
            }
        }

        _lastProc = current;
        _lastProcTicks = now;
        var top = list.OrderByDescending(p => Math.Round(p.Cpu, 1)).ThenByDescending(p => p.Memory).Take(12).ToList();
        return (top, procs.Length);
    }

    private static IReadOnlyList<DriveSample> ReadDrives()
    {
        var list = new List<DriveSample>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveSample(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.TotalFreeSpace));
            }
            catch { }
        }
        return list;
    }

    private static BatterySample? ReadBattery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        if ((s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255) return null; // no battery
        return new BatterySample(s.BatteryLifePercent, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1, s.BatteryLifeTime);
    }

    private static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue(); // first read always returns 0; prime it
            return c;
        }
        catch { return null; }
    }

    private static double? Read(PerformanceCounter? c)
    {
        if (c == null) return null;
        try { return c.NextValue(); } catch { return null; }
    }

    public void Dispose()
    {
        foreach (var c in new[] { _cpuTotal, _cpuPerf, _diskIdle, _diskRead, _diskWrite }.Concat(_cores).Concat(_gpu.Values))
            c?.Dispose();
        _gpu.Clear();
    }
}
