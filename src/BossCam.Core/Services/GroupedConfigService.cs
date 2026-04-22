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
            var first = await ApplyGroupedFieldAsync(deviceId, field, contract, contractField, candidate, request.ExpertOverride, cancellationToken);
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
                var secondWrite = await ApplyGroupedFieldAsync(deviceId, field, contract, contractField, candidate, request.ExpertOverride, cancellationToken);
                secondary = secondWrite?.Success == true;
                immediate = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
                behavior = JsonNode.DeepEquals(immediate, candidate)
                    ? GroupedApplyBehavior.RequiresSecondWrite
                    : behavior;

                if (behavior is GroupedApplyBehavior.RequiresCommitTrigger or GroupedApplyBehavior.Unapplied)
                {
                    var thirdWrite = await ApplyGroupedFieldAsync(deviceId, field, contract, contractField, candidate, request.ExpertOverride, cancellationToken);
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

    private async Task<WriteResult?> ApplyGroupedFieldAsync(
        Guid deviceId,
        NormalizedSettingField field,
        EndpointContract contract,
        ContractField contractField,
        JsonNode value,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        var snapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
        var endpointValue = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(item => NormalizeEndpoint(item.SourceEndpoint ?? item.Key).Equals(NormalizeEndpoint(field.SourceEndpoint), StringComparison.OrdinalIgnoreCase));
        if (endpointValue?.Value is not JsonObject root)
        {
            return null;
        }

        var payload = (JsonObject)root.DeepClone();
        SetPathValue(payload, contractField.SourcePath, value.DeepClone());
        var plan = new WritePlan
        {
            GroupName = contract.GroupName,
            Endpoint = field.SourceEndpoint,
            Method = contract.Method,
            AdapterName = field.AdapterName,
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
                    Notes = $"retested={group.Count()} unsupported fields"
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
            GroupedConfigKind.UserConfig => endpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase),
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
}
