using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using BossCam.Contracts;
using BossCam.Core.Utilities;
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
    private readonly ConcurrentDictionary<string, SharedFmp4Session> _fmp4Sessions = new(StringComparer.Ordinal);
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
        // Compatibility output is deliberately bounded: ultrafast H.264, no B-frames,
        // explicit maxrate/bufsize, and a low-latency MPEG-TS pipe.
        var args = BuildRtspH264TsArguments(rtspUrl, IsMain(quality));
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

        // A viewer can survive a camera/RTSP process drop: each failed shared session is
        // discarded and restarted with bounded backoff before the authenticated snapshot pump
        // takes over. This prevents a transient RTP/ffmpeg failure from becoming a permanent black tile.
        var lastFailure = default(Exception);
        for (var attempt = 0; attempt < 3 && !cancellationToken.IsCancellationRequested; attempt++)
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
                lastFailure = ex;
                logger.LogWarning(ex, "Shared RTSP attempt {Attempt}/3 failed for {Ip}; reconnecting", attempt + 1, device.IpAddress);
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                }
            }
        }

        try
        {
            logger.LogWarning(lastFailure, "Shared RTSP unavailable for {Ip}; using authenticated snapshot fallback", device.IpAddress);
            await StreamMjpegFromSnapshotPumpAsync(device, output, quality, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// Streams RTSP as fragmented MP4 via ffmpeg for browser MSE playback.
    /// Uses codec copy to minimize latency — the browser decodes natively.
    /// Flags: frag_keyframe (keyframe-aligned fragments), empty_moov (immediate init),
    /// default_base_moof (compatible moof offsets).
    /// </summary>
    public async Task StreamFragmentedMp4Async(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var (device, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found. Install ffmpeg for live streams.");
        var args = BuildRtspFmp4Arguments(rtspUrl);
        logger.LogInformation("Live fMP4 {Ip} q={Q}", device.IpAddress, quality);
        await RunFfmpegCopyAsync(ffmpeg, args, output, cancellationToken);
    }

    /// <summary>
    /// Compatibility fMP4 output for browsers and native clients that cannot decode the
    /// 5523-W's HEVC stream. The transcode is intentionally bounded and low-latency.
    /// </summary>
    public async Task StreamH264Fmp4Async(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");
        var normalizedQuality = IsMain(quality) ? "main" : "sub";
        var key = $"{deviceId:N}:{normalizedQuality}";
        var session = _fmp4Sessions.GetOrAdd(
            key,
            _ => new SharedFmp4Session(key, deviceId, device, normalizedQuality, this, logger));
        // Browser MSE and Avalonia both consume this one bounded H.264 fMP4 session. The
        // fallback MPEG-TS endpoint remains available for native clients that need it.
        await session.WriteToAsync(output, cancellationToken);
    }

    public async Task<LiveMediaManifest> BuildManifestAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");
        var sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);
        var decision = PlayableSourcePolicy.Resolve(sources, IsMain(quality) ? "main" : "sub");
        var selected = SelectManifestSource(decision, quality);
        var codec = selected?.Metadata.GetValueOrDefault("codec")
            ?? (selected?.DisplayName?.Contains("HEVC", StringComparison.OrdinalIgnoreCase) == true ? "hevc" : "unknown");
        var rtspPlayable = false;
        if (selected?.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp
            && Uri.TryCreate(selected.Url, UriKind.Absolute, out var rtspUri)
            && !string.IsNullOrWhiteSpace(rtspUri.Host))
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            rtspPlayable = await RtspProbe.ProbeAsync(
                rtspUri.Host,
                rtspUri.Port > 0 ? rtspUri.Port : 554,
                probeCts.Token,
                TimeSpan.FromSeconds(3));
        }

        var facts = new LiveMediaSourceFacts
        {
            IsRtspPlayable = rtspPlayable,
            MainCodec = codec,
            // The snapshot endpoint itself performs authenticated JPEG validation and port
            // fallback. A registered LAN address is sufficient to advertise that safety net;
            // the endpoint remains the final authority if the camera drops between calls.
            SnapshotAvailable = decision.Snapshot is not null || !string.IsNullOrWhiteSpace(device.IpAddress)
        };
        var negotiated = LiveMediaNegotiationPolicy.Resolve(facts, browserSupportsHevc: false);
        var basePath = $"/api/devices/{deviceId}";
        return new LiveMediaManifest
        {
            DeviceId = deviceId,
            SourceCodec = codec,
            SourceRole = IsMain(quality) ? "main" : "sub",
            DecisionReason = negotiated.Reason,
            PreferredMode = ToContractMode(negotiated.PreferredMode),
            FallbackModes = negotiated.FallbackModes.Select(ToContractMode).ToList(),
            SnapshotAvailable = facts.SnapshotAvailable,
            MjpegUrl = $"{basePath}/live.mjpeg?quality={Uri.EscapeDataString(quality)}",
            H264Fmp4Url = $"{basePath}/live.h264.mp4?quality={Uri.EscapeDataString(quality)}",
            HevcFmp4Url = $"{basePath}/live.mp4?quality={Uri.EscapeDataString(quality)}",
            MpegTsUrl = $"{basePath}/live.ts?quality={Uri.EscapeDataString(quality)}",
            SnapshotUrl = $"{basePath}/snapshot"
        };
    }

    internal static VideoSourceDescriptor? SelectManifestSource(PlayableSourceDecision decision, string quality)
        => IsMain(quality) ? decision.Main ?? decision.Preferred : decision.Sub ?? decision.Main ?? decision.Preferred;

    private static LiveMediaModeContract ToContractMode(LiveMediaMode mode)
        => mode switch
        {
            LiveMediaMode.HevcFmp4 => LiveMediaModeContract.HevcFmp4,
            LiveMediaMode.H264Fmp4 => LiveMediaModeContract.H264Fmp4,
            LiveMediaMode.H264MpegTs => LiveMediaModeContract.H264MpegTs,
            LiveMediaMode.Mjpeg => LiveMediaModeContract.Mjpeg,
            _ => LiveMediaModeContract.Snapshot
        };

    public async Task<(string? MainRtsp, string? SubRtsp, string? PreferredLive)> DescribeAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");
        var sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);
        var decision = PlayableSourcePolicy.Resolve(sources);
        var main = decision.Main?.Url ?? BuildJuanUrl(device, "ch0_0.264");
        var sub = decision.Sub?.Url ?? BuildJuanUrl(device, "ch0_1.264");
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
        var session = _sessions.GetOrAdd(key, _ => new SharedMjpegSession(key, deviceId, device, q, this, logger));
        // Subscribe first so early frames are not dropped, then start ffmpeg.
        await session.WriteToAsync(output, cancellationToken);
    }

    private void RemoveSession(string key, SharedMjpegSession session)
    {
        ((ICollection<KeyValuePair<string, SharedMjpegSession>>)_sessions)
            .Remove(new KeyValuePair<string, SharedMjpegSession>(key, session));
    }

    internal async Task<(string Ffmpeg, IReadOnlyList<string> Args)> BuildRtspH264Fmp4CommandAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var (_, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found.");
        return (ffmpeg, BuildRtspH264Fmp4Arguments(rtspUrl, IsMain(quality)));
    }

    internal async Task<(string Ffmpeg, IReadOnlyList<string> Args)> BuildRtspMjpegCommandAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var (_, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found.");
        // Sub is often HEVC 704x480 — decode once per cam (shared session) into light MJPEG.
        var args = BuildRtspMjpegArguments(rtspUrl, IsMain(quality));
        return (ffmpeg, args);
    }

    private async Task StreamMjpegFromSnapshotPumpAsync(
        DeviceIdentity device,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            throw new InvalidOperationException("Device has no IP.");
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        // Discovery can record the ONVIF/media port (8888/8899) while the NetSDK REST
        // snapshot API listens on 80 — try the recorded port first, then fall back to 80.
        var ports = NetSdkPortCandidates.For(device.Port);
        var paths = IsMain(quality)
            ? new[]
            {
                "/NetSDK/Video/encode/channel/101/snapShot",
                "/NetSDK/Video/encode/channel/102/snapShot"
            }
            : new[]
            {
                "/NetSDK/Video/encode/channel/102/snapShot",
                "/NetSDK/Video/encode/channel/101/snapShot"
            };

        logger.LogInformation("Live snapShot-pump {Ip}", device.IpAddress);
        const string boundary = "ffmpeg";
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? jpeg = null;
            foreach (var port in ports)
            {
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

                if (jpeg is not null)
                {
                    break; // succeeded on this port — skip remaining fallback candidates
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
        var decision = PlayableSourcePolicy.Resolve(sources, IsMain(quality) ? "main" : "sub");
        url = decision.Preferred?.Url
            ?? BuildJuanUrl(device, IsMain(quality) ? "ch0_0.264" : "ch0_1.264");

        return (device, EnsureCredentials(url!, device));
    }

    private static async Task RunFfmpegCopyAsync(
        string ffmpegPath,
        IEnumerable<string> args,
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

    internal static Process StartFfmpeg(string ffmpegPath, IEnumerable<string> args)
    {
        var process = new Process
        {
            StartInfo = ProcessLauncher.Build(ffmpegPath, args),
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

    internal static IReadOnlyList<string> BuildRtspMjpegArguments(string rtspUrl, bool isMain)
    {
        var scale = isMain ? "960:-2" : "640:-2";
        var fps = isMain ? "10" : "12";
        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            "-fflags", "nobuffer+genpts",
            "-flags", "low_delay",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            "-i", rtspUrl,
            "-an", "-map", "0:v:0",
            "-vf", $"fps={fps},scale={scale}",
            "-q:v", "7", "-f", "mpjpeg", "-"
        ];
    }

    private static IReadOnlyList<string> BuildRtspH264Fmp4Arguments(string rtspUrl, bool isMain)
    {
        var scale = isMain ? "1280:-2" : "960:-2";
        var bitrate = isMain ? "2500k" : "1200k";
        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            "-fflags", "nobuffer+genpts",
            "-flags", "low_delay",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            "-i", rtspUrl,
            "-an", "-map", "0:v:0", "-vf", $"scale={scale}",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-pix_fmt", "yuv420p",
            "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", "800k",
            "-g", "30", "-bf", "0", "-f", "mp4",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-"
        ];
    }

    private static IReadOnlyList<string> BuildRtspFmp4Arguments(string rtspUrl)
        =>
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            "-fflags", "nobuffer+genpts",
            "-flags", "low_delay",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            "-i", rtspUrl,
            "-an", "-c:v", "copy",
            "-f", "mp4",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-"
        ];

    private static IReadOnlyList<string> BuildRtspH264TsArguments(string rtspUrl, bool isMain)
    {
        var scale = isMain ? "1280:-2" : "960:-2";
        var bitrate = isMain ? "2500k" : "1200k";
        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            "-fflags", "nobuffer+genpts",
            "-flags", "low_delay",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            "-i", rtspUrl,
            "-an", "-map", "0:v:0", "-vf", $"scale={scale}",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-pix_fmt", "yuv420p",
            "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", "800k",
            "-g", "30", "-bf", "0", "-f", "mpegts", "-flush_packets", "1", "-"
        ];
    }

    private static string QuoteForDiagnostics(string argument)
        => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

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
        foreach (var s in _fmp4Sessions.Values)
        {
            await s.DisposeAsync();
        }

        _sessions.Clear();
        _fmp4Sessions.Clear();
    }

    /// <summary>One bounded H.264 fMP4 process shared by browser and native viewers.</summary>
    private sealed class SharedFmp4Session(
        string key,
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
        private int _generation;
        private CancellationTokenSource? _cts;

        public async Task WriteToAsync(Stream output, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            lock (_gate)
            {
                _subscribers[id] = channel;
                _generation++;
            }

            var gotBytes = false;
            try
            {
                await EnsureStartedAsync(cancellationToken);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(10));
                await foreach (var chunk in channel.Reader.ReadAllAsync(linked.Token))
                {
                    if (!gotBytes)
                    {
                        gotBytes = true;
                        linked.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                    await output.WriteAsync(chunk, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                if (!gotBytes)
                {
                    throw new InvalidOperationException("Shared H.264 fMP4 session produced no media.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !gotBytes)
            {
                throw new InvalidOperationException("Shared H.264 fMP4 session produced no media in time.");
            }
            finally
            {
                var generation = 0;
                lock (_gate)
                {
                    _subscribers.TryRemove(id, out _);
                    _generation++;
                    if (_subscribers.IsEmpty) generation = _generation;
                }
                channel.Writer.TryComplete();
                if (generation != 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        DisposeIfStillIdle(generation);
                    });
                }
            }
        }

        private async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            try
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                var (ffmpeg, args) = await owner.BuildRtspH264Fmp4CommandAsync(deviceId, quality, cancellationToken);
                logger.LogInformation("Shared H264 fMP4 session start {Ip} q={Q}", device.IpAddress, quality);
                _process = StartFfmpeg(ffmpeg, args);
                _pumpTask = Task.Run(() => PumpAsync(cts.Token), CancellationToken.None);
            }
            catch
            {
                Interlocked.Exchange(ref _started, 0);
                try { _cts?.Cancel(); } catch { /* best effort */ }
                if (_process is not null) TryKill(_process);
                throw;
            }
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            if (_process is null) return;
            try
            {
                var buffer = new byte[64 * 1024];
                var stdout = _process.StandardOutput.BaseStream;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stdout.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read <= 0) break;
                    var chunk = buffer.AsSpan(0, read).ToArray();
                    foreach (var pair in _subscribers.ToArray())
                    {
                        if (!pair.Value.Writer.TryWrite(chunk))
                        {
                            // Never drop bytes from a live MP4 stream. Drop only the slow
                            // subscriber, preserving a valid stream for all other viewers.
                            pair.Value.Writer.TryComplete(new InvalidOperationException("Viewer fell behind the live fMP4 session."));
                            _subscribers.TryRemove(pair.Key, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Shared H264 fMP4 pump ended for {Ip}", device.IpAddress);
            }
            finally
            {
                owner.RemoveFmp4Session(key, this);
                foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
                if (_process is not null) TryKill(_process);
                Interlocked.Exchange(ref _started, 0);
            }
        }

        private void DisposeIfStillIdle(int generation)
        {
            lock (_gate)
            {
                if (!_subscribers.IsEmpty || generation != _generation) return;
                DisposeCoreNoLock();
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate) DisposeCoreNoLock();
            return ValueTask.CompletedTask;
        }

        private void DisposeCoreNoLock()
        {
            try { _cts?.Cancel(); } catch { /* best effort */ }
            if (_process is not null) TryKill(_process);
            foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
            _subscribers.Clear();
            Interlocked.Exchange(ref _started, 0);
        }
    }

    private void RemoveFmp4Session(string key, SharedFmp4Session session)
    {
        ((ICollection<KeyValuePair<string, SharedFmp4Session>>)_fmp4Sessions)
            .Remove(new KeyValuePair<string, SharedFmp4Session>(key, session));
    }

    /// <summary>One ffmpeg process, many HTTP viewers.</summary>
    private sealed class SharedMjpegSession(
        string key,
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
        private int _subscriberGeneration;
        private CancellationTokenSource? _cts;

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                try
                {
                    var cts = new CancellationTokenSource();
                    _cts = cts;
                    var (ffmpeg, args) = await owner.BuildRtspMjpegCommandAsync(deviceId, quality, cancellationToken);
                    logger.LogInformation("Shared RTSP session start {Ip} q={Q}", device.IpAddress, quality);
                    _process = StartFfmpeg(ffmpeg, args);
                    _pumpTask = Task.Run(() => PumpAsync(cts.Token), CancellationToken.None);
                }
                catch
                {
                    Interlocked.Exchange(ref _started, 0);
                    try { _cts?.Cancel(); } catch { /* best effort */ }
                    if (_process is not null) TryKill(_process);
                    throw;
                }
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
            lock (_gate)
            {
                _subscribers[id] = channel;
                _subscriberGeneration++;
            }
            var gotFrame = false;
            try
            {
                await EnsureStartedAsync(cancellationToken);
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
                var generation = 0;
                lock (_gate)
                {
                    _subscribers.TryRemove(id, out _);
                    _subscriberGeneration++;
                    if (_subscribers.IsEmpty)
                    {
                        generation = _subscriberGeneration;
                    }
                }

                if (generation != 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        DisposeIfStillIdle(generation);
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
                // Remove this exact failed session before completing subscribers. A reconnecting
                // request must receive a fresh session, never the process that just died.
                owner.RemoveSession(key, this);
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

        private void DisposeIfStillIdle(int generation)
        {
            lock (_gate)
            {
                if (!_subscribers.IsEmpty || generation != _subscriberGeneration)
                {
                    return;
                }

                DisposeCoreNoLock();
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                DisposeCoreNoLock();
            }
            return ValueTask.CompletedTask;
        }

        private void DisposeCoreNoLock()
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
        }
    }
}
