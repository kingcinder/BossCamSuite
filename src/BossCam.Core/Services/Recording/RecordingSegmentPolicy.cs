using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Pure policy for recording-file classification and retention selection. Filesystem access,
/// deletion, indexing, and persistence stay in the recording lifecycle façade; this module owns
/// the decisions that can be tested without processes or SQLite.
/// </summary>
public static class RecordingSegmentPolicy
{
    public static bool IsSupportedSegmentPath(string path)
        => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);

    public static (string StreamRole, bool HasAudio) InferMetadata(string fileName, RecordingProfile profile)
    {
        var directory = profile.OutputDirectory.ToLowerInvariant();
        if (directory.Contains("/snapshot") || directory.Contains("\\snapshot") || directory.Contains("_snap"))
        {
            return ("snapshot", false);
        }

        if (directory.Contains("/sub") || directory.Contains("\\sub") || directory.EndsWith("_sub", StringComparison.Ordinal))
        {
            return ("sub", true);
        }

        return ("main", true);
    }

    public static IReadOnlyCollection<RecordingFileFact> SelectRetentionPurge(
        IEnumerable<RecordingFileFact> files,
        DateTimeOffset now,
        int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return [];
        }

        var cutoff = now.AddDays(-retentionDays);
        return files
            .Where(file => file.CreatedAt < cutoff)
            .OrderBy(file => file.CreatedAt)
            .ToList();
    }

    public static IReadOnlyCollection<RecordingFileFact> SelectStoragePurge(
        IEnumerable<RecordingFileFact> files,
        long maxStorageBytes)
    {
        if (maxStorageBytes <= 0)
        {
            return [];
        }

        var ordered = files.OrderBy(file => file.CreatedAt).ToList();
        var total = ordered.Sum(static file => file.SizeBytes);
        var selected = new List<RecordingFileFact>();
        foreach (var file in ordered)
        {
            if (total <= maxStorageBytes)
            {
                break;
            }

            selected.Add(file);
            total -= file.SizeBytes;
        }

        return selected;
    }

    public static IReadOnlyCollection<RecordingFileFact> MergePurgeSelections(
        IEnumerable<RecordingFileFact> first,
        IEnumerable<RecordingFileFact> second)
        => first.Concat(second)
            .GroupBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static file => file.CreatedAt)
            .ToList();
}

public sealed record RecordingFileFact(string Path, long SizeBytes, DateTimeOffset CreatedAt);
