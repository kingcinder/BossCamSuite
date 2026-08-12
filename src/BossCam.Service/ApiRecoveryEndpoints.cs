using BossCam.Core;

namespace BossCam.Service;

/// <summary>Request to start a camera recovery run (AP hotspot → LAN → Suite enrollment).</summary>
public sealed record CameraRecoveryStartRequest(string Serial, string? ApSsid, bool DryRun = false);

/// <summary>
/// Maps the camera-recovery surface: scan the host WiFi for factory-reset camera
/// APs (IPCZ7C34…), start a background recover-and-enroll run, and poll status.
/// </summary>
public static class ApiRecoveryEndpoints
{
    public static WebApplication MapRecoveryEndpoints(this WebApplication app)
    {
        // Non-destructive: rescan + list visible camera APs only.
        app.MapGet("/api/recovery/scan", async (CameraRecoveryService recovery, CancellationToken ct) =>
        {
            var aps = await recovery.ScanCameraApsAsync(ct);
            return Results.Ok(new { aps, count = aps.Count });
        });

        // Start the background recovery pipeline. Returns immediately with a run id.
        app.MapPost("/api/recovery/recover", (CameraRecoveryService recovery, CameraRecoveryStartRequest? request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Serial))
            {
                return Results.BadRequest(new { error = "serial is required." });
            }

            var target = string.IsNullOrWhiteSpace(request.ApSsid) ? request.Serial : request.ApSsid;
            try
            {
                var runId = recovery.StartRecovery(target, ct, dryRun: request.DryRun);
                return Results.Accepted($"/api/recovery/status/{runId}", new { runId, serial = request.Serial, dryRun = request.DryRun });
            }
            catch (InvalidOperationException ex)
            {
                // One-radio invariant: a recovery (manual or autonomous) is already in flight.
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapGet("/api/recovery/status/{runId}", (CameraRecoveryService recovery, string runId) =>
        {
            var status = recovery.GetStatus(runId);
            return status is null ? Results.NotFound(new { error = $"No recovery run '{runId}'." }) : Results.Ok(status);
        });

        // Autonomous scan status: whether the self-driving worker is enabled, the last scan
        // outcome, and any currently-active recovery run. The UI polls this to show that the
        // Suite is watching for factory-reset cameras on its own.
        app.MapGet("/api/recovery/auto/status", async (CameraRecoveryService recovery, CancellationToken ct) =>
        {
            var status = recovery.GetAutoStatus();
            if (status.Enabled)
            {
                status = status with { CurrentSsid = await recovery.GetCurrentSsidAsync(ct) };
            }

            return Results.Ok(status);
        });

        return app;
    }
}
