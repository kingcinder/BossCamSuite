using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

/// <summary>
/// Live multi-view streaming.
/// Each camera gets at most one shared ffmpeg RTSP→MJPEG session (subscribers fan-out),
/// so multi-tile boards stay fluid without exhausting camera RTSP slots.
/// Falls back to NetSDK snapShot pump when RTSP cannot produce frames.
/// </summary>
public sealed class LiveStreamService(
    IApplicationStore store,
    TransportBroker transportBroker,
    ILogger<LiveStreamService> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SharedMjpegSession> _sessions = new(StringComparer.Ordinal);
    private static readonly HttpClient SnapshotClient = CreateSnapshotClient();

    public async Task StreamMpegTsAsync(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        // TS path stays one-ffmpeg-per-viewer (optional advanced); multi-view uses MJPEG sessions.
        var (device, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found. Install ffmpeg for live streams.");
        var scale = IsMain(quality) ? "1280:-2" : "960:-2";
        var bitrate = IsMain(quality) ? "2500k" : "1200k";
        var args = new StringBuilder()
            .Append("-hide_banner -loglevel warning ")
            .Append(RtspInputFlags())
            .Append("-i \"").Append(rtspUrl).Append("\" ")
            .Append("-an -map 0:v:0 -vf scale=").Append(scale).Append(' ')
            .Append("-c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline -pix_fmt yuv420p ")
            .Append("-b:v ").Append(bitrate).Append(" -maxrate ").Append(bitrate).Append(" -bufsize 800k ")
            .Append("-g 30 -bf 0 -f mpegts -flush_packets 1 -")
            .ToString();
        logger.LogInformation("Live MPEG-TS {Ip} q={Q}", device.IpAddress, quality);
        await RunFfmpegCopyAsync(ffmpeg, args, output, cancellationToken);
    }

    public async Task StreamMjpegAsync(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");

        // Prefer shared RTSP session for real motion. snapShot-only when quality=sub and RTSP is dead.
        var preferSnapOnly = string.Equals(quality, "snap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(quality, "sub", StringComparison.OrdinalIgnoreCase);

        if (!preferSnapOnly)
        {
            try
            {
                await StreamFromSharedRtspAsync(deviceId, device, quality, output, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Shared RTSP failed for {Ip}; snapShot pump", device.IpAddress);
            }
        }

        try
        {
            // For multi-view default (sub): try shared RTSP first too — much smoother than snapShot.
            if (preferSnapOnly)
            {
                try
                {
                    await StreamFromSharedRtspAsync(deviceId, device, "rtsp", output, cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "RTSP shared unavailable for {Ip}, using snapShot", device.IpAddress);
                }
            }

            await StreamMjpegFromSnapshotPumpAsync(device, output, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public async Task<(string? MainRtsp, string? SubRtsp, string? PreferredLive)> DescribeAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");
        var sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);
        var main = RecordingService.SelectHighResMainSource(sources)?.Url
            ?? BuildJuanUrl(device, "ch0_0.264");
        var sub = sources.FirstOrDefault(s =>
            s.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp
            && (s.Url.Contains("ch0_1", StringComparison.OrdinalIgnoreCase)
                || (s.Metadata.TryGetValue("stream", out var st)
                    && st.Equals("sub", StringComparison.OrdinalIgnoreCase))))?.Url
            ?? BuildJuanUrl(device, "ch0_1.264");
        return (EnsureCredentials(main!, device), EnsureCredentials(sub!, device), EnsureCredentials(sub!, device));
    }

    private async Task StreamFromSharedRtspAsync(
        Guid deviceId,
        DeviceIdentity device,
        string quality,
        Stream output,
        CancellationToken cancellationToken)
    {
        var q = IsMain(quality) ? "main" : "sub";
        var key = $"{deviceId:N}:{q}";
        var session = _sessions.GetOrAdd(key, _ => new SharedMjpegSession(deviceId, device, q, this, logger));
        // Subscribe first so early frames are not dropped, then start ffmpeg.
        await session.WriteToAsync(output, cancellationToken);
    }

    internal async Task<(string Ffmpeg, string Args)> BuildRtspMjpegCommandAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var (_, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found.");
        // Sub is often HEVC 704x480 — decode once per cam (shared session) into light MJPEG.
        var scale = IsMain(quality) ? "960:-2" : "640:-2";
        var fps = IsMain(quality) ? 10 : 12;
        var args = new StringBuilder()
            .Append("-hide_banner -loglevel warning ")
            .Append(RtspInputFlags())
            .Append("-i \"").Append(rtspUrl).Append("\" ")
            .Append("-an -map 0:v:0 ")
            .Append("-vf \"fps=").Append(fps).Append(",scale=").Append(scale).Append("\" ")
            .Append("-q:v 7 -f mpjpeg -")
            .ToString();
        return (ffmpeg, args);
    }

    private async Task StreamMjpegFromSnapshotPumpAsync(
        DeviceIdentity device,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            throw new InvalidOperationException("Device has no IP.");
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var port = device.Port <= 0 ? 80 : device.Port;
        var paths = new[]
        {
            "/NetSDK/Video/encode/channel/101/snapShot",
            "/NetSDK/Video/encode/channel/102/snapShot"
        };

        logger.LogInformation("Live snapShot-pump {Ip}", device.IpAddress);
        const string boundary = "ffmpeg";
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? jpeg = null;
            foreach (var path in paths)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{device.IpAddress}:{port}{path}");
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                    using var res = await SnapshotClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!res.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var bytes = await res.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                    {
                        jpeg = bytes;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // next path
                }
            }

            if (jpeg is null)
            {
                failures++;
                if (failures >= 10)
                {
                    throw new InvalidOperationException($"NetSDK snapShot unavailable for {device.IpAddress}.");
                }

                await Task.Delay(120, cancellationToken);
                continue;
            }

            failures = 0;
            var header = Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(jpeg, cancellationToken);
            await output.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private async Task<(DeviceIdentity Device, string RtspUrl)> ResolveRtspAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            throw new InvalidOperationException("Device has no IP address.");
        }

        var sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);
        string? url;
        if (IsMain(quality))
        {
            url = RecordingService.SelectHighResMainSource(sources)?.Url
                ?? BuildJuanUrl(device, "ch0_0.264");
        }
        else
        {
            url = sources.FirstOrDefault(s =>
                    s.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp
                    && (s.Url.Contains("ch0_1", StringComparison.OrdinalIgnoreCase)
                        || s.Url.Contains("/12", StringComparison.OrdinalIgnoreCase)
                        || (s.Metadata.TryGetValue("stream", out var st)
                            && st.Equals("sub", StringComparison.OrdinalIgnoreCase))))?.Url
                ?? BuildJuanUrl(device, "ch0_1.264");
        }

        return (device, EnsureCredentials(url!, device));
    }

    private static async Task RunFfmpegCopyAsync(
        string ffmpegPath,
        string args,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var process = StartFfmpeg(ffmpegPath, args);
        long bytes = 0;
        try
        {
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await output.FlushAsync(cancellationToken);
                bytes += read;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            TryKill(process);
        }

        if (bytes == 0)
        {
            throw new InvalidOperationException("ffmpeg produced no live media.");
        }
    }

    internal static Process StartFfmpeg(string ffmpegPath, string args)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg.");
        }

        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(); }
            catch { /* ignore */ }
        });
        return process;
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string RtspInputFlags()
        => "-rtsp_transport tcp -rtsp_flags prefer_tcp "
           + "-fflags nobuffer+genpts -flags low_delay "
           + "-probesize 2000000 -analyzeduration 2000000 "
           + "-max_delay 500000 ";

    private static bool IsMain(string? quality)
        => string.Equals(quality, "main", StringComparison.OrdinalIgnoreCase)
           || string.Equals(quality, "high", StringComparison.OrdinalIgnoreCase);

    private static string BuildJuanUrl(DeviceIdentity device, string path)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var auth = $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";
        return $"rtsp://{auth}{device.IpAddress}:554/{path.TrimStart('/')}";
    }

    private static string EnsureCredentials(string url, DeviceIdentity device)
    {
        if (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        try
        {
            var builder = new UriBuilder(url);
            if (!string.IsNullOrEmpty(builder.UserName))
            {
                return url;
            }

            builder.UserName = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
            builder.Password = device.Password ?? string.Empty;
            return builder.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }

    private static string? ResolveFfmpegPath()
    {
        var env = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var candidate in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static HttpClient CreateSnapshotClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
        {
            await s.DisposeAsync();
        }

        _sessions.Clear();
    }

    /// <summary>One ffmpeg process, many HTTP viewers.</summary>
    private sealed class SharedMjpegSession(
        Guid deviceId,
        DeviceIdentity device,
        string quality,
        LiveStreamService owner,
        ILogger logger) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _subscribers = new();
        private Process? _process;
        private Task? _pumpTask;
        private int _started;
        private CancellationTokenSource? _cts;

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                _cts = new CancellationTokenSource();
                var (ffmpeg, args) = await owner.BuildRtspMjpegCommandAsync(deviceId, quality, cancellationToken);
                logger.LogInformation("Shared RTSP session start {Ip} q={Q}", device.IpAddress, quality);
                _process = StartFfmpeg(ffmpeg, args);
                _pumpTask = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
            }

            // Wait briefly for first frame so clients don't hang on black.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (_subscribers.IsEmpty && _process is { HasExited: true })
                {
                    break;
                }

                // session is up once pump is running
                if (_pumpTask is not null)
                {
                    break;
                }

                await Task.Delay(50, cancellationToken);
            }
        }

        public async Task WriteToAsync(Stream output, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _subscribers[id] = channel;
            await EnsureStartedAsync(cancellationToken);

            var gotFrame = false;
            try
            {
                const string boundary = "ffmpeg";
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Fail over to snapShot if RTSP never yields a frame.
                linked.CancelAfter(TimeSpan.FromSeconds(14));
                try
                {
                    await foreach (var jpeg in channel.Reader.ReadAllAsync(linked.Token))
                    {
                        if (!gotFrame)
                        {
                            gotFrame = true;
                            // After first frame, only client cancel should stop the stream.
                            linked.CancelAfter(Timeout.InfiniteTimeSpan);
                        }

                        var header = Encoding.ASCII.GetBytes(
                            $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                        await output.WriteAsync(header, cancellationToken);
                        await output.WriteAsync(jpeg, cancellationToken);
                        await output.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
                        await output.FlushAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !gotFrame)
                {
                    throw new InvalidOperationException("Shared RTSP session produced no frames in time.");
                }

                if (!gotFrame)
                {
                    throw new InvalidOperationException("Shared RTSP session ended without frames.");
                }
            }
            finally
            {
                _subscribers.TryRemove(id, out _);
                if (_subscribers.IsEmpty)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        if (_subscribers.IsEmpty)
                        {
                            await DisposeAsync();
                        }
                    });
                }
            }
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                // Parse multipart MJPEG from ffmpeg stdout and fan-out complete JPEG frames.
                var stdout = _process.StandardOutput.BaseStream;
                var buffer = new byte[64 * 1024];
                var acc = new MemoryStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    acc.Write(buffer, 0, read);
                    ExtractAndPublishJpegs(acc);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Shared RTSP pump ended for {Ip}", device.IpAddress);
            }
            finally
            {
                // unblock waiters
                foreach (var ch in _subscribers.Values)
                {
                    ch.Writer.TryComplete();
                }

                if (_process is not null)
                {
                    TryKill(_process);
                }

                Interlocked.Exchange(ref _started, 0);
            }
        }

        private void ExtractAndPublishJpegs(MemoryStream acc)
        {
            var data = acc.ToArray();
            var searchFrom = 0;
            while (true)
            {
                var soi = IndexOf(data, [0xFF, 0xD8], searchFrom);
                if (soi < 0)
                {
                    break;
                }

                var eoi = IndexOf(data, [0xFF, 0xD9], soi + 2);
                if (eoi < 0)
                {
                    // incomplete frame — keep from SOI
                    var keep = data.AsSpan(soi).ToArray();
                    acc.SetLength(0);
                    acc.Write(keep, 0, keep.Length);
                    return;
                }

                var len = eoi + 2 - soi;
                var jpeg = new byte[len];
                Buffer.BlockCopy(data, soi, jpeg, 0, len);
                foreach (var ch in _subscribers.Values)
                {
                    ch.Writer.TryWrite(jpeg);
                }

                searchFrom = eoi + 2;
            }

            if (searchFrom > 0 && searchFrom < data.Length)
            {
                var keep = data.AsSpan(searchFrom).ToArray();
                acc.SetLength(0);
                acc.Write(keep, 0, keep.Length);
            }
            else if (searchFrom >= data.Length)
            {
                acc.SetLength(0);
            }
            else if (data.Length > 2 * 1024 * 1024)
            {
                // avoid unbounded growth if stream is garbage
                acc.SetLength(0);
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (var i = start; i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    return i;
                }
            }

            return -1;
        }

        public ValueTask DisposeAsync()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            if (_process is not null)
            {
                TryKill(_process);
            }

            foreach (var ch in _subscribers.Values)
            {
                ch.Writer.TryComplete();
            }

            _subscribers.Clear();
            Interlocked.Exchange(ref _started, 0);
            return ValueTask.CompletedTask;
        }
    }
}
