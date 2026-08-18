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
        // Compatibility output is deliberately bounded: veryfast H.264, no B-frames,
        // explicit maxrate/bufsize, and a low-latency MPEG-TS pipe.
        var args = BuildRtspH264TsArguments(rtspUrl, IsMain(quality));
        logger.LogInformation("Live MPEG-TS {Ip} q={Q}", device.IpAddress, quality);
        try
        {
            await RunFfmpegCopyAsync(ffmpeg, args, output, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // One-shot TS playback produced no media — the cached probe verdict is stale.
            await InvalidateNetSdkVerdictAsync(deviceId, cancellationToken);
            throw;
        }
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
            // Playback failed through the shared RTSP session — drop the persisted NetSDK probe
            // verdict so the next source resolution re-probes instead of re-serving cached paths
            // that just failed (camera rebooted / port changed / plane flipped to digest-only).
            await InvalidateNetSdkVerdictAsync(deviceId, cancellationToken);
            await StreamMjpegFromSnapshotPumpAsync(device, output, quality, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
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
        // Browser MSE and native fallback both consume this one bounded H.264 fMP4 session.
        try
        {
            await session.WriteToAsync(output, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Shared fMP4 session produced no media — the cached probe verdict is stale.
            await InvalidateNetSdkVerdictAsync(deviceId, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Direct HEVC fMP4 output for native clients that decode HEVC locally (the Avalonia
    /// desktop runs ffmpeg, which decodes the camera's native HEVC). Server-side this is a
    /// pure codec copy — no transcode, no re-encode, no resolution loss, and no extra CPU —
    /// so it is the fastest and highest-quality path available. One shared session per
    /// camera fans out to every desktop viewer (single RTSP connection per camera).
    /// </summary>
    public async Task StreamHevcFmp4Async(
        Guid deviceId,
        Stream output,
        string quality,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Device not found.");
        var normalizedQuality = IsMain(quality) ? "main" : "sub";
        var key = $"hevc:{deviceId:N}:{normalizedQuality}";
        var session = _fmp4Sessions.GetOrAdd(
            key,
            _ => new SharedFmp4Session(key, deviceId, device, normalizedQuality, this, logger, useHevcCopy: true));
        try
        {
            await session.WriteToAsync(output, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Shared HEVC session produced no media — the cached probe verdict is stale.
            await InvalidateNetSdkVerdictAsync(deviceId, cancellationToken);
            throw;
        }
    }

    public async Task<LiveMediaManifest> BuildManifestAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken,
        bool nativeClient = false)
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
        // Direct RTSP is the freshest path for native clients (desktop ffmpeg decodes HEVC
        // locally): no server HTTP hop, no fragment-alignment delay, one connection straight
        // to the camera. Only advertised when the probe just succeeded, so a dead RTSP never
        // leads the ladder; the negotiated HTTP modes below remain the automatic fallback.
        // Note: this adds one RTSP connection per desktop viewer alongside the server's
        // shared session (tile + fullscreen + a browser viewer ≈ up to 3 per camera, within
        // the 5523-W's 4–6 slot range). If a camera ever rejects the extra connection the
        // desktop's ladder self-heals by falling back to the server HTTP path.
        var rtspUrl = nativeClient && rtspPlayable
            && selected?.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp
            && !string.IsNullOrWhiteSpace(selected.Url)
            ? EnsureCredentials(selected.Url, device)
            : string.Empty;
        // Native clients (the desktop's local ffmpeg) decode HEVC directly, so they get the
        // zero-transcode path; browsers cannot decode HEVC and negotiate H.264 instead.
        var negotiated = LiveMediaNegotiationPolicy.Resolve(facts, browserSupportsHevc: nativeClient);
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
            SnapshotUrl = $"{basePath}/snapshot",
            RtspUrl = rtspUrl
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

    /// <summary>
    /// Drops the persisted NetSDK probe verdict after a playback failure (shared RTSP session
    /// exhausted its retries, shared fMP4 session produced no media). The next source resolution
    /// re-probes the camera instead of re-serving cached paths that just failed. Best-effort:
    /// a store failure must not break the streaming fallback, and cancellation is never swallowed.
    /// </summary>
    private async Task InvalidateNetSdkVerdictAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await NetSdkProbeVerdictCache.InvalidateAsync(store, deviceId, cancellationToken);
            logger.LogDebug("Invalidated NetSDK probe verdict after playback failure for {DeviceId}", deviceId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to invalidate NetSDK probe verdict after playback failure for {DeviceId}", deviceId);
        }
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

    internal async Task<(string Ffmpeg, IReadOnlyList<string> Args)> BuildRtspHevcFmp4CommandAsync(
        Guid deviceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var (_, rtspUrl) = await ResolveRtspAsync(deviceId, quality, cancellationToken);
        var ffmpeg = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found.");
        // Direct HEVC: codec copy into fragmented MP4 — zero transcode, native resolution.
        return (ffmpeg, BuildRtspFmp4Arguments(rtspUrl));
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

        // Explicit Basic first (works on firmware whose 401 carries no WWW-Authenticate
        // challenge — the handler's credential cache can never negotiate those, so the pump
        // would 401 forever); only when the camera rejects Basic AND issues a real Digest
        // challenge do we retry header-less so the handler answers the Digest round-trip.
        var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        using var snapshotHandler = new HttpClientHandler
        {
            Credentials = new System.Net.NetworkCredential(user, password),
            PreAuthenticate = true
        };
        using var snapshotClient = new HttpClient(snapshotHandler)
        {
            Timeout = TimeSpan.FromSeconds(5)
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
                        var url = $"http://{device.IpAddress}:{port}{path}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
                        using var res = await snapshotClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (!res.IsSuccessStatusCode)
                        {
                            // 401 + a real Digest challenge: retry without the explicit Basic
                            // header so the handler's credential cache answers Digest instead.
                            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                && res.Headers.WwwAuthenticate.Any(static h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)))
                            {
                                using var digestReq = new HttpRequestMessage(HttpMethod.Get, url);
                                using var digestRes = await snapshotClient.SendAsync(digestReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                                if (digestRes.IsSuccessStatusCode)
                                {
                                    var digestBytes = await digestRes.Content.ReadAsByteArrayAsync(cancellationToken);
                                    if (digestBytes.Length > 500 && digestBytes[0] == 0xFF && digestBytes[1] == 0xD8)
                                    {
                                        jpeg = digestBytes;
                                        break;
                                    }
                                }
                            }
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
        // Resolve the requested quality explicitly. Using decision.Preferred here made a
        // sub-quality request silently open the main source whenever the preferred source
        // was the main stream, which increased decode load and could starve the live feed.
        url = SelectManifestSource(decision, quality)?.Url
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

    /// <summary>
    /// Extracts the fMP4 initialization segment (all top-level boxes before the first
    /// <c>moof</c>). A newly joined decoder must receive this <c>ftyp</c>/<c>moov</c>
    /// prefix before media fragments; otherwise a fullscreen viewer joining an already
    /// running shared session can remain blank indefinitely.
    /// </summary>
    internal static byte[]? TryExtractFmp4InitializationSegment(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var size32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var headerSize = 8;
            ulong boxSize = size32;
            if (size32 == 1)
            {
                if (offset + 16 > data.Length)
                {
                    return null;
                }
                boxSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                return null;
            }

            if (boxSize < (uint)headerSize || boxSize > (ulong)(data.Length - offset))
            {
                return null;
            }

            if (type == "moof")
            {
                return data[..offset].ToArray();
            }

            offset = checked(offset + (int)boxSize);
        }

        return null;
    }

    internal static IReadOnlyList<string> BuildRtspMjpegArguments(string rtspUrl, bool isMain)
    {
        var scale = isMain ? "960:-2" : "640:-2";
        // The 5523-W main stream captures at 15 fps; the old 10/12 caps rendered
        // below the camera's rate on every platform. Target the camera's own rate.
        var fps = "15";
        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            // Preserve the HEVC reference chain on ordered TCP. Do not disable decoder
            // frame reordering: that drops POC references on 5523-W and turns a 15 fps
            // source into a slideshow.
            "-fflags", "genpts+discardcorrupt",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            // TCP preserves packet order, but HEVC still requires ffmpeg's normal
            // reorder queue for reference frames. A zero queue produced POC/reference
            // errors on 5523-W and collapsed the source into a slideshow.
            // RTSP demuxer socket I/O timeout (µs): a silent media stall (5523-W drops
            // frames but keeps the TCP socket open) must abort ffmpeg instead of serving
            // the last frame forever. Mirrors the recording pipeline's verified flag.
            "-timeout", "10000000",
            "-i", rtspUrl,
            "-an", "-map", "0:v:0",
            "-vf", $"fps={fps},scale={scale}",
            "-q:v", "3", "-f", "mpjpeg", "-"
        ];
    }

    internal static IReadOnlyList<string> BuildRtspH264Fmp4Arguments(string rtspUrl, bool isMain)
    {
        // H.264 serves only clients that cannot decode HEVC (browsers). Keep the transcode
        // light (veryfast, no B-frames) but give it real bandwidth — 2500k was visibly
        // soft at 1280 wide. VBV buffer sized to the bitrate so rate control stays smooth.
        var scale = isMain ? "1920:-2" : "960:-2";
        var bitrate = isMain ? "4000k" : "2000k";
        var bufsize = isMain ? "8000k" : "4000k";
        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            // Preserve the HEVC reference chain on ordered TCP. Do not disable decoder
            // frame reordering: that drops POC references on 5523-W and turns a 15 fps
            // source into a slideshow.
            "-fflags", "genpts+discardcorrupt",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            // TCP preserves packet order, but HEVC still requires ffmpeg's normal
            // reorder queue for reference frames. A zero queue produced POC/reference
            // errors on 5523-W and collapsed the source into a slideshow.
            // RTSP demuxer socket I/O timeout (µs): a silent media stall (5523-W drops
            // frames but keeps the TCP socket open) must abort ffmpeg instead of serving
            // the last frame forever. Mirrors the recording pipeline's verified flag.
            "-timeout", "10000000",
            "-i", rtspUrl,
            "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"scale={scale}",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-pix_fmt", "yuv420p",
            "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize,
            "-c:a", "aac", "-b:a", "128k",
            "-g", "15", "-bf", "0", "-f", "mp4",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-"
        ];
    }

    internal static IReadOnlyList<string> BuildRtspFmp4Arguments(string rtspUrl)
        =>
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            // Preserve the HEVC reference chain on ordered TCP. Do not disable decoder
            // frame reordering: that drops POC references on 5523-W and turns a 15 fps
            // source into a slideshow.
            "-fflags", "genpts+discardcorrupt",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            // TCP preserves packet order, but HEVC still requires ffmpeg's normal
            // reorder queue for reference frames. A zero queue produced POC/reference
            // errors on 5523-W and collapsed the source into a slideshow.
            // RTSP demuxer socket I/O timeout (µs): a silent media stall (5523-W drops
            // frames but keeps the TCP socket open) must abort ffmpeg instead of serving
            // the last frame forever. Mirrors the recording pipeline's verified flag.
            "-timeout", "10000000",
            "-i", rtspUrl,
            "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "copy",
            "-c:a", "aac", "-b:a", "128k",
            "-f", "mp4",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-"
        ];

    internal static IReadOnlyList<string> BuildRtspH264TsArguments(string rtspUrl, bool isMain)
    {
        // Same quality floor as the H.264 fMP4 path (this is the native MPEG-TS fallback).
        var scale = isMain ? "1920:-2" : "960:-2";
        var bitrate = isMain ? "4000k" : "2000k";
        var bufsize = isMain ? "8000k" : "4000k";
        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            // Preserve the HEVC reference chain on ordered TCP. Do not disable decoder
            // frame reordering: that drops POC references on 5523-W and turns a 15 fps
            // source into a slideshow.
            "-fflags", "genpts+discardcorrupt",
            "-probesize", "2000000",
            "-analyzeduration", "2000000",
            "-max_delay", "500000",
            // TCP preserves packet order, but HEVC still requires ffmpeg's normal
            // reorder queue for reference frames. A zero queue produced POC/reference
            // errors on 5523-W and collapsed the source into a slideshow.
            // RTSP demuxer socket I/O timeout (µs): a silent media stall (5523-W drops
            // frames but keeps the TCP socket open) must abort ffmpeg instead of serving
            // the last frame forever. Mirrors the recording pipeline's verified flag.
            "-timeout", "10000000",
            "-i", rtspUrl,
            "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"scale={scale}",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-pix_fmt", "yuv420p",
            "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize,
            "-c:a", "aac", "-b:a", "128k",
            "-g", "15", "-bf", "0", "-f", "mpegts", "-flush_packets", "1", "-"
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
        ILogger logger,
        bool useHevcCopy = false) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _startupReplayGate = new(1, 1);
        private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _subscribers = new();
        private Process? _process;
        private Task? _pumpTask;
        private int _started;
        private int _generation;
        private CancellationTokenSource? _cts;
        private Task? _stallWatchdogTask;
        private Task? _startupTask;
        private long _lastChunkUtcTicks;
        private Exception? _pumpFailure;
        private MemoryStream? _startupBuffer;
        private byte[]? _initializationSegment;

        /// <summary>Human-readable session mode for logs ("/"HEVC copy" vs "H264").</summary>
        private string Mode => useHevcCopy ? "HEVC copy" : "H264";

        public async Task WriteToAsync(Stream output, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            await _startupReplayGate.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    _subscribers[id] = channel;
                    _generation++;
                    // A late subscriber cannot decode a fragment by itself. Replay the cached
                    // fMP4 init segment before the pump sends its next moof/mdat bytes.
                    if (_initializationSegment is { Length: > 0 } initialization)
                    {
                        channel.Writer.TryWrite(initialization);
                    }
                }
            }
            finally
            {
                _startupReplayGate.Release();
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
                    throw new InvalidOperationException($"Shared {Mode} fMP4 session produced no media.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !gotBytes)
            {
                throw new InvalidOperationException($"Shared {Mode} fMP4 session produced no media in time.");
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
            Task startup;
            lock (_gate)
            {
                // Every viewer awaits the same startup task. This prevents a late fullscreen
                // viewer from observing _started=1 before ffmpeg exists, and lets idle teardown
                // cancel the session-owned startup before an orphan process can be launched.
                if (_startupTask is null || _startupTask.IsCompleted)
                {
                    _startupTask = StartProcessAsync();
                }

                startup = _startupTask;
            }

            await startup.WaitAsync(cancellationToken);
        }

        private async Task StartProcessAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return;
            }

            // A session may be restarted after a stalled process. The new ffmpeg process
            // must never inherit ftyp/moov bytes from the previous process: stale init data
            // before the new stream's moof can leave a late fullscreen decoder blank.
            lock (_gate)
            {
                _initializationSegment = null;
                _startupBuffer?.Dispose();
                _startupBuffer = null;
                _pumpFailure = null;
            }

            try
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                // Startup is shared by every subscriber. Do not bind process startup to the
                // first HTTP request's cancellation token: a second viewer must not inherit a
                // transient cancellation from the first viewer and wait on a dead channel.
                // The session-owned token is canceled as soon as the last subscriber
                // leaves. Propagate it through source resolution so a slow probe cannot
                // finish and launch an orphan ffmpeg process after teardown.
                var (ffmpeg, args) = useHevcCopy
                    ? await owner.BuildRtspHevcFmp4CommandAsync(deviceId, quality, cts.Token)
                    : await owner.BuildRtspH264Fmp4CommandAsync(deviceId, quality, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_subscribers.IsEmpty)
                    {
                        cts.Cancel();
                        Interlocked.Exchange(ref _started, 0);
                        return;
                    }
                }

                logger.LogInformation(
                    "Shared {Mode} fMP4 session start {Ip} q={Q}",
                    Mode, device.IpAddress, quality);
                _process = StartFfmpeg(ffmpeg, args);
                _pumpTask = Task.Run(() => PumpAsync(cts.Token), CancellationToken.None);
                // Baseline the stall watchdog at the fresh process (first-bytes deadline owns
                // the initial window).
                Interlocked.Exchange(ref _lastChunkUtcTicks, 0);
                _stallWatchdogTask = Task.Run(() => StallWatchdogLoopAsync(cts.Token), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _started, 0);
                try { _cts?.Cancel(); } catch { /* best effort */ }
                if (_process is not null) TryKill(_process);
                // Wake every concurrent subscriber immediately. Without this, subscribers
                // arriving during startup remained on an uncompleted channel until timeout.
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete(ex);
                }
                throw;
            }
        }

        /// <summary>
        /// Kills the ffmpeg process when no media bytes have been published for a while
        /// (backup to ffmpeg's -timeout socket abort). Ends the pump, which fails subscribers
        /// so viewers reconnect on a fresh session instead of freezing on stale video.
        /// </summary>
        private async Task StallWatchdogLoopAsync(CancellationToken cancellationToken)
        {
            const long thresholdTicks = 15 * TimeSpan.TicksPerSecond;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    if (_subscribers.IsEmpty)
                    {
                        continue; // idle session — the 5s idle-dispose path handles it
                    }

                    var lastTicks = Interlocked.Read(ref _lastChunkUtcTicks);
                    if (lastTicks == 0)
                    {
                        continue; // no first bytes yet — the 10s first-bytes deadline owns this
                    }

                    var silent = DateTime.UtcNow.Ticks - lastTicks;
                    if (silent <= thresholdTicks)
                    {
                        continue;
                    }

                    logger.LogWarning(
                        "Shared {Mode} fMP4 session {Key} stalled ({Seconds}s since last chunk) — killing ffmpeg for {Ip} q={Q}",
                        Mode, key, silent / TimeSpan.TicksPerSecond, device.IpAddress, quality);
                    if (_process is { HasExited: false })
                    {
                        TryKill(_process);
                    }
                    return; // the pump's finally removes the session and fails subscribers
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Shared H264 fMP4 stall watchdog ended for {Ip}", device.IpAddress);
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
                    if (_initializationSegment is null)
                    {
                        _startupBuffer ??= new MemoryStream();
                        _startupBuffer.Write(chunk, 0, chunk.Length);
                        var initialization = TryExtractFmp4InitializationSegment(_startupBuffer.ToArray());
                        if (initialization is null)
                        {
                            // Hold the short init prefix until the first moof is seen so
                            // every subscriber joining during startup receives one coherent
                            // ftyp/moov/moof sequence rather than a partial stream.
                            if (_startupBuffer.Length > 16 * 1024 * 1024)
                            {
                                throw new InvalidOperationException($"Shared {Mode} fMP4 stream has no valid initialization segment.");
                            }
                            continue;
                        }

                        var startupBytes = _startupBuffer.ToArray();
                        _startupBuffer.Dispose();
                        _startupBuffer = null;
                        // Serialize initialization publication with late-subscriber
                        // registration. Without this gate a joiner could receive the cached
                        // init segment and the startup buffer containing the same init boxes,
                        // leaving MSE/ffmpeg with a duplicated ftyp/moov prefix.
                        await _startupReplayGate.WaitAsync(cancellationToken);
                        try
                        {
                            lock (_gate)
                            {
                                _initializationSegment = initialization;
                            }
                            PublishChunk(startupBytes);
                        }
                        finally
                        {
                            _startupReplayGate.Release();
                        }
                        continue;
                    }

                    PublishChunk(chunk);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Preserve the real pump failure for subscribers. A generic "ended"
                // exception hides whether ffmpeg exited, parsing failed, or the process
                // was killed by the stall watchdog, making the desktop fallback opaque.
                _pumpFailure = ex;
                logger.LogDebug(ex, "Shared {Mode} fMP4 pump ended for {Ip}", Mode, device.IpAddress);
            }
            finally
            {
                owner.RemoveFmp4Session(key, this);
                // Fail subscribers explicitly so browser MSE / desktop viewers run their
                // reconnect logic instead of treating a dead/stalled session as clean EOF.
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete(_pumpFailure
                        ?? new InvalidOperationException($"Shared {Mode} fMP4 session ended (stalled or dropped)."));
                }
                if (_process is not null) TryKill(_process);
                // Session is done — stop the stall watchdog immediately instead of letting it
                // poll until the 5s idle-dispose runs.
                try { _cts?.Cancel(); } catch { /* best effort */ }
                lock (_gate)
                {
                    _initializationSegment = null;
                    _startupBuffer?.Dispose();
                    _startupBuffer = null;
                }
                Interlocked.Exchange(ref _started, 0);
            }
        }

        /// <summary>
        /// Publishes a complete fMP4 chunk without ever blocking the shared ffmpeg pump on
        /// one slow HTTP client. MP4 chunks cannot be dropped selectively without corrupting
        /// that client's byte stream, so a subscriber whose bounded queue is full is removed
        /// and its HTTP request reconnects; healthy viewers keep receiving every chunk at the
        /// camera cadence instead of all viewers stalling behind one slow socket.
        /// </summary>
        private void PublishChunk(byte[] chunk)
        {
            foreach (var pair in _subscribers.ToArray())
            {
                if (pair.Value.Writer.TryWrite(chunk))
                {
                    continue;
                }

                logger.LogWarning(
                    "Dropping stalled {Mode} fMP4 viewer {ViewerId} for {Ip} q={Q}; queued chunks exceeded {Capacity}",
                    Mode,
                    pair.Key,
                    device.IpAddress,
                    quality,
                    64);
                pair.Value.Writer.TryComplete(
                    new InvalidOperationException("Viewer fell behind the live fMP4 session."));
                _subscribers.TryRemove(pair.Key, out _);
            }

            Interlocked.Exchange(ref _lastChunkUtcTicks, DateTime.UtcNow.Ticks);
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
            _initializationSegment = null;
            _startupBuffer?.Dispose();
            _startupBuffer = null;
            foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
            _subscribers.Clear();
            // Do not dispose the gate here: a late request or the pump may still be leaving
            // a wait after idle teardown. The session is removed from the owner dictionary,
            // so the gate becomes unreachable with the session and is reclaimed safely.
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
        private Task? _stallWatchdogTask;
        private Task? _startupTask;
        private long _lastFrameUtcTicks;

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            Task startup;
            lock (_gate)
            {
                if (_startupTask is null || _startupTask.IsCompleted)
                {
                    _startupTask = StartProcessAsync();
                }

                startup = _startupTask;
            }

            await startup.WaitAsync(cancellationToken);

            // Wait briefly for first frame so clients don't hang on black.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (_subscribers.IsEmpty && _process is { HasExited: true })
                {
                    break;
                }

                // Session is up once the pump is running.
                if (_pumpTask is not null)
                {
                    break;
                }

                await Task.Delay(50, cancellationToken);
            }
        }

        private async Task StartProcessAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                // Cancel source resolution with the session token too; otherwise a slow
                // transport probe can start an orphan process after the final subscriber
                // has already disconnected.
                var (ffmpeg, args) = await owner.BuildRtspMjpegCommandAsync(deviceId, quality, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_subscribers.IsEmpty)
                    {
                        cts.Cancel();
                        Interlocked.Exchange(ref _started, 0);
                        return;
                    }
                }

                logger.LogInformation("Shared RTSP session start {Ip} q={Q}", device.IpAddress, quality);
                _process = StartFfmpeg(ffmpeg, args);
                _pumpTask = Task.Run(() => PumpAsync(cts.Token), CancellationToken.None);
                // Baseline the stall watchdog at the fresh process: no frames yet is the
                // first-frame window's job (14s deadline), not a stall.
                Interlocked.Exchange(ref _lastFrameUtcTicks, 0);
                _stallWatchdogTask = Task.Run(() => StallWatchdogLoopAsync(cts.Token), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _started, 0);
                try { _cts?.Cancel(); } catch { /* best effort */ }
                if (_process is not null) TryKill(_process);
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete(ex);
                }
                throw;
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

        /// <summary>
        /// Kills the ffmpeg process when no complete frame has been published for a while.
        /// ffmpeg's -timeout aborts socket-level stalls; this watchdog covers the rarer case
        /// where ffmpeg keeps a session alive but stops producing decodable frames. Killing the
        /// process ends the pump, which removes the session and fails subscribers, so viewers
        /// reconnect on a fresh session instead of staring at a frozen frame.
        /// </summary>
        private async Task StallWatchdogLoopAsync(CancellationToken cancellationToken)
        {
            const long thresholdTicks = 12 * TimeSpan.TicksPerSecond;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    if (_subscribers.IsEmpty)
                    {
                        continue; // idle session — the 5s idle-dispose path handles it
                    }

                    var lastTicks = Interlocked.Read(ref _lastFrameUtcTicks);
                    if (lastTicks == 0)
                    {
                        continue; // no first frame yet — the 14s first-frame deadline owns this
                    }

                    var silent = DateTime.UtcNow.Ticks - lastTicks;
                    if (silent <= thresholdTicks)
                    {
                        continue;
                    }

                    logger.LogWarning(
                        "Shared RTSP session {Key} stalled ({Seconds}s since last frame) — killing ffmpeg for {Ip} q={Q}",
                        key, silent / TimeSpan.TicksPerSecond, device.IpAddress, quality);
                    if (_process is { HasExited: false })
                    {
                        TryKill(_process);
                    }
                    return; // the pump's finally removes the session and fails subscribers
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Shared RTSP stall watchdog ended for {Ip}", device.IpAddress);
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
                // Scans the accumulator's backing buffer in place — no per-read ToArray copy
                // of the whole accumulator (which was O(n²) memory traffic as it grew).
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
                // Fail subscribers explicitly (not a clean end-of-stream) so the shared-session
                // retry ladder in StreamMjpegAsync reconnects with a fresh process instead of
                // the HTTP response silently ending and leaving a stale frame on screen.
                foreach (var ch in _subscribers.Values)
                {
                    ch.Writer.TryComplete(new InvalidOperationException("Shared RTSP session ended (stalled or dropped)."));
                }

                if (_process is not null)
                {
                    TryKill(_process);
                }

                // Session is done — stop the stall watchdog immediately instead of letting it
                // poll until the 5s idle-dispose runs.
                try { _cts?.Cancel(); } catch { /* best effort */ }
                Interlocked.Exchange(ref _started, 0);
            }
        }

        private void ExtractAndPublishJpegs(MemoryStream acc)
        {
            // Work directly on the backing buffer (GetBuffer) bounded by Length — the
            // buffer is reused across reads, so no per-read allocation. Complete frames
            // are extracted into fresh arrays (subscribers each need an independent copy);
            // a partial trailing frame is kept in place for the next read.
            var data = acc.GetBuffer();
            var length = (int)acc.Length;
            var searchFrom = 0;
            var partialFrom = -1;
            var partialLen = 0;
            while (true)
            {
                var soi = IndexOf(data, [0xFF, 0xD8], searchFrom, length);
                if (soi < 0)
                {
                    break;
                }

                var eoi = IndexOf(data, [0xFF, 0xD9], soi + 2, length);
                if (eoi < 0)
                {
                    // incomplete frame — keep from SOI (SOI at 0 is a valid, common case)
                    partialFrom = soi;
                    partialLen = length - soi;
                    break;
                }

                var len = eoi + 2 - soi;
                var jpeg = new byte[len];
                Buffer.BlockCopy(data, soi, jpeg, 0, len);
                foreach (var ch in _subscribers.Values)
                {
                    ch.Writer.TryWrite(jpeg);
                }
                Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);

                searchFrom = eoi + 2;
            }

            // Trim consumed bytes. If a partial frame remains, slide it to the front so
            // the next read appends directly after it (avoids unbounded growth).
            if (partialFrom >= 0)
            {
                // incomplete frame from SOI — keep the tail regardless of its position
                Buffer.BlockCopy(data, partialFrom, data, 0, partialLen);
                acc.SetLength(partialLen);
            }
            else if (searchFrom > 0 && searchFrom < length)
            {
                // consumed frames, partial tail remains — keep from searchFrom
                var keepLen = length - searchFrom;
                Buffer.BlockCopy(data, searchFrom, data, 0, keepLen);
                acc.SetLength(keepLen);
            }
            else if (searchFrom >= length)
            {
                // everything consumed — clear
                acc.SetLength(0);
            }
            else if (length > 2 * 1024 * 1024)
            {
                // avoid unbounded growth if stream is garbage
                acc.SetLength(0);
            }
            // else: no SOI yet and the buffer is small — keep everything for the next read
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start, int length)
        {
            for (var i = start; i <= length - needle.Length; i++)
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
