using System.Globalization;
using System.Text.RegularExpressions;

namespace SysPulse.Core;

/// <summary>
/// Live readings from an Android device over adb. There's no agent on the phone, so each sample is
/// one batched `adb exec-out` that cats the same /proc files top/htop read, plus `dumpsys battery`.
/// Everything is computed from deltas between two readings, exactly like <see cref="LinuxMonitor"/>;
/// Android's quirks (toybox, SELinux-limited /proc, battery via dumpsys) are why this is its own reader.
/// </summary>
public sealed class AndroidMonitor : ISystemMonitor
{
    private static readonly Regex PhysicalDisk = new(@"^(sd[a-z]+|nvme\d+n\d+|mmcblk\d+|dm-\d+)$", RegexOptions.Compiled);

    // One batched device command. Begins with a marker so every section parses the same way.
    private const string LiveBase =
        "echo '###STAT'; cat /proc/stat;" +
        " echo '###MEM'; cat /proc/meminfo;" +
        " echo '###NET'; cat /proc/net/dev;" +
        " echo '###DISK'; cat /proc/diskstats;" +
        " echo '###DF'; df -k 2>/dev/null;" +
        " echo '###UP'; cat /proc/uptime;" +
        " echo '###FREQ'; cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq 2>/dev/null;" +
        " echo '###GPU'; cat /sys/class/kgsl/kgsl-3d0/gpubusy 2>/dev/null;" +
        " echo '###BAT'; dumpsys battery 2>/dev/null";
    private const string ProcTail = "; echo '###PROC'; cat /proc/[0-9]*/stat 2>/dev/null";

    private const int ProcessesDefault = 2, DfEvery = 5;

    private readonly IAdbShell _shell;
    private readonly Func<long> _clockMs;
    private readonly int _pageSize;
    private readonly int _processesEvery;

    private Dictionary<int, (long Total, long Idle)> _lastCpu = new();
    private Dictionary<string, (long Rx, long Tx)> _lastNet = new();
    private Dictionary<string, (long Read, long Written, long MsIo)> _lastDisk = new();
    private Dictionary<(int Pid, long Start), long> _lastProc = new();
    private long _lastProcTotalJiffies;
    private long _lastMs;
    private int _tick;

    private IReadOnlyList<ProcSample>? _top;
    private int? _processCount;
    private IReadOnlyList<DriveSample> _drives = [];

    /// <summary>Production: real device clock, default page size (overridden once probed), skip process scan every other tick.</summary>
    public AndroidMonitor(IAdbShell shell)
        : this(shell, () => Environment.TickCount64, ProbePageSize(shell), ProcessesDefault)
    {
        // Prime the delta baselines so the first real sample ~1 s later is meaningful.
        Prime();
    }

    internal AndroidMonitor(IAdbShell shell, Func<long> clockMs, int pageSize, int processesEvery)
    {
        _shell = shell;
        _clockMs = clockMs;
        _pageSize = pageSize > 0 ? pageSize : 4096;
        _processesEvery = Math.Max(1, processesEvery);
        _lastMs = _clockMs();
    }

    private void Prime()
    {
        var s = Read(LiveBase + ProcTail);
        _lastCpu = ParseCpu(Sec(s, "STAT"));
        _lastNet = ParseNet(Sec(s, "NET"));
        _lastDisk = ParseDisk(Sec(s, "DISK"));
        long total = _lastCpu.TryGetValue(-1, out var all) ? all.Total : 0;
        // Baseline the process jiffies too, so the first real sample already shows per-process CPU%.
        (_lastProc, _) = ParseProcesses(Sec(s, "PROC"), total);
        _lastProcTotalJiffies = total;
        _lastMs = _clockMs();
    }

    public Sample Sample(bool includeProcesses = true)
    {
        _tick++;
        bool scanProc = includeProcesses && (_top == null || (_tick - 1) % _processesEvery == 0);
        var s = Read(scanProc ? LiveBase + ProcTail : LiveBase);

        long now = _clockMs();
        double seconds = Math.Max((now - _lastMs) / 1000.0, 0.001);
        _lastMs = now;

        // ---- CPU (aggregate + per thread), keyed by core index so offline/hotplugged cores stay aligned
        var cpu = ParseCpu(Sec(s, "STAT"));
        double total = Usage(_lastCpu, cpu, -1);
        double[] cores = cpu.Keys.Where(k => k >= 0).OrderBy(k => k).Select(k => Usage(_lastCpu, cpu, k)).ToArray();
        long cpuTotalNow = cpu.TryGetValue(-1, out var allNow) ? allNow.Total : 0;
        _lastCpu = cpu;

        // ---- Memory (+ swap in the "commit" slot, same as Linux)
        var mem = ParseMeminfo(Sec(s, "MEM"));
        ulong memTotal = mem.GetValueOrDefault("MemTotal");
        ulong available = mem.TryGetValue("MemAvailable", out ulong a) ? a
            : mem.GetValueOrDefault("MemFree") + mem.GetValueOrDefault("Buffers") + mem.GetValueOrDefault("Cached");
        ulong swapTotal = mem.GetValueOrDefault("SwapTotal");
        ulong swapUsed = swapTotal - Math.Min(swapTotal, mem.GetValueOrDefault("SwapFree"));

        // ---- Network (Wi-Fi + cellular + ethernet); skip a reading if the adapter set changed
        var net = ParseNet(Sec(s, "NET"));
        double down = 0, up = 0;
        if (net.Keys.ToHashSet().SetEquals(_lastNet.Keys))
        {
            down = Math.Max(0, net.Sum(n => n.Value.Rx - _lastNet[n.Key].Rx) / seconds);
            up = Math.Max(0, net.Sum(n => n.Value.Tx - _lastNet[n.Key].Tx) / seconds);
        }
        _lastNet = net;

        // ---- Disk (eMMC/UFS): bytes/s and busiest-device active %
        var disk = ParseDisk(Sec(s, "DISK"));
        double read = 0, write = 0, active = 0;
        foreach (var (name, d) in disk)
        {
            if (!_lastDisk.TryGetValue(name, out var p)) continue;
            read += Math.Max(0, d.Read - p.Read) * 512.0 / seconds;
            write += Math.Max(0, d.Written - p.Written) * 512.0 / seconds;
            active = Math.Max(active, Math.Max(0, d.MsIo - p.MsIo) / (seconds * 1000) * 100);
        }
        _lastDisk = disk;

        // ---- Storage volumes (occasionally; df is cheap but static)
        if (_tick % DfEvery == 1 || _drives.Count == 0) _drives = ParseDf(Sec(s, "DF"));

        // ---- Processes
        if (scanProc)
        {
            var (map, list) = ParseProcesses(Sec(s, "PROC"), cpuTotalNow);
            _lastProc = map;
            _lastProcTotalJiffies = cpuTotalNow;
            _top = list;
        }

        return new Sample(
            Math.Clamp(total, 0, 100), AvgMhz(Sec(s, "FREQ")), cores,
            memTotal, memTotal - Math.Min(memTotal, available), swapTotal, swapUsed,
            down, up,
            disk.Count > 0 ? Math.Clamp(active, 0, 100) : null, disk.Count > 0 ? read : null, disk.Count > 0 ? write : null,
            ParseGpu(Sec(s, "GPU")),
            includeProcesses ? _top : null, includeProcesses ? _processCount : null,
            _drives, ParseBattery(Sec(s, "BAT")),
            ParseUptime(Sec(s, "UP")));
    }

    public void Dispose() { }

    // ---------------------------------------------------------------- parsing

    /// <summary>Runs the batched command and splits its output into "###KEY" sections.</summary>
    private Dictionary<string, string> Read(string command) => Split(_shell.Run(command));

    /// <summary>/proc/stat cpu lines → index (-1 for the aggregate "cpu") to (total, idle+iowait).</summary>
    internal static Dictionary<int, (long Total, long Idle)> ParseCpu(string section)
    {
        var map = new Dictionary<int, (long, long)>();
        foreach (string line in section.Split('\n'))
        {
            if (!line.StartsWith("cpu")) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 5) continue;
            int index = f[0] == "cpu" ? -1 : int.TryParse(f[0].AsSpan(3), out int n) ? n : int.MaxValue;
            long[] v = f.Skip(1).Take(8).Select(x => long.TryParse(x, out long y) ? y : 0).ToArray();
            long idle = v[3] + (v.Length > 4 ? v[4] : 0);
            map[index] = (v.Sum(), idle);
        }
        return map;
    }

    private static double Usage(Dictionary<int, (long Total, long Idle)> before, Dictionary<int, (long Total, long Idle)> after, int key)
    {
        if (!before.TryGetValue(key, out var b) || !after.TryGetValue(key, out var a)) return 0;
        long dTotal = a.Total - b.Total, dIdle = a.Idle - b.Idle;
        return dTotal > 0 ? Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100) : 0;
    }

    internal static Dictionary<string, ulong> ParseMeminfo(string section)
    {
        var map = new Dictionary<string, ulong>();
        foreach (string line in section.Split('\n'))
        {
            var p = line.Split(':', 2);
            if (p.Length < 2) continue;
            var v = p[1].Trim().Split(' ');
            if (ulong.TryParse(v[0], out ulong n)) map[p[0].Trim()] = v.Length > 1 && v[1] == "kB" ? n * 1024 : n;
        }
        return map;
    }

    /// <summary>Real adapters only: Wi-Fi (wlan), cellular (rmnet) and ethernet. Skips lo and virtual ifaces.</summary>
    internal static Dictionary<string, (long Rx, long Tx)> ParseNet(string section)
    {
        var map = new Dictionary<string, (long, long)>();
        foreach (string line in section.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string name = line[..colon].Trim();
            if (!(name.StartsWith("wlan") || name.StartsWith("rmnet") || name.StartsWith("eth"))) continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 9 && long.TryParse(f[0], out long rx) && long.TryParse(f[8], out long tx)) map[name] = (rx, tx);
        }
        return map;
    }

    /// <summary>/proc/diskstats: sectors read [5], sectors written [9], ms doing I/O [12]. Whole devices only.</summary>
    internal static Dictionary<string, (long Read, long Written, long MsIo)> ParseDisk(string section)
    {
        var map = new Dictionary<string, (long, long, long)>();
        foreach (string line in section.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13 || !PhysicalDisk.IsMatch(f[2])) continue;
            map[f[2]] = (L(f[5]), L(f[9]), L(f[12]));
        }
        return map;
    }

    /// <summary>`df -k` → a DriveSample per real mount (internal storage, sdcard). Sizes are 1K blocks.</summary>
    internal static IReadOnlyList<DriveSample> ParseDf(string section)
    {
        var list = new List<DriveSample>();
        var seen = new HashSet<string>();
        foreach (string line in section.Split('\n').Skip(1)) // header
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6) continue;
            string fsName = f[0], mount = f[^1];
            // Real block devices only. /dev/fuse at /storage/emulated is a view of /data, so skip it to avoid double-counting.
            if (!fsName.StartsWith("/dev/block/")) continue;
            bool internalData = mount == "/data";
            bool externalSd = mount.StartsWith("/mnt/media_rw") || (mount.StartsWith("/storage/") && mount != "/storage/emulated");
            if (!(internalData || externalSd)) continue;
            if (!long.TryParse(f[1], out long totalK) || totalK <= 0) continue;
            long availK = long.TryParse(f[3], out long av) ? av : 0;
            if (!seen.Add(mount)) continue;
            string label = internalData ? "Internal storage" : mount;
            list.Add(new DriveSample(label, "eMMC/UFS", totalK * 1024, availK * 1024));
        }
        return list;
    }

    private readonly record struct ProcRow(int Pid, long Start, string Name, long Jiffies, long Rss);

    /// <summary>/proc/[pid]/stat: "pid (comm) state ... utime[11] stime[12] ... starttime[19] vsize[20] rss[21]" after "(comm) ".</summary>
    private (Dictionary<(int, long), long> Map, List<ProcSample> Top) ParseProcesses(string section, long cpuTotalNow)
    {
        long window = cpuTotalNow - _lastProcTotalJiffies;
        var map = new Dictionary<(int, long), long>(_lastProc.Count + 16);
        var rows = new List<ProcRow>(_lastProc.Count + 16);

        foreach (string line in section.Split('\n'))
        {
            int open = line.IndexOf('('), close = line.LastIndexOf(')');
            if (open < 0 || close < open || close + 2 >= line.Length) continue;
            if (!int.TryParse(line.AsSpan(0, open).Trim(), out int pid)) continue;
            string name = line.Substring(open + 1, close - open - 1);

            var rest = line[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // rest[0] = state, so utime=rest[11], stime=rest[12], starttime=rest[19], rss=rest[21]
            if (rest.Length < 22) continue;
            long jiffies = L(rest[11]) + L(rest[12]);
            long start = L(rest[19]);
            long rss = L(rest[21]);

            map[(pid, start)] = jiffies;
            rows.Add(new ProcRow(pid, start, name, jiffies, rss));
        }

        var top = new List<ProcSample>(rows.Count);
        foreach (var r in rows)
        {
            double share = window > 0 && _lastProc.TryGetValue((r.Pid, r.Start), out long prev)
                ? Math.Clamp(100.0 * (r.Jiffies - prev) / window, 0, 100) : 0;
            top.Add(new ProcSample(r.Name, r.Pid, share, r.Rss * _pageSize));
        }
        _processCount = rows.Count;
        var ranked = top.OrderByDescending(p => Math.Round(p.Cpu, 1)).ThenByDescending(p => p.Memory).Take(12).ToList();
        return (map, ranked);
    }

    /// <summary>Average current CPU frequency (kHz lines from cpufreq) in MHz.</summary>
    internal static double? AvgMhz(string section)
    {
        long sum = 0; int n = 0;
        foreach (string line in section.Split('\n'))
            if (long.TryParse(line.Trim(), out long khz) && khz > 0) { sum += khz; n++; }
        return n > 0 ? sum / (double)n / 1000.0 : null;
    }

    /// <summary>Adreno kgsl "busy total" (reset on read) → busy fraction as a percent. null if unavailable.</summary>
    internal static double? ParseGpu(string section)
    {
        var f = section.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length >= 2 && long.TryParse(f[0], out long busy) && long.TryParse(f[1], out long total) && total > 0)
            return Math.Clamp(100.0 * busy / total, 0, 100);
        return null;
    }

    /// <summary>`dumpsys battery`: level, scale, status (2=charging, 5=full) and the *-powered flags.</summary>
    internal static BatterySample? ParseBattery(string section)
    {
        if (section.Trim().Length == 0) return null;
        int level = -1, scale = 100, status = 0;
        bool onAc = false;
        foreach (string line in section.Split('\n'))
        {
            var p = line.Split(':', 2);
            if (p.Length < 2) continue;
            string k = p[0].Trim(), v = p[1].Trim();
            switch (k)
            {
                case "level": int.TryParse(v, out level); break;
                case "scale": int.TryParse(v, out scale); break;
                case "status": int.TryParse(v, out status); break;
                case "AC powered" or "USB powered" or "Wireless powered" or "Dock powered":
                    if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) onAc = true;
                    break;
            }
        }
        if (level < 0) return null;
        int percent = scale > 0 ? (int)Math.Clamp(level * 100L / scale, 0, 100) : Math.Clamp(level, 0, 100);
        bool charging = status == 2;          // BatteryManager.BATTERY_STATUS_CHARGING
        onAc |= charging || status == 5;      // 5 = full
        return new BatterySample(percent, charging, onAc, -1); // Android doesn't expose a reliable time-to-empty without root
    }

    private static TimeSpan ParseUptime(string section)
    {
        string first = section.Trim().Split(' ').FirstOrDefault() ?? "0";
        return TimeSpan.FromSeconds(double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double up) ? up : 0);
    }

    private static int ProbePageSize(IAdbShell shell) =>
        int.TryParse(shell.Run("getconf PAGE_SIZE").Trim(), out int p) && p > 0 ? p : 4096;

    // ---------------------------------------------------------------- section splitter

    private static string Sec(Dictionary<string, string> s, string key) => s.TryGetValue(key, out var v) ? v : "";

    /// <summary>Splits the batched output into its "###KEY" sections.</summary>
    internal static Dictionary<string, string> Split(string output)
    {
        var map = new Dictionary<string, string>();
        string current = "";
        var sb = new System.Text.StringBuilder();
        foreach (string raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("###"))
            {
                map[current] = sb.ToString();
                sb.Clear();
                current = raw[3..].Trim();
            }
            else sb.Append(raw).Append('\n');
        }
        map[current] = sb.ToString();
        return map;
    }

    private static long L(string s) => long.TryParse(s, out long v) ? v : 0;
}
