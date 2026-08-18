using System.Diagnostics;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// One auto-loading camera tile on the Live View landing board. Streams the camera's
/// live video (HD main, through the same negotiated ffmpeg rawvideo pipeline as the
/// shell's live view) into a WriteableBitmap with latest-wins rendering, so the whole
/// board shows real motion at the camera's native rate instead of a 2-second snapshot
/// slideshow. A slow snapshot poll stays as the offline-fallback signal (it drives the
/// offline badge / watchdog) and keeps a still on the tile while video reconnects.
/// Star toggle, selection and fullscreen commands route through the shell so the star
/// set stays server-synced (mirrors the SPA's Live Wall tiles).
/// </summary>
public sealed partial class BoardTileViewModel : ObservableObject, IDisposable
{
    private readonly IBossCamApiClient _api;
    private readonly MainWindowViewModel _shell;
    private Timer? _snapshotTimer;
    private CancellationTokenSource? _videoCts;
    private Task? _videoTask;
    private Task? _startupTask;
    private Process? _videoProcess;
    private int _snapshotPollInProgress;
    private int _videoGeneration;
    private long _renderedFrameCount;
    private int _attemptRenderedFrame;
    private bool _disposed;
    private long _lastFrameUtcTicks;
    private long _videoStartedUtcTicks;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private const int FrameWidth = 960;
    private const int FrameHeight = 540;
    private static readonly int FrameSize = FrameWidth * FrameHeight * 4;

    public BoardTileViewModel(IBossCamApiClient api, MainWindowViewModel shell, DeviceIdentity device)
    {
        _api = api;
        _shell = shell;
        Device = device;
        // Slow snapshot poll: drives the offline badge and bridges reconnects, but skips
        // entirely while live video is flowing (checked against the last-render stamp).
        _snapshotTimer = new Timer(async _ => await PollSnapshotAsync(), null, 3000, 3000);
        _shell.StarsChanged += OnStarsChanged;
        _startupTask = StartVideoAsync();
    }

    public DeviceIdentity Device { get; }

    public string DisplayName => Device.DisplayName ?? Device.IpAddress ?? Device.Id.ToString();

    public string IpAddress => Device.IpAddress ?? string.Empty;

    [ObservableProperty]
    private Bitmap? _liveFrame;

    /// <summary>Live video rate shown on the tile ("15 fps") while the video loop renders.</summary>
    [ObservableProperty]
    private string? _fpsText;

    /// <summary>Consecutive fallback failures (drives the offline badge).</summary>
    private int _snapshotFailures;

    /// <summary>
    /// Offline badge shown when the camera stopped answering — the feed watchdog
    /// (scripts/video-feed-watchdog.sh) monitors the same signal and applies its repair
    /// tree (probe → reconnect → restart recording → hunt → page). Null (not empty) when
    /// healthy so the NotNullToBoolConverter keeps the badge hidden on live tiles.
    /// </summary>
    public string? TileStateText => Volatile.Read(ref _snapshotFailures) >= 3
        ? "● offline — watchdog active"
        : null;

    public bool IsStarred => _shell.IsStarred(Device.Id);

    /// <summary>Hollow ☆ / gold ★ glyph for the tile's star button.</summary>
    public string StarGlyph => IsStarred ? "★" : "☆";

    /// <summary>True while the tile is the shell's selected camera (drives the detail pane below).</summary>
    public bool IsSelected => _shell.SelectedDevice?.Id == Device.Id;

    private void OnStarsChanged()
    {
        OnPropertyChanged(nameof(IsStarred));
        OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>Refreshes IsSelected when the shell's selection changes (toggled by the shell).</summary>
    internal void RefreshSelectionState() => OnPropertyChanged(nameof(IsSelected));

    [RelayCommand]
    private void ToggleStar() => _shell.ToggleStarCommand.Execute(Device);

    [RelayCommand]
    private void Select() => _shell.SelectedDevice = Device;

    [RelayCommand]
    private void OpenFullscreen() => _shell.OpenFullscreenCommand.Execute(Device);

    // ── Live video (HD main, latest-wins render) ─────────────────

    private async Task StartVideoAsync()
    {
        var lifetimeToken = _lifetimeCts.Token;
        try
        {
            LiveMediaManifest? manifest = null;
            for (var attempt = 0; MainWindowViewModel.ShouldRetryLiveManifest(attempt, _disposed); attempt++)
            {
                try
                {
                    manifest = await _api.GetLiveManifestAsync(Device.Id, "main");
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || _disposed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A transient service/manifest failure is a transport retry, not a
                    // terminal tile failure. Keep the bounded ladder alive and let the
                    // snapshot watchdog bridge the short gap.
                    System.Diagnostics.Debug.WriteLine($"Tile manifest attempt {attempt + 1} failed for {DisplayName}: {ex.Message}");
                    manifest = null;
                }

                if (manifest is not null)
                {
                    break;
                }

                if (MainWindowViewModel.ShouldRetryLiveManifest(attempt + 1, _disposed))
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), lifetimeToken);
                    }
                    catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested || _disposed)
                    {
                        return;
                    }
                }
            }

            if (_disposed)
            {
                return;
            }

            // Keep the landing board on the service-owned HTTP ladder. It is one shared
            // RTSP session per camera, with replayable fMP4 initialization and server-side
            // retries; opening a direct socket from each tile can exhaust a 5523-W camera's
            // session budget and freeze the board while recording is active.
            var streamUrls = manifest is null
                ? []
                : SelectBoardStreamUrls(manifest);
            if (streamUrls.Count == 0)
            {
                return; // snapshot poll keeps the tile alive
            }

            var cts = new CancellationTokenSource();
            _videoCts = cts;
            _videoTask = Task.Run(() => RunVideoLoopAsync(streamUrls, cts.Token));
        }
        catch (Exception ex)
        {
            // Video failed to start — the snapshot fallback + offline badge take over.
            System.Diagnostics.Debug.WriteLine($"Tile video start failed for {DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the board's rawvideo command with the same dimensions as its WriteableBitmap.
    /// Keeping this call beside the frame-size constants prevents a future decoder/output
    /// mismatch from silently consuming partial frames.
    /// </summary>
    internal static IReadOnlyList<string> BuildVideoFfmpegArguments(
        string streamUrl,
        string? lanToken,
        bool? useHardwareAcceleration = null)
        => MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            streamUrl,
            lanToken,
            width: FrameWidth,
            height: FrameHeight,
            useHardwareAcceleration: useHardwareAcceleration);

    /// <summary>
    /// Selects the board's shared-session ladder. The service-owned HEVC/H.264 HTTP
    /// representations remain authoritative for landing tiles; they provide replayable
    /// initialization and avoid one direct RTSP connection per tile competing with
    /// recording and other viewers.
    /// </summary>
    internal static IReadOnlyList<string> SelectBoardStreamUrls(
        LiveMediaManifest manifest,
        bool preferDirectRtsp = false,
        bool directRtspFirst = false)
        => MainWindowViewModel.SelectDesktopStreamUrls(manifest, preferDirectRtsp, directRtspFirst);

    /// <summary>
    /// Treats a decoder that is still alive but has stopped delivering frames as stalled.
    /// Process liveness alone is insufficient: ffmpeg can keep its RTSP socket open while
    /// the camera sends no decodable media, which otherwise leaves the last bitmap frozen
    /// indefinitely and prevents the snapshot repair path from running.
    /// </summary>
    internal static bool ShouldUseSnapshotFallback(
        bool videoProcessActive,
        long lastFrameUtcTicks,
        long videoStartedUtcTicks,
        long nowUtcTicks,
        TimeSpan maxFrameAge)
    {
        if (!videoProcessActive)
        {
            return true;
        }

        if (maxFrameAge <= TimeSpan.Zero)
        {
            return false;
        }

        var referenceTicks = lastFrameUtcTicks > 0 ? lastFrameUtcTicks : videoStartedUtcTicks;
        return referenceTicks > 0 && nowUtcTicks - referenceTicks > maxFrameAge.Ticks;
    }

    /// <summary>
    /// Rejects a snapshot response that began before a newer decoder generation. This closes
    /// the reconnect race where an old HTTP snapshot arrives after live video has resumed.
    /// </summary>
    internal static bool ShouldApplySnapshot(
        bool videoProcessActive,
        int snapshotGeneration,
        int currentVideoGeneration)
        => !videoProcessActive && snapshotGeneration == currentVideoGeneration;

    private bool IsVideoProcessActive()
    {
        try
        {
            // Keep snapshots out of the image binding while the decoder task is reconnecting.
            // During a process restart _videoProcess is briefly null, but the task is still
            // the authoritative owner of the live surface.
            return _videoProcess is { HasExited: false }
                || _videoTask is { IsCompleted: false };
        }
        catch (InvalidOperationException)
        {
            return _videoTask is { IsCompleted: false };
        }
    }

    private async Task RunVideoLoopAsync(IReadOnlyList<string> streamUrls, CancellationToken cancellationToken)
    {
        var streamIndex = 0;
        var consecutiveFailures = 0;
        var useHardwareAcceleration = MainWindowViewModel.IsHardwareAccelerationEnabled();
        var activeStreamUrls = streamUrls.ToList();
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            // Each negotiated representation/reconnect is a new generation. Snapshot
            // responses captured before this point must never overwrite its frames.
            Interlocked.Increment(ref _videoGeneration);
            var frameGeneration = Volatile.Read(ref _videoGeneration);
            Process? process = null;
            var attemptHardwareAcceleration = useHardwareAcceleration;
            var attemptWatch = Stopwatch.StartNew();
            var processExited = false;
            Volatile.Write(ref _attemptRenderedFrame, 0);

            try
            {
                var streamUrl = activeStreamUrls[streamIndex];
                var ffmpeg = MainWindowViewModel.ResolveFfmpegPath();
                if (ffmpeg is null)
                {
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in BuildVideoFfmpegArguments(
                    streamUrl,
                    _api.LanToken,
                    attemptHardwareAcceleration))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg failed to start.");
                }
                _ = Task.Run(async () =>
                {
                    try { await process.StandardError.ReadToEndAsync(); }
                    catch { }
                });
                _videoProcess = process;
                var attemptStartedAt = DateTimeOffset.UtcNow;
                // A reconnect is a new freshness epoch. Do not let the last frame from
                // the previous ffmpeg process suppress snapshot recovery for this one.
                Interlocked.Exchange(ref _lastFrameUtcTicks, 0);
                Interlocked.Exchange(ref _videoStartedUtcTicks, DateTime.UtcNow.Ticks);

                // A coalescing mailbox keeps only the newest pending frame. The previous
                // two-slot approach queued two expensive UI copies and then discarded every
                // decoded frame until both callbacks drained, which can look like a
                // 2–5-second slideshow under three-camera/fullscreen load.
                var mailbox = new LatestFrameMailbox(
                    FrameSize,
                    // Render priority keeps frame presentation ahead of ordinary binding/layout
                    // work without allowing a queued frame to starve input or window chrome.
                    callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Render),
                    frameBytes => RenderFrame(frameBytes, frameGeneration));
                try
                {
                    var discardBuffer = new byte[FrameSize];
                var fpsWatch = Stopwatch.StartNew();
                long frameCount = 0;
                while (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    if (MainWindowViewModel.ShouldReportNoFirstFrame(
                            processActive: !HasExitedSafely(process),
                            renderedFrame: Volatile.Read(ref _attemptRenderedFrame) != 0,
                            startedAt: attemptStartedAt,
                            now: DateTimeOffset.UtcNow,
                            // A 5s window is too short for the shared HEVC fMP4 session:
                            // the server-side session needs a few seconds to start and the
                            // first keyframe to arrive over the camera Wi-Fi. Killing the
                            // decoder at 5s made tiles cycle reconnect forever (observed as
                            // the .29/.169 frozen tiles). 15s covers slow session startup
                            // while a genuinely dead source is still caught quickly.
                            timeout: TimeSpan.FromSeconds(15)))
                    {
                        // Do not leave an alive-but-silent ffmpeg process holding the tile
                        // forever. The outer loop tears it down and advances/retries the
                        // service-owned ladder; the snapshot watchdog can bridge the gap.
                        break;
                    }

                    if (!mailbox.TryAcquire(out var renderSlot, out var frame))
                    {
                        // Keep draining stdout so ffmpeg does not stall behind a full pipe.
                        if (!await MainWindowViewModel.ReadExactAsync(process.StandardOutput.BaseStream, discardBuffer, cancellationToken))
                        {
                            // Invalidate the queued callback before leaving the mailbox
                            // scope; its dispatcher callback may already be waiting.
                            Interlocked.Increment(ref _videoGeneration);
                            break;
                        }
                        continue;
                    }

                    if (!await MainWindowViewModel.ReadExactAsync(
                            process.StandardOutput.BaseStream,
                            frame,
                            cancellationToken))
                    {
                        mailbox.Release(renderSlot);
                        // Invalidate before the mailbox is disposed so a queued callback
                        // cannot present a frame from the dead/stalled attempt.
                        Interlocked.Increment(ref _videoGeneration);
                        break;
                    }

                    mailbox.Publish(renderSlot);

                    frameCount++;
                    if (fpsWatch.ElapsedMilliseconds >= 1000)
                    {
                        var renderedFps =
                            $"{Interlocked.Exchange(ref _renderedFrameCount, 0) * 1000.0 / fpsWatch.ElapsedMilliseconds:0} fps rendered";
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (MainWindowViewModel.ShouldRenderFrame(
                                    _disposed,
                                    frameGeneration,
                                    Volatile.Read(ref _videoGeneration)))
                            {
                                FpsText = renderedFps;
                            }
                        });
                        frameCount = 0;
                        fpsWatch.Restart();
                    }
                    }
                }
                finally
                {
                    // Invalidate before mailbox disposal: a render-priority callback can
                    // already be queued when the decoder stalls or is cancelled.
                    Interlocked.Increment(ref _videoGeneration);
                    mailbox.Dispose();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Fall through to the next negotiated representation / snapshot fallback.
                System.Diagnostics.Debug.WriteLine($"Tile video loop failed for {DisplayName}: {ex.Message}");
            }
            finally
            {
                processExited = HasExitedSafely(process);
                if (ReferenceEquals(_videoProcess, process))
                {
                    _videoProcess = null;
                    Interlocked.Exchange(ref _videoStartedUtcTicks, 0);
                }
                await MainWindowViewModel.StopProcessSafelyAsync(process);
            }

            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                if (MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
                        attemptHardwareAcceleration,
                        processExited,
                        Volatile.Read(ref _attemptRenderedFrame) != 0,
                        attemptWatch.Elapsed))
                {
                    // Keep the same source and retry once without hardware acceleration;
                    // a driver/VAAPI negotiation failure must not be mistaken for a dead
                    // camera and needlessly advance to a lower-quality representation.
                    useHardwareAcceleration = false;
                }
                else
                {
                    streamIndex = MainWindowViewModel.NextDesktopStreamIndex(streamIndex, activeStreamUrls.Count);
                    // If this is the only source, retain software mode after a driver
                    // failure so every reconnect does not repeat the same broken hardware
                    // initialization. With a real ladder, the next representation gets a
                    // fresh hardware attempt.
                    useHardwareAcceleration = activeStreamUrls.Count > 1
                        && MainWindowViewModel.IsHardwareAccelerationEnabled();
                }

                consecutiveFailures++;
                if (consecutiveFailures >= Math.Max(2, activeStreamUrls.Count))
                {
                    // A manifest URL can become stale when the service restarts its shared
                    // session. Refresh the negotiated ladder instead of cycling dead URLs
                    // forever; this is also what lets a recovered 10.0.0.29 re-enter live
                    // playback without recreating the landing board.
                    try
                    {
                        var refreshed = await _api.GetLiveManifestAsync(Device.Id, "main");
                        // Emergency rung: after the shared-session ladder failed repeatedly,
                        // put the camera's direct RTSP first — the recorder proves these
                        // cameras stream direct RTSP reliably, and a stalled service session
                        // must not strand a tile that the camera itself can still feed.
                        var refreshedUrls = refreshed is null ? [] : SelectBoardStreamUrls(refreshed, directRtspFirst: true);
                        if (refreshedUrls.Count > 0)
                        {
                            activeStreamUrls = refreshedUrls.ToList();
                            streamIndex = 0;
                            consecutiveFailures = 0;
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine($"Tile manifest refresh failed for {DisplayName}: {ex.Message}");
                    }
                }
                // Back off after repeated decoder/session failures. The cap keeps a dead
                // camera from creating an ffmpeg/HTTP storm while still retrying quickly
                // enough for a short Wi-Fi or service interruption.
                await Task.Delay(
                    MainWindowViewModel.GetReconnectDelay(consecutiveFailures - 1),
                    cancellationToken);
            }
        }
        var endedGeneration = Volatile.Read(ref _videoGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (MainWindowViewModel.ShouldRenderFrame(
                    _disposed,
                    endedGeneration,
                    Volatile.Read(ref _videoGeneration)))
            {
                FpsText = null;
            }
        });
    }

    private static bool HasExitedSafely(Process? process)
    {
        try
        {
            return process is null || process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private void RenderFrame(byte[] frame, int frameGeneration)
    {
        if (!MainWindowViewModel.ShouldRenderFrame(
                _disposed,
                frameGeneration,
                Volatile.Read(ref _videoGeneration)))
        {
            return;
        }

        try
        {
            // Snapshot fallback may have installed a plain, normalized Bitmap while the
            // decoder was stalled. Recreate the writable surface on the first resumed video
            // frame; otherwise frames would be decoded but never displayed.
            if (LiveFrame is not WriteableBitmap wb)
            {
                wb = new WriteableBitmap(
                    new global::Avalonia.PixelSize(FrameWidth, FrameHeight),
                    new global::Avalonia.Vector(96, 96),
                    global::Avalonia.Platform.PixelFormat.Bgra8888,
                    global::Avalonia.Platform.AlphaFormat.Opaque);
                var previous = LiveFrame;
                LiveFrame = wb;
                if (previous is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            using (var locked = wb.Lock())
                {
                    MainWindowViewModel.CopyBgraFrameToFramebuffer(
                        frame,
                        locked.Address,
                        locked.RowBytes,
                        FrameWidth,
                        FrameHeight);
            }
            // Lock/Unlock updates the backing store; explicitly raise the binding
            // notification as well so every renderer backend schedules a fresh draw.
            OnPropertyChanged(nameof(LiveFrame));
            Interlocked.Increment(ref _renderedFrameCount);
            Volatile.Write(ref _attemptRenderedFrame, 1);
            Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
            if (Interlocked.Exchange(ref _snapshotFailures, 0) != 0)
            {
                OnPropertyChanged(nameof(TileStateText));
            }
        }
        catch (Exception ex)
        {
            // Keep the decoder alive, but leave an actionable trace instead of turning a
            // stride/pixel-format failure into an unexplained blank tile.
            System.Diagnostics.Debug.WriteLine($"Tile frame render failed for {DisplayName}: {ex}");
        }
    }

    // ── Snapshot fallback / offline badge ─────────────────────────

    /// <summary>
    /// Applies a JPEG fallback without replacing the image object bound to Avalonia. The
    /// decoded image is aspect-fit onto an exact 960×540 render target with black bars,
    /// then copied into the stable writable framebuffer used by live video.
    /// </summary>
    private void ApplySnapshotToSurface(byte[] bytes, int snapshotGeneration)
    {
        if (_disposed || snapshotGeneration != Volatile.Read(ref _videoGeneration))
        {
            return;
        }

        using var ms = new MemoryStream(bytes);
        using var decoded = Bitmap.DecodeToWidth(ms, FrameWidth, BitmapInterpolationMode.HighQuality);
        using var normalized = new RenderTargetBitmap(
            new global::Avalonia.PixelSize(FrameWidth, FrameHeight),
            new global::Avalonia.Vector(96, 96));
        if (_disposed || snapshotGeneration != Volatile.Read(ref _videoGeneration))
        {
            return;
        }

        using (var context = normalized.CreateDrawingContext())
        {
            var surface = new global::Avalonia.Rect(0, 0, FrameWidth, FrameHeight);
            context.DrawRectangle(Brushes.Black, null, surface);

            var sourceWidth = decoded.PixelSize.Width;
            var sourceHeight = decoded.PixelSize.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return;
            }

            var scale = Math.Min(
                (double)FrameWidth / sourceWidth,
                (double)FrameHeight / sourceHeight);
            var targetWidth = sourceWidth * scale;
            var targetHeight = sourceHeight * scale;
            var target = new global::Avalonia.Rect(
                (FrameWidth - targetWidth) / 2,
                (FrameHeight - targetHeight) / 2,
                targetWidth,
                targetHeight);
            context.DrawImage(
                decoded,
                new global::Avalonia.Rect(0, 0, sourceWidth, sourceHeight),
                target);
        }

        if (_disposed || snapshotGeneration != Volatile.Read(ref _videoGeneration))
        {
            return;
        }

        if (LiveFrame is not WriteableBitmap writable)
        {
            writable = new WriteableBitmap(
                new global::Avalonia.PixelSize(FrameWidth, FrameHeight),
                new global::Avalonia.Vector(96, 96),
                global::Avalonia.Platform.PixelFormat.Bgra8888,
                global::Avalonia.Platform.AlphaFormat.Opaque);
            LiveFrame = writable;
        }

        using var locked = writable.Lock();
        normalized.CopyPixels(locked, global::Avalonia.Platform.AlphaFormat.Opaque);
        OnPropertyChanged(nameof(LiveFrame));
    }

    private async Task PollSnapshotAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _snapshotPollInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Live video is flowing — skip the snapshot fetch entirely (no redundant load).
            // A live process is not enough evidence: ffmpeg can remain alive while its RTSP
            // input is silent. Allow the fallback after a bounded frame-age window so a frozen
            // tile gets a fresh picture instead of retaining stale imagery indefinitely.
            const long maxFrameAgeTicks = 5 * TimeSpan.TicksPerSecond;
            var nowTicks = DateTime.UtcNow.Ticks;
            var videoActive = IsVideoProcessActive();
            var lastTicks = Interlocked.Read(ref _lastFrameUtcTicks);
            var startedTicks = Interlocked.Read(ref _videoStartedUtcTicks);
            if (!ShouldUseSnapshotFallback(
                    videoActive,
                    lastTicks,
                    startedTicks,
                    nowTicks,
                    TimeSpan.FromTicks(maxFrameAgeTicks)))
            {
                return;
            }

            // Capture the generation before awaiting the HTTP request. A new video worker
            // invalidates this response even if the process is between reconnect attempts.
            var snapshotGeneration = Volatile.Read(ref _videoGeneration);
            var bytes = await _api.GetSnapshotAsync(Device.Id);
            if (bytes is { Length: > 100 })
            {
                // The decoder can start between the worker-thread check and the HTTP response.
                // Re-check both process activity and generation immediately before replacing
                // the bound image, so a late snapshot can never overwrite live video.
                Dispatcher.UIThread.Post(() =>
                {
                    var currentNowTicks = DateTime.UtcNow.Ticks;
                    if (_disposed
                        || !ShouldUseSnapshotFallback(
                            IsVideoProcessActive(),
                            Interlocked.Read(ref _lastFrameUtcTicks),
                            Interlocked.Read(ref _videoStartedUtcTicks),
                            currentNowTicks,
                            TimeSpan.FromTicks(maxFrameAgeTicks))
                        || snapshotGeneration != Volatile.Read(ref _videoGeneration))
                    {
                        return;
                    }

                    var wasOffline = TileStateText is not null;
                    Interlocked.Exchange(ref _snapshotFailures, 0);
                    if (wasOffline)
                    {
                        OnPropertyChanged(nameof(TileStateText));
                    }
                    // Keep one stable 960×540 writable surface for both video and
                    // snapshots. Replacing Bitmap objects here made Avalonia's compositor
                    // race disposal and also let 4:3 snapshots change the apparent tile
                    // geometry. Decode once, letterbox onto the fixed surface, then copy
                    // pixels into that same framebuffer.
                    ApplySnapshotToSurface(bytes, snapshotGeneration);
                });
            }
            else
            {
                // Camera not answering: keep counting so the tile shows its offline badge.
                // The snapshot layer stays dark rather than showing a stale frame.
                var failures = Interlocked.Increment(ref _snapshotFailures);
                if (failures == 3)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_disposed) OnPropertyChanged(nameof(TileStateText));
                    });
                }
            }
        }
        finally
        {
            Volatile.Write(ref _snapshotPollInProgress, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetimeCts.Cancel();
        _shell.StarsChanged -= OnStarsChanged;
        _snapshotTimer?.Dispose();
        _snapshotTimer = null;

        var cts = Interlocked.Exchange(ref _videoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _videoProcess, null);
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                // Already stopped.
            }
        }
        var task = Interlocked.Exchange(ref _videoTask, null);
        var startup = Interlocked.Exchange(ref _startupTask, null);
        if (task is not null || startup is not null || cts is not null)
        {
            _ = Task.Run(async () =>
            {
                // The synchronous close path has already cancelled and killed the decoder.
                // Finish cleanup in the background without disposing the lifetime source
                // while StartVideoAsync may still be reading its captured token. This avoids
                // both the old CTS race and the old leak when the three-second UI wait timed
                // out: cancellation should make these tasks complete promptly, after which
                // disposal is always performed.
                try
                {
                    if (startup is not null)
                    {
                        await startup.ConfigureAwait(false);
                    }
                    if (task is not null)
                    {
                        await task.ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    // Cancellation and an already-closing decoder are expected during teardown.
                }
                finally
                {
                    cts?.Dispose();
                    _lifetimeCts.Dispose();
                    process?.Dispose();
                }
            });
        }
        else
        {
            process?.Dispose();
            _lifetimeCts.Dispose();
        }
    }
}
