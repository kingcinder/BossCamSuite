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

    public async Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> ProbeGroupedFamiliesAsync(Guid deviceId, GroupedFamilyProbeRequest request, CancellationToken cancellationToken)
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
        var results = new List<GroupedUnsupportedRetestResult>();
        var requestedFamilies = request.Families.Count == 0
            ? null
            : new HashSet<string>(request.Families, StringComparer.OrdinalIgnoreCase);
        var requestedFields = request.FieldKeys.Count == 0
            ? null
            : new HashSet<string>(request.FieldKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var family in BuildProbeFamilies(request.IncludePrivacyMasks))
        {
            if (requestedFamilies is not null && !requestedFamilies.Contains(family.Name))
            {
                continue;
            }

            var filteredFields = requestedFields is null
                ? family.Fields
                : family.Fields.Where(field => requestedFields.Contains(field.FieldKey)).ToList();
            if (filteredFields.Count == 0)
            {
                continue;
            }

            results.AddRange(await ProbeFamilyAsync(device, family with { Fields = filteredFields }, request.ExpertOverride, cancellationToken));
        }

        await store.SaveGroupedRetestResultsAsync(results, cancellationToken);
        await SaveGroupedProfilesAsync(device, normalizedFields, results, cancellationToken);
        await PromoteRetestedFieldsAsync(device.Id, results, cancellationToken);
        await PromoteImageInventoryAsync(device.Id, results, cancellationToken);
        logger.LogInformation("Grouped family probe completed for {Device} fields={Count}", device.DisplayName, results.Count);
        return results;
    }

    public async Task<PipelineOwnershipProbeReport?> ProbePipelineOwnershipAsync(Guid deviceId, PipelineOwnershipProbeRequest request, CancellationToken cancellationToken)
    {
        if (request.RefreshFromDevice)
        {
            _ = await typedSettingsService.NormalizeDeviceAsync(deviceId, refreshFromDevice: true, cancellationToken);
            _ = await settingsService.ReadAsync(deviceId, cancellationToken);
        }

        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var normalizedFields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var firmware = BuildFirmwareFingerprint(device, normalizedFields);
        var videoInputShape = await ReadEndpointObjectDirectAsync(deviceId, "/NetSDK/Video/input/channel/1", cancellationToken);
        var imageShape = await ReadEndpointObjectDirectAsync(deviceId, "/NetSDK/Image", cancellationToken);
        var groupedResults = new List<GroupedUnsupportedRetestResult>();
        var fields = new List<PipelineOwnershipFieldResult>
        {
            BuildStaticPipelineResult(device, firmware, "brightness", "Brightness", FieldPipelineGroup.Isp, "/NetSDK/Video/input/channel/1", "$.brightnessLevel", TryGetPathValue(videoInputShape, "$.brightnessLevel"), groupedResults),
            BuildStaticPipelineResult(device, firmware, "contrast", "Contrast", FieldPipelineGroup.Isp, "/NetSDK/Video/input/channel/1", "$.contrastLevel", TryGetPathValue(videoInputShape, "$.contrastLevel"), groupedResults),
            BuildStaticPipelineResult(device, firmware, "saturation", "Saturation", FieldPipelineGroup.Isp, "/NetSDK/Video/input/channel/1", "$.saturationLevel", TryGetPathValue(videoInputShape, "$.saturationLevel"), groupedResults),
            BuildStaticPipelineResult(device, firmware, "sharpness", "Sharpness", FieldPipelineGroup.Isp, "/NetSDK/Video/input/channel/1", "$.sharpnessLevel", TryGetPathValue(videoInputShape, "$.sharpnessLevel"), groupedResults)
        };

        var hue = await ProbeHueOwnershipAsync(device, firmware, request.ExpertOverride, cancellationToken);
        fields.Add(hue.Field);
        groupedResults.Add(hue.GroupedResult);

        var flip = await ProbeVideoInputBooleanAsync(
            device,
            firmware,
            "flip",
            "Flip",
            "$.flipEnabled",
            "/NetSDK/Video/input/channel/1",
            "/NetSDK/Video/input/channel/1/flip",
            request.ExpertOverride,
            cancellationToken);
        fields.Add(flip.Field);
        groupedResults.Add(flip.GroupedResult);

        var mirror = await ProbeVideoInputBooleanAsync(
            device,
            firmware,
            "mirror",
            "Mirror",
            "$.mirrorEnabled",
            "/NetSDK/Video/input/channel/1",
            "/NetSDK/Video/input/channel/1/mirror",
            request.ExpertOverride,
            cancellationToken);
        fields.Add(mirror.Field);
        groupedResults.Add(mirror.GroupedResult);

        var sceneMode = await ProbeFullObjectFieldAsync(
            device,
            firmware,
            "sceneMode",
            "Scene Mode",
            FieldPipelineGroup.ModeHardware,
            "/NetSDK/Image",
            "$.sceneMode",
            static baseline => NextEnumValue(baseline, ["auto", "indoor", "outdoor"]),
            request.ExpertOverride,
            cancellationToken);
        fields.Add(sceneMode.Field);
        groupedResults.Add(sceneMode.GroupedResult);

        var irCut = await ProbeFullObjectFieldAsync(
            device,
            firmware,
            "irCut",
            "IR Cut",
            FieldPipelineGroup.ModeHardware,
            "/NetSDK/Image/irCutFilter",
            "$.irCutMode",
            static baseline => NextEnumValue(baseline, ["auto", "daylight", "night"]),
            request.ExpertOverride,
            cancellationToken);
        fields.Add(irCut.Field);
        groupedResults.Add(irCut.GroupedResult);

        var dayNight = await ProbeFullObjectFieldAsync(
            device,
            firmware,
            "dayNight",
            "Day/Night",
            FieldPipelineGroup.ModeHardware,
            "/NetSDK/Image",
            "$.lowlightMode",
            static baseline => NextEnumValue(baseline, ["close", "only night", "day-night", "auto"]),
            request.ExpertOverride,
            cancellationToken);
        fields.Add(dayNight.Field);
        groupedResults.Add(dayNight.GroupedResult);

        var encode = await ProbeEncodeFullObjectAsync(device, firmware, request.ExpertOverride, cancellationToken);
        if (encode.GroupedResult is not null)
        {
            groupedResults.Add(encode.GroupedResult);
        }

        await store.SaveGroupedRetestResultsAsync(groupedResults, cancellationToken);
        await SaveGroupedProfilesAsync(device, normalizedFields, groupedResults, cancellationToken);
        await PromoteRetestedFieldsAsync(device.Id, groupedResults, cancellationToken);
        await PromoteImageInventoryAsync(device.Id, groupedResults, cancellationToken);

        return new PipelineOwnershipProbeReport
        {
            DeviceId = device.Id,
            IpAddress = device.IpAddress ?? string.Empty,
            FirmwareFingerprint = firmware,
            VideoInputShape = videoInputShape,
            ImageShape = imageShape,
            Fields = fields,
            EncodeProbe = encode.Report
        };
    }

    private sealed record FamilyProbeDefinition(
        string Name,
        string Endpoint,
        GroupedConfigKind GroupKind,
        IReadOnlyCollection<FamilyFieldProbe> Fields,
        string? CommitTriggerEndpoint = null,
        string? CommitTriggerMethod = null,
        JsonObject? CommitTriggerPayload = null,
        string? ReadTriggerEndpoint = null,
        string? SecondaryApplyEndpoint = null);

    private sealed record FamilyFieldProbe(
        string FieldKey,
        string SourcePath,
        Func<JsonObject, JsonNode?, JsonNode?> CandidateFactory,
        Func<JsonObject, JsonObject?>? RelatedFieldPayloadFactory = null);

    private static PipelineOwnershipFieldResult BuildStaticPipelineResult(
        DeviceIdentity device,
        string firmware,
        string fieldKey,
        string displayName,
        FieldPipelineGroup pipeline,
        string endpoint,
        string sourcePath,
        JsonNode? baselineValue,
        ICollection<GroupedUnsupportedRetestResult> groupedResults)
    {
        groupedResults.Add(new GroupedUnsupportedRetestResult
        {
            DeviceId = device.Id,
            FirmwareFingerprint = firmware,
            IpAddress = device.IpAddress ?? string.Empty,
            GroupKind = GroupedConfigKind.ImageConfig,
            ContractKey = $"pipeline.{fieldKey}",
            FieldKey = fieldKey,
            SourceEndpoint = endpoint,
            SourcePath = sourcePath,
            BaselineValue = baselineValue?.DeepClone(),
            AttemptedValue = baselineValue?.DeepClone(),
            ImmediateValue = baselineValue?.DeepClone(),
            Behavior = GroupedApplyBehavior.ImmediateApplied,
            Classification = ForcedFieldClassification.Writable,
            BaselineFieldPresent = baselineValue is not null,
            DefinitionSource = "pipeline-ownership",
            Notes = "Previously proven live on the grouped image-input family."
        });

        return new PipelineOwnershipFieldResult
        {
            FieldKey = fieldKey,
            DisplayName = displayName,
            Pipeline = pipeline,
            RequestedEndpoint = endpoint,
            EffectiveEndpoint = endpoint,
            SourcePath = sourcePath,
            Classification = OwnershipWriteClassification.Writable,
            BaselineValue = baselineValue?.DeepClone(),
            ResultValue = baselineValue?.DeepClone(),
            Notes = "Previously proven live on the grouped image-input family."
        };
    }

    private async Task<(PipelineOwnershipFieldResult Field, GroupedUnsupportedRetestResult GroupedResult)> ProbeHueOwnershipAsync(
        DeviceIdentity device,
        string firmware,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        const string groupedEndpoint = "/NetSDK/Video/input/channel/1";
        const string scalarEndpoint = "/NetSDK/Video/input/channel/1/hueLevel";
        var root = await ReadEndpointObjectDirectAsync(device.Id, groupedEndpoint, cancellationToken);
        var baseline = TryGetPathValue(root, "$.hueLevel");
        var candidate = JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 50) == 60 ? 50 : 60, 0, 100));
        var groupedValue = baseline?.DeepClone();
        var scalarValue = baseline?.DeepClone();
        var groupedMessage = string.Empty;
        var scalarMessage = string.Empty;

        if (root is not null)
        {
            var groupedPayload = (JsonObject)root.DeepClone();
            SetPathValue(groupedPayload, "$.hueLevel", candidate);
            var groupedWrite = await ExecutePlanAsync(device.Id, groupedEndpoint, "PUT", groupedPayload, expertOverride, cancellationToken);
            groupedMessage = groupedWrite?.Message ?? string.Empty;
            groupedValue = await ReadEndpointValueDirectAsync(device.Id, scalarEndpoint, "$", cancellationToken);
            _ = await ExecutePlanAsync(device.Id, groupedEndpoint, "PUT", (JsonObject)root.DeepClone(), expertOverride: true, cancellationToken);
        }

        var scalarRead = await ExecutePlanAsync(device.Id, scalarEndpoint, "GET", null, expertOverride: true, cancellationToken);
        var scalarAvailable = scalarRead?.Success == true;
        if (scalarAvailable)
        {
            var scalarWrite = await ExecutePlanAsync(
                device.Id,
                scalarEndpoint,
                "PUT",
                new JsonObject { ["hueLevel"] = candidate.DeepClone() },
                expertOverride,
                cancellationToken);
            scalarMessage = scalarWrite?.Message ?? string.Empty;
            scalarValue = await ReadEndpointValueDirectAsync(device.Id, scalarEndpoint, "$", cancellationToken);
            _ = await ExecutePlanAsync(
                device.Id,
                scalarEndpoint,
                "PUT",
                new JsonObject { ["hueLevel"] = baseline?.DeepClone() ?? JsonValue.Create(50) },
                expertOverride: true,
                cancellationToken);
        }

        var classification = scalarAvailable && ValuesEquivalent(scalarValue, candidate)
            ? OwnershipWriteClassification.WritableDifferentEndpoint
            : groupedValue is not null && ValuesEquivalent(groupedValue, candidate)
                ? OwnershipWriteClassification.Writable
                : OwnershipWriteClassification.ReadableOnly;
        var effectiveEndpoint = classification == OwnershipWriteClassification.WritableDifferentEndpoint ? scalarEndpoint : groupedEndpoint;
        var effectiveValue = classification == OwnershipWriteClassification.WritableDifferentEndpoint ? scalarValue : groupedValue;
        var notes = classification switch
        {
            OwnershipWriteClassification.WritableDifferentEndpoint => $"owner={scalarEndpoint}; groupedEndpointAcceptedButReadbackStayed={groupedValue?.ToJsonString() ?? "null"}; scalarMessage={scalarMessage}",
            OwnershipWriteClassification.Writable => $"owner={groupedEndpoint}; groupedMessage={groupedMessage}",
            _ => $"groupedMessage={groupedMessage}; scalarAvailable={scalarAvailable}; scalarMessage={scalarMessage}"
        };

        return
        (
            new PipelineOwnershipFieldResult
            {
                FieldKey = "hue",
                DisplayName = "Hue",
                Pipeline = FieldPipelineGroup.TransformDisplay,
                RequestedEndpoint = groupedEndpoint,
                AlternateEndpoint = scalarEndpoint,
                EffectiveEndpoint = effectiveEndpoint,
                SourcePath = classification == OwnershipWriteClassification.WritableDifferentEndpoint ? "$" : "$.hueLevel",
                Classification = classification,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ResultValue = effectiveValue?.DeepClone(),
                AlternateEndpointAvailable = scalarAvailable,
                Notes = notes
            },
            new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = GroupedConfigKind.ImageConfig,
                ContractKey = "pipeline.hue",
                FieldKey = "hue",
                SourceEndpoint = effectiveEndpoint,
                SourcePath = classification == OwnershipWriteClassification.WritableDifferentEndpoint ? "$" : "$.hueLevel",
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ImmediateValue = effectiveValue?.DeepClone(),
                FirstWriteSucceeded = classification is OwnershipWriteClassification.Writable or OwnershipWriteClassification.WritableDifferentEndpoint,
                Behavior = classification is OwnershipWriteClassification.Writable or OwnershipWriteClassification.WritableDifferentEndpoint
                    ? GroupedApplyBehavior.ImmediateApplied
                    : GroupedApplyBehavior.Unapplied,
                Classification = classification is OwnershipWriteClassification.Writable or OwnershipWriteClassification.WritableDifferentEndpoint
                    ? ForcedFieldClassification.Writable
                    : ForcedFieldClassification.ReadableOnly,
                BaselineFieldPresent = baseline is not null,
                DefinitionSource = "pipeline-ownership",
                Notes = notes
            }
        );
    }

    private async Task<(PipelineOwnershipFieldResult Field, GroupedUnsupportedRetestResult GroupedResult)> ProbeVideoInputBooleanAsync(
        DeviceIdentity device,
        string firmware,
        string fieldKey,
        string displayName,
        string sourcePath,
        string groupedEndpoint,
        string leafEndpoint,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        var root = await ReadEndpointObjectDirectAsync(device.Id, groupedEndpoint, cancellationToken);
        var baseline = TryGetPathValue(root, sourcePath);
        var candidate = JsonValue.Create(!ParseBool(baseline));
        var resultValue = baseline?.DeepClone();
        var writeMessage = string.Empty;

        if (root is not null)
        {
            var payload = (JsonObject)root.DeepClone();
            SetPathValue(payload, sourcePath, candidate);
            var write = await ExecutePlanAsync(device.Id, groupedEndpoint, "PUT", payload, expertOverride, cancellationToken);
            writeMessage = write?.Message ?? string.Empty;
            resultValue = await ReadEndpointValueDirectAsync(device.Id, groupedEndpoint, sourcePath, cancellationToken);
            _ = await ExecutePlanAsync(device.Id, groupedEndpoint, "PUT", (JsonObject)root.DeepClone(), expertOverride: true, cancellationToken);
        }

        var leafRead = await ExecutePlanAsync(device.Id, leafEndpoint, "GET", null, expertOverride: true, cancellationToken);
        var leafAvailable = leafRead?.Success == true;
        var classification = ValuesEquivalent(resultValue, candidate)
            ? OwnershipWriteClassification.Writable
            : OwnershipWriteClassification.ReadableOnly;
        var notes = classification == OwnershipWriteClassification.Writable
            ? $"owner={groupedEndpoint}; leafAvailable={leafAvailable}; groupedMessage={writeMessage}"
            : $"groupedMessage={writeMessage}; leafAvailable={leafAvailable}";

        return
        (
            new PipelineOwnershipFieldResult
            {
                FieldKey = fieldKey,
                DisplayName = displayName,
                Pipeline = FieldPipelineGroup.TransformDisplay,
                RequestedEndpoint = groupedEndpoint,
                AlternateEndpoint = leafEndpoint,
                EffectiveEndpoint = groupedEndpoint,
                SourcePath = sourcePath,
                Classification = classification,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ResultValue = resultValue?.DeepClone(),
                AlternateEndpointAvailable = leafAvailable,
                Notes = notes
            },
            new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = GroupedConfigKind.ImageConfig,
                ContractKey = $"pipeline.{fieldKey}",
                FieldKey = fieldKey,
                SourceEndpoint = groupedEndpoint,
                SourcePath = sourcePath,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ImmediateValue = resultValue?.DeepClone(),
                FirstWriteSucceeded = classification == OwnershipWriteClassification.Writable,
                Behavior = classification == OwnershipWriteClassification.Writable ? GroupedApplyBehavior.ImmediateApplied : GroupedApplyBehavior.Unapplied,
                Classification = classification == OwnershipWriteClassification.Writable ? ForcedFieldClassification.Writable : ForcedFieldClassification.ReadableOnly,
                BaselineFieldPresent = baseline is not null,
                DefinitionSource = "pipeline-ownership",
                Notes = notes
            }
        );
    }

    private async Task<(PipelineOwnershipFieldResult Field, GroupedUnsupportedRetestResult GroupedResult)> ProbeFullObjectFieldAsync(
        DeviceIdentity device,
        string firmware,
        string fieldKey,
        string displayName,
        FieldPipelineGroup pipeline,
        string endpoint,
        string sourcePath,
        Func<JsonNode?, JsonNode?> candidateFactory,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        var root = await ReadEndpointObjectDirectAsync(device.Id, endpoint, cancellationToken);
        var baseline = TryGetPathValue(root, sourcePath);
        var candidate = candidateFactory(baseline);
        if (root is null || candidate is null)
        {
            var notes = root is null ? "Endpoint was not readable." : "No alternate candidate value was available.";
            return
            (
                new PipelineOwnershipFieldResult
                {
                    FieldKey = fieldKey,
                    DisplayName = displayName,
                    Pipeline = pipeline,
                    RequestedEndpoint = endpoint,
                    EffectiveEndpoint = endpoint,
                    SourcePath = sourcePath,
                    Classification = OwnershipWriteClassification.ReadableOnly,
                    BaselineValue = baseline?.DeepClone(),
                    Notes = notes
                },
                new GroupedUnsupportedRetestResult
                {
                    DeviceId = device.Id,
                    FirmwareFingerprint = firmware,
                    IpAddress = device.IpAddress ?? string.Empty,
                    GroupKind = GroupedConfigKind.ImageConfig,
                    ContractKey = $"pipeline.{fieldKey}",
                    FieldKey = fieldKey,
                    SourceEndpoint = endpoint,
                    SourcePath = sourcePath,
                    BaselineValue = baseline?.DeepClone(),
                    Behavior = GroupedApplyBehavior.Unapplied,
                    Classification = ForcedFieldClassification.ReadableOnly,
                    BaselineFieldPresent = baseline is not null,
                    DefinitionSource = "pipeline-ownership",
                    Notes = notes
                }
            );
        }

        var payload = (JsonObject)root.DeepClone();
        SetPathValue(payload, sourcePath, candidate);
        var write = await ExecutePlanAsync(device.Id, endpoint, "PUT", payload, expertOverride, cancellationToken);
        var resultValue = await ReadEndpointValueDirectAsync(device.Id, endpoint, sourcePath, cancellationToken);
        _ = await ExecutePlanAsync(device.Id, endpoint, "PUT", (JsonObject)root.DeepClone(), expertOverride: true, cancellationToken);

        var writable = ValuesEquivalent(resultValue, candidate);
        var notesText = writable
            ? $"owner={endpoint}; writeMessage={write?.Message ?? string.Empty}"
            : $"writeAccepted={write?.Success == true}; writeMessage={write?.Message ?? string.Empty}";

        return
        (
            new PipelineOwnershipFieldResult
            {
                FieldKey = fieldKey,
                DisplayName = displayName,
                Pipeline = pipeline,
                RequestedEndpoint = endpoint,
                EffectiveEndpoint = endpoint,
                SourcePath = sourcePath,
                Classification = writable ? OwnershipWriteClassification.Writable : OwnershipWriteClassification.ReadableOnly,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ResultValue = resultValue?.DeepClone(),
                Notes = notesText
            },
            new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = GroupedConfigKind.ImageConfig,
                ContractKey = $"pipeline.{fieldKey}",
                FieldKey = fieldKey,
                SourceEndpoint = endpoint,
                SourcePath = sourcePath,
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ImmediateValue = resultValue?.DeepClone(),
                FirstWriteSucceeded = write?.Success == true,
                Behavior = writable ? GroupedApplyBehavior.ImmediateApplied : GroupedApplyBehavior.Unapplied,
                Classification = writable ? ForcedFieldClassification.Writable : ForcedFieldClassification.ReadableOnly,
                BaselineFieldPresent = baseline is not null,
                DefinitionSource = "pipeline-ownership",
                Notes = notesText
            }
        );
    }

    private async Task<(EncodeFullObjectProbeResult Report, GroupedUnsupportedRetestResult? GroupedResult)> ProbeEncodeFullObjectAsync(
        DeviceIdentity device,
        string firmware,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        const string endpoint = "/NetSDK/Video/encode/channel/101/properties";
        var root = await ReadEndpointObjectDirectAsync(device.Id, endpoint, cancellationToken);
        if (root is null)
        {
            return
            (
                new EncodeFullObjectProbeResult
                {
                    Endpoint = endpoint,
                    Classification = OwnershipWriteClassification.ReadableOnly,
                    Notes = "Encode endpoint was not readable."
                },
                null
            );
        }

        var baseline = TryGetPathValue(root, "$.frameRate");
        var candidate = JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 13) >= 25 ? 24 : (TryToDecimal(baseline) ?? 13) + 1, 5, 25));
        var payload = (JsonObject)root.DeepClone();
        SetPathValue(payload, "$.frameRate", candidate);
        var write = await ExecutePlanAsync(device.Id, endpoint, "PUT", payload, expertOverride, cancellationToken);
        var resultValue = await ReadEndpointValueDirectAsync(device.Id, endpoint, "$.frameRate", cancellationToken);
        if (ValuesEquivalent(resultValue, candidate))
        {
            _ = await ExecutePlanAsync(device.Id, endpoint, "PUT", (JsonObject)root.DeepClone(), expertOverride: true, cancellationToken);
        }

        var writable = ValuesEquivalent(resultValue, candidate);
        var notes = write?.Message ?? string.Empty;
        return
        (
            new EncodeFullObjectProbeResult
            {
                Endpoint = endpoint,
                BaselinePayload = (JsonObject)root.DeepClone(),
                AttemptedPayload = payload,
                ResultValue = resultValue?.DeepClone(),
                WriteAccepted = write?.Success == true,
                Classification = writable ? OwnershipWriteClassification.Writable : OwnershipWriteClassification.ReadableOnly,
                Notes = notes
            },
            new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = GroupedConfigKind.VideoEncodeConfig,
                ContractKey = "pipeline.frameRate",
                FieldKey = "frameRate",
                SourceEndpoint = endpoint,
                SourcePath = "$.frameRate",
                BaselineValue = baseline?.DeepClone(),
                AttemptedValue = candidate.DeepClone(),
                ImmediateValue = resultValue?.DeepClone(),
                FirstWriteSucceeded = write?.Success == true,
                Behavior = writable ? GroupedApplyBehavior.ImmediateApplied : GroupedApplyBehavior.Unapplied,
                Classification = writable ? ForcedFieldClassification.Writable : ForcedFieldClassification.ReadableOnly,
                BaselineFieldPresent = baseline is not null,
                DefinitionSource = "pipeline-ownership",
                Notes = $"full-object-put:{notes}"
            }
        );
    }

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

            var first = await ApplyGroupedFieldAsync(deviceId, def.GroupKind, endpointMatch.Value.SourceEndpoint, def.SourcePath, candidate, request.ExpertOverride, cancellationToken);
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
                var secondWrite = await ApplyGroupedFieldAsync(deviceId, def.GroupKind, endpointMatch.Value.SourceEndpoint, def.SourcePath, candidate, request.ExpertOverride, cancellationToken);
                secondary = secondWrite?.Success == true;
                var secondImmediate = await ReadFieldValueAsync(deviceId, endpointMatch.Value.SourceEndpoint, def.SourcePath, cancellationToken);
                if (JsonNode.DeepEquals(secondImmediate, candidate))
                {
                    behavior = GroupedApplyBehavior.RequiresSecondWrite;
                }
                else
                {
                    var thirdWrite = await ApplyGroupedFieldAsync(deviceId, def.GroupKind, endpointMatch.Value.SourceEndpoint, def.SourcePath, candidate, request.ExpertOverride, cancellationToken);
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
                _ = await ApplyGroupedFieldAsync(deviceId, def.GroupKind, endpointMatch.Value.SourceEndpoint, def.SourcePath, baseline!.DeepClone(), expertOverride: true, cancellationToken);
            }
        }

        await store.SaveGroupedRetestResultsAsync(results, cancellationToken);
        await SaveGroupedProfilesAsync(device, normalizedFields, results, cancellationToken);
        await PromoteRetestedFieldsAsync(deviceId, results, cancellationToken);
        await PromoteImageInventoryAsync(deviceId, results, cancellationToken);
        logger.LogInformation("SDK forced enumeration completed for {Device} fields={Count}", device.DisplayName, results.Count);
        return results;
    }

    private async Task<IReadOnlyCollection<GroupedUnsupportedRetestResult>> ProbeFamilyAsync(
        DeviceIdentity device,
        FamilyProbeDefinition family,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        var results = new List<GroupedUnsupportedRetestResult>();
        foreach (var field in family.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ProbeFamilyFieldAsync(device, family, field, expertOverride, cancellationToken));
        }

        return results;
    }

    private async Task<GroupedUnsupportedRetestResult> ProbeFamilyFieldAsync(
        DeviceIdentity device,
        FamilyProbeDefinition family,
        FamilyFieldProbe field,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        var root = await ReadEndpointObjectAsync(device.Id, family.Endpoint, cancellationToken);
        var firmware = BuildFirmwareFingerprint(device, await store.GetNormalizedSettingFieldsAsync(device.Id, cancellationToken));
        if (root is null)
        {
            return new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = family.GroupKind,
                ContractKey = $"family.{family.Name}.{field.FieldKey}",
                FieldKey = field.FieldKey,
                SourceEndpoint = family.Endpoint,
                SourcePath = field.SourcePath,
                Behavior = GroupedApplyBehavior.Uncertain,
                Classification = ForcedFieldClassification.Uncertain,
                Notes = "Family endpoint payload was not readable."
            };
        }

        var baseline = TryGetPathValue(root, field.SourcePath);
        var candidateValue = field.CandidateFactory(root, baseline);
        if (candidateValue is null)
        {
            return new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = firmware,
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = family.GroupKind,
                ContractKey = $"family.{family.Name}.{field.FieldKey}",
                FieldKey = field.FieldKey,
                SourceEndpoint = family.Endpoint,
                SourcePath = field.SourcePath,
                BaselineValue = baseline?.DeepClone(),
                Behavior = GroupedApplyBehavior.Uncertain,
                Classification = ForcedFieldClassification.Uncertain,
                BaselineFieldPresent = baseline is not null,
                Notes = "No candidate mutation could be built from the current grouped payload."
            };
        }

        var payload = (JsonObject)root.DeepClone();
        SetPathValue(payload, field.SourcePath, candidateValue.DeepClone());

        var first = await ExecutePlanAsync(device.Id, family.Endpoint, "PUT", payload, expertOverride, cancellationToken);
        var immediate = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var delayed1 = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var delayed3 = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var delayed5 = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);

        var behavior = DetermineBehavior(candidateValue, immediate, delayed1, delayed3, delayed5, first?.Success == true);
        var classification = DetermineClassification(behavior, first?.Success == true, baseline is not null);
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(first?.Message))
        {
            notes.Add($"write:{first!.Message}");
        }

        var secondaryWriteSucceeded = false;
        var resendWriteSucceeded = false;
        if (behavior == GroupedApplyBehavior.Uncertain && first?.Success == true)
        {
            var second = await ExecutePlanAsync(device.Id, family.Endpoint, "PUT", payload, expertOverride, cancellationToken);
            secondaryWriteSucceeded = second?.Success == true;
            var secondRead = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
            if (JsonNode.DeepEquals(secondRead, candidateValue))
            {
                behavior = GroupedApplyBehavior.RequiresSecondWrite;
                classification = ForcedFieldClassification.WritableNeedsCommitTrigger;
                notes.Add("mechanism:second-write");
            }
            else if (field.RelatedFieldPayloadFactory is not null)
            {
                var relatedPayload = field.RelatedFieldPayloadFactory(payload);
                if (relatedPayload is not null)
                {
                    var related = await ExecutePlanAsync(device.Id, family.Endpoint, "PUT", relatedPayload, expertOverride, cancellationToken);
                    resendWriteSucceeded = related?.Success == true;
                    var relatedRead = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
                    if (JsonNode.DeepEquals(relatedRead, candidateValue))
                    {
                        behavior = GroupedApplyBehavior.RequiresRelatedFieldWrite;
                        classification = ForcedFieldClassification.WritableNeedsCommitTrigger;
                        notes.Add("mechanism:related-field-write");
                    }
                }
            }

            if (behavior == GroupedApplyBehavior.Uncertain && !string.IsNullOrWhiteSpace(family.CommitTriggerEndpoint))
            {
                var trigger = await ExecutePlanAsync(
                    device.Id,
                    family.CommitTriggerEndpoint!,
                    family.CommitTriggerMethod ?? "POST",
                    family.CommitTriggerPayload,
                    expertOverride: true,
                    cancellationToken);
                var afterTrigger = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
                if (JsonNode.DeepEquals(afterTrigger, candidateValue))
                {
                    behavior = GroupedApplyBehavior.RequiresCommitTrigger;
                    classification = ForcedFieldClassification.WritableNeedsCommitTrigger;
                    notes.Add($"mechanism:commit-trigger:{family.CommitTriggerEndpoint}");
                }
                else if (!string.IsNullOrWhiteSpace(trigger?.Message))
                {
                    notes.Add($"commit-trigger:{trigger!.Message}");
                }
            }

            if (behavior == GroupedApplyBehavior.Uncertain && !string.IsNullOrWhiteSpace(family.ReadTriggerEndpoint))
            {
                var readTrigger = await ExecutePlanAsync(device.Id, family.ReadTriggerEndpoint!, "GET", null, expertOverride: true, cancellationToken);
                var afterReadTrigger = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
                if (JsonNode.DeepEquals(afterReadTrigger, candidateValue))
                {
                    behavior = GroupedApplyBehavior.RequiresCommitTrigger;
                    classification = ForcedFieldClassification.WritableNeedsCommitTrigger;
                    notes.Add($"mechanism:read-trigger:{family.ReadTriggerEndpoint}");
                }
                else if (!string.IsNullOrWhiteSpace(readTrigger?.Message))
                {
                    notes.Add($"read-trigger:{readTrigger!.Message}");
                }
            }

            if (behavior == GroupedApplyBehavior.Uncertain && !string.IsNullOrWhiteSpace(family.SecondaryApplyEndpoint))
            {
                var secondary = await ExecutePlanAsync(device.Id, family.SecondaryApplyEndpoint!, "PUT", payload, expertOverride: true, cancellationToken);
                var afterSecondary = await ReadFieldValueAsync(device.Id, family.Endpoint, field.SourcePath, cancellationToken);
                if (JsonNode.DeepEquals(afterSecondary, candidateValue))
                {
                    behavior = GroupedApplyBehavior.RequiresCommitTrigger;
                    classification = ForcedFieldClassification.WritableNeedsCommitTrigger;
                    notes.Add($"mechanism:secondary-apply:{family.SecondaryApplyEndpoint}");
                }
                else if (!string.IsNullOrWhiteSpace(secondary?.Message))
                {
                    notes.Add($"secondary-apply:{secondary!.Message}");
                }
            }

            if (behavior == GroupedApplyBehavior.Uncertain)
            {
                behavior = first.Success ? GroupedApplyBehavior.StoredButNotOperational : GroupedApplyBehavior.Uncertain;
                classification = first.Success ? ForcedFieldClassification.Uncertain : (baseline is null ? ForcedFieldClassification.Unsupported : ForcedFieldClassification.ReadableOnly);
                notes.Add(first.Success ? "mechanism:none-of-tested-triggers" : "write-not-accepted");
            }
        }

        if (baseline is not null)
        {
            var rollbackPayload = (JsonObject)root.DeepClone();
            _ = await ExecutePlanAsync(device.Id, family.Endpoint, "PUT", rollbackPayload, expertOverride: true, cancellationToken);
        }

        return new GroupedUnsupportedRetestResult
        {
            DeviceId = device.Id,
            FirmwareFingerprint = firmware,
            IpAddress = device.IpAddress ?? string.Empty,
            GroupKind = family.GroupKind,
            ContractKey = $"family.{family.Name}.{field.FieldKey}",
            FieldKey = field.FieldKey,
            SourceEndpoint = family.Endpoint,
            SourcePath = field.SourcePath,
            BaselineValue = baseline?.DeepClone(),
            AttemptedValue = candidateValue.DeepClone(),
            ImmediateValue = immediate?.DeepClone(),
            Delayed1sValue = delayed1?.DeepClone(),
            Delayed3sValue = delayed3?.DeepClone(),
            Delayed5sValue = delayed5?.DeepClone(),
            FirstWriteSucceeded = first?.Success == true,
            SecondaryWriteSucceeded = secondaryWriteSucceeded,
            ResendWriteSucceeded = resendWriteSucceeded,
            Behavior = behavior,
            Classification = classification,
            BaselineFieldPresent = baseline is not null,
            DefinitionSource = family.Name,
            Notes = string.Join(" | ", notes)
        };
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
            var groupKind = ResolveGroupKind(field.FieldKey);
            var first = await ApplyGroupedFieldAsync(deviceId, groupKind, field.SourceEndpoint, contractField.SourcePath, candidate, request.ExpertOverride, cancellationToken);
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
                var secondWrite = await ApplyGroupedFieldAsync(deviceId, groupKind, field.SourceEndpoint, contractField.SourcePath, candidate, request.ExpertOverride, cancellationToken);
                secondary = secondWrite?.Success == true;
                immediate = await ReadFieldValueAsync(deviceId, field.SourceEndpoint, contractField.SourcePath, cancellationToken);
                behavior = JsonNode.DeepEquals(immediate, candidate)
                    ? GroupedApplyBehavior.RequiresSecondWrite
                    : behavior;

                if (behavior is GroupedApplyBehavior.RequiresCommitTrigger or GroupedApplyBehavior.Unapplied)
                {
                    var thirdWrite = await ApplyGroupedFieldAsync(deviceId, groupKind, field.SourceEndpoint, contractField.SourcePath, candidate, request.ExpertOverride, cancellationToken);
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
                GroupKind = groupKind,
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

    private async Task<JsonObject?> ReadEndpointObjectAsync(Guid deviceId, string endpoint, CancellationToken cancellationToken)
    {
        var snapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
        var endpointValue = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(item => NormalizeEndpoint(item.SourceEndpoint ?? item.Key).Equals(NormalizeEndpoint(endpoint), StringComparison.OrdinalIgnoreCase));
        return endpointValue?.Value as JsonObject;
    }

    private async Task<JsonObject?> ReadEndpointObjectDirectAsync(Guid deviceId, string endpoint, CancellationToken cancellationToken)
        => await ReadEndpointNodeDirectAsync(deviceId, endpoint, cancellationToken) as JsonObject;

    private async Task<JsonNode?> ReadEndpointNodeDirectAsync(Guid deviceId, string endpoint, CancellationToken cancellationToken)
    {
        var read = await ExecutePlanAsync(deviceId, endpoint, "GET", null, expertOverride: true, cancellationToken);
        return read?.Response?.DeepClone();
    }

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

    private async Task<WriteResult?> ExecutePlanAsync(
        Guid deviceId,
        string endpoint,
        string method,
        JsonObject? payload,
        bool expertOverride,
        CancellationToken cancellationToken)
        => await settingsService.WriteAsync(
            deviceId,
            new WritePlan
            {
                GroupName = "Grouped Family Probe",
                Endpoint = endpoint,
                Method = method,
                Payload = payload,
                SnapshotBeforeWrite = true,
                RequireWriteVerification = !expertOverride,
                AllowRollback = false
            },
            cancellationToken);

    private async Task<WriteResult?> ApplyGroupedFieldAsync(
        Guid deviceId,
        GroupedConfigKind groupKind,
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

        WriteResult? last = null;
        foreach (var endpointCandidate in BuildEndpointCandidates(groupKind, endpoint))
        {
            foreach (var payload in BuildPayloadCandidates(groupKind, root, sourcePath, value))
            {
                var plan = new WritePlan
                {
                    GroupName = "SDK Forced Enumeration",
                    Endpoint = endpointCandidate,
                    Method = "PUT",
                    AdapterName = null,
                    Payload = payload,
                    SnapshotBeforeWrite = true,
                    RequireWriteVerification = !expertOverride,
                    AllowRollback = false
                };
                var result = await settingsService.WriteAsync(deviceId, plan, cancellationToken);
                if (result is null)
                {
                    continue;
                }

                last = result;
                if (result.Success)
                {
                    return result;
                }
            }
        }

        return last;
    }

    private async Task<JsonNode?> ReadFieldValueAsync(Guid deviceId, string endpoint, string sourcePath, CancellationToken cancellationToken)
    {
        var snapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
        var endpointValue = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(item => NormalizeEndpoint(item.SourceEndpoint ?? item.Key).Equals(NormalizeEndpoint(endpoint), StringComparison.OrdinalIgnoreCase));
        return TryGetPathValue(endpointValue?.Value, sourcePath);
    }

    private async Task<JsonNode?> ReadEndpointValueDirectAsync(Guid deviceId, string endpoint, string sourcePath, CancellationToken cancellationToken)
    {
        var node = await ReadEndpointNodeDirectAsync(deviceId, endpoint, cancellationToken);
        return TryGetPathValue(node, sourcePath);
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

    private static GroupedApplyBehavior DetermineBehavior(
        JsonNode intended,
        JsonNode? immediate,
        JsonNode? delayed1,
        JsonNode? delayed3,
        JsonNode? delayed5,
        bool writeAccepted)
    {
        if (JsonNode.DeepEquals(immediate, intended))
        {
            return GroupedApplyBehavior.ImmediateApplied;
        }

        if (JsonNode.DeepEquals(delayed1, intended) || JsonNode.DeepEquals(delayed3, intended) || JsonNode.DeepEquals(delayed5, intended))
        {
            return GroupedApplyBehavior.DelayedApplied;
        }

        return writeAccepted ? GroupedApplyBehavior.Uncertain : GroupedApplyBehavior.Unapplied;
    }

    private static ForcedFieldClassification DetermineClassification(GroupedApplyBehavior behavior, bool writeAccepted, bool baselinePresent)
        => behavior switch
        {
            GroupedApplyBehavior.ImmediateApplied => ForcedFieldClassification.Writable,
            GroupedApplyBehavior.DelayedApplied => ForcedFieldClassification.Writable,
            GroupedApplyBehavior.RequiresSecondWrite => ForcedFieldClassification.WritableNeedsCommitTrigger,
            GroupedApplyBehavior.RequiresRelatedFieldWrite => ForcedFieldClassification.WritableNeedsCommitTrigger,
            GroupedApplyBehavior.RequiresCommitTrigger => ForcedFieldClassification.WritableNeedsCommitTrigger,
            GroupedApplyBehavior.StoredButNotOperational => ForcedFieldClassification.Uncertain,
            GroupedApplyBehavior.Uncertain => writeAccepted ? ForcedFieldClassification.Uncertain : (baselinePresent ? ForcedFieldClassification.ReadableOnly : ForcedFieldClassification.Unsupported),
            GroupedApplyBehavior.Unapplied => baselinePresent ? ForcedFieldClassification.ReadableOnly : ForcedFieldClassification.Unsupported,
            _ => ForcedFieldClassification.Uncertain
        };

    private static FamilyProbeDefinition BuildVideoInputFamily(bool includePrivacyMasks)
    {
        var fields = new List<FamilyFieldProbe>
        {
            new("brightness", "$.brightnessLevel", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 60) + 1, 0, 100)), static payload => BuildRelatedVideoInputPayload(payload, "contrastLevel")),
            new("contrast", "$.contrastLevel", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 50) + 1, 0, 100)), static payload => BuildRelatedVideoInputPayload(payload, "brightnessLevel")),
            new("saturation", "$.saturationLevel", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 50) + 1, 0, 100)), static payload => BuildRelatedVideoInputPayload(payload, "brightnessLevel")),
            new("sharpness", "$.sharpnessLevel", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 50) + 1, 0, 100)), static payload => BuildRelatedVideoInputPayload(payload, "contrastLevel")),
            new("hue", "$.hueLevel", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 50) + 1, 0, 100)), static payload => BuildRelatedVideoInputPayload(payload, "brightnessLevel")),
            new("flip", "$.flipEnabled", static (_, baseline) => JsonValue.Create(!ParseBool(baseline)), static payload => BuildRelatedVideoInputPayload(payload, "mirrorEnabled")),
            new("mirror", "$.mirrorEnabled", static (_, baseline) => JsonValue.Create(!ParseBool(baseline)), static payload => BuildRelatedVideoInputPayload(payload, "flipEnabled"))
        };
        if (includePrivacyMasks)
        {
            fields.Add(new FamilyFieldProbe("privacyMask1Enabled", "$.privacyMask[0].enabled", static (_, baseline) => JsonValue.Create(!ParseBool(baseline))));
            fields.Add(new FamilyFieldProbe("privacyMask1Width", "$.privacyMask[0].regionWidth", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 0) == 0 ? 8 : 0, 0, 100))));
        }

        return new FamilyProbeDefinition(
            "video-input-channel-1",
            "/NetSDK/Video/input/channel/1",
            GroupedConfigKind.ImageConfig,
            fields,
            CommitTriggerEndpoint: "/NetSDK/Video/encode/channel/101/requestKeyFrame",
            CommitTriggerMethod: "POST",
            CommitTriggerPayload: new JsonObject { ["requestKeyFrame"] = true },
            ReadTriggerEndpoint: "/NetSDK/Video/encode/channel/101/snapShot");
    }

    private static IReadOnlyCollection<FamilyProbeDefinition> BuildProbeFamilies(bool includePrivacyMasks)
        =>
        [
            BuildVideoInputFamily(includePrivacyMasks),
            BuildVideoEncodeFamily(),
            BuildImageFamily(),
            BuildIrCutFamily()
        ];

    private static FamilyProbeDefinition BuildVideoEncodeFamily()
        => new(
            "video-encode-channel-101",
            "/NetSDK/Video/encode/channel/101/properties",
            GroupedConfigKind.VideoEncodeConfig,
            [
                new("codec", "$.codecType", static (root, baseline) => NextEnumCandidate(root, "$.codecTypeProperty.opt", baseline)),
                new("profile", "$.h264Profile", static (root, baseline) => NextEnumCandidate(root, "$.h264ProfileProperty.opt", baseline)),
                new("resolution", "$.resolution", static (root, baseline) => NextEnumCandidate(root, "$.resolutionProperty.opt", baseline)),
                new("bitrateMode", "$.bitRateControlType", static (root, baseline) => NextEnumCandidate(root, "$.bitRateControlTypeProperty.opt", baseline)),
                new("bitrate", "$.constantBitRate", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 512) - 128, 128, 5120))),
                new("frameRate", "$.frameRate", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 15) + 1, 5, 25))),
                new("keyframeInterval", "$.keyFrameInterval", static (_, baseline) => JsonValue.Create((int)Clamp((TryToDecimal(baseline) ?? 30) + 30, 30, 300)))
            ],
            CommitTriggerEndpoint: "/NetSDK/Video/encode/channel/101/requestKeyFrame",
            CommitTriggerMethod: "POST",
            CommitTriggerPayload: new JsonObject { ["requestKeyFrame"] = true },
            ReadTriggerEndpoint: "/NetSDK/Video/encode/channel/101/snapShot");

    private static FamilyProbeDefinition BuildImageFamily()
        => new(
            "image-root",
            "/NetSDK/Image",
            GroupedConfigKind.ImageConfig,
            [
                new("sceneMode", "$.sceneMode", static (_, baseline) => NextEnumValue(baseline, ["auto", "indoor", "outdoor"])),
                new("exposureMode", "$.exposureMode", static (_, baseline) => NextEnumValue(baseline, ["auto", "bright", "dark"])),
                new("awbMode", "$.awbMode", static (_, baseline) => NextEnumValue(baseline, ["auto", "indoor", "outdoor"])),
                new("lowlightMode", "$.lowlightMode", static (_, baseline) => NextEnumValue(baseline, ["close", "only night", "day-night", "auto"]))
            ],
            ReadTriggerEndpoint: "/NetSDK/Image");

    private static FamilyProbeDefinition BuildIrCutFamily()
        => new(
            "image-ircut",
            "/NetSDK/Image/irCutFilter",
            GroupedConfigKind.ImageConfig,
            [
                new("irCutControlMode", "$.irCutControlMode", static (_, baseline) => NextEnumValue(baseline, ["hardware", "software"])),
                new("irCutMode", "$.irCutMode", static (_, baseline) => NextEnumValue(baseline, ["auto", "daylight", "night"]))
            ],
            ReadTriggerEndpoint: "/NetSDK/Image/irCutFilter");

    private static JsonObject? BuildRelatedVideoInputPayload(JsonObject payload, string relatedPath)
    {
        var clone = (JsonObject)payload.DeepClone();
        var baseline = TryGetPathValue(clone, "$." + relatedPath);
        if (relatedPath.EndsWith("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            SetPathValue(clone, "$." + relatedPath, JsonValue.Create(!ParseBool(baseline)));
            return clone;
        }

        var number = TryToDecimal(baseline) ?? 50;
        SetPathValue(clone, "$." + relatedPath, JsonValue.Create((int)Clamp(number + 1, 0, 100)));
        return clone;
    }

    private static JsonNode? NextEnumCandidate(JsonObject root, string optionPath, JsonNode? baseline)
    {
        var options = TryGetPathValue(root, optionPath) as JsonArray;
        var values = options?
            .Select(static item => item?.ToJsonString().Trim('"'))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray()
            ?? [];
        return NextEnumValue(baseline, values);
    }

    private static JsonNode? NextEnumValue(JsonNode? baseline, IReadOnlyCollection<string> options)
    {
        var current = baseline?.ToJsonString().Trim('"');
        var next = options.FirstOrDefault(value => !string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(next) ? null : JsonValue.Create(next);
    }

    private static IReadOnlyCollection<string> BuildEndpointCandidates(GroupedConfigKind groupKind, string endpoint)
    {
        var normalized = NormalizeEndpoint(endpoint);
        var candidates = new List<string> { normalized };
        static void Add(List<string> list, string value)
        {
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
        }

        switch (groupKind)
        {
            case GroupedConfigKind.ImageConfig:
                Add(candidates, "/NetSDK/Image");
                Add(candidates, "/NetSDK/Image/0");
                Add(candidates, "/NetSDK/Video/input/channel/0");
                Add(candidates, "/NetSDK/Video/input/channel/1");
                break;
            case GroupedConfigKind.VideoEncodeConfig:
                Add(candidates, "/NetSDK/Video/encode/channel/0");
                Add(candidates, "/NetSDK/Video/encode/channel/101");
                Add(candidates, "/NetSDK/Video/encode/channel/101/properties");
                Add(candidates, "/NetSDK/Video/encode/channel/102");
                Add(candidates, "/NetSDK/Video/encode/channel/102/properties");
                break;
            case GroupedConfigKind.NetworkConfig:
            case GroupedConfigKind.WifiConfig:
                Add(candidates, "/NetSDK/Network/interfaces");
                Add(candidates, "/NetSDK/Network/interfaces/0");
                Add(candidates, "/NetSDK/Network/Ports");
                Add(candidates, "/NetSDK/Network/Dns");
                Add(candidates, "/NetSDK/Network/Esee");
                break;
        }

        return candidates;
    }

    private static IReadOnlyCollection<JsonObject> BuildPayloadCandidates(GroupedConfigKind groupKind, JsonObject root, string sourcePath, JsonNode value)
    {
        var payload = (JsonObject)root.DeepClone();
        SetPathValue(payload, sourcePath, value.DeepClone());
        var candidates = new List<JsonObject> { payload };

        JsonObject Wrap(string key) => new() { [key] = payload.DeepClone() };
        switch (groupKind)
        {
            case GroupedConfigKind.ImageConfig:
                candidates.Add(Wrap("Image"));
                break;
            case GroupedConfigKind.VideoEncodeConfig:
                candidates.Add(Wrap("VideoEncodeChannel"));
                break;
            case GroupedConfigKind.NetworkConfig:
                candidates.Add(Wrap("NetworkInterfaceList"));
                break;
            case GroupedConfigKind.WifiConfig:
                candidates.Add(Wrap("NetworkInterfaceWireless"));
                break;
            case GroupedConfigKind.UserConfig:
                candidates.Add(Wrap("DeviceInfo"));
                break;
            case GroupedConfigKind.AlarmConfig:
                candidates.Add(Wrap("AlarmOutputChannel"));
                candidates.Add(Wrap("VideoMotionDetectionChannel"));
                break;
            case GroupedConfigKind.StorageConfig:
                candidates.Add(Wrap("SDCardDbMedia"));
                break;
        }

        return candidates;
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

    private static bool ValuesEquivalent(JsonNode? left, JsonNode? right)
    {
        if (JsonNode.DeepEquals(left, right))
        {
            return true;
        }

        var leftDecimal = TryToDecimal(left);
        var rightDecimal = TryToDecimal(right);
        if (leftDecimal is not null && rightDecimal is not null)
        {
            return leftDecimal.Value == rightDecimal.Value;
        }

        var leftText = left?.ToJsonString().Trim('"');
        var rightText = right?.ToJsonString().Trim('"');
        return !string.IsNullOrWhiteSpace(leftText)
            && !string.IsNullOrWhiteSpace(rightText)
            && string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase);
    }

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
        foreach (var token in TokenizePath(cleaned))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(token.Key, out var next))
            {
                return null;
            }

            current = next;
            if (token.Index is int index)
            {
                if (current is not JsonArray arr || index < 0 || index >= arr.Count)
                {
                    return null;
                }

                current = arr[index];
            }
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

        JsonObject current = root;
        var tokens = TokenizePath(cleaned);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var key = token.Key;
            var leaf = index == tokens.Count - 1;
            if (leaf)
            {
                if (token.Index is int leafIndex)
                {
                    current[key] ??= new JsonArray();
                    if (current[key] is not JsonArray leafArray)
                    {
                        return;
                    }

                    while (leafArray.Count <= leafIndex)
                    {
                        leafArray.Add(null);
                    }

                    leafArray[leafIndex] = value?.DeepClone();
                }
                else
                {
                    current[key] = value?.DeepClone();
                }

                return;
            }

            if (token.Index is int itemIndex)
            {
                current[key] ??= new JsonArray();
                if (current[key] is not JsonArray array)
                {
                    return;
                }

                while (array.Count <= itemIndex)
                {
                    array.Add(new JsonObject());
                }

                if (array[itemIndex] is JsonObject itemObject)
                {
                    current = itemObject;
                }
                else
                {
                    return;
                }
            }
            else
            {
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

    private static List<(string Key, int? Index)> TokenizePath(string path)
    {
        var tokens = new List<(string Key, int? Index)>();
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cursor = part;
            while (!string.IsNullOrWhiteSpace(cursor))
            {
                var bracket = cursor.IndexOf('[', StringComparison.Ordinal);
                if (bracket < 0)
                {
                    tokens.Add((cursor, null));
                    break;
                }

                var key = cursor[..bracket];
                var close = cursor.IndexOf(']', bracket);
                if (close < 0 || !int.TryParse(cursor[(bracket + 1)..close], out var index))
                {
                    tokens.Add((cursor, null));
                    break;
                }

                tokens.Add((key, index));
                cursor = close + 1 < cursor.Length ? cursor[(close + 1)..] : string.Empty;
            }
        }

        return tokens;
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
