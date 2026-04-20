using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class PersistenceVerificationService(
    IEnumerable<IControlAdapter> adapters,
    IApplicationStore store,
    ILogger<PersistenceVerificationService> logger)
{
    public async Task<PersistenceVerificationResult?> VerifyAsync(PersistenceVerificationRequest request, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var adapter = adapters.FirstOrDefault(candidate => candidate.Name.Equals(request.AdapterName, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || !await adapter.CanHandleAsync(device, cancellationToken))
        {
            return new PersistenceVerificationResult
            {
                DeviceId = request.DeviceId,
                AdapterName = request.AdapterName,
                Endpoint = request.Endpoint,
                Notes = "Adapter unavailable."
            };
        }

        var pre = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            GroupName = "PersistenceVerification",
            Endpoint = request.Endpoint,
            Method = "GET",
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        var write = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            GroupName = "PersistenceVerification",
            Endpoint = request.Endpoint,
            Method = request.Method,
            Payload = request.Payload,
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        var post = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            GroupName = "PersistenceVerification",
            Endpoint = request.Endpoint,
            Method = "GET",
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        var immediate = write.Success && post.Success;
        var rebootPassed = false;
        JsonNode? postRebootValue = null;
        JsonNode? baselineField = null;
        JsonNode? immediateField = null;
        JsonNode? rebootField = null;
        if (!string.IsNullOrWhiteSpace(request.FieldSourcePath))
        {
            baselineField = TryGetPathValue(pre.Response, request.FieldSourcePath);
            immediateField = TryGetPathValue(post.Response, request.FieldSourcePath);
        }
        if (request.RebootForVerification)
        {
            var reboot = await adapter.ExecuteMaintenanceAsync(device, MaintenanceOperation.Reboot, null, cancellationToken);
            if (reboot.Success)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, request.RebootWaitSeconds)), cancellationToken);
                var reread = await adapter.ApplyAsync(device, new WritePlan
                {
                    AdapterName = adapter.Name,
                    GroupName = "PersistenceVerification",
                    Endpoint = request.Endpoint,
                    Method = "GET",
                    SnapshotBeforeWrite = false,
                    RequireWriteVerification = false
                }, cancellationToken);
                rebootPassed = reread.Success;
                postRebootValue = reread.Response?.DeepClone();
                if (!string.IsNullOrWhiteSpace(request.FieldSourcePath))
                {
                    rebootField = TryGetPathValue(postRebootValue, request.FieldSourcePath);
                }
            }
            else
            {
                logger.LogWarning("Persistence verification reboot failed for {Device}", device.DisplayName);
            }
        }

        var immediateStatus = ClassifyImmediate(write, request.IntendedValue, baselineField, immediateField);
        var persistenceStatus = ClassifyPersistence(request.RebootForVerification, immediateStatus, request.IntendedValue, rebootField);

        var result = new PersistenceVerificationResult
        {
            DeviceId = request.DeviceId,
            AdapterName = adapter.Name,
            Endpoint = request.Endpoint,
            ImmediateVerifyPassed = immediate,
            RebootRequested = request.RebootForVerification,
            RebootVerifyPassed = rebootPassed,
            PreValue = pre.Response?.DeepClone(),
            PostValue = post.Response?.DeepClone(),
            PostRebootValue = postRebootValue,
            ImmediateStatus = immediateStatus,
            PersistenceStatus = persistenceStatus
        };

        await store.SavePersistenceVerificationResultAsync(result, cancellationToken);
        if (rebootPassed)
        {
            var existing = await store.GetEndpointValidationResultsAsync(request.DeviceId, cancellationToken);
            var updates = existing
                .Where(validation => validation.Endpoint.Equals(request.Endpoint, StringComparison.OrdinalIgnoreCase) && validation.AdapterName.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase))
                .Select(validation => validation with { PersistsAfterReboot = true, Status = "confirmed-persistence" })
                .ToList();
            if (updates.Count > 0)
            {
                await store.SaveEndpointValidationResultsAsync(updates, cancellationToken);
            }
        }

        return result;
    }

    public Task<IReadOnlyCollection<PersistenceVerificationResult>> GetResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken)
        => store.GetPersistenceVerificationResultsAsync(deviceId, limit, cancellationToken);

    private static SemanticWriteStatus ClassifyImmediate(WriteResult write, JsonNode? intended, JsonNode? baseline, JsonNode? immediate)
    {
        if (!write.Success)
        {
            return write.StatusCode is >= 400 ? SemanticWriteStatus.Rejected : SemanticWriteStatus.TransportFailed;
        }

        if (immediate is null)
        {
            return SemanticWriteStatus.ShapeMismatch;
        }

        if (baseline is not null && JsonNode.DeepEquals(baseline, immediate))
        {
            return SemanticWriteStatus.AcceptedNoChange;
        }

        if (intended is not null && JsonNode.DeepEquals(intended, immediate))
        {
            return SemanticWriteStatus.AcceptedChanged;
        }

        if (TryToDecimal(intended) is decimal intendedNumber && TryToDecimal(immediate) is decimal actualNumber)
        {
            return actualNumber != intendedNumber ? SemanticWriteStatus.AcceptedClamped : SemanticWriteStatus.AcceptedChanged;
        }

        return intended is null ? SemanticWriteStatus.AcceptedChanged : SemanticWriteStatus.AcceptedTranslated;
    }

    private static SemanticWriteStatus ClassifyPersistence(bool rebootRequested, SemanticWriteStatus immediateStatus, JsonNode? intended, JsonNode? postReboot)
    {
        if (!rebootRequested)
        {
            return immediateStatus switch
            {
                SemanticWriteStatus.AcceptedChanged => SemanticWriteStatus.PersistedAfterDelay,
                SemanticWriteStatus.AcceptedClamped => SemanticWriteStatus.PersistedAfterDelay,
                SemanticWriteStatus.AcceptedTranslated => SemanticWriteStatus.PersistedAfterDelay,
                _ => SemanticWriteStatus.Unverified
            };
        }

        if (postReboot is null)
        {
            return SemanticWriteStatus.Uncertain;
        }

        if (intended is not null && JsonNode.DeepEquals(intended, postReboot))
        {
            return SemanticWriteStatus.PersistedAfterReboot;
        }

        if (TryToDecimal(intended) is decimal intendedNumber && TryToDecimal(postReboot) is decimal rebootNumber)
        {
            return rebootNumber == intendedNumber ? SemanticWriteStatus.PersistedAfterReboot : SemanticWriteStatus.LostAfterReboot;
        }

        return SemanticWriteStatus.LostAfterReboot;
    }

    private static JsonNode? TryGetPathValue(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var cleaned = path.Trim().TrimStart('$').TrimStart('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return root;
        }

        JsonNode? current = root;
        foreach (var part in cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static decimal? TryToDecimal(JsonNode? value)
    {
        if (value is JsonValue json)
        {
            if (json.TryGetValue<decimal>(out var decimalValue))
            {
                return decimalValue;
            }

            if (json.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (json.TryGetValue<double>(out var doubleValue))
            {
                return Convert.ToDecimal(doubleValue);
            }
        }

        if (value is not null && decimal.TryParse(value.ToJsonString().Trim('"'), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}


