using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using SysPulse.Services;
using SysPulse.ViewModels;

namespace SysPulse;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly CancellationTokenSource _cts = new();
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
        DataContext = _vm;

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

    // ---------------------------------------------------------------- monitoring

    public async void StartMonitoring()
    {
        try
        {
            // WMI and perf-counter setup are slow (~1-2 s), so keep them off the UI thread.
            var info = await Task.Run(StaticInfo.Collect);
            _vm.SetStatic(info);
            var monitor = await Task.Run(() => new SystemMonitor(info));
            await RunAsync(monitor, _cts.Token);
        }
        catch (Exception ex)
        {
            _vm.Status = "Failed to start: " + ex.Message;
        }
    }

    private async Task RunAsync(SystemMonitor monitor, CancellationToken ct)
    {
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.IntervalMs));
        try
        {
            do
            {
                try
                {
                    bool visible = IsVisible && WindowState != WindowState.Minimized;
                    var sample = await Task.Run(() => monitor.Sample(includeProcesses: visible), ct);
                    _vm.Apply(sample); // back on the UI thread here
                    UpdateTray(sample);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _vm.Status = "Sampling error: " + ex.Message; }
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

    /// <summary>Really quit (tray "Exit", Windows sign-out, installer upgrade).</summary>
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
    // SyncControls sets IsChecked too; the equality guards stop that from looping.
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
        try
        {
            Clipboard.SetText(_vm.FetchText());
            _vm.Status = "System info copied to clipboard";
        }
        catch (Exception ex)
        {
            _vm.Status = "Copy failed: " + ex.Message;
        }
    }
}
