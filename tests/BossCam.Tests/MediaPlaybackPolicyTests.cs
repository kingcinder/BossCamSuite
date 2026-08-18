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
    public void Late_Fmp4_Subscriber_Replay_Includes_Initialization_Boxes_Before_First_Moof()
    {
        var bytes = Convert.FromHexString(
            "000000146674797069736F6D000000006D7034310000000C6D6F6F7600000000000000106D6F6F660000000000000000000000086D646174");

        var initialization = LiveStreamService.TryExtractFmp4InitializationSegment(bytes);

        Assert.NotNull(initialization);
        Assert.Equal(
            "000000146674797069736f6d000000006d7034310000000c6d6f6f7600000000",
            Convert.ToHexString(initialization!).ToLowerInvariant());
    }

    [Fact]
    public void All_Shared_Rtsp_Outputs_Keep_The_Normal_Reorder_Queue()
    {
        var mjpeg = LiveStreamService.BuildRtspMjpegArguments("rtsp://admin:@10.0.0.169:554/ch0_1.264", false);
        var h264Fmp4 = LiveStreamService.BuildRtspH264Fmp4Arguments("rtsp://admin:@10.0.0.169:554/ch0_1.264", false);
        var hevcFmp4 = LiveStreamService.BuildRtspFmp4Arguments("rtsp://admin:@10.0.0.169:554/ch0_1.264");
        var h264Ts = LiveStreamService.BuildRtspH264TsArguments("rtsp://admin:@10.0.0.169:554/ch0_1.264", false);

        Assert.All(new[] { mjpeg, h264Fmp4, hevcFmp4, h264Ts }, args =>
        {
            Assert.DoesNotContain("-reorder_queue_size", args);
            Assert.DoesNotContain("low_delay", args);
            var argList = args.ToList();
            Assert.Contains("discardcorrupt", argList[argList.IndexOf("-fflags") + 1]);
        });
    }

    [Fact]
    public void Ffmpeg_Mjpeg_Command_Passes_Each_Value_As_An_Argument()
    {
        var args = LiveStreamService.BuildRtspMjpegArguments(
            "rtsp://admin:p%40ss@10.0.0.169:554/ch0_1.264",
            isMain: false);

        Assert.Contains("-rtsp_transport", args);
        Assert.Contains("tcp", args);
        // Ordered TCP is required for the camera's HEVC reference frames. The previous
        // nobuffer/reorder-queue-zero combination caused POC errors and collapsed the
        // source below 15 fps, so the shared server path must preserve the normal queue.
        // discardcorrupt is an fflags value, not a standalone ffmpeg option.
        var argList = args.ToList();
        Assert.DoesNotContain("nobuffer", argList[argList.IndexOf("-fflags") + 1]);
        Assert.DoesNotContain("-reorder_queue_size", args);
        Assert.DoesNotContain("low_delay", args);
        Assert.Contains("discardcorrupt", argList[argList.IndexOf("-fflags") + 1]);
        Assert.DoesNotContain("-discardcorrupt", args);
        Assert.Contains("rtsp://admin:p%40ss@10.0.0.169:554/ch0_1.264", args);
        Assert.Equal("-f", args[^3]);
        Assert.Equal("mpjpeg", args[^2]);
        Assert.Equal("-", args[^1]);
    }
}
