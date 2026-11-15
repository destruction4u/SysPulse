using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace SysPulse.Core.Remote;

public sealed class AgentAuthException(string message) : Exception(message);
public sealed class AgentCertificateException(string message) : Exception(message);

/// <summary>
/// HTTPS client for one agent. The agent uses a self-signed certificate, so instead of the
/// public CA system we pin its SHA-256 fingerprint: recorded on first connect ("pairing"),
/// required to match on every connection after that.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _pinned;

    /// <summary>Fingerprint of the certificate the agent presented on the last connection.</summary>
    public string? ObservedFingerprint { get; private set; }

    /// <param name="pinnedFingerprint">null only when pairing for the first time.</param>
    public AgentClient(string host, int port, string pairingCode, string? pinnedFingerprint)
    {
        _pinned = string.IsNullOrWhiteSpace(pinnedFingerprint) ? null : pinnedFingerprint;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
        {
            if (cert == null) return false;
            ObservedFingerprint = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
            return _pinned == null || string.Equals(_pinned, ObservedFingerprint, StringComparison.OrdinalIgnoreCase);
        };

        string h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host; // IPv6 literal
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{h}:{port}"),
            Timeout = TimeSpan.FromSeconds(6),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PairingCode.Normalize(pairingCode));
    }

    public async Task<AgentInfo> GetInfoAsync(CancellationToken ct = default) =>
        await SendAsync<AgentInfo>(AgentProtocol.InfoPath, ct)
        ?? throw new HttpRequestException("The agent is still starting up. Try again in a few seconds.");

    /// <returns>null while the agent is warming up (it pauses sampling when nobody is watching).</returns>
    public Task<Sample?> GetSampleAsync(CancellationToken ct = default) => SendAsync<Sample>(AgentProtocol.SamplePath, ct);

    private async Task<T?> SendAsync<T>(string path, CancellationToken ct) where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && _pinned != null && ObservedFingerprint != _pinned)
        {
            throw new AgentCertificateException(
                "This PC's security certificate has changed since it was added. If you reinstalled the agent, remove the PC and add it again.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AgentAuthException("The pairing code was rejected.");
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(AgentProtocol.Json, ct);
        }
    }

    public void Dispose() => _http.Dispose();
}
