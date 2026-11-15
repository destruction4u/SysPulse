using System.Windows;
using System.Windows.Interop;
using SysPulse.Services;
using SysPulse.ViewModels;

namespace SysPulse;

public partial class MainWindow : Window
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly MainViewModel _vm = new();
    private readonly CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;
        Closed += (_, _) => _cts.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
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
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    var sample = await Task.Run(monitor.Sample, ct);
                    _vm.Apply(sample); // back on the UI thread here
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _vm.Status = "Sampling error: " + ex.Message; }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* window closed */ }
        finally
        {
            monitor.Dispose();
        }
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
