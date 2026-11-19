using System.Globalization;

namespace SysPulse.Core;

/// <summary>One-time facts about an Android device, pulled over adb (getprop, /proc/cpuinfo, uname).</summary>
public static class AndroidInfo
{
    // Section markers for the single batched command. Unlikely to appear in real output.
    private const string Cmd =
        "getprop ro.product.manufacturer; echo '###MODEL'; getprop ro.product.model;" +
        " echo '###REL'; getprop ro.build.version.release;" +
        " echo '###BOARD'; getprop ro.board.platform; echo '###HW'; getprop ro.hardware;" +
        " echo '###KERN'; uname -r -m;" +
        " echo '###CPUINFO'; cat /proc/cpuinfo;" +
        " echo '###MEM'; grep MemTotal /proc/meminfo;" +
        " echo '###IP'; ip -o -4 addr show 2>/dev/null";

    public static StaticInfo Collect(IAdbShell shell) => Parse(shell.Run(Cmd));

    internal static StaticInfo Parse(string output)
    {
        var s = Sections(output);
        string manufacturer = First(s, "").Trim();              // the pre-marker head is the first getprop
        string model = Line(s, "MODEL");
        string release = Line(s, "REL");
        string board = Line(s, "BOARD");
        string hardware = Line(s, "HW");
        string kernel = Line(s, "KERN");
        string cpuinfo = Section(s, "CPUINFO");

        int cores = cpuinfo.Split('\n').Count(l => l.StartsWith("processor"));
        string soc = CpuName(cpuinfo, board, hardware);
        string friendlyModel = string.Join(' ', new[] { manufacturer, model }.Where(x => x.Length > 0));

        return new StaticInfo
        {
            Platform = "Android",
            Os = release.Length > 0 ? $"Android {release}" : "Android",
            Kernel = kernel,
            Model = friendlyModel,
            Cpu = soc,
            LogicalProcessors = cores > 0 ? cores : Environment.ProcessorCount,
            CpuTopology = CoreCluster(cpuinfo),
            Host = model.Length > 0 ? model : "Android device",
            User = manufacturer.Length > 0 ? manufacturer.ToLowerInvariant() : "android",
            LocalIp = WlanIp(Section(s, "IP")),
            Gpus = [],
            Displays = [],
            Bios = "",
            Theme = "",
        };
    }

    /// <summary>Best-effort SoC name: /proc/cpuinfo "Hardware", else ro.board.platform / ro.hardware.</summary>
    private static string CpuName(string cpuinfo, string board, string hardware)
    {
        foreach (string line in cpuinfo.Split('\n'))
            if (line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                string v = line.Split(':', 2)[1].Trim();
                if (v.Length > 0) return v;
            }
        if (board.Length > 0) return board;
        if (hardware.Length > 0) return hardware;
        return "ARM";
    }

    // ARM core "CPU part" codes → marketing names (the common mobile cores). Unknown codes show as the raw hex.
    private static readonly Dictionary<string, string> ArmCores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0xd03"] = "A53", ["0xd04"] = "A35", ["0xd05"] = "A55", ["0xd07"] = "A57", ["0xd08"] = "A72",
        ["0xd09"] = "A73", ["0xd0a"] = "A75", ["0xd0b"] = "A76", ["0xd0d"] = "A77", ["0xd0e"] = "A76AE",
        ["0xd41"] = "A78", ["0xd44"] = "X1", ["0xd46"] = "A510", ["0xd47"] = "A710", ["0xd48"] = "X2",
        ["0xd4d"] = "A715", ["0xd4e"] = "X3", ["0xd80"] = "A520", ["0xd81"] = "A720", ["0xd82"] = "X4",
        ["0xd87"] = "A725", ["0xd88"] = "A520AE",
    };

    /// <summary>Summarises big.LITTLE clusters from the distinct "CPU part" values, e.g. "6x A55 + 2x A76".</summary>
    private static string CoreCluster(string cpuinfo)
    {
        var parts = new List<string>();
        foreach (string line in cpuinfo.Split('\n'))
            if (line.StartsWith("CPU part", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                string code = line.Split(':', 2)[1].Trim();
                parts.Add(ArmCores.TryGetValue(code, out var name) ? name : code);
            }
        if (parts.Count == 0) return "";
        // Group consecutive-identical parts into "Nx <code>" without naming specific cores (which we can't map reliably).
        var groups = new List<(string Part, int Count)>();
        foreach (string p in parts)
        {
            if (groups.Count > 0 && groups[^1].Part == p) groups[^1] = (p, groups[^1].Count + 1);
            else groups.Add((p, 1));
        }
        return string.Join(" + ", groups.Select(g => $"{g.Count}x {g.Part}"));
    }

    private static string WlanIp(string ipOutput)
    {
        // `ip -o -4 addr show`:  "23: wlan0    inet 192.168.1.50/24 brd ... scope global wlan0\ ..."
        string? fallback = null;
        foreach (string line in ipOutput.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int idx = Array.IndexOf(f, "inet");
            if (idx < 0 || idx + 1 >= f.Length) continue;
            string iface = f.Length > 1 ? f[1] : "";
            string addr = f[idx + 1].Split('/')[0];
            if (addr.StartsWith("127.")) continue;
            if (iface.StartsWith("wlan")) return $"{addr} ({iface})";
            fallback ??= $"{addr} ({iface})";
        }
        return fallback ?? "Not connected";
    }

    // ---------------------------------------------------------------- section parsing

    private static Dictionary<string, string> Sections(string output)
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

    private static string First(Dictionary<string, string> s, string key) => s.TryGetValue(key, out var v) ? v : "";
    private static string Section(Dictionary<string, string> s, string key) => s.TryGetValue(key, out var v) ? v : "";
    private static string Line(Dictionary<string, string> s, string key) => Section(s, key).Trim().Split('\n')[0].Trim();
}
