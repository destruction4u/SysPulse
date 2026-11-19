using System.Diagnostics;
using System.Text;

namespace SysPulse.Core;

/// <summary>Runs a command on an Android device and returns its stdout. Abstracted so the
/// monitor and the info collector can be unit-tested against canned command output.</summary>
public interface IAdbShell
{
    /// <summary>Runs a shell command on the device and returns stdout. Returns "" on any failure.</summary>
    string Run(string command);
}

/// <summary>One line of `adb devices -l`.</summary>
public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsReady => State == "device";
    public string Display => Model.Length > 0 ? $"{Model} ({Serial})" : Serial;
}

/// <summary>
/// Talks to one Android device through the platform-tools `adb` executable. There's no agent on
/// the phone: readings are pulled over `adb exec-out` (the same /proc and dumpsys reads a shell
/// session would do). Works over USB or wireless adb; the transport is adb's concern, not ours.
/// </summary>
public sealed class AdbClient : IAdbShell
{
    private readonly string _adb;
    private readonly string _serial;
    private readonly int _timeoutMs;

    public AdbClient(string adbPath, string serial, int timeoutMs = 8000)
    {
        _adb = adbPath;
        _serial = serial;
        _timeoutMs = timeoutMs;
    }

    public string AdbPath => _adb;
    public string Serial => _serial;

    public string Run(string command)
    {
        // The whole command goes as ONE argv entry after exec-out, so adb forwards it verbatim to
        // the device shell: semicolons, globs and quotes are interpreted on the phone, not here.
        var r = Exec(_adb, ["-s", _serial, "exec-out", command], _timeoutMs);
        return r.Ok ? r.Stdout : "";
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>Locates adb: SYSPULSE_ADB, then PATH, then the usual install spots. null if not found.</summary>
    public static string? FindAdb()
    {
        string exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("SYSPULSE_ADB") is { Length: > 0 } env) candidates.Add(env);
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir, exe));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localApp, "Android", "Sdk", "platform-tools", exe));
        candidates.Add(Path.Combine(home, "platform-tools", exe));
        candidates.Add(Path.Combine(home, "platform-tools-dl", "platform-tools", exe));
        if (OperatingSystem.IsWindows()) candidates.Add(@"C:\platform-tools\adb.exe");
        else { candidates.Add("/usr/lib/android-sdk/platform-tools/adb"); candidates.Add("/usr/local/bin/adb"); }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Parses `adb devices -l`. Empty list if adb can't be run.</summary>
    public static IReadOnlyList<AdbDevice> ListDevices(string adbPath, int timeoutMs = 8000)
    {
        var list = new List<AdbDevice>();
        var r = Exec(adbPath, ["devices", "-l"], timeoutMs);
        if (!r.Ok) return list;
        foreach (string line in r.Stdout.Split('\n').Skip(1)) // first line is "List of devices attached"
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            var f = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2) continue;
            string model = "";
            foreach (string kv in f) if (kv.StartsWith("model:")) model = kv["model:".Length..].Replace('_', ' ').Trim();
            list.Add(new AdbDevice(f[0], f[1], model));
        }
        return list;
    }

    /// <summary>`adb connect host[:port]` for wireless adb. Returns whether the device is now connected.</summary>
    public static (bool Ok, string Message) Connect(string adbPath, string address, int timeoutMs = 12000)
    {
        var r = Exec(adbPath, ["connect", address], timeoutMs);
        string msg = (r.Stdout + " " + r.Stderr).Trim();
        // adb prints "connected to ..." or "already connected to ..." on success; "cannot/failed" on error.
        bool ok = msg.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                  && !msg.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                  && !msg.Contains("failed", StringComparison.OrdinalIgnoreCase);
        return (ok, msg.Length > 0 ? msg : "no response from adb");
    }

    // ---------------------------------------------------------------- process plumbing

    private readonly record struct ExecResult(bool Ok, string Stdout, string Stderr);

    private static ExecResult Exec(string exe, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("adb did not start");
            // Read both pipes concurrently so a full stderr buffer can't deadlock a large stdout read.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ExecResult(false, "", "timed out");
            }
            return new ExecResult(p.ExitCode == 0, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new ExecResult(false, "", ex.Message);
        }
    }
}
