using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SysPulse.Core;

/// <summary>Linux: machine facts from /etc/os-release, /proc and /sys (no external tools needed).</summary>
internal static class LinuxInfo
{
    // Placeholder strings firmware vendors leave in DMI fields
    private static readonly string[] DmiJunk =
        ["To be filled by O.E.M.", "To Be Filled By O.E.M.", "System Product Name", "System manufacturer", "Default string", "Not Specified", "None", "0123456789"];

    public static StaticInfo Collect() => Collect(new LinuxFs("/"), live: true);

    /// <param name="live">false in tests: skip things that read the real machine (network adapters, environment).</param>
    internal static StaticInfo Collect(LinuxFs fs, bool live)
    {
        var i = new StaticInfo { Platform = "Linux", Theme = "", Gpus = [], Displays = [] };

        // OS: "Ubuntu 24.04.1 LTS x86_64"
        var osr = LinuxFs.KeyValues(fs.Read("etc/os-release") ?? fs.Read("usr/lib/os-release"));
        string distro = osr.GetValueOrDefault("PRETTY_NAME")
            ?? $"{osr.GetValueOrDefault("NAME", "Linux")} {osr.GetValueOrDefault("VERSION", "")}".Trim();
        i.Os = $"{distro} {Arch()}".Trim();
        i.Kernel = fs.Line("proc/sys/kernel/osrelease");
        string host = fs.Line("proc/sys/kernel/hostname");
        if (host.Length > 0) i.Host = host;
        else if (!live) i.Host = "linux";

        // Hardware model: DMI on PCs/servers, device tree on ARM boards (e.g. "Raspberry Pi 5 Model B Rev 1.0")
        string vendor = Dmi(fs, "sys_vendor"), product = Dmi(fs, "product_name"), version = Dmi(fs, "product_version");
        if (vendor == "LENOVO" && version.Length > 0) product = version; // Lenovo puts the marketing name in product_version
        i.Model = $"{vendor} {product}".Trim();
        if (i.Model.Length == 0)
            i.Model = Coalesce(fs.Line("proc/device-tree/model"), fs.Line("sys/firmware/devicetree/base/model"));
        string bios = Dmi(fs, "bios_version"), biosDate = Dmi(fs, "bios_date");
        i.Bios = biosDate.Length > 0 && bios.Length > 0 ? $"{bios} ({biosDate})" : bios;

        ReadCpu(fs, i, live);
        ReadGpus(fs, i);
        ReadDisplays(fs, i);

        i.User = SignedInUser(fs);
        string lang = Coalesce(LinuxFs.KeyValues(fs.Read("etc/locale.conf")).GetValueOrDefault("LANG"),
                               LinuxFs.KeyValues(fs.Read("etc/default/locale")).GetValueOrDefault("LANG"));
        i.Locale = lang.Length > 0 ? lang.Split('.', '@')[0].Replace('_', '-') : "";
        i.LocalIp = live ? StaticInfo.FindLocalIp() : "";
        return i;
    }

    private static void ReadCpu(LinuxFs fs, StaticInfo i, bool live)
    {
        string text = fs.Read("proc/cpuinfo") ?? "";
        var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Split('\n')
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim()))
            .Where(d => d.ContainsKey("processor"))
            .ToList();
        var all = LinuxFs.KeyValues(text.Replace(':', '=')); // whole-file keys (ARM puts "Model"/"Hardware" at the end)

        string model = blocks.Select(b => b.GetValueOrDefault("model name")).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
        if (model.Length == 0)
        {
            // ARM: no "model name"; use the board's SoC/model if present
            string hw = Coalesce(all.GetValueOrDefault("Hardware"), all.GetValueOrDefault("Model"));
            model = hw.Length > 0 ? $"{hw} (ARM)" : RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.Arm ? "ARM processor" : "Unknown CPU";
        }
        i.Cpu = Regex.Replace(model.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""), @"\s+", " ").Trim();

        int threads = blocks.Count > 0 ? blocks.Count : (live ? Environment.ProcessorCount : 1);
        // Physical cores = distinct (socket, core) pairs; falls back to thread count (e.g. ARM, VMs)
        var cores = blocks.Where(b => b.ContainsKey("core id"))
            .Select(b => (b.GetValueOrDefault("physical id", "0"), b["core id"])).Distinct().Count();
        if (cores == 0) cores = threads;
        i.LogicalProcessors = threads;
        i.CpuTopology = $"{cores} cores / {threads} threads";

        long? maxKhz = fs.Long("sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
        if (maxKhz is > 0) i.CpuBaseMhz = maxKhz.Value / 1000.0;
        else if (double.TryParse(blocks.FirstOrDefault()?.GetValueOrDefault("cpu MHz"), System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out double mhz)) i.CpuBaseMhz = mhz;
    }

    private static void ReadGpus(LinuxFs fs, StaticInfo i)
    {
        PciIds? ids = null;
        foreach (string card in fs.Entries("sys/class/drm").Where(n => Regex.IsMatch(n, @"^card\d+$")))
        {
            string dev = $"sys/class/drm/{card}/device";
            string vendorId = fs.Line($"{dev}/vendor"), deviceId = fs.Line($"{dev}/device");
            string driver = LinuxFs.KeyValues(fs.Read($"{dev}/uevent")).GetValueOrDefault("DRIVER", "");
            string? name = null;
            if (vendorId.StartsWith("0x") && deviceId.StartsWith("0x"))
            {
                ids ??= PciIds.Load(fs);
                name = ids.Lookup(vendorId[2..], deviceId[2..]);
            }
            name ??= VendorName(vendorId) is { } v ? $"{v} GPU" : null;
            name ??= driver.Length > 0 ? $"{driver} display" : null; // e.g. vc4 on a Raspberry Pi
            if (name != null && !i.Gpus.Contains(name)) i.Gpus.Add(name);
        }
    }

    private static void ReadDisplays(LinuxFs fs, StaticInfo i)
    {
        foreach (string conn in fs.Entries("sys/class/drm").Where(n => n.StartsWith("card") && n.Contains('-')))
        {
            if (fs.Line($"sys/class/drm/{conn}/status") != "connected") continue;
            string mode = fs.Line($"sys/class/drm/{conn}/modes"); // first line = preferred/current mode
            if (mode.Length > 0) i.Displays.Add(mode);
        }
    }

    /// <summary>A human (uid ≥ 1000) with an active session: systemd creates /run/user/&lt;uid&gt; for each.</summary>
    private static string SignedInUser(LinuxFs fs)
    {
        var uids = fs.Entries("run/user").Select(s => int.TryParse(s, out int u) ? u : -1).Where(u => u >= 1000 && u < 60000).ToHashSet();
        if (uids.Count == 0) return "nobody-signed-in";
        foreach (string line in (fs.Read("etc/passwd") ?? "").Split('\n'))
        {
            var f = line.Split(':');
            if (f.Length > 2 && int.TryParse(f[2], out int uid) && uids.Contains(uid)) return f[0];
        }
        return $"uid {uids.Min()}";
    }

    private static string Dmi(LinuxFs fs, string field)
    {
        string v = fs.Line($"sys/class/dmi/id/{field}");
        return DmiJunk.Any(j => v.Equals(j, StringComparison.OrdinalIgnoreCase)) ? "" : v;
    }

    private static string? VendorName(string vendorId) => vendorId.ToLowerInvariant() switch
    {
        "0x8086" => "Intel",
        "0x1002" => "AMD",
        "0x10de" => "NVIDIA",
        "0x1af4" => "Virtio",
        "0x15ad" => "VMware",
        "0x1234" => "QEMU",
        _ => null,
    };

    private static string Arch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.Arm => "armv7l",
        var a => a.ToString().ToLowerInvariant(),
    };

    private static string Coalesce(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    /// <summary>Names from the PCI ID database most distros ship (pciutils/hwdata), if present.</summary>
    private sealed class PciIds
    {
        private readonly string? _text;
        // Prefix a newline so a vendor on the very first line is found by the same "\n<id>  " search
        private PciIds(string? text) => _text = text == null ? null : "\n" + text;

        public static PciIds Load(LinuxFs fs) =>
            new(fs.Read("usr/share/hwdata/pci.ids") ?? fs.Read("usr/share/misc/pci.ids") ?? fs.Read("usr/share/pci.ids"));

        /// <summary>Format: "vvvv  Vendor" lines, then "\tdddd  Device" lines under each vendor.</summary>
        public string? Lookup(string vendor, string device)
        {
            if (_text == null) return null;
            int v = _text.IndexOf($"\n{vendor.ToLowerInvariant()}  ", StringComparison.Ordinal);
            if (v < 0) return null;
            int vEnd = _text.IndexOf('\n', v + 1);
            string vendorName = _text[(v + 7)..vEnd].Trim();
            int next = vEnd;
            while (next >= 0 && next + 1 < _text.Length)
            {
                int lineEnd = _text.IndexOf('\n', next + 1);
                if (lineEnd < 0) lineEnd = _text.Length;
                string line = _text[(next + 1)..lineEnd];
                if (line.Length > 0 && line[0] != '\t' && line[0] != '#') break; // next vendor
                if (line.StartsWith($"\t{device.ToLowerInvariant()}  ", StringComparison.Ordinal))
                {
                    string dev = line[7..].Trim();
                    string shortVendor = vendorName.Split(' ')[0].TrimEnd(',');
                    if (vendorName.Contains("Advanced Micro Devices")) shortVendor = "AMD";
                    // Device names often include brackets with the marketing name: prefer that part
                    var m = Regex.Match(dev, @"\[(.+)\]");
                    return $"{shortVendor} {(m.Success ? m.Groups[1].Value : dev)}";
                }
                next = lineEnd;
            }
            return null;
        }
    }
}
