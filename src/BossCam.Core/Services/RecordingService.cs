using System.Diagnostics;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class RecordingService(
    IApplicationStore store,
    TransportBroker transportBroker,
    ILogger<RecordingService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, (RecordingJob Job, Process Process)> _running = [];

    public async Task<RecordingJob> StartAsync(RecordingStartRequest request, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(request.DeviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");

        var profile = await ResolveProfileAsync(device, request, cancellationToken);
        var existing = await GetRunningForProfileAsync(profile.Id, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var sourceUrl = request.SourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            var sources = (await transportBroker.GetSourcesAsync(device.Id, cancellationToken)).OrderBy(static source => source.Rank).ToList();
            sourceUrl = !string.IsNullOrWhiteSpace(profile.SourceId)
                ? sources.FirstOrDefault(source => source.Id.Equals(profile.SourceId, StringComparison.OrdinalIgnoreCase))?.Url
                : sources.FirstOrDefault(static source => !source.LowResOnly)?.Url ?? sources.FirstOrDefault()?.Url;
        }

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("No video source URL available for recording.");
        }

        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            throw new InvalidOperationException("ffmpeg not found on PATH. Install ffmpeg to enable recording.");
        }

        Directory.CreateDirectory(profile.OutputDirectory);
        var pattern = Path.Combine(profile.OutputDirectory, $"{device.Id:N}_%Y%m%d_%H%M%S.mp4");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };
        AddRecordingArguments(process.StartInfo, sourceUrl, pattern, profile.SegmentSeconds);

        process.Start();

        var started = new RecordingJob
        {
            DeviceId = device.Id,
            ProfileId = profile.Id,
            SourceUrl = sourceUrl,
            OutputDirectory = profile.OutputDirectory,
            SegmentPattern = pattern,
            SegmentSeconds = profile.SegmentSeconds,
            IsRunning = true,
            ProcessId = process.Id,
            StartedAt = DateTimeOffset.UtcNow
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _running[started.Id] = (started, process);
        }
        finally
        {
            _gate.Release();
        }

        process.Exited += async (_, _) =>
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_running.Remove(started.Id, out var running))
                {
                    logger.LogWarning("Recording job exited: {JobId}", started.Id);
                }
            }
            finally
            {
                _gate.Release();
            }
        };

        return started;
    }

    public async Task<RecordingJob?> StopAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_running.TryGetValue(jobId, out var running))
            {
                return null;
            }

            try
            {
                if (!running.Process.HasExited)
                {
                    running.Process.Kill(entireProcessTree: true);
                    running.Process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop recording process {JobId}", jobId);
            }

            _running.Remove(jobId);
            return running.Job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<RecordingJob>> GetJobsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _running.Values.Select(static entry => entry.Job with { IsRunning = !entry.Process.HasExited }).OrderByDescending(static job => job.StartedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordingJob?> GetRunningForProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entry = _running.Values.FirstOrDefault(item => item.Job.ProfileId == profileId && !item.Process.HasExited);
            return entry.Job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<RecordingJob>> ReconcileAutoStartAsync(CancellationToken cancellationToken)
    {
        var profiles = (await store.GetRecordingProfilesAsync(null, cancellationToken))
            .Where(static profile => profile.Enabled && profile.AutoStart)
            .ToList();
        var started = new List<RecordingJob>();
        foreach (var profile in profiles)
        {
            if (await GetRunningForProfileAsync(profile.Id, cancellationToken) is not null)
            {
                continue;
            }

            try
            {
                var job = await StartAsync(new RecordingStartRequest { DeviceId = profile.DeviceId, ProfileId = profile.Id }, cancellationToken);
                started.Add(job);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-start reconcile failed for profile {ProfileId}", profile.Id);
            }
        }

        return started;
    }

    public async Task<IReadOnlyCollection<RecordingSegment>> RefreshIndexAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var profiles = await store.GetRecordingProfilesAsync(deviceId, cancellationToken);
        var segments = new List<RecordingSegment>();
        foreach (var profile in profiles)
        {
            if (!Directory.Exists(profile.OutputDirectory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(profile.OutputDirectory, "*.mp4", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (!TryParseStartTime(info.Name, out var start))
                {
                    start = new DateTimeOffset(info.CreationTimeUtc);
                }

                var end = start.AddSeconds(Math.Max(5, profile.SegmentSeconds));
                segments.Add(new RecordingSegment
                {
                    DeviceId = profile.DeviceId,
                    ProfileId = profile.Id,
                    FilePath = info.FullName,
                    SizeBytes = info.Length,
                    StartTime = start,
                    EndTime = end,
                    IndexedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var deduped = segments
            .GroupBy(static segment => segment.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(segment => segment.IndexedAt).First())
            .ToList();
        await store.SaveRecordingSegmentsAsync(deduped, cancellationToken);
        return deduped;
    }

    public Task<IReadOnlyCollection<RecordingSegment>> GetIndexedSegmentsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
        => store.GetRecordingSegmentsAsync(deviceId, limit, cancellationToken);

    public async Task<RecordingHousekeepingResult> RunHousekeepingAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var profiles = await store.GetRecordingProfilesAsync(deviceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var deletedFiles = 0;
        long deletedBytes = 0;

        foreach (var profile in profiles.Where(static profile => profile.RetentionDays > 0 || profile.MaxStorageBytes > 0))
        {
            if (!Directory.Exists(profile.OutputDirectory))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(profile.OutputDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.CreationTimeUtc)
                .ToList();

            if (profile.RetentionDays > 0)
            {
                var cutoff = now.AddDays(-profile.RetentionDays);
                foreach (var info in files.Where(info => info.CreationTimeUtc < cutoff.UtcDateTime).ToList())
                {
                    TryDelete(info, ref deletedFiles, ref deletedBytes);
                    files.Remove(info);
                }
            }

            if (profile.MaxStorageBytes > 0)
            {
                long total = files.Sum(static info => info.Length);
                foreach (var info in files)
                {
                    if (total <= profile.MaxStorageBytes)
                    {
                        break;
                    }

                    var length = info.Length;
                    if (TryDelete(info, ref deletedFiles, ref deletedBytes))
                    {
                        total -= length;
                    }
                }
            }
        }

        return new RecordingHousekeepingResult
        {
            ProfilesChecked = profiles.Count,
            FilesDeleted = deletedFiles,
            BytesDeleted = deletedBytes
        };
    }

    public async Task<ClipExportResult> ExportClipAsync(ClipExportRequest request, CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = "ffmpeg not found." };
        }

        var segments = (await store.GetRecordingSegmentsAsync(request.DeviceId, 5000, cancellationToken))
            .Where(segment => segment.EndTime >= request.StartTime && segment.StartTime <= request.EndTime)
            .OrderBy(segment => segment.StartTime)
            .ToList();

        if (segments.Count == 0)
        {
            return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = "No indexed segments overlap the requested window." };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        var listFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(listFile, segments.Select(segment => $"file '{segment.FilePath.Replace("'", "''")}'"), cancellationToken);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel warning -f concat -safe 0 -i \"{listFile}\" -c copy \"{request.OutputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = error };
            }

            return new ClipExportResult { Success = true, OutputPath = request.OutputPath };
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    private async Task<RecordingProfile> ResolveProfileAsync(DeviceIdentity device, RecordingStartRequest request, CancellationToken cancellationToken)
    {
        var profiles = await store.GetRecordingProfilesAsync(device.Id, cancellationToken);
        var profile = request.ProfileId is Guid id
            ? profiles.FirstOrDefault(item => item.Id == id)
            : profiles.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        if (profile is not null)
        {
            return profile;
        }

        var outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BossCamSuite", "recordings", device.Id.ToString("N"));
        profile = new RecordingProfile
        {
            DeviceId = device.Id,
            Name = "Default",
            OutputDirectory = outputDirectory,
            SegmentSeconds = 300,
            Enabled = true
        };
        await store.SaveRecordingProfilesAsync([profile], cancellationToken);
        return profile;
    }

    private static bool TryParseStartTime(string fileName, out DateTimeOffset parsed)
    {
        parsed = default;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var token = stem.Split('_').TakeLast(2).ToArray();
        if (token.Length == 2 && DateTime.TryParseExact($"{token[0]}{token[1]}", "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
        {
            parsed = new DateTimeOffset(dt);
            return true;
        }

        return false;
    }

    private static bool TryDelete(FileInfo info, ref int filesDeleted, ref long bytesDeleted)
    {
        try
        {
            var length = info.Length;
            info.Delete();
            filesDeleted++;
            bytesDeleted += length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveFfmpegPath()
    {
        var local = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
        {
            return local;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void AddRecordingArguments(ProcessStartInfo startInfo, string sourceUrl, string pattern, int segmentSeconds)
    {
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        if (sourceUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-rtsp_transport");
            startInfo.ArgumentList.Add("tcp");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourceUrl);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("segment");
        startInfo.ArgumentList.Add("-segment_time");
        startInfo.ArgumentList.Add(Math.Max(5, segmentSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-reset_timestamps");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-strftime");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add(pattern);
    }
}
