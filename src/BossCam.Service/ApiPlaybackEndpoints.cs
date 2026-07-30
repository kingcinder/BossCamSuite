using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Service;

/// <summary>
/// Maps NVR playback control endpoints through the PlayBack SDK surface.
/// Each method corresponds to a NetSDK playback operation (find-file, playback-by-time, save-data, etc.).
/// </summary>
public static class ApiPlaybackEndpoints
{
    public static WebApplication MapPlaybackEndpoints(this WebApplication app)
    {
        app.MapPost("/api/devices/{id:guid}/playback/find-file", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.FindFileAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/find-next-file", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.FindNextFileAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/get-file-by-time", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.GetFileByTimeAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/playback-by-time", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.PlayBackByTimeExAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/find-close", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.FindCloseAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/playback-by-name", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.PlayBackByNameAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/get-file-by-name", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.GetFileByNameAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/stop-get-file", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.StopGetFileAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/playback-save-data", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.PlayBackSaveDataAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/devices/{id:guid}/playback/stop-playback-save", async (Guid id, NvrPlaybackRequest request, NvrPlaybackService playbackService, CancellationToken ct) =>
        {
            var result = await playbackService.StopPlayBackSaveAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return app;
    }
}
