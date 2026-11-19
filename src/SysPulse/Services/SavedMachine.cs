using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SysPulse.Services;

/// <summary>How a saved machine is reached.</summary>
public enum MachineKind
{
    /// <summary>A PC running SysPulse Agent, reached over HTTPS. The default, so old settings.json files deserialize to it.</summary>
    PcAgent = 0,
    /// <summary>An Android device, polled over adb from this PC (no agent on the phone).</summary>
    Android = 1,
}

/// <summary>A saved machine in the bar: a paired remote PC, or an Android device reached over adb.</summary>
public sealed class SavedMachine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }

    /// <summary>PcAgent (default) or Android.</summary>
    public MachineKind Kind { get; set; } = MachineKind.PcAgent;

    /// <summary>For Kind == Android: the adb serial (USB serial, or "host:port" for wireless adb).</summary>
    public string AdbSerial { get; set; } = "";

    /// <summary>SHA-256 of the agent's certificate, recorded when pairing. Connections to anything else are refused.</summary>
    public string CertFingerprint { get; set; } = "";

    /// <summary>The pairing code, encrypted with DPAPI for the current Windows user (useless if copied to another account or PC).</summary>
    public string ProtectedCode { get; set; } = "";

    private static readonly byte[] Entropy = "SysPulse pairing code v1"u8.ToArray();

    [JsonIgnore]
    public string PairingCode
    {
        get
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedCode), Entropy, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return ""; // settings copied from another user/PC: the agent will reject it and the UI says so
            }
        }
        set => ProtectedCode = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }
}
