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
            }
            else
            {
                logger.LogWarning("Persistence verification reboot failed for {Device}", device.DisplayName);
            }
        }

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
            ImmediateStatus = immediate ? SemanticWriteStatus.AcceptedChanged : SemanticWriteStatus.Rejected,
            PersistenceStatus = request.RebootForVerification
                ? (rebootPassed ? SemanticWriteStatus.PersistedAfterReboot : SemanticWriteStatus.LostAfterReboot)
                : SemanticWriteStatus.Unverified
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
}


