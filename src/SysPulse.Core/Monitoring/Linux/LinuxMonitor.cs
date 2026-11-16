using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SysPulse.Core;

/// <summary>
/// Linux live readings from /proc and /sys. Everything is computed from deltas between two
/// readings of kernel counters, the same way top/htop do it.
/// </summary>
public sealed class LinuxMonitor : ISystemMonitor
{
    // Whole disks only (not partitions): sda, nvme0n1, vda, mmcblk0, xvda, hda
    private static readonly Regex PhysicalDisk = new(@"^(sd[a-z]+|nvme\d+n\d+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|mmcblk\d+)$", RegexOptions.Compiled);
    private static readonly HashSet<string> RealFilesystems =
        ["ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "jfs", "reiserfs", "bcachefs"];

    private readonly LinuxFs _fs;
    private readonly Func<long> _clockMs;
    private readonly Func<string, (long Total, long Free)?> _diskSpace;
    private readonly int _pageSize;
    private bool _nvidiaSmi;

    private CpuTimes[] _lastCpu = [];               // [0] = all CPUs, then cpu0..cpuN
    private Dictionary<string, (long Rx, long Tx)> _lastNet = [];
    private Dictionary<string, DiskCounters> _lastDisk = [];
    private Dictionary<(int Pid, long Start), long> _lastProc = [];
    private long _lastProcTotalJiffies;
    private long _lastMs;
    private IReadOnlyList<DriveSample> _drives = [];
    private int _tick;

    private readonly record struct CpuTimes(long Total, long Idle);
    private readonly record struct DiskCounters(long SectorsRead, long SectorsWritten, long MsIo);

    public LinuxMonitor(StaticInfo info)
        : this(new LinuxFs("/"), () => Environment.TickCount64, DriveSpace, Environment.SystemPageSize, allowTools: true)
    {
        Thread.Sleep(500); // give the first reading a baseline interval
    }

    internal LinuxMonitor(LinuxFs fs, Func<long> clockMs, Func<string, (long, long)?> diskSpace, int pageSize, bool allowTools)
    {
        _fs = fs;
        _clockMs = clockMs;
        _diskSpace = diskSpace;
        _pageSize = pageSize;
        _nvidiaSmi = allowTools && FindOnPath("nvidia-smi") != null;

        // Baselines for every delta-based reading
        _lastCpu = ReadCpu();
        _lastNet = ReadNet();
        _lastDisk = ReadDisks();
        (_lastProc, _) = ReadProcesses();
        _lastProcTotalJiffies = _lastCpu.Length > 0 ? _lastCpu[0].Total : 0;
        _lastMs = _clockMs();
    }

    public Sample Sample(bool includeProcesses = true)
    {
        _tick++;
        long now = _clockMs();
        double seconds = Math.Max((now - _lastMs) / 1000.0, 0.001);
        _lastMs = now;

        // ---- CPU (total + per thread)
        var cpu = ReadCpu();
        double total = Usage(_lastCpu, cpu, 0);
        double[] cores = Enumerable.Range(1, Math.Max(0, cpu.Length - 1)).Select(k => Usage(_lastCpu, cpu, k)).ToArray();
        _lastCpu = cpu;

        // ---- Memory (+ swap in the "commit" slot)
        var mem = Meminfo();
        ulong memTotal = mem.GetValueOrDefault("MemTotal");
        ulong available = mem.TryGetValue("MemAvailable", out ulong a) ? a
            : mem.GetValueOrDefault("MemFree") + mem.GetValueOrDefault("Buffers") + mem.GetValueOrDefault("Cached"); // kernels < 3.14
        ulong swapTotal = mem.GetValueOrDefault("SwapTotal");
        ulong swapUsed = swapTotal - Math.Min(swapTotal, mem.GetValueOrDefault("SwapFree"));

        // ---- Network: if the set of adapters changed, skip one reading (totals aren't comparable)
        var net = ReadNet();
        double down = 0, up = 0;
        if (net.Keys.ToHashSet().SetEquals(_lastNet.Keys))
        {
            down = Math.Max(0, net.Sum(n => n.Value.Rx - _lastNet[n.Key].Rx) / seconds);
            up = Math.Max(0, net.Sum(n => n.Value.Tx - _lastNet[n.Key].Tx) / seconds);
        }
        _lastNet = net;

        // ---- Disks: bytes/s summed; "active" = busiest disk's % of time with I/O in flight
        var disks = ReadDisks();
        double read = 0, write = 0, active = 0;
        foreach (var (name, d) in disks)
        {
            if (!_lastDisk.TryGetValue(name, out var p)) continue;
            read += Math.Max(0, d.SectorsRead - p.SectorsRead) * 512.0 / seconds;   // diskstats sectors are always 512 bytes
            write += Math.Max(0, d.SectorsWritten - p.SectorsWritten) * 512.0 / seconds;
            active = Math.Max(active, Math.Max(0, d.MsIo - p.MsIo) / (seconds * 1000) * 100);
        }
        _lastDisk = disks;

        // ---- Processes: CPU share = process jiffies / all-CPU jiffies over the same interval,
        // which is already normalized to the whole machine (like Task Manager) and needs no clock-tick constant.
        IReadOnlyList<ProcSample>? top = null;
        int? count = null;
        if (includeProcesses)
        {
            var (procs, rows) = ReadProcesses();
            long totalNow = cpu.Length > 0 ? cpu[0].Total : 0;
            long procWindow = totalNow - _lastProcTotalJiffies;
            var list = new List<ProcSample>(rows.Count);
            foreach (var r in rows)
            {
                double share = procWindow > 0 && _lastProc.TryGetValue((r.Pid, r.Start), out long prev)
                    ? Math.Clamp(100.0 * (r.Jiffies - prev) / procWindow, 0, 100) : 0;
                list.Add(new ProcSample(r.Name, r.Pid, share, r.Rss * _pageSize));
            }
            _lastProc = procs;
            _lastProcTotalJiffies = totalNow;
            top = list.OrderByDescending(p => Math.Round(p.Cpu, 1)).ThenByDescending(p => p.Memory).Take(12).ToList();
            count = rows.Count;
        }
        if (_tick % 5 == 1) _drives = ReadDrives();

        return new Sample(
            Math.Clamp(total, 0, 100), CurrentMhz(), cores,
            memTotal, memTotal - Math.Min(memTotal, available), swapTotal, swapUsed,
            down, up,
            disks.Count > 0 ? Math.Clamp(active, 0, 100) : null, disks.Count > 0 ? read : null, disks.Count > 0 ? write : null,
            ReadGpu(), top, count, _drives, ReadBattery(),
            TimeSpan.FromSeconds(double.TryParse(_fs.Line("proc/uptime").Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double uptime) ? uptime : 0));
    }

    // ------------------------------------------------------------------ CPU

    /// <summary>/proc/stat "cpu" lines: user nice system idle iowait irq softirq steal (guest time is already inside user).</summary>
    private CpuTimes[] ReadCpu()
    {
        var all = new List<(int Index, CpuTimes Times)>();
        foreach (string line in (_fs.Read("proc/stat") ?? "").Split('\n'))
        {
            if (!line.StartsWith("cpu")) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int index = f[0] == "cpu" ? -1 : int.TryParse(f[0].AsSpan(3), out int n) ? n : int.MaxValue;
            long[] v = f.Skip(1).Take(8).Select(s => long.TryParse(s, out long x) ? x : 0).ToArray();
            if (v.Length < 4) continue;
            long idle = v[3] + (v.Length > 4 ? v[4] : 0); // idle + iowait
            all.Add((index, new CpuTimes(v.Sum(), idle)));
        }
        return all.OrderBy(c => c.Index).Select(c => c.Times).ToArray();
    }

    private static double Usage(CpuTimes[] before, CpuTimes[] after, int k)
    {
        if (k >= before.Length || k >= after.Length) return 0;
        long dTotal = after[k].Total - before[k].Total, dIdle = after[k].Idle - before[k].Idle;
        return dTotal > 0 ? Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100) : 0;
    }

    /// <summary>Average current frequency across CPUs (cpufreq), else /proc/cpuinfo "cpu MHz".</summary>
    private double? CurrentMhz()
    {
        var khz = _fs.Entries("sys/devices/system/cpu")
            .Where(n => Regex.IsMatch(n, @"^cpu\d+$"))
            .Select(n => _fs.Long($"sys/devices/system/cpu/{n}/cpufreq/scaling_cur_freq"))
            .Where(v => v is > 0).Select(v => v!.Value).ToList();
        if (khz.Count > 0) return khz.Average() / 1000.0;

        var mhz = (_fs.Read("proc/cpuinfo") ?? "").Split('\n')
            .Where(l => l.StartsWith("cpu MHz"))
            .Select(l => double.TryParse(l.Split(':')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : 0)
            .Where(m => m > 0).ToList();
        return mhz.Count > 0 ? mhz.Average() : null;
    }

    // ------------------------------------------------------------------ memory, network, disks

    private Dictionary<string, ulong> Meminfo()
    {
        var map = new Dictionary<string, ulong>();
        foreach (string line in (_fs.Read("proc/meminfo") ?? "").Split('\n'))
        {
            var p = line.Split(':', 2);
            if (p.Length < 2) continue;
            var v = p[1].Trim().Split(' ');
            if (ulong.TryParse(v[0], out ulong n)) map[p[0].Trim()] = v.Length > 1 && v[1] == "kB" ? n * 1024 : n;
        }
        return map;
    }

    /// <summary>
    /// /proc/net/dev for real adapters: ones backed by hardware (sys/class/net/X/device) or Wi-Fi.
    /// Skips lo, docker0, veth*, br-*, virbr*, tun/tap etc., which would double-count traffic.
    /// </summary>
    private Dictionary<string, (long Rx, long Tx)> ReadNet()
    {
        var map = new Dictionary<string, (long, long)>();
        foreach (string line in (_fs.Read("proc/net/dev") ?? "").Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string name = line[..colon].Trim();
            if (name == "lo" || !(_fs.Exists($"sys/class/net/{name}/device") || _fs.Exists($"sys/class/net/{name}/wireless"))) continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 9 && long.TryParse(f[0], out long rx) && long.TryParse(f[8], out long tx)) map[name] = (rx, tx);
        }
        return map;
    }

    /// <summary>/proc/diskstats: name, then reads, merged, sectors read, ms, writes, merged, sectors written, ms, in-flight, ms doing I/O.</summary>
    private Dictionary<string, DiskCounters> ReadDisks()
    {
        var map = new Dictionary<string, DiskCounters>();
        foreach (string line in (_fs.Read("proc/diskstats") ?? "").Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13 || !PhysicalDisk.IsMatch(f[2])) continue;
            map[f[2]] = new DiskCounters(L(f[5]), L(f[9]), L(f[12]));
        }
        return map;
    }

    private IReadOnlyList<DriveSample> ReadDrives()
    {
        var list = new List<DriveSample>();
        var seen = new HashSet<string>();
        foreach (string line in (_fs.Read("proc/mounts") ?? "").Split('\n'))
        {
            var f = line.Split(' ');
            if (f.Length < 3 || !f[0].StartsWith("/dev/") || !RealFilesystems.Contains(f[2]) || !seen.Add(f[0])) continue;
            string mount = f[1].Replace("\\040", " ").Replace("\\011", "\t");
            if (_diskSpace(mount) is not { } space || space.Total <= 0) continue;
            list.Add(new DriveSample(mount, $"{Path.GetFileName(f[0])} · {f[2]}", space.Total, space.Free));
        }
        return list;
    }

    private static (long, long)? DriveSpace(string mount)
    {
        try { var d = new DriveInfo(mount); return d.IsReady ? (d.TotalSize, d.AvailableFreeSpace) : null; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ processes

    private readonly record struct ProcRow(int Pid, long Start, string Name, long Jiffies, long Rss);

    /// <summary>
    /// /proc/[pid]/stat: "pid (comm) state ppid ... utime stime ... starttime vsize rss ...".
    /// comm may contain spaces and parentheses, so split at the LAST ')'. Keyed by (pid, start time) so a
    /// reused PID is never mistaken for the old process.
    /// </summary>
    private (Dictionary<(int, long), long> Map, List<ProcRow> Rows) ReadProcesses()
    {
        var map = new Dictionary<(int, long), long>();
        var rows = new List<ProcRow>();
        foreach (string entry in _fs.Entries("proc"))
        {
            if (!int.TryParse(entry, out int pid)) continue;
            string? stat = _fs.Read($"proc/{entry}/stat");
            if (stat == null) continue; // exited meanwhile
            int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
            if (open < 0 || close < open) continue;
            string name = stat[(open + 1)..close];
            var f = stat[(close + 2)..].Split(' '); // f[0] = state (field 3)
            if (f.Length < 22) continue;
            long jiffies = L(f[11]) + L(f[12]);    // utime + stime (fields 14, 15)
            long start = L(f[19]);                 // starttime (field 22)
            long rss = L(f[21]);                   // rss in pages (field 24)
            map[(pid, start)] = jiffies;
            rows.Add(new ProcRow(pid, start, name, jiffies, rss));
        }
        return (map, rows);
    }

    // ------------------------------------------------------------------ GPU, battery

    /// <summary>AMD exposes gpu_busy_percent in sysfs; NVIDIA via nvidia-smi if installed; Intel has no simple counter (n/a).</summary>
    private double? ReadGpu()
    {
        double? amd = _fs.Entries("sys/class/drm")
            .Where(n => Regex.IsMatch(n, @"^card\d+$"))
            .Select(n => _fs.Long($"sys/class/drm/{n}/device/gpu_busy_percent"))
            .Where(v => v != null).Select(v => (double?)v!.Value).Max();
        if (amd != null) return Math.Clamp(amd.Value, 0, 100);
        if (!_nvidiaSmi) return null;

        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000)) { p.Kill(); _nvidiaSmi = false; return null; }
            var values = output.Split('\n').Select(s => double.TryParse(s.Trim(), out double v) ? v : -1).Where(v => v >= 0).ToList();
            return values.Count > 0 ? values.Max() : null;
        }
        catch { _nvidiaSmi = false; return null; } // don't keep retrying a broken tool
    }

    /// <summary>sysfs power_supply: Battery entries (scope "Device" = mouse/keyboard batteries, skipped) and Mains (AC).</summary>
    private BatterySample? ReadBattery()
    {
        bool onAc = false;
        string? battery = null;
        foreach (string ps in _fs.Entries("sys/class/power_supply"))
        {
            string type = _fs.Line($"sys/class/power_supply/{ps}/type");
            if (type == "Mains" && _fs.Line($"sys/class/power_supply/{ps}/online") == "1") onAc = true;
            if (type == "Battery" && _fs.Line($"sys/class/power_supply/{ps}/scope") != "Device") battery ??= ps;
        }
        if (battery == null) return null;

        string b = $"sys/class/power_supply/{battery}";
        int percent = (int)Math.Clamp(_fs.Long($"{b}/capacity") ?? 0, 0, 100);
        string status = _fs.Line($"{b}/status");
        bool charging = status == "Charging";
        onAc |= charging || status is "Full" or "Not charging";

        int secondsLeft = -1;
        if (status == "Discharging")
        {
            // energy (µWh / µW) or charge (µAh / µA): both give hours
            double? left = Ratio(_fs.Long($"{b}/energy_now"), _fs.Long($"{b}/power_now"))
                        ?? Ratio(_fs.Long($"{b}/charge_now"), _fs.Long($"{b}/current_now"));
            if (left is > 0 and < 48) secondsLeft = (int)(left.Value * 3600);
        }
        return new BatterySample(percent, charging, onAc, secondsLeft);
    }

    // ------------------------------------------------------------------ helpers

    private static double? Ratio(long? a, long? b) => a is > 0 && b is > 0 ? (double)a.Value / b.Value : null;

    private static long L(string s) => long.TryParse(s, out long v) ? v : 0;

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, exe)).FirstOrDefault(File.Exists);

    public void Dispose() { }
}
