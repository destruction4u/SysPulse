using System.Runtime.Versioning;
using SysPulse.Core.Remote;
using static SysPulse.Agent.Cli;

namespace SysPulse.Agent;

/// <summary>
/// Linux: a systemd service running as its own unprivileged system user (reading /proc and /sys
/// needs no special rights), with systemd sandboxing, plus ufw/firewalld rules for the local subnet.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxSetup
{
    private const string Unit = "syspulse-agent";
    private const string UnitPath = "/etc/systemd/system/syspulse-agent.service";
    private const string FirewallRecord = "firewall-rules.txt"; // what --install opened, so --uninstall can close exactly that

    public static void Install(AgentConfig config, string dir)
    {
        if (Run("systemctl", ["--version"], out _) != 0)
            throw new InvalidOperationException("systemd is required (systemctl not found).");

        EnsureUser();
        SecureConfig(dir);

        File.WriteAllText(UnitPath, UnitFile());
        File.SetUnixFileMode(UnitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Must(Run("systemctl", ["daemon-reload"], out var e1), "reload systemd", e1);
        Must(Run("systemctl", ["enable", Unit], out var e2), "enable the service", e2);
        Must(Run("systemctl", ["restart", Unit], out var e3), "start the service", e3); // restart = also picks up an upgrade

        OpenFirewall(config.Port, dir);
        WaitUntilActive();
    }

    public static void Uninstall(string dir)
    {
        Run("systemctl", ["disable", "--now", Unit], out _);
        if (File.Exists(UnitPath)) File.Delete(UnitPath);
        Run("systemctl", ["daemon-reload"], out _);
        CloseFirewall(dir);
        try { Directory.Delete("/var/cache/syspulse-agent", recursive: true); } catch { }
        Run("userdel", [AgentConfig.LinuxUser], out _);
    }

    public static bool StopService() => Run("systemctl", ["stop", Unit], out _) is 0 or 5; // 5 = unit not loaded

    public static void Restart() => Run("systemctl", ["restart", Unit], out _);

    public static string Status()
    {
        Run("systemctl", ["is-active", Unit], out string state);
        return string.IsNullOrWhiteSpace(state) ? "unknown" : state.Split('\n')[0];
    }

    /// <summary>Config folder: root:syspulse-agent, dir 0750, files 0640. The agent can read its secrets; other users can't.</summary>
    public static void SecureConfig(string dir)
    {
        Must(Run("chown", ["-R", $"root:{AgentConfig.LinuxUser}", dir], out var err), "set config ownership", err);
        new DirectoryInfo(dir).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        foreach (string f in Directory.EnumerateFiles(dir))
            File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }

    private static void EnsureUser()
    {
        if (Run("id", ["-u", AgentConfig.LinuxUser], out _) == 0) return;
        string nologin = File.Exists("/usr/sbin/nologin") ? "/usr/sbin/nologin" : "/sbin/nologin";
        Must(Run("useradd", ["--system", "--no-create-home", "--home-dir", "/nonexistent", "--shell", nologin, AgentConfig.LinuxUser], out var err),
            "create the syspulse-agent system user", err);
    }

    /// <summary>
    /// Runs unprivileged and sandboxed: read-only view of the system, no home folders, no new privileges,
    /// no capabilities. (Not ProtectProc/PrivateDevices: it needs to see other processes and NVIDIA's devices.)
    /// </summary>
    private static string UnitFile() => $"""
        [Unit]
        Description=SysPulse Agent (shares this PC's readings with SysPulse on your local network, read-only)
        Documentation=https://github.com/destruction4u/SysPulse
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=notify
        ExecStart={Exe}
        User={AgentConfig.LinuxUser}
        Group={AgentConfig.LinuxUser}
        Restart=on-failure
        RestartSec=5
        # The single-file .NET app unpacks a few native libraries here on first start
        CacheDirectory=syspulse-agent
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/syspulse-agent

        NoNewPrivileges=yes
        CapabilityBoundingSet=
        AmbientCapabilities=
        ProtectSystem=strict
        ProtectHome=yes
        PrivateTmp=yes
        ProtectKernelTunables=yes
        ProtectKernelModules=yes
        ProtectKernelLogs=yes
        ProtectControlGroups=yes
        ProtectClock=yes
        ProtectHostname=yes
        RestrictSUIDSGID=yes
        RestrictNamespaces=yes
        RestrictRealtime=yes
        LockPersonality=yes
        RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK

        [Install]
        WantedBy=multi-user.target

        """;

    private static void WaitUntilActive()
    {
        for (int i = 0; i < 30; i++)
        {
            if (Status() == "active") return;
            Thread.Sleep(1000);
        }
        Run("journalctl", ["-u", Unit, "-n", "20", "--no-pager"], out string log);
        throw new InvalidOperationException($"The service didn't start. Recent log:\n{log}");
    }

    // ------------------------------------------------------------------ firewall

    /// <summary>
    /// Opens the port only to the local subnet(s), and only if a firewall frontend is active.
    /// Most desktop distros have no active firewall, in which case nothing needs opening.
    /// </summary>
    private static void OpenFirewall(int port, string dir)
    {
        var subnets = LocalNetworks().Select(n => n.Subnet).Distinct().ToList();
        var record = new List<string>();

        if (Run("ufw", ["status"], out string ufw) == 0 && ufw.StartsWith("Status: active"))
        {
            foreach (string net in subnets)
                if (Run("ufw", ["allow", "proto", "tcp", "from", net, "to", "any", "port", port.ToString(), "comment", "SysPulse Agent"], out _) == 0)
                    record.Add($"ufw {net} {port}");
            Console.WriteLine($"Firewall (ufw): allowed TCP {port} from {string.Join(", ", subnets)}.");
        }
        else if (Run("firewall-cmd", ["--state"], out string fwd) == 0 && fwd.Trim() == "running")
        {
            foreach (string net in subnets)
                if (Run("firewall-cmd", ["--permanent", "--add-rich-rule", RichRule(net, port)], out _) == 0)
                    record.Add($"firewalld {net} {port}");
            Run("firewall-cmd", ["--reload"], out _);
            Console.WriteLine($"Firewall (firewalld): allowed TCP {port} from {string.Join(", ", subnets)}.");
        }
        else
        {
            Console.WriteLine($"No ufw or firewalld firewall is active. If you use another firewall, allow TCP {port} from your local network.");
        }

        File.WriteAllLines(Path.Combine(dir, FirewallRecord), record);
        SecureConfig(dir);
    }

    private static void CloseFirewall(string dir)
    {
        string path = Path.Combine(dir, FirewallRecord);
        if (!File.Exists(path)) return;
        bool reload = false;
        foreach (string line in File.ReadAllLines(path))
        {
            var f = line.Split(' ');
            if (f.Length != 3) continue;
            if (f[0] == "ufw")
                Run("ufw", ["delete", "allow", "proto", "tcp", "from", f[1], "to", "any", "port", f[2]], out _);
            else if (f[0] == "firewalld" && int.TryParse(f[2], out int port))
            {
                Run("firewall-cmd", ["--permanent", "--remove-rich-rule", RichRule(f[1], port)], out _);
                reload = true;
            }
        }
        if (reload) Run("firewall-cmd", ["--reload"], out _);
    }

    private static string RichRule(string subnet, int port) =>
        $"rule family=\"ipv4\" source address=\"{subnet}\" port port=\"{port}\" protocol=\"tcp\" accept";
}
