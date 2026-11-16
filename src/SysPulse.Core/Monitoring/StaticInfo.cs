using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SysPulse.Core;

/// <summary>Facts about the machine that don't change while the app runs (collected once).</summary>
/// <remarks>Public setters so it can be sent as JSON from the agent to the viewer.</remarks>
public sealed class StaticInfo
{
    public string User { get; set; } = Environment.UserName;
    public string Host { get; set; } = Environment.MachineName;
    public string Os { get; set; } = "Windows";
    public string Kernel { get; set; } = "";
    public string Model { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string CpuTopology { get; set; } = "";
    public double CpuBaseMhz { get; set; }
    public int LogicalProcessors { get; set; } = Environment.ProcessorCount;
    public List<string> Gpus { get; set; } = [];
    public List<string> Displays { get; set; } = [];
    public string Theme { get; set; } = "";
    public string Locale { get; set; } = CultureInfo.CurrentUICulture.Name;
    public string LocalIp { get; set; } = "";

    /// <summary>"Windows" or "Linux". Agents older than 1.2 don't send it, and they were all Windows.</summary>
    public string Platform { get; set; } = "Windows";

    /// <summary>Collects facts about the machine this code runs on.</summary>
    public static StaticInfo Collect() =>
        FakeLinuxRoot is { } fake ? LinuxInfo.Collect(new LinuxFs(fake), live: false)
        : OperatingSystem.IsWindows() ? WindowsInfo.Collect()
        : OperatingSystem.IsLinux() ? LinuxInfo.Collect()
        : throw new PlatformNotSupportedException("SysPulse supports Windows and Linux.");

    /// <summary>
    /// Testing aid: when SYSPULSE_FAKE_LINUX_ROOT points at a folder laid out like a Linux root
    /// (/proc, /sys, /etc), the Linux readers use it on any OS. Lets the Linux path be exercised
    /// end to end (agent → viewer) on a Windows development machine.
    /// </summary>
    internal static string? FakeLinuxRoot =>
        Environment.GetEnvironmentVariable("SYSPULSE_FAKE_LINUX_ROOT") is { Length: > 0 } root ? root : null;

    internal static string FindLocalIp()
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

}
