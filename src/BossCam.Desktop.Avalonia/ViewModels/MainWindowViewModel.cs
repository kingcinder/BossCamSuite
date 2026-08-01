using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Main window shell: owns navigation between sections, the shared device
/// selection, and the live-preview section (this VM is itself the first
/// section in the list). All other sections are hosted as child ViewModels.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, ISectionViewModel, IDisposable
{
    private readonly IBossCamApiClient _api;
    private readonly ILogger<MainWindowViewModel> _logger;
    private Timer? _liveTimer;
    private CancellationTokenSource? _liveVideoCts;
    private Task? _liveVideoTask;
    private Process? _liveVideoProcess;

    /// <summary>Default constructor — real HTTP client at http://127.0.0.1:5317.</summary>
    public MainWindowViewModel()
        : this(new HttpBossCamApiClient())
    {
    }

    /// <summary>DI-friendly constructor for production wiring and tests.</summary>
    public MainWindowViewModel(IBossCamApiClient apiClient, ILogger<MainWindowViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        _api = apiClient;
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;

        DashboardSection = new DashboardViewModel(apiClient, this);
        DevicesSection = new DevicesViewModel(apiClient, this);
        FeaturesSection = new FeaturesViewModel(apiClient, this);
        RecordingsSection = new RecordingsViewModel(apiClient, this);
        HighlightsSection = new HighlightsViewModel(apiClient, this);
        PlaybackSection = new PlaybackViewModel(apiClient, this);
        DiagnosticsSection = new DiagnosticsViewModel(apiClient, this);
        FirmwareSection = new FirmwareViewModel(apiClient, this);
        ConnectivitySection = new ConnectivityViewModel(apiClient, this);
        StorageSection = new StorageViewModel(apiClient, this);

        Sections = [this, DashboardSection, DevicesSection, FeaturesSection, RecordingsSection,
                    HighlightsSection, PlaybackSection, DiagnosticsSection, FirmwareSection,
                    ConnectivitySection, StorageSection];
        _selectedSection = this;
    }

    // ── Navigation ───────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<ISectionViewModel> _sections;

    [ObservableProperty]
    private object? _selectedSection;

    partial void OnSelectedSectionChanged(object? value)
    {
        if (value is ISectionViewModel section)
        {
            _ = ActivateSectionAsync(section);
        }
    }

    private async Task ActivateSectionAsync(ISectionViewModel section)
    {
        try
        {
            await section.ActivateAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"{section.Title} failed to load: {ex.Message}";
        }
    }

    // ── Section children (bound from MainWindow.xaml) ─────────────

    public DashboardViewModel DashboardSection { get; }
    public DevicesViewModel DevicesSection { get; }
    public FeaturesViewModel FeaturesSection { get; }
    public RecordingsViewModel RecordingsSection { get; }
    public HighlightsViewModel HighlightsSection { get; }
    public PlaybackViewModel PlaybackSection { get; }
    public DiagnosticsViewModel DiagnosticsSection { get; }
    public FirmwareViewModel FirmwareSection { get; }
    public ConnectivityViewModel ConnectivitySection { get; }
    public StorageViewModel StorageSection { get; }

    // ── ISectionViewModel (this shell is the "Live View" section) ─

    public string Title => "Live View";
    public string Glyph => "\U0001F4FA";
    public string Explain =>
        "Live View streams a snapshot of the selected camera and shows its identity " +
        "details. Click a camera in the sidebar device list, then use Refresh, Snapshot " +
        "or Save to manage the current view.";

    public async Task ActivateAsync()
    {
        if (Devices.Count == 0)
        {
            await LoadDevicesAsync();
        }
        if (SelectedDevice is not null)
        {
            await RefreshDeviceAsync();
        }
    }

    // ── Shared device list / selection ────────────────────────────

    [ObservableProperty]
    private ObservableCollection<DeviceIdentity> _devices = [];

    [ObservableProperty]
    private DeviceIdentity? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "Connect to BossCamService at http://127.0.0.1:5317";

    [ObservableProperty]
    private string _deviceInfoText = string.Empty;

    [ObservableProperty]
    private Bitmap? _liveFrame;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private WriteableBitmap? _liveVideoFrame;

    [ObservableProperty]
    private string _livePlaybackStatus = "Not started";

    partial void OnSelectedDeviceChanged(DeviceIdentity? value)
    {
        if (value is not null)
        {
            _ = RefreshDeviceAsync();
        }

        // Keep the Features section in sync with the camera being managed.
        _ = FeaturesSection.DeviceChangedAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _api.GetDevicesAsync();
            Devices = new ObservableCollection<DeviceIdentity>(devices);
            StatusText = $"Loaded {devices.Count} device(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load devices: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshDeviceAsync()
    {
        if (SelectedDevice is null) return;

        var id = SelectedDevice.Id;
        DeviceInfoText = $"Model: {SelectedDevice.HardwareModel ?? "\u2014"}\n"
            + $"IP: {SelectedDevice.IpAddress ?? "\u2014"}\n"
            + $"Firmware: {SelectedDevice.FirmwareVersion ?? "\u2014"}\n"
            + $"Type: {SelectedDevice.DeviceType ?? "\u2014"}";
        IsLive = true;

        try
        {
            var info = await _api.GetLiveInfoAsync(id);
            if (info.HasValue && info.Value.TryGetProperty("mainRtsp", out var main))
            {
                DeviceInfoText += $"\nMain RTSP: {main}";
            }
        }
        catch (Exception ex)
        {
            // Optional enrichment only — GetLiveInfoAsync already returns null on failure,
            // so the live view must not break if live-info is temporarily unavailable. The
            // Debug log keeps transient failures traceable without spamming the 2s poll loop.
            _logger.LogDebug(ex, "Live-info enrichment failed for device {DeviceId}", id);
        }

        _liveTimer?.Dispose();
        _liveTimer = new Timer(async _ =>
        {
            var bytes = await _api.GetSnapshotAsync(id);
            if (bytes is { Length: > 100 })
            {
                using var ms = new MemoryStream(bytes);
                LiveFrame = new Bitmap(ms);
            }
        }, null, 0, 2000);

        await StartLiveVideoAsync(id);
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (SelectedDevice is null) return;
        try
        {
            var saved = await _api.SaveSnapshotAsync(SelectedDevice.Id);
            StatusText = saved ? "Snapshot saved" : "Snapshot failed";
        }
        catch (Exception ex)
        {
            StatusText = $"Snapshot failed: {ex.Message}";
        }
    }

    private async Task StartLiveVideoAsync(Guid deviceId)
    {
        await StopLiveVideoAsync();
        var manifest = await _api.GetLiveManifestAsync(deviceId, "sub");
        var streamUrls = manifest is null ? [] : SelectDesktopStreamUrls(manifest);
        if (streamUrls.Count == 0)
        {
            LivePlaybackStatus = "Live manifest unavailable; snapshot view remains active.";
            return;
        }

        var cts = new CancellationTokenSource();
        _liveVideoCts = cts;
        var lanToken = _api.LanToken;
        _liveVideoTask = Task.Run(() => RunLiveVideoLoopAsync(streamUrls, lanToken, cts.Token));
    }

    internal static IReadOnlyList<string> SelectDesktopStreamUrls(LiveMediaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Follow the backend's ordered negotiation result. The old "first non-empty URL"
        // behavior could launch fMP4 even when RTSP had already degraded to MJPEG/snapshot.
        var modes = new[] { manifest.PreferredMode }
            .Concat(manifest.FallbackModes)
            .Distinct()
            .ToList();
        var urls = new List<string>();
        foreach (var mode in modes)
        {
            var url = mode switch
            {
                LiveMediaModeContract.H264Fmp4 => manifest.H264Fmp4Url,
                LiveMediaModeContract.H264MpegTs => manifest.MpegTsUrl,
                LiveMediaModeContract.Mjpeg => manifest.MjpegUrl,
                LiveMediaModeContract.Snapshot when manifest.SnapshotAvailable => manifest.SnapshotUrl,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        // Compatibility with older manifests that omitted FallbackModes.
        foreach (var url in new[] { manifest.H264Fmp4Url, manifest.MpegTsUrl, manifest.MjpegUrl,
                                    manifest.SnapshotAvailable ? manifest.SnapshotUrl : string.Empty })
        {
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }
        return urls;
    }

    internal static string? SelectDesktopStreamUrl(LiveMediaManifest manifest)
        => SelectDesktopStreamUrls(manifest).FirstOrDefault();

    internal static int NextDesktopStreamIndex(int currentIndex, int streamCount)
        => streamCount <= 0 ? 0 : (currentIndex + 1) % streamCount;

    private async Task RunLiveVideoLoopAsync(IReadOnlyList<string> streamUrls, string? lanToken, CancellationToken cancellationToken)
    {
        var streamIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                var streamUrl = streamUrls[streamIndex];
                LivePlaybackStatus = $"Connecting compatibility stream {streamIndex + 1}/{streamUrls.Count}…";
                var ffmpeg = ResolveFfmpegPath();
                if (ffmpeg is null)
                {
                    LivePlaybackStatus = "FFmpeg unavailable; snapshot view remains active.";
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
                foreach (var arg in BuildLiveVideoFfmpegArguments(streamUrl, lanToken))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg failed to start.");
                }
                // stderr is redirected; drain it continuously or repeated camera/network
                // errors can fill the OS pipe and stall ffmpeg's stdout playback stream.
                _ = Task.Run(async () =>
                {
                    try { await process.StandardError.ReadToEndAsync(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        _logger.LogDebug(ex, "FFmpeg stderr drain ended");
                    }
                });
                _liveVideoProcess = process;
                LivePlaybackStatus = $"Playing compatibility stream {streamIndex + 1}/{streamUrls.Count}";

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
                LivePlaybackStatus = $"Playback reconnecting: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_liveVideoProcess, process))
                {
                    _liveVideoProcess = null;
                }
                await StopProcessSafelyAsync(process);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                // Move through the backend-negotiated representations after a decoder or
                // transport failure instead of retrying a dead fMP4 URL forever.
                streamIndex = NextDesktopStreamIndex(streamIndex, streamUrls.Count);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    internal static IReadOnlyList<string> BuildLiveVideoFfmpegArguments(string streamUrl, string? lanToken)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error"
        };
        // ffmpeg's HTTP input accepts custom headers. Keep the token out of the URL and
        // reject line breaks so a malformed token cannot create additional HTTP headers.
        if (!string.IsNullOrWhiteSpace(lanToken)
            && !lanToken.Contains('\r', StringComparison.Ordinal)
            && !lanToken.Contains('\n', StringComparison.Ordinal))
        {
            args.Add("-headers");
            args.Add($"X-LAN-Token: {lanToken}\r\n");
        }
        args.AddRange([
            "-i", streamUrl,
            "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ]);
        return args;
    }

    private static string? ResolveFfmpegPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null;
    }

    private async Task StopLiveVideoAsync()
    {
        var cts = Interlocked.Exchange(ref _liveVideoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _liveVideoProcess, null);
        await StopProcessSafelyAsync(process);

        var task = Interlocked.Exchange(ref _liveVideoTask, null);
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // The process was already terminated; do not block navigation forever on a
                // decoder that ignored cancellation.
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live video task ended during stop");
            }
        }
        cts?.Dispose();
    }

    private static async Task StopProcessSafelyAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch (ObjectDisposedException)
        {
            // Another teardown path already disposed the process.
        }
        catch (InvalidOperationException)
        {
            // The process may have exited or been disposed concurrently during a device switch.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS can report a transient teardown race; cleanup remains best effort.
        }
        catch (OperationCanceledException)
        {
            // Bounded cleanup: never hang the UI on a broken decoder process.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        _liveTimer?.Dispose();

        // Do not synchronously join the decoder task here: it can be waiting for the
        // Avalonia UI dispatcher while Dispose is called on that same UI thread. Cancel and
        // terminate immediately, then let the task's finally block finish asynchronously.
        var cts = Interlocked.Exchange(ref _liveVideoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _liveVideoProcess, null);
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "Live video process was already stopped during dispose");
            }
        }

        var task = Interlocked.Exchange(ref _liveVideoTask, null);
        if (task is not null || cts is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (task is not null) await task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    _logger.LogDebug(ex, "Live video task cleanup completed asynchronously");
                }
                finally
                {
                    cts?.Dispose();
                    process?.Dispose();
                }
            });
        }
        else
        {
            process?.Dispose();
        }

        _api.Dispose();
    }
}
