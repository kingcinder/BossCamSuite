using System.Diagnostics;
using BossCam.Contracts;
using BossCam.Core;
using Xunit;

namespace BossCam.Tests;

/// <summary>
/// Locks down the structural shape of <see cref="RecordingService.RunningRecording"/>.
/// The runtime dictionary (<c>_running</c>) keys on Guid and the Stop/Exited cleanup
/// paths consume the record positionally — a 4th field bolted on later without
/// widening these tests risks silent behavior drift. Equality on positional records
/// is field-by-field, so a swap from <c>string</c> → <c>string?</c> on
/// <c>ScriptPath</c> must still compare equal when both sides are null.
/// </summary>
public class RunningRecordingEqualityTests
{
    private static RecordingJob BuildJob(Guid id)
        => new RecordingJob
        {
            Id = id,
            DeviceId = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            SourceUrl = "rtsp://example/cam",
            OutputDirectory = "/tmp/recs",
            SegmentPattern = "%Y%m%d.ts",
            SegmentSeconds = 30,
            IsRunning = true,
            ProcessId = 4242,
            StartedAt = DateTimeOffset.UtcNow
        };

    private static Process StartPlaceholderProcess()
    {
        // Process itself isn't part of equality (override on reference equality only),
        // but the type signature requires a non-null Process.
        return new Process { StartInfo = new ProcessStartInfo("/bin/true") };
    }

    [Fact]
    public void Same_Job_Process_And_ScriptPath_Compare_Equal()
    {
        var job = BuildJob(Guid.NewGuid());
        using var proc = StartPlaceholderProcess();
        var path = "/tmp/bosscam-rec-aaa.sh";

        var left = new RecordingService.RunningRecording(job, proc, path);
        var right = new RecordingService.RunningRecording(job, proc, path);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Different_ScriptPath_Does_Not_Compare_Equal()
    {
        var job = BuildJob(Guid.NewGuid());
        using var proc = StartPlaceholderProcess();

        var left = new RecordingService.RunningRecording(job, proc, "/tmp/one.sh");
        var right = new RecordingService.RunningRecording(job, proc, "/tmp/two.sh");

        Assert.NotEqual(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    [Fact]
    public void Both_ScriptPaths_Null_Compare_Equal()
    {
        // Direct-ffmpeg recordings never write /tmp/bosscam-rec-*.sh — the Linux
        // pod's StartSnapshotPipeline still does. Equality must hold across both.
        var job = BuildJob(Guid.NewGuid());
        using var proc = StartPlaceholderProcess();

        var left = new RecordingService.RunningRecording(job, proc, ScriptPath: null);
        var right = new RecordingService.RunningRecording(job, proc, ScriptPath: null);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Null_And_Populated_ScriptPath_Do_Not_Compare_Equal()
    {
        var job = BuildJob(Guid.NewGuid());
        using var proc = StartPlaceholderProcess();

        var left = new RecordingService.RunningRecording(job, proc, ScriptPath: null);
        var right = new RecordingService.RunningRecording(job, proc, "/tmp/bosscam-rec-bbb.sh");

        Assert.NotEqual(left, right);
        Assert.False(left == right);
    }

    [Fact]
    public void Different_Job_Does_Not_Compare_Equal()
    {
        using var proc = StartPlaceholderProcess();
        var path = "/tmp/bosscam-rec-ccc.sh";

        var left = new RecordingService.RunningRecording(BuildJob(Guid.NewGuid()), proc, path);
        var right = new RecordingService.RunningRecording(BuildJob(Guid.NewGuid()), proc, path);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Deconstruction_Preserves_All_Three_Positions()
    {
        var job = BuildJob(Guid.NewGuid());
        using var proc = StartPlaceholderProcess();
        var path = "/tmp/bosscam-rec-ddd.sh";

        var (j, p, s) = new RecordingService.RunningRecording(job, proc, path);

        Assert.Same(job, j);
        Assert.Same(proc, p);
        Assert.Equal(path, s);
    }
}
