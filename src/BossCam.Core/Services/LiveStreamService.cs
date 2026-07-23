using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

/// <summary>
/// Live preview for browser multi-view.
/// Juan/5523-W RTSP is often HEVC and needs multi-second probe before video packets arrive.
/// Primary path: ffmpeg RTSP → MJPEG (works in &lt;img&gt; multipart). Fallback: NetSDK snapShot pump.
/// Optional: RTSP → H.264 MPEG-TS for mpegts.js.
/// </summary>
public sealed class LiveStreamService(
    IApplicationStore store,
    TransportBroker transportBroker,
    ILogger<LiveStreamService> logger)
{
    private static readonly HttpClient SnapshotClient = CreateSnapshotClient();

    public async Task StreamMpegTsAsync(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var (device, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found. Install ffmpeg for live streams.");

        // Always transcode to baseline H.264 — these firmwares advertise HEVC even on "sub".
        var scale = IsMain(quality) ? "1280:-2" : "960:-2";
        var bitrate = IsMain(quality) ? "2500k" : "1200k";
        var args = new StringBuilder()
            .Append("-hide_banner -loglevel warning ")
            .Append(RtspInputFlags())
            .Append("-i \"").Append(rtspUrl).Append("\" ")
            .Append("-an -map 0:v:0 ")
            .Append("-vf scale=").Append(scale).Append(" ")
            .Append("-c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline -pix_fmt yuv420p ")
            .Append("-b:v ").Append(bitrate).Append(" -maxrate ").Append(bitrate).Append(" -bufsize 800k ")
            .Append("-g 30 -bf 0 -f mpegts -flush_packets 1 -")
            .ToString();

        logger.LogInformation("Live MPEG-TS {Name} {Ip} q={Q}", device.DisplayName, device.IpAddress, quality);
        await RunFfmpegPipeAsync(ffmpeg, args, output, cancellationToken);
    }

    public async Task StreamMjpegAsync(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");

        // quality:
        //  - sub/auto (default multi-view): prefer fast NetSDK snapShot pump (~3–8 fps, instant start)
        //    then RTSP if snapShot is dead (e.g. 403).
        //  - main/rtsp: prefer RTSP→MJPEG (~12–15 fps after probe) for watchable full-motion.
        var wantRtspFirst = IsMain(quality)
            || string.Equals(quality, "rtsp", StringComparison.OrdinalIgnoreCase);

        if (!wantRtspFirst)
        {
            try
            {
                await StreamMjpegFromSnapshotPumpAsync(device, output, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "snapShot pump failed for {Ip}; trying RTSP MJPEG", device.IpAddress);
            }

            await StreamMjpegFromRtspAsync(deviceId, output, quality, cancellationToken);
            return;
        }

        try
        {
            await StreamMjpegFromRtspAsync(deviceId, output, quality, cancellationToken);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RTSP MJPEG failed for {Ip}; trying snapShot pump", device.IpAddress);
        }

        await StreamMjpegFromSnapshotPumpAsync(device, output, cancellationToken);
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

    private async Task StreamMjpegFromRtspAsync(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var (device, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found.");

        var scale = IsMain(quality) ? "1280:-2" : "720:-2";
        var fps = IsMain(quality) ? 12 : 15;
        var args = new StringBuilder()
            .Append("-hide_banner -loglevel warning ")
            .Append(RtspInputFlags())
            .Append("-i \"").Append(rtspUrl).Append("\" ")
            .Append("-an -map 0:v:0 ")
            .Append("-vf \"fps=").Append(fps).Append(",scale=").Append(scale).Append("\" ")
            .Append("-q:v 5 -f mpjpeg -")
            .ToString();

        logger.LogInformation("Live RTSP-MJPEG {Name} {Ip} q={Q}", device.DisplayName, device.IpAddress, quality);
        await RunFfmpegPipeAsync(ffmpeg, args, output, cancellationToken);
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
            $"/NetSDK/Video/encode/channel/101/snapShot",
            $"/NetSDK/Video/encode/channel/102/snapShot"
        };

        logger.LogInformation("Live snapShot-pump {Ip}", device.IpAddress);
        var boundary = "ffmpeg";
        // Note: browsers expect multipart/x-mixed-replace; boundary=...
        // We write frames matching that boundary.

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
                    // try next path
                }
            }

            if (jpeg is null)
            {
                failures++;
                // Fail fast so caller can switch to RTSP (e.g. cam .30 returns 403 on snapShot).
                if (failures >= 8)
                {
                    throw new InvalidOperationException(
                        $"NetSDK snapShot unavailable for {device.IpAddress}.");
                }

                await Task.Delay(150, cancellationToken);
                continue;
            }

            failures = 0;
            var header = Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(jpeg, cancellationToken);
            await output.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), cancellationToken);
            await output.FlushAsync(cancellationToken);
            // No artificial delay — pace is limited by camera snapShot latency (~200–400ms).
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
                ?? sources.FirstOrDefault(s => s.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp)?.Url
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

    private async Task RunFfmpegPipeAsync(
        string ffmpegPath,
        string args,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var process = new Process
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
            throw new InvalidOperationException("Failed to start ffmpeg for live stream.");
        }

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                var err = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    logger.LogDebug("ffmpeg live: {Err}", err.Length > 2000 ? err[^2000..] : err);
                }
            }
            catch
            {
                // ignore
            }
        }, CancellationToken.None);

        long bytesCopied = 0;
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
                bytesCopied += read;
            }
        }
        catch (OperationCanceledException)
        {
            // client gone — success if we already delivered frames
        }
        catch (IOException)
        {
            // broken pipe
        }
        finally
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

            try { await stderrTask; } catch { /* ignore */ }
        }

        // Only fail when nothing was delivered (true RTSP connect/decode failure).
        if (bytesCopied == 0)
        {
            var code = process.HasExited ? process.ExitCode : -1;
            throw new InvalidOperationException($"ffmpeg produced no live media (exit {code}).");
        }
    }

    /// <summary>
    /// Critical for Juan/GUANGZHOU: tiny probesize yields "video stream with no packets".
    /// </summary>
    private static string RtspInputFlags()
        // Juan needs a non-trivial probe or video arrives with "no packets".
        // Keep this under ~2s so first frame is not multi-second lag.
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

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(segment, "ffmpeg");
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static HttpClient CreateSnapshotClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            EnableMultipleHttp2Connections = true
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.ConnectionClose = false;
        return c;
    }
}
