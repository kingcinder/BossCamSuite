using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Tests;

public sealed class RecordingSegmentPolicyTests
{
    [Theory]
    [InlineData("camera.ts", true)]
    [InlineData("camera.MP4", true)]
    [InlineData("camera.mkv", true)]
    [InlineData("camera.jpg", false)]
    [InlineData("camera", false)]
    public void IsSupportedSegmentPath_Recognizes_Recording_Containers(string filePath, bool expected)
    {
        Assert.Equal(expected, RecordingSegmentPolicy.IsSupportedSegmentPath(filePath));
    }

    [Fact]
    public void InferMetadata_Uses_Output_Directory_To_Preserve_Stream_Truth()
    {
        var main = RecordingSegmentPolicy.InferMetadata("camera.ts", new RecordingProfile { OutputDirectory = "/var/recordings/main" });
        var sub = RecordingSegmentPolicy.InferMetadata("camera.ts", new RecordingProfile { OutputDirectory = "/var/recordings/sub" });
        var snapshot = RecordingSegmentPolicy.InferMetadata("camera.ts", new RecordingProfile { OutputDirectory = "/var/recordings/snapshot" });

        Assert.Equal(("main", true), main);
        Assert.Equal(("sub", true), sub);
        Assert.Equal(("snapshot", false), snapshot);
    }

    [Fact]
    public void Retention_Selects_Oldest_Files_Only_Until_Storage_Budget_Is_Met()
    {
        var now = DateTimeOffset.UtcNow;
        var files = new[]
        {
            new RecordingFileFact("old.ts", 40, now.AddMinutes(-30)),
            new RecordingFileFact("middle.ts", 35, now.AddMinutes(-20)),
            new RecordingFileFact("new.ts", 25, now.AddMinutes(-5))
        };

        var selected = RecordingSegmentPolicy.SelectStoragePurge(files, maxStorageBytes: 60);

        Assert.Equal(new[] { "old.ts" }, selected.Select(file => file.Path));
    }

    [Fact]
    public void Retention_Combines_Age_And_Budget_Without_Duplicating_Files()
    {
        var now = DateTimeOffset.UtcNow;
        var files = new[]
        {
            new RecordingFileFact("expired.ts", 100, now.AddDays(-10)),
            new RecordingFileFact("recent.ts", 80, now.AddMinutes(-2))
        };

        var expired = RecordingSegmentPolicy.SelectRetentionPurge(files, now, retentionDays: 1);
        var budget = RecordingSegmentPolicy.SelectStoragePurge(files, maxStorageBytes: 50);
        var combined = RecordingSegmentPolicy.MergePurgeSelections(expired, budget);

        Assert.Equal(new[] { "expired.ts", "recent.ts" }, combined.Select(file => file.Path));
    }
}
