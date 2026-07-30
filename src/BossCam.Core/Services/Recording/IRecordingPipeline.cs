using System.Diagnostics;
using BossCam.Contracts;

namespace BossCam.Core.Services.Recording;

/// <summary>
/// Unified recording-pipeline abstraction. Replaces the two parallel implementations
/// that used to live inside <c>RecordingService.StartAsync</c> (a bash-script snapshot
/// loop piped to ffmpeg, and a direct-ffmpeg RTSP-to-segment invocation) with one
/// interface so future fixes (escaping, ArgumentList, error handling) only need to
/// happen once.
/// </summary>
public interface IRecordingPipeline
{
    /// <summary>Short identifier used in log messages ("snapshot-pipeline" / "direct-ffmpeg").</summary>
    string Mode { get; }

    /// <summary>
    /// Spawn the recording process for <paramref name="context"/>. Returns a
    /// <see cref="RecordingHandle"/> carrying the started process and, for pipelines
    /// that need it, a helper-script path that the pipeline itself is responsible for
    /// cleaning up on <see cref="StopAsync"/>.
    /// </summary>
    RecordingHandle Start(RecordingPipelineContext context);

    /// <summary>
    /// Stop a recording previously started by <see cref="Start"/>. Kills the entire
    /// process tree, waits briefly, and deletes any helper-script left on disk so /tmp
    /// doesn't accumulate <c>bosscam-rec-*.sh</c> across runs.
    /// </summary>
    Task StopAsync(RecordingHandle handle, CancellationToken cancellationToken);
}

/// <summary>
/// Per-start handle returned by <see cref="IRecordingPipeline.Start"/>. Lives only as
/// long as the running recording job; the pipeline implementation owns any cleanup
/// associated with <see cref="HelperScriptPath"/>.
/// </summary>
public sealed record RecordingHandle(Process Process, string? HelperScriptPath);

/// <summary>
/// Inputs for <see cref="IRecordingPipeline.Start"/>. Bundles the device identity,
/// resolved source URL, output pattern, segment length, ffmpeg path, and a logger
/// so pipeline implementations don't have to chase dependencies separately.
/// </summary>
public sealed record RecordingPipelineContext(
    DeviceIdentity Device,
    string SourceUrl,
    string SegmentPattern,
    int SegmentSeconds,
    string FfmpegPath,
    Action<string, Exception?> Log);
