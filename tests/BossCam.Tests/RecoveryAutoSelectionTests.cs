using BossCam.Core;

namespace BossCam.Tests;

/// <summary>
/// Pins the autonomous camera-recovery selection rules: identity normalization across the
/// three spellings a 5523-W serial appears in (AP SSID IPC…, serial JA…, deviceInfo Z7C…),
/// per-serial cooldown, and strongest-signal ranking.
///
/// Deliberately NO enrolled-skip coverage: a camera broadcasting its AP is off the LAN by
/// definition and needs recovery regardless of any existing Suite record (both live-verified
/// 2026-08-11 units had pre-existing records yet required recovery after factory reset).
/// </summary>
public sealed class RecoveryAutoSelectionTests
{
    private static CameraApInfo Ap(string ssid, string signal, string? bssid = null, string serial = "")
        => new()
        {
            Ssid = ssid,
            Bssid = bssid ?? "9C:A3:A9:00:00:00",
            Signal = signal,
            Security = "WPA2",
            Serial = string.IsNullOrEmpty(serial) ? $"JA{ssid[3..]}" : serial
        };

    [Fact]
    public void NormalizeIdentity_Maps_All_Serial_Spellings_To_One_Key()
    {
        // AP-derived serial (IPC… → JA…), raw deviceInfo serial (Z7C…), and a lowercase
        // value all normalize to the same canonical key.
        Assert.Equal("Z7C34781620744", RecoveryAutoSelection.NormalizeIdentity("JAZ7C34781620744"));
        Assert.Equal("Z7C34781620744", RecoveryAutoSelection.NormalizeIdentity("Z7C34781620744"));
        Assert.Equal("Z7C34781620744", RecoveryAutoSelection.NormalizeIdentity("jaz7c34781620744"));
    }

    [Fact]
    public void NormalizeIdentity_Strips_Mac_Separators_And_Cases()
    {
        Assert.Equal("9CA3A9BC6FEC", RecoveryAutoSelection.NormalizeIdentity("9C:A3:A9:BC:6F:EC"));
        Assert.Equal("9CA3A9BC6FEC", RecoveryAutoSelection.NormalizeIdentity("9c-a3-a9-bc-6f-ec"));
    }

    [Fact]
    public void PickCandidate_Returns_Strongest_Ap()
    {
        var aps = new[]
        {
            Ap("IPCZ7C34781611111", "45"),
            Ap("IPCZ7C34781622222", "90"),
            Ap("IPCZ7C34781633333", "60")
        };

        var picked = RecoveryAutoSelection.PickCandidate(
            aps, new Dictionary<string, DateTimeOffset>(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));

        Assert.NotNull(picked);
        Assert.Equal("IPCZ7C34781622222", picked.Ssid); // strongest signal
    }

    [Fact]
    public void PickCandidate_Skips_Serials_In_Cooldown()
    {
        var aps = new[]
        {
            Ap("IPCZ7C34781611111", "45"),
            Ap("IPCZ7C34781622222", "90")
        };
        var now = DateTimeOffset.UtcNow;
        var cooldown = new Dictionary<string, DateTimeOffset>
        {
            ["Z7C34781622222"] = now // strongest is cooling down → next strongest wins
        };

        var picked = RecoveryAutoSelection.PickCandidate(aps, cooldown, now, TimeSpan.FromMinutes(30));

        Assert.NotNull(picked);
        Assert.Equal("IPCZ7C34781611111", picked.Ssid);
    }

    [Fact]
    public void PickCandidate_Allows_Cooldown_Expiry()
    {
        var aps = new[] { Ap("IPCZ7C34781622222", "90") };
        var now = DateTimeOffset.UtcNow;
        var cooldown = new Dictionary<string, DateTimeOffset>
        {
            ["Z7C34781622222"] = now - TimeSpan.FromMinutes(45) // older than the 30m window
        };

        var picked = RecoveryAutoSelection.PickCandidate(aps, cooldown, now, TimeSpan.FromMinutes(30));

        Assert.NotNull(picked);
        Assert.Equal("IPCZ7C34781622222", picked.Ssid);
    }

    [Fact]
    public void PickCandidate_Returns_Null_When_All_In_Cooldown()
    {
        var aps = new[] { Ap("IPCZ7C34781622222", "90"), Ap("IPCZ7C34781633333", "45") };
        var now = DateTimeOffset.UtcNow;
        var cooldown = new Dictionary<string, DateTimeOffset>
        {
            ["Z7C34781622222"] = now,
            ["Z7C34781633333"] = now
        };

        var picked = RecoveryAutoSelection.PickCandidate(aps, cooldown, now, TimeSpan.FromMinutes(30));

        Assert.Null(picked);
    }

    [Fact]
    public void PickCandidate_Takes_No_Enrollment_Input_And_Picks_Visible_Ap()
    {
        // Smoke pin for the no-enrolled-skip redesign: the selection signature deliberately
        // takes NO enrolled-identity set (the param was removed) — a visible camera AP is
        // recovered regardless of any existing Suite record, because AP mode means the camera
        // dropped off the LAN. This test pins the signature shape AND that a lone visible AP
        // (even one whose MAC/serial would match an enrolled record, were one passed) is picked.
        var aps = new[] { Ap("IPCZ7C34781622222", "90", bssid: "9C:A3:A9:BC:6F:EC") };
        var cooldown = new Dictionary<string, DateTimeOffset>();

        var picked = RecoveryAutoSelection.PickCandidate(aps, cooldown, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));

        Assert.NotNull(picked);
        Assert.Equal("IPCZ7C34781622222", picked.Ssid);
    }
}
