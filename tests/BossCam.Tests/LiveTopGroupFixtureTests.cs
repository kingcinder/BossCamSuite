using System.Text.Json;
using Xunit;

namespace BossCam.Tests;

public sealed class LiveTopGroupFixtureTests
{
    private sealed record ResultRow(string ip, string group, string field, bool verified, string endpoint);

    [Fact]
    public void LiveFixture_HasVerifiedTopGroupWrites_ForAllThreeCameras()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "5523w", "firmware-unknown", "2026-04-19-topgroup-write-readback.json");
        Assert.True(File.Exists(fixturePath), $"Expected fixture file at {fixturePath}");

        var rows = JsonSerializer.Deserialize<List<ResultRow>>(File.ReadAllText(fixturePath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        var ips = new[] { "10.0.0.4", "10.0.0.29", "10.0.0.227" };
        foreach (var ip in ips)
        {
            Assert.Contains(rows, row => row.ip == ip && row.group == "video_image" && row.field == "brightnessLevel" && row.verified);
            Assert.Contains(rows, row => row.ip == ip && row.group == "video_image" && row.field == "constantBitRate" && row.verified);
            Assert.Contains(rows, row => row.ip == ip && row.group == "network_wireless" && row.field == "esee.enabled" && row.verified);
            Assert.Contains(rows, row => row.ip == ip && row.group == "users_maintenance" && row.field == "set_pass_blank_to_blank" && row.verified);
        }
    }
}
