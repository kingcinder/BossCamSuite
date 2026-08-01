using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class MediaPlaybackPolicyTests
{
    [Fact]
    public void Juan5523_MainStream_Offers_Hevc_DirectPlay_Then_H264_Then_Mjpeg_Then_Snapshot()
    {
        var decision = LiveMediaNegotiationPolicy.Resolve(
            new LiveMediaSourceFacts
            {
                IsRtspPlayable = true,
                MainCodec = "hevc",
                SnapshotAvailable = true
            },
            browserSupportsHevc: true);

        Assert.Equal(LiveMediaMode.HevcFmp4, decision.PreferredMode);
        Assert.Equal(
            [LiveMediaMode.HevcFmp4, LiveMediaMode.H264Fmp4, LiveMediaMode.H264MpegTs, LiveMediaMode.Mjpeg, LiveMediaMode.Snapshot],
            decision.FallbackModes);
    }

    [Fact]
    public void Juan5523_MainStream_Uses_H264_When_Browser_Cannot_Decode_Hevc()
    {
        var decision = LiveMediaNegotiationPolicy.Resolve(
            new LiveMediaSourceFacts
            {
                IsRtspPlayable = true,
                MainCodec = "hevc",
                SnapshotAvailable = true
            },
            browserSupportsHevc: false);

        Assert.Equal(LiveMediaMode.H264Fmp4, decision.PreferredMode);
        Assert.DoesNotContain(LiveMediaMode.HevcFmp4, decision.FallbackModes);
        Assert.Contains(LiveMediaMode.H264MpegTs, decision.FallbackModes);
    }

    [Fact]
    public void Dead_Rtsp_Uses_Mjpeg_Then_Snapshot_Without_Claiming_Direct_Play()
    {
        var decision = LiveMediaNegotiationPolicy.Resolve(
            new LiveMediaSourceFacts
            {
                IsRtspPlayable = false,
                MainCodec = "hevc",
                SnapshotAvailable = true
            },
            browserSupportsHevc: true);

        Assert.Equal(LiveMediaMode.Mjpeg, decision.PreferredMode);
        Assert.DoesNotContain(LiveMediaMode.HevcFmp4, decision.FallbackModes);
        Assert.DoesNotContain(LiveMediaMode.H264Fmp4, decision.FallbackModes);
        Assert.Equal(LiveMediaMode.Snapshot, decision.FallbackModes[^1]);
    }

    [Fact]
    public void Manifest_Source_Selection_Uses_Sub_Source_For_Sub_Quality()
    {
        var main = new VideoSourceDescriptor
        {
            Kind = TransportKind.Rtsp,
            Url = "rtsp://10.0.0.169/ch0_0.264",
            Metadata = new Dictionary<string, string> { ["stream"] = "main", ["codec"] = "hevc" }
        };
        var sub = new VideoSourceDescriptor
        {
            Kind = TransportKind.Rtsp,
            Url = "rtsp://10.0.0.169/ch0_1.264",
            Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["codec"] = "h264" }
        };
        var decision = PlayableSourcePolicy.Resolve([main, sub], "sub");

        var selected = LiveStreamService.SelectManifestSource(decision, "sub");

        Assert.Same(sub, selected);
    }

    [Fact]
    public void Juan5523_Source_Metadata_Identifies_Both_Streams_As_Hevc()
    {
        var main = new VideoSourceDescriptor
        {
            Kind = TransportKind.Rtsp,
            Url = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            DisplayName = "Juan main HEVC (ch0_0.264)",
            Metadata = new Dictionary<string, string> { ["stream"] = "main", ["codec"] = "hevc" }
        };
        var sub = new VideoSourceDescriptor
        {
            Kind = TransportKind.Rtsp,
            Url = "rtsp://admin:@10.0.0.169:554/ch0_1.264",
            DisplayName = "Juan sub HEVC (ch0_1.264)",
            Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["codec"] = "hevc" }
        };

        Assert.Equal("hevc", main.Metadata["codec"]);
        Assert.Equal("hevc", sub.Metadata["codec"]);
        Assert.Equal("hevc", PlayableSourcePolicy.Resolve([main, sub], "sub").Sub?.Metadata["codec"]);
    }

    [Fact]
    public void Ffmpeg_Mjpeg_Command_Passes_Each_Value_As_An_Argument()
    {
        var args = LiveStreamService.BuildRtspMjpegArguments(
            "rtsp://admin:p%40ss@10.0.0.169:554/ch0_1.264",
            isMain: false);

        Assert.Contains("-rtsp_transport", args);
        Assert.Contains("tcp", args);
        Assert.Contains("rtsp://admin:p%40ss@10.0.0.169:554/ch0_1.264", args);
        Assert.Equal("-f", args[^3]);
        Assert.Equal("mpjpeg", args[^2]);
        Assert.Equal("-", args[^1]);
    }
}
