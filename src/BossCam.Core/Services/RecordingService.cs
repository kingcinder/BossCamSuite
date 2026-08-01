using System.Diagnostics;
using System.Text;
using BossCam.Contracts;
using BossCam.Core.Services.Recording;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class RecordingService(
    IApplicationStore store,
    TransportBroker transportBroker,
    IRecordingPipelineResolver pipelines,
    IBossCamEventBroadcaster broadcaster,
    IHttpClientFactory httpClientFactory,
    ILogger<RecordingService> logger)
{
    /// <summary>
    /// Bookkeeping record describing a currently-running recording. Internal-only (exposed
    /// to the test project via InternalsVisibleTo). Held in <see cref="_running"/> keyed
    /// by job id so cleanup paths (StopAsync, process.Exited) can recover the bookkeeping
    /// alongside the process. <see cref="ScriptPath"/> is nullable because the Windows
    /// direct-ffmpeg branch never writes a helper script to /tmp.
    /// </summary>
    internal sealed record RunningRecording(RecordingJob Job, Process Process, string? ScriptPath);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, RunningRecording> _running = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset mtime, long size)> _indexedCache = new(StringComparer.OrdinalIgnoreCase);

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

        var selectedMain = SelectHighResMainSource(sources);
        var forceSnapshot = string.Equals(request.SourceUrl, "snapshot", StringComparison.OrdinalIgnoreCase)
            || (sourceUrl?.Contains("snapShot", StringComparison.OrdinalIgnoreCase) ?? false)
            || (sourceUrl?.Contains("snapshot.jpg", StringComparison.OrdinalIgnoreCase) ?? false);

        var useSnapshotPipeline = forceSnapshot
            || (string.IsNullOrWhiteSpace(request.SourceUrl) && selectedMain is null);

        string? sourceRole = null;
        string? degradedReason = null;

        if (useSnapshotPipeline)
        {
            // Snapshot is fallback only (often 704x480). Prefer a *reachable* snapshot: probe the
            // adapters' snapshot-kind descriptors in rank order (recorded port first, then the
            // :80 fallback they emit) so recording self-heals when the recorded port is dead.
            // Last resort is BuildSnapshotUrl (port normalized via NetSdkPortCandidates.For).
            sourceUrl = await ResolveSnapshotUrlAsync(device, sources, cancellationToken);
            sourceRole = "snapshot";
            degradedReason = selectedMain is null ? "No RTSP main source available — using snapshot pipeline" : "Snapshot forced by request";
        }
        else if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = selectedMain?.Url ?? sources.FirstOrDefault()?.Url;
            sourceRole = "main";
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

        string? scriptPath = null;
        Process process;
        var ctx = new RecordingPipelineContext(device, sourceUrl!, pattern, Math.Max(5, profile.SegmentSeconds), ffmpegPath,
            Log: (msg, ex) => { if (ex is null) logger.LogDebug("{Pipeline} {Msg}", useSnapshotPipeline ? "snapshot" : "direct", msg); else logger.LogDebug(ex, "{Pipeline} {Msg}", useSnapshotPipeline ? "snapshot" : "direct", msg); });
        RecordingHandle handle;
        if (useSnapshotPipeline)
        {
            handle = pipelines.Snapshot.Start(ctx);
        }
        else
        {
            handle = pipelines.DirectFfmpeg.Start(ctx);
        }
        process = handle.Process;
        scriptPath = handle.HelperScriptPath;
        _ = DrainProcessOutputAsync(process, process.Id);

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
            Mode = useSnapshotPipeline ? "snapshot" : "direct",
            SourceRole = sourceRole,
            DegradedReason = degradedReason,
            StartedAt = DateTimeOffset.UtcNow
        };

        var startedEntry = new RunningRecording(started, process, scriptPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _running[started.Id] = startedEntry;
        }
        finally
        {
            _gate.Release();
        }

        WireExitCleanup(process, startedEntry);

        // PR-R1: Persist the recording job
        try
        {
            await store.SaveRecordingJobsAsync([started], cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist recording job {JobId}", started.Id);
        }

        // Push recording started to all connected SPA clients.
        _ = broadcaster.RecordingJobStartedAsync(started, cancellationToken);

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
    /// Returns the spawned process and, on Linux/macOS, the bash script path so the
    /// caller can clean up the helper script after the process exits.
    /// </summary>
    private (Process Process, string? ScriptPath) StartSnapshotPipeline(DeviceIdentity device, string snapshotUrl, string segmentPattern, int segmentSeconds, string ffmpegPath)
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
        catch (Exception ex)
        {
            // Malformed snapshot URL from discovery — fall back to the raw URL (credentials
            // still flow through -u), but trace it so a bad source isn't silently misrouted.
            logger.LogDebug(ex, "Failed to strip credentials from snapshot URL for {Device}", device.DisplayName);
        }

        Process process;
        string? pipelineScriptPath = null;
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
            pipelineScriptPath = scriptPath;

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
            // Roll back the helper script if launch failed so we don't leak it on /tmp.
            if (pipelineScriptPath is not null)
            {
                TryDeleteScript(pipelineScriptPath);
            }
            throw new InvalidOperationException($"Failed to start snapshot recording pipeline for {device.DisplayName}.");
        }

        _ = DrainProcessOutputAsync(process, process.Id);
        return (process, pipelineScriptPath);
    }

    private static string BashQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Last-resort snapshot URL when no snapshot source descriptor exists. The port is derived
    /// through <see cref="NetSdkPortCandidates.For(int)"/> (recorded port, or :80 for a
    /// non-positive/80 recorded port) so the fallback contract stays in one place. This returns a
    /// single URL — the snapshot pipeline is a one-shot curl loop — so it cannot itself express
    /// the :80 fallback candidate; multi-candidate probing is left to the video adapters' rank-26
    /// descriptor and <see cref="TransportFailoverService"/>.
    /// </summary>
    internal static string BuildSnapshotUrl(DeviceIdentity device)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var port = NetSdkPortCandidates.For(device.Port)[0];
        return $"http://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{device.IpAddress}:{port}/NetSDK/Video/encode/channel/101/snapShot";
    }

    /// <summary>
    /// Picks the snapshot URL for the snapshot pipeline: probes the adapters' snapshot-kind
    /// descriptors in ascending rank order (recorded port first, then the :80 fallback they
    /// emit) and returns the first that actually serves a JPEG. Falls back to
    /// <see cref="BuildSnapshotUrl(DeviceIdentity)"/> when no descriptor answers. Internal for
    /// unit tests (InternalsVisibleTo).
    /// </summary>
    internal async Task<string> ResolveSnapshotUrlAsync(
        DeviceIdentity device,
        IReadOnlyCollection<VideoSourceDescriptor> sources,
        CancellationToken cancellationToken)
        => (await NetSdkPortCandidates.FirstReachableSnapshotAsync(httpClientFactory, sources, cancellationToken))?.Url
           ?? BuildSnapshotUrl(device);

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

            var handle = new RecordingHandle(running.Process, running.ScriptPath);
            var startedSnapshot = running.ScriptPath is { Length: > 0 };
            var pipeline = startedSnapshot ? (IRecordingPipeline)pipelines.Snapshot : pipelines.DirectFfmpeg;
            try
            {
                await pipeline.StopAsync(handle, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop recording process {JobId}", jobId);
            }

            _running.Remove(jobId);
            var stopped = running.Job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
            // PR-R1: Persist the stopped job
            try { await store.SaveRecordingJobsAsync([stopped], cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist stopped recording job {JobId}", stopped.Id); }
            // Push recording stopped to all connected SPA clients.
            _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
            return stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDeleteScript(string? scriptPath)
    {
        if (string.IsNullOrEmpty(scriptPath))
        {
            return;
        }

        try
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
        catch
        {
            // best-effort /tmp cleanup; ignore IO failures during shutdown
        }
    }

    /// <summary>
    /// PR-R4: Stall watchdog — check each running job's output directory for recent file growth.
    /// If a segment file hasn't grown within StallTimeoutSeconds, the pipeline is considered stalled.
    /// Returns list of jobs that were found stalled and restarted/stopped.
    /// </summary>
    public async Task<IReadOnlyCollection<RecordingJob>> CheckStalledJobsAsync(int stallTimeoutSeconds, bool autoRestart, CancellationToken cancellationToken)
    {
        if (stallTimeoutSeconds <= 0) return [];

        var now = DateTimeOffset.UtcNow;
        var stalled = new List<RecordingJob>();
        // Snapshot job IDs under the gate; process each outside to avoid deadlock on auto-restart
        List<(Guid JobId, RunningRecording Running)> snapshots;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            snapshots = _running
                .Where(kvp => !kvp.Value.Process.HasExited)
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var (jobId, running) in snapshots)
        {
            var dir = running.Job.OutputDirectory;
            if (!Directory.Exists(dir))
            {
                logger.LogWarning("Stall check: output directory missing for job={JobId} path={Dir}", jobId, dir);
                continue;
            }

            // Find the most recent segment file
            var latest = Directory.EnumerateFiles(dir, "*.*")
                .Where(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null) continue;

            var idle = (now - new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero)).TotalSeconds;
            if (idle < stallTimeoutSeconds) continue;

            logger.LogWarning("Stall detected: job={JobId} device={Device} idle={Idle:F0}s threshold={Threshold}s latest={Latest}",
                jobId, running.Job.DeviceId, idle, stallTimeoutSeconds, latest.FullName);

            // Stop the stalled pipeline (need gate to access _running)
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_running.TryGetValue(jobId, out var current) || current.Process.HasExited)
                    continue;

                var handle = new RecordingHandle(current.Process, current.ScriptPath);
                var pipeline = current.ScriptPath is { Length: > 0 } ? (IRecordingPipeline)pipelines.Snapshot : pipelines.DirectFfmpeg;
                try { await pipeline.StopAsync(handle, cancellationToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to stop stalled job {JobId}", jobId); }

                _running.Remove(jobId);
                var stopped = current.Job with { IsRunning = false, StoppedAt = now, LastError = "Stalled: no segment growth" };
                try { await store.SaveRecordingJobsAsync([stopped], cancellationToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to persist stalled recording job {JobId}", stopped.Id); }
                _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
                stalled.Add(stopped);
            }
            finally
            {
                _gate.Release();
            }

            // PR-R4: Auto-restart once if configured — outside the gate to avoid deadlock
            if (autoRestart)
            {
                try
                {
                    var restarted = await StartAsync(new RecordingStartRequest { DeviceId = running.Job.DeviceId }, cancellationToken);
                    logger.LogInformation("Auto-restarted stalled job: new={NewJobId} device={Device}", restarted.Id, running.Job.DeviceId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Auto-restart failed for stalled job {JobId}", jobId);
                }
            }
        }

        return stalled;
    }

    /// <summary>
    /// PR-R1: Load persisted recording jobs from the store and reconcile their running state.
    /// For jobs marked IsRunning=true, verify the OS process is still alive by recorded PID.
    /// Live processes are re-attached into the in-memory table so StopAsync / the stall
    /// watchdog keep working after a service restart; dead processes are marked stopped and
    /// persisted, with a RecordingJobStopped broadcast.
    /// </summary>
    public async Task<IReadOnlyCollection<RecordingJob>> ReconcilePersistedJobsAsync(CancellationToken cancellationToken)
    {
        var persisted = await store.GetRecordingJobsAsync(null, cancellationToken);
        var reconciled = new List<RecordingJob>();
        foreach (var job in persisted)
        {
            if (!job.IsRunning)
            {
                reconciled.Add(job);
                continue;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Fast path: still attached in memory and the process is alive.
                if (_running.TryGetValue(job.Id, out var running) && !running.Process.HasExited)
                {
                    reconciled.Add(job);
                    continue;
                }

                // Restart path: check the OS process by recorded PID so a still-running
                // ffmpeg / snapshot pipeline is re-attached instead of falsely stopped.
                var liveProcess = TryGetLiveProcess(job);
                if (liveProcess is null)
                {
                    var stopped = job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
                    reconciled.Add(stopped);
                    try { await store.SaveRecordingJobsAsync([stopped], cancellationToken); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist reconciled stopped job {JobId}", job.Id); }
                    _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
                    logger.LogWarning("Persisted job {JobId} marked running but process pid={Pid} is gone — reconciled as stopped", job.Id, job.ProcessId);
                }
                else
                {
                    // Re-attach the live process so stop / stall handling can manage it.
                    var scriptPath = string.Equals(job.Mode, "snapshot", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Path.GetTempPath(), $"bosscam-rec-{job.DeviceId:N}.sh")
                        : null;
                    // PR-R1: The helper script may have been deleted on a prior stop or a
                    // /tmp sweep after restart, but stopping still works — both pipelines
                    // kill the recorded PID's whole process tree, so the ffmpeg/curl
                    // children die with it even when the script file is gone. Warn when
                    // the expected script is missing so operators can spot /tmp cleanup.
                    if (scriptPath is not null && !File.Exists(scriptPath))
                    {
                        logger.LogWarning("Re-attaching snapshot job {JobId} but helper script is missing ({Script}); stop will rely on process-tree kill", job.Id, scriptPath);
                    }

                    var reattachedEntry = new RunningRecording(job, liveProcess, scriptPath);
                    _running[job.Id] = reattachedEntry;
                    WireExitCleanup(liveProcess, reattachedEntry);
                    logger.LogInformation("Persisted job {JobId} re-attached to live process pid={Pid}", job.Id, job.ProcessId);
                    reconciled.Add(job);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        return reconciled;
    }

    /// <summary>
    /// Returns a live <see cref="Process"/> handle for the given job, or null when the PID is
    /// missing, non-positive, the process has already exited, or the PID was recycled onto an
    /// unrelated process (guarded by comparing the process start time against the job's
    /// <c>StartedAt</c> — a surviving recorder can never start after its own job record).
    /// Used by the restart-reconcile path to re-attach surviving recording processes safely.
    /// </summary>
    private static Process? TryGetLiveProcess(RecordingJob job)
    {
        if (job.ProcessId is not int pid || pid <= 0)
        {
            return null;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            // PID-reuse guard: the OS may have recycled the PID onto a completely different
            // process since the recorder died. A real recorder's process always starts before
            // its job record exists, so a start time after StartedAt means we must not adopt it.
            var processStartedAt = process.StartTime.ToUniversalTime();
            if (processStartedAt > job.StartedAt.UtcDateTime)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (ArgumentException)
        {
            return null; // no such process
        }
        catch (InvalidOperationException)
        {
            return null; // process exited between GetProcessById and HasExited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // permission / platform probe failure — treat as not adoptable
        }
    }

    /// <summary>
    /// Wires a spontaneous-exit handler onto a running recorder so the job is removed from the
    /// running table, persisted as stopped, broadcast over SignalR, and its helper script is
    /// cleaned up. Shared by <see cref="StartAsync"/> and the restart-reconcile path so both
    /// in-memory and re-attached jobs get identical cleanup semantics.
    /// <para>
    /// The handler guards on <see cref="RunningRecording"/> reference identity: the
    /// restart-reconcile path can re-register the same job id with a newer entry (e.g. a stale
    /// in-memory entry whose process exited but whose Exited event hadn't dispatched yet, with
    /// the PID since recycled onto a live process). A stale handler firing late must never
    /// remove the newer entry — that would orphan the live recorder and persist "stopped"
    /// while the process keeps running.
    /// </para>
    /// </summary>
    private void WireExitCleanup(Process process, RunningRecording entry)
    {
        process.EnableRaisingEvents = true;
        process.Exited += async (_, _) =>
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                // Reference-equality guard: only remove if this entry is still the one
                // tracked for the job id (see summary above).
                if (_running.TryGetValue(entry.Job.Id, out var current)
                    && ReferenceEquals(current, entry)
                    && _running.Remove(entry.Job.Id, out var removed))
                {
                    var stopped = removed.Job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
                    logger.LogWarning("Recording job exited: {JobId}", entry.Job.Id);
                    // PR-R1: Persist the stopped job
                    try { await store.SaveRecordingJobsAsync([stopped], CancellationToken.None); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist exited recording job {JobId}", stopped.Id); }
                    // Push recording stopped to all connected SPA clients.
                    _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
                    // Clean up the helper script on spontaneous exit too (camera drop, EOF, signal)
                    // so we don't leak /tmp/bosscam-rec-*.sh when nobody ever calls StopAsync.
                    TryDeleteScript(removed.ScriptPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        };
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
            return entry?.Job;
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

    /// <summary>
    /// Fleet continuous-record policy: for every enrolled device flagged <see cref="DeviceIdentity.ContinuousRecord"/>
    /// with no running job, start a continuous job on the best playable source (snapshot pipeline
    /// when no RTSP answers). Runs after <see cref="ReconcilePersistedJobsAsync"/> at startup and on
    /// the worker cycle, so an enrolled camera whose recorder died (crash, reboot, stall, manual
    /// stop) is brought back automatically. The worker cycle is the backoff cadence; per-device
    /// failures are logged (and surfaced through the job's LastError / the record step in
    /// <c>EnrollService</c>) without throwing or blocking other devices. A persisted running job is
    /// the dedup guard, so a surviving/re-attached recorder is never double-started.
    /// </summary>
    public async Task<IReadOnlyCollection<RecordingJob>> ReconcileContinuousAsync(CancellationToken cancellationToken)
    {
        var devices = (await store.GetDevicesAsync(cancellationToken))
            .Where(static device => device.ContinuousRecord)
            .ToList();
        var started = new List<RecordingJob>();
        foreach (var device in devices)
        {
            try
            {
                var running = (await store.GetRecordingJobsAsync(device.Id, cancellationToken))
                    .FirstOrDefault(job => job.IsRunning);
                if (running is not null)
                {
                    continue;
                }

                // Also guard on the in-memory map: a job spawned but not yet persisted (persist
                // step failed) must not be double-started by the next cycle — that would be the
                // runaway-ffmpeg case the persisted-store check alone cannot see.
                var runningInMemory = false;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    runningInMemory = _running.Values.Any(entry => entry.Job.DeviceId == device.Id);
                }
                finally
                {
                    _gate.Release();
                }
                if (runningInMemory)
                {
                    continue;
                }

                var job = await StartAsync(new RecordingStartRequest { DeviceId = device.Id }, cancellationToken);
                started.Add(job);
                logger.LogInformation("Continuous-record policy started job {JobId} for {Device}", job.Id, device.DisplayName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Continuous-record policy start failed for {Device}", device.DisplayName);
            }
        }

        return started;
    }

    /// <summary>
    /// PR-R2: Enhanced segment indexer that populates DurationSec, StreamRole, Container,
    /// HasAudio, and JobId. Uses ffprobe for duration (cached per file via mtime/size key)
    /// and parses strftime patterns from filenames for stream role inference.
    /// Skips files whose mtime+size haven't changed since last index (incremental).
    /// </summary>
    public async Task<IReadOnlyCollection<RecordingSegment>> RefreshIndexAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var profiles = await store.GetRecordingProfilesAsync(deviceId, cancellationToken);
        var ffmpegPath = ResolveFfmpegPath();
        // PR-R2: Class-level cache keyed by file path; stores (mtime, size) so unchanged files are skipped across calls.
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
                if (info.Length < 8)
                {
                    continue;
                }

                // Incremental: skip if mtime+size unchanged
                if (_indexedCache.TryGetValue(file, out var cached)
                    && cached.mtime == new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                    && cached.size == info.Length)
                {
                    continue;
                }
                _indexedCache[file] = (new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);

                if (!TryParseStartTime(info.Name, out var start))
                {
                    start = new DateTimeOffset(info.CreationTimeUtc);
                }

                // Determine container from extension
                var container = info.Extension.TrimStart('.').ToLowerInvariant();
                if (container is "mp4" or "ts" or "mkv")
                {
                    // already set
                }
                else
                {
                    container = "ts";
                }

                // PR-R2: Infer stream role from strftime prefix token (deviceId_YYYYMMDD_HHMMSS)
                var streamRole = InferStreamRoleFromFileName(info.Name, profile);

                // PR-R2: Probe duration via ffprobe (best-effort)
                var duration = await ProbeDurationAsync(file, ffmpegPath, cancellationToken);
                var durationSec = duration ?? Math.Max(5, profile.SegmentSeconds);

                // PR-R2: Determine hasAudio from pipeline mode — direct pipeline has audio, snapshot is video-only
                // Mode is stored in the job; fall back to true for TS (direct) and false for snapshot
                var hasAudio = InferHasAudio(info.Name, profile);

                var end = start.AddSeconds(durationSec);
                segments.Add(new RecordingSegment
                {
                    DeviceId = profile.DeviceId,
                    ProfileId = profile.Id,
                    FilePath = info.FullName,
                    SizeBytes = info.Length,
                    DurationSec = durationSec,
                    StreamRole = streamRole,
                    Container = container,
                    HasAudio = hasAudio,
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

    /// <summary>
    /// PR-R2: Infer stream role from the strftime filename pattern.
    /// The segment pattern is <deviceId>_%Y%m%d_%H%M%S.ts, so we check the deviceId prefix
    /// against known profile device types. Falls back to directory or profile name hints.
    /// </summary>
    private static string InferStreamRoleFromFileName(string fileName, RecordingProfile profile)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        // Segment pattern is deviceId_N_YYYYMMDD_HHMMSS — the role is encoded in the directory structure
        var dirLower = profile.OutputDirectory.ToLowerInvariant();
        if (dirLower.Contains("/sub") || dirLower.Contains("\\sub") || dirLower.EndsWith("_sub")) return "sub";
        if (dirLower.Contains("/snapshot") || dirLower.Contains("\\snapshot") || dirLower.Contains("_snap")) return "snapshot";
        if (dirLower.Contains("/main") || dirLower.Contains("\\main")) return "main";
        return "main";
    }

    /// <summary>
    /// PR-R2: Infer whether a segment file likely contains audio.
    /// Direct FFmpeg pipeline maps audio; snapshot pipeline is video-only.
    /// If the output directory contains "snapshot" or the profile uses snapshot, return false.
    /// </summary>
    private static bool InferHasAudio(string fileName, RecordingProfile profile)
    {
        var dirLower = profile.OutputDirectory.ToLowerInvariant();
        if (dirLower.Contains("snapshot") || dirLower.Contains("_snap")) return false;
        // Default: TS/MP4 direct pipeline segments usually have audio
        return true;
    }

    /// <summary>
    /// PR-R2: Best-effort ffprobe duration probe. Returns null on failure.
    /// Uses a lightweight ffprobe call that reads only format duration.
    /// Tries "ffprobe" on PATH first, then falls back to sibling of ffmpeg binary.
    /// Internal for unit tests (InternalsVisibleTo BossCam.Tests) so the Debug log
    /// on probe failure can be asserted with a captured logger.
    /// </summary>
    internal async Task<double?> ProbeDurationAsync(string filePath, string? ffmpegPath, CancellationToken cancellationToken)
    {
        if (ffmpegPath is null) return null;

        // Try "ffprobe" on PATH first
        var probePath = "ffprobe";
        if (!File.Exists(probePath))
        {
            // Fall back to sibling of ffmpeg binary
            var dir = Path.GetDirectoryName(ffmpegPath);
            var probeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            if (dir is not null) probePath = Path.Combine(dir, probeName);
            if (!File.Exists(probePath)) return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probePath,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0 && double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var duration) && duration > 0)
            {
                return duration;
            }
        }
        catch (Exception ex)
        {
            // ffprobe failed — fall back to segment-seconds estimate. Debug so a missing or
            // misconfigured ffprobe shows up in logs instead of silently degrading every index.
            logger.LogDebug(ex, "ffprobe duration probe failed for {File}", filePath);
        }
        return null;
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

    /// <summary>
    /// PR-R3: Clip export with copy-first optimization. Uses concat demuxer with -c copy for segments
    /// that share compatible codecs. Falls back to re-encode only when timestamps or codecs force it.
    /// Returns result with path, bytes, duration, and whether re-encode was required.
    /// </summary>
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

            // PR-R3: Copy-first — try concat with -c copy; fall back to re-encode if that fails
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
                logger.LogWarning("Copy-first export failed, falling back to re-encode: {Error}", error?.Length > 200 ? error[^200..] : error);

                // Fallback: re-encode with libx264
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-hide_banner -loglevel warning -f concat -safe 0 -i \"{listFile}\" -c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k \"{request.OutputPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    var fallbackError = await process.StandardError.ReadToEndAsync(cancellationToken);
                    return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = $"Concat failed: {error}; Re-encode failed: {fallbackError}" };
                }

                var fi = new FileInfo(request.OutputPath);
                return new ClipExportResult
                {
                    Success = true,
                    OutputPath = request.OutputPath,
                    Bytes = fi.Length,
                    DurationSec = segments.Sum(static s => s.DurationSec > 0 ? s.DurationSec : 30),
                    ReEncoded = true,
                    Message = "Copy-first failed; re-encode fallback was used."
                };
            }

            var fileInfo = new FileInfo(request.OutputPath);
            return new ClipExportResult
            {
                Success = true,
                OutputPath = request.OutputPath,
                Bytes = fileInfo.Length,
                DurationSec = segments.Sum(static s => s.DurationSec > 0 ? s.DurationSec : 30),
                ReEncoded = false,
                Message = $"Copied {segments.Count} segment(s)"
            };
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
        // PR-R7: Map best video + best audio stream when available. Use optional audio
        // (-map 0:a:0?) so the pipeline doesn't fail if no audio track exists.
        sb.Append("-map 0:v:0 -c:v copy ");
        sb.Append("-map 0:a:0? -c:a copy ");
        sb.Append("-f segment -segment_time ").Append(Math.Max(10, segmentSeconds));
        sb.Append(" -segment_format mpegts -reset_timestamps 1 -strftime 1 \"");
        sb.Append(segmentPattern).Append('"');
        return sb.ToString();
    }

    /// <summary>Internal for unit tests (InternalsVisibleTo BossCam.Tests) so the Debug log
    /// on stderr-drain failure can be asserted with a captured logger.</summary>
    internal async Task DrainProcessOutputAsync(Process process, int processId)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogDebug("ffmpeg stderr pid={Pid}: {Stderr}", processId, stderr.Length > 2000 ? stderr[^2000..] : stderr);
            }
        }
        catch (Exception ex)
        {
            // Losing the stderr drain hides ffmpeg's diagnostics channel (auth failures, codec
            // errors). Never take down the recording, but trace the drain failure.
            logger.LogDebug(ex, "Failed to drain ffmpeg stderr pid={Pid}", processId);
        }
    }

    private async Task<RecordingProfile> ResolveProfileAsync(DeviceIdentity device, RecordingStartRequest request, CancellationToken cancellationToken)
    {
        var profiles = await store.GetRecordingProfilesAsync(device.Id, cancellationToken);
        var profile = request.ProfileId is Guid id
            ? profiles.FirstOrDefault(item => item.Id == id)
            : profiles.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        var overrideDir = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? null
            : Path.GetFullPath(request.OutputDirectory.Trim());

        if (profile is not null)
        {
            var next = profile;
            if (next.SegmentSeconds > 120)
            {
                next = next with { SegmentSeconds = 30, UpdatedAt = DateTimeOffset.UtcNow };
            }

            if (overrideDir is not null && !string.Equals(next.OutputDirectory, overrideDir, StringComparison.Ordinal))
            {
                next = next with { OutputDirectory = overrideDir, UpdatedAt = DateTimeOffset.UtcNow };
            }

            if (!ReferenceEquals(next, profile))
            {
                await store.SaveRecordingProfilesAsync([next], cancellationToken);
            }

            return next;
        }

        var outputDirectory = overrideDir ?? Path.Combine(
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

    /// <summary>Internal for unit tests (InternalsVisibleTo BossCam.Tests) so the Debug log
    /// on housekeeping delete failure can be asserted with a captured logger.</summary>
    internal bool TryDelete(FileInfo info, ref int filesDeleted, ref long bytesDeleted)
    {
        try
        {
            var length = info.Length;
            info.Delete();
            filesDeleted++;
            bytesDeleted += length;
            return true;
        }
        catch (Exception ex)
        {
            // A file that can't be deleted (in use, permissions) silently under-delivers on
            // retention policy — Debug so housekeeping shortfalls are traceable.
            logger.LogDebug(ex, "Housekeeping could not delete {File}", info.FullName);
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
