using System.Collections.ObjectModel;

namespace SysPulse.ViewModels;

/// <summary>Window-level state: the machine bar and which machine the dashboard shows.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private MachineViewModel? _selected;

    public ObservableCollection<MachineViewModel> Machines { get; } = [];

    public MachineViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value == null || _selected == value) return; // never show "nothing"
            if (_selected != null) _selected.IsSelected = false;
            _selected = value;
            _selected.IsSelected = true;
            Raise();
            SelectedChanged?.Invoke(value);
        }
    }

    public event Action<MachineViewModel>? SelectedChanged;
}
