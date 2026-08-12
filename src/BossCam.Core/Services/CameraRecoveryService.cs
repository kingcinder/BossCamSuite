using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BossCam.Contracts;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

/// <summary>
/// One visible camera AP (SSID "IPC" + serial without the "JA" prefix) found by
/// scanning the host's WiFi radio. These APs are broadcast by factory-reset
/// 5523-W cameras that have dropped off the LAN.
/// </summary>
public sealed record CameraApInfo
{
    public string Ssid { get; init; } = string.Empty;
    public string Bssid { get; init; } = string.Empty;
    public string Signal { get; init; } = string.Empty;
    public string Security { get; init; } = string.Empty;
    /// <summary>Canonical camera serial derived from the AP SSID (IPCZ7C34… → JAZ7C34…).</summary>
    public string Serial { get; init; } = string.Empty;
}

/// <summary>Snapshot of the autonomous camera-AP scan worker (for the operator UI).</summary>
public sealed record AutoRecoveryStatus
{
    public bool Enabled { get; init; }
    public int IntervalSeconds { get; init; }
    public int CooldownMinutes { get; init; }
    public string StaSsid { get; init; } = string.Empty;
    public DateTimeOffset LastScanAtUtc { get; init; }
    public int LastScanCount { get; init; }
    public string LastAction { get; init; } = string.Empty;
    public string? ActiveSerial { get; init; }
    public string CurrentSsid { get; init; } = string.Empty;
}

/// <summary>Status of a background camera-recovery run.</summary>
public sealed record CameraRecoveryRunStatus
{
    public string RunId { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public bool Running { get; init; }
    public bool Succeeded { get; init; }
    public int? ExitCode { get; init; }
    public string? LanIp { get; init; }
    public string? Message { get; init; }
    public string LogTail { get; init; } = string.Empty;

    /// <summary>
    /// Post-recovery recording verification: true when a recording job was confirmed active
    /// (or started on demand) for the recovered camera. Recording continuity is the top
    /// priority — a camera that came back on the LAN but is NOT recording must be surfaced.
    /// </summary>
    public bool RecordingVerified { get; init; }
    /// <summary>Whether the camera's RTSP endpoint answered the verification probe.</summary>
    public bool RtspReachable { get; init; }
    /// <summary>The recording job id that was found running / started on demand.</summary>
    public string? RecordingJobId { get; init; }
    /// <summary>Human-readable recording-verification outcome (for the operator UI).</summary>
    public string? RecordingMessage { get; init; }
}

/// <summary>
/// Outcome of the post-recovery recording verification (<see cref="CameraRecoveryService.VerifyRecordingAsync"/>).
/// </summary>
public sealed record RecoveryRecordingVerification
{
    public bool Verified { get; init; }
    public bool RtspReachable { get; init; }
    public string? JobId { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Orchestrates the AP-hotspot → LAN → BossCamSuite recovery procedure for
/// factory-reset cameras. Scans the host WiFi for camera APs
/// (<c>IPCZ7C34…</c>) and runs <c>scripts/recover-and-enroll-camera.sh</c> in
/// the background, capturing output so the operator UI can poll progress.
/// </summary>
public sealed class CameraRecoveryService(
    ILogger<CameraRecoveryService> logger,
    Microsoft.Extensions.Options.IOptions<BossCamRuntimeOptions> options,
    IApplicationStore store,
    RecordingService recordingService,
    IRecordingStore recordingStore)
{
    private static readonly ConcurrentDictionary<string, CameraRecoveryRunStatus> Runs = new(StringComparer.Ordinal);
    private static int _runCounter;
    private static readonly object RunStartLock = new();
    // Serializes the check-then-act in StartRecovery: two concurrent callers (double-click on
    // the manual button, or a manual start landing in the auto-worker's HasActiveRun→
    // StartRecovery gap) must not both read HasActiveRun==false before either inserts a
    // Running run. Static because Runs/_runCounter are static (singleton semantics).

    /// <summary>Scan the host WiFi for camera APs (non-destructive: rescan + list only).</summary>
    public async Task<IReadOnlyCollection<CameraApInfo>> ScanCameraApsAsync(CancellationToken ct)
    {
        var results = new List<CameraApInfo>();
        try
        {
            // Rescan is BEST-EFFORT: NetworkManager only authorizes wifi.rescan from an
            // interactive session (polkit); a systemd-hosted service gets
            // "not authorized" for the rescan while the LIST still works off the last
            // scan cache. Never fail the scan because the rescan was denied — the list
            // is the source of truth and the operator's interactive session keeps the
            // cache fresh.
            try
            {
                await RunCaptureAsync("nmcli", ["dev", "wifi", "rescan"], ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("nmcli rescan not authorized from service context; using cached scan: {Message}", ex.Message);
            }

            var output = await RunCaptureAsync("nmcli", ["-t", "-f", "SSID,BSSID,SIGNAL,SECURITY", "dev", "wifi", "list"], ct).ConfigureAwait(false);
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // nmcli -t escapes ':' and '\' as '\:' / '\\' (BSSIDs are full of colons).
                // Split on UNESCAPED colons, then unescape each field, so a BSSID like
                // 9C\:A3\:A9\:B9\:BF\:55 lands whole instead of as 6 bogus fields.
                var fields = SplitUnescaped(raw, ':');
                if (fields.Length < 4)
                {
                    continue;
                }

                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = fields[i].Replace("\\:", ":", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                }

                var ssid = fields[0];
                if (!ssid.StartsWith("IPCZ7C34", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new CameraApInfo
                {
                    Ssid = ssid,
                    Bssid = fields[1],
                    Signal = fields[2],
                    Security = fields[3],
                    Serial = ssid.StartsWith("IPC", StringComparison.OrdinalIgnoreCase)
                        ? $"JA{ssid[3..]}"
                        : ssid
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Camera AP scan failed: {Message}", ex.Message);
        }

        return results;
    }

    /// <summary>
    /// Start a background recovery run for <paramref name="serial"/> (or an AP SSID).
    /// The script joins the camera AP, re-provisions station mode, rejoins the LAN,
    /// redisovers the camera by MAC, then enrolls it into BossCamSuite via the API.
    /// Returns a run id that <see cref="GetStatus"/> can poll.
    /// </summary>
    /// <remarks>
    /// The spawned run deliberately outlives the triggering HTTP request: the caller's
    /// token is only used to validate the start, and the process lifecycle uses
    /// <see cref="CancellationToken.None"/> so a client disconnect or connection reuse
    /// after <c>Results.Accepted</c> cannot cancel the recovery mid-flight.
    /// </remarks>
    public string StartRecovery(string serial, CancellationToken startToken, bool dryRun = false)
    {
        // The caller's token gates only the START of the run — validating it here
        // (synchronously) is the one place the request lifetime is allowed to matter.
        // The spawned process itself always runs with CancellationToken.None so a
        // client disconnect after Results.Accepted cannot cancel the recovery mid-flight.
        startToken.ThrowIfCancellationRequested();

        // One-radio invariant: never run two recoveries concurrently. The auto-worker checks
        // HasActiveRun before calling, but that check is not atomic with this call, and the
        // manual API path lands here with no prior check at all — so refuse synchronously at
        // this single chokepoint, under a lock so the check-then-act is atomic even when two
        // callers arrive together. The API maps this to 409 Conflict; the worker's own
        // HasActiveRun gate means it can only hit this on a true manual/auto race.
        string runId;
        lock (RunStartLock)
        {
            if (HasActiveRun)
            {
                throw new InvalidOperationException("A camera recovery run is already active — only one recovery may run at a time.");
            }

            runId = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _runCounter)}";
            Runs[runId] = new CameraRecoveryRunStatus { RunId = runId, Serial = serial, Running = true };
        }

        var script = ResolveScriptPath();
        _ = Task.Run(async () =>
        {
            var log = new StringBuilder();
            try
            {
                if (!File.Exists(script))
                {
                    throw new FileNotFoundException($"Recovery script not found: {script}");
                }

                var info = ProcessLauncher.BuildBashScript(script, redirectStdout: true, redirectStderr: true, createNoWindow: true);
                info.ArgumentList.Add(serial);
                // DRY_RUN flows to both the wrapper and the folded reprovision tool: the
                // whole pipeline prints its plan and touches nothing (no WiFi join, no
                // camera writes, no enrollment) — the operator can smoke-test the full
                // Suite-driven path without a live factory-reset camera.
                if (dryRun)
                {
                    info.Environment["DRY_RUN"] = "1";
                }

                // Operator-configured camera-AP hotspot passphrase. The script's own factory
                // try-list already includes the fleet default (11111111); when an operator
                // overrides RecoveryApPass, AP_PASS takes precedence so the auto-worker and
                // manual runs both use it without editing scripts.
                var apPass = options.Value.RecoveryApPass;
                if (!string.IsNullOrWhiteSpace(apPass))
                {
                    info.Environment["AP_PASS"] = apPass;
                }

                using var process = ProcessLauncher.Start(info);
                // Drain stdout and stderr CONCURRENTLY so a chatty child cannot fill a
                // pipe buffer and deadlock the run (stdout drained inline, stderr on a
                // parallel task; both append to the shared log under a lock).
                var stdoutTask = DrainAsync(process.StandardOutput, runId, log);
                var stderrTask = DrainAsync(process.StandardError, runId, log);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                var lanIp = ExtractLanIp(log.ToString());
                var succeeded = process.ExitCode == 0;
                var status = new CameraRecoveryRunStatus
                {
                    RunId = runId,
                    Serial = serial,
                    Running = false,
                    Succeeded = succeeded,
                    ExitCode = process.ExitCode,
                    LanIp = lanIp,
                    Message = dryRun
                        ? (succeeded
                            ? "Dry-run completed — plan printed only; no WiFi join, camera writes, or enrollment were made."
                            : "Dry-run failed — inspect the log tail.")
                        : (succeeded
                            ? $"Recovery complete. Camera is on the network{(lanIp is null ? "" : $" at {lanIp}")} and enrolled in BossCamSuite."
                            : "Recovery failed — inspect the log tail."),
                    LogTail = Tail(log.ToString(), 6000)
                };
                // Fail-open default: this pre-verification write deliberately exposes
                // Succeeded=true with RecordingVerified=false to a poller for a moment until the
                // verification below completes and overwrites the entry. That transient is
                // harmless for a status endpoint (and the alternative — withholding the
                // completion write until verification finishes — would delay the UI's
                // Succeeded signal by the whole verify window). Do not remove this pre-write.
                Runs[runId] = status;

                // RECORDING CONTINUITY IS THE TOP PRIORITY: a camera that rejoined the LAN and
                // was enrolled must be PROVEN to be recording, not assumed. The recovery script
                // already fires /api/recordings/start, but this Suite-side verification is the
                // independent check: RTSP reachable + a recording job actually active. When no
                // job is running, retry starting one (bounded), then surface the truth on the
                // run status either way — a recovered-but-not-recording camera is a failure
                // state the operator must see, not a silent gap.
                if (succeeded && !dryRun)
                {
                    try
                    {
                        var verification = await VerifyRecordingAsync(serial, lanIp, CancellationToken.None);
                        status = status with
                        {
                            RecordingVerified = verification.Verified,
                            RtspReachable = verification.RtspReachable,
                            RecordingJobId = verification.JobId,
                            RecordingMessage = verification.Message
                        };
                        Runs[runId] = status;
                        AppendLog(runId, log, $"\n[verify] {verification.Message}\n");
                        if (verification.Verified)
                        {
                            logger.LogInformation(
                                "Recovery run {RunId} ({Serial}): recording VERIFIED — {Message}",
                                runId, serial, verification.Message);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Recovery run {RunId} ({Serial}) succeeded but recording NOT verified: {Message}",
                                runId, serial, verification.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        status = status with
                        {
                            RecordingVerified = false,
                            RecordingMessage = $"Recording verification failed: {ex.Message}"
                        };
                        Runs[runId] = status;
                        logger.LogError(ex, "Recording verification failed for recovery run {RunId} ({Serial})", runId, serial);
                    }
                }
                logger.LogInformation("Camera recovery run {RunId} ({Serial}) exited {ExitCode}", runId, serial, process.ExitCode);
            }
            catch (Exception ex)
            {
                Runs[runId] = new CameraRecoveryRunStatus
                {
                    RunId = runId,
                    Serial = serial,
                    Running = false,
                    Succeeded = false,
                    Message = $"Recovery failed: {ex.Message}",
                    LogTail = Tail(log.ToString(), 6000)
                };
                logger.LogError(ex, "Camera recovery run {RunId} failed", runId);
            }
        }, CancellationToken.None);
        return runId;
    }

    /// <summary>
    /// Post-recovery recording verification — the recording-continuity gate. After the recovery
    /// script reports success (camera back on the LAN + enrolled), independently confirm the
    /// camera actually records before the run is declared clean:
    ///  1. locate the enrolled device by serial (JA-prefixed or raw) or by the LAN IP the
    ///     script handed back;
    ///  2. probe RTSP on the device's media port;
    ///  3. confirm a recording job is ACTIVE for the device (in-memory supervisor first, then
    ///     the persisted store);
    ///  4. when none is active, start one with bounded retries
    ///     (<see cref="BossCamRuntimeOptions.RecoveryRecordingVerifyAttempts"/> / delay).
    /// Returns a <see cref="RecoveryRecordingVerification"/> so the caller can surface the gap
    /// without swallowing it. Internal for unit tests (InternalsVisibleTo BossCam.Tests).
    /// </summary>
    internal async Task<RecoveryRecordingVerification> VerifyRecordingAsync(
        string serial,
        string? lanIp,
        CancellationToken ct,
        Func<DeviceIdentity, CancellationToken, Task<bool>>? rtspProbe = null,
        Func<DeviceIdentity, CancellationToken, Task<RecordingJob?>>? ensureRecording = null)
    {
        var probe = rtspProbe ?? ProbeRtspAsync;
        var ensure = ensureRecording ?? EnsureRecordingAsync;

        var device = await FindEnrolledDeviceAsync(serial, lanIp, ct).ConfigureAwait(false);
        if (device is null)
        {
            return new RecoveryRecordingVerification
            {
                Verified = false,
                Message = $"Enrolled device not found for serial {serial}{(lanIp is null ? "" : $" / LAN IP {lanIp}")} — cannot verify recording. The camera may have rejoined the network but enrollment did not persist."
            };
        }

        // RTSP reachability is the first half of the check (informational on its own — the
        // snapshot pipeline records fine without RTSP — but a dead RTSP port is a strong
        // early-warning signal worth reporting alongside the job state).
        var rtspReachable = false;
        try
        {
            rtspReachable = await probe(device, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("RTSP verification probe failed for {Ip}: {Message}", device.IpAddress, ex.Message);
        }

        // Is a recording job already active? Check the live supervisor first (fast, reflects
        // what is actually running), then the persisted store (durable truth after restarts).
        var activeJob = await FindActiveJobAsync(device, ct).ConfigureAwait(false);
        if (activeJob is not null)
        {
            return new RecoveryRecordingVerification
            {
                Verified = true,
                RtspReachable = rtspReachable,
                JobId = activeJob.Id.ToString("N"),
                Message = $"Recording job {activeJob.Id:N} already active (mode {activeJob.Mode}) — RTSP {(rtspReachable ? "reachable" : "not answering")}."
            };
        }

        // No active job: start one, with bounded retries. The recovery script already fired
        // /api/recordings/start, but if that didn't stick (camera still settling on the new
        // network, ffmpeg flake, policy race) the Suite retries here instead of silently
        // leaving the camera unrecorded.
        var attempts = Math.Max(1, options.Value.RecoveryRecordingVerifyAttempts);
        var delay = TimeSpan.FromSeconds(Math.Max(0, options.Value.RecoveryRecordingVerifyDelaySeconds));
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var job = await ensure(device, ct).ConfigureAwait(false);
                if (job is not null)
                {
                    return new RecoveryRecordingVerification
                    {
                        Verified = true,
                        RtspReachable = rtspReachable,
                        JobId = job.Id.ToString("N"),
                        Message = $"No recording was running — started job {job.Id:N} (mode {job.Mode}) on attempt {attempt}/{attempts}. RTSP {(rtspReachable ? "reachable" : "not answering — snapshot pipeline in use")}."
                    };
                }

                lastError = new InvalidOperationException("recording start returned no job");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(
                    "Recording start retry {Attempt}/{Total} failed for {Serial} ({Ip}): {Message}",
                    attempt, attempts, serial, device.IpAddress, ex.Message);
            }

            if (attempt < attempts)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return new RecoveryRecordingVerification
        {
            Verified = false,
            RtspReachable = rtspReachable,
            Message = $"Camera recovered and enrolled but recording did NOT start after {attempts} attempt(s): {lastError?.Message ?? "unknown error"}."
        };
    }

    /// <summary>Find the enrolled device for a recovered serial (JA-prefixed or raw) or LAN IP.</summary>
    private async Task<DeviceIdentity?> FindEnrolledDeviceAsync(string serial, string? lanIp, CancellationToken ct)
    {
        var devices = await store.GetDevicesAsync(ct).ConfigureAwait(false);
        var serialWithoutJa = serial.StartsWith("JA", StringComparison.OrdinalIgnoreCase)
            ? serial[2..]
            : serial;
        var normalized = new[] { serial, serialWithoutJa }
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return devices.FirstOrDefault(device =>
            // Exact serial match is the primary channel (the recovery script stores the raw
            // deviceInfo serialNumber, e.g. Z7C34781634738, while the run key may carry the JA
            // prefix — both are covered here).
            (!string.IsNullOrWhiteSpace(device.DeviceId)
             && normalized.Contains(device.DeviceId, StringComparer.OrdinalIgnoreCase))
            // Name substring match is a secondary channel, and deliberately gated to serials
            // that look like real camera serials: the recovery script falls back to the
            // placeholder "unknown" when serial derivation fails, and a bare "unknown" (or any
            // short/generic string) substring-matching a device Name would bind the
            // verification to the wrong camera. Real 5523-W serials are 16 alphanumerics.
            || (!string.IsNullOrWhiteSpace(device.Name)
                && normalized.Any(n => n.Length >= 8 && device.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            // LAN-IP handoff is the deterministic fallback when the serial channels miss.
            || (!string.IsNullOrWhiteSpace(lanIp)
                && string.Equals(device.IpAddress, lanIp, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Live supervisor first, then persisted store — either proves recording is active.</summary>
    private async Task<RecordingJob?> FindActiveJobAsync(DeviceIdentity device, CancellationToken ct)
    {
        try
        {
            var live = await recordingService.GetJobsAsync(ct).ConfigureAwait(false);
            var running = live.FirstOrDefault(job => job.DeviceId == device.Id && job.IsRunning);
            if (running is not null)
            {
                return running;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Live recording-job lookup failed for {Ip}: {Message}", device.IpAddress, ex.Message);
        }

        try
        {
            var persisted = await recordingStore.GetRecordingJobsAsync(device.Id, ct).ConfigureAwait(false);
            return persisted.FirstOrDefault(job => job.IsRunning);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Persisted recording-job lookup failed for {Ip}: {Message}", device.IpAddress, ex.Message);
            return null;
        }
    }

    /// <summary>Default RTSP probe used by <see cref="VerifyRecordingAsync"/> (2s bounded handshake).</summary>
    private Task<bool> ProbeRtspAsync(DeviceIdentity device, CancellationToken ct)
    {
        var port = device.RtspPort is > 0 ? device.RtspPort.Value : 554;
        return RtspProbe.ProbeAsync(device.IpAddress ?? string.Empty, port, ct, TimeSpan.FromSeconds(2));
    }

    /// <summary>Default recording starter used by <see cref="VerifyRecordingAsync"/> — start on best playable source.
    /// Exceptions propagate to the retry loop so the run status reports the real failure reason.</summary>
    private async Task<RecordingJob?> EnsureRecordingAsync(DeviceIdentity device, CancellationToken ct)
        => await recordingService.StartAsync(new RecordingStartRequest { DeviceId = device.Id }, ct).ConfigureAwait(false);

    private async Task DrainAsync(StreamReader reader, string runId, StringBuilder log)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            AppendLog(runId, log, new string(buffer, 0, read));
        }
    }

    /// <summary>Poll a recovery run's status (safe to call for unknown ids — returns null).</summary>
    public CameraRecoveryRunStatus? GetStatus(string runId)
        => Runs.TryGetValue(runId, out var status) ? status : null;

    /// <summary>True when at least one recovery run is currently executing (used to serialize auto-recovery).</summary>
    public bool HasActiveRun
        => Runs.Values.Any(static s => s.Running);

    /// <summary>
    /// The SSID the host WiFi is currently connected to (or "" when not on WiFi). Used by the
    /// auto-worker to only scan/act while on the home network — the host must NOT be mid-join
    /// on a camera AP or on an unrelated network.
    /// </summary>
    public async Task<string> GetCurrentSsidAsync(CancellationToken ct)
    {
        try
        {
            var output = await RunCaptureAsync("nmcli", ["-t", "-f", "ACTIVE,SSID", "dev", "wifi"], ct).ConfigureAwait(false);
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = SplitUnescaped(raw, ':');
                if (fields.Length >= 2 && fields[0].Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    return fields[1].Replace("\\:", ":", StringComparison.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not read current WiFi SSID: {Message}", ex.Message);
        }

        return string.Empty;
    }

    // ── autonomous scan surface (written by CameraRecoveryAutoWorker, read by the API/UI) ──
    private readonly object _autoLock = new();
    private DateTimeOffset _autoLastScanAt;
    private int _autoLastScanCount;
    private string? _autoLastAction;

    /// <summary>Snapshot of the autonomous worker's most recent scan cycle (for the UI).</summary>
    public AutoRecoveryStatus GetAutoStatus()
    {
        lock (_autoLock)
        {
            // ActiveSerial is derived from the LIVE Runs dictionary rather than a recorded
            // field: the worker's "waiting" cycles run while a recovery is in flight and would
            // otherwise overwrite the recorded serial with null ~45s in, hiding the indicator.
            var activeRun = Runs.Values.FirstOrDefault(static s => s.Running);
            return new AutoRecoveryStatus
            {
                Enabled = options.Value.RecoveryAutoScanEnabled,
                IntervalSeconds = options.Value.RecoveryAutoScanIntervalSeconds,
                CooldownMinutes = options.Value.RecoveryAutoCooldownMinutes,
                StaSsid = options.Value.RecoveryStaSsid,
                LastScanAtUtc = _autoLastScanAt,
                LastScanCount = _autoLastScanCount,
                LastAction = _autoLastAction ?? string.Empty,
                ActiveSerial = activeRun?.Serial,
                CurrentSsid = string.Empty
            };
        }
    }

    /// <summary>Record one autonomous scan cycle outcome (called by the worker under its own lock discipline).</summary>
    public void RecordAutoScan(DateTimeOffset at, int apCount, string action)
    {
        lock (_autoLock)
        {
            _autoLastScanAt = at;
            _autoLastScanCount = apCount;
            _autoLastAction = action;
        }
    }

    private void AppendLog(string runId, StringBuilder log, string chunk)
    {
        lock (log)
        {
            log.Append(chunk);
            if (Runs.TryGetValue(runId, out var current))
            {
                Runs[runId] = current with { LogTail = Tail(log.ToString(), 6000) };
            }
        }
    }

    private string ResolveScriptPath()
    {
        var configured = options.Value.RecoveryScriptPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        // Fall back to well-known locations so the deployed service finds the script
        // without configuration: content root, then the repo checkout.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", "recover-and-enroll-camera.sh"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "recover-and-enroll-camera.sh")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? Path.Combine(AppContext.BaseDirectory, "scripts", "recover-and-enroll-camera.sh");
    }

    private static string[] SplitUnescaped(string text, char separator)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == ':' || text[i + 1] == '\\'))
            {
                // Escaped separator or escape: keep the pair in this field.
                current.Append(c);
                current.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static string? ExtractLanIp(string log)
    {
        // The script prints "camera is back on the network at <ip>" and the reprovision
        // phase writes the LAN IP to REPRO_OUT. Match the log line for the handoff.
        var match = System.Text.RegularExpressions.Regex.Match(log, @"back on the network at ([\d.]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    private static async Task<string> RunCaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = ProcessLauncher.Build(fileName, arguments, redirectStdout: true, redirectStderr: true, createNoWindow: true);
        using var process = ProcessLauncher.Start(info);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited {process.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
