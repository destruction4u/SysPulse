using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SysPulse.Core;
using SysPulse.Services;

namespace SysPulse;

/// <summary>Picks an Android device reachable over adb (USB or wireless) and collects its static info.</summary>
public partial class AddAndroidWindow : Window
{
    private readonly IReadOnlyCollection<SavedMachine> _existing;

    public SavedMachine? Result { get; private set; }
    public StaticInfo? ResultInfo { get; private set; }
    public string AdbPath { get; private set; } = "";

    public AddAndroidWindow(IReadOnlyCollection<SavedMachine> existing)
    {
        _existing = existing;
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            if (AdbClient.FindAdb() is { } adb)
            {
                AdbPath = adb;
                RefreshDevices();
            }
            else
            {
                Fail("adb (Android platform-tools) wasn't found. Install it, or set the SYSPULSE_ADB environment variable to adb.exe, then reopen this dialog.");
                AddButton.IsEnabled = RefreshButton.IsEnabled = WirelessButton.IsEnabled = false;
            }
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        var devices = AdbClient.ListDevices(AdbPath).Where(d => d.IsReady).ToList();
        DeviceBox.ItemsSource = devices;
        if (devices.Count > 0) { DeviceBox.SelectedIndex = 0; Info($"{devices.Count} device(s) ready."); }
        else Info("No devices in 'device' state. Connect by USB and tap Allow on the phone, or connect over Wi-Fi below.");
    }

    private void Wireless_Click(object sender, RoutedEventArgs e)
    {
        string addr = WirelessBox.Text.Trim();
        if (addr.Length == 0) { Fail("Enter the phone's Wi-Fi address, e.g. 192.168.1.189:37000."); return; }
        WirelessButton.IsEnabled = false;
        Info($"Connecting to {addr}…");
        try
        {
            var (ok, msg) = AdbClient.Connect(AdbPath, addr);
            if (ok) RefreshDevices();
            else Fail($"Couldn't connect: {msg}. On the phone, open Wireless debugging and use the IP and port shown there (you may need to pair first).");
        }
        finally { WirelessButton.IsEnabled = true; }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not AdbDevice device)
        {
            Fail("Pick a connected device first (Refresh after plugging in or connecting over Wi-Fi).");
            return;
        }
        if (_existing.Any(m => m.Kind == MachineKind.Android && m.AdbSerial == device.Serial))
        {
            Fail("That device is already in your list.");
            return;
        }

        AddButton.IsEnabled = false;
        Info($"Reading {device.Display}…");
        try
        {
            var client = new AdbClient(AdbPath, device.Serial);
            var info = await Task.Run(() => AndroidInfo.Collect(client));
            if (info.Model.Length == 0 && info.LogicalProcessors == 0)
            {
                Fail("Couldn't read the device. Make sure it's unlocked and USB debugging is still allowed.");
                return;
            }
            ResultInfo = info;
            Result = new SavedMachine
            {
                Kind = MachineKind.Android,
                AdbSerial = device.Serial,
                Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim()
                     : info.Model.Length > 0 ? info.Model : device.Display,
            };
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Fail("Couldn't add the device: " + ex.Message);
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
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
