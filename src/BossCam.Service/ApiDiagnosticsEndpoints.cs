using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Service;

/// <summary>
/// Maps diagnostic and monitoring endpoints: health, audit, transcripts,
/// probe sessions, and truth sweeps.
/// </summary>
public static class ApiDiagnosticsEndpoints
{
    public static WebApplication MapDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            timestamp = DateTimeOffset.UtcNow,
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            contentRoot = app.Environment.ContentRootPath,
            ffmpeg = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH")
                ?? (File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null)
        }));

        app.MapGet("/api/diagnostics/audit", async (Guid? deviceId, int? limit, IApplicationStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAuditEntriesAsync(deviceId, limit ?? 100, ct)));

        app.MapGet("/api/diagnostics/transcripts", async (Guid? deviceId, int? limit, ProtocolValidationService validationService, CancellationToken ct) =>
            Results.Ok(await validationService.GetTranscriptsAsync(deviceId, limit ?? 200, ct)));

        app.MapPost("/api/probe/sessions/start", async (ProbeSessionRequest request, ProbeSessionService probeSessionService, CancellationToken ct) =>
        {
            var session = await probeSessionService.StartSessionAsync(request, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        app.MapGet("/api/probe/sessions", async (Guid? deviceId, int? limit, ProbeSessionService probeSessionService, CancellationToken ct) =>
            Results.Ok(await probeSessionService.GetSessionsAsync(deviceId, limit ?? 50, ct)));

        app.MapGet("/api/probe/sessions/{id:guid}/stages", async (Guid id, ProbeSessionService probeSessionService, CancellationToken ct) =>
            Results.Ok(await probeSessionService.GetStagesAsync(id, ct)));

        app.MapGet("/api/truth/sweep", async (string? ips, ProbeSessionService probeSessionService, CancellationToken ct) =>
        {
            var targetIps = string.IsNullOrWhiteSpace(ips)
                ? null
                : ips.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Ok(await probeSessionService.BuildTruthSweepReportAsync(targetIps, ct));
        });

        return app;
    }
}
