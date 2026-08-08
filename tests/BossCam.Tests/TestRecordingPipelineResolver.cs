using BossCam.Core;
using BossCam.Core.Services.Recording;

namespace BossCam.Tests;

/// <summary>
/// DI-less <see cref="IRecordingPipelineResolver"/> for unit tests that construct
/// <see cref="RecordingService"/> directly with positional ctor arguments. The pipelines
/// themselves have no DB / IO dependencies so the default ctor path is sufficient and no
/// shared state needs to be threaded through.
/// </summary>
internal sealed class TestRecordingPipelineResolver : IRecordingPipelineResolver
{
    public SnapshotRecordingPipeline Snapshot { get; } = new();
    public DirectFfmpegRecordingPipeline DirectFfmpeg { get; } = new();
    public BubbleFlvRecordingPipeline BubbleFlv { get; } = new();
}
