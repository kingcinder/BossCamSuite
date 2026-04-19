using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class ContractEvidenceService(
    IApplicationStore store,
    IEndpointContractCatalog catalog,
    ILogger<ContractEvidenceService> logger) : IContractEvidenceService
{
    public async Task<IReadOnlyCollection<EndpointContractFixture>> PromoteFromTranscriptsAsync(Guid deviceId, string exportRoot, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var contracts = await catalog.GetContractsForDeviceAsync(device, cancellationToken);
        var transcripts = await store.GetEndpointTranscriptsAsync(deviceId, 5000, cancellationToken);

        var grouped = transcripts
            .GroupBy(transcript => $"{NormalizeEndpoint(transcript.Endpoint)}::{transcript.Method.ToUpperInvariant()}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(static item => item.Timestamp).First())
            .ToList();

        var fixtures = new List<EndpointContractFixture>();
        foreach (var transcript in grouped)
        {
            var contract = catalog.MatchContract(transcript.Endpoint, transcript.Method, contracts)
                ?? catalog.MatchContract(transcript.Endpoint, transcript.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "PUT" : transcript.Method, contracts);
            if (contract is null)
            {
                continue;
            }

            var firmwareFolder = (transcript.FirmwareFingerprint ?? "unknown").Replace('|', '_');
            var folder = Path.Combine(exportRoot, "contracts", contract.GroupKind.ToString(), firmwareFolder);
            Directory.CreateDirectory(folder);
            var fileName = SanitizeFileName($"{NormalizeEndpoint(transcript.Endpoint)}_{transcript.Method}.json");
            var filePath = Path.Combine(folder, fileName);

            var payload = new JsonObject
            {
                ["contractKey"] = contract.ContractKey,
                ["endpoint"] = transcript.Endpoint,
                ["method"] = transcript.Method,
                ["firmwareFingerprint"] = transcript.FirmwareFingerprint,
                ["authMode"] = transcript.AuthMode,
                ["requestBody"] = ParseIfJson(transcript.RequestBody),
                ["responseBody"] = transcript.ParsedResponse?.DeepClone() ?? ParseIfJson(transcript.ResponseBody),
                ["timestamp"] = transcript.Timestamp.ToString("O"),
                ["success"] = transcript.Success
            };

            await File.WriteAllTextAsync(filePath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var firmwareFixtureFolder = Path.Combine(exportRoot, "fixtures", "5523w", firmwareFolder);
            Directory.CreateDirectory(firmwareFixtureFolder);
            var firmwareFixturePath = Path.Combine(firmwareFixtureFolder, fileName);
            await File.WriteAllTextAsync(firmwareFixturePath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            fixtures.Add(new EndpointContractFixture
            {
                DeviceId = deviceId,
                Endpoint = transcript.Endpoint,
                Method = transcript.Method,
                ContractKey = contract.ContractKey,
                FirmwareFingerprint = transcript.FirmwareFingerprint,
                AuthMode = transcript.AuthMode,
                FixturePath = firmwareFixturePath,
                RequestBody = ParseIfJson(transcript.RequestBody),
                ResponseBody = transcript.ParsedResponse?.DeepClone() ?? ParseIfJson(transcript.ResponseBody),
                TruthState = transcript.Success ? ContractTruthState.Proven : ContractTruthState.Unverified,
                CapturedAt = transcript.Timestamp
            });
        }

        if (fixtures.Count > 0)
        {
            await store.SaveContractFixturesAsync(fixtures, cancellationToken);
            logger.LogInformation("Promoted {Count} transcript fixtures for {DeviceId}", fixtures.Count, deviceId);
        }

        return fixtures;
    }

    public Task<IReadOnlyCollection<EndpointContractFixture>> GetFixturesAsync(Guid? deviceId, CancellationToken cancellationToken)
        => store.GetContractFixturesAsync(deviceId, 5000, cancellationToken);

    private static JsonNode? ParseIfJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch
        {
            return JsonValue.Create(raw);
        }
    }

    private static string NormalizeEndpoint(string endpoint)
        => endpoint.Trim('/').Replace('/', '_').Replace('?', '_').Replace('&', '_').Replace('=', '_');

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in invalid)
        {
            fileName = fileName.Replace(ch, '_');
        }

        return fileName;
    }
}
