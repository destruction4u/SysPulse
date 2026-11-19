using System.Text;

namespace SysPulse.Core.Tests;

/// <summary>Feeds the AndroidMonitor / AndroidInfo canned `adb exec-out` output (captured from a real phone's layout).</summary>
public class AndroidMonitorTests(Xunit.Abstractions.ITestOutputHelper? output = null)
{
    private readonly Xunit.Abstractions.ITestOutputHelper? _out = output;

    /// <summary>Returns queued frames in order; the last one repeats if asked again.</summary>
    private sealed class FakeAdb(params string[] frames) : IAdbShell
    {
        private int _i;
        public string Run(string command) => frames[Math.Min(_i++, frames.Length - 1)];
    }

    private static string Frame(string stat, string mem, string net, string up, string freq, string gpu, string bat, string proc, string disk = "", string df = "")
    {
        var sb = new StringBuilder();
        void Sec(string k, string body) { sb.Append("###").Append(k).Append('\n').Append(body); if (!body.EndsWith('\n')) sb.Append('\n'); }
        Sec("STAT", stat); Sec("MEM", mem); Sec("NET", net); Sec("DISK", disk);
        Sec("DF", df); Sec("UP", up); Sec("FREQ", freq); Sec("GPU", gpu); Sec("BAT", bat); Sec("PROC", proc);
        return sb.ToString();
    }

    private static string ProcLine(int pid, string name, long utime, long stime, long start, long rssPages) =>
        $"{pid} ({name}) S 0 0 0 0 -1 0 0 0 0 0 {utime} {stime} 0 0 20 0 1 0 {start} 0 {rssPages}\n";

    private static AndroidMonitor Monitor(FakeAdb adb, Func<long> clock) => new(adb, clock, pageSize: 4096, processesEvery: 1);

    [Fact]
    public void Cpu_memory_network_uptime_battery_are_computed_from_deltas()
    {
        string f0 = Frame(
            stat: "cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 50 0 50 400 0 0 0 0 0 0\ncpu1 50 0 50 400 0 0 0 0 0 0\n",
            mem: "MemTotal:  8000000 kB\nMemAvailable:  4000000 kB\nSwapTotal:  2000000 kB\nSwapFree:  1500000 kB\n",
            net: " wlan0: 1000000 10 0 0 0 0 0 0 500000 5 0 0 0 0 0 0\n",
            up: "1000.00 9000.00\n", freq: "2000000\n2400000\n", gpu: "", bat: "",
            proc: ProcLine(100, "surfaceflinger", 50, 50, 5000, 2560));
        string f1 = Frame(
            stat: "cpu  200 0 200 1400 0 0 0 0 0 0\ncpu0 100 0 100 700 0 0 0 0 0 0\ncpu1 100 0 100 700 0 0 0 0 0 0\n",
            mem: "MemTotal:  8000000 kB\nMemAvailable:  4000000 kB\nSwapTotal:  2000000 kB\nSwapFree:  1500000 kB\n",
            net: " wlan0: 1500000 20 0 0 0 0 0 0 600000 7 0 0 0 0 0 0\n",
            up: "1500.00 9500.00\n", freq: "1800000\n2400000\n", gpu: "",
            bat: "  AC powered: false\n  USB powered: true\n  status: 2\n  level: 50\n  scale: 100\n",
            proc: ProcLine(100, "surfaceflinger", 150, 50, 5000, 2560)); // +100 jiffies

        long t = 0;
        var m = Monitor(new FakeAdb(f0, f1), () => t);
        m.Sample();                 // baseline (establishes deltas)
        t += 1000;
        var s = m.Sample();

        Assert.Equal(25, s.CpuPercent, 1);                 // (800-600)/800
        Assert.Equal(2, s.CorePercents.Length);
        Assert.All(s.CorePercents, c => Assert.Equal(25, c, 1));
        Assert.Equal(2100, s.CpuMhz!.Value, 0);            // (1800+2400)/2 kHz → MHz
        Assert.Equal(8000000UL * 1024, s.MemTotal);
        Assert.Equal(4000000UL * 1024, s.MemUsed);         // total - available
        Assert.Equal(500000UL * 1024, s.CommitUsed);       // swap used
        Assert.Equal(500000, s.NetDown, 0);                // (1.5M-1.0M)/1s
        Assert.Equal(100000, s.NetUp, 0);                  // (600k-500k)/1s
        Assert.Equal(TimeSpan.FromSeconds(1500), s.Uptime);
        Assert.NotNull(s.Battery);
        Assert.Equal(50, s.Battery!.Percent);
        Assert.True(s.Battery.Charging);
        Assert.True(s.Battery.OnAc);
        Assert.Null(s.GpuPercent);                         // no kgsl node → n/a
        Assert.Null(s.DiskActive);                         // diskstats blocked → null, not zero
    }

    [Fact]
    public void Top_process_cpu_share_uses_the_all_cpu_window()
    {
        // Aggregate jiffies advance by 800 (1000→1800); the process gains 100 → 12.5% of the machine.
        string f0 = Frame("cpu  1000 0 0 0 0 0 0 0 0 0\n", "MemTotal: 8000000 kB\nMemAvailable: 4000000 kB\n",
            " wlan0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n", "10 0\n", "", "", "",
            ProcLine(100, "chrome", 50, 50, 5000, 25000));
        string f1 = Frame("cpu  1800 0 0 0 0 0 0 0 0 0\n", "MemTotal: 8000000 kB\nMemAvailable: 4000000 kB\n",
            " wlan0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n", "11 0\n", "", "", "",
            ProcLine(100, "chrome", 150, 50, 5000, 25000)); // +100 jiffies

        long t = 0;
        var m = Monitor(new FakeAdb(f0, f1), () => t);
        m.Sample(); t += 1000;
        var s = m.Sample(includeProcesses: true);

        var chrome = Assert.Single(s.TopProcesses!);
        Assert.Equal("chrome", chrome.Name);
        Assert.Equal(12.5, chrome.Cpu, 1);
        Assert.Equal(25000L * 4096, chrome.Memory);        // rss pages × page size
        Assert.Equal(1, s.ProcessCount);
    }

    [Fact]
    public void Df_reports_internal_storage_and_skips_the_fuse_overlay()
    {
        string df =
            "Filesystem        1K-blocks    Used Available Use% Mounted on\n" +
            "/dev/block/dm-59  111851488 8806356 102508264   8% /data\n" +
            "/dev/fuse         111851488 8806364 102508256   8% /storage/emulated\n" +
            "tmpfs                 65536       0     65536   0% /dev\n";
        var drives = AndroidMonitor.ParseDf(df);
        var d = Assert.Single(drives);
        Assert.Equal("Internal storage", d.Name);
        Assert.Equal(111851488L * 1024, d.Total);
        Assert.Equal(102508264L * 1024, d.Free);
    }

    [Fact]
    public void Battery_full_on_usb_is_on_ac_but_not_charging()
    {
        var b = AndroidMonitor.ParseBattery("  AC powered: false\n  USB powered: true\n  status: 5\n  level: 100\n  scale: 100\n");
        Assert.NotNull(b);
        Assert.Equal(100, b!.Percent);
        Assert.False(b.Charging);   // status 5 = full
        Assert.True(b.OnAc);        // USB powered
    }

    [Fact]
    public void Gpu_parses_kgsl_busy_over_total()
    {
        Assert.Equal(25, AndroidMonitor.ParseGpu("250 1000\n")!.Value, 1);
        Assert.Null(AndroidMonitor.ParseGpu(""));            // node unreadable
        Assert.Null(AndroidMonitor.ParseGpu("Permission denied\n"));
    }

    /// <summary>
    /// End-to-end against a real phone. Opt-in: set SYSPULSE_LIVE_ADB=1 (and SYSPULSE_ADB to adb.exe if it
    /// isn't on PATH). Skips otherwise, so CI and dev machines without a device stay green.
    /// </summary>
    [Fact]
    public void Live_device_produces_a_plausible_sample()
    {
        if (Environment.GetEnvironmentVariable("SYSPULSE_LIVE_ADB") != "1") return; // skipped

        string adb = AdbClient.FindAdb() ?? throw new InvalidOperationException("adb not found");
        var device = AdbClient.ListDevices(adb).FirstOrDefault(d => d.IsReady)
            ?? throw new InvalidOperationException("no device in 'device' state");

        var client = new AdbClient(adb, device.Serial);
        var info = AndroidInfo.Collect(client);
        Assert.Equal("Android", info.Platform);
        Assert.True(info.LogicalProcessors > 0);

        var m = new AndroidMonitor(client);
        Thread.Sleep(1000);
        var s = m.Sample();

        _out?.WriteLine($"{info.Model} · {info.Os} · {info.Cpu} · {info.LogicalProcessors} cores ({info.CpuTopology}) · {info.LocalIp}");
        _out?.WriteLine($"CPU {s.CpuPercent:0}% @ {s.CpuMhz:0} MHz  |  RAM {s.MemUsed / 1048576}/{s.MemTotal / 1048576} MB  |  ↓{s.NetDown / 1024:0} ↑{s.NetUp / 1024:0} KB/s");
        _out?.WriteLine($"Battery {s.Battery?.Percent}% (charging={s.Battery?.Charging})  |  up {s.Uptime:dd\\.hh\\:mm\\:ss}  |  procs {s.ProcessCount}");
        foreach (var p in s.TopProcesses!.Take(5)) _out?.WriteLine($"   {p.Cpu,5:0.0}%  {p.Memory / 1048576,5} MB  {p.Name}");

        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.True(s.MemTotal > 0 && s.MemUsed <= s.MemTotal);
        Assert.True(s.Uptime > TimeSpan.Zero);
        Assert.NotNull(s.TopProcesses);
        Assert.NotEmpty(s.TopProcesses!);
    }

    [Fact]
    public void Info_parses_props_cores_topology_and_wlan_ip()
    {
        var sb = new StringBuilder();
        sb.Append("motorola\n");
        sb.Append("###MODEL\nmoto g power - 2026\n");
        sb.Append("###REL\n16\n");
        sb.Append("###BOARD\nmt6835\n");
        sb.Append("###HW\nmt6835\n");
        sb.Append("###KERN\n5.15.189-android13 aarch64\n");
        sb.Append("###CPUINFO\n");
        for (int i = 0; i < 8; i++) sb.Append($"processor\t: {i}\nCPU part\t: {(i < 6 ? "0xd05" : "0xd0b")}\n");
        sb.Append("###MEM\nMemTotal:  7842352 kB\n");
        sb.Append("###IP\n1: lo    inet 127.0.0.1/8 scope host lo\\\n36: wlan0    inet 192.168.1.189/24 brd 192.168.1.255 scope global wlan0\\\n");

        var info = AndroidInfo.Parse(sb.ToString());

        Assert.Equal("Android", info.Platform);
        Assert.Equal("Android 16", info.Os);
        Assert.Equal("motorola moto g power - 2026", info.Model);
        Assert.Equal("mt6835", info.Cpu);                 // no cpuinfo "Hardware" line → board
        Assert.Equal(8, info.LogicalProcessors);
        Assert.Equal("6x A55 + 2x A76", info.CpuTopology);
        Assert.Equal("192.168.1.189 (wlan0)", info.LocalIp);
        Assert.Equal("5.15.189-android13 aarch64", info.Kernel);
    }
}
