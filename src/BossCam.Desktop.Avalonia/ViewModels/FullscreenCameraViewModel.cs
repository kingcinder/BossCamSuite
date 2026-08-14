using System.Diagnostics;
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
    private CancellationTokenSource? _videoCts;
    private Task? _videoTask;
    private Process? _videoProcess;
    private Process? _audioProcess;
    private bool _disposed;

    public FullscreenCameraViewModel(
        IBossCamApiClient api,
        DeviceIdentity device,
        string? lanToken,
        ILogger<FullscreenCameraViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(device);
        _api = api;
        Device = device;
        _lanToken = lanToken;
        _logger = logger ?? NullLogger<FullscreenCameraViewModel>.Instance;
    }

    public DeviceIdentity Device { get; }

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

    [ObservableProperty]
    private bool _bannerVisible;

    /// <summary>Open menu tile key, or null when the menu sheet is hidden.</summary>
    [ObservableProperty]
    private string? _activeMenu;

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
        await StartVideoAsync();
    }

    // ── Video (own decode loop at HD main quality) ─────────────────

    private async Task StartVideoAsync()
    {
        await StopVideoAsync();
        var manifest = await _api.GetLiveManifestAsync(Device.Id, "main");
        var streamUrls = manifest is null ? [] : MainWindowViewModel.SelectDesktopStreamUrls(manifest);
        if (streamUrls.Count == 0)
        {
            PlaybackStatus = "Live manifest unavailable.";
            return;
        }

        var cts = new CancellationTokenSource();
        _videoCts = cts;
        _videoTask = Task.Run(() => RunVideoLoopAsync(streamUrls, cts.Token));
    }

    private async Task RunVideoLoopAsync(IReadOnlyList<string> streamUrls, CancellationToken cancellationToken)
    {
        var streamIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                var streamUrl = streamUrls[streamIndex];
                PlaybackStatus = $"Connecting compatibility stream {streamIndex + 1}/{streamUrls.Count}…";
                var ffmpeg = ResolveFfmpegPath();
                if (ffmpeg is null)
                {
                    PlaybackStatus = "FFmpeg unavailable.";
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
                foreach (var arg in MainWindowViewModel.BuildLiveVideoFfmpegArguments(streamUrl, _lanToken))
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
                PlaybackStatus = $"Playing compatibility stream {streamIndex + 1}/{streamUrls.Count}";

                const int width = 960;
                const int height = 540;
                var frameSize = width * height * 4;
                var buffer = new byte[frameSize];
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(process.StandardOutput.BaseStream, buffer, cancellationToken))
                    {
                        break;
                    }

                    var frame = buffer.ToArray();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LiveVideoFrame ??= new WriteableBitmap(
                            new global::Avalonia.PixelSize(width, height),
                            new global::Avalonia.Vector(96, 96),
                            global::Avalonia.Platform.PixelFormat.Bgra8888,
                            global::Avalonia.Platform.AlphaFormat.Opaque);
                        using var locked = LiveVideoFrame.Lock();
                        System.Runtime.InteropServices.Marshal.Copy(frame, 0, locked.Address, frame.Length);
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                PlaybackStatus = $"Playback reconnecting: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_videoProcess, process))
                {
                    _videoProcess = null;
                }
                await StopProcessSafelyAsync(process);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                streamIndex = MainWindowViewModel.NextDesktopStreamIndex(streamIndex, streamUrls.Count);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
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

        var manifest = await _api.GetLiveManifestAsync(Device.Id, "main");
        var streamUrl = manifest is null ? null : MainWindowViewModel.SelectDesktopStreamUrl(manifest);
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
        if (!string.IsNullOrWhiteSpace(_lanToken)
            && !_lanToken.Contains('\r', StringComparison.Ordinal)
            && !_lanToken.Contains('\n', StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-headers");
            startInfo.ArgumentList.Add($"X-LAN-Token: {_lanToken}\r\n");
        }
        startInfo.ArgumentList.Add("-nodisp");
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

    /// <summary>Raised when a banner tile wants to open the matching management section.</summary>
    public event Action<string>? RequestOpenSection;

    /// <summary>Navigates the shell to the given section (from a banner tile).</summary>
    [RelayCommand]
    private void OpenSection(string title) => RequestOpenSection?.Invoke(title);

    /// <summary>
    /// Reads a full rawvideo frame, bounding each read with a stall timeout. A silent RTSP
    /// stall (5523-W drops media but keeps the socket open) otherwise blocks ReadAsync forever
    /// and freezes the last frame on screen. A timed-out read returns false so the caller
    /// advances to the next negotiated representation and reconnects.
    /// </summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), readTimeout.Token);
                if (read == 0) return false;
                offset += read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false; // stalled — fail over to the next stream representation
            }
        }
        return true;
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
        var cts = Interlocked.Exchange(ref _videoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _videoProcess, null);
        await StopProcessSafelyAsync(process);
        var task = Interlocked.Exchange(ref _videoTask, null);
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }
        cts?.Dispose();
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
        // Synchronous disposal: cancel/kill immediately, then let the bounded teardown
        // complete in the background. Never block the window-close path on a decoder that
        // ignored cancellation, and never throw from a synchronous Dispose.
        _ = TeardownAsync();
    }

    private async Task TeardownAsync()
    {
        try
        {
            await StopVideoAsync();
            await StopAudioAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fullscreen teardown completed with non-fatal error");
        }
    }
}

/// <summary>One banner tile: display name + glyph.</summary>
public sealed record FullscreenMenuTile(string Title, string Glyph);
