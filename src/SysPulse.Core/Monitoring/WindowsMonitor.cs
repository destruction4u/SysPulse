using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace SysPulse.Core;

/// <summary>
/// Takes one snapshot of live system load per call to <see cref="Sample"/>.
/// Uses the same performance counters as Task Manager where possible, with fallbacks.
/// Not thread-safe: call from one thread at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMonitor : ISystemMonitor
{
    private readonly StaticInfo _info;

    private readonly PerformanceCounter? _cpuTotal, _cpuPerf, _diskIdle, _diskRead, _diskWrite;
    private readonly PerformanceCounter[] _cores = [];
    private PerformanceCounterCategory? _gpuCategory;
    private Dictionary<string, CounterSample> _gpuPrev = [];
    private bool _gpuSupported = true;

    private long _lastIdle, _lastKernel, _lastUser;           // CPU fallback
    private long _lastRx, _lastTx; private long _lastNetTicks; // network
    private NetworkInterface[] _netAdapters = [];
    private long _netListTicks;
    private Dictionary<int, (TimeSpan Cpu, string Name)> _lastProc = [];
    private long _lastProcTicks;
    private IReadOnlyList<DriveSample> _drives = [];
    private int _tick;

    public WindowsMonitor(StaticInfo info)
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
        (_lastRx, _lastTx, _) = ReadNetTotals();
        _lastNetTicks = _lastProcTicks = Stopwatch.GetTimestamp();
        ReadGpu(); // first snapshot = baseline
        Thread.Sleep(500); // give rate counters a baseline interval before the first sample
    }

    /// <param name="includeProcesses">
    /// Walking every process is the most expensive part of a sample; skip it while the window is hidden.
    /// </param>
    public Sample Sample(bool includeProcesses = true)
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
        var (rx, tx, adaptersChanged) = ReadNetTotals();
        double netSecs = Math.Max(Stopwatch.GetElapsedTime(_lastNetTicks, now).TotalSeconds, 0.001);
        double down = 0, up = 0;
        if (!adaptersChanged) // otherwise the totals jump by a whole adapter's lifetime bytes
        {
            down = Math.Max(0, (rx - _lastRx) / netSecs);
            up = Math.Max(0, (tx - _lastTx) / netSecs);
        }
        (_lastRx, _lastTx, _lastNetTicks) = (rx, tx, now);

        // ---- Disk
        double? diskActive = Read(_diskIdle) is double idle ? Math.Clamp(100 - idle, 0, 100) : null;

        // ---- GPU (instances come and go with processes; resync every 5 s)
        double? gpu = ReadGpu();

        // ---- Processes, drives, battery
        IReadOnlyList<ProcSample>? top = null;
        int? count = null;
        if (includeProcesses) (top, count) = SampleProcesses();
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

    /// <summary>
    /// Total bytes across active adapters. Enumerating adapters and checking their status is
    /// slow (~15-45 ms with many virtual adapters), so the active list is cached for 10 s;
    /// reading one adapter's counters is ~1 ms.
    /// </summary>
    /// <returns>Changed = the set of adapters changed, so the totals aren't comparable with last time.</returns>
    private (long Rx, long Tx, bool Changed) ReadNetTotals()
    {
        bool changed = false;
        if (_netAdapters.Length == 0 || Stopwatch.GetElapsedTime(_netListTicks).TotalSeconds >= 10)
        {
            string[] before = _netAdapters.Select(a => a.Id).ToArray();
            try
            {
                _netAdapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                              && ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .ToArray();
            }
            catch { _netAdapters = []; }
            _netListTicks = Stopwatch.GetTimestamp();
            changed = !before.SequenceEqual(_netAdapters.Select(a => a.Id));
        }

        long rx = 0, tx = 0;
        foreach (var ni in _netAdapters)
        {
            try { var s = ni.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; }
            catch { changed = true; } // adapter vanished; list refreshes on the next cycle
        }
        return (rx, tx, changed);
    }

    /// <summary>
    /// GPU 3D load = sum of every process's 3D-engine utilization. There are hundreds of
    /// per-process instances, so instead of one PerformanceCounter each (~50 ms per read, plus
    /// ~300 ms to re-list instances) we snapshot the whole category in one call (~7 ms) and
    /// compute the rate ourselves from consecutive raw samples.
    /// </summary>
    private double? ReadGpu()
    {
        if (!_gpuSupported) return null;
        try
        {
            _gpuCategory ??= new PerformanceCounterCategory("GPU Engine");
            var data = _gpuCategory.ReadCategory()["Utilization Percentage"];
            if (data == null) return null;

            var next = new Dictionary<string, CounterSample>(_gpuPrev.Count);
            double sum = 0;
            foreach (InstanceData d in data.Values)
            {
                if (!d.InstanceName.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                next[d.InstanceName] = d.Sample;
                if (_gpuPrev.TryGetValue(d.InstanceName, out var prev))
                    sum += CounterSample.Calculate(prev, d.Sample);
            }
            _gpuPrev = next; // instances for exited processes simply drop out
            return Math.Clamp(sum, 0, 100);
        }
        catch
        {
            _gpuSupported = false; // no GPU counters on this machine (e.g. some VMs)
            return null;
        }
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
        foreach (var c in new[] { _cpuTotal, _cpuPerf, _diskIdle, _diskRead, _diskWrite }.Concat(_cores))
            c?.Dispose();
    }
}
