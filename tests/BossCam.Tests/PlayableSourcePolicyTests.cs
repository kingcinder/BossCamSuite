using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class PlayableSourcePolicyTests
{
    [Fact]
    public void Resolve_Picks_Main_And_Sub_Without_Allowing_Snapshot_To_Become_Main()
    {
        var sources = new[]
        {
            new VideoSourceDescriptor
            {
                Kind = TransportKind.LanRest,
                Url = "http://camera:80/snapshot.jpg",
                Rank = 25,
                Metadata = new Dictionary<string, string> { ["kind"] = "snapshot" }
            },
            new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = "rtsp://camera:554/ch0_1.264",
                Rank = 50,
                Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["highRes"] = "false" }
            },
            new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = "rtsp://camera:554/ch0_0.264",
                Rank = 0,
                Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" }
            }
        };

        var decision = PlayableSourcePolicy.Resolve(sources);

        Assert.Equal("rtsp://camera:554/ch0_0.264", decision.Main?.Url);
        Assert.Equal("rtsp://camera:554/ch0_1.264", decision.Sub?.Url);
        Assert.Equal("http://camera:80/snapshot.jpg", decision.Snapshot?.Url);
        Assert.Equal(decision.Main, decision.Preferred);
        Assert.False(decision.IsDegraded);
    }

    [Fact]
    public void Resolve_Uses_Sub_As_Preferred_When_Main_Is_Missing_And_Explains_Degradation()
    {
        var sources = new[]
        {
            new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = "rtsp://camera:554/ch0_1.264",
                Rank = 50,
                Metadata = new Dictionary<string, string> { ["stream"] = "sub" }
            }
        };

        var decision = PlayableSourcePolicy.Resolve(sources, preferredStream: "main");

        Assert.Null(decision.Main);
        Assert.Equal("rtsp://camera:554/ch0_1.264", decision.Preferred?.Url);
        Assert.True(decision.IsDegraded);
        Assert.Contains("sub", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Uses_Snapshot_As_Last_Resort_When_No_Rtsp_Source_Exists()
    {
        var sources = new[]
        {
            new VideoSourceDescriptor
            {
                Kind = TransportKind.LanRest,
                Url = "http://camera:80/snapshot.jpg",
                Rank = 25,
                Metadata = new Dictionary<string, string> { ["kind"] = "snapshot" }
            }
        };

        var decision = PlayableSourcePolicy.Resolve(sources, preferredStream: "main");

        Assert.Equal("http://camera:80/snapshot.jpg", decision.Preferred?.Url);
        Assert.True(decision.IsDegraded);
        Assert.Contains("snapshot", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsSub_Does_Not_False_Match_Subtype_In_DisplayName()
    {
        // Live evidence (10.0.0.169): the generic main candidate is displayed as
        // "Generic main /cam/realmonitor?channel=1&subtype=0". A naive
        // DisplayName.Contains("sub") treats the "sub" inside "subtype" as a sub-stream marker,
        // misclassifying the main path as a sub candidate (rank 24) that then outranks the real
        // ch0_1.264 sub (rank 58) — ffmpeg gets fed a nonexistent Dahua main URL and returns 0 bytes.
        var main = new VideoSourceDescriptor
        {
            Kind = TransportKind.Rtsp,
            Url = "rtsp://admin:@10.0.0.169:554/cam/realmonitor?channel=1&subtype=0",
            DisplayName = "Generic main /cam/realmonitor?channel=1&subtype=0",
            Rank = 24,
            Metadata = new Dictionary<string, string>
            {
                ["stream"] = "main",
                ["path"] = "/cam/realmonitor?channel=1&subtype=0",
                ["highRes"] = "true",
                ["generic"] = "true"
            }
        };
        var sub = new VideoSourceDescriptor
        {
            Kind = TransportKind.Rtsp,
            Url = "rtsp://admin:@10.0.0.169:554/ch0_1.264",
            DisplayName = "Generic sub /ch0_1.264",
            Rank = 58,
            Metadata = new Dictionary<string, string>
            {
                ["stream"] = "sub",
                ["path"] = "/ch0_1.264",
                ["highRes"] = "false",
                ["codec"] = "hevc"
            }
        };

        Assert.False(PlayableSourcePolicy.IsSub(main));
        Assert.True(PlayableSourcePolicy.IsSub(sub));

        var decision = PlayableSourcePolicy.Resolve([main, sub], preferredStream: "sub");
        Assert.Equal("rtsp://admin:@10.0.0.169:554/ch0_1.264", decision.Sub?.Url);
        Assert.Equal("rtsp://admin:@10.0.0.169:554/ch0_1.264", decision.Preferred?.Url);
    }

    [Fact]
    public void BuildProbeOrder_Puts_Main_Sub_Onvif_Http_And_Snapshot_In_Stable_Order()
    {
        var sources = new[]
        {
            Source(TransportKind.LanRest, "http://camera/snapshot", 1, kindMetadata: "snapshot"),
            Source(TransportKind.OnvifRtsp, "rtsp://camera/onvif", 1),
            Source(TransportKind.Rtsp, "rtsp://camera/sub", 1, stream: "sub"),
            Source(TransportKind.Rtsp, "rtsp://camera/main", 1, stream: "main"),
            Source(TransportKind.BubbleFlv, "http://camera/bubble", 1)
        };

        var order = PlayableSourcePolicy.BuildProbeOrder(sources).Select(source => source.Url).ToArray();

        Assert.Equal(
            new[]
            {
                "rtsp://camera/main",
                "rtsp://camera/sub",
                "rtsp://camera/onvif",
                "http://camera/bubble",
                "http://camera/snapshot"
            },
            order);
    }

    private static VideoSourceDescriptor Source(TransportKind transportKind, string url, int rank, string? stream = null, string? kindMetadata = null)
    {
        var metadata = new Dictionary<string, string>();
        if (stream is not null) metadata["stream"] = stream;
        if (kindMetadata is not null) metadata["kind"] = kindMetadata;
        return new VideoSourceDescriptor { Kind = transportKind, Url = url, Rank = rank, Metadata = metadata };
    }
}
