using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Service;

/// <summary>
/// Maps recording CRUD, start/stop/all, index, export, reconcile, housekeeping,
/// and highlight-board endpoints.
/// </summary>
public static class ApiRecordingsEndpoints
{
    public static WebApplication MapRecordingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recordings", async (Guid? deviceId, IApplicationStore store, CancellationToken ct) =>
            Results.Ok(await store.GetRecordingProfilesAsync(deviceId, ct)));

        app.MapPost("/api/recordings", async (IEnumerable<RecordingProfile> profiles, IApplicationStore store, CancellationToken ct) =>
        {
            await store.SaveRecordingProfilesAsync(profiles, ct);
            return Results.Accepted();
        });

        app.MapPost("/api/recordings/start", async (RecordingStartRequest request, RecordingService recordingService, CancellationToken ct) =>
        {
            try
            {
                var job = await recordingService.StartAsync(request, ct);
                return Results.Ok(job);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Device not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No video source", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("ffmpeg not found", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireRateLimiting("recordings-start");

        app.MapPost("/api/recordings/start-all", async (bool? preferSubStream, RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.StartAllAsync(preferSubStream ?? false, ct)));

        app.MapPost("/api/recordings/stop-all", async (RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.StopAllAsync(ct)));

        app.MapPost("/api/recordings/stop/{jobId:guid}", async (Guid jobId, RecordingService recordingService, CancellationToken ct) =>
        {
            var job = await recordingService.StopAsync(jobId, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        app.MapGet("/api/recordings/jobs", async (RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.GetJobsAsync(ct)));

        app.MapPost("/api/recordings/index/refresh", async (Guid? deviceId, RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.RefreshIndexAsync(deviceId, ct)));

        app.MapGet("/api/recordings/index", async (Guid? deviceId, int? limit, RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.GetIndexedSegmentsAsync(deviceId, limit ?? 500, ct)));

        app.MapPost("/api/recordings/export", async (ClipExportRequest request, RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.ExportClipAsync(request, ct)));

        // PR-R3: Download a clip by path (must be under storage root for safety)
        app.MapGet("/api/recordings/download", async (string path, IOptions<BossCamRuntimeOptions> runtime, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest(new { error = "path query parameter is required." });
            }

            // Resolve storage root inline (same logic as ApiStorageEndpoints.ResolveStorageRoot)
            string storageRoot;
            if (!string.IsNullOrWhiteSpace(runtime.Value.StorageRoot))
            {
                storageRoot = Path.GetFullPath(runtime.Value.StorageRoot.Trim());
            }
            else
            {
                var dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BossCamSuite");
                storageRoot = Path.Combine(dataRoot, "recordings");
            }

            var fullPath = Path.GetFullPath(path.Trim());
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(Path.GetFullPath(storageRoot), comparison))
            {
                return Results.StatusCode(403);
            }

            if (!File.Exists(fullPath))
            {
                return Results.NotFound();
            }

            var fileName = Path.GetFileName(fullPath);
            var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4",
                ".ts" => "video/mp2t",
                ".mkv" => "video/x-matroska",
                _ => "application/octet-stream"
            };
            return Results.File(fullPath, contentType, fileName);
        });

        app.MapPost("/api/recordings/stall-check", async (RecordingService recordingService, IOptions<BossCamRuntimeOptions> options, CancellationToken ct) =>
        {
            var timeout = options.Value.StallTimeoutSeconds;
            var autoRestart = options.Value.StallAutoRestart;
            var stalled = await recordingService.CheckStalledJobsAsync(timeout, autoRestart, ct);
            return Results.Ok(new { checked = true, stalled = stalled.Count, autoRestart, stalled });
        });

        app.MapPost("/api/recordings/reconcile", async (RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.ReconcileAutoStartAsync(ct)));

        app.MapPost("/api/recordings/housekeeping", async (Guid? deviceId, RecordingService recordingService, CancellationToken ct) =>
            Results.Ok(await recordingService.RunHousekeepingAsync(deviceId, ct)));

        // Highlight board — closely related to recordings (select, play, record).
        app.MapGet("/api/highlights", async (HighlightBoardService highlights, CancellationToken ct) =>
            Results.Ok(await highlights.GetStateAsync(ct)));

        app.MapPost("/api/highlights/select/{deviceId:guid}", async (Guid deviceId, HighlightBoardService highlights, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await highlights.SelectAsync(deviceId, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapPost("/api/highlights/next", async (HighlightBoardService highlights, CancellationToken ct) =>
            Results.Ok(await highlights.FlipAsync(+1, ct)));

        app.MapPost("/api/highlights/prev", async (HighlightBoardService highlights, CancellationToken ct) =>
            Results.Ok(await highlights.FlipAsync(-1, ct)));

        app.MapPost("/api/highlights/stream/{mode}", async (string mode, HighlightBoardService highlights, CancellationToken ct) =>
            Results.Ok(await highlights.SetPreferredStreamAsync(mode, ct)));

        app.MapPost("/api/highlights/record-selected", async (HighlightBoardService highlights, CancellationToken ct) =>
            Results.Ok(await highlights.RecordSelectedAsync(ct)));

        return app;
    }
}
