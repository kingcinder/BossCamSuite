using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class ProtocolValidationService(
    IEnumerable<IControlAdapter> controlAdapters,
    IEndpointContractCatalog contractCatalog,
    IApplicationStore store,
    ILogger<ProtocolValidationService> logger)
{
    public async Task<CapabilityProbeResult?> ValidateDeviceAsync(Guid deviceId, ValidationRunOptions? options, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        return await ValidateDeviceAsync(device, options ?? new ValidationRunOptions(), cancellationToken);
    }

    public async Task<CapabilityProbeResult> ValidateDeviceAsync(DeviceIdentity device, ValidationRunOptions options, CancellationToken cancellationToken)
    {
        var manifests = await store.GetProtocolManifestsAsync(cancellationToken);
        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var allResults = new List<EndpointValidationResult>();
        var allTranscripts = new List<EndpointTranscript>();

        foreach (var adapter in controlAdapters.OrderBy(static adapter => adapter.Priority))
        {
            if (!string.IsNullOrWhiteSpace(options.AdapterName) && !adapter.Name.Equals(options.AdapterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (adapter.TransportKind is not (TransportKind.LanRest or TransportKind.LanPrivateHttp))
            {
                continue;
            }

            bool canHandle;
            try
            {
                canHandle = await adapter.CanHandleAsync(device, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Validation skip: can-handle failed for adapter {Adapter} device {Device}", adapter.Name, device.DisplayName);
                continue;
            }

            if (!canHandle)
            {
                continue;
            }

            var endpoints = SelectEndpointsForAdapter(adapter, manifests, contracts);
            foreach (var endpoint in endpoints)
            {
                var result = await ValidateEndpointAsync(device, adapter, endpoint, options, cancellationToken);
                allResults.Add(result.Result);
                allTranscripts.AddRange(result.Transcripts);
            }
        }

        await store.SaveEndpointValidationResultsAsync(allResults, cancellationToken);
        if (options.CaptureTranscripts)
        {
            await store.SaveEndpointTranscriptsAsync(allTranscripts, cancellationToken);
        }

        return new CapabilityProbeResult
        {
            DeviceId = device.Id,
            AdapterName = options.AdapterName ?? "all-lan-http-adapters",
            FirmwareFingerprint = BuildFirmwareFingerprint(device),
            Endpoints = allResults,
            Transcripts = allTranscripts
        };
    }

    public Task<IReadOnlyCollection<EndpointValidationResult>> GetValidationResultsAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetEndpointValidationResultsAsync(deviceId, cancellationToken);

    public Task<IReadOnlyCollection<EndpointTranscript>> GetTranscriptsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
        => store.GetEndpointTranscriptsAsync(deviceId, limit, cancellationToken);

    public async Task<bool> IsEndpointWriteVerifiedAsync(Guid deviceId, string adapterName, string endpoint, CancellationToken cancellationToken)
    {
        var results = await store.GetEndpointValidationResultsAsync(deviceId, cancellationToken);
        return results.Any(result =>
            result.AdapterName.Equals(adapterName, StringComparison.OrdinalIgnoreCase) &&
            result.Endpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase) &&
            result.WriteVerified);
    }

    private static IReadOnlyCollection<ProtocolEndpoint> SelectEndpointsForAdapter(IControlAdapter adapter, IReadOnlyCollection<ProtocolManifest> manifests, IReadOnlyCollection<EndpointContract> contracts)
    {
        var contracted = contracts
            .Where(contract => adapter.TransportKind switch
            {
                TransportKind.LanRest => contract.Surface == ContractSurface.NetSdkRest,
                TransportKind.LanPrivateHttp => contract.Surface == ContractSurface.PrivateCgiXml,
                _ => false
            })
            .Select(contract => new ProtocolEndpoint
            {
                Path = contract.Endpoint,
                Tag = contract.GroupName,
                Methods = [contract.Method],
                Safety = contract.DisruptionClass.ToString()
            })
            .ToList();
        if (contracted.Count > 0)
        {
            return contracted;
        }

        if (adapter.TransportKind == TransportKind.LanRest)
        {
            return manifests
                .SelectMany(static manifest => manifest.Endpoints)
                .Where(static endpoint => endpoint.Path.StartsWith("/NetSDK/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return manifests
            .Where(static manifest => manifest.ManifestId.Contains("ipcamsuite_private", StringComparison.OrdinalIgnoreCase) || manifest.Name.Contains("ipcamsuite", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static manifest => manifest.Endpoints)
            .ToList();
    }

    private async Task<(EndpointValidationResult Result, IReadOnlyCollection<EndpointTranscript> Transcripts)> ValidateEndpointAsync(
        DeviceIdentity device,
        IControlAdapter adapter,
        ProtocolEndpoint endpoint,
        ValidationRunOptions options,
        CancellationToken cancellationToken)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpoint.Path);
        var methods = endpoint.Methods.Count == 0 ? ["GET"] : endpoint.Methods.Select(static method => method.ToUpperInvariant()).ToList();
        var readMethod = methods.Contains("GET", StringComparer.OrdinalIgnoreCase) ? "GET" : methods[0];
        var writeMethod = methods.FirstOrDefault(static method => method is "PUT" or "POST" or "DELETE");
        var disruption = ParseDisruption(endpoint.Safety, endpoint.Tag, normalizedEndpoint);
        var transcripts = new List<EndpointTranscript>();

        var readResult = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            Endpoint = normalizedEndpoint,
            GroupName = endpoint.Tag,
            Method = readMethod,
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, readMethod, null, readResult, BuildFirmwareFingerprint(device), "pre-read"));

        var readVerified = readResult.Success;
        var writeVerified = false;
        var rollbackSupported = false;
        var persistsAfterReboot = false;
        var status = readVerified ? "confirmed-read" : "read-failed";
        string? notes = null;
        var preReadNode = readResult.Response?.DeepClone();
        JsonNode? postReadNode = null;

        if (writeMethod is not null)
        {
            if (!options.AttemptWrites)
            {
                status = "read-only-run";
                notes = "Write attempt disabled for this stage.";
            }
            else if (!options.IncludeUnsafeWrites && disruption is not (DisruptionClass.Safe or DisruptionClass.Transient))
            {
                status = "unsafe-skipped";
                notes = $"Write skipped due to disruption class {disruption}.";
            }
            else if (options.AllowedDisruptionClasses.Count > 0 && !options.AllowedDisruptionClasses.Contains(disruption))
            {
                status = "stage-skip";
                notes = $"Write skipped for stage; disruption class {disruption} not in allowed set.";
            }
            else
            {
                var writePayload = BuildWritePayload(preReadNode);
                var writeResult = await adapter.ApplyAsync(device, new WritePlan
                {
                    AdapterName = adapter.Name,
                    Endpoint = normalizedEndpoint,
                    GroupName = endpoint.Tag,
                    Method = writeMethod,
                    Payload = writePayload,
                    SnapshotBeforeWrite = false,
                    RequireWriteVerification = false
                }, cancellationToken);

                transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, writeMethod, writePayload?.ToJsonString(), writeResult, BuildFirmwareFingerprint(device), "write-attempt", beforeValue: preReadNode));

                if (readVerified)
                {
                    var postReadResult = await adapter.ApplyAsync(device, new WritePlan
                    {
                        AdapterName = adapter.Name,
                        Endpoint = normalizedEndpoint,
                        GroupName = endpoint.Tag,
                        Method = "GET",
                        SnapshotBeforeWrite = false,
                        RequireWriteVerification = false
                    }, cancellationToken);

                    postReadNode = postReadResult.Response?.DeepClone();
                    transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, "GET", null, postReadResult, BuildFirmwareFingerprint(device), "post-read", beforeValue: preReadNode, afterValue: postReadNode));
                    writeVerified = writeResult.Success && postReadResult.Success;
                }
                else
                {
                    writeVerified = writeResult.Success;
                }

                status = writeVerified ? "confirmed-write" : "write-failed";

                if (options.IncludeRollbackChecks && writeVerified && preReadNode is JsonObject preObject && postReadNode is not null && !JsonNode.DeepEquals(preReadNode, postReadNode) && writeMethod is not "DELETE")
                {
                    var rollbackResult = await adapter.ApplyAsync(device, new WritePlan
                    {
                        AdapterName = adapter.Name,
                        Endpoint = normalizedEndpoint,
                        GroupName = endpoint.Tag,
                        Method = writeMethod,
                        Payload = preObject,
                        SnapshotBeforeWrite = false,
                        RequireWriteVerification = false
                    }, cancellationToken);

                    rollbackSupported = rollbackResult.Success;
                    transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, writeMethod, preObject.ToJsonString(), rollbackResult, BuildFirmwareFingerprint(device), "rollback-attempt", beforeValue: postReadNode, afterValue: preReadNode));
                }
            }
        }

        if (options.IncludePersistenceChecks && writeVerified)
        {
            notes = AppendNote(notes, "Persistence check requested but not executed automatically in this pass.");
            persistsAfterReboot = false;
        }

        return (new EndpointValidationResult
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Endpoint = normalizedEndpoint,
            Method = writeMethod ?? readMethod,
            AuthMode = "basic-or-digest",
            FirmwareFingerprint = BuildFirmwareFingerprint(device),
            ReadVerified = readVerified,
            WriteVerified = writeVerified,
            PersistsAfterReboot = persistsAfterReboot,
            RollbackSupported = rollbackSupported,
            DisruptionClass = disruption,
            RequestTemplateHash = BuildRequestTemplateHash(BuildWritePayload(preReadNode)),
            Status = status,
            Notes = notes
        }, transcripts);
    }

    private static EndpointTranscript ToTranscript(
        DeviceIdentity device,
        string adapterName,
        string endpoint,
        string method,
        string? requestBody,
        WriteResult result,
        string? firmwareFingerprint,
        string notes,
        JsonNode? beforeValue = null,
        JsonNode? afterValue = null)
        => new()
        {
            DeviceId = device.Id,
            AdapterName = adapterName,
            Endpoint = endpoint,
            Method = method,
            RequestBody = requestBody,
            ResponseBody = result.Message,
            StatusCode = result.StatusCode,
            ParsedResponse = result.Response?.DeepClone(),
            FirmwareFingerprint = firmwareFingerprint,
            Success = result.Success,
            BeforeValue = beforeValue?.DeepClone(),
            AfterValue = afterValue?.DeepClone(),
            Notes = notes
        };

    private static JsonObject? BuildWritePayload(JsonNode? preReadNode)
    {
        if (preReadNode is JsonObject preObject)
        {
            return preObject;
        }

        return new JsonObject();
    }

    private static string NormalizeEndpoint(string endpoint)
        => endpoint
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[/properties]", string.Empty, StringComparison.Ordinal)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

    private static DisruptionClass ParseDisruption(string? safety, string? tag, string endpoint)
    {
        var text = $"{safety} {tag} {endpoint}".ToLowerInvariant();
        if (text.Contains("firmware"))
        {
            return DisruptionClass.FirmwareUpgrade;
        }

        if (text.Contains("factory"))
        {
            return DisruptionClass.FactoryReset;
        }

        if (text.Contains("reboot"))
        {
            return DisruptionClass.Reboot;
        }

        if (text.Contains("network"))
        {
            return DisruptionClass.NetworkChanging;
        }

        if (text.Contains("service-impacting"))
        {
            return DisruptionClass.ServiceImpacting;
        }

        if (text.Contains("safe-write"))
        {
            return DisruptionClass.Safe;
        }

        if (text.Contains("read-only"))
        {
            return DisruptionClass.Unknown;
        }

        return DisruptionClass.Unknown;
    }

    private static string BuildFirmwareFingerprint(DeviceIdentity device)
        => $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

    private static string? BuildRequestTemplateHash(JsonObject? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return Convert.ToHexString(bytes);
    }

    private static string AppendNote(string? existing, string note)
        => string.IsNullOrWhiteSpace(existing) ? note : $"{existing} {note}";
}
