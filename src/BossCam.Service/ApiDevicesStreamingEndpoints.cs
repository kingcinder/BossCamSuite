using BossCam.Contracts;
using BossCam.Core;
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
            var port = device.Port <= 0 ? 80 : device.Port;
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
            var candidatePaths = new[]
            {
                $"/NetSDK/Video/encode/channel/101/snapShot",
                $"/NetSDK/Video/encode/channel/102/snapShot",
                $"/NetSDK/Video/input/channel/1/snapShot",
                $"/cgi-bin/snapshot.cgi",
                $"/snapshot.jpg"
            };

            // Digest-auth fallback requires per-request handler; pooled factory doesn't apply here.
            using var handler = new HttpClientHandler { Credentials = new System.Net.NetworkCredential(user, password) };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            foreach (var path in candidatePaths)
            {
                try
                {
                    var url = $"http://{device.IpAddress}:{port}{path}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
                    using var response = await client.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                    {
                        return Results.File(bytes, "image/jpeg");
                    }
                }
                catch
                {
                    // try next candidate
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
                // client hung up
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
}
