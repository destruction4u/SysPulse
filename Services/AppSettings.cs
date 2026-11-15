using System.IO;
using System.Text.Json;

namespace SysPulse.Services;

/// <summary>User preferences, stored as JSON in %APPDATA%\SysPulse\settings.json.</summary>
public sealed class AppSettings
{
    public static readonly int[] Intervals = [500, 1000, 2000];

    // Window placement (null = first run: center on screen)
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    public bool Topmost { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysPulse");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            s = new(); // corrupt file: fall back to defaults
        }
        if (!Intervals.Contains(s.IntervalMs)) s.IntervalMs = 1000;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true); // atomic replace: never leaves a half-written file
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
