using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class RecordingSourceSelectionTests
{
    private static readonly VideoSourceDescriptor MainRtsp = new()
    {
        Id = "fresh-guid-main",
        Kind = TransportKind.Rtsp,
        Url = "rtsp://10.0.0.4:554/ch0_0.264",
        Rank = 1,
        DisplayName = "Main stream",
        StreamRole = "main"
    };

    private static readonly VideoSourceDescriptor SubRtsp = new()
    {
        Id = "fresh-guid-sub",
        Kind = TransportKind.Rtsp,
        Url = "rtsp://10.0.0.4:554/ch0_1.264",
        Rank = 2,
        DisplayName = "Sub stream",
        StreamRole = "sub"
    };

    private static readonly VideoSourceDescriptor Snapshot = new()
    {
        Id = "fresh-guid-snap",
        Kind = TransportKind.LanRest,
        Url = "http://10.0.0.4/snapshot.jpg",
        Rank = 90,
        DisplayName = "Snapshot",
        LowResOnly = true,
        StreamRole = "snapshot"
    };

    [Fact]
    public void Selects_Ranked_Fallback_When_Stored_SourceId_Misses()
    {
        // VideoSourceDescriptor.Id defaults to a fresh GUID per refresh, so a RecordingProfile
        // saved with a previous SourceId usually won't match. The ranked fallback must win
        // instead of throwing "No video source URL available".
        var sources = new[] { MainRtsp, SubRtsp, Snapshot };

        var url = RecordingService.SelectSourceUrl(sources, "stale-source-id-from-previous-refresh");

        Assert.Equal("rtsp://10.0.0.4:554/ch0_0.264", url);
    }

    [Fact]
    public void Uses_Pinned_SourceId_When_It_Matches()
    {
        var sources = new[] { MainRtsp, SubRtsp, Snapshot };

        var url = RecordingService.SelectSourceUrl(sources, "fresh-guid-sub");

        Assert.Equal("rtsp://10.0.0.4:554/ch0_1.264", url);
    }

    [Fact]
    public void Skips_LowRes_Only_Snapshot_By_Default()
    {
        var sources = new[] { Snapshot, MainRtsp };

        var url = RecordingService.SelectSourceUrl(sources, null);

        Assert.Equal("rtsp://10.0.0.4:554/ch0_0.264", url);
    }

    [Fact]
    public void Prefers_Non_Relay_Over_Relay_Required_Even_When_Relay_Ranks_First()
    {
        // Finding 4: the go2rtc relay URL (127.0.0.1:8554) is only brought up by the desktop live
        // path. Recording must not hand the unopened relay URL to ffmpeg when a direct source exists.
        var relay = new VideoSourceDescriptor
        {
            Id = "relay-guid",
            Kind = TransportKind.Rtsp,
            Url = "rtsp://127.0.0.1:8554/5523w_main",
            Rank = 0,
            DisplayName = "Main go2rtc bubble relay",
            Metadata = new Dictionary<string, string> { ["relayRequired"] = "go2rtc" }
        };
        var sources = new[] { relay, MainRtsp };

        var url = RecordingService.SelectSourceUrl(sources, null);

        Assert.Equal("rtsp://10.0.0.4:554/ch0_0.264", url);
    }

    [Fact]
    public void Falls_Back_To_Relay_When_It_Is_The_Only_Non_LowRes_Source()
    {
        var relay = new VideoSourceDescriptor
        {
            Id = "relay-guid",
            Kind = TransportKind.Rtsp,
            Url = "rtsp://127.0.0.1:8554/5523w_main",
            Rank = 0,
            DisplayName = "Main go2rtc bubble relay",
            Metadata = new Dictionary<string, string> { ["relayRequired"] = "go2rtc" }
        };

        var url = RecordingService.SelectSourceUrl([relay], null);

        Assert.Equal("rtsp://127.0.0.1:8554/5523w_main", url);
    }

    [Fact]
    public void Returns_Null_When_No_Source_Is_Available()
    {
        var url = RecordingService.SelectSourceUrl([], null);

        Assert.Null(url);
    }
}
