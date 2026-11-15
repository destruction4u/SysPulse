using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SysPulse.Services;

/// <summary>Facts about the machine that don't change while the app runs (collected once, via WMI + registry).</summary>
public sealed class StaticInfo
{
    public string User { get; private set; } = Environment.UserName;
    public string Host { get; private set; } = Environment.MachineName;
    public string Os { get; private set; } = "Windows";
    public string Kernel { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string Bios { get; private set; } = "";
    public string Cpu { get; private set; } = "";
    public string CpuTopology { get; private set; } = "";
    public double CpuBaseMhz { get; private set; }
    public int LogicalProcessors { get; private set; } = Environment.ProcessorCount;
    public List<string> Gpus { get; } = [];
    public List<string> Displays { get; } = [];
    public string Theme { get; private set; } = "";
    public string Locale { get; private set; } = CultureInfo.CurrentUICulture.Name;
    public string LocalIp { get; private set; } = "";

    public static StaticInfo Collect()
    {
        var i = new StaticInfo();

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

        foreach (var o in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            i.Model = $"{Str(o, "Manufacturer")} {Str(o, "Model")}".Trim();

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

        using (var p = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            object? light = p?.GetValue("AppsUseLightTheme");
            i.Theme = light is int v ? (v == 0 ? "Dark" : "Light") : "Unknown";
        }

        i.LocalIp = FindLocalIp();
        return i;
    }

    private static string FindLocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue; // skip virtual adapters without a route out
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) return $"{v4.Address} ({ni.Name})";
            }
        }
        catch { /* ignore */ }
        return "Not connected";
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
