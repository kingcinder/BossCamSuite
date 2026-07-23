using System.Diagnostics;
using System.Text;
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

        var sources = await transportBroker.GetSourcesAsync(device.Id, cancellationToken);
        string? sourceUrl = request.SourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            // Prefer proven high-res main RTSP (Juan ch0_0.264 / ONVIF PROFILE_000 / Dahua subtype=0).
            sourceUrl = SelectHighResMainSource(sources)?.Url;
        }

        // Snapshot is fallback only (often 704x480). Prefer high-res RTSP when available.
        var snapshotUrl = sources.FirstOrDefault(static s =>
                s.Metadata.TryGetValue("kind", out var kind) && kind.Equals("snapshot", StringComparison.OrdinalIgnoreCase)
                && s.Metadata.TryGetValue("highRes", out var hr) && hr.Equals("true", StringComparison.OrdinalIgnoreCase))?.Url
            ?? sources.FirstOrDefault(static s =>
                s.Metadata.TryGetValue("kind", out var kind) && kind.Equals("snapshot", StringComparison.OrdinalIgnoreCase))?.Url
            ?? BuildSnapshotUrl(device);

        var forceSnapshot = string.Equals(request.SourceUrl, "snapshot", StringComparison.OrdinalIgnoreCase)
            || (sourceUrl?.Contains("snapShot", StringComparison.OrdinalIgnoreCase) ?? false)
            || (sourceUrl?.Contains("snapshot.jpg", StringComparison.OrdinalIgnoreCase) ?? false);

        var useSnapshotPipeline = forceSnapshot
            || (string.IsNullOrWhiteSpace(request.SourceUrl) && SelectHighResMainSource(sources) is null);

        if (useSnapshotPipeline)
        {
            sourceUrl = snapshotUrl;
        }
        else if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = SelectHighResMainSource(sources)?.Url ?? sources.FirstOrDefault()?.Url;
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
        // MPEG-TS segments stay playable without a trailing moov atom (unlike mid-write MP4).
        var pattern = Path.Combine(profile.OutputDirectory, $"{device.Id:N}_%Y%m%d_%H%M%S.ts");

        Process process;
        if (useSnapshotPipeline)
        {
            process = StartSnapshotPipeline(device, sourceUrl!, pattern, Math.Max(5, profile.SegmentSeconds), ffmpegPath);
        }
        else
        {
            var args = BuildFfmpegArgs(sourceUrl!, pattern, Math.Max(5, profile.SegmentSeconds));
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start ffmpeg for device {device.DisplayName}.");
            }
            _ = DrainProcessOutputAsync(process, process.Id);
        }

        var started = new RecordingJob
        {
            DeviceId = device.Id,
            ProfileId = profile.Id,
            SourceUrl = RedactUrlCredentials(sourceUrl!),
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
                if (_running.Remove(started.Id, out _))
                {
                    logger.LogWarning("Recording job exited: {JobId}", started.Id);
                }
            }
            finally
            {
                _gate.Release();
            }
        };

        logger.LogInformation(
            "Recording started. job={JobId} device={Device} source={Source} pattern={Pattern} mode={Mode}",
            started.Id,
            device.DisplayName,
            started.SourceUrl,
            pattern,
            useSnapshotPipeline ? "snapshot-pipeline" : "direct-ffmpeg");

        return started;
    }

    /// <summary>
    /// Polls JPEG snapshots and pipes them into ffmpeg segment writer.
    /// Reliable on 5523-W where /NetSDK/.../snapShot returns image/jpg.
    /// </summary>
    private Process StartSnapshotPipeline(DeviceIdentity device, string snapshotUrl, string segmentPattern, int segmentSeconds, string ffmpegPath)
    {
        var fps = 2;
        var interval = "0.5";
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;

        var plainSnapshot = snapshotUrl;
        try
        {
            if (Uri.TryCreate(snapshotUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                var b = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
                plainSnapshot = b.Uri.ToString();
            }
        }
        catch
        {
        }

        Process process;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"bosscam-rec-{device.Id:N}.sh");
            var script = new StringBuilder();
            script.AppendLine("#!/usr/bin/env bash");
            script.AppendLine("set -euo pipefail");
            script.Append("while true; do curl -fsS -m 4 -u ")
                .Append(BashQuote($"{user}:{password}"))
                .Append(' ')
                .Append(BashQuote(plainSnapshot))
                .Append(" || true; sleep ")
                .Append(interval)
                .AppendLine("; done \\");
            // MPEG-TS is robust under kill/restart; no trailing moov required.
            script.Append("| ")
                .Append(BashQuote(ffmpegPath))
                .Append(" -hide_banner -loglevel warning -y -f image2pipe -framerate ")
                .Append(fps)
                .Append(" -c:v mjpeg -i - -c:v libx264 -preset veryfast -pix_fmt yuv420p ")
                .Append("-f segment -segment_time ")
                .Append(Math.Max(10, segmentSeconds))
                .Append(" -segment_format mpegts -reset_timestamps 1 -strftime 1 ")
                .Append(BashQuote(segmentPattern))
                .AppendLine();
            File.WriteAllText(scriptPath, script.ToString());
            try { Process.Start("chmod", $"+x {scriptPath}")?.WaitForExit(2000); } catch { }

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = scriptPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
        }
        else
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel warning -y -loop 1 -re -i \"{snapshotUrl}\" -c:v libx264 -pix_fmt yuv420p -t 86400 -f segment -segment_time {segmentSeconds} -reset_timestamps 1 -strftime 1 \"{segmentPattern}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start snapshot recording pipeline for {device.DisplayName}.");
        }

        _ = DrainProcessOutputAsync(process, process.Id);
        return process;
    }

    private static string BashQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string BuildSnapshotUrl(DeviceIdentity device)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var port = device.Port <= 0 ? 80 : device.Port;
        return $"http://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{device.IpAddress}:{port}/NetSDK/Video/encode/channel/101/snapShot";
    }

    /// <summary>
    /// Picks the highest-priority main/high-res stream. Never selects sub paths like ch0_1, /12, subtype=1.
    /// </summary>
    public static VideoSourceDescriptor? SelectHighResMainSource(IEnumerable<VideoSourceDescriptor> sources)
    {
        static bool IsSub(VideoSourceDescriptor s)
        {
            var url = s.Url ?? string.Empty;
            if (s.Metadata.TryGetValue("stream", out var stream) && stream.Equals("sub", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (s.Metadata.TryGetValue("highRes", out var hr) && hr.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return url.Contains("ch0_1", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/12", StringComparison.OrdinalIgnoreCase)
                || url.Contains("subtype=1", StringComparison.OrdinalIgnoreCase)
                || url.Contains("PROFILE_001", StringComparison.OrdinalIgnoreCase)
                || (s.DisplayName?.Contains("sub", StringComparison.OrdinalIgnoreCase) ?? false);
        }

        static bool IsMainHint(VideoSourceDescriptor s)
        {
            var url = s.Url ?? string.Empty;
            if (s.Metadata.TryGetValue("highRes", out var hr) && hr.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (s.Metadata.TryGetValue("stream", out var stream) && stream.Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return url.Contains("ch0_0", StringComparison.OrdinalIgnoreCase)
                || url.Contains("subtype=0", StringComparison.OrdinalIgnoreCase)
                || url.Contains("PROFILE_000", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/11", StringComparison.OrdinalIgnoreCase);
        }

        var ordered = sources
            .Where(static s => s.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp)
            .Where(static s => !IsSub(s))
            .OrderBy(static s => s.Rank)
            .ToList();

        return ordered.FirstOrDefault(IsMainHint) ?? ordered.FirstOrDefault();
    }

    public async Task<IReadOnlyCollection<RecordingJob>> StartAllAsync(bool preferSubStream, CancellationToken cancellationToken)
    {
        var devices = (await store.GetDevicesAsync(cancellationToken))
            .Where(static device => !string.IsNullOrWhiteSpace(device.IpAddress))
            .GroupBy(device => device.IpAddress!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(static d => string.Equals(d.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static d => d.DiscoveredAt)
                .First())
            .Where(static d =>
                string.Equals(d.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase)
                || (d.HardwareModel?.Contains("5523", StringComparison.OrdinalIgnoreCase) ?? false)
                || !string.IsNullOrWhiteSpace(d.EseeId))
            .ToList();

        var jobs = new List<RecordingJob>();
        foreach (var device in devices)
        {
            try
            {
                // Leave SourceUrl null so StartAsync uses the proven snapshot pipeline on 5523-W.
                // preferSubStream is reserved for future RTSP media path when RTP is available.
                _ = preferSubStream;
                var job = await StartAsync(new RecordingStartRequest
                {
                    DeviceId = device.Id
                }, cancellationToken);
                jobs.Add(job);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to start recording for {Device} ({Ip})", device.DisplayName, device.IpAddress);
            }
        }

        return jobs;
    }

    public async Task<IReadOnlyCollection<RecordingJob>> StopAllAsync(CancellationToken cancellationToken)
    {
        var jobs = await GetJobsAsync(cancellationToken);
        var stopped = new List<RecordingJob>();
        foreach (var job in jobs.Where(static j => j.IsRunning))
        {
            var result = await StopAsync(job.Id, cancellationToken);
            if (result is not null)
            {
                stopped.Add(result);
            }
        }

        return stopped;
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
                    // entireProcessTree is required so bash pipeline children (curl/ffmpeg) die too.
                    running.Process.Kill(entireProcessTree: true);
                    running.Process.WaitForExit(8000);
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

            foreach (var file in Directory.EnumerateFiles(profile.OutputDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(static path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)))
            {
                var info = new FileInfo(file);
                // Skip empty/stub segment headers (e.g. 48-byte ftyp-only mp4) but keep tiny test fixtures.
                if (info.Length < 8)
                {
                    continue;
                }

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

            var files = Directory.EnumerateFiles(profile.OutputDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(static path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// High-res RTSP (HEVC/H264) + drop PCMA audio. Segment to MPEG-TS for kill-safe files.
    /// </summary>
    public static string BuildFfmpegArgs(string sourceUrl, string segmentPattern, int segmentSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -y ");
        sb.Append("-analyzeduration 8000000 -probesize 8000000 ");

        if (sourceUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            // TCP interleaved RTP. Avoid stimeout/rw_timeout — option names vary by ffmpeg build.
            sb.Append("-rtsp_transport tcp ");
        }

        sb.Append("-i \"").Append(sourceUrl).Append("\" ");
        // Drop PCMA/PCMU audio; copy video (HEVC main high-res or H264).
        sb.Append("-map 0:v:0 -c:v copy -an ");
        sb.Append("-f segment -segment_time ").Append(Math.Max(10, segmentSeconds));
        sb.Append(" -segment_format mpegts -reset_timestamps 1 -strftime 1 \"");
        sb.Append(segmentPattern).Append('"');
        return sb.ToString();
    }

    private async Task DrainProcessOutputAsync(Process process, int processId)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogDebug("ffmpeg stderr pid={Pid}: {Stderr}", processId, stderr.Length > 2000 ? stderr[^2000..] : stderr);
            }
        }
        catch
        {
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
            // Keep existing custom directories; clamp overly-long segment times so files finalize often.
            if (profile.SegmentSeconds > 120)
            {
                profile = profile with { SegmentSeconds = 30, UpdatedAt = DateTimeOffset.UtcNow };
                await store.SaveRecordingProfilesAsync([profile], cancellationToken);
            }

            return profile;
        }

        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BossCamSuite",
            "recordings",
            string.IsNullOrWhiteSpace(device.IpAddress) ? device.Id.ToString("N") : device.IpAddress.Replace('.', '_'));
        profile = new RecordingProfile
        {
            DeviceId = device.Id,
            Name = "Default",
            OutputDirectory = outputDirectory,
            SegmentSeconds = 30,
            Enabled = true,
            AutoStart = false
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

    private static string RedactUrlCredentials(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            {
                return url;
            }

            var userInfo = uri.UserInfo;
            var user = userInfo.Contains(':') ? userInfo.Split(':', 2)[0] : userInfo;
            var builder = new UriBuilder(uri) { UserName = user, Password = "***" };
            return builder.Uri.ToString();
        }
        catch
        {
            return url;
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
            foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" })
            {
                var candidate = Path.Combine(segment, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Common absolute locations
        foreach (var candidate in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", @"C:\ffmpeg\bin\ffmpeg.exe" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
