namespace SysPulse.Core;

/// <summary>
/// Reads Linux's /proc and /sys text files relative to a root folder ("/" in production;
/// a fixture folder in tests, which is how the Linux code is tested on Windows).
/// Every read is best-effort: missing or unreadable files just give null/empty.
/// </summary>
internal sealed class LinuxFs(string root)
{
    public string Root { get; } = root;

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <remarks>ReadAllText (not ReadAllBytes): procfs files report size 0, so we must read until EOF.</remarks>
    public string? Read(string relative)
    {
        try { return File.ReadAllText(PathOf(relative)); }
        catch { return null; }
    }

    /// <summary>First line, trimmed of whitespace and the NULs that device-tree strings end with.</summary>
    public string Line(string relative) =>
        Read(relative)?.Split('\n')[0].Trim().Trim('\0').Trim() ?? "";

    public long? Long(string relative) => long.TryParse(Line(relative), out long v) ? v : null;

    public bool Exists(string relative) => File.Exists(PathOf(relative)) || Directory.Exists(PathOf(relative));

    /// <summary>Names of entries in a folder (sysfs folders are full of symlinks, so files and folders both count).</summary>
    public List<string> Entries(string relative)
    {
        try { return Directory.EnumerateFileSystemEntries(PathOf(relative)).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToList(); }
        catch { return []; }
    }

    /// <summary>Parses KEY=value lines (os-release, locale.conf), stripping quotes.</summary>
    public static Dictionary<string, string> KeyValues(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text == null) return map;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('#')) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
        }
        return map;
    }
}
