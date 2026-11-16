using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SysPulse.Core;

/// <summary>Windows: machine facts via WMI and the registry.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsInfo
{
    public static StaticInfo Collect()
    {
        var i = new StaticInfo();
        bool asService = System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem;

        // OS + kernel build
        using (var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            string display = cv?.GetValue("DisplayVersion") as string ?? "";
            string build = cv?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
            int ubr = cv?.GetValue("UBR") is int u ? u : 0;
            i.Kernel = $"10.0.{build}.{ubr}";

            string caption = "Windows", arch = "";
            foreach (var o in Query("SELECT Caption, OSArchitecture FROM Win32_OperatingSystem"))
            {
                caption = Str(o, "Caption").Replace("Microsoft ", "");
                arch = Str(o, "OSArchitecture");
            }
            i.Os = string.Join(' ', new[] { caption, display, arch }.Where(s => s.Length > 0));
        }

        foreach (var o in Query("SELECT Manufacturer, Model, UserName FROM Win32_ComputerSystem"))
        {
            i.Model = $"{Str(o, "Manufacturer")} {Str(o, "Model")}".Trim();
            if (asService)
            {
                // As a Windows service we run as SYSTEM: report who is signed in at the console instead.
                string console = Str(o, "UserName");
                i.User = console.Length > 0 ? console[(console.LastIndexOf('\\') + 1)..] : "nobody-signed-in";
            }
        }

        foreach (var o in Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            i.Bios = Str(o, "SMBIOSBIOSVersion");

        foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            i.Cpu = Regex.Replace(Str(o, "Name").Replace("(R)", "").Replace("(TM)", ""), @"\s+", " ");
            i.CpuTopology = $"{Str(o, "NumberOfCores")} cores / {Str(o, "NumberOfLogicalProcessors")} threads";
            if (double.TryParse(Str(o, "MaxClockSpeed"), out var mhz)) i.CpuBaseMhz = mhz;
            break; // first socket is enough for a laptop/desktop
        }

        foreach (var o in Query("SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            string name = Str(o, "Name");
            if (name.Length > 0) i.Gpus.Add(name);
            string w = Str(o, "CurrentHorizontalResolution"), h = Str(o, "CurrentVerticalResolution"), hz = Str(o, "CurrentRefreshRate");
            if (w.Length > 0 && h.Length > 0)
                i.Displays.Add(hz.Length > 0 ? $"{w}x{h} @ {hz} Hz" : $"{w}x{h}");
        }

        if (!asService) // SYSTEM's own theme setting would be meaningless
        {
            using var p = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? light = p?.GetValue("AppsUseLightTheme");
            i.Theme = light is int v ? (v == 0 ? "Dark" : "Light") : "Unknown";
        }

        i.LocalIp = StaticInfo.FindLocalIp();
        return i;
    }

    private static List<ManagementBaseObject> Query(string wql)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var o in searcher.Get()) list.Add(o);
        }
        catch { /* WMI unavailable: leave fields blank */ }
        return list;
    }

    private static string Str(ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }
}
