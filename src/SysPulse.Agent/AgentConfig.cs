using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using SysPulse.Core.Remote;

namespace SysPulse.Agent;

/// <summary>
/// Agent settings + secrets:
///   agent.json  port and pairing code
///   agent.pfx   self-signed TLS certificate (private key included)
/// Windows: %ProgramData%\SysPulse Agent, readable only by Administrators and SYSTEM.
/// Linux:   /etc/syspulse-agent, root:syspulse-agent 0750 (files 0640), so only root and the agent's own user can read it.
/// </summary>
public sealed class AgentConfig
{
    public const string LinuxUser = "syspulse-agent";

    public int Port { get; set; } = AgentProtocol.DefaultPort;
    public string PairingCode { get; set; } = "";

    public static string DefaultDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SysPulse Agent")
        : "/etc/syspulse-agent";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Administrator/SYSTEM on Windows, root on Linux.</summary>
    public static bool IsPrivileged => Environment.IsPrivilegedProcess;

    /// <summary>Loads the config, creating it (new pairing code) on first run.</summary>
    public static AgentConfig LoadOrCreate(string dir)
    {
        EnsureDirectory(dir);
        string path = Path.Combine(dir, "agent.json");
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path));
            if (existing != null && Core.Remote.PairingCode.IsValid(existing.PairingCode)) return existing;
        }
        var created = new AgentConfig { PairingCode = Core.Remote.PairingCode.Generate() };
        created.Save(dir);
        return created;
    }

    public void Save(string dir)
    {
        EnsureDirectory(dir);
        string path = Path.Combine(dir, "agent.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        RestrictFile(path);
    }

    /// <summary>Loads the TLS certificate, creating a new self-signed one (valid 10 years) on first run.</summary>
    public static X509Certificate2 LoadOrCreateCertificate(string dir)
    {
        string path = Path.Combine(dir, "agent.pfx");
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={Environment.MachineName} (SysPulse Agent)", key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(Environment.MachineName);
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());

            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
            RestrictFile(path);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, KeyStorage());
    }

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>
    /// Windows: SYSTEM/admins keep the key in the machine store, a non-admin dev run in the user store.
    /// Linux: OpenSSL keeps it in memory; there are no key stores.
    /// </summary>
    private static X509KeyStorageFlags KeyStorage() =>
        !OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet
        : IsPrivileged ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet;

    /// <summary>Creates the data folder and, when privileged and new, locks it down (the pairing code and private key live here).</summary>
    private static void EnsureDirectory(string dir)
    {
        var di = new DirectoryInfo(dir);
        bool isNew = !di.Exists;
        di.Create();
        if (!isNew || !IsPrivileged) return;

        if (OperatingSystem.IsWindows()) LockDownWindows(di);
        else di.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute; // 0750; group set by --install
    }

    /// <summary>Linux: 0640 (owner rw, group r). Windows files inherit the folder's ACL.</summary>
    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && IsPrivileged)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }

    [SupportedOSPlatform("windows")]
    private static void LockDownWindows(DirectoryInfo di)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(security);
    }
}
