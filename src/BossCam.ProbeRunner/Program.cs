using System.Text.Json;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("src/BossCam.Service/appsettings.json", optional: true);
builder.Services.AddBossCamInfrastructure(builder.Configuration);
builder.Services.AddBossCamCore();

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var services = scope.ServiceProvider;
var store = services.GetRequiredService<IApplicationStore>();
var protocolCatalog = services.GetRequiredService<ProtocolCatalogService>();
var discovery = services.GetRequiredService<DiscoveryCoordinator>();
var probeSessions = services.GetRequiredService<ProbeSessionService>();
var cli = ProbeCliOptions.Parse(args);

await store.InitializeAsync(CancellationToken.None);
await protocolCatalog.RefreshAsync(CancellationToken.None);
if (cli.DiscoverFirst)
{
    await discovery.RunAsync(CancellationToken.None);
}

var loopTargets = cli.DeviceId is not null
    ? [cli.DeviceIps.FirstOrDefault() ?? "by-id"]
    : (cli.DeviceIps.Count > 0 ? cli.DeviceIps : ["10.0.0.4", "10.0.0.29", "10.0.0.227"]);
var created = new List<ProbeSession>();
foreach (var ip in loopTargets)
{
    var request = new ProbeSessionRequest
    {
        DeviceId = cli.DeviceId,
        DeviceIp = cli.DeviceId is null ? ip : null,
        ProfileName = cli.ProfileName,
        Mode = cli.Mode,
        DiscoverIfMissing = cli.DiscoverFirst,
        ResumeIfExists = cli.Resume,
        IncludePersistenceChecks = cli.IncludePersistenceChecks,
        IncludeRollbackChecks = true,
        RequestRebootVerification = cli.RebootVerification,
        TranscriptExportDirectory = cli.ExportDirectory
    };

    Console.WriteLine($"Running probe session: mode={request.Mode} ip={request.DeviceIp ?? "by-id"}");
    var session = await probeSessions.StartSessionAsync(request, CancellationToken.None);
    if (session is not null)
    {
        created.Add(session);
        Console.WriteLine($"  session={session.Id} status={session.Status} firmware={session.FirmwareFingerprint}");
    }
    else
    {
        Console.WriteLine("  no matching device found.");
    }
}

if (!string.IsNullOrWhiteSpace(cli.ExportSummaryPath))
{
    var json = JsonSerializer.Serialize(created, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cli.ExportSummaryPath))!);
    await File.WriteAllTextAsync(cli.ExportSummaryPath, json);
    Console.WriteLine($"Exported session summary: {Path.GetFullPath(cli.ExportSummaryPath)}");
}

if (cli.ExportTruthSweep)
{
    var reportTargets = cli.DeviceId is not null
        ? (created.Where(session => !string.IsNullOrWhiteSpace(session.DeviceIp)).Select(session => session.DeviceIp!).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        : loopTargets;
    var report = await probeSessions.BuildTruthSweepReportAsync(reportTargets, CancellationToken.None);
    var reportPath = string.IsNullOrWhiteSpace(cli.TruthSweepPath)
        ? Path.Combine(cli.ExportDirectory ?? "artifacts", "truth-sweep-report.json")
        : cli.TruthSweepPath;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    Console.WriteLine($"Exported truth sweep report: {Path.GetFullPath(reportPath)}");
}

Console.WriteLine("Probe runner complete.");

internal sealed record ProbeCliOptions
{
    public Guid? DeviceId { get; init; }
    public string? ProfileName { get; init; }
    public ProbeStageMode Mode { get; init; } = ProbeStageMode.SafeReadOnly;
    public bool DiscoverFirst { get; init; }
    public bool Resume { get; init; }
    public bool IncludePersistenceChecks { get; init; }
    public bool RebootVerification { get; init; }
    public List<string> DeviceIps { get; init; } = [];
    public string? ExportDirectory { get; init; }
    public string? ExportSummaryPath { get; init; }
    public bool ExportTruthSweep { get; init; }
    public string? TruthSweepPath { get; init; }

    public static ProbeCliOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg;
            var value = (index + 1) < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[++index] : "true";
            map[key] = value;
        }

        Guid? deviceId = null;
        if (map.TryGetValue("--device-id", out var idRaw) && Guid.TryParse(idRaw, out var parsed))
        {
            deviceId = parsed;
        }

        var mode = ProbeStageMode.SafeReadOnly;
        if (map.TryGetValue("--mode", out var modeRaw) && Enum.TryParse<ProbeStageMode>(modeRaw, true, out var parsedMode))
        {
            mode = parsedMode;
        }

        var ips = new List<string>();
        if (map.TryGetValue("--device-ip", out var singleIp) && !string.IsNullOrWhiteSpace(singleIp))
        {
            ips.Add(singleIp);
        }
        if (map.TryGetValue("--device-ips", out var multipleIps) && !string.IsNullOrWhiteSpace(multipleIps))
        {
            ips.AddRange(multipleIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new ProbeCliOptions
        {
            DeviceId = deviceId,
            ProfileName = map.GetValueOrDefault("--profile"),
            Mode = mode,
            DiscoverFirst = bool.TryParse(map.GetValueOrDefault("--discover"), out var discover) && discover,
            Resume = bool.TryParse(map.GetValueOrDefault("--resume"), out var resume) && resume,
            IncludePersistenceChecks = bool.TryParse(map.GetValueOrDefault("--include-persistence"), out var includePersistence) && includePersistence,
            RebootVerification = bool.TryParse(map.GetValueOrDefault("--reboot-verification"), out var rebootVerification) && rebootVerification,
            DeviceIps = ips,
            ExportDirectory = map.GetValueOrDefault("--export-dir"),
            ExportSummaryPath = map.GetValueOrDefault("--export-summary"),
            ExportTruthSweep = bool.TryParse(map.GetValueOrDefault("--export-truth-sweep"), out var exportTruth) && exportTruth,
            TruthSweepPath = map.GetValueOrDefault("--truth-sweep-path")
        };
    }
}
