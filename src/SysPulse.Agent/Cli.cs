using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SysPulse.Core.Remote;

namespace SysPulse.Agent;

/// <summary>
/// Admin commands (used by the installers and the "Show pairing code" shortcut):
///   --install [--out file]   create config + certificate, register and start the service, open the firewall
///   --uninstall              stop and remove the service, firewall rule and data folder
///   --stop-service           stop the service and wait (installer, before replacing files)
///   --pairing [--pause]      show the address and pairing code to enter in SysPulse
///   --reset-code             issue a new pairing code (disconnects existing viewers)
/// The platform-specific work lives in <see cref="WindowsSetup"/> and <see cref="LinuxSetup"/>.
/// </summary>
public static class Cli
{
    internal static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");

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

        if (!AgentConfig.IsPrivileged)
        {
            if (OperatingSystem.IsWindows() && command == "--pairing") { exitCode = WindowsSetup.RelaunchElevated("--pairing --pause"); return true; }
            Console.Error.WriteLine(OperatingSystem.IsWindows()
                ? $"{command} needs administrator rights. Run it from an elevated prompt."
                : $"{command} needs root. Run: sudo {Exe} {command}");
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

        if (OperatingSystem.IsWindows()) WindowsSetup.Install(config);
        else if (OperatingSystem.IsLinux()) LinuxSetup.Install(config, dir);
        else throw new PlatformNotSupportedException();

        string text = PairingText(config, dir);
        Console.WriteLine();
        Console.WriteLine(text);
        if (outFile != null) File.WriteAllText(outFile, text);
        return 0;
    }

    private static int Uninstall()
    {
        if (OperatingSystem.IsWindows()) WindowsSetup.Uninstall();
        else if (OperatingSystem.IsLinux()) LinuxSetup.Uninstall(AgentConfig.DefaultDir);
        try { Directory.Delete(AgentConfig.DefaultDir, recursive: true); } catch (DirectoryNotFoundException) { }
        Console.WriteLine("SysPulse Agent removed.");
        return 0;
    }

    private static bool StopService() =>
        OperatingSystem.IsWindows() ? WindowsSetup.StopService()
        : OperatingSystem.IsLinux() && LinuxSetup.StopService();

    private static int ShowPairing()
    {
        string dir = AgentConfig.DefaultDir;
        if (!File.Exists(Path.Combine(dir, "agent.json")))
        {
            Console.WriteLine($"SysPulse Agent isn't set up yet. Reinstall it, or run: {(OperatingSystem.IsWindows() ? "" : "sudo ")}\"{Exe}\" --install");
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
        if (OperatingSystem.IsWindows()) WindowsSetup.Restart();
        else if (OperatingSystem.IsLinux()) { LinuxSetup.SecureConfig(dir); LinuxSetup.Restart(); }
        Console.WriteLine("New pairing code issued. Viewers using the old code are disconnected.\n");
        Console.WriteLine(PairingText(config, dir));
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static string PairingText(AgentConfig config, string dir)
    {
        using var cert = AgentConfig.LoadOrCreateCertificate(dir);
        string fp = AgentConfig.Fingerprint(cert);
        string status = OperatingSystem.IsWindows() ? WindowsSetup.Status() : OperatingSystem.IsLinux() ? LinuxSetup.Status() : "?";
        string resetHint = OperatingSystem.IsWindows()
            ? "SysPulse.Agent.exe --reset-code (as administrator)"
            : $"sudo {Exe} --reset-code";

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
        sb.AppendLine($"To issue a new code: {resetHint}");
        return sb.ToString();
    }

    /// <summary>IPv4 addresses of connected adapters that have a gateway (i.e. real LAN connections).</summary>
    internal static List<string> LocalAddresses() => LocalNetworks().Select(n => n.Address).ToList();

    /// <summary>Address and subnet (CIDR) of each connected adapter with a gateway.</summary>
    internal static List<(string Address, string Subnet)> LocalNetworks()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (var a in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    int prefix = a.PrefixLength is > 0 and <= 32 ? a.PrefixLength : 24;
                    uint ip = BitConverter.ToUInt32(a.Address.GetAddressBytes().Reverse().ToArray());
                    uint mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
                    byte[] net = BitConverter.GetBytes(ip & mask).Reverse().ToArray();
                    list.Add((a.Address.ToString(), $"{new System.Net.IPAddress(net)}/{prefix}"));
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Runs a tool without a shell; arguments are passed exactly as given.</summary>
    internal static int Run(string file, IEnumerable<string> arguments, out string output)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            output = ex.Message; // tool not installed
            return -1;
        }
    }

    /// <summary>Windows tools (sc.exe, netsh) parse their own quoting, so they get a raw command line.</summary>
    internal static int RunRaw(string file, string arguments, out string output)
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

    internal static void Must(int exitCode, string what, string output)
    {
        if (exitCode != 0) throw new InvalidOperationException($"Couldn't {what} (exit {exitCode}): {output}");
    }
}
