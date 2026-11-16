using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using SysPulse.Core.Remote;
using static SysPulse.Agent.Cli;

namespace SysPulse.Agent;

/// <summary>Windows: a delayed-auto-start service running as LocalSystem, plus a Windows Firewall rule.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSetup
{
    private const string FirewallRule = "SysPulse Agent";

    public static void Install(AgentConfig config)
    {
        StopService();
        // Create, or update the path if it already exists (upgrade into a different folder).
        string bin = $"binPath= \"\\\"{Exe}\\\"\" start= delayed-auto DisplayName= \"SysPulse Agent\"";
        if (RunRaw("sc.exe", $"create {AgentProtocol.ServiceName} {bin}", out _) != 0)
            Must(RunRaw("sc.exe", $"config {AgentProtocol.ServiceName} {bin}", out var err), "configure the service", err);
        RunRaw("sc.exe", $"description {AgentProtocol.ServiceName} \"Shares this PC's health and resource readings with SysPulse on your local network (read-only).\"", out _);
        // Restart automatically if it ever crashes
        RunRaw("sc.exe", $"failure {AgentProtocol.ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000", out _);

        // Inbound rule: only this program, only this port, only from the local subnet, never on Public networks.
        RunRaw("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", out _);
        Must(RunRaw("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={config.Port} " +
            $"remoteip=localsubnet profile=private,domain program=\"{Exe}\" enable=yes " +
            "description=\"Lets SysPulse on other PCs in your local network read this PC's stats.\"", out var fwErr),
            "add the firewall rule", fwErr);

        using var sc = new ServiceController(AgentProtocol.ServiceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Uninstall()
    {
        StopService();
        RunRaw("sc.exe", $"delete {AgentProtocol.ServiceName}", out _);
        RunRaw("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", out _);
    }

    public static bool StopService()
    {
        try
        {
            using var sc = new ServiceController(AgentProtocol.ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped) return true;
            if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (InvalidOperationException) { return true; } // not installed
        catch (System.ServiceProcess.TimeoutException) { return false; }
    }

    public static void Restart()
    {
        if (!StopService()) return;
        using var sc = new ServiceController(AgentProtocol.ServiceName);
        try { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
        catch (InvalidOperationException) { } // not installed
    }

    public static string Status()
    {
        try { using var sc = new ServiceController(AgentProtocol.ServiceName); return sc.Status.ToString(); }
        catch (InvalidOperationException) { return "not installed"; }
    }

    public static int RelaunchElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Exe, arguments) { UseShellExecute = true, Verb = "runas" });
            return 0;
        }
        catch (Win32Exception)
        {
            Console.Error.WriteLine("Administrator permission is needed to read the pairing code.");
            return 1;
        }
    }
}
