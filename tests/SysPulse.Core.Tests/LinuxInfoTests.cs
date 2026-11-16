namespace SysPulse.Core.Tests;

public class LinuxInfoTests
{
    [Fact]
    public void Laptop_facts_come_from_os_release_dmi_cpuinfo_and_drm()
    {
        using var fake = new FakeLinux();
        fake.Write("etc/os-release", """
                NAME="Ubuntu"
                VERSION="24.04.1 LTS (Noble Numbat)"
                PRETTY_NAME="Ubuntu 24.04.1 LTS"
                ID=ubuntu
                """)
            .Write("proc/sys/kernel/osrelease", "6.8.0-45-generic\n")
            .Write("proc/sys/kernel/hostname", "thinkpad\n")
            .Write("sys/class/dmi/id/sys_vendor", "LENOVO\n")
            .Write("sys/class/dmi/id/product_name", "21HMCTO1WW\n")
            .Write("sys/class/dmi/id/product_version", "ThinkPad X1 Carbon Gen 11\n")
            .Write("sys/class/dmi/id/bios_version", "N3XET45W (1.20 )\n")
            .Write("sys/class/dmi/id/bios_date", "05/06/2024\n")
            .Write("proc/cpuinfo", CpuInfoBlock(0, 0) + CpuInfoBlock(1, 0) + CpuInfoBlock(2, 1) + CpuInfoBlock(3, 1))
            .Write("sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "5200000\n")
            .Write("sys/class/drm/card1/device/vendor", "0x8086\n")
            .Write("sys/class/drm/card1/device/device", "0xa7a1\n")
            .Write("sys/class/drm/card1/device/uevent", "DRIVER=i915\nPCI_CLASS=30000\n")
            .Write("sys/class/drm/card1-eDP-1/status", "connected\n")
            .Write("sys/class/drm/card1-eDP-1/modes", "1920x1200\n1600x1200\n")
            .Write("sys/class/drm/card1-HDMI-A-1/status", "disconnected\n")
            .Write("usr/share/hwdata/pci.ids", "# pci.ids\n10de  NVIDIA Corporation\n\t1234  Something\n8086  Intel Corporation\n\ta7a0  Raptor Lake-P [Iris Xe Graphics]\n\ta7a1  Raptor Lake-P [Iris Xe Graphics]\n9004  Adaptec\n")
            .Dir("run/user/1000")
            .Dir("run/user/0")
            .Write("etc/passwd", "root:x:0:0:root:/root:/bin/bash\nalex:x:1000:1000:Alex,,,:/home/alex:/bin/bash\n")
            .Write("etc/locale.conf", "LANG=en_GB.UTF-8\n");

        var i = LinuxInfo.Collect(fake.Fs, live: false);

        Assert.Equal("Linux", i.Platform);
        Assert.StartsWith("Ubuntu 24.04.1 LTS ", i.Os);
        Assert.Equal("6.8.0-45-generic", i.Kernel);
        Assert.Equal("thinkpad", i.Host);
        Assert.Equal("LENOVO ThinkPad X1 Carbon Gen 11", i.Model);
        Assert.Equal("N3XET45W (1.20 ) (05/06/2024)", i.Bios);
        Assert.Equal("13th Gen Intel Core i7-1365U", i.Cpu);
        Assert.Equal("2 cores / 4 threads", i.CpuTopology);
        Assert.Equal(4, i.LogicalProcessors);
        Assert.Equal(5200, i.CpuBaseMhz);
        Assert.Equal(["Intel Iris Xe Graphics"], i.Gpus);
        Assert.Equal(["1920x1200"], i.Displays);
        Assert.Equal("alex", i.User);
        Assert.Equal("en-GB", i.Locale);
        Assert.Equal("", i.Theme);
    }

    [Fact]
    public void Raspberry_pi_uses_device_tree_model_and_has_no_dmi()
    {
        using var fake = new FakeLinux();
        string cpu = string.Concat(Enumerable.Range(0, 4).Select(n => $"processor\t: {n}\nBogoMIPS\t: 108.00\nCPU implementer\t: 0x41\nCPU part\t: 0xd0b\n\n"));
        fake.Write("etc/os-release", "PRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\n")
            .Write("proc/sys/kernel/osrelease", "6.6.51+rpt-rpi-2712\n")
            .Write("proc/device-tree/model", "Raspberry Pi 5 Model B Rev 1.0\0")
            .Write("proc/cpuinfo", cpu + "Revision\t: d04170\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n")
            .Write("sys/class/drm/card0/device/uevent", "DRIVER=vc4-drm\n")
            .Write("sys/class/dmi/id/sys_vendor", "To be filled by O.E.M.\n"); // junk must be ignored

        var i = LinuxInfo.Collect(fake.Fs, live: false);

        Assert.Equal("Raspberry Pi 5 Model B Rev 1.0", i.Model);
        Assert.Equal("Raspberry Pi 5 Model B Rev 1.0 (ARM)", i.Cpu);
        Assert.Equal("4 cores / 4 threads", i.CpuTopology);
        Assert.Equal(["vc4-drm display"], i.Gpus);
        Assert.Empty(i.Displays);
        Assert.Equal("nobody-signed-in", i.User);
        Assert.Equal("", i.Bios);
    }

    // Blocks in /proc/cpuinfo are separated by a blank line (the raw string ends with one newline; add the second)
    private static string CpuInfoBlock(int processor, int coreId) => "\n" + $"""
        processor	: {processor}
        vendor_id	: GenuineIntel
        model name	: 13th Gen Intel(R) Core(TM) i7-1365U
        cpu MHz		: 1200.000
        physical id	: 0
        core id		: {coreId}

        """;
}

public class PciIdsTests
{
    [Fact]
    public void Gpu_vendor_on_the_first_line_of_pci_ids_is_found()
    {
        using var fake = new FakeLinux();
        fake.Write("usr/share/hwdata/pci.ids", "1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t1638  Cezanne [Radeon Vega Series / Radeon Vega Mobile Series]\n")
            .Write("sys/class/drm/card0/device/vendor", "0x1002\n")
            .Write("sys/class/drm/card0/device/device", "0x1638\n");

        var i = LinuxInfo.Collect(fake.Fs, live: false);

        Assert.Equal(["AMD Radeon Vega Series / Radeon Vega Mobile Series"], i.Gpus);
    }
}
