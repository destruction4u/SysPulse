using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using SysPulse.Core;
using SysPulse.Core.Remote;
using SysPulse.Services;
using SysPulse.ViewModels;

namespace SysPulse;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();
    private readonly MachineViewModel _local;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<MachineViewModel, CancellationTokenSource> _remoteLoops = [];
    private readonly AppSettings _settings;
    private readonly TrayIcon _tray;
    private PeriodicTimer? _timer;
    private bool _exiting;
    private bool _syncing;
    private bool _nudging;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = _shell;

        _local = new MachineViewModel($"{Environment.MachineName} (this PC)", saved: null);
        _shell.Machines.Add(_local);
        foreach (var saved in _settings.Machines) _shell.Machines.Add(new MachineViewModel(saved.Name, saved));
        _shell.Selected = _shell.Machines.FirstOrDefault(m => m.Id == _settings.SelectedMachine) ?? _local;
        _shell.SelectedChanged += m => { _settings.SelectedMachine = m.Id; _settings.Save(); };

        RestorePlacement();
        Topmost = _settings.Topmost;
        SyncControls();

        _tray = new TrayIcon(_settings);
        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.TopmostChanged += on => Topmost = on;
        _tray.CloseToTrayChanged += SetCloseToTray;
        _tray.IntervalChanged += SetInterval;

        // Keep settings + tray menu in sync however Topmost changes (button or tray menu).
        DependencyPropertyDescriptor.FromProperty(TopmostProperty, typeof(Window))
            .AddValueChanged(this, (_, _) =>
            {
                if (_nudging || _settings.Topmost == Topmost) return;
                _settings.Topmost = Topmost;
                SettingsChanged();
            });

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;
    }

    private bool WindowShowing => IsVisible && WindowState != WindowState.Minimized;

    // ---------------------------------------------------------------- this PC

    public async void StartMonitoring()
    {
        foreach (var m in _shell.Machines.Where(m => !m.IsLocal)) StartMachine(m);
        try
        {
            // WMI and perf-counter setup are slow (~1-2 s), so keep them off the UI thread.
            var info = await Task.Run(StaticInfo.Collect);
            _local.Dashboard.SetStatic(info);
            var monitor = await Task.Run(() => SystemMonitors.Create(info));
            await RunLocalAsync(monitor, _cts.Token);
        }
        catch (Exception ex)
        {
            _local.Dashboard.Status = "Failed to start: " + ex.Message;
        }
    }

    private async Task RunLocalAsync(ISystemMonitor monitor, CancellationToken ct)
    {
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.IntervalMs));
        try
        {
            do
            {
                try
                {
                    // The process list is the expensive part: only when someone can see it.
                    bool showProcesses = WindowShowing && _shell.Selected == _local;
                    var sample = await Task.Run(() => monitor.Sample(includeProcesses: showProcesses), ct);
                    _local.ApplySample(sample); // back on the UI thread here
                    UpdateTray(sample);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _local.Dashboard.Status = "Sampling error: " + ex.Message; }
            }
            while (await _timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* exiting */ }
        finally
        {
            _timer.Dispose();
            monitor.Dispose();
        }
    }

    private void UpdateTray(Sample s)
    {
        double mem = s.MemTotal > 0 ? 100.0 * s.MemUsed / s.MemTotal : 0;
        _tray.Update(s.CpuPercent,
            $"SysPulse\nCPU {s.CpuPercent:0}%  ·  RAM {mem:0}%\n↓ {Fmt.Rate(s.NetDown)}  ↑ {Fmt.Rate(s.NetUp)}");
    }

    // ---------------------------------------------------------------- remote machines (PC agent or Android)

    private void StartMachine(MachineViewModel m)
    {
        if (m.Saved!.Kind == MachineKind.Android) StartAndroid(m);
        else StartRemote(m);
    }

    private void StartRemote(MachineViewModel m)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _remoteLoops[m] = cts;
        _ = PollRemoteAsync(m, cts.Token);
    }

    /// <summary>
    /// One loop per remote PC. Polls at the update speed while the window is showing, pauses
    /// while it's hidden (the agent then idles too), backs off when the PC is unreachable.
    /// </summary>
    private async Task PollRemoteAsync(MachineViewModel m, CancellationToken ct)
    {
        var saved = m.Saved!;
        using var client = new AgentClient(saved.Host, saved.Port, saved.PairingCode, saved.CertFingerprint);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = TimeSpan.FromMilliseconds(_settings.IntervalMs);
            if (!WindowShowing)
            {
                wait = TimeSpan.FromSeconds(1);
            }
            else
            {
                try
                {
                    if (!m.Dashboard.HasInfo)
                    {
                        if (m.State != MachineState.Online) m.SetConnecting();
                        var info = await client.GetInfoAsync(ct);
                        m.Dashboard.SetStatic(info.Machine);
                    }
                    if (await client.GetSampleAsync(ct) is { } sample) m.ApplySample(sample);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (AgentAuthException)
                {
                    m.SetError($"{m.Name} rejected the saved pairing code (it may have been reset). Remove this PC and add it again with the new code.");
                    wait = TimeSpan.FromSeconds(30);
                }
                catch (AgentCertificateException ex)
                {
                    m.SetError(ex.Message);
                    wait = TimeSpan.FromSeconds(30);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    m.SetOffline(ex is TaskCanceledException ? "timed out" : "no connection");
                    wait = TimeSpan.FromSeconds(5);
                }
            }
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowShowing) ShowFromTray();
        var dialog = new AddMachineWindow(_settings.Machines) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } saved) return;

        _settings.Machines.Add(saved);
        _settings.Save();
        var m = new MachineViewModel(saved.Name, saved);
        if (dialog.ResultInfo is { } info) m.Dashboard.SetStatic(info.Machine);
        _shell.Machines.Add(m);
        _shell.Selected = m;
        StartRemote(m);
    }

    // ---------------------------------------------------------------- Android devices (over adb)

    private void AddAndroid_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowShowing) ShowFromTray();
        var dialog = new AddAndroidWindow(_settings.Machines) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } saved) return;

        _settings.Machines.Add(saved);
        _settings.Save();
        var m = new MachineViewModel(saved.Name, saved);
        if (dialog.ResultInfo is { } info) m.Dashboard.SetStatic(info);
        _shell.Machines.Add(m);
        _shell.Selected = m;
        StartAndroid(m);
    }

    private void StartAndroid(MachineViewModel m)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _remoteLoops[m] = cts;
        _ = PollAndroidAsync(m, cts.Token);
    }

    /// <summary>
    /// One loop per Android device. There's no agent on the phone, so this PC pulls each reading over adb.
    /// Polls at the update speed while the window is showing; a dropped connection (unplugged, or debugging
    /// revoked) shows the device offline and it reconnects on its own when adb can reach it again.
    /// </summary>
    private async Task PollAndroidAsync(MachineViewModel m, CancellationToken ct)
    {
        var saved = m.Saved!;
        if (AdbClient.FindAdb() is not { } adbPath)
        {
            m.SetError("adb (Android platform-tools) wasn't found. Install it or set SYSPULSE_ADB to adb.exe, then restart SysPulse.");
            return;
        }
        var client = new AdbClient(adbPath, saved.AdbSerial);
        AndroidMonitor? monitor = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait = TimeSpan.FromMilliseconds(_settings.IntervalMs);
                if (!WindowShowing)
                {
                    wait = TimeSpan.FromSeconds(1);
                }
                else
                {
                    try
                    {
                        if (monitor == null)
                        {
                            if (m.State != MachineState.Online) m.SetConnecting();
                            if (!m.Dashboard.HasInfo)
                            {
                                var info = await Task.Run(() => AndroidInfo.Collect(client), ct);
                                if (info.LogicalProcessors == 0 && info.Model.Length == 0)
                                    throw new InvalidOperationException("device not responding");
                                m.Dashboard.SetStatic(info);
                            }
                            monitor = await Task.Run(() => new AndroidMonitor(client), ct);
                        }
                        bool showProcesses = WindowShowing && _shell.Selected == m;
                        var sample = await Task.Run(() => monitor!.Sample(includeProcesses: showProcesses), ct);
                        if (sample.MemTotal == 0) throw new InvalidOperationException("disconnected"); // adb read failed
                        m.ApplySample(sample);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        m.SetOffline(ex is InvalidOperationException ? ex.Message : "no connection");
                        monitor?.Dispose();
                        monitor = null;
                        wait = TimeSpan.FromSeconds(5);
                    }
                }
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            monitor?.Dispose();
        }
    }

    private void RemoveMachine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MachineViewModel { IsLocal: false } m) return;
        string prompt = m.Saved!.Kind == MachineKind.Android
            ? $"Stop monitoring {m.Name}?\n\nThis only removes it from SysPulse. You can add it again later over adb."
            : $"Stop monitoring {m.Name}?\n\nThe agent stays installed on that PC; you can add it again later with its pairing code.";
        var answer = MessageBox.Show(this, prompt,
            "Remove device", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        if (_remoteLoops.Remove(m, out var cts)) { cts.Cancel(); cts.Dispose(); }
        if (_shell.Selected == m) _shell.Selected = _local;
        _shell.Machines.Remove(m);
        _settings.Machines.RemoveAll(s => s.Id == m.Id);
        _settings.Save();
    }

    // ---------------------------------------------------------------- tray / window lifecycle

    public void ShowFromTray()
    {
        if (_exiting) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _settings.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        // Nudge to the foreground even when another app has focus. Done with the change-tracking
        // suppressed, otherwise the temporary "true" would be saved as the user's setting.
        if (!Topmost)
        {
            _nudging = true;
            try { Topmost = true; Topmost = false; }
            finally { _nudging = false; }
        }
        Focus();
    }

    private void HideToTray()
    {
        SavePlacement();
        Hide();
        if (!_settings.TrayHintShown)
        {
            _tray.ShowHint("SysPulse is still running",
                "It's in the notification area. Right-click the icon to exit or change settings.");
            _settings.TrayHintShown = true;
            _settings.Save();
        }
    }

    /// <summary>Really quit (tray "Exit", --exit, Windows sign-out, installer upgrade).</summary>
    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _exiting = true;
        SavePlacement();
        _cts.Cancel();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    // ---------------------------------------------------------------- settings

    private void SetInterval(int ms)
    {
        _settings.IntervalMs = ms;
        if (_timer != null) _timer.Period = TimeSpan.FromMilliseconds(ms);
        SettingsChanged();
    }

    private void SetCloseToTray(bool on)
    {
        _settings.CloseToTray = on;
        SettingsChanged();
    }

    private void SettingsChanged()
    {
        SyncControls();
        _tray?.Sync(_settings);
        _settings.Save();
    }

    private void SyncControls()
    {
        _syncing = true;
        try
        {
            Speed500.IsChecked = _settings.IntervalMs == 500;
            Speed1000.IsChecked = _settings.IntervalMs == 1000;
            Speed2000.IsChecked = _settings.IntervalMs == 2000;
            CloseToTrayToggle.IsChecked = _settings.CloseToTray;
        }
        finally { _syncing = false; }
    }

    // Checked/Unchecked (not Click) so keyboard, accessibility tools and code all go through the same path.
    // SyncControls sets IsChecked too; the guards stop that from looping.
    private void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int ms) && ms != _settings.IntervalMs)
            SetInterval(ms);
    }

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool on = ((ToggleButton)sender).IsChecked == true;
        if (on != _settings.CloseToTray) SetCloseToTray(on);
    }

    private void RestorePlacement()
    {
        if (_settings is not { Left: double l, Top: double t, Width: double w, Height: double h }) return;

        // Only restore if the window would still be reachable (monitors can change between runs)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(l, t, w, 40);
        if (w < MinWidth || h < MinHeight || !screen.IntersectsWith(titleBar)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (l, t, w, h);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        // RestoreBounds = the normal-size rectangle even while maximized/minimized/hidden
        var r = WindowState == WindowState.Normal && IsVisible ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty && r.Width > 0)
            (_settings.Left, _settings.Top, _settings.Width, _settings.Height) = (r.Left, r.Top, r.Width, r.Height);
        if (IsVisible) _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        var dashboard = _shell.Selected?.Dashboard;
        if (dashboard == null) return;
        try
        {
            Clipboard.SetText(dashboard.FetchText());
            dashboard.Status = "System info copied to clipboard";
        }
        catch (Exception ex)
        {
            dashboard.Status = "Copy failed: " + ex.Message;
        }
    }
}
