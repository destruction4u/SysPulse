using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using SysPulse.Core.Remote;

namespace SysPulse.Agent;

/// <summary>
/// Admin commands (used by the installer and the "Show pairing code" shortcut):
///   --install [--out file]   create config + certificate, register and start the service, open the firewall
///   --uninstall              stop and remove the service, firewall rule and data folder
///   --stop-service           stop the service and wait (installer, before replacing files)
///   --pairing [--pause]      show the address and pairing code to enter in SysPulse
///   --reset-code             issue a new pairing code (disconnects existing viewers)
/// </summary>
public static class Cli
{
    private const string FirewallRule = "SysPulse Agent";
    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");

    public static string? Option(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        string? command = new[] { "--install", "--uninstall", "--stop-service", "--pairing", "--reset-code" }.FirstOrDefault(c => Has(args, c));
        if (command == null) return false;

        if (!AgentConfig.IsElevated)
        {
            if (command == "--pairing") { exitCode = RelaunchElevated("--pairing --pause"); return true; }
            Console.Error.WriteLine($"{command} needs administrator rights. Run it from an elevated prompt.");
            exitCode = 1;
            return true;
        }

        try
        {
            exitCode = command switch
            {
                "--install" => Install(Option(args, "--out")),
                "--uninstall" => Uninstall(),
                "--stop-service" => StopService() ? 0 : 1,
                "--pairing" => ShowPairing(),
                "--reset-code" => ResetCode(),
                _ => 1,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            exitCode = 1;
        }

        if (Has(args, "--pause"))
        {
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
        }
        return true;
    }

    // ------------------------------------------------------------------ commands

    private static int Install(string? outFile)
    {
        string dir = AgentConfig.DefaultDir;
        var config = AgentConfig.LoadOrCreate(dir);
        using (AgentConfig.LoadOrCreateCertificate(dir)) { }

        StopService();
        // Create, or update the path if it already exists (upgrade into a different folder).
        string bin = $"binPath= \"\\\"{Exe}\\\"\" start= delayed-auto DisplayName= \"SysPulse Agent\"";
        if (Run("sc.exe", $"create {AgentProtocol.ServiceName} {bin}", out _) != 0)
            Must(Run("sc.exe", $"config {AgentProtocol.ServiceName} {bin}", out var err), "configure the service", err);
        Run("sc.exe", $"description {AgentProtocol.ServiceName} \"Shares this PC's health and resource readings with SysPulse on your local network (read-only).\"", out _);
        // Restart automatically if it ever crashes
        Run("sc.exe", $"failure {AgentProtocol.ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000", out _);

        // Inbound rule: only this program, only this port, only from the local subnet, never on Public networks.
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", out _);
        Must(Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={config.Port} " +
            $"remoteip=localsubnet profile=private,domain program=\"{Exe}\" enable=yes " +
            "description=\"Lets SysPulse on other PCs in your local network read this PC's stats.\"", out var fwErr),
            "add the firewall rule", fwErr);

        using (var sc = new ServiceController(AgentProtocol.ServiceName))
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }

        string text = PairingText(config, dir);
        Console.WriteLine(text);
        if (outFile != null) File.WriteAllText(outFile, text);
        return 0;
    }

    private static int Uninstall()
    {
        StopService();
        Run("sc.exe", $"delete {AgentProtocol.ServiceName}", out _);
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", out _);
        try { Directory.Delete(AgentConfig.DefaultDir, recursive: true); } catch (DirectoryNotFoundException) { }
        Console.WriteLine("SysPulse Agent removed.");
        return 0;
    }

    private static bool StopService()
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

    private static int ShowPairing()
    {
        string dir = AgentConfig.DefaultDir;
        if (!File.Exists(Path.Combine(dir, "agent.json")))
        {
            Console.WriteLine("SysPulse Agent isn't set up yet. Reinstall it, or run: SysPulse.Agent.exe --install");
            return 1;
        }
        Console.WriteLine(PairingText(AgentConfig.LoadOrCreate(dir), dir));
        return 0;
    }

    private static int ResetCode()
    {
        string dir = AgentConfig.DefaultDir;
        var config = AgentConfig.LoadOrCreate(dir);
        config.PairingCode = PairingCode.Generate();
        config.Save(dir);
        if (StopService())
        {
            using var sc = new ServiceController(AgentProtocol.ServiceName);
            try { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); } catch (InvalidOperationException) { }
        }
        Console.WriteLine("New pairing code issued. Viewers using the old code are disconnected.\n");
        Console.WriteLine(PairingText(config, dir));
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static string PairingText(AgentConfig config, string dir)
    {
        using var cert = AgentConfig.LoadOrCreateCertificate(dir);
        string fp = AgentConfig.Fingerprint(cert);
        string status;
        try { using var sc = new ServiceController(AgentProtocol.ServiceName); status = sc.Status.ToString(); }
        catch (InvalidOperationException) { status = "not installed"; }

        var addresses = LocalAddresses();
        var sb = new StringBuilder();
        sb.AppendLine($"SysPulse Agent on {Environment.MachineName}  (service: {status})");
        sb.AppendLine();
        sb.AppendLine("In SysPulse on your other PC, click \"+ Add PC\" and enter:");
        sb.AppendLine();
        sb.AppendLine($"  Address:       {(addresses.Count > 0 ? string.Join("  or  ", addresses) : Environment.MachineName)}");
        sb.AppendLine($"  Pairing code:  {PairingCode.Format(config.PairingCode)}");
        sb.AppendLine();
        sb.AppendLine($"  (Port {config.Port}. Certificate fingerprint starts {fp[..16]}.)");
        sb.AppendLine();
        sb.AppendLine("Keep the pairing code private: anyone on your network who has it can view this PC's readings.");
        sb.AppendLine("To issue a new code: SysPulse.Agent.exe --reset-code (as administrator).");
        return sb.ToString();
    }

    /// <summary>IPv4 addresses of connected adapters that have a gateway (i.e. real LAN connections).</summary>
    private static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                list.AddRange(props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()));
            }
        }
        catch { }
        return list;
    }

    private static int Run(string file, string arguments, out string output)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void Must(int exitCode, string what, string output)
    {
        if (exitCode != 0) throw new InvalidOperationException($"Couldn't {what} (exit {exitCode}): {output}");
    }

    private static int RelaunchElevated(string arguments)
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
