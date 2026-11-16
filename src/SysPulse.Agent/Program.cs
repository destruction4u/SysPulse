using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SysPulse.Agent;
using SysPulse.Core.Remote;

// Command-line tools (install/uninstall/pairing) run and exit; no arguments = run the agent.
if (Cli.TryRun(args, out int exitCode)) return exitCode;

string dataDir = Cli.Option(args, "--data-dir") ?? AgentConfig.DefaultDir;
AgentConfig config;
System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
try
{
    // The service account (SYSTEM on Windows, syspulse-agent on Linux) can read the config; ordinary users can't.
    if (dataDir == AgentConfig.DefaultDir && !AgentConfig.IsPrivileged && !File.Exists(Path.Combine(dataDir, "agent.json")))
        throw new UnauthorizedAccessException();
    config = AgentConfig.LoadOrCreate(dataDir);
    certificate = AgentConfig.LoadOrCreateCertificate(dataDir);
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine(OperatingSystem.IsWindows()
        ? "SysPulse Agent normally runs as a Windows service. To run it by hand, use an elevated prompt, or pass --data-dir <folder> for a test setup."
        : "SysPulse Agent normally runs as a systemd service (systemctl status syspulse-agent). To run it by hand, use sudo, or pass --data-dir <folder> for a test setup.");
    return 1;
}
string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // services start in System32 otherwise
});
builder.Host.UseWindowsService(o => o.ServiceName = AgentProtocol.ServiceName);
builder.Host.UseSystemd(); // readiness + journal-friendly logs when started by systemd
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("SysPulse", LogLevel.Information);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxConcurrentConnections = 32;
    k.Limits.MaxRequestBodySize = 0; // GET only
    k.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
    k.ListenAnyIP(config.Port, listen => listen.UseHttps(certificate));
});

builder.Services.AddSingleton<SampleCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SampleCache>());

var app = builder.Build();

// 1) Local network only (defence in depth on top of the firewall rule).
// 2) Pairing code required on every request. Wrong codes are slowed down to make guessing useless.
app.Use(async (ctx, next) =>
{
    if (!NetGuard.IsLocalNetwork(ctx.Connection.RemoteIpAddress))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    string auth = ctx.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !PairingCode.Matches(auth[7..], config.PairingCode))
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

app.MapGet(AgentProtocol.InfoPath, (SampleCache cache) =>
{
    cache.Touch();
    return cache.Info is { } info
        ? Results.Json(new AgentInfo(version, info), AgentProtocol.Json)
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet(AgentProtocol.SamplePath, (SampleCache cache) =>
{
    cache.Touch();
    return cache.Latest is { } sample
        ? Results.Json(sample, AgentProtocol.Json)
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();
return 0;

static class NetGuard
{
    /// <summary>Loopback, private (RFC 1918), link-local and IPv6 unique-local addresses.</summary>
    public static bool IsLocalNetwork(IPAddress? ip)
    {
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }
}
