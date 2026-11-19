using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using SysPulse.Core;

namespace SysPulse.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed record InfoLine(string Key, string Value);
public sealed record ProcRow(string Name, int Pid, double Cpu, string CpuText, string MemText);
public sealed record DriveRow(string Title, string Detail, double Percent);

/// <summary>Rolling window of the last N samples for a graph.</summary>
public sealed class History(int capacity = 60)
{
    private readonly Queue<double> _q = new();

    public double[] Push(double v)
    {
        _q.Enqueue(v);
        while (_q.Count > capacity) _q.Dequeue();
        return _q.ToArray();
    }
}

/// <summary>One dashboard tile: big number, subtitles, and a graph (one or two series).</summary>
public sealed class MetricVM(string title, Brush color, double max, Brush? color2 = null) : ObservableObject
{
    public string Title { get; } = title;
    public Brush Color { get; } = color;
    public Brush? Color2 { get; } = color2;
    public double Max { get; } = max;

    private string _value = "—", _sub = "", _sub2 = "";
    private double[]? _history, _history2;

    public string Value { get => _value; set => Set(ref _value, value); }
    public string Sub { get => _sub; set => Set(ref _sub, value); }
    public string Sub2 { get => _sub2; set => Set(ref _sub2, value); }
    public double[]? History { get => _history; set => Set(ref _history, value); }
    public double[]? History2 { get => _history2; set => Set(ref _history2, value); }
}

public sealed class CoreVM(int index) : ObservableObject
{
    public const double BarMax = 54;
    private double _value;

    public string Label { get; } = index.ToString();
    public double BarHeight => Math.Max(2, _value / 100 * BarMax);
    public string Tip => $"Thread {Label}: {_value:0}%";

    public double Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            Raise(nameof(BarHeight));
            Raise(nameof(Tip));
        }
    }
}

/// <summary>Everything one machine's dashboard shows (this PC, or a remote PC via its agent).</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public MetricVM Cpu { get; } = new("CPU", B("#4FC3F7"), 100);
    public MetricVM Memory { get; } = new("MEMORY", B("#B388FF"), 100);
    public MetricVM Gpu { get; } = new("GPU", B("#F06292"), 100);
    public MetricVM Network { get; } = new("NETWORK", B("#69F0AE"), 0, B("#FFD54F"));
    public MetricVM Disk { get; } = new("DISK", B("#FF8A65"), 100);
    public MetricVM Power { get; } = new("BATTERY", B("#AED581"), 100);
    public IReadOnlyList<MetricVM> Metrics { get; }

    public ObservableCollection<CoreVM> Cores { get; } = [];

    private readonly History _cpuH = new(), _memH = new(), _gpuH = new(), _downH = new(), _upH = new(), _diskH = new(), _batH = new();

    private StaticInfo? _info;
    private string _title = Environment.UserName + "@" + Environment.MachineName;
    private IReadOnlyList<InfoLine> _infoLines = [new("Status", "Reading system information…")];
    private IReadOnlyList<ProcRow> _processes = [];
    private IReadOnlyList<DriveRow> _drives = [];
    private string _status = "Starting…";
    private int _processCount;

    public DashboardViewModel() => Metrics = [Cpu, Memory, Gpu, Network, Disk, Power];

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Separator => new('─', Title.Length);
    public IReadOnlyList<InfoLine> InfoLines { get => _infoLines; private set => Set(ref _infoLines, value); }
    public IReadOnlyList<ProcRow> Processes { get => _processes; private set => Set(ref _processes, value); }
    public IReadOnlyList<DriveRow> Drives { get => _drives; private set => Set(ref _drives, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    private static readonly Brush WindowsLogoBrush = B("#4FC3F7"), LinuxLogoBrush = B("#FFD54F"), AndroidLogoBrush = B("#3DDC84");
    private static readonly string WindowsLogo = BuildWindowsLogo();

    /// <summary>An Android robot in block characters (Android devices), neofetch style.</summary>
    private const string AndroidLogo = """
          ▄▄       ▄▄
            ██   ██
          ▄▄███████▄▄
         ██  ██ ██  ██
         ███████████
         ██       ██
         ██       ██
          ▀▀     ▀▀
        """;

    /// <summary>A penguin in block characters (Linux machines), neofetch style.</summary>
    private const string LinuxLogo = """
              ▄▄████▄▄
             ██▀ ██ ▀██
             ██▄▄▀▀▄▄██
            ▄██▀▀▀▀▀▀██▄
           ██▀        ▀██
          ██            ██
          ██            ██
           ██▄        ▄██
         ▄▄▄██▄▄    ▄▄██▄▄▄
         ▀▀▀▀▀▀▀    ▀▀▀▀▀▀▀
        """;

    private string _logo = WindowsLogo;
    private Brush _logoBrush = WindowsLogoBrush;

    /// <summary>The OS logo in block characters: Windows panes or a Linux penguin.</summary>
    public string Logo { get => _logo; private set => Set(ref _logo, value); }
    public Brush LogoBrush { get => _logoBrush; private set => Set(ref _logoBrush, value); }

    private bool IsLinux => _info?.Platform == "Linux";
    private bool IsAndroid => _info?.Platform == "Android";
    private bool IsUnixLike => IsLinux || IsAndroid; // swap in the "commit" slot, same /proc semantics

    private static string BuildWindowsLogo()
    {
        string pane = new('█', 10);
        string row = pane + "  " + pane;
        var sb = new StringBuilder();
        for (int i = 0; i < 5; i++) sb.AppendLine(row);
        sb.AppendLine();
        for (int i = 0; i < 5; i++) sb.AppendLine(row);
        return sb.ToString().TrimEnd();
    }

    public void SetStatic(StaticInfo info)
    {
        _info = info;
        Title = $"{info.User}@{info.Host}";
        Raise(nameof(Separator));
        Gpu.Sub2 = info.Gpus.FirstOrDefault() ?? "";
        Logo = IsAndroid ? AndroidLogo : IsLinux ? LinuxLogo : WindowsLogo;
        LogoBrush = IsAndroid ? AndroidLogoBrush : IsLinux ? LinuxLogoBrush : WindowsLogoBrush;
        Status = "Starting monitors…";
    }

    public bool HasInfo => _info != null;

    public void Apply(Sample s)
    {
        if (_info is not { } info) return; // static info must arrive first

        // CPU
        Cpu.Value = $"{s.CpuPercent:0}%";
        Cpu.Sub = s.CpuMhz is double mhz ? $"{mhz / 1000:0.00} GHz" : "";
        Cpu.Sub2 = $"{info.LogicalProcessors} threads";
        Cpu.History = _cpuH.Push(s.CpuPercent);

        // Memory
        double memPct = s.MemTotal > 0 ? 100.0 * s.MemUsed / s.MemTotal : 0;
        Memory.Value = $"{memPct:0}%";
        Memory.Sub = $"{Fmt.Bytes(s.MemUsed)} / {Fmt.Bytes(s.MemTotal)}";
        Memory.Sub2 = IsUnixLike
            ? (s.CommitTotal > 0 ? $"Swap {Fmt.Bytes(s.CommitUsed)} / {Fmt.Bytes(s.CommitTotal)}" : "No swap")
            : $"Commit {Fmt.Bytes(s.CommitUsed)} / {Fmt.Bytes(s.CommitTotal)}";
        Memory.History = _memH.Push(memPct);

        // GPU
        Gpu.Value = s.GpuPercent is double g ? $"{g:0}%" : "n/a";
        // Linux: AMD reports overall busy %, NVIDIA via nvidia-smi; Intel GPUs don't expose a simple load counter
        Gpu.Sub = s.GpuPercent == null ? (IsLinux ? "Load not reported by this GPU" : "Not available") : IsLinux ? "Busy" : "3D engine";
        Gpu.History = _gpuH.Push(s.GpuPercent ?? 0);

        // Network
        Network.Value = $"↓ {Fmt.Rate(s.NetDown)}";
        Network.Sub = $"↑ {Fmt.Rate(s.NetUp)}";
        Network.Sub2 = info.LocalIp;
        Network.History = _downH.Push(s.NetDown);
        Network.History2 = _upH.Push(s.NetUp);

        // Disk
        Disk.Value = s.DiskActive is double d ? $"{d:0}%" : "n/a";
        Disk.Sub = $"Read {Fmt.Rate(s.DiskRead ?? 0)}";
        Disk.Sub2 = $"Write {Fmt.Rate(s.DiskWrite ?? 0)}";
        Disk.History = _diskH.Push(s.DiskActive ?? 0);

        // Battery
        if (s.Battery is { } bat)
        {
            Power.Value = $"{bat.Percent}%";
            Power.Sub = bat.Charging ? "Charging" : bat.OnAc ? "Plugged in" : "On battery";
            Power.Sub2 = !bat.OnAc && bat.SecondsLeft > 0 ? $"{Fmt.Duration(TimeSpan.FromSeconds(bat.SecondsLeft))} left" : "";
            Power.History = _batH.Push(bat.Percent);
        }
        else
        {
            Power.Value = "AC";
            Power.Sub = "No battery detected";
        }

        // Per-thread bars
        if (Cores.Count != s.CorePercents.Length)
        {
            Cores.Clear();
            for (int i = 0; i < s.CorePercents.Length; i++) Cores.Add(new CoreVM(i));
        }
        for (int i = 0; i < s.CorePercents.Length; i++) Cores[i].Value = s.CorePercents[i];

        if (s.TopProcesses is { } top)
            Processes = top
                .Select(p => new ProcRow(p.Name, p.Pid, p.Cpu, $"{p.Cpu:0.0}%", Fmt.Bytes(p.Memory)))
                .ToList();
        if (s.ProcessCount is int n) _processCount = n;

        Drives = s.Drives
            .Select(d =>
            {
                long used = d.Total - d.Free;
                string title = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name}  {d.Label}";
                return new DriveRow(title, $"{Fmt.Bytes(used)} / {Fmt.Bytes(d.Total)}", d.Total > 0 ? 100.0 * used / d.Total : 0);
            })
            .ToList();

        InfoLines = BuildInfo(info, s, memPct);
        Status = $"{_processCount} processes  ·  updated {DateTime.Now:T}";
    }

    private static List<InfoLine> BuildInfo(StaticInfo info, Sample s, double memPct)
    {
        var lines = new List<InfoLine>
        {
            new("OS", info.Os),
            new("Host", info.Model),
            new("Kernel", info.Kernel),
            new("Uptime", Fmt.Duration(s.Uptime)),
            new("CPU", info.Cpu),
            new("Topology", info.CpuTopology),
        };
        lines.AddRange(info.Gpus.Select(g => new InfoLine("GPU", g)));
        lines.Add(new("Memory", $"{Fmt.Bytes(s.MemUsed)} / {Fmt.Bytes(s.MemTotal)} ({memPct:0}%)"));
        foreach (var d in s.Drives)
        {
            // "Disk (C:)" fits the key column; long Linux mount points ("/srv/media") go after the value
            string usage = $"{Fmt.Bytes(d.Total - d.Free)} / {Fmt.Bytes(d.Total)}";
            if (d.Name.Length <= 4) lines.Add(new($"Disk ({d.Name})", usage));
            else lines.Add(new("Disk", $"{usage}  ({d.Name})"));
        }
        lines.AddRange(info.Displays.Select(d => new InfoLine("Display", d)));
        if (s.Battery is { } b) lines.Add(new("Battery", $"{b.Percent}%{(b.Charging ? " (charging)" : "")}"));
        lines.Add(new("Local IP", info.LocalIp));
        lines.Add(new("Theme", info.Theme));
        lines.Add(new("Locale", info.Locale));
        lines.Add(new("BIOS", info.Bios));
        return lines.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
    }

    /// <summary>Plain-text version of the fetch panel, for the clipboard.</summary>
    public string FetchText()
    {
        var sb = new StringBuilder().AppendLine(Title).AppendLine(Separator);
        foreach (var l in InfoLines) sb.AppendLine($"{l.Key}: {l.Value}");
        return sb.ToString();
    }
}
