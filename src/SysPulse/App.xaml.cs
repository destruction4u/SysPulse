using System.Runtime.InteropServices;
using System.Windows;
using SysPulse.Core;
using SysPulse.Services;

namespace SysPulse;

public partial class App : Application
{
    // Per-user names: one SysPulse per signed-in user.
    private const string InstanceId = @"Local\SysPulse-9F1C2B7E";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showSignal, _exitSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool HasArg(string a) => e.Args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));

        // Single instance: if SysPulse is already running (maybe hidden in the tray),
        // ask it to show its window (or, with --exit, to quit) and end this copy.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceId + "-mutex", out _ownsMutex);
        if (!_ownsMutex)
        {
            if (HasArg("--exit"))
            {
                // Used by the installer before upgrading/uninstalling: Windows' normal "close app"
                // request can't reach a tray-only SysPulse because it has no window.
                try { EventWaitHandle.OpenExisting(InstanceId + "-exit").Set(); } catch { }
                try { if (_instanceMutex.WaitOne(TimeSpan.FromSeconds(10))) _instanceMutex.ReleaseMutex(); }
                catch (AbandonedMutexException) { _instanceMutex.ReleaseMutex(); }
            }
            else
            {
                AllowSetForegroundWindow(-1); // let the running copy bring itself to the front
                try { EventWaitHandle.OpenExisting(InstanceId + "-show").Set(); } catch { }
            }
            Shutdown();
            return;
        }
        if (HasArg("--exit")) { Shutdown(); return; } // nothing was running

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceId + "-show");
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceId + "-exit");

        // The window may be hidden while the tray icon keeps the app alive.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = AppSettings.Load();
        var window = new MainWindow(settings);
        MainWindow = window;
        if (!HasArg("--tray")) window.Show();
        window.StartMonitoring();

        WaitHandle[] signals = [_showSignal, _exitSignal];
        new Thread(() =>
        {
            while (true)
            {
                int which;
                try { which = WaitHandle.WaitAny(signals); }
                catch (ObjectDisposedException) { return; }
                if (which == 0) Dispatcher.InvokeAsync(window.ShowFromTray);
                else { Dispatcher.InvokeAsync(window.ExitApp); return; }
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" }.Start();

        // Windows sign-out/shutdown, or an installer closing the app: really exit, don't hide.
        SessionEnding += (_, _) => window.ExitApp();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _showSignal?.Dispose();
        _exitSignal?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
