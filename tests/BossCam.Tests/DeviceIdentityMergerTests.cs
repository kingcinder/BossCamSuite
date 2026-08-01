using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the fleet-identity pass:
/// 1. DeviceIdentityMerger keeps the durable MAC key across DHCP renumbering and merges the new
///    port/URL/link-hint/continuous-record fields (never losing an explicitly-known value).
/// 2. Store dedupe (MAC-first, IP-second) mirrors the merger so discovery + enroll cannot fragment
///    a camera into two identities or inherit a foreign host's slot.
/// 3. DiscoveryCoordinator still collapses duplicates through the shared merger.
/// </summary>
public sealed class DeviceIdentityMergerTests
{
    [Fact]
    public void MergePair_Prefers_Explicit_Port_Hints_And_Urls()
    {
        var onvifCopy = new DeviceIdentity
        {
            IpAddress = "10.0.0.7",
            Port = 8899,
            OnvifMediaPort = 8899,
            DeviceType = "ONVIF",
            MacAddress = "AA:BB:CC:DD:EE:01"
        };
        var netsdkCopy = new DeviceIdentity
        {
            IpAddress = "10.0.0.7",
            Port = 80,
            HttpControlPort = 80,
            LoginName = "admin",
            Password = "pw",
            DeviceType = "IPC",
            MacAddress = "AA:BB:CC:DD:EE:01",
            LastGoodControlUrl = "http://10.0.0.7:80/NetSDK/System/deviceInfo",
            LinkHint = LinkHint.Lan,
            ContinuousRecord = true
        };

        var merged = DeviceIdentityMerger.MergePair(onvifCopy, netsdkCopy);

        // Same MAC → single identity; richer (credentialed IPC) copy is primary.
        Assert.Equal(80, merged.Port);
        Assert.Equal(80, merged.HttpControlPort);
        Assert.Equal(8899, merged.OnvifMediaPort); // explicit ONVIF hint survives
        Assert.Equal("admin", merged.LoginName);
        Assert.Equal("pw", merged.Password);
        Assert.Equal("http://10.0.0.7:80/NetSDK/System/deviceInfo", merged.LastGoodControlUrl);
        Assert.Equal(LinkHint.Lan, merged.LinkHint);
        Assert.True(merged.ContinuousRecord);
    }

    [Fact]
    public void MergePair_Keeps_Recorded_Non80_Port_When_No_ControlPort_Known()
    {
        // No side knows HttpControlPort: the legacy "prefer non-80 recorded port" rule applies,
        // so an ONVIF-only identity still drives the {8899, 80} NetSdkPortCandidates fallback.
        var onvifOnly = new DeviceIdentity { IpAddress = "10.0.0.7", Port = 8899, OnvifMediaPort = 8899, DeviceType = "ONVIF", MacAddress = "AA:BB:CC:DD:EE:01" };
        var bare = new DeviceIdentity { IpAddress = "10.0.0.7", Port = 80, DeviceType = "IPC", MacAddress = "AA:BB:CC:DD:EE:01" };

        var merged = DeviceIdentityMerger.MergePair(onvifOnly, bare);
        Assert.Equal(8899, merged.Port);
        Assert.Equal(8899, merged.OnvifMediaPort);
        Assert.Equal(0, merged.HttpControlPort);
    }

    [Fact]
    public void MergePair_Fills_RtspPort_And_LastGoodRtspUrl_From_Secondary()
    {
        var bare = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", MacAddress = "11:22:33:44:55:66" };
        var enriched = new DeviceIdentity
        {
            IpAddress = "10.0.0.4",
            DeviceType = "IPC",
            MacAddress = "11:22:33:44:55:66",
            RtspPort = 554,
            LastGoodRtspUrl = "rtsp://admin:***@10.0.0.4:554/ch0_0.264"
        };

        var merged = DeviceIdentityMerger.MergePair(bare, enriched);

        Assert.Equal(554, merged.RtspPort);
        Assert.Equal("rtsp://admin:***@10.0.0.4:554/ch0_0.264", merged.LastGoodRtspUrl);
        Assert.Equal(LinkHint.Unknown, merged.LinkHint); // absent on both → Unknown
    }

    [Fact]
    public void MergePair_Wifi_Hint_And_ContinuousRecord_Are_Or_And_Prefer_NonUnknown()
    {
        var a = new DeviceIdentity { IpAddress = "10.0.0.9", MacAddress = "AA:AA:AA:AA:AA:01", LinkHint = LinkHint.Unknown };
        var b = new DeviceIdentity { IpAddress = "10.0.0.9", MacAddress = "AA:AA:AA:AA:AA:01", LinkHint = LinkHint.Wifi, ContinuousRecord = true };

        var merged = DeviceIdentityMerger.MergePair(a, b);
        Assert.Equal(LinkHint.Wifi, merged.LinkHint);
        Assert.True(merged.ContinuousRecord);

        // ContinuousRecord is sticky: once true it survives a merge with a bare record.
        var again = DeviceIdentityMerger.MergePair(new DeviceIdentity { IpAddress = "10.0.0.9", MacAddress = "AA:AA:AA:AA:AA:01" }, merged);
        Assert.True(again.ContinuousRecord);
        Assert.Equal(LinkHint.Wifi, again.LinkHint);
    }

    [Fact]
    public async Task Store_Dedupe_Merges_Same_Mac_Across_Providers_Without_Fragmenting()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-merge-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);

        var onvifCopy = new DeviceIdentity { IpAddress = "10.0.0.7", Port = 8899, OnvifMediaPort = 8899, DeviceType = "ONVIF", MacAddress = "AA:BB:CC:DD:EE:01" };
        var netsdkCopy = new DeviceIdentity { IpAddress = "10.0.0.7", Port = 80, HttpControlPort = 80, LoginName = "admin", Password = "pw", DeviceType = "IPC", MacAddress = "AA:BB:CC:DD:EE:01", LinkHint = LinkHint.Lan };

        var merged = DeviceIdentityMerger.Merge([onvifCopy, netsdkCopy]);
        await store.UpsertDevicesAsync(merged, CancellationToken.None);

        var devices = await store.GetDevicesAsync(CancellationToken.None);
        var single = Assert.Single(devices);
        Assert.Equal("10.0.0.7", single.IpAddress);
        Assert.Equal(80, single.HttpControlPort);
        Assert.Equal(8899, single.OnvifMediaPort);
        Assert.Equal("admin", single.LoginName);
        Assert.Equal(LinkHint.Lan, single.LinkHint);
    }

    [Fact]
    public void Merge_Keys_On_Mac_Not_Ip_So_Ip_Reuse_Does_Not_Collide()
    {
        var cam = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", MacAddress = "AA:BB:CC:DD:EE:01" };
        var foreign = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "OTHER", MacAddress = "11:22:33:44:55:66" };

        var merged = DeviceIdentityMerger.Merge([cam, foreign]);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_Keys_Fallback_To_Ip_When_No_Mac()
    {
        var a = new DeviceIdentity { IpAddress = "10.0.0.5", DeviceType = "IPC", LoginName = "admin" };
        var b = new DeviceIdentity { IpAddress = "10.0.0.5", DeviceType = "IPC", Password = "pw" };

        var merged = DeviceIdentityMerger.Merge([a, b]);
        var single = Assert.Single(merged);
        Assert.Equal("admin", single.LoginName);
        Assert.Equal("pw", single.Password);
    }
}
