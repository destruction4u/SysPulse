namespace SysPulse.Core.Tests;

/// <summary>A throwaway folder laid out like a Linux root (/proc, /sys, /etc ...) for the Linux readers.</summary>
public sealed class FakeLinux : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "syspulse-fakelinux-" + Guid.NewGuid().ToString("N"));
    public long NowMs { get; set; } = 1_000_000;

    public FakeLinux() => Directory.CreateDirectory(Root);

    public FakeLinux Write(string path, string content)
    {
        string full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.Replace("\r\n", "\n"));
        return this;
    }

    public FakeLinux Dir(string path)
    {
        Directory.CreateDirectory(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        return this;
    }

    public FakeLinux Delete(string path)
    {
        string full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        return this;
    }

    internal LinuxFs Fs => new(Root);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
