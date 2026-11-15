using System.Windows.Media;
using SysPulse.Core;
using SysPulse.Core.Remote;
using SysPulse.Services;

namespace SysPulse.ViewModels;

public enum MachineState { Connecting, Online, Offline, Error }

/// <summary>One entry in the machine bar: this PC or a paired remote PC.</summary>
public sealed class MachineViewModel : ObservableObject
{
    private static readonly Brush OnlineBrush = Frozen("#69F0AE"), OfflineBrush = Frozen("#5C6477"),
                                  ConnectingBrush = Frozen("#FFD54F"), ErrorBrush = Frozen("#F07178");

    private string _name;
    private MachineState _state = MachineState.Connecting;
    private string _summary = "Connecting…";
    private bool _isSelected;

    public MachineViewModel(string name, SavedMachine? saved)
    {
        _name = name;
        Saved = saved;
        if (saved == null) { _state = MachineState.Online; _summary = "This PC"; }
    }

    /// <summary>null for this PC.</summary>
    public SavedMachine? Saved { get; }
    public bool IsLocal => Saved == null;
    public string Id => Saved?.Id ?? "local";
    public DashboardViewModel Dashboard { get; } = new();

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public MachineState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StateBrush)); }
    }

    public Brush StateBrush => State switch
    {
        MachineState.Online => OnlineBrush,
        MachineState.Connecting => ConnectingBrush,
        MachineState.Error => ErrorBrush,
        _ => OfflineBrush,
    };

    public string Tooltip => IsLocal ? "This PC" : $"{Saved!.Host}:{Saved.Port}";

    // ------------------------------------------------------------ updates from the poller

    public void ApplySample(Sample s)
    {
        Dashboard.Apply(s);
        State = MachineState.Online;
        double mem = s.MemTotal > 0 ? 100.0 * s.MemUsed / s.MemTotal : 0;
        Summary = $"CPU {s.CpuPercent:0}%  ·  RAM {mem:0}%";
    }

    public void SetOffline(string reason)
    {
        State = MachineState.Offline;
        Summary = "Offline";
        Dashboard.Status = $"Can't reach {Name} ({reason}). Retrying…";
    }

    public void SetError(string message)
    {
        State = MachineState.Error;
        Summary = "Needs attention";
        Dashboard.Status = message;
    }

    public void SetConnecting()
    {
        State = MachineState.Connecting;
        Summary = "Connecting…";
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
