using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.AspNetCore.Http.Features;

namespace BossCam.Service;

/// <summary>
/// Maps device streaming endpoints: video sources, preview, snapshot, live TS/MJPEG/fMP4, and live-info.
/// Each live endpoint pipes ffmpeg output through the response stream for browser-based viewing.
/// </summary>
public static class ApiDevicesStreamingEndpoints
{
    public static WebApplication MapDevicesStreamingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/devices/{id:guid}/sources", async (Guid id, TransportBroker transportBroker, CancellationToken ct) =>
            Results.Ok(await transportBroker.GetSourcesAsync(id, ct)));

        app.MapGet("/api/devices/{id:guid}/preview", async (Guid id, TransportBroker transportBroker, CancellationToken ct) =>
        {
            var result = await transportBroker.StartPreviewAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/devices/{id:guid}/snapshot", async (Guid id, IApplicationStore store, CancellationToken ct) =>
        {
            var device = await store.GetDeviceAsync(id, ct);
            if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
            {
                return Results.NotFound();
            }

            var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
            var password = device.Password ?? string.Empty;
            // Discovery can record the ONVIF/media port (8888/8899) on the device while the
            // NetSDK REST snapshot API actually listens on 80 (verified live on 5523-W units:
            // deviceInfo returns 200 on :80, transport-fails on the recorded ONVIF port).
            // Try the recorded port first, then fall back to 80 for each candidate path.
            var ports = NetSdkPortCandidates.For(device.Port);
            var candidatePaths = new[]
            {
                $"/NetSDK/Video/encode/channel/101/snapShot",
                $"/NetSDK/Video/encode/channel/102/snapShot",
                $"/NetSDK/Video/input/channel/1/snapShot",
                $"/cgi-bin/snapshot.cgi",
                $"/snapshot.jpg"
            };

            // Some firmware generations answer Basic with a 200 directly but reject the
            // unauthenticated first request WITHOUT issuing a WWW-Authenticate challenge
            // (verified live on 5523-W units). HttpClientHandler's credential cache only
            // sends auth when a challenge arrives, so it can never authenticate against
            // those units. Send explicit Basic first; only when the camera rejects it AND
            // emits a Digest challenge do we retry header-less so the handler negotiates.
            var basicToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
            using var handler = new HttpClientHandler
            {
                Credentials = new System.Net.NetworkCredential(user, password),
                PreAuthenticate = true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            foreach (var port in ports)
            {
                foreach (var path in candidatePaths)
                {
                    try
                    {
                        var url = $"http://{device.IpAddress}:{port}{path}";
                        var bytes = await FetchSnapshotJpegAsync(client, url, basicToken, ct);
                        if (bytes is not null)
                        {
                            return Results.File(bytes, "image/jpeg");
                        }
                    }
                    catch
                    {
                        // try next candidate
                    }
                }
            }

            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }).RequireRateLimiting("snapshot");

        app.MapGet("/api/devices/{id:guid}/live.ts", async (Guid id, string? quality, HttpContext http, LiveStreamService live, CancellationToken ct) =>
        {
            http.Response.ContentType = "video/mp2t";
            http.Response.Headers.CacheControl = "no-cache, no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await http.Response.StartAsync(ct);
                await live.StreamMpegTsAsync(id, http.Response.Body, quality ?? "sub", ct);
            }
            catch (InvalidOperationException ex)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client hung up
            }
        });

        app.MapGet("/api/devices/{id:guid}/live.mjpeg", async (Guid id, string? quality, HttpContext http, LiveStreamService live, CancellationToken ct) =>
        {
            http.Response.ContentType = "multipart/x-mixed-replace;boundary=ffmpeg";
            http.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await http.Response.StartAsync(ct);
                await live.StreamMjpegAsync(id, http.Response.Body, quality ?? "sub", ct);
            }
            catch (InvalidOperationException ex)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Preserve the original /live.mp4 contract as the direct HEVC fMP4 path. Browsers
        // that cannot decode it use /live.h264.mp4 from the negotiated manifest.
        app.MapGet("/api/devices/{id:guid}/live.mp4", async (Guid id, string? quality, HttpContext http, LiveStreamService live, CancellationToken ct) =>
        {
            http.Response.ContentType = "video/mp4";
            http.Response.Headers.CacheControl = "no-cache, no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await http.Response.StartAsync(ct);
                await live.StreamFragmentedMp4Async(id, http.Response.Body, quality ?? "sub", ct);
            }
            catch (InvalidOperationException ex)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        app.MapGet("/api/devices/{id:guid}/live.h264.mp4", async (Guid id, string? quality, HttpContext http, LiveStreamService live, CancellationToken ct) =>
        {
            http.Response.ContentType = "video/mp4";
            http.Response.Headers.CacheControl = "no-cache, no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await http.Response.StartAsync(ct);
                await live.StreamH264Fmp4Async(id, http.Response.Body, quality ?? "sub", ct);
            }
            catch (InvalidOperationException ex)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client hung up
            }
        });

        app.MapGet("/api/devices/{id:guid}/live-manifest", async (Guid id, string? quality, LiveStreamService live, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await live.BuildManifestAsync(id, quality ?? "sub", ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapGet("/api/devices/{id:guid}/live-info", async (Guid id, LiveStreamService live, CancellationToken ct) =>
        {
            try
            {
                var (main, sub, preferred) = await live.DescribeAsync(id, ct);
                return Results.Ok(new { mainRtsp = main, subRtsp = sub, preferredLive = preferred });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        return app;
    }

    /// <summary>
    /// Fetches a validated JPEG snapshot with explicit Basic auth first (works on firmware whose
    /// 401 carries no WWW-Authenticate challenge and therefore can never be negotiated by the
    /// handler's credential cache), falling back to header-less handler negotiation when the
    /// camera explicitly challenges with Digest. Returns <c>null</c> when no candidate yields a
    /// JPEG so the caller can try the next port/path candidate.
    /// </summary>
    private static async Task<byte[]?> FetchSnapshotJpegAsync(
        HttpClient client,
        string url,
        string basicToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basicToken);
        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return await ReadValidJpegAsync(response, ct);
        }

        // 401 + a real Digest challenge: retry without the explicit Basic header so the
        // handler's credential cache answers the Digest round-trip instead.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && response.Headers.WwwAuthenticate.Any(static h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)))
        {
            using var digestRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var digestResponse = await client.SendAsync(digestRequest, ct);
            if (digestResponse.IsSuccessStatusCode)
            {
                return await ReadValidJpegAsync(digestResponse, ct);
            }
        }

        return null;
    }

    private static async Task<byte[]?> ReadValidJpegAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
    }
}
