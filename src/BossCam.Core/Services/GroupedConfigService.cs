using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class GroupedConfigService(
    IApplicationStore store,
    SettingsService settingsService,
    TypedSettingsService typedSettingsService,
    IEndpointContractCatalog contractCatalog,
    ILogger<GroupedConfigService> logger)
{
    private static readonly string[] ImageStepFields = ["brightness", "contrast", "saturation", "sharpness", "wdr"];
    private static readonly IReadOnlyCollection<SdkFieldDefinition> SdkFieldCatalog = BuildSdkFieldCatalog();

    public async Task<IReadOnlyCollection<GroupedConfigSnapshot>> GetGroupedConfigSnapshotsAsync(Guid deviceId, bool refreshFromDevice, CancellationToken cancellationToken)
    {
        if (refreshFromDevice)
        {
            _ = await settingsService.ReadAsync(deviceId, cancellationToken);
            _ = await typedSettingsService.NormalizeDeviceAsync(deviceId, refreshFromDevice: true, cancellationToken);
        }

        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        var snapshot = await store.GetSettingsSnapshotAsync(deviceId, cancellationToken);
        if (device is null || snapshot is null)
        {
            return [];
        }

        var firmware = BuildFirmwareFingerprint(device, await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken));
        var endpointValues = snapshot.Groups
            .SelectMany(static g => g.Values.Values)
            .Where(static v => v.Value is JsonObject)
            .ToList();

        var grouped = new List<GroupedConfigSnapshot>();
        foreach (var groupKind in Enum.GetValues<GroupedConfigKind>())
        {
            var match = endpointValues.FirstOrDefault(value => MatchesGroup(groupKind, NormalizeEndpoint(value.SourceEndpoint ?? value.Key)));
            if (match?.Value is not JsonObject obj)
            {
                continue;
            }

            grouped.Add(new GroupedConfigSnapshot
            {
                DeviceId = deviceId,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = groupKind,
                Endpoint = NormalizeEndpoint(match.SourceEndpoint ?? match.Key),
                Method = "PUT",
                Payload = (JsonObject)obj.DeepClone()
            });
        }

        return grouped;
    }

    public IReadOnlyCollection<SdkFieldDefinition> GetSdkFieldCatalog() => SdkFieldCatalog;

    public async Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> ForceEnumerateSdkFieldsAsync(Guid deviceId, ForcedEnumerationRequest request, CancellationToken cancellationToken)
    {
        if (request.RefreshFromDevice)
        {
            _ = await typedSettingsService.NormalizeDeviceAsync(deviceId, refreshFromDevice: true, cancellationToken);
            _ = await settingsService.ReadAsync(deviceId, cancellationToken);
        }

        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var normalizedFields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var firmware = BuildFirmwareFingerprint(device, normalizedFields);
        var defs = SdkFieldCatalog
            .Where(def => request.Groups.Count == 0 || request.Groups.Contains(def.GroupKind))
            .Where(def => request.FieldKeys.Count == 0 || request.FieldKeys.Contains(def.FieldKey, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var results = new List<GroupedUnsupportedRetestResult>();
        foreach (var def in defs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!request.IncludeDangerous && def.FieldKey.Equals("sdFormat", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new GroupedUnsupportedRetestResult
                {
                    DeviceId = deviceId,
                    FirmwareFingerprint = firmware,
                    IpAddress = device.IpAddress ?? string.Empty,
                    GroupKind = def.GroupKind,
                    ContractKey = $"sdk.{def.GroupKind}.{def.FieldKey}",
                    FieldKey = def.FieldKey,
                    SourceEndpoint = def.EndpointPattern,
                    SourcePath = def.SourcePath,
                    Behavior = GroupedApplyBehavior.Unknown,
                    Classification = ForcedFieldClassification.Unsupported,
                    DefinitionSource = def.SourceEvidence,
                    Notes = "Dangerous field skipped (set includeDangerous=true to test)."
                });
                continue;
            }

            if (!def.Writable)
            {
                results.Add(new GroupedUnsupportedRetestResult
                {
                    DeviceId = deviceId,
                    FirmwareFingerprint = firmware,
                    IpAddress = device.IpAddress ?? string.Empty,
                    GroupKind = def.GroupKind,
                    ContractKey = $"sdk.{def.GroupKind}.{def.FieldKey}",
                    FieldKey = def.FieldKey,
                    SourceEndpoint = def.EndpointPattern,
                    SourcePath = def.SourcePath,
                    Behavior = GroupedApplyBehavior.Unknown,
                    Classification = ForcedFieldClassification.ReadableOnly,
                    DefinitionSource = def.SourceEvidence,
                    Notes = "SDK marks this field as read-only."
                });
                continue;
            }

            var endpointMatch = await FindEndpointPayloadAsync(deviceId, def.EndpointPattern, cancellationToken);
            if (endpointMatch?.Value is not JsonObject endpointPayload)
            {
                results.Add(new GroupedUnsupportedRetestResult
                {
                    DeviceId = deviceId,
                    FirmwareFingerprint = firmware,
                    IpAddress = device.IpAddress ?? string.Empty,
                    GroupKind = def.GroupKind,
                    ContractKey = $"sdk.{def.GroupKind}.{def.FieldKey}",
                    FieldKey = def.FieldKey,
                    SourceEndpoint = def.EndpointPattern,
                    SourcePath = def.SourcePath,
                    Behavior = GroupedApplyBehavior.Unapplied,
                    Classification = ForcedFieldClassification.Unsupported,
                    DefinitionSource = def.SourceEvidence,
                    Notes = "Endpoint not present in current snapshot for this firmware/device."
                });
                continue;
            }

            var baseline = TryGetPathValue(endpointPayload, def.SourcePath);
            var baselinePresent = baseline is not null;
            var candidate = BuildSdkCandidateValue(def, baseline);
            if (candidate is null)
            {
                results.Add(new GroupedUnsupportedRetestResult
                {
                    DeviceId = deviceId,
                    FirmwareFingerprint = firmware,
                    IpAddress = device.IpAddress ?? string.Empty,
                    GroupKind = def.GroupKind,
                    ContractKey = $"sdk.{def.GroupKind}.{def.FieldKey}",
                    FieldKey = def.FieldKey,
                    SourceEndpoint = endpointMatch.Value.SourceEndpoint,
                    SourcePath = def.SourcePath,
                    BaselineValue = baseline?.DeepClone(),
                    Behavior = GroupedApplyBehavior.Unknown,
                    Classification = baselinePresent ? ForcedFieldClassification.ReadableOnly : ForcedFieldClassification.Unsupported,
                    BaselineFieldPresent = baselinePresent,
                    DefinitionSource = def.SourceEvidence,
                    Notes = "No safe mutation candidate generated for this field shape."
                });
                continue;
            }

            var first = await ApplyGroupedFieldAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, candidate, request.ExpertOverride, cancellationToken);
            var immediate = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var delayed1 = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var delayed3 = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var delayed5 = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);

            var secondary = false;
            var resend = false;
            var behavior = ClassifyBehavior(candidate, immediate, delayed1, delayed3, delayed5, first?.Success == true);
            if (behavior is GroupedApplyBehavior.RequiresCommitTrigger or GroupedApplyBehavior.Unapplied)
            {
                var secondWrite = await ApplyGroupedFieldAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, candidate, request.ExpertOverride, cancellationToken);
                secondary = secondWrite?.Success == true;
                var secondImmediate = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);
                if (JsonNode.DeepEquals(secondImmediate, candidate))
                {
                    behavior = GroupedApplyBehavior.RequiresSecondWrite;
                }
                else
                {
                    var thirdWrite = await ApplyGroupedFieldAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, candidate, request.ExpertOverride, cancellationToken);
                    resend = thirdWrite?.Success == true;
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    var afterResend = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);
                    if (JsonNode.DeepEquals(afterResend, candidate) || (first?.Success == true || secondary || resend))
                    {
                        behavior = GroupedApplyBehavior.RequiresCommitTrigger;
                    }
                }
            }

            var classification = ClassifyForcedField(behavior, baseline, immediate, delayed1, delayed3, delayed5, candidate, baselinePresent, first?.Success == true, secondary, resend);
            results.Add(new GroupedUnsupportedRetestResult
            {
                DeviceId = deviceId,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = def.GroupKind,
                ContractKey = $"sdk.{def.GroupKind}.{def.FieldKey}",
                FieldKey = def.FieldKey,
                SourceEndpoint = endpointMatch.Value.SourceEndpoint,
                SourcePath = def.SourcePath,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ImmediateValue = immediate?.DeepClone(),
                Delayed1sValue = delayed1?.DeepClone(),
                Delayed3sValue = delayed3?.DeepClone(),
                Delayed5sValue = delayed5?.DeepClone(),
                FirstWriteSucceeded = first?.Success == true,
                SecondaryWriteSucceeded = secondary,
                ResendWriteSucceeded = resend,
                Behavior = behavior,
                Classification = classification,
                InjectedMissingField = !baselinePresent,
                BaselineFieldPresent = baselinePresent,
                DefinitionSource = def.SourceEvidence,
                Notes = first?.Message ?? def.Notes
            });

            if (baselinePresent)
            {
                _ = await ApplyGroupedFieldAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, baseline!.DeepClone(), expertOverride: true, cancellationToken);
            }
        }

        await store.SaveGroupedRetestResultsAsync(results, cancellationToken);
        await SaveGroupedProfilesAsync(device, normalizedFields, results, cancellationToken);
        await PromoteRetestedFieldsAsync(deviceId, results, cancellationToken);
        await PromoteImageInventoryAsync(deviceId, results, cancellationToken);
        logger.LogInformation("SDK forced enumeration completed for {Device} fields={Count}", device.DisplayName, results.Count);
        return results;
    }

    public async Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> RetestUnsupportedFieldsAsync(Guid deviceId, GroupedRetestRequest request, CancellationToken cancellationToken)
    {
        _ = await typedSettingsService.NormalizeDeviceAsync(deviceId, request.RefreshFromDevice, cancellationToken);
        var fields = (await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken))
            .GroupBy(static f => f.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.OrderByDescending(static f => f.CapturedAt).First())
            .ToList();
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var targets = fields
            .Where(field => field.SupportState == ContractSupportState.Unsupported
                || field.Validity == FieldValidityState.Unsupported
                || field.Confidence.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
            .Where(field => request.FieldKeys.Count == 0 || request.FieldKeys.Contains(field.FieldKey, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var output = new List<GroupedUnsupportedRetestResult>();
        foreach (var field in targets)
        {
            var contract = contracts.FirstOrDefault(candidate => string.Equals(candidate.ContractKey, field.ContractKey, StringComparison.OrdinalIgnoreCase)
                && EndpointPatternMatches(candidate.Endpoint, field.SourceEndpoint));
            var contractField = contract?.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
            if (contract is null || contractField is null || !contractField.Writable)
            {
                continue;
            }

            if (!request.IncludeDangerous && (contract.DisruptionClass is DisruptionClass.FirmwareUpgrade or DisruptionClass.FactoryReset
                || contractField.DisruptionClass is DisruptionClass.FirmwareUpgrade or DisruptionClass.FactoryReset))
            {
                continue;
            }

            var candidate = BuildCandidateValue(field.FieldKey, field.TypedValue, contractField);
            if (candidate is null)
            {
                continue;
            }

            var baseline = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
            var first = await ApplyGroupedFieldAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, candidate, request.ExpertOverride, cancellationToken);
            var immediate = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var delayed1 = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var delayed3 = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var delayed5 = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);

            var secondary = false;
            var resend = false;
            var behavior = ClassifyBehavior(candidate, immediate, delayed1, delayed3, delayed5, first?.Success == true);
            if (behavior is GroupedApplyBehavior.RequiresCommitTrigger or GroupedApplyBehavior.Unapplied)
            {
                var secondWrite = await ApplyGroupedFieldAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, candidate, request.ExpertOverride, cancellationToken);
                secondary = secondWrite?.Success == true;
                immediate = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
                behavior = JsonNode.DeepEquals(immediate, candidate)
                    ? GroupedApplyBehavior.RequiresSecondWrite
                    : behavior;

                if (behavior is GroupedApplyBehavior.RequiresCommitTrigger or GroupedApplyBehavior.Unapplied)
                {
                    var thirdWrite = await ApplyGroupedFieldAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, candidate, request.ExpertOverride, cancellationToken);
                    resend = thirdWrite?.Success == true;
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    var afterResend = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
                    if (JsonNode.DeepEquals(afterResend, candidate))
                    {
                        behavior = GroupedApplyBehavior.RequiresCommitTrigger;
                    }
                    else if ((first?.Success == true || secondary || resend) && behavior == GroupedApplyBehavior.Unapplied)
                    {
                        behavior = GroupedApplyBehavior.RequiresCommitTrigger;
                    }
                }
            }

            output.Add(new GroupedUnsupportedRetestResult
            {
                DeviceId = deviceId,
                FirmwareFingerprint = BuildFirmwareFingerprint(device, fields),
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = ResolveGroupKind(field.FieldKey),
                ContractKey = contract.ContractKey,
                FieldKey = field.FieldKey,
                SourceEndpoint = field.SourceEndpoint,
                SourcePath = contractField.SourcePath,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ImmediateValue = immediate?.DeepClone(),
                Delayed1sValue = delayed1?.DeepClone(),
                Delayed3sValue = delayed3?.DeepClone(),
                Delayed5sValue = delayed5?.DeepClone(),
                FirstWriteSucceeded = first?.Success == true,
                SecondaryWriteSucceeded = secondary,
                ResendWriteSucceeded = resend,
                Behavior = behavior,
                Classification = behavior switch
                {
                    GroupedApplyBehavior.ImmediateApplied => ForcedFieldClassification.Writable,
                    GroupedApplyBehavior.DelayedApplied => ForcedFieldClassification.DelayedApply,
                    GroupedApplyBehavior.RequiresSecondWrite => ForcedFieldClassification.RequiresGroupedWrite,
                    GroupedApplyBehavior.RequiresCommitTrigger => ForcedFieldClassification.RequiresCommitTrigger,
                    _ => ForcedFieldClassification.Unsupported
                },
                BaselineFieldPresent = baseline is not null,
                DefinitionSource = contractField.Evidence.Source,
                Notes = first?.Message ?? string.Empty
            });
        }

        await store.SaveGroupedRetestResultsAsync(output, cancellationToken);
        await SaveGroupedProfilesAsync(device, fields, output, cancellationToken);
        await PromoteRetestedFieldsAsync(deviceId, output, cancellationToken);
        await PromoteImageInventoryAsync(deviceId, output, cancellationToken);
        logger.LogInformation("Grouped unsupported retest completed for {Device} fields={Count}", device.DisplayName, output.Count);
        return output;
    }

    public Task<IReadOnlyCollection<GroupedApplyProfile>> GetProfilesAsync(Guid deviceId, string? firmwareFingerprint, CancellationToken cancellationToken)
        => store.GetGroupedApplyProfilesAsync(deviceId, firmwareFingerprint, cancellationToken);

    public Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> GetRetestResultsAsync(Guid deviceId, int limit, CancellationToken cancellationToken)
        => store.GetGroupedRetestResultsAsync(deviceId, limit, cancellationToken);

    private async Task<(string SourceEndpoint, JsonObject Value)?> FindEndpointPayloadAsync(Guid deviceId, string endpointPattern, CancellationToken cancellationToken)
    {
        var snapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
        var endpointValue = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(item => EndpointPatternMatches(endpointPattern, NormalizeEndpoint(item.SourceEndpoint ?? item.Key)));
        return endpointValue?.Value is JsonObject obj
            ? (NormalizeEndpoint(endpointValue.SourceEndpoint ?? endpointValue.Key), (JsonObject)obj.DeepClone())
            : null;
    }

    private async Task<WriteResult?> ApplyGroupedFieldAsync(
        Guid deviceId,
        string endpoint,
        string sourcePath,
        JsonNode value,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        var snapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
        var endpointValue = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(item => NormalizeEndpoint(item.SourceEndpoint ?? item.Key).Equals(NormalizeEndpoint(endpoint), StringComparison.OrdinalIgnoreCase));
        if (endpointValue?.Value is not JsonObject root)
        {
            return null;
        }

        var payload = (JsonObject)root.DeepClone();
        SetPathValue(payload, sourcePath, value.DeepClone());
        var plan = new WritePlan
        {
            GroupName = "SDK Forced Enumeration",
            Endpoint = endpoint,
            Method = "PUT",
            AdapterName = "sdk-enumerator",
            Payload = payload,
            SnapshotBeforeWrite = true,
            RequireWriteVerification = !expertOverride
        };
        return await settingsService.WriteAsync(deviceId, plan, cancellationToken);
    }

    private async Task<JsonNode?> ReadFieldValueAsync(Guid deviceId, string endpoint, string sourcePath, CancellationToken cancellationToken)
    {
        var snapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
        var endpointValue = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(item => NormalizeEndpoint(item.SourceEndpoint ?? item.Key).Equals(NormalizeEndpoint(endpoint), StringComparison.OrdinalIgnoreCase));
        return TryGetPathValue(endpointValue?.Value, sourcePath);
    }

    private async Task SaveGroupedProfilesAsync(
        DeviceIdentity device,
        IReadOnlyCollection<NormalizedSettingField> fields,
        IReadOnlyCollection<GroupedUnsupportedRetestResult> results,
        CancellationToken cancellationToken)
    {
        var firmware = BuildFirmwareFingerprint(device, fields);
        var profiles = results
            .GroupBy(static result => result.GroupKind)
            .Select(group =>
            {
                var immediate = group.Count(item => item.Behavior == GroupedApplyBehavior.ImmediateApplied);
                var delayed = group.Count(item => item.Behavior == GroupedApplyBehavior.DelayedApplied);
                var second = group.Count(item => item.Behavior == GroupedApplyBehavior.RequiresSecondWrite);
                var commit = group.Count(item => item.Behavior == GroupedApplyBehavior.RequiresCommitTrigger);
                var unapplied = group.Count(item => item.Behavior == GroupedApplyBehavior.Unapplied);
                var dominant = ResolveDominantBehavior(group.Select(static item => item.Behavior));
                return new GroupedApplyProfile
                {
                    DeviceId = device.Id,
                    FirmwareFingerprint = firmware,
                    IpAddress = device.IpAddress ?? string.Empty,
                    GroupKind = group.Key,
                    DominantBehavior = dominant,
                    ImmediateAppliedCount = immediate,
                    DelayedAppliedCount = delayed,
                    RequiresSecondWriteCount = second,
                    RequiresCommitTriggerCount = commit,
                    UnappliedCount = unapplied,
                    Endpoints = group.Select(static item => item.SourceEndpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Notes = $"retested={group.Count()} fields"
                };
            })
            .ToList();

        await store.SaveGroupedApplyProfilesAsync(profiles, cancellationToken);
    }

    private async Task PromoteRetestedFieldsAsync(Guid deviceId, IReadOnlyCollection<GroupedUnsupportedRetestResult> results, CancellationToken cancellationToken)
    {
        var fields = (await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken)).ToList();
        var latestByKey = fields
            .GroupBy(static field => field.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(static item => item.CapturedAt).First(), StringComparer.OrdinalIgnoreCase);
        var promoted = new List<NormalizedSettingField>();
        foreach (var result in results)
        {
            if (!latestByKey.TryGetValue(result.FieldKey, out var current))
            {
                continue;
            }

            if (result.Behavior is GroupedApplyBehavior.Unapplied or GroupedApplyBehavior.Unknown)
            {
                continue;
            }

            promoted.Add(current with
            {
                SupportState = ContractSupportState.Supported,
                WriteVerified = true,
                Validity = FieldValidityState.Inferred,
                Confidence = $"grouped-retest:{result.Behavior}",
                CapturedAt = DateTimeOffset.UtcNow
            });
        }

        if (promoted.Count > 0)
        {
            await store.SaveNormalizedSettingFieldsAsync(promoted, cancellationToken);
        }
    }

    private async Task PromoteImageInventoryAsync(Guid deviceId, IReadOnlyCollection<GroupedUnsupportedRetestResult> results, CancellationToken cancellationToken)
    {
        var inventory = (await store.GetImageControlInventoryAsync(deviceId, cancellationToken)).ToList();
        if (inventory.Count == 0)
        {
            return;
        }

        var promoted = new List<ImageControlInventoryItem>();
        foreach (var item in inventory)
        {
            var match = results.FirstOrDefault(result => result.FieldKey.Equals(item.FieldKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                promoted.Add(item);
                continue;
            }

            if (match.Behavior is GroupedApplyBehavior.ImmediateApplied or GroupedApplyBehavior.DelayedApplied or GroupedApplyBehavior.RequiresSecondWrite)
            {
                promoted.Add(item with
                {
                    Readable = true,
                    Writable = true,
                    WriteVerified = true,
                    SupportState = ContractSupportState.Supported,
                    TruthState = ContractTruthState.Proven,
                    Status = ImageInventoryStatus.Writable,
                    CandidateClassification = HiddenCandidateClassification.ProvenWritable,
                    PromotedToUi = true,
                    ReasonCodes = ["grouped_model_retest_success"],
                    Notes = $"Promoted by grouped model retest ({match.Behavior}).",
                    CapturedAt = DateTimeOffset.UtcNow
                });
                continue;
            }

            if (match.Behavior == GroupedApplyBehavior.RequiresCommitTrigger)
            {
                promoted.Add(item with
                {
                    Readable = true,
                    Writable = false,
                    WriteVerified = false,
                    SupportState = ContractSupportState.Uncertain,
                    TruthState = ContractTruthState.Inferred,
                    Status = ImageInventoryStatus.TransportSuccessNoSemanticChange,
                    CandidateClassification = HiddenCandidateClassification.RequiresCommitTrigger,
                    PromotedToUi = false,
                    ReasonCodes = ["grouped_model_requires_commit_trigger"],
                    Notes = "Grouped model retest indicates commit trigger behavior.",
                    CapturedAt = DateTimeOffset.UtcNow
                });
                continue;
            }

            promoted.Add(item with
            {
                CandidateClassification = item.CandidateClassification == HiddenCandidateClassification.UnsupportedOnFirmware
                    ? HiddenCandidateClassification.LikelyUnsupported
                    : item.CandidateClassification
            });
        }

        await store.SaveImageControlInventoryAsync(promoted, cancellationToken);
    }

    private static ForcedFieldClassification ClassifyForcedField(
        GroupedApplyBehavior behavior,
        JsonNode? baseline,
        JsonNode? immediate,
        JsonNode? delayed1,
        JsonNode? delayed3,
        JsonNode? delayed5,
        JsonNode intended,
        bool baselinePresent,
        bool firstWriteSucceeded,
        bool secondaryWriteSucceeded,
        bool resendWriteSucceeded)
    {
        if (behavior == GroupedApplyBehavior.ImmediateApplied)
        {
            return baselinePresent ? ForcedFieldClassification.Writable : ForcedFieldClassification.RequiresGroupedWrite;
        }

        if (behavior == GroupedApplyBehavior.DelayedApplied)
        {
            return ForcedFieldClassification.DelayedApply;
        }

        if (behavior == GroupedApplyBehavior.RequiresSecondWrite)
        {
            return ForcedFieldClassification.RequiresGroupedWrite;
        }

        if (behavior == GroupedApplyBehavior.RequiresCommitTrigger)
        {
            return ForcedFieldClassification.RequiresCommitTrigger;
        }

        if (firstWriteSucceeded || secondaryWriteSucceeded || resendWriteSucceeded)
        {
            var unchanged = JsonNode.DeepEquals(baseline, immediate)
                && JsonNode.DeepEquals(baseline, delayed1)
                && JsonNode.DeepEquals(baseline, delayed3)
                && JsonNode.DeepEquals(baseline, delayed5)
                && !JsonNode.DeepEquals(intended, immediate)
                && !JsonNode.DeepEquals(intended, delayed1)
                && !JsonNode.DeepEquals(intended, delayed3)
                && !JsonNode.DeepEquals(intended, delayed5);
            return unchanged ? ForcedFieldClassification.Ignored : ForcedFieldClassification.RequiresCommitTrigger;
        }

        return baselinePresent ? ForcedFieldClassification.ReadableOnly : ForcedFieldClassification.Unsupported;
    }

    private static GroupedApplyBehavior ResolveDominantBehavior(IEnumerable<GroupedApplyBehavior> values)
    {
        var ordered = values.ToList();
        if (ordered.Contains(GroupedApplyBehavior.RequiresCommitTrigger))
        {
            return GroupedApplyBehavior.RequiresCommitTrigger;
        }
        if (ordered.Contains(GroupedApplyBehavior.RequiresSecondWrite))
        {
            return GroupedApplyBehavior.RequiresSecondWrite;
        }
        if (ordered.Contains(GroupedApplyBehavior.DelayedApplied))
        {
            return GroupedApplyBehavior.DelayedApplied;
        }
        if (ordered.Contains(GroupedApplyBehavior.ImmediateApplied))
        {
            return GroupedApplyBehavior.ImmediateApplied;
        }
        if (ordered.Contains(GroupedApplyBehavior.Unapplied))
        {
            return GroupedApplyBehavior.Unapplied;
        }
        return GroupedApplyBehavior.Unknown;
    }

    private static GroupedApplyBehavior ClassifyBehavior(
        JsonNode intended,
        JsonNode? immediate,
        JsonNode? delayed1,
        JsonNode? delayed3,
        JsonNode? delayed5,
        bool writeSuccess)
    {
        if (JsonNode.DeepEquals(immediate, intended))
        {
            return GroupedApplyBehavior.ImmediateApplied;
        }

        if (JsonNode.DeepEquals(delayed1, intended) || JsonNode.DeepEquals(delayed3, intended) || JsonNode.DeepEquals(delayed5, intended))
        {
            return GroupedApplyBehavior.DelayedApplied;
        }

        return writeSuccess ? GroupedApplyBehavior.RequiresCommitTrigger : GroupedApplyBehavior.Unapplied;
    }

    private static JsonNode? BuildSdkCandidateValue(SdkFieldDefinition def, JsonNode? baseline)
    {
        if (def.Kind == ContractFieldKind.Boolean)
        {
            return JsonValue.Create(!ParseBool(baseline));
        }

        if (def.Kind == ContractFieldKind.Enum && def.EnumValues.Count > 0)
        {
            var current = baseline?.ToJsonString().Trim('"');
            var next = def.EnumValues.FirstOrDefault(v => !string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(next) ? null : JsonValue.Create(next);
        }

        if (def.Kind is ContractFieldKind.Integer or ContractFieldKind.Number)
        {
            var number = TryToDecimal(baseline) ?? def.Min ?? 0;
            var min = def.Min ?? Math.Max(0, number - 10);
            var max = def.Max ?? (number + 10);
            var delta = ImageStepFields.Contains(def.FieldKey, StringComparer.OrdinalIgnoreCase) ? 1m : 1m;
            var attempted = Clamp(number + delta, min, max);
            if (attempted == number)
            {
                attempted = Clamp(number - delta, min, max);
            }

            return def.Kind == ContractFieldKind.Integer ? JsonValue.Create((int)attempted) : JsonValue.Create(attempted);
        }

        if (def.Kind == ContractFieldKind.String || def.Kind == ContractFieldKind.Password || def.Kind == ContractFieldKind.IpAddress)
        {
            var current = baseline?.ToJsonString().Trim('"');
            if (string.IsNullOrWhiteSpace(current))
            {
                return JsonValue.Create("sdk-test");
            }

            return JsonValue.Create(current.Equals("sdk-test", StringComparison.OrdinalIgnoreCase) ? "sdk-test-2" : "sdk-test");
        }

        if (def.Kind == ContractFieldKind.Object)
        {
            var cloned = baseline?.DeepClone();
            if (cloned is JsonObject obj && TryMutateAnyScalar(obj))
            {
                return obj;
            }

            return new JsonObject();
        }

        if (def.Kind == ContractFieldKind.Array)
        {
            var cloned = baseline?.DeepClone();
            if (cloned is JsonArray arr)
            {
                if (arr.Count == 0)
                {
                    arr.Add(new JsonObject { ["enabled"] = true });
                    return arr;
                }

                if (arr[0] is JsonObject obj && TryMutateAnyScalar(obj))
                {
                    return arr;
                }
            }

            return new JsonArray();
        }

        return null;
    }

    private static bool TryMutateAnyScalar(JsonObject obj)
    {
        foreach (var property in obj.ToList())
        {
            if (property.Value is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var b))
                {
                    obj[property.Key] = !b;
                    return true;
                }

                if (value.TryGetValue<int>(out var i))
                {
                    obj[property.Key] = i == 0 ? 1 : i - 1;
                    return true;
                }

                if (value.TryGetValue<string>(out var s))
                {
                    obj[property.Key] = string.IsNullOrWhiteSpace(s) ? "sdk-test" : s + "-sdk";
                    return true;
                }
            }

            if (property.Value is JsonObject child && TryMutateAnyScalar(child))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonNode? BuildCandidateValue(string fieldKey, JsonNode? baseline, ContractField field)
    {
        if (field.Kind == ContractFieldKind.Boolean)
        {
            var current = ParseBool(baseline);
            return JsonValue.Create(!current);
        }

        if (field.Kind == ContractFieldKind.Enum && field.EnumValues.Count > 0)
        {
            var current = baseline?.ToJsonString().Trim('"');
            var next = field.EnumValues.Select(static value => value.Value).FirstOrDefault(value => !string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(next) ? null : JsonValue.Create(next);
        }

        var number = TryToDecimal(baseline);
        if (number is null || field.Kind is not (ContractFieldKind.Integer or ContractFieldKind.Number))
        {
            return null;
        }

        var min = field.Validation.Min ?? Math.Max(0, number.Value - 10);
        var max = field.Validation.Max ?? number.Value + 10;
        decimal delta = 1m;
        if (ImageStepFields.Contains(fieldKey, StringComparer.OrdinalIgnoreCase))
        {
            delta = 1m;
        }
        var attempted = Clamp(number.Value + delta, min, max);
        if (attempted == number.Value)
        {
            attempted = Clamp(number.Value - delta, min, max);
        }

        return field.Kind == ContractFieldKind.Integer ? JsonValue.Create((int)attempted) : JsonValue.Create(attempted);
    }

    private static GroupedConfigKind ResolveGroupKind(string fieldKey)
    {
        if (fieldKey.Contains("alarm", StringComparison.OrdinalIgnoreCase) || fieldKey.Contains("motion", StringComparison.OrdinalIgnoreCase))
        {
            return GroupedConfigKind.AlarmConfig;
        }
        if (fieldKey.Contains("sd", StringComparison.OrdinalIgnoreCase) || fieldKey.Contains("storage", StringComparison.OrdinalIgnoreCase) || fieldKey.Contains("playback", StringComparison.OrdinalIgnoreCase))
        {
            return GroupedConfigKind.StorageConfig;
        }
        if (fieldKey.Contains("wireless", StringComparison.OrdinalIgnoreCase) || fieldKey.StartsWith("ap", StringComparison.OrdinalIgnoreCase))
        {
            return GroupedConfigKind.WifiConfig;
        }
        if (fieldKey.Contains("ip", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("dns", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("gateway", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("port", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("dhcp", StringComparison.OrdinalIgnoreCase))
        {
            return GroupedConfigKind.NetworkConfig;
        }
        if (fieldKey.Contains("codec", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("resolution", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("bitrate", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("frame", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("keyframe", StringComparison.OrdinalIgnoreCase)
            || fieldKey.Contains("profile", StringComparison.OrdinalIgnoreCase))
        {
            return GroupedConfigKind.VideoEncodeConfig;
        }
        if (fieldKey.Contains("user", StringComparison.OrdinalIgnoreCase) || fieldKey.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return GroupedConfigKind.UserConfig;
        }
        return GroupedConfigKind.ImageConfig;
    }

    private static bool MatchesGroup(GroupedConfigKind kind, string endpoint)
        => kind switch
        {
            GroupedConfigKind.ImageConfig => endpoint.Contains("/netsdk/image", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/video/input/channel", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("overlay", StringComparison.OrdinalIgnoreCase),
            GroupedConfigKind.VideoEncodeConfig => endpoint.Contains("/netsdk/video/encode/channel", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/stream/channel", StringComparison.OrdinalIgnoreCase),
            GroupedConfigKind.NetworkConfig => endpoint.Contains("/netsdk/network/interfaces", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/network/ports", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/network/dns", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/network/esee", StringComparison.OrdinalIgnoreCase),
            GroupedConfigKind.WifiConfig => endpoint.Contains("/netsdk/network/interfaces", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("wireless", StringComparison.OrdinalIgnoreCase),
            GroupedConfigKind.UserConfig => endpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/system/deviceinfo", StringComparison.OrdinalIgnoreCase),
            GroupedConfigKind.AlarmConfig => endpoint.Contains("/netsdk/io/alarm", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("/netsdk/video/motiondetection", StringComparison.OrdinalIgnoreCase),
            GroupedConfigKind.StorageConfig => endpoint.Contains("/netsdk/sdcard", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool EndpointPatternMatches(string pattern, string endpoint)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(NormalizeEndpoint(pattern)).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(NormalizeEndpoint(endpoint), regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string NormalizeEndpoint(string endpoint)
        => (endpoint ?? string.Empty)
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static string BuildFirmwareFingerprint(DeviceIdentity device, IEnumerable<NormalizedSettingField> fields)
        => fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
           ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

    private static decimal Clamp(decimal value, decimal min, decimal max) => Math.Min(max, Math.Max(min, value));

    private static bool ParseBool(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var parsed))
            {
                return parsed;
            }
            if (value.TryGetValue<string>(out var raw) && bool.TryParse(raw, out parsed))
            {
                return parsed;
            }
        }

        return bool.TryParse(node?.ToJsonString().Trim('"'), out var fallback) && fallback;
    }

    private static decimal? TryToDecimal(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var d))
            {
                return d;
            }
            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }
            if (value.TryGetValue<double>(out var dbl))
            {
                return Convert.ToDecimal(dbl);
            }
            if (value.TryGetValue<string>(out var s) && decimal.TryParse(s, out var parsed))
            {
                return parsed;
            }
        }

        return decimal.TryParse(node?.ToJsonString().Trim('"'), out var fallback) ? fallback : null;
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
            if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var next))
            {
                current = next;
                continue;
            }

            return null;
        }

        return current;
    }

    private static void SetPathValue(JsonObject root, string path, JsonNode? value)
    {
        var cleaned = path.Trim().TrimStart('$').TrimStart('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonObject current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var key = parts[index];
            var leaf = index == parts.Length - 1;
            if (leaf)
            {
                current[key] = value?.DeepClone();
                return;
            }

            current[key] ??= new JsonObject();
            if (current[key] is JsonObject child)
            {
                current = child;
            }
            else
            {
                return;
            }
        }
    }

    private static IReadOnlyCollection<SdkFieldDefinition> BuildSdkFieldCatalog()
    {
        static SdkFieldDefinition F(GroupedConfigKind group, string key, string label, string endpoint, string path, ContractFieldKind kind, bool writable = true, decimal? min = null, decimal? max = null, IReadOnlyCollection<string>? enums = null, string source = "ipc-sdk-v1.4", string notes = "")
            => new()
            {
                GroupKind = group,
                FieldKey = key,
                DisplayName = label,
                EndpointPattern = endpoint,
                SourcePath = path,
                Kind = kind,
                Writable = writable,
                Min = min,
                Max = max,
                EnumValues = enums ?? [],
                SourceEvidence = source,
                Notes = notes
            };

        return
        [
            F(GroupedConfigKind.ImageConfig, "brightness", "Brightness", "/NetSDK/Video/input/channel/*", "$.brightnessLevel", ContractFieldKind.Integer, true, 0, 100),
            F(GroupedConfigKind.ImageConfig, "contrast", "Contrast", "/NetSDK/Video/input/channel/*", "$.contrastLevel", ContractFieldKind.Integer, true, 0, 100),
            F(GroupedConfigKind.ImageConfig, "saturation", "Saturation", "/NetSDK/Video/input/channel/*", "$.saturationLevel", ContractFieldKind.Integer, true, 0, 100),
            F(GroupedConfigKind.ImageConfig, "sharpness", "Sharpness", "/NetSDK/Video/input/channel/*", "$.sharpnessLevel", ContractFieldKind.Integer, true, 0, 100),
            F(GroupedConfigKind.ImageConfig, "hue", "Hue", "/NetSDK/Video/input/channel/*", "$.hueLevel", ContractFieldKind.Integer, true, 0, 100),
            F(GroupedConfigKind.ImageConfig, "captureFrameRate", "Capture Frame Rate", "/NetSDK/Video/input/channel/*", "$.captureFrameRate", ContractFieldKind.Integer, false),
            F(GroupedConfigKind.ImageConfig, "powerLineFrequencyMode", "Power Line Frequency", "/NetSDK/Video/input/channel/*", "$.powerLineFrequencyMode", ContractFieldKind.Integer, false),
            F(GroupedConfigKind.ImageConfig, "mirror", "Mirror", "/NetSDK/Video/input/channel/*", "$.mirrorEnabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.ImageConfig, "flip", "Flip", "/NetSDK/Video/input/channel/*", "$.flipEnabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.ImageConfig, "sceneMode", "Scene Mode", "/NetSDK/Image/*", "$.sceneMode", ContractFieldKind.Enum, true, enums: ["auto", "indoor", "outdoor"]),
            F(GroupedConfigKind.ImageConfig, "exposureMode", "Exposure Mode", "/NetSDK/Image/*", "$.exposureMode", ContractFieldKind.Enum, true, enums: ["auto", "bright", "dark"]),
            F(GroupedConfigKind.ImageConfig, "awbMode", "AWB Mode", "/NetSDK/Image/*", "$.awbMode", ContractFieldKind.Enum, true, enums: ["auto", "indoor", "outdoor"]),
            F(GroupedConfigKind.ImageConfig, "lowlightMode", "Lowlight Mode", "/NetSDK/Image/*", "$.lowlightMode", ContractFieldKind.Enum, true, enums: ["close", "only night", "day-night", "auto"]),
            F(GroupedConfigKind.ImageConfig, "irCutControlMode", "IR Cut Control Mode", "/NetSDK/Image/*", "$.irCutFilter.irCutControlMode", ContractFieldKind.Enum, true, enums: ["hardware", "software"]),
            F(GroupedConfigKind.ImageConfig, "irCutMode", "IR Cut Mode", "/NetSDK/Image/*", "$.irCutFilter.irCutMode", ContractFieldKind.Enum, true, enums: ["auto", "daylight", "night"]),
            F(GroupedConfigKind.ImageConfig, "manualSharpnessEnabled", "Manual Sharpness Enabled", "/NetSDK/Image/*", "$.manualSharpness.enabled", ContractFieldKind.Boolean, false),
            F(GroupedConfigKind.ImageConfig, "manualSharpness", "Manual Sharpness", "/NetSDK/Image/*", "$.manualSharpness.sharpnessLevel", ContractFieldKind.Integer, true, 0, 255),
            F(GroupedConfigKind.ImageConfig, "denoise3dEnabled", "Denoise3D Enabled", "/NetSDK/Image/*", "$.denoise3d.enabled", ContractFieldKind.Boolean, false),
            F(GroupedConfigKind.ImageConfig, "denoise3dStrength", "Denoise3D Strength", "/NetSDK/Image/*", "$.denoise3d.denoise3dStrength", ContractFieldKind.Integer, true, 1, 5),
            F(GroupedConfigKind.ImageConfig, "wdrEnabled", "WDR Enabled", "/NetSDK/Image/*", "$.WDR.enabled", ContractFieldKind.Boolean, false),
            F(GroupedConfigKind.ImageConfig, "wdr", "WDR Strength", "/NetSDK/Image/*", "$.WDR.WDRStrength", ContractFieldKind.Integer, true, 1, 5),

            F(GroupedConfigKind.VideoEncodeConfig, "channelName", "Channel Name", "/NetSDK/Video/encode/channel/*", "$.channelName", ContractFieldKind.String),
            F(GroupedConfigKind.VideoEncodeConfig, "videoEnabled", "Video Enabled", "/NetSDK/Video/encode/channel/*", "$.enabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.VideoEncodeConfig, "videoInputChannelID", "Video Input Channel ID", "/NetSDK/Video/encode/channel/*", "$.videoInputChannelID", ContractFieldKind.Integer, false),
            F(GroupedConfigKind.VideoEncodeConfig, "codec", "Codec", "/NetSDK/Video/encode/channel/*", "$.codecType", ContractFieldKind.Enum, true, enums: ["H.264", "H.265", "MJPEG"]),
            F(GroupedConfigKind.VideoEncodeConfig, "profile", "H264 Profile", "/NetSDK/Video/encode/channel/*", "$.h264Profile", ContractFieldKind.Enum, true, enums: ["baseline", "main", "high"]),
            F(GroupedConfigKind.VideoEncodeConfig, "resolution", "Resolution", "/NetSDK/Video/encode/channel/*", "$.resolution", ContractFieldKind.String),
            F(GroupedConfigKind.VideoEncodeConfig, "freeResolution", "Free Resolution", "/NetSDK/Video/encode/channel/*", "$.freeResolution", ContractFieldKind.Boolean),
            F(GroupedConfigKind.VideoEncodeConfig, "resolutionWidth", "Resolution Width", "/NetSDK/Video/encode/channel/*", "$.resolutionWidth", ContractFieldKind.Integer, true, 1, 8192),
            F(GroupedConfigKind.VideoEncodeConfig, "resolutionHeight", "Resolution Height", "/NetSDK/Video/encode/channel/*", "$.resolutionHeight", ContractFieldKind.Integer, true, 1, 8192),
            F(GroupedConfigKind.VideoEncodeConfig, "bitRateControlType", "Bitrate Mode", "/NetSDK/Video/encode/channel/*", "$.bitRateControlType", ContractFieldKind.Enum, true, enums: ["CBR", "VBR"]),
            F(GroupedConfigKind.VideoEncodeConfig, "bitrate", "Constant Bitrate", "/NetSDK/Video/encode/channel/*", "$.constantBitRate", ContractFieldKind.Integer, true, 1, 65535),
            F(GroupedConfigKind.VideoEncodeConfig, "frameRate", "Frame Rate", "/NetSDK/Video/encode/channel/*", "$.frameRate", ContractFieldKind.Integer, true, 1, 120),
            F(GroupedConfigKind.VideoEncodeConfig, "keyframeInterval", "Keyframe Interval", "/NetSDK/Video/encode/channel/*", "$.keyFrameInterval", ContractFieldKind.Integer, true, 1, 240),

            F(GroupedConfigKind.NetworkConfig, "addressingType", "Addressing Type", "/NetSDK/Network/interfaces/*", "$.lan.addressingType", ContractFieldKind.Enum, true, enums: ["static", "dynamic"]),
            F(GroupedConfigKind.NetworkConfig, "staticIP", "Static IP", "/NetSDK/Network/interfaces/*", "$.lan.staticIP", ContractFieldKind.IpAddress),
            F(GroupedConfigKind.NetworkConfig, "staticNetmask", "Static Netmask", "/NetSDK/Network/interfaces/*", "$.lan.staticNetmask", ContractFieldKind.IpAddress),
            F(GroupedConfigKind.NetworkConfig, "staticGateway", "Static Gateway", "/NetSDK/Network/interfaces/*", "$.lan.staticGateway", ContractFieldKind.IpAddress),
            F(GroupedConfigKind.NetworkConfig, "upnpEnabled", "UPnP Enabled", "/NetSDK/Network/interfaces/*", "$.upnp.enabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.NetworkConfig, "pppoeEnabled", "PPPoE Enabled", "/NetSDK/Network/interfaces/*", "$.pppoe.enabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.NetworkConfig, "pppoeUserName", "PPPoE Username", "/NetSDK/Network/interfaces/*", "$.pppoe.userName", ContractFieldKind.String),
            F(GroupedConfigKind.NetworkConfig, "pppoePassword", "PPPoE Password", "/NetSDK/Network/interfaces/*", "$.pppoe.password", ContractFieldKind.Password),
            F(GroupedConfigKind.NetworkConfig, "preferredDns", "Preferred DNS", "/NetSDK/Network/DNS", "$.preferredDns", ContractFieldKind.IpAddress, false),
            F(GroupedConfigKind.NetworkConfig, "alternateDns", "Alternate DNS", "/NetSDK/Network/DNS", "$.staticAlternateDns", ContractFieldKind.IpAddress, false),
            F(GroupedConfigKind.NetworkConfig, "eseeEnabled", "ESEE Enabled", "/NetSDK/Network/Esee", "$.enabled", ContractFieldKind.Boolean, false),
            F(GroupedConfigKind.NetworkConfig, "portValue", "Port Value", "/NetSDK/Network/ports/*", "$.value", ContractFieldKind.Integer, true, 1, 60000),

            F(GroupedConfigKind.WifiConfig, "wirelessMode", "Wireless Mode", "/NetSDK/Network/interfaces/*", "$.wireless.wirelessMode", ContractFieldKind.Enum, true, enums: ["none", "accessPoint", "stationMode"]),
            F(GroupedConfigKind.WifiConfig, "wirelessApBssId", "Wireless AP BSSID", "/NetSDK/Network/interfaces/*", "$.wireless.stationMode.wirelessApBssId", ContractFieldKind.String),
            F(GroupedConfigKind.WifiConfig, "wirelessApEssId", "Wireless AP ESSID", "/NetSDK/Network/interfaces/*", "$.wireless.stationMode.wirelessApEssId", ContractFieldKind.String),
            F(GroupedConfigKind.WifiConfig, "wirelessApPsk", "Wireless AP PSK", "/NetSDK/Network/interfaces/*", "$.wireless.stationMode.wirelessApPsk", ContractFieldKind.Password),
            F(GroupedConfigKind.WifiConfig, "wirelessEssId", "AP ESSID", "/NetSDK/Network/interfaces/*", "$.wireless.accessPointMode.wirelessEssId", ContractFieldKind.String),
            F(GroupedConfigKind.WifiConfig, "wirelessPsk", "AP PSK", "/NetSDK/Network/interfaces/*", "$.wireless.accessPointMode.wirelessPsk", ContractFieldKind.Password),
            F(GroupedConfigKind.WifiConfig, "wirelessApMode", "AP Mode", "/NetSDK/Network/interfaces/*", "$.wireless.accessPointMode.wirelessApMode", ContractFieldKind.Enum, true, enums: ["802.11b", "802.11g", "802.11n", "802.11bg", "802.11bgn"]),
            F(GroupedConfigKind.WifiConfig, "wirelessApMode80211nChannel", "AP Channel", "/NetSDK/Network/interfaces/*", "$.wireless.accessPointMode.wirelessApMode80211nChannel", ContractFieldKind.Enum, true, enums: ["Auto", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14"]),
            F(GroupedConfigKind.WifiConfig, "wirelessWpaMode", "Wireless WPA Mode", "/NetSDK/Network/interfaces/*", "$.wireless.accessPointMode.wirelessWpaMode", ContractFieldKind.Enum, true, enums: ["WPA_PSK", "WPA2_PSK"]),
            F(GroupedConfigKind.WifiConfig, "dhcpIpRange", "DHCP IP Range", "/NetSDK/Network/interfaces/*", "$.dhcpServer.dhcpIpRange", ContractFieldKind.String, false),
            F(GroupedConfigKind.WifiConfig, "dhcpIpNumber", "DHCP IP Number", "/NetSDK/Network/interfaces/*", "$.dhcpServer.dhcpIpNumber", ContractFieldKind.String, false),
            F(GroupedConfigKind.WifiConfig, "dhcpIpDns", "DHCP DNS", "/NetSDK/Network/interfaces/*", "$.dhcpServer.dhcpIpDns", ContractFieldKind.String, false),
            F(GroupedConfigKind.WifiConfig, "dhcpIpGateway", "DHCP Gateway", "/NetSDK/Network/interfaces/*", "$.dhcpServer.dhcpIpGateway", ContractFieldKind.String, false),

            F(GroupedConfigKind.UserConfig, "deviceName", "Device Name", "/NetSDK/System/deviceInfo", "$.name", ContractFieldKind.String),
            F(GroupedConfigKind.UserConfig, "deviceSerial", "Device Serial", "/NetSDK/System/deviceInfo", "$.serial", ContractFieldKind.String, false),
            F(GroupedConfigKind.UserConfig, "deviceModel", "Device Model", "/NetSDK/System/deviceInfo", "$.model", ContractFieldKind.String, false),
            F(GroupedConfigKind.UserConfig, "ntpEnabled", "NTP Enabled", "/NetSDK/System/time/ntp", "$.ntpEnabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.UserConfig, "ntpServerDomain", "NTP Server", "/NetSDK/System/time/ntp", "$.ntpServerDomain", ContractFieldKind.String),
            F(GroupedConfigKind.UserConfig, "userList", "User List", "/user/user_list.xml", "$.users", ContractFieldKind.Array, false, source: "ipcamsuite-private-manifest"),

            F(GroupedConfigKind.AlarmConfig, "alarmInputDefaultState", "Alarm Input Default State", "/NetSDK/IO/alarmInput/channel/*", "$.active.defaultState", ContractFieldKind.Enum, true, enums: ["high", "low"]),
            F(GroupedConfigKind.AlarmConfig, "alarmInputActiveState", "Alarm Input Active State", "/NetSDK/IO/alarmInput/channel/*", "$.active.activeState", ContractFieldKind.Enum, true, enums: ["high", "low"]),
            F(GroupedConfigKind.AlarmConfig, "alarmOutputDefaultState", "Alarm Output Default State", "/NetSDK/IO/alarmOutput/channel/*", "$.active.defaultState", ContractFieldKind.Enum, true, enums: ["high", "low"]),
            F(GroupedConfigKind.AlarmConfig, "alarmOutputActiveState", "Alarm Output Active State", "/NetSDK/IO/alarmOutput/channel/*", "$.active.activeState", ContractFieldKind.Enum, true, enums: ["high", "low"]),
            F(GroupedConfigKind.AlarmConfig, "alarmOutputPowerOnState", "Alarm Output Power-On State", "/NetSDK/IO/alarmOutput/channel/*", "$.powerOnState", ContractFieldKind.Enum, true, enums: ["continuous", "low"]),
            F(GroupedConfigKind.AlarmConfig, "alarmOutputPulseDuration", "Alarm Output Pulse Duration", "/NetSDK/IO/alarmOutput/channel/*", "$.pulseDuration", ContractFieldKind.Integer, true, 1000, 60000),
            F(GroupedConfigKind.AlarmConfig, "motionEnabled", "Motion Detection Enabled", "/NetSDK/Video/motionDetection/channel/*", "$.enabled", ContractFieldKind.Boolean),
            F(GroupedConfigKind.AlarmConfig, "motionDetectionType", "Motion Detection Type", "/NetSDK/Video/motionDetection/channel/*", "$.detectionType", ContractFieldKind.Enum, true, enums: ["grid", "region"]),
            F(GroupedConfigKind.AlarmConfig, "motionGrid", "Motion Grid", "/NetSDK/Video/motionDetection/channel/*", "$.detectionGrid", ContractFieldKind.Object),
            F(GroupedConfigKind.AlarmConfig, "motionRegion", "Motion Region", "/NetSDK/Video/motionDetection/channel/*", "$.detectionRegion", ContractFieldKind.Array),

            F(GroupedConfigKind.StorageConfig, "sdSessionId", "SD Search Session ID", "/NetSDK/SDCard/media/search", "$.sessionID", ContractFieldKind.Integer),
            F(GroupedConfigKind.StorageConfig, "sdChannelId", "SD Search Channel ID", "/NetSDK/SDCard/media/search", "$.channelID", ContractFieldKind.Integer),
            F(GroupedConfigKind.StorageConfig, "sdBeginUtc", "SD Search Begin UTC", "/NetSDK/SDCard/media/search", "$.beginUTC", ContractFieldKind.Integer),
            F(GroupedConfigKind.StorageConfig, "sdEndUtc", "SD Search End UTC", "/NetSDK/SDCard/media/search", "$.endUTC", ContractFieldKind.Integer),
            F(GroupedConfigKind.StorageConfig, "sdType", "SD Search Type", "/NetSDK/SDCard/media/search", "$.type", ContractFieldKind.String),
            F(GroupedConfigKind.StorageConfig, "sdStatus", "SD Card Status", "/NetSDK/SDCard/status", "$.status", ContractFieldKind.String, false),
            F(GroupedConfigKind.StorageConfig, "sdFormat", "SD Card Format", "/NetSDK/SDCard/format", "$.format", ContractFieldKind.Boolean, true, source: "ipc-sdk-v1.4", notes: "Dangerous operation; write guarded by caller includeDangerous flag."),
            F(GroupedConfigKind.StorageConfig, "sdPlaybackFlv", "SD Playback FLV", "/NetSDK/SDCard/media/playbackFLV", "$.sessionID", ContractFieldKind.Integer)
        ];
    }
}
