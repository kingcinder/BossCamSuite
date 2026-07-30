using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure;
using BossCam.Service.Hosted;
using BossCam.Service.Security;
using BossCam.Service;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
if (OperatingSystem.IsLinux())
{
    builder.Configuration.AddJsonFile("appsettings.Linux.json", optional: true, reloadOnChange: true);
}

var localApiBaseUrl = builder.Configuration["BossCam:LocalApiBaseUrl"] ?? "http://127.0.0.1:5317";
builder.WebHost.UseUrls(localApiBaseUrl);
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
}
else if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

// SignalR hub for real-time push events to the Svelte SPA.
builder.Services.AddSignalR()
    .AddHubOptions<BossCam.Service.Hubs.BossCamHub>(options =>
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.MaximumReceiveMessageSize = 128 * 1024;
    });
builder.Services.AddSingleton<BossCam.Core.IBossCamEventBroadcaster, BossCam.Service.Hubs.BossCamEventBroadcaster>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
ConfigureCors(builder);
ConfigureRateLimiter(builder);
builder.Services.AddBossCamInfrastructure(builder.Configuration);
builder.Services.AddBossCamCore();
builder.Services.AddHostedService<BossCamBootstrapWorker>();
builder.Services.AddHostedService<RecordingLifecycleWorker>();
builder.Services.AddHostedService<ConnectivityWatchdogWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ----- Host-aware LAN gate wiring (post-build, post-config-merge) ----------
var finalLocalApiBaseUrl = app.Configuration["BossCam:LocalApiBaseUrl"] ?? localApiBaseUrl;
var lanBound = BindAddressInspector.IsAnyNonLoopback(finalLocalApiBaseUrl);
var lanEnvToken = Environment.GetEnvironmentVariable("BOSSCAM_LAN_TOKEN");
var lanCfgToken = app.Configuration["BossCam:LanAuthToken"];
var lanResolvedToken = !string.IsNullOrEmpty(lanEnvToken)
    ? lanEnvToken
    : (!string.IsNullOrEmpty(lanCfgToken) ? lanCfgToken : null);

if (lanBound && lanResolvedToken is null)
{
    throw new InvalidOperationException(
        "BossCamService refuses to start: bound to a non-loopback interface " +
        "(BossCam:LocalApiBaseUrl='" + finalLocalApiBaseUrl + "') but no LAN bearer " +
        "token is configured.\n" +
        "Generate one with:   openssl rand -hex 32\n" +
        "Then export it:      BOSSCAM_LAN_TOKEN='<token>'\n" +
        "Or rebind to loopback: BossCam:LocalApiBaseUrl='http://127.0.0.1:5317'.\n" +
        "Without this protection the LAN could read /api/devices, /api/recordings, " +
        "/api/devices/{id}/settings/write, and the Swagger UI/anonymously.");
}

if (lanResolvedToken is not null)
{
    if (!lanBound && !string.IsNullOrEmpty(lanEnvToken))
    {
        app.Logger.LogWarning(
            "BOSSCAM_LAN_TOKEN is set but the service is bound to a loopback address ('{Bind}'). " +
            "The token is loaded but is only required when an interface-facing address is bound. " +
            "To enforce auth, change BossCam:LocalApiBaseUrl to a non-loopback host (or set BOSSCAM_BIND).",
            finalLocalApiBaseUrl);
    }
    app.UseLanBoundTokenGate(lanResolvedToken);
}

app.UseCors(lanBound && lanResolvedToken is not null ? "RestrictedTokenMode" : "PermissiveLoopback");
app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

// ---- Route endpoints (split by domain into separate files) ----
app.MapDevicesEndpoints()
   .MapDevicesStreamingEndpoints()
   .MapDevicesInsightsEndpoints()
   .MapRecordingsEndpoints()
   .MapStorageEndpoints()
   .MapDiagnosticsEndpoints()
   .MapFirmwareContractsProtocolsEndpoints()
   .MapPlaybackEndpoints()
   .MapConnectivityEndpoints();

// SPA fallback for operator console.
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/hub", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":"Not found"}""");
        return;
    }

    var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var index = Path.Combine(webRoot, "index.html");
    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Operator UI not found.");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

// SignalR hub for the Svelte SPA real-time events.
app.MapHub<BossCam.Service.Hubs.BossCamHub>("/hub/bosscam");

app.Run();

// ---- Static configuration helpers ----

static void ConfigureCors(WebApplicationBuilder webBuilder)
{
    var allowedOrigins = webBuilder.Configuration.GetSection("BossCam:AllowedOrigins").Get<string[]>() ?? [];
    webBuilder.Services.AddCors(options =>
    {
        options.AddPolicy("PermissiveLoopback", policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        options.AddPolicy("RestrictedTokenMode", policy =>
        {
            if (allowedOrigins.Length == 0)
            {
                policy.SetIsOriginAllowed(_ => false).AllowAnyHeader().AllowAnyMethod();
            }
            else
            {
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
            }
        });
    });
}

static void ConfigureRateLimiter(WebApplicationBuilder webBuilder)
{
    var rateLimitEnabled = webBuilder.Configuration.GetValue("BossCam:RateLimitEnabled", true);
    if (!rateLimitEnabled)
    {
        return;
    }

    webBuilder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("probe", httpContext =>
        {
            var key = httpContext.Request.RouteValues["id"]?.ToString()
                      ?? httpContext.Connection.RemoteIpAddress?.ToString()
                      ?? "anon";
            var permit = webBuilder.Configuration.GetValue("BossCam:RateLimitProbePerMinute", 6);
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });

        options.AddPolicy("recordings-start", httpContext =>
        {
            var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
            var permit = webBuilder.Configuration.GetValue("BossCam:RateLimitRecordingStartPerMinute", 10);
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });

        options.AddPolicy("snapshot", httpContext =>
        {
            var key = httpContext.Request.RouteValues["id"]?.ToString()
                      ?? httpContext.Connection.RemoteIpAddress?.ToString()
                      ?? "anon";
            var permit = webBuilder.Configuration.GetValue("BossCam:RateLimitSnapshotPerMinute", 30);
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });

        options.AddPolicy("firmware-register", httpContext =>
        {
            var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
    });
}

// ---- Shared request/response record types ----
public sealed record AegonLanRegisterRequest(string? LorexPassword, string? WvcPassword);
public sealed record FirmwareRegisterRequest(string FilePath);
public sealed record TypedSettingApplyRequest(string FieldKey, JsonNode? Value, bool ExpertOverride);
public sealed record TypedSettingBatchApplyRequest(IReadOnlyCollection<TypedFieldChange> Changes, bool ExpertOverride);
public sealed record PersistenceFieldVerifyRequest(string FieldKey, JsonNode? Value, bool RebootForVerification, bool ExpertOverride);
public sealed record ContractFixturePromotionRequest(string ExportRoot);
public sealed record ContractFixtureCleanupRequest(int OlderThanDays = 90, int MaxPerDevice = 2000, int MaxTotal = 10000);
public sealed record ImageTruthSweepRequest(bool IncludeBehaviorMapping, bool RefreshFromDevice, string? ExportRoot);

// Expose entry point for WebApplicationFactory / E2E host.
public partial class Program;
