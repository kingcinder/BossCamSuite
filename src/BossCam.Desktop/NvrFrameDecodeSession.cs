using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BossCam.Contracts;

namespace BossCam.Desktop;

public sealed class NvrFrameDecodeSession : IDisposable
{
    public const int FrameWidth = 640;
    public const int FrameHeight = 360;
    private const int BytesPerPixel = 4;
    private const int FrameBytes = FrameWidth * FrameHeight * BytesPerPixel;

    private readonly WriteableBitmap _target;
    private readonly DispatcherTimer _renderTimer;
    private readonly object _frameLock = new();
    private byte[]? _latestFrame;
    private CancellationTokenSource? _cts;
    private Process? _process;

    public NvrFrameDecodeSession(WriteableBitmap target)
    {
        _target = target;
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += (_, _) => RenderLatestFrame();
    }

    public async Task StartAsync(string source, CancellationToken cancellationToken)
    {
        Stop();
        var ffmpegPath = ResolveFfmpegPath() ?? throw new InvalidOperationException("ffmpeg not found. Set BOSSCAM_FFMPEG_PATH or add ffmpeg.exe to PATH.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var process = new Process
        {
            StartInfo = BuildStartInfo(ffmpegPath, source),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("ffmpeg failed to start.");
        }

        _process = process;
        _renderTimer.Start();
        _ = Task.Run(() => ReadFramesAsync(process, _cts.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _renderTimer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public void Dispose() => Stop();

    private static ProcessStartInfo BuildStartInfo(string ffmpegPath, string source)
    {
        var info = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        if (source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("-rtsp_transport");
            info.ArgumentList.Add("tcp");
        }

        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(source);
        info.ArgumentList.Add("-an");
        info.ArgumentList.Add("-vf");
        info.ArgumentList.Add($"scale={FrameWidth}:{FrameHeight}:force_original_aspect_ratio=decrease,pad={FrameWidth}:{FrameHeight}:(ow-iw)/2:(oh-ih)/2");
        info.ArgumentList.Add("-pix_fmt");
        info.ArgumentList.Add("bgra");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("rawvideo");
        info.ArgumentList.Add("pipe:1");
        return info;
    }

    private async Task ReadFramesAsync(Process process, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(cancellationToken); } catch { }
        }, CancellationToken.None);

        var stream = process.StandardOutput.BaseStream;
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            var frame = new byte[FrameBytes];
            if (!await ReadExactAsync(stream, frame, cancellationToken))
            {
                break;
            }

            lock (_frameLock)
            {
                _latestFrame = frame;
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private void RenderLatestFrame()
    {
        byte[]? frame;
        lock (_frameLock)
        {
            frame = _latestFrame;
            _latestFrame = null;
        }

        if (frame is null)
        {
            return;
        }

        _target.WritePixels(
            new System.Windows.Int32Rect(0, 0, FrameWidth, FrameHeight),
            frame,
            FrameWidth * BytesPerPixel,
            0);
    }

    private static string? ResolveFfmpegPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
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
}

public sealed class NvrTileViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private DeviceIdentity? _device;
    private NvrStreamMode _mode = NvrStreamMode.Live;
    private NvrStreamStatus _status = NvrStreamStatus.Empty;
    private string _source = string.Empty;
    private string _message = "Empty tile";
    private bool _isSelected;

    public NvrTileViewModel(int tileId)
    {
        TileId = tileId;
        FrameSource = new WriteableBitmap(NvrFrameDecodeSession.FrameWidth, NvrFrameDecodeSession.FrameHeight, 96, 96, PixelFormats.Bgra32, null);
    }

    public int TileId { get; }
    public WriteableBitmap FrameSource { get; }

    public DeviceIdentity? Device
    {
        get => _device;
        set
        {
            if (!Equals(_device, value))
            {
                _device = value;
                OnPropertyChanged(nameof(Device));
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public NvrStreamMode Mode
    {
        get => _mode;
        set
        {
            if (_mode != value)
            {
                _mode = value;
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public NvrStreamStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string Source
    {
        get => _source;
        set
        {
            if (_source != value)
            {
                _source = value;
                OnPropertyChanged(nameof(Source));
            }
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message != value)
            {
                _message = value;
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public string Title => Device is null ? $"Tile {TileId + 1}" : $"{Device.DisplayName} | {Mode}";
    public string StatusText => $"{Status}: {Message}";
    public NvrFrameDecodeSession? Session { get; set; }

    public void Stop()
    {
        Session?.Dispose();
        Session = null;
        Status = Device is null ? NvrStreamStatus.Empty : NvrStreamStatus.Stopped;
        Message = Device is null ? "Empty tile" : "Stopped";
        Source = string.Empty;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
