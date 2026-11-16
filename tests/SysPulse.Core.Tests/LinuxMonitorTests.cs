namespace SysPulse.Core.Tests;

public class LinuxMonitorTests
{
    private const long KiB = 1024;

    /// <summary>A laptop with 2 CPU threads, Wi-Fi + docker bridge, an NVMe and a SATA disk, a battery and an AMD GPU.</summary>
    private static FakeLinux Laptop()
    {
        var f = new FakeLinux();
        f.Write("proc/stat", Stat(all: (100, 50, 800, 50), cpu0: (60, 20, 400, 20), cpu1: (40, 30, 400, 30)))
         .Write("proc/meminfo", "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    6000000 kB\nBuffers:          200000 kB\nCached:          3000000 kB\nSwapTotal:       2000000 kB\nSwapFree:        1500000 kB\n")
         .Write("proc/net/dev", NetDev(wlanRx: 1_000_000, wlanTx: 500_000, dockerRx: 5_000, loRx: 9_999))
         .Dir("sys/class/net/wlan0/wireless")
         .Dir("sys/class/net/docker0")                 // virtual bridge: no "device"
         .Dir("sys/class/net/lo")
         .Write("proc/diskstats", DiskStats(nvmeRead: 1000, nvmeWritten: 0, nvmeMsIo: 100, sdaMsIo: 0))
         .Write("proc/uptime", "12345.67 40000.00\n")
         .Write("proc/mounts",
             "/dev/nvme0n1p2 / ext4 rw,relatime 0 0\n" +
             "/dev/nvme0n1p1 /boot/efi vfat rw 0 0\n" +
             "tmpfs /run tmpfs rw 0 0\n" +
             "/dev/loop0 /snap/core/1 squashfs ro 0 0\n" +
             "/dev/nvme0n1p2 /var/snap ext4 rw 0 0\n" +      // same device mounted twice: count once
             "/dev/sdb1 /mnt/My\\040Disk ntfs3 rw 0 0\n")
         .Write("sys/class/power_supply/BAT0/type", "Battery\n")
         .Write("sys/class/power_supply/BAT0/scope", "System\n")
         .Write("sys/class/power_supply/BAT0/capacity", "80\n")
         .Write("sys/class/power_supply/BAT0/status", "Discharging\n")
         .Write("sys/class/power_supply/BAT0/energy_now", "30000000\n")
         .Write("sys/class/power_supply/BAT0/power_now", "10000000\n")
         .Write("sys/class/power_supply/AC/type", "Mains\n")
         .Write("sys/class/power_supply/AC/online", "0\n")
         .Write("sys/class/power_supply/hidpp_battery_0/type", "Battery\n")   // wireless mouse: ignored
         .Write("sys/class/power_supply/hidpp_battery_0/scope", "Device\n")
         .Write("sys/class/drm/card0/device/gpu_busy_percent", "37\n")
         .Write("sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "2000000\n")
         .Write("sys/devices/system/cpu/cpu1/cpufreq/scaling_cur_freq", "3000000\n")
         .Dir("sys/devices/system/cpu/cpufreq");           // not a cpuN folder: ignored
        Proc(f, 100, "my (weird) app", utime: 0, stime: 0, start: 5000, rssPages: 2560);
        Proc(f, 200, "bash", utime: 10, stime: 0, start: 6000, rssPages: 100);
        Proc(f, 300, "sshd", utime: 0, stime: 0, start: 7000, rssPages: 300);
        return f;
    }

    private static LinuxMonitor Monitor(FakeLinux f) =>
        new(f.Fs, () => f.NowMs,
            mount => mount switch { "/" => (500_000_000_000, 200_000_000_000), "/boot/efi" => (1_000_000_000, 900_000_000), "/mnt/My Disk" => (2_000_000_000_000, 1_000_000_000_000), _ => null },
            pageSize: 4096, allowTools: false);

    /// <summary>Advance 2 seconds and change every counter by a known amount.</summary>
    private static void TwoSecondsLater(FakeLinux f)
    {
        f.NowMs += 2000;
        // all: busy 150 -> 400 (+250), idle+iowait 850 -> 1100 (+250): 50%
        f.Write("proc/stat", Stat(all: (300, 100, 1000, 100), cpu0: (210, 70, 400, 20), cpu1: (90, 30, 600, 80)));
        f.Write("proc/net/dev", NetDev(wlanRx: 1_200_000, wlanTx: 520_000, dockerRx: 900_000, loRx: 99_999));
        f.Write("proc/diskstats", DiskStats(nvmeRead: 3000, nvmeWritten: 4000, nvmeMsIo: 600, sdaMsIo: 1000));
        Proc(f, 100, "my (weird) app", utime: 40, stime: 10, start: 5000, rssPages: 2560); // +50 of 500 jiffies = 10%
        Proc(f, 200, "bash", utime: 0, stime: 0, start: 9000, rssPages: 100);                 // PID reused by a new process
        f.Delete("proc/300");                                                                // sshd exited
        Proc(f, 400, "python3", utime: 5, stime: 0, start: 9500, rssPages: 1000);            // new process
    }

    [Fact]
    public void Cpu_usage_total_and_per_thread()
    {
        using var f = Laptop();
        var m = Monitor(f);
        TwoSecondsLater(f);
        var s = m.Sample();

        Assert.Equal(50, s.CpuPercent, 3);
        // cpu0: busy 80 -> 280 (+200), total 500 -> 700 (+200): 100%;  cpu1: busy 70 -> 120 (+50), total 500 -> 800 (+300): 16.7%
        Assert.Equal(2, s.CorePercents.Length);
        Assert.Equal(100, s.CorePercents[0], 6);
        Assert.Equal(50.0 / 300 * 100, s.CorePercents[1], 6);
        Assert.Equal(2500, s.CpuMhz);
    }

    [Fact]
    public void Memory_uses_MemAvailable_and_swap_goes_in_the_commit_slot()
    {
        using var f = Laptop();
        var s = Monitor(f).Sample();

        Assert.Equal(16_000_000UL * KiB, s.MemTotal);
        Assert.Equal(10_000_000UL * KiB, s.MemUsed);
        Assert.Equal(2_000_000UL * KiB, s.CommitTotal);
        Assert.Equal(500_000UL * KiB, s.CommitUsed);
    }

    [Fact]
    public void Network_counts_physical_adapters_only()
    {
        using var f = Laptop();
        var m = Monitor(f);
        TwoSecondsLater(f);
        var s = m.Sample();

        Assert.Equal(100_000, s.NetDown); // (1.2M - 1.0M) / 2 s; docker0 and lo ignored
        Assert.Equal(10_000, s.NetUp);
    }

    [Fact]
    public void Network_skips_one_reading_when_adapters_change()
    {
        using var f = Laptop();
        var m = Monitor(f);
        TwoSecondsLater(f);
        f.Dir("sys/class/net/eth0/device");
        f.Write("proc/net/dev", NetDev(1_200_000, 520_000, 0, 0) + "  eth0: 999999999 0 0 0 0 0 0 0 888888888 0 0 0 0 0 0 0\n");
        var s = m.Sample();

        Assert.Equal(0, s.NetDown); // not a 1 GB/s spike from eth0's lifetime total
        Assert.Equal(0, s.NetUp);
    }

    [Fact]
    public void Disks_whole_devices_only_and_busiest_disk_is_active_percent()
    {
        using var f = Laptop();
        var m = Monitor(f);
        TwoSecondsLater(f);
        var s = m.Sample();

        Assert.Equal(2000 * 512 / 2.0, s.DiskRead);   // nvme0n1 only; the partition line is ignored
        Assert.Equal(4000 * 512 / 2.0, s.DiskWrite);
        Assert.Equal(50, s.DiskActive);                // sda: 1000 ms of I/O in 2000 ms; nvme0n1: 25%
    }

    [Fact]
    public void Processes_cpu_share_names_with_parentheses_and_reused_pids()
    {
        using var f = Laptop();
        var m = Monitor(f);
        TwoSecondsLater(f);
        var s = m.Sample();

        Assert.Equal(3, s.ProcessCount);
        var app = s.TopProcesses!.Single(p => p.Pid == 100);
        Assert.Equal("my (weird) app", app.Name);
        Assert.Equal(10, app.Cpu, 6);                          // 50 of 500 jiffies
        Assert.Equal(2560L * 4096, app.Memory);
        Assert.Equal(0, s.TopProcesses!.Single(p => p.Pid == 200).Cpu); // new process with an old PID: no bogus delta
        Assert.Equal(0, s.TopProcesses!.Single(p => p.Pid == 400).Cpu); // first time seen
        Assert.Equal(100, s.TopProcesses![0].Pid);             // sorted by CPU
    }

    [Fact]
    public void Processes_can_be_skipped()
    {
        using var f = Laptop();
        var s = Monitor(f).Sample(includeProcesses: false);
        Assert.Null(s.TopProcesses);
        Assert.Null(s.ProcessCount);
    }

    [Fact]
    public void Drives_real_filesystems_once_each_with_unescaped_names()
    {
        using var f = Laptop();
        var s = Monitor(f).Sample();

        Assert.Equal(["/", "/boot/efi", "/mnt/My Disk"], s.Drives.Select(d => d.Name).ToArray());
        Assert.Equal("nvme0n1p2 · ext4", s.Drives[0].Label);
        Assert.Equal(500_000_000_000, s.Drives[0].Total);
    }

    [Fact]
    public void Battery_gpu_and_uptime()
    {
        using var f = Laptop();
        var s = Monitor(f).Sample();

        Assert.NotNull(s.Battery);
        Assert.Equal(80, s.Battery!.Percent);
        Assert.False(s.Battery.Charging);
        Assert.False(s.Battery.OnAc);
        Assert.Equal(3 * 3600, s.Battery.SecondsLeft);   // 30 Wh / 10 W
        Assert.Equal(37, s.GpuPercent);
        Assert.Equal(TimeSpan.FromSeconds(12345.67), s.Uptime);
    }

    [Fact]
    public void Desktop_without_battery_gpu_or_cpufreq()
    {
        using var f = Laptop();
        f.Delete("sys/class/power_supply/BAT0").Delete("sys/class/drm").Delete("sys/devices/system/cpu");
        f.Write("proc/cpuinfo", "processor\t: 0\ncpu MHz\t\t: 3400.000\n\nprocessor\t: 1\ncpu MHz\t\t: 3600.000\n");
        var s = Monitor(f).Sample();

        Assert.Null(s.Battery);   // only the mouse battery is left, and it's skipped
        Assert.Null(s.GpuPercent);
        Assert.Equal(3500, s.CpuMhz);
    }

    // ------------------------------------------------------------------ fixture text

    private static string Stat((long User, long System, long Idle, long Iowait) all,
                               (long User, long System, long Idle, long Iowait) cpu0,
                               (long User, long System, long Idle, long Iowait) cpu1)
    {
        static string Line(string name, (long U, long S, long I, long W) c) => $"{name}  {c.U} 0 {c.S} {c.I} {c.W} 0 0 0 0 0";
        return $"{Line("cpu", all)}\n{Line("cpu0", cpu0)}\n{Line("cpu1", cpu1)}\nintr 12345 0 0\nctxt 999\nbtime 1700000000\nprocesses 4000\n";
    }

    private static string NetDev(long wlanRx, long wlanTx, long dockerRx, long loRx) =>
        "Inter-|   Receive                                                |  Transmit\n" +
        " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n" +
        $"    lo: {loRx} 10 0 0 0 0 0 0 {loRx} 10 0 0 0 0 0 0\n" +
        $"wlan0: {wlanRx} 100 0 0 0 0 0 0 {wlanTx} 50 0 0 0 0 0 0\n" +
        $"docker0: {dockerRx} 5 0 0 0 0 0 0 {dockerRx} 5 0 0 0 0 0 0\n";

    private static string DiskStats(long nvmeRead, long nvmeWritten, long nvmeMsIo, long sdaMsIo) =>
        $" 259       0 nvme0n1 500 0 {nvmeRead} 100 600 0 {nvmeWritten} 200 0 {nvmeMsIo} 300 0 0 0 0\n" +
        $" 259       1 nvme0n1p1 50 0 999999 10 0 0 999999 0 0 999999 10 0 0 0 0\n" +
        $"   8       0 sda 10 0 0 5 0 0 0 0 0 {sdaMsIo} 5 0 0 0 0\n" +
        "   7       0 loop0 10 0 80 1 0 0 0 0 0 999 1 0 0 0 0\n";

    private static void Proc(FakeLinux f, int pid, string comm, long utime, long stime, long start, long rssPages)
    {
        // fields: pid (comm) state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime
        //         priority nice num_threads itrealvalue starttime vsize rss ...
        f.Write($"proc/{pid}/stat",
            $"{pid} ({comm}) S 1 {pid} {pid} 0 -1 4194560 100 0 0 0 {utime} {stime} 0 0 20 0 1 0 {start} 123456789 {rssPages} 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0\n");
    }
}
