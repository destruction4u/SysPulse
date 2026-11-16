using System.Text.Json;

namespace SysPulse.Core.Remote;

/// <summary>Wire format shared by the agent (server) and the viewer (client).</summary>
public static class AgentProtocol
{
    public const int DefaultPort = 47613;
    public const string ServiceName = "SysPulseAgent";

    public const string InfoPath = "/api/v1/info";
    public const string SamplePath = "/api/v1/sample";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Response of <see cref="AgentProtocol.InfoPath"/>.</summary>
public sealed record AgentInfo(string AgentVersion, StaticInfo Machine);
