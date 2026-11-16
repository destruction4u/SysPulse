using System.Text.Json;
using SysPulse.Core.Remote;

namespace SysPulse.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Pairing_codes_are_24_unambiguous_characters()
    {
        for (int i = 0; i < 200; i++)
        {
            string code = PairingCode.Generate();
            Assert.Equal(24, code.Length);
            Assert.DoesNotContain(code, c => "ILOU".Contains(c));
            Assert.True(PairingCode.IsValid(code));
        }
    }

    [Fact]
    public void Pairing_code_input_is_forgiving()
    {
        string code = "QNH4JQ0B67X22PPQ2ARHYEDH";
        Assert.Equal("QNH4-JQ0B-67X2-2PPQ-2ARH-YEDH", PairingCode.Format(code));
        Assert.True(PairingCode.Matches(" qnh4-jq0b 67x2-2ppq-2arh-yedh ", code));
        Assert.True(PairingCode.Matches("QNH4JQOB67X22PPQ2ARHYEDH", code)); // letter O typed for zero
        Assert.False(PairingCode.Matches("QNH4JQ0B67X22PPQ2ARHYEDJ", code));
        Assert.False(PairingCode.Matches("QNH4JQ0B67X22PPQ2ARHYED", code)); // too short
        Assert.False(PairingCode.Matches("", ""));                          // empty never matches
    }

    [Fact]
    public void Sample_survives_the_agent_to_viewer_json_round_trip()
    {
        var s = new Sample(42.5, 2400, [10, 90], 16UL << 30, 8UL << 30, 2UL << 30, 1UL << 30, 1000, 200, 5, 1e6, 2e6, null,
            [new ProcSample("my (weird) app", 100, 10, 1 << 20)], 3, [new DriveSample("/", "nvme0n1p2 · ext4", 100, 40)],
            new BatterySample(80, false, false, 3600), TimeSpan.FromHours(5.5));

        var back = JsonSerializer.Deserialize<Sample>(JsonSerializer.Serialize(s, AgentProtocol.Json), AgentProtocol.Json)!;

        Assert.Equal(s.CpuPercent, back.CpuPercent);
        Assert.Equal(s.CorePercents, back.CorePercents);
        Assert.Equal(s.MemUsed, back.MemUsed);
        Assert.Null(back.GpuPercent);
        Assert.Equal("my (weird) app", back.TopProcesses![0].Name);
        Assert.Equal("nvme0n1p2 · ext4", back.Drives[0].Label);
        Assert.Equal(s.Battery, back.Battery);
        Assert.Equal(s.Uptime, back.Uptime);
    }

    [Fact]
    public void Info_from_an_agent_without_platform_field_is_treated_as_windows()
    {
        // What a 1.2.0 Windows agent sent before the Platform field existed
        string json = """{"agentVersion":"1.2.0","machine":{"user":"bob","host":"OFFICE-PC","os":"Windows 11 Pro","gpus":["Intel UHD"]}}""";
        var info = JsonSerializer.Deserialize<AgentInfo>(json, AgentProtocol.Json)!;

        Assert.Equal("Windows", info.Machine.Platform);
        Assert.Equal("OFFICE-PC", info.Machine.Host);
        Assert.Equal(["Intel UHD"], info.Machine.Gpus);
    }
}
