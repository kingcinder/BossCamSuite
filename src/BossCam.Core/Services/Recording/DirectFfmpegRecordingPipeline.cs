using System.Diagnostics;
using System.Text;
using BossCam.Core.Utilities;

namespace BossCam.Core.Services.Recording;

/// <summary>
/// Direct-ffmpeg pipeline. RTSP (TCP interleaved RTP) → copy video → MPEG-TS segments.
/// Used when the camera exposes a working RTSP feed (which the 5523-W does not on the
/// BossCam owner's LAN — that's why <see cref="SnapshotRecordingPipeline"/> is the
/// default, but this path is first-class for any future camera or transport).
///
/// All arguments are passed via <see cref="ProcessLauncher.Build"/> so they go through
/// <c>ProcessStartInfo.ArgumentList</c> one-per-element. This closes the
/// argument-injection vector exposed by the original string-interpolated
/// <c>BuildFfmpegArgs</c>; the URL no longer needs shell-style quoting inside the
/// ffmpeg command line at all.
/// </summary>
public sealed class DirectFfmpegRecordingPipeline : IRecordingPipeline
{
    public string Mode => "direct-ffmpeg";

    public RecordingHandle Start(RecordingPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceUrl = context.SourceUrl;
        var segmentPattern = context.SegmentPattern;
        var segmentSeconds = Math.Max(10, context.SegmentSeconds);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-analyzeduration", "8000000", "-probesize", "8000000"
        };
        if (sourceUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            // TCP interleaved RTP. Avoid stimeout/rw_timeout — option names vary by ffmpeg build.
            args.Add("-rtsp_transport");
            args.Add("tcp");
        }
        args.Add("-i");
        args.Add(sourceUrl);
        // Map the best video stream (copy) plus the best audio stream when the source has one.
        // PR-R7: use an optional audio map so video-only sources still record without failing.
        // Audio is transcoded to AAC (not copied) because these cameras emit G.711 a-law,
        // which ffmpeg's MPEG-TS muxer writes as an unlabeled private data stream
        // (stream_type 6 / bin_data) — present in the file but not a decodable audio track.
        // AAC is natively muxable into TS, so recordings carry real playable audio.
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-c:v");
        args.Add("copy");
        args.Add("-map");
        args.Add("0:a:0?");
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("128k");
        args.Add("-f");
        args.Add("segment");
        args.Add("-segment_time");
        args.Add(segmentSeconds.ToString());
        args.Add("-segment_format");
        args.Add("mpegts");
        args.Add("-reset_timestamps");
        args.Add("1");
        args.Add("-strftime");
        args.Add("1");
        args.Add(segmentPattern);

        var info = ProcessLauncher.Build(context.FfmpegPath, args);
        var process = ProcessLauncher.Start(info);
        return new RecordingHandle(process, HelperScriptPath: null);
    }

    public Task StopAsync(RecordingHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            if (!handle.Process.HasExited)
            {
                handle.Process.Kill(entireProcessTree: true);
                handle.Process.WaitForExit(8000);
            }
        }
        catch
        {
            // best-effort stop
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pure helper retained so external callers (tests, the legacy
    /// <c>RecordingService.BuildFfmpegArgs</c>-based test in <c>UbuntuPlatformAndStaticUiTests</c>)
    /// can still inspect the argv that this pipeline would produce. Not used at runtime.
    /// </summary>
    public static IReadOnlyList<string> BuildFfmpegArgs(string sourceUrl, string segmentPattern, int segmentSeconds)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-analyzeduration", "8000000", "-probesize", "8000000"
        };
        if (sourceUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-rtsp_transport");
            args.Add("tcp");
        }
        args.Add("-i");
        args.Add(sourceUrl);
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-c:v");
        args.Add("copy");
        // PR-R7: match the runtime Start() path — map best audio stream when present
        // (optional map so video-only sources don't fail). Audio transcoded to AAC so the
        // G.711 a-law source becomes a real decodable TS audio track (copy would mux it
        // as bin_data private-stream garbage).
        args.Add("-map");
        args.Add("0:a:0?");
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("128k");
        args.Add("-f");
        args.Add("segment");
        args.Add("-segment_time");
        args.Add(Math.Max(10, segmentSeconds).ToString());
        args.Add("-segment_format");
        args.Add("mpegts");
        args.Add("-reset_timestamps");
        args.Add("1");
        args.Add("-strftime");
        args.Add("1");
        args.Add(segmentPattern);
        return args;
    }

    /// <summary>
    /// Same argv as <see cref="BuildFfmpegArgs"/>, but formatted as a single quoted
    /// string for human-readable diagnostics. Not used to invoke ffmpeg — the
    /// production path always uses ArgumentList via <see cref="ProcessLauncher.Build"/>.
    /// </summary>
    public static string BuildFfmpegArgsString(string sourceUrl, string segmentPattern, int segmentSeconds)
        => string.Join(' ', BuildFfmpegArgs(sourceUrl, segmentPattern, segmentSeconds).Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string argument)
        => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
