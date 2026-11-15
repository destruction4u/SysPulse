using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SysPulse.Core;
using SysPulse.Core.Remote;
using SysPulse.Services;

namespace SysPulse;

/// <summary>Pairs with a SysPulse Agent: checks address + code by fetching its info, and pins its certificate.</summary>
public partial class AddMachineWindow : Window
{
    private readonly IReadOnlyCollection<SavedMachine> _existing;

    public SavedMachine? Result { get; private set; }
    public AgentInfo? ResultInfo { get; private set; }

    public AddMachineWindow(IReadOnlyCollection<SavedMachine> existing)
    {
        _existing = existing;
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => AddressBox.Focus();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseAddress(AddressBox.Text, out string host, out int port))
        {
            Fail("Enter the other PC's address, e.g. 192.168.1.50 or OFFICE-PC.");
            AddressBox.Focus();
            return;
        }
        if (!PairingCode.IsValid(CodeBox.Text))
        {
            Fail($"The pairing code should have {PairingCode.Length} letters and digits (you entered {PairingCode.Normalize(CodeBox.Text).Length}).");
            CodeBox.Focus();
            return;
        }
        if (_existing.Any(m => m.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && m.Port == port))
        {
            Fail("That PC is already in your list.");
            return;
        }

        ConnectButton.IsEnabled = false;
        Info($"Connecting to {host}…");
        try
        {
            string code = PairingCode.Normalize(CodeBox.Text);
            using var client = new AgentClient(host, port, code, pinnedFingerprint: null);
            var info = await client.GetInfoAsync();

            var saved = new SavedMachine
            {
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? info.Machine.Host : NameBox.Text.Trim(),
                Host = host,
                Port = port,
                CertFingerprint = client.ObservedFingerprint ?? "",
                PairingCode = code,
            };
            Result = saved;
            ResultInfo = info;
            DialogResult = true;
        }
        catch (AgentAuthException)
        {
            Fail("The pairing code doesn't match. Check it under Start ▸ \"SysPulse Agent - Show pairing code\" on the other PC.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Fail($"Couldn't reach {host}:{port}. Check that SysPulse Agent is installed and running there, both PCs are on the same network, " +
                 "and that network is set to Private in Windows (the agent's firewall rule doesn't allow Public networks).");
        }
        catch (Exception ex)
        {
            Fail("Couldn't connect: " + ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>Accepts "host", "host:port", an IPv4 address or a bracketed IPv6 address.</summary>
    private static bool TryParseAddress(string text, out string host, out int port)
    {
        text = text.Trim();
        host = text;
        port = AgentProtocol.DefaultPort;
        if (text.Length == 0 || text.Contains(' ') || text.Contains('/')) return false;

        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end < 0) return false;
            host = text[1..end];
            string rest = text[(end + 1)..];
            return rest.Length == 0 || (rest.StartsWith(':') && int.TryParse(rest[1..], out port) && port is > 0 and < 65536);
        }
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon) // exactly one colon = host:port
        {
            host = text[..colon];
            return int.TryParse(text[(colon + 1)..], out port) && port is > 0 and < 65536;
        }
        return true; // plain name, IPv4, or unbracketed IPv6
    }

    private void Fail(string message)
    {
        StatusText.Foreground = (Brush)FindResource("ErrorBrush");
        StatusText.Text = message;
    }

    private void Info(string message)
    {
        StatusText.Foreground = (Brush)FindResource("Muted");
        StatusText.Text = message;
    }
}
