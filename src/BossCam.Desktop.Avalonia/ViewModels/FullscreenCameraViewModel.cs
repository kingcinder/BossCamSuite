using System.Diagnostics;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Immersive fullscreen camera view — the desktop mirror of the SPA's CameraFullscreen.
/// Owns its own ffmpeg decode loop (HD main stream), the spacebar audio toggle (a
/// dedicated ffplay audio-only process so the video pipe stays rawvideo), the single-click
/// option banner, and the menu sheet of camera option tiles. Double-click exits; Escape /
/// Backspace / a second slow click dismiss the banner; spacebar toggles audio.
/// </summary>
public sealed partial class FullscreenCameraViewModel : ObservableObject, IDisposable
{
    private readonly IBossCamApiClient _api;
    private readonly ILogger _logger;
    private readonly string? _lanToken;
    private readonly MainWindowViewModel? _shell;
    private CancellationTokenSource? _videoCts;
    private Task? _videoTask;
    private Task? _startupTask;
    private Process? _videoProcess;
    private Process? _audioProcess;
    private Timer? _snapshotTimer;
    private int _snapshotPollInProgress;
    private long _lastFrameUtcTicks;
    private long _videoStartedUtcTicks;
    private long _renderedFrameCount;
    private int _attemptRenderedFrame;
    private int _videoGeneration;
    private bool _disposed;
    // Serializes StartAsync with teardown. Without this gate, Dispose could observe no
    // startup task between StartAsync's initial stop and task registration, dispose the
    // lifetime CTS, and leave fullscreen blank on the next continuation.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    public FullscreenCameraViewModel(
        IBossCamApiClient api,
        DeviceIdentity device,
        string? lanToken,
        MainWindowViewModel? shell = null,
        ILogger<FullscreenCameraViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(device);
        _api = api;
        Device = device;
        _lanToken = lanToken;
        _shell = shell;
        _logger = logger ?? NullLogger<FullscreenCameraViewModel>.Instance;
        // The menu sheet embeds the real camera-options surface (typed control points,
        // mirroring the SPA's panel tiles). Built only when a shell is available so tests
        // can construct the VM standalone.
        if (shell is not null)
        {
            Features = new FeaturesViewModel(api, shell);
        }
        // Snapshot fallback: the board tiles can bridge a stalled decoder with fresh
        // snapshots, but fullscreen had none — a silent RTSP/HEVC stall left the window
        // permanently black. Poll snapshots at the same cadence and letterbox them onto
        // the framebuffer whenever video has not delivered a frame recently.
        _snapshotTimer = new Timer(async _ => await PollSnapshotAsync(), null, 3000, 3000);
    }

    public DeviceIdentity Device { get; }

    /// <summary>Embedded camera-options surface shown in the menu sheet (Features control points).</summary>
    public FeaturesViewModel? Features { get; }

    public string DisplayName => Device.DisplayName ?? Device.IpAddress ?? Device.Id.ToString();

    public string Subtitle => $"{Device.HardwareModel ?? "Camera"} · {Device.IpAddress ?? "—"}";

    [ObservableProperty]
    private WriteableBitmap? _liveVideoFrame;

    [ObservableProperty]
    private string _playbackStatus = "Starting stream…";

    [ObservableProperty]
    private bool _isAudioOn;

    [ObservableProperty]
    private string _audioStatus = "Audio muted — press Space";

    /// <summary>Button label for the in-sheet audio toggle (mirrors the audio status).</summary>
    public string AudioToggleLabel => IsAudioOn ? "Mute (Space)" : "Unmute (Space)";

    partial void OnIsAudioOnChanged(bool value)
    {
        OnPropertyChanged(nameof(AudioToggleLabel));
    }

    [ObservableProperty]
    private bool _bannerVisible;

    /// <summary>Open menu tile key, or null when the menu sheet is hidden.</summary>
    [ObservableProperty]
    private string? _activeMenu;

    partial void OnActiveMenuChanged(string? value)
    {
        // The menu sheet body switches with the active tile: Network/Record/Advanced/
        // Firmware embed the shell's real section surfaces, Features/Settings embed the
        // typed control points, and Display/Audio/Hotspot/Recovery render dedicated
        // panels off this VM (see the window's DataTemplates).
        OnPropertyChanged(nameof(MenuContent));
        OnPropertyChanged(nameof(IsDisplayMenu));
        OnPropertyChanged(nameof(IsAudioMenu));
        OnPropertyChanged(nameof(IsHotspotMenu));
        OnPropertyChanged(nameof(IsRecoveryMenu));
    }

    /// <summary>Body content for the currently open menu tile (per-tile, mirrors the SPA).</summary>
    public object? MenuContent => ActiveMenu switch
    {
        "Network" => _shell?.ConnectivitySection,
        "Record" => _shell?.RecordingsSection,
        "Advanced" => _shell?.DiagnosticsSection,
        "Firmware" => _shell?.FirmwareSection,
        "Features" or "Settings" => Features,
        // Display / Audio / Hotspot / Recovery render from this VM itself via the
        // DataTemplate that switches on Is*Menu (dedicated panels, never Features).
        _ => this
    };

    public bool IsDisplayMenu => ActiveMenu == "Display";
    public bool IsAudioMenu => ActiveMenu == "Audio";
    public bool IsHotspotMenu => ActiveMenu == "Hotspot";
    public bool IsRecoveryMenu => ActiveMenu == "Recovery";

    /// <summary>Display/Audio/Network/Hotspot/Features/Settings/Record/Advanced/Firmware/Recovery tiles.</summary>
    public IReadOnlyList<FullscreenMenuTile> MenuTiles { get; } =
    [
        new("Display", "🖥"),
        new("Audio", "🔊"),
        new("Network", "🌐"),
        new("Hotspot", "📶"),
        new("Features", "🎛"),
        new("Settings", "⚙"),
        new("Record", "⏺"),
        new("Advanced", "🧪"),
        new("Firmware", "📦"),
        new("Recovery", "🚑")
    ];

    public string MenuHeading => ActiveMenu is null ? string.Empty : $"{ActiveMenu} options";

    /// <summary>Starts the HD-main decode loop. Call once after the window is shown.</summary>
    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            // Stop the prior decoder before registering this startup task. StartVideoAsync
            // itself must not await the task that is currently executing it during its own
            // initial StopVideoAsync call. Holding the lifecycle gate makes task registration
            // atomic with respect to Dispose/TeardownAsync.
            await StopVideoAsync().ConfigureAwait(false);
            if (_disposed)
            {
                return;
            }

            var startup = StartVideoAsync();
            Volatile.Write(ref _startupTask, startup);
            try
            {
                await startup.ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(Volatile.Read(ref _startupTask), startup))
                {
                    Volatile.Write(ref _startupTask, null);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // ── Video (own decode loop at HD main quality) ─────────────────

    private async Task StartVideoAsync()
    {
        var lifetimeToken = _lifetimeCts.Token;
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
                // A transient service/manifest failure should consume one bounded retry,
                // not abandon fullscreen with a permanent blank view.
                _logger.LogDebug(ex, "Fullscreen manifest attempt {Attempt} failed", attempt + 1);
                manifest = null;
            }

            if (manifest is not null)
            {
                break;
            }

            if (MainWindowViewModel.ShouldRetryLiveManifest(attempt + 1, _disposed))
            {
                SetPlaybackStatus($"Live manifest unavailable; retrying ({attempt + 1}/3)…");
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

        // Fullscreen uses the same service-owned shared HTTP ladder as the landing board.
        // Do not open another direct RTSP socket here: a board tile + fullscreen + recording
        // can otherwise exhaust the 5523-W session budget and freeze other viewers. The
        // shared HEVC copy preserves native codec/resolution while the service owns retries.
        var streamUrls = manifest is null
            ? []
            : MainWindowViewModel.SelectDesktopStreamUrls(manifest, preferDirectRtsp: false);
        if (streamUrls.Count == 0)
        {
            SetPlaybackStatus("Live manifest unavailable.");
            return;
        }

        var cts = new CancellationTokenSource();
        _videoCts = cts;
        _videoTask = Task.Run(() => RunVideoLoopAsync(streamUrls, cts.Token));
    }

    private async Task RunVideoLoopAsync(IReadOnlyList<string> streamUrls, CancellationToken cancellationToken)
    {
        var streamIndex = 0;
        var consecutiveFailures = 0;
        var useHardwareAcceleration = MainWindowViewModel.IsHardwareAccelerationEnabled();
        var activeStreamUrls = streamUrls.ToList();
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            // Each reconnect gets a new generation. A render-priority callback from the
            // previous ffmpeg process may still be queued when that process exits; the
            // generation check below prevents it from replacing a fresh frame.
            Interlocked.Increment(ref _videoGeneration);
            var frameGeneration = Volatile.Read(ref _videoGeneration);
            Process? process = null;
            var attemptHardwareAcceleration = useHardwareAcceleration;
            var attemptWatch = System.Diagnostics.Stopwatch.StartNew();
            var processExited = false;
            Volatile.Write(ref _attemptRenderedFrame, 0);
            try
            {
                var streamUrl = activeStreamUrls[streamIndex];
                SetPlaybackStatus($"Connecting {DescribeStream(streamUrl)} {streamIndex + 1}/{streamUrls.Count}…", frameGeneration);
                var ffmpeg = ResolveFfmpegPath();
                if (ffmpeg is null)
                {
                    SetPlaybackStatus("FFmpeg unavailable.", frameGeneration);
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
                foreach (var arg in MainWindowViewModel.BuildLiveVideoFfmpegArguments(
                    streamUrl,
                    _lanToken,
                    width: 1920,
                    height: 1080,
                    useHardwareAcceleration: attemptHardwareAcceleration))
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
                // A reconnect is a new freshness epoch. Do not let the last frame from the
                // previous ffmpeg process suppress snapshot recovery for this one.
                Interlocked.Exchange(ref _lastFrameUtcTicks, 0);
                Interlocked.Exchange(ref _videoStartedUtcTicks, DateTime.UtcNow.Ticks);
                SetPlaybackStatus($"Playing {DescribeStream(streamUrl)} {streamIndex + 1}/{streamUrls.Count}", frameGeneration);

                // Fullscreen decodes at the camera's native HD (1920x1080) — the sharpest
                // view, with -threads 0 letting ffmpeg use every core for HEVC decode.
                const int width = 1920;
                const int height = 1080;
                var frameSize = width * height * 4;
                // A coalescing mailbox keeps only the newest pending frame. The old
                // two-slot queue could leave two full 1920x1080 copies queued on Avalonia,
                // then discard every decoded frame until they drained — the direct cause of
                // slideshow-like playback under load.
                var mailbox = new LatestFrameMailbox(
                    frameSize,
                    // Match the legacy desktop renderer: present frames at render priority so
                    // decode bursts do not sit behind normal binding/layout work.
                    callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Render),
                    frameBytes => RenderFrame(frameBytes, width, height, frameGeneration));
                try
                {
                    var discardBuffer = new byte[frameSize];
                var fpsClock = System.Diagnostics.Stopwatch.StartNew();
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (MainWindowViewModel.ShouldReportNoFirstFrame(
                            processActive: !HasExitedSafely(process),
                            renderedFrame: Volatile.Read(ref _attemptRenderedFrame) != 0,
                            startedAt: attemptStartedAt,
                            now: DateTimeOffset.UtcNow,
                            // The shared HEVC fMP4 session needs a few seconds to start and
                            // the first keyframe to arrive over camera Wi-Fi; 5s made the
                            // fullscreen window tear down before its first frame and loop
                            // reconnect forever (permanently black). 15s covers slow session
                            // startup while a dead source is still caught promptly.
                            timeout: TimeSpan.FromSeconds(15)))
                    {
                        SetPlaybackStatus("No video frames received; reconnecting…", frameGeneration);
                        // Tear down an alive-but-silent decoder so the outer loop advances
                        // to the next service-owned representation instead of leaving a
                        // blank fullscreen window indefinitely.
                        break;
                    }

                    if (!mailbox.TryAcquire(out var renderSlot, out var frame))
                    {
                        if (!await MainWindowViewModel.ReadExactAsync(process.StandardOutput.BaseStream, discardBuffer, cancellationToken))
                        {
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
                        break;
                    }

                    mailbox.Publish(renderSlot);

                    if (fpsClock.ElapsedMilliseconds >= 1000)
                    {
                        var renderedFps =
                            $"Playing {DescribeStream(streamUrl)} {streamIndex + 1}/{streamUrls.Count} · "
                            + $"{Interlocked.Exchange(ref _renderedFrameCount, 0) * 1000.0 / fpsClock.ElapsedMilliseconds:0} fps rendered";
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (MainWindowViewModel.ShouldRenderFrame(
                                    _disposed,
                                    frameGeneration,
                                    Volatile.Read(ref _videoGeneration)))
                            {
                                PlaybackStatus = renderedFps;
                            }
                        });
                        fpsClock.Restart();
                    }
                }
                }
                finally
                {
                    // Invalidate before mailbox disposal: a queued render callback from
                    // this attempt must not replace the first frame of the next source.
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
                SetPlaybackStatus($"Playback reconnecting: {ex.Message}");
            }
            finally
            {
                // Invalidate any callback still queued for this attempt before the mailbox
                // is disposed and the next transport representation starts.
                Interlocked.Increment(ref _videoGeneration);
                processExited = HasExitedSafely(process);
                if (ReferenceEquals(_videoProcess, process))
                {
                    _videoProcess = null;
                }
                await StopProcessSafelyAsync(process);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                if (MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
                        attemptHardwareAcceleration,
                        processExited,
                        Volatile.Read(ref _attemptRenderedFrame) != 0,
                        attemptWatch.Elapsed))
                {
                    // A local hardware-frame negotiation failure should retry this same
                    // high-quality source in software before trying a lower representation.
                    useHardwareAcceleration = false;
                }
                else
                {
                    streamIndex = MainWindowViewModel.NextDesktopStreamIndex(streamIndex, activeStreamUrls.Count);
                    // Keep software mode for a single-source ladder after a local
                    // hardware failure; multi-source ladders can try hardware on the next
                    // representation while preserving the configured escape hatch.
                    useHardwareAcceleration = activeStreamUrls.Count > 1
                        && MainWindowViewModel.IsHardwareAccelerationEnabled();
                }

                consecutiveFailures++;
                if (consecutiveFailures >= Math.Max(2, activeStreamUrls.Count))
                {
                    // Refresh after the negotiated service session has failed across the
                    // current ladder. This avoids cycling stale URLs forever after the
                    // service restarts its shared camera session.
                    try
                    {
                        var refreshed = await _api.GetLiveManifestAsync(Device.Id, Quality);
                        // Emergency rung after repeated shared-session failures: put the
                        // camera's direct RTSP first (the recorder proves it streams fine).
                        var refreshedUrls = refreshed is null
                            ? []
                            : MainWindowViewModel.SelectDesktopStreamUrls(refreshed, directRtspFirst: true);
                        if (refreshedUrls.Count > 0)
                        {
                            activeStreamUrls = refreshedUrls.ToList();
                            streamIndex = 0;
                            consecutiveFailures = 0;
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, "Fullscreen manifest refresh failed");
                    }
                }
                // Back off after repeated decoder/session failures. The cap prevents a
                // dead camera from creating continuous ffmpeg/HTTP churn while preserving
                // fast recovery for brief Wi-Fi or service interruptions.
                await Task.Delay(
                    MainWindowViewModel.GetReconnectDelay(consecutiveFailures - 1),
                    cancellationToken);
            }
        }
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

    private void RenderFrame(byte[] frame, int width, int height, int frameGeneration)
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
            LiveVideoFrame ??= new WriteableBitmap(
                new global::Avalonia.PixelSize(width, height),
                new global::Avalonia.Vector(96, 96),
                global::Avalonia.Platform.PixelFormat.Bgra8888,
                global::Avalonia.Platform.AlphaFormat.Opaque);
            using (var locked = LiveVideoFrame.Lock())
            {
                MainWindowViewModel.CopyBgraFrameToFramebuffer(
                    frame,
                    locked.Address,
                    locked.RowBytes,
                    width,
                    height);
            }
            // Notify only after the framebuffer is unlocked; this avoids blank frames on
            // renderers that schedule the image draw immediately from PropertyChanged.
            OnPropertyChanged(nameof(LiveVideoFrame));
            Interlocked.Increment(ref _renderedFrameCount);
            Volatile.Write(ref _attemptRenderedFrame, 1);
            Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
        }
        catch (Exception ex)
        {
            // Keep reconnecting, but make framebuffer/pixel-format failures diagnosable
            // instead of silently presenting a blank fullscreen view.
            _logger.LogDebug(ex, "Fullscreen frame render failed");
        }
    }

    /// <summary>
    /// Marshals decoder status to Avalonia's UI thread. The decode loop runs on a worker
    /// so a status assignment must not race the bound TextBlock or the fullscreen window.
    /// </summary>
    private void SetPlaybackStatus(string value, int? frameGeneration = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (frameGeneration is null
                ? !_disposed
                : MainWindowViewModel.ShouldRenderFrame(
                    _disposed,
                    frameGeneration.Value,
                    Volatile.Read(ref _videoGeneration)))
            {
                PlaybackStatus = value;
            }
        });
    }

    // ── Snapshot fallback (never a permanently black fullscreen) ───

    /// <summary>
    /// Applies a JPEG snapshot to the fullscreen framebuffer whenever live video has not
    /// delivered a frame recently (decoder stall, silent RTSP, slow session startup). The
    /// image is aspect-fit onto the 1920×1080 writable surface with black bars — the same
    /// letterbox the video pipe uses — so the window always shows something current.
    /// </summary>
    private async Task PollSnapshotAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _snapshotPollInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Same freshness gate as the board tiles: fall back when the decoder is not
            // running at all, or when it has not delivered a frame within the bounded
            // window. An alive-but-silent ffmpeg process must not suppress the fallback.
            if (!BoardTileViewModel.ShouldUseSnapshotFallback(
                    videoProcessActive: IsVideoProcessActive(),
                    lastFrameUtcTicks: Interlocked.Read(ref _lastFrameUtcTicks),
                    videoStartedUtcTicks: Interlocked.Read(ref _videoStartedUtcTicks),
                    nowUtcTicks: DateTime.UtcNow.Ticks,
                    maxFrameAge: TimeSpan.FromSeconds(6)))
            {
                return;
            }

            // Capture the generation before awaiting the HTTP request so a late response
            // cannot overwrite a frame from a newer decoder session.
            var snapshotGeneration = Volatile.Read(ref _videoGeneration);
            var bytes = await _api.GetSnapshotAsync(Device.Id);
            if (bytes is not { Length: > 100 })
            {
                return;
            }

            // The fullscreen window may not own a UI dispatcher yet during teardown; guard
            // the post so a snapshot never escapes the poll loop as an unobserved fault.
            try
            {
                Dispatcher.UIThread.Post(() =>
            {
                // Re-check freshness inside the post, not just the decoder generation: a
                // running ffmpeg process that resumes delivering frames does NOT bump the
                // generation, so a JPEG fetched during the stall must not overwrite the
                // fresh live frame that just arrived (same race the board tile closes).
                if (_disposed
                    || snapshotGeneration != Volatile.Read(ref _videoGeneration)
                    || !BoardTileViewModel.ShouldUseSnapshotFallback(
                        videoProcessActive: IsVideoProcessActive(),
                        lastFrameUtcTicks: Interlocked.Read(ref _lastFrameUtcTicks),
                        videoStartedUtcTicks: Interlocked.Read(ref _videoStartedUtcTicks),
                        nowUtcTicks: DateTime.UtcNow.Ticks,
                        maxFrameAge: TimeSpan.FromSeconds(6)))
                {
                    return;
                }

                try
                {
                    using var ms = new MemoryStream(bytes);
                    using var decoded = Bitmap.DecodeToWidth(ms, 1920, BitmapInterpolationMode.HighQuality);
                    using var normalized = new RenderTargetBitmap(
                        new global::Avalonia.PixelSize(1920, 1080),
                        new global::Avalonia.Vector(96, 96));
                    using (var context = normalized.CreateDrawingContext())
                    {
                        var surface = new global::Avalonia.Rect(0, 0, 1920, 1080);
                        context.DrawRectangle(Brushes.Black, null, surface);

                        var sourceWidth = decoded.PixelSize.Width;
                        var sourceHeight = decoded.PixelSize.Height;
                        if (sourceWidth > 0 && sourceHeight > 0)
                        {
                            var scale = Math.Min(1920.0 / sourceWidth, 1080.0 / sourceHeight);
                            var targetWidth = sourceWidth * scale;
                            var targetHeight = sourceHeight * scale;
                            context.DrawImage(
                                decoded,
                                new global::Avalonia.Rect(0, 0, sourceWidth, sourceHeight),
                                new global::Avalonia.Rect(
                                    (1920 - targetWidth) / 2,
                                    (1080 - targetHeight) / 2,
                                    targetWidth,
                                    targetHeight));
                        }
                    }

                    LiveVideoFrame ??= new WriteableBitmap(
                        new global::Avalonia.PixelSize(1920, 1080),
                        new global::Avalonia.Vector(96, 96),
                        global::Avalonia.Platform.PixelFormat.Bgra8888,
                        global::Avalonia.Platform.AlphaFormat.Opaque);
                    using (var locked = LiveVideoFrame.Lock())
                    {
                        normalized.CopyPixels(locked, global::Avalonia.Platform.AlphaFormat.Opaque);
                    }
                    OnPropertyChanged(nameof(LiveVideoFrame));
                }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Fullscreen snapshot fallback failed");
                    }
                });
            }
            catch (Exception ex)
            {
                // A disposed/absent dispatcher during teardown is expected; never fault.
                _logger.LogDebug(ex, "Fullscreen snapshot UI post failed");
            }
        }
        catch (Exception ex)
        {
            // A throwing API client (tests, teardown) must never escape the timer callback.
            _logger.LogDebug(ex, "Fullscreen snapshot poll failed");
        }
        finally
        {
            Volatile.Write(ref _snapshotPollInProgress, 0);
        }
    }

    private bool IsVideoProcessActive()
    {
        try
        {
            return _videoProcess is { HasExited: false }
                || _videoTask is { IsCompleted: false };
        }
        catch (InvalidOperationException)
        {
            return _videoTask is { IsCompleted: false };
        }
    }

    // ── Audio (spacebar) ───────────────────────────────────────────

    /// <summary>
    /// Toggles audio output. The video pipe is rawvideo (no audio), so audio plays through
    /// a dedicated ffplay process on the same negotiated stream. Requires ffplay (ships
    /// with the ffmpeg suite); when it is missing the toggle reports a clear status.
    /// </summary>
    [RelayCommand]
    private async Task ToggleAudioAsync()
    {
        if (_audioProcess is { HasExited: false })
        {
            await StopAudioAsync();
            IsAudioOn = false;
            AudioStatus = "Audio muted — press Space";
            return;
        }

        var manifest = await _api.GetLiveManifestAsync(Device.Id, Quality);
        // Use the same negotiated direct/native source as video when available. This keeps
        // audio on the camera's real-time stream instead of attaching ffplay to a second
        // fragment-delayed compatibility session; the HTTP ladder remains the fallback.
        var streamUrl = manifest is null
            ? null
            : MainWindowViewModel.SelectDesktopStreamUrl(manifest, preferDirectRtsp: false);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            AudioStatus = "No audio stream available.";
            return;
        }

        var ffplay = ResolveFfplayPath();
        if (ffplay is null)
        {
            AudioStatus = "Audio requires ffplay (install the ffmpeg suite).";
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffplay,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if ((streamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || streamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(_lanToken)
            && !_lanToken.Contains('\r', StringComparison.Ordinal)
            && !_lanToken.Contains('\n', StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-headers");
            startInfo.ArgumentList.Add($"X-LAN-Token: {_lanToken}\r\n");
        }
        // Audio-only ffplay: -vn stops ffplay from decoding the full 1920x1080 HEVC
        // video it would otherwise discard (huge CPU saving while audio is on — the same
        // CPU the video pipe needs). Low-latency input flags keep audio start in step
        // with the video pipe. When the negotiated stream is the direct-RTSP URL, force
        // TCP transport the same way the video pipe does (verified live on the 5523-W).
        startInfo.ArgumentList.Add("-nodisp");
        startInfo.ArgumentList.Add("-vn");
        // Preserve audio timestamps without applying the video decoder's low-delay
        // reference-frame shortcut. The same shortcut that breaks HEVC video can make
        // the shared camera stream's audio clock unstable during reconnects.
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("genpts+discardcorrupt");
        if (streamUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-rtsp_transport");
            startInfo.ArgumentList.Add("tcp");
        }
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-autoexit");
        startInfo.ArgumentList.Add(streamUrl);

        try
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("ffplay failed to start.");
            }
            _ = Task.Run(async () =>
            {
                try { await process.StandardError.ReadToEndAsync(); }
                catch { }
            });
            _audioProcess = process;
            IsAudioOn = true;
            AudioStatus = "Audio on — press Space to mute";
        }
        catch (Exception ex)
        {
            AudioStatus = $"Audio failed to start: {ex.Message}";
        }
    }

    private async Task StopAudioAsync()
    {
        var process = Interlocked.Exchange(ref _audioProcess, null);
        await StopProcessSafelyAsync(process);
    }

    // ── Banner / menu sheet (single-click, Escape, Backspace) ──────

    /// <summary>Single click on the video stage toggles the option banner.</summary>
    /// <summary>Stream quality used for the manifest fetch ("main" = HD main, always default).</summary>
    [ObservableProperty]
    private string _quality = "main";

    partial void OnQualityChanged(string value)
    {
        OnPropertyChanged(nameof(IsMainQuality));
        OnPropertyChanged(nameof(IsSubQuality));
    }

    /// <summary>True when the HD main stream is selected (the always-preferred default).</summary>
    public bool IsMainQuality => string.Equals(Quality, "main", StringComparison.OrdinalIgnoreCase);

    public bool IsSubQuality => !IsMainQuality;

    /// <summary>Switches the decode loop to the chosen quality (HD main default; sub for weak links).</summary>
    [RelayCommand]
    private Task SelectQualityAsync(string quality)
    {
        if (string.Equals(Quality, quality, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }
        Quality = string.Equals(quality, "sub", StringComparison.OrdinalIgnoreCase) ? "sub" : "main";
        SetPlaybackStatus($"Switching to {Quality} stream…");
        return StartAsync();
    }

    /// <summary>Forces a fresh decode of the current quality (used by the Display menu).</summary>
    [RelayCommand]
    private Task RestartStreamAsync()
    {
        SetPlaybackStatus("Restarting stream…");
        return StartAsync();
    }

    [RelayCommand]
    private void ToggleBanner()
    {
        if (ActiveMenu is not null)
        {
            // A click while a sheet is open closes the sheet AND the banner (same as the SPA).
            ActiveMenu = null;
            BannerVisible = false;
            return;
        }
        BannerVisible = !BannerVisible;
    }

    /// <summary>Opens the menu sheet for a banner tile (key from <see cref="MenuTiles"/>).</summary>
    [RelayCommand]
    private void OpenMenu(string key)
    {
        ActiveMenu = key;
        BannerVisible = true;
        // Load the embedded camera-options surface for this device when the sheet opens
        // (same control-point inventory the Features section uses, applied in-window).
        if (Features is not null && Features.Controls.Count == 0)
        {
            Features.LoadControlPointsCommand.Execute(null);
        }
    }

    /// <summary>Escape / Backspace / second slow click: dismiss the menu, then the banner.</summary>
    [RelayCommand]
    private void DismissMenus()
    {
        if (ActiveMenu is not null)
        {
            ActiveMenu = null;
            return;
        }
        BannerVisible = false;
    }

    // ── Close / dispose ───────────────────────────────────────────

    [RelayCommand]
    private void ExitFullscreen() => RequestClose?.Invoke();

    /// <summary>Raised when the user double-clicks to exit fullscreen.</summary>
    public event Action? RequestClose;

    /// <summary>Human label for a ladder entry: direct RTSP vs the HTTP compatibility modes.</summary>
    private static string DescribeStream(string streamUrl)
    {
        if (streamUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return "direct RTSP";
        }
        if (streamUrl.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return "H.264 TS";
        }
        if (streamUrl.Contains("mjpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "MJPEG";
        }
        if (streamUrl.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return "snapshot";
        }
        if (streamUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "HEVC";
        }
        return "compatibility stream";
    }

    private static string? ResolveFfmpegPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null;
    }

    private static string? ResolveFfplayPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOSSCAM_FFPLAY_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return File.Exists("/usr/bin/ffplay") ? "/usr/bin/ffplay" : null;
    }

    private async Task StopVideoAsync()
    {
        // Invalidate queued UI callbacks immediately when a window restart/close begins.
        Interlocked.Increment(ref _videoGeneration);
        var cts = Interlocked.Exchange(ref _videoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _videoProcess, null);
        await StopProcessSafelyAsync(process);
        var task = Interlocked.Exchange(ref _videoTask, null);
        var completed = task is null;
        try
        {
            if (task is not null)
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3));
                completed = true;
            }
        }
        catch (TimeoutException)
        {
            // Do not dispose the CTS while the decoder still owns its token. The
            // completion continuation below releases it once cancellation actually
            // reaches the decoder task.
            completed = false;
        }
        catch (Exception)
        {
            // A faulted/canceled task is complete and its CTS is safe to release.
            completed = task?.IsCompleted ?? true;
        }

        if (completed)
        {
            cts?.Dispose();
        }
        else if (cts is not null && task is not null)
        {
            _ = DisposeVideoResourcesWhenCompleteAsync(cts, task);
        }
    }

    private static async Task DisposeVideoResourcesWhenCompleteAsync(
        CancellationTokenSource cts,
        Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Decoder cancellation/failure is expected during reconnect teardown.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static async Task StopProcessSafelyAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch
        {
            // Best-effort teardown.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        _snapshotTimer?.Dispose();
        _snapshotTimer = null;
        // Synchronous disposal: cancel/kill immediately, then let the bounded teardown
        // complete in the background. Never block the window-close path on a decoder that
        // ignored cancellation, and never throw from a synchronous Dispose.
        _ = TeardownAsync();
    }

    private async Task TeardownAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var startup = Interlocked.Exchange(ref _startupTask, null);
            try
            {
                await StopVideoAsync().ConfigureAwait(false);
                if (startup is not null)
                {
                    await startup.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                await StopAudioAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fullscreen teardown completed with non-fatal error");
            }
            finally
            {
                // StartAsync cannot still be between task registration and completion while
                // this gate is held. The captured lifetime token is therefore safe to release
                // after the bounded startup wait; cancellation has already stopped new work.
                if (startup is null || startup.IsCompleted)
                {
                    _lifetimeCts.Dispose();
                }
            }
        }
        finally
        {
            // Keep the gate alive for a late StartAsync call after Dispose. That call will
            // acquire it, observe _disposed, and return safely instead of racing a disposed
            // semaphore during window shutdown.
            _lifecycleGate.Release();
        }
    }
}

/// <summary>One banner tile: display name + glyph.</summary>
public sealed record FullscreenMenuTile(string Title, string Glyph);
