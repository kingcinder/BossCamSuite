using System.Diagnostics;
using System.Text;
using BossCam.Contracts;
using BossCam.Core.Services.Recording;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Core;

public sealed class RecordingService(
    IApplicationStore store,
    TransportBroker transportBroker,
    IRecordingPipelineResolver pipelines,
    IBossCamEventBroadcaster broadcaster,
    IHttpClientFactory httpClientFactory,
    ILogger<RecordingService> logger,
    IRecordingStore recordingStore,
    RecordingProcessSupervisor processSupervisor,
    IOptions<BossCamRuntimeOptions>? options = null)
{
    private readonly IRecordingStore _recordingStore = recordingStore;

    /// <summary>Captured runtime options used by the clip-export path allow-list. Null-tolerant
    /// so existing test call sites (which never configured export roots before) keep compiling;
    /// production DI always supplies the real <see cref="BossCamRuntimeOptions"/>.</summary>
    private readonly BossCamRuntimeOptions _runtimeOptions = options?.Value ?? new BossCamRuntimeOptions();

    /// <summary>
    /// Bookkeeping record describing a currently-running recording. Internal-only (exposed
    /// to the test project via InternalsVisibleTo). Held in the process supervisor keyed
    /// by job id so cleanup paths (StopAsync, process.Exited) can recover the bookkeeping
    /// alongside the process. <see cref="ScriptPath"/> is nullable because the Windows
    /// direct-ffmpeg branch never writes a helper script to /tmp.
    /// </summary>
    internal sealed record RunningRecording(RecordingJob Job, Process Process, string? ScriptPath);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RecordingProcessSupervisor _processSupervisor = processSupervisor;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset mtime, long size)> _indexedCache = new(StringComparer.OrdinalIgnoreCase);
    // Per-device consecutive stall-restart counter. A source that never produces media (dead
    // encoder / silent RTSP) must not spawn ffmpeg forever: after RecordingMaxConsecutiveRestarts
    // consecutive stalled restarts the fast auto-restart is suspended and the job is marked with
    // a clear error. Reset to zero whenever a stall check observes fresh segment growth (the
    // source demonstrably recovered) or when the cap suspends the auto-restart. NOTE: StartAsync
    // intentionally does NOT clear it — the auto-restart path itself calls StartAsync right after
    // incrementing, so a clear there would erase the very debt the cap is meant to accumulate.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _consecutiveStallRestarts = new();

    /// <summary>
    /// Last exit-rapid-restart timestamp per device. A continuous-record device whose
    /// recorder exits spontaneously while the source was still producing fresh segments
    /// (e.g. a 5523-W whose RTSP session the camera drops every few minutes) is re-picked
    /// after <see cref="BossCamRuntimeOptions.RecordingExitRestartDelaySeconds"/> instead
    /// of waiting for the slow, backed-off continuous-record policy. The timestamp doubles
    /// as a cooldown so a flapping camera cannot spawn a tight ffmpeg loop.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _lastExitRestart = new();

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

        // An explicit snapshot request is already a complete source-selection decision. Avoid
        // probing every transport adapter (and the failover chain) for an unreachable camera;
        // ResolveSnapshotUrlAsync will probe only descriptors when discovery was actually needed,
        // then fall back to the deterministic NetSDK snapshot URL.
        var explicitSnapshot = string.Equals(request.SourceUrl, "snapshot", StringComparison.OrdinalIgnoreCase);
        var sources = explicitSnapshot
            ? Array.Empty<VideoSourceDescriptor>()
            : await transportBroker.GetSourcesAsync(device.Id, cancellationToken);
        var selectedMain = SelectHighResMainSource(sources);
        var rtspProbeFailed = false;
        string? sourceUrl = request.SourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            // Prefer proven high-res main RTSP (Juan ch0_0.264 / ONVIF PROFILE_000 / Dahua subtype=0).
            sourceUrl = selectedMain?.Url;

            // When the caller didn't force a specific source and the best pick is RTSP, probe
            // it first. Dead RTSP should fall through to the snapshot pipeline instead of
            // starting ffmpeg against an unreachable port and exiting instantly.
            if (!string.IsNullOrWhiteSpace(sourceUrl)
                && sourceUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var rtspUri))
            {
                var rtspHost = rtspUri.Host;
                var rtspPort = rtspUri.Port > 0 ? rtspUri.Port : 554;
                if (!await RtspProbe.ProbeAsync(rtspHost, rtspPort, cancellationToken, TimeSpan.FromSeconds(2)))
                {
                    logger.LogWarning("Main RTSP source unreachable on {Host}:{Port}; falling back to snapshot pipeline", rtspHost, rtspPort);
                    sourceUrl = null;
                    rtspProbeFailed = true;
                }
            }
        }
        var forceSnapshot = string.Equals(request.SourceUrl, "snapshot", StringComparison.OrdinalIgnoreCase)
            || (sourceUrl?.Contains("snapShot", StringComparison.OrdinalIgnoreCase) ?? false)
            || (sourceUrl?.Contains("snapshot.jpg", StringComparison.OrdinalIgnoreCase) ?? false);

        var useSnapshotPipeline = forceSnapshot
            || (string.IsNullOrWhiteSpace(request.SourceUrl) && (selectedMain is null || string.IsNullOrWhiteSpace(sourceUrl)));

        string? sourceRole = null;
        string? degradedReason = null;

        if (useSnapshotPipeline)
        {
            // Snapshot is fallback only (often 704x480). Prefer a *reachable* snapshot: probe the
            // adapters' snapshot-kind descriptors in rank order (recorded port first, then the
            // :80 fallback they emit) so recording self-heals when the recorded port is dead.
            // Last resort is BuildSnapshotUrl (port normalized via NetSdkPortCandidates.For).
            sourceUrl = await ResolveSnapshotUrlAsync(
                device,
                explicitSnapshot ? BuildSnapshotCandidates(device) : sources,
                cancellationToken);
            sourceRole = "snapshot";
            degradedReason = selectedMain is null
                ? "No RTSP main source available — using snapshot pipeline"
                : rtspProbeFailed
                    ? "Main RTSP unreachable — using snapshot pipeline"
                    : "Snapshot forced by request";
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

        var isBubble = !useSnapshotPipeline && sourceUrl.Contains("/bubble/live", StringComparison.OrdinalIgnoreCase);
        var pipelineMode = useSnapshotPipeline ? "snapshot" : isBubble ? "bubble-flv" : "direct";

        string? scriptPath = null;
        Process process;
        var ctx = new RecordingPipelineContext(device, sourceUrl!, pattern, Math.Max(5, profile.SegmentSeconds), ffmpegPath,
            Log: (msg, ex) => { if (ex is null) logger.LogDebug("{Pipeline} {Msg}", pipelineMode, msg); else logger.LogDebug(ex, "{Pipeline} {Msg}", pipelineMode, msg); });
        RecordingHandle handle;
        if (useSnapshotPipeline)
        {
            handle = pipelines.Snapshot.Start(ctx);
        }
        else if (isBubble)
        {
            handle = pipelines.BubbleFlv.Start(ctx);
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
            Mode = pipelineMode,
            SourceRole = sourceRole,
            DegradedReason = degradedReason,
            StartedAt = DateTimeOffset.UtcNow
        };

        var startedEntry = new RunningRecording(started, process, scriptPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _processSupervisor.Add(startedEntry);
        }
        finally
        {
            _gate.Release();
        }

        WireExitCleanup(process, startedEntry);

        // PR-R1: Persist the recording job
        try
        {
            await _recordingStore.SaveRecordingJobsAsync([started], cancellationToken);
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
            started.Mode);

        return started;
    }

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
    {
        // Explicit snapshot requests bypass the expensive adapter/failover discovery above, but
        // still retain the recorded-port → :80 fallback contract by synthesizing the same two
        // descriptors the adapters would have emitted for this device.
        var candidates = sources.Count > 0 ? sources : BuildSnapshotCandidates(device);
        return (await NetSdkPortCandidates.FirstReachableSnapshotAsync(httpClientFactory, candidates, cancellationToken))?.Url
            ?? BuildSnapshotUrl(device);
    }

    private static IReadOnlyCollection<VideoSourceDescriptor> BuildSnapshotCandidates(DeviceIdentity device)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        return NetSdkPortCandidates.For(device.Port)
            .Select((port, index) => new VideoSourceDescriptor
            {
                Kind = TransportKind.LanRest,
                Url = $"http://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{device.IpAddress}:{port}/NetSDK/Video/encode/channel/101/snapShot",
                Rank = 25 + index,
                DisplayName = index == 0 ? "JPEG snapshot (NetSDK)" : "JPEG snapshot (:80 fallback)",
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = "snapshot",
                    ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            })
            .ToList();
    }

    /// <summary>
    /// Picks the highest-priority main/high-res stream. Never selects sub paths like ch0_1, /12, subtype=1.
    /// </summary>
    /// <summary>Compatibility façade for callers that still need the legacy main-source helper.
    /// The decision now lives in <see cref="PlayableSourcePolicy"/>.</summary>
    public static VideoSourceDescriptor? SelectHighResMainSource(IEnumerable<VideoSourceDescriptor> sources)
        => PlayableSourcePolicy.Resolve(sources).Main;

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
                // Leave SourceUrl null so StartAsync resolves the best playable source
                // (snapshot or bubble fallback) on 5523-W.
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
            if (!_processSupervisor.TryGet(jobId, out var running) || running is null)
            {
                return null;
            }

            var handle = new RecordingHandle(running.Process, running.ScriptPath);
            var startedScript = running.ScriptPath is { Length: > 0 };
            var isBubble = running.Job.SourceUrl?.Contains("/bubble/live", StringComparison.OrdinalIgnoreCase) == true;
            var pipeline = isBubble ? (IRecordingPipeline)pipelines.BubbleFlv
                : startedScript ? (IRecordingPipeline)pipelines.Snapshot
                : pipelines.DirectFfmpeg;
            try
            {
                await pipeline.StopAsync(handle, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop recording process {JobId}", jobId);
            }

            _processSupervisor.Remove(jobId, out _);
            var stopped = running.Job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
            // PR-R1: Persist the stopped job
            try { await _recordingStore.SaveRecordingJobsAsync([stopped], cancellationToken); }
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
            snapshots = _processSupervisor.Snapshot()
                .Where(static entry => !entry.Process.HasExited)
                .Select(static entry => (entry.Job.Id, entry))
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
                .Where(RecordingSegmentPolicy.IsSupportedSegmentPath)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null) continue;

            var latestWrite = new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero);
            if (!RecordingLifecyclePolicy.IsStalled(now, latestWrite, stallTimeoutSeconds))
            {
                // Fresh segment growth proves this source is alive again — clear any prior
                // consecutive-restart debt so a recovered camera gets the full restart budget.
                _consecutiveStallRestarts.TryRemove(running.Job.DeviceId, out _);
                continue;
            }

            var idle = (now - latestWrite).TotalSeconds;
            logger.LogWarning("Stall detected: job={JobId} device={Device} idle={Idle:F0}s threshold={Threshold}s latest={Latest}",
                jobId, running.Job.DeviceId, idle, stallTimeoutSeconds, latest.FullName);

            // Stop the stalled pipeline while holding the orchestration gate.
            // `stoppedJob` is hoisted so the auto-restart block below (outside the gate)
            // can persist the suspended state without re-querying the supervisor.
            RecordingJob? stoppedJob = null;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_processSupervisor.TryGet(jobId, out var current) || current is null || current.Process.HasExited)
                    continue;

                var handle = new RecordingHandle(current.Process, current.ScriptPath);
                var isBubbleStalled = current.Job.SourceUrl?.Contains("/bubble/live", StringComparison.OrdinalIgnoreCase) == true;
                var pipeline = isBubbleStalled ? (IRecordingPipeline)pipelines.BubbleFlv
                    : current.ScriptPath is { Length: > 0 } ? (IRecordingPipeline)pipelines.Snapshot
                    : pipelines.DirectFfmpeg;
                try { await pipeline.StopAsync(handle, cancellationToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to stop stalled job {JobId}", jobId); }

                _processSupervisor.Remove(jobId, out _);
                var stopped = current.Job with { IsRunning = false, StoppedAt = now, LastError = "Stalled: no segment growth" };
                stoppedJob = stopped;
                try { await _recordingStore.SaveRecordingJobsAsync([stopped], cancellationToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to persist stalled recording job {JobId}", stopped.Id); }
                _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
                // `stalled` is populated below (outside the gate) so the suspension outcome —
                // when the restart cap trips — is the reported result, not the raw stop.
            }
            finally
            {
                _gate.Release();
            }

            // PR-R4: Auto-restart once if configured — outside the gate to avoid deadlock.
            // Cap consecutive restarts per device: a source that never produces media (e.g. a
            // 5523-W whose encoder pipeline locked up — RTSP answers but serves zero streams)
            // would otherwise restart a doomed ffmpeg every stall cycle forever. After
            // RecordingMaxConsecutiveRestarts consecutive stalls with no fresh segment, the
            // fast auto-restart is suspended and the job is marked stopped with a clear error;
            // the continuous-record policy (slow, backed-off) remains the recovery path for a
            // camera that genuinely comes back.
            if (autoRestart)
            {
                // Defensive: unreachable in practice — an in-gate `continue` (process already
                // exited) skips this whole block via the finally. Keeps the nullable analysis
                // simple without null-forgiving operators.
                if (stoppedJob is null)
                {
                    _consecutiveStallRestarts.TryRemove(running.Job.DeviceId, out _);
                    continue;
                }

                var maxRestarts = Math.Max(0, _runtimeOptions.RecordingMaxConsecutiveRestarts);
                var restartCount = _consecutiveStallRestarts.AddOrUpdate(
                    running.Job.DeviceId, 1, static (_, current) => current + 1);
                if (maxRestarts > 0 && restartCount > maxRestarts)
                {
                    // Cap tripped: persist the stopped job with a clear error and report it as
                    // the outcome of this cycle. The continuous-record policy (slow, backed-off)
                    // remains the recovery path for a camera that genuinely comes back.
                    _consecutiveStallRestarts.TryRemove(running.Job.DeviceId, out _);
                    var suspended = stoppedJob with
                    {
                        LastError = $"Camera source not producing media after {restartCount} consecutive restarts — auto-restart suspended; verify camera encoder/stream (recording continuity preserved via continuous-record policy)."
                    };
                    try { await _recordingStore.SaveRecordingJobsAsync([suspended], cancellationToken); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist suspended recording job {JobId}", suspended.Id); }
                    _ = broadcaster.RecordingJobStoppedAsync(suspended, CancellationToken.None);
                    stalled.Add(suspended);
                    logger.LogWarning("Auto-restart suspended for device={Device} after {Count} consecutive stalls — camera source not producing media", running.Job.DeviceId, restartCount);
                    continue;
                }

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

            // Report the final outcome of this cycle for the job. When the cap suspends the
            // auto-restart, `suspended` was already added above and `continue` skipped this.
            if (stoppedJob is not null)
            {
                stalled.Add(stoppedJob);
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
        var persisted = await _recordingStore.GetRecordingJobsAsync(null, cancellationToken);
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
                if (_processSupervisor.TryGet(job.Id, out var running) && running is not null && !running.Process.HasExited)
                {
                    reconciled.Add(job);
                    continue;
                }

                // Restart path: check the OS process by recorded PID so a still-running
                // ffmpeg / snapshot pipeline is re-attached instead of falsely stopped.
                var liveProcess = TryGetLiveProcess(job);
                var reconciliation = RecordingLifecyclePolicy.DecideReconciliation(
                    job,
                    liveProcess is not null,
                    liveProcess is null ? DateTime.MinValue : liveProcess.StartTime.ToUniversalTime());
                if (reconciliation.Action == RecordingReconciliationAction.Stop)
                {
                    liveProcess?.Dispose();
                    var stopped = job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
                    reconciled.Add(stopped);
                    try { await _recordingStore.SaveRecordingJobsAsync([stopped], cancellationToken); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist reconciled stopped job {JobId}", job.Id); }
                    _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
                    logger.LogWarning("Persisted job {JobId} marked running but process pid={Pid} is gone — reconciled as stopped", job.Id, job.ProcessId);
                }
                else
                {
                    // Re-attach the live process so stop / stall handling can manage it.
                    var scriptPath = string.Equals(job.Mode, "snapshot", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Path.GetTempPath(), $"bosscam-rec-{job.DeviceId:N}.sh")
                        : string.Equals(job.Mode, "bubble-flv", StringComparison.OrdinalIgnoreCase)
                            ? Path.Combine(Path.GetTempPath(), $"bosscam-rec-bubble-{job.DeviceId:N}.sh")
                            : null;
                    // PR-R1: The helper script may have been deleted on a prior stop or a
                    // /tmp sweep after restart, but stopping still works — both pipelines
                    // kill the recorded PID's whole process tree, so the ffmpeg/curl
                    // children die with it even when the script file is gone. Warn when
                    // the expected script is missing so operators can spot /tmp cleanup.
                    if (scriptPath is not null && !File.Exists(scriptPath))
                    {
                        logger.LogWarning("Re-attaching {Mode} job {JobId} but helper script is missing ({Script}); stop will rely on process-tree kill", job.Mode, job.Id, scriptPath);
                    }

                    var adoptedProcess = liveProcess ?? throw new InvalidOperationException("Lifecycle policy requested attachment without a live process.");
                    var reattachedEntry = new RunningRecording(job, adoptedProcess, scriptPath);
                    _processSupervisor.Add(reattachedEntry);
                    WireExitCleanup(adoptedProcess, reattachedEntry);
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
                if (_processSupervisor.TryGet(entry.Job.Id, out var current)
                    && current is not null
                    && ReferenceEquals(current, entry)
                    && _processSupervisor.RemoveIfCurrent(entry, out var removed)
                    && removed is not null)
                {
                    var stopped = removed.Job with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
                    logger.LogWarning("Recording job exited: {JobId}", entry.Job.Id);
                    // PR-R1: Persist the stopped job
                    try { await _recordingStore.SaveRecordingJobsAsync([stopped], CancellationToken.None); }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to persist exited recording job {JobId}", stopped.Id); }
                    // Push recording stopped to all connected SPA clients.
                    _ = broadcaster.RecordingJobStoppedAsync(stopped, CancellationToken.None);
                    // Clean up the helper script on spontaneous exit too (camera drop, EOF, signal)
                    // so we don't leak /tmp/bosscam-rec-*.sh when nobody ever calls StopAsync.
                    TryDeleteScript(removed.ScriptPath);

                    // Recording continuity: a continuous-record device whose recorder exited
                    // spontaneously while its source was still producing media (e.g. the 5523-W
                    // dropping the RTSP session every few minutes) must be re-picked in seconds,
                    // not after the slow backed-off policy cycle (up to
                    // RecordingRecoveryMaxRetrySeconds). Only when the source demonstrably
                    // produced a fresh segment — a dead encoder must stay on the slow path so we
                    // never spawn-storm a locked camera.
                    await TryScheduleExitRapidRestartAsync(removed, CancellationToken.None);
                }
            }
            finally
            {
                _gate.Release();
            }
        };
    }

    /// <summary>
    /// Schedules a rapid restart for a continuous-record device whose recorder exited
    /// spontaneously while the source was still producing fresh segments. Guards: (1) only
    /// devices flagged <see cref="DeviceIdentity.ContinuousRecord"/> (fleet policy devices);
    /// (2) a fresh segment must exist in the output directory (proves the camera was alive
    /// right up to the drop — a locked encoder gets no spawn storm); (3) a per-device
    /// cooldown (the same delay setting) prevents a tight loop when a camera flaps
    /// repeatedly. The restart runs off the exit handler's gate to avoid a re-entrant
    /// deadlock, and failures fall back to the normal continuous-record policy cycle.
    /// </summary>
    private async Task TryScheduleExitRapidRestartAsync(RunningRecording removed, CancellationToken cancellationToken)
    {
        var delaySeconds = _runtimeOptions.RecordingExitRestartDelaySeconds;
        if (delaySeconds <= 0)
        {
            return; // exit rapid-restart disabled
        }

        try
        {
            var device = await store.GetDeviceAsync(removed.Job.DeviceId, cancellationToken);
            if (device is null || !device.ContinuousRecord)
            {
                return;
            }

            var dir = removed.Job.OutputDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return;
            }

            // Fresh-segment proof: the source must have written media within the stall
            // window right up to the drop. No segment (or an ancient one) means the source
            // was never producing — stay on the slow backed-off policy path.
            var latest = Directory.EnumerateFiles(dir, "*.*")
                .Where(RecordingSegmentPolicy.IsSupportedSegmentPath)
                .Select(static p => new FileInfo(p))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                return;
            }

            var stallTimeout = Math.Max(30, _runtimeOptions.StallTimeoutSeconds);
            if (RecordingLifecyclePolicy.IsStalled(
                DateTimeOffset.UtcNow,
                new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero),
                stallTimeout))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var cooldown = TimeSpan.FromSeconds(Math.Max(5, delaySeconds));
            if (_lastExitRestart.TryGetValue(removed.Job.DeviceId, out var last) && now - last < cooldown)
            {
                return;
            }

            _lastExitRestart[removed.Job.DeviceId] = now;
            var restartDelay = TimeSpan.FromSeconds(Math.Max(3, delaySeconds));
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(restartDelay, CancellationToken.None);
                    var restarted = await StartAsync(new RecordingStartRequest { DeviceId = removed.Job.DeviceId }, CancellationToken.None);
                    logger.LogInformation("Exit rapid-restart: new job {JobId} for device={Device} (recording continuity)", restarted.Id, removed.Job.DeviceId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Exit rapid-restart failed for device={Device}; continuous-record policy remains the fallback", removed.Job.DeviceId);
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never let exit cleanup fail because the rapid-restart bookkeeping threw.
            logger.LogDebug(ex, "Exit rapid-restart check skipped for device={Device}", removed.Job.DeviceId);
        }
    }

    /// <summary>
    /// Runs a network-bound supervision call under a hard wall-clock budget so one unreachable
    /// camera (source-resolution probes stacking HttpClient timeouts) can never starve the
    /// single-threaded <c>RecordingLifecycleWorker</c> loop — a stalled recorder on camera B
    /// must still be detected while camera A is offline. On timeout the loop moves on and the
    /// abandoned call keeps running in the background: if it later completes and starts a job,
    /// the supervisor's dedup (GetRunningForProfileAsync + persisted-running guard) adopts it,
    /// so recording continuity is preserved either way. The abandoned task's exceptions are
    /// observed so an unhandled fault can never crash the process. Public because the
    /// <c>RecordingLifecycleWorker</c> (BossCam.Service assembly) invokes it for the stall
    /// check and both record reconciles; it is also exercised directly by unit tests.
    /// </summary>
    public static async Task<T> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        ILogger logger,
        string what,
        CancellationToken cancellationToken)
    {
        var task = work(cancellationToken);
        var completed = await Task.WhenAny(task, Task.Delay(budget, cancellationToken));
        if (completed != task)
        {
            logger.LogWarning(
                "Recording supervision call {What} exceeded its {Seconds:F0}s budget; continuing the loop (background work may still complete)",
                what,
                budget.TotalSeconds);
            // Observe the abandoned task so a late fault is logged (never unhandled) and a
            // late success is attributed to the budgeted call — otherwise a recording job
            // appearing minutes after the warning would look unexplained to an operator.
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    logger.LogWarning(t.Exception, "Abandoned supervision call {What} faulted after its budget", what);
                }
                else if (t.IsCompletedSuccessfully)
                {
                    logger.LogInformation("Abandoned supervision call {What} completed after its budget", what);
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
            return default!;
        }

        return await task;
    }

    public async Task<IReadOnlyCollection<RecordingJob>> GetJobsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _processSupervisor.Snapshot().Select(static entry => entry.Job with { IsRunning = !entry.Process.HasExited }).OrderByDescending(static job => job.StartedAt).ToList();
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
            var entry = _processSupervisor.Snapshot().FirstOrDefault(item => item.Job.ProfileId == profileId && !item.Process.HasExited);
            return entry?.Job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<RecordingJob>> ReconcileAutoStartAsync(CancellationToken cancellationToken)
    {
        var profiles = (await _recordingStore.GetRecordingProfilesAsync(null, cancellationToken))
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
                var running = (await _recordingStore.GetRecordingJobsAsync(device.Id, cancellationToken))
                    .FirstOrDefault(job => job.IsRunning);
                if (running is not null)
                {
                    // Re-promotion: a job that degraded to the snapshot pipeline when the main
                    // RTSP source was briefly unreachable (the 5523-W drops RTSP sessions every
                    // few minutes) must be promoted back to the full RTSP pipeline once that
                    // source answers again — otherwise the fleet silently records JPEG snapshots
                    // (no audio, no video motion) forever. Skipped for operator-forced snapshot
                    // jobs and whenever RTSP still does not answer, so a still-dead camera keeps
                    // its working snapshot pipeline during the outage. On success the degraded
                    // job was stopped; fall through to start a fresh direct job.
                    if (!await TryRePromoteDegradedSnapshotAsync(device, running, cancellationToken))
                    {
                        continue;
                    }
                }

                // Also guard on the in-memory map: a job spawned but not yet persisted (persist
                // step failed) must not be double-started by the next cycle — that would be the
                // runaway-ffmpeg case the persisted-store check alone cannot see.
                var runningInMemory = false;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    runningInMemory = _processSupervisor.Snapshot().Any(entry => entry.Job.DeviceId == device.Id);
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
    /// Re-promotion path for jobs degraded to the snapshot pipeline: when the main RTSP source
    /// that was unreachable at job-start answers again, the snapshot job is stopped so the
    /// caller (continuous-record reconcile) can start a fresh direct-RTSP job. Returns false
    /// (leaving the snapshot job untouched) for operator-forced snapshot requests and whenever
    /// RTSP is still unreachable — the snapshot pipeline keeps recording during the outage, so
    /// a still-dead camera is never churned. The RTSP probe is bounded (2s) so a dead camera
    /// cannot stall the reconcile loop.
    /// </summary>
    private async Task<bool> TryRePromoteDegradedSnapshotAsync(DeviceIdentity device, RecordingJob running, CancellationToken cancellationToken)
    {
        var isDegradedFallback = string.Equals(running.Mode, "snapshot", StringComparison.OrdinalIgnoreCase)
            && running.DegradedReason is { Length: > 0 }
            && !running.DegradedReason.Contains("forced by request", StringComparison.OrdinalIgnoreCase);
        if (!isDegradedFallback)
        {
            return false;
        }

        try
        {
            var sources = await transportBroker.GetSourcesAsync(device.Id, cancellationToken);
            var main = SelectHighResMainSource(sources);
            if (main?.Url is not string url
                || !url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(url, UriKind.Absolute, out var rtspUri))
            {
                return false;
            }

            var host = rtspUri.Host;
            var port = rtspUri.Port > 0 ? rtspUri.Port : 554;
            if (!await RtspProbe.ProbeAsync(host, port, cancellationToken, TimeSpan.FromSeconds(2)))
            {
                logger.LogDebug("RTSP still unreachable for {Device}; keeping degraded snapshot job {JobId}", device.DisplayName, running.Id);
                return false;
            }

            logger.LogInformation("RTSP recovered for {Device}; re-promoting degraded snapshot job {JobId} to the direct pipeline", device.DisplayName, running.Id);
            var stopped = await StopAsync(running.Id, cancellationToken);
            if (stopped is null)
            {
                // Persisted-only record (no live process in the supervisor — e.g. the job was
                // re-attached on boot or the process died while the store still said running).
                // Mark it stopped so the fresh direct job below is the only running job for the
                // device, otherwise the stale record would keep re-triggering this path.
                var stale = running with { IsRunning = false, StoppedAt = DateTimeOffset.UtcNow };
                try
                {
                    await _recordingStore.SaveRecordingJobsAsync([stale], cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to persist re-promoted stopped job {JobId}", running.Id);
                }

                _ = broadcaster.RecordingJobStoppedAsync(stale, CancellationToken.None);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Re-promotion probe failed for {Device}; keeping degraded snapshot job", device.DisplayName);
            return false;
        }
    }

    /// <summary>
    /// PR-R2: Enhanced segment indexer that populates DurationSec, StreamRole, Container,
    /// HasAudio, and JobId. Uses ffprobe for duration (cached per file via mtime/size key)
    /// and parses strftime patterns from filenames for stream role inference.
    /// Skips files whose mtime+size haven't changed since last index (incremental).
    /// </summary>
    public async Task<IReadOnlyCollection<RecordingSegment>> RefreshIndexAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var profiles = await _recordingStore.GetRecordingProfilesAsync(deviceId, cancellationToken);
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
                .Where(RecordingSegmentPolicy.IsSupportedSegmentPath))
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
                var (streamRole, hasAudio) = RecordingSegmentPolicy.InferMetadata(info.Name, profile);

                // PR-R2: Probe duration via ffprobe (best-effort)
                var duration = await ProbeDurationAsync(file, ffmpegPath, cancellationToken);
                var durationSec = duration ?? Math.Max(5, profile.SegmentSeconds);


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
        await _recordingStore.SaveRecordingSegmentsAsync(deduped, cancellationToken);
        return deduped;
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
            // ArgumentList (one argv element per value) so a path containing quotes/spaces
            // cannot break argument boundaries — same rule as every other ffmpeg invocation.
            var psi = ProcessLauncher.Build(probePath, new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                filePath
            });
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
        => _recordingStore.GetRecordingSegmentsAsync(deviceId, limit, cancellationToken);

    public async Task<RecordingHousekeepingResult> RunHousekeepingAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var profiles = await _recordingStore.GetRecordingProfilesAsync(deviceId, cancellationToken);
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
                .Where(RecordingSegmentPolicy.IsSupportedSegmentPath)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.CreationTimeUtc)
                .ToList();

            var deletedPaths = new List<string>();

            if (profile.RetentionDays > 0)
            {
                var cutoff = now.AddDays(-profile.RetentionDays);
                foreach (var info in files.Where(info => info.CreationTimeUtc < cutoff.UtcDateTime).ToList())
                {
                    if (TryDelete(info, ref deletedFiles, ref deletedBytes))
                    {
                        deletedPaths.Add(info.FullName);
                        _indexedCache.TryRemove(info.FullName, out _);
                    }
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
                        deletedPaths.Add(info.FullName);
                        _indexedCache.TryRemove(info.FullName, out _);
                    }
                }
            }

            if (deletedPaths.Count > 0)
            {
                await ReconcileDeletedSegmentRowsAsync(profile.DeviceId, deletedPaths, cancellationToken);
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
    /// Removes recording_segments rows whose physical files were just purged by retention, so
    /// the index table and <see cref="_indexedCache"/> do not grow unbounded and
    /// <see cref="ExportClipAsync"/> never selects a segment whose file is gone. Best-effort: a
    /// failed index reconcile must not stop retention from reclaiming disk.
    /// </summary>
    private async Task ReconcileDeletedSegmentRowsAsync(Guid deviceId, IReadOnlyCollection<string> deletedPaths, CancellationToken cancellationToken)
    {
        try
        {
            var segments = await _recordingStore.GetRecordingSegmentsAsync(deviceId, 100_000, cancellationToken);
            var ids = segments
                .Where(segment => deletedPaths.Contains(segment.FilePath, StringComparer.OrdinalIgnoreCase))
                .Select(static segment => segment.Id)
                .ToList();
            if (ids.Count > 0)
            {
                var removed = await _recordingStore.DeleteRecordingSegmentsAsync(ids, cancellationToken);
                logger.LogDebug("Housekeeping removed {Removed} recording segment index rows for purged files", removed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to reconcile recording segment index after housekeeping purge");
        }
    }

    /// <summary>Internal for unit tests (InternalsVisibleTo) so cache eviction after a
    /// housekeeping purge can be asserted without reflection.</summary>
    internal bool IsFileIndexed(string filePath) => _indexedCache.ContainsKey(filePath);

    /// <summary>
    /// PR-R3: Clip export with copy-first optimization. Uses concat demuxer with -c copy for segments
    /// that share compatible codecs. Falls back to re-encode only when timestamps or codecs force it.
    /// Returns result with path, bytes, duration, and whether re-encode was required.
    /// <para>
    /// Hardened: <c>OutputPath</c> is validated against <see cref="ExportOutputPathPolicy"/> before
    /// anything touches the filesystem, and both ffmpeg invocations go through
    /// <see cref="ProcessLauncher.Build"/> (ArgumentList, one argv element per value) so no
    /// caller-supplied path can break argument boundaries or inject ffmpeg flags. Segments whose
    /// physical files were already purged by retention are dropped and reported instead of failing
    /// the concat demuxer opaquely.
    /// </para>
    /// </summary>
    public async Task<ClipExportResult> ExportClipAsync(ClipExportRequest request, CancellationToken cancellationToken)
    {
        // Write-side allow-list: reject before creating directories or spawning ffmpeg. Without
        // this, OutputPath could write the clip anywhere the process has permission.
        if (!ExportOutputPathPolicy.IsAllowed(request.OutputPath, _runtimeOptions, out var policyReason))
        {
            return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = policyReason };
        }

        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = "ffmpeg not found." };
        }

        var segments = (await _recordingStore.GetRecordingSegmentsAsync(request.DeviceId, 5000, cancellationToken))
            .Where(segment => segment.EndTime >= request.StartTime && segment.StartTime <= request.EndTime)
            .OrderBy(segment => segment.StartTime)
            .ToList();

        if (segments.Count == 0)
        {
            return new ClipExportResult { Success = false, OutputPath = request.OutputPath, Message = "No indexed segments overlap the requested window." };
        }

        // Retention may have purged files after they were indexed — drop them before writing the
        // concat list so the demuxer can't fail opaquely, and report the skip honestly.
        var existing = segments.Where(static segment => File.Exists(segment.FilePath)).ToList();
        var missingCount = segments.Count - existing.Count;
        if (existing.Count == 0)
        {
            return new ClipExportResult
            {
                Success = false,
                OutputPath = request.OutputPath,
                Message = "All segments overlapping the requested window have been purged by retention."
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        var listFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(listFile, existing.Select(segment => $"file '{segment.FilePath.Replace("'", "''")}'"), cancellationToken);
            var skippedSuffix = missingCount > 0 ? $" (skipped {missingCount} purged)" : string.Empty;

            // PR-R3: Copy-first — try concat with -c copy; fall back to re-encode if that fails.
            // Both go through ArgumentList (no shell quoting) via BuildExportFfmpegStartInfo.
            var process = new Process
            {
                StartInfo = BuildExportFfmpegStartInfo(ffmpegPath, listFile, request.OutputPath, reEncode: false)
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
                    StartInfo = BuildExportFfmpegStartInfo(ffmpegPath, listFile, request.OutputPath, reEncode: true)
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
                    DurationSec = existing.Sum(static s => s.DurationSec > 0 ? s.DurationSec : 30),
                    ReEncoded = true,
                    Message = $"Copy-first failed; re-encode fallback was used.{skippedSuffix}"
                };
            }

            var fileInfo = new FileInfo(request.OutputPath);
            return new ClipExportResult
            {
                Success = true,
                OutputPath = request.OutputPath,
                Bytes = fileInfo.Length,
                DurationSec = existing.Sum(static s => s.DurationSec > 0 ? s.DurationSec : 30),
                ReEncoded = false,
                Message = $"Copied {existing.Count} segment(s){skippedSuffix}"
            };
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
        }
    }

    /// <summary>
    /// Builds the ffmpeg <see cref="ProcessStartInfo"/> for a clip export via
    /// <see cref="ProcessLauncher.Build"/> — every value travels as its own
    /// <c>ArgumentList</c> element, so a caller-supplied <c>OutputPath</c> containing a quote
    /// or shell metacharacter can never break argument boundaries or inject ffmpeg flags.
    /// Internal for unit tests (InternalsVisibleTo) to pin the ArgumentList shape.
    /// </summary>
    internal static ProcessStartInfo BuildExportFfmpegStartInfo(string ffmpegPath, string listFile, string outputPath, bool reEncode)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-f", "concat", "-safe", "0",
            "-i", listFile
        };
        if (reEncode)
        {
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "128k" });
        }
        else
        {
            args.Add("-c");
            args.Add("copy");
        }
        args.Add(outputPath);
        // stdout is not consumed by the export path (ffmpeg writes diagnostics to stderr), so
        // keep it unredirected to match the original ProcessStartInfo exactly.
        return ProcessLauncher.Build(ffmpegPath, args, redirectStdout: false);
    }

    /// <summary>
    /// High-res RTSP (HEVC/H264) + transcode PCMA audio to AAC (copying a-law into MPEG-TS
    /// would produce an unlabeled bin_data private stream, not a decodable audio track).
    /// Segment to MPEG-TS for kill-safe files.
    /// </summary>
    public static string BuildFfmpegArgs(string sourceUrl, string segmentPattern, int segmentSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -y ");
        sb.Append("-analyzeduration 8000000 -probesize 8000000 ");

        if (sourceUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            // TCP interleaved RTP. A silent RTSP stall would otherwise block ffmpeg's read
            // forever (~0% CPU, never exits) — -timeout (rtsp demuxer socket I/O timeout, µs)
            // aborts the stalled input so WireExitCleanup's exit rapid-restart can re-arm.
            // Single source of truth: the argv-pinning E2E test asserts this exact string, so
            // it must never drift from DirectFfmpegRecordingPipeline's real args.
            sb.Append("-timeout ")
                .Append(DirectFfmpegRecordingPipeline.RtspSocketTimeoutMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ');
            sb.Append("-rtsp_transport tcp ");
        }

        sb.Append("-i \"").Append(sourceUrl).Append("\" ");
        // PR-R7: Map best video + best audio stream when available. Use optional audio
        // (-map 0:a:0?) so the pipeline doesn't fail if no audio track exists.
        // Audio transcoded to AAC: 5523-W cameras emit G.711 a-law, which the TS muxer
        // would otherwise write as an unlabeled bin_data private stream.
        sb.Append("-map 0:v:0 -c:v copy ");
        sb.Append("-map 0:a:0? -c:a aac -b:a 128k ");
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
        var profiles = await _recordingStore.GetRecordingProfilesAsync(device.Id, cancellationToken);
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
                await _recordingStore.SaveRecordingProfilesAsync([next], cancellationToken);
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
        await _recordingStore.SaveRecordingProfilesAsync([profile], cancellationToken);
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
