using BossCam.Contracts;

namespace BossCam.Core;

public sealed class ControlPointInventoryService(
    IApplicationStore store,
    IEndpointContractCatalog contractCatalog,
    GroupedConfigService groupedConfigService)
{
    private static readonly IReadOnlyDictionary<string, string> CurrentWidgetByFieldKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["codec"] = nameof(ControlPointWidgetKind.Dropdown),
        ["profile"] = nameof(ControlPointWidgetKind.Dropdown),
        ["dayNight"] = nameof(ControlPointWidgetKind.Dropdown),
        ["irMode"] = nameof(ControlPointWidgetKind.Dropdown),
        ["irCut"] = nameof(ControlPointWidgetKind.Dropdown),
        ["irCutMethod"] = nameof(ControlPointWidgetKind.Dropdown),
        ["sceneMode"] = nameof(ControlPointWidgetKind.Dropdown),
        ["exposure"] = nameof(ControlPointWidgetKind.Dropdown),
        ["awb"] = nameof(ControlPointWidgetKind.Dropdown),
        ["lowlight"] = nameof(ControlPointWidgetKind.Dropdown),
        ["resolution"] = nameof(ControlPointWidgetKind.Dropdown),
        ["bitrateMode"] = nameof(ControlPointWidgetKind.Dropdown),
        ["definition"] = nameof(ControlPointWidgetKind.Dropdown),
        ["wirelessMode"] = nameof(ControlPointWidgetKind.Dropdown),
        ["apMode"] = nameof(ControlPointWidgetKind.Dropdown),
        ["motionType"] = nameof(ControlPointWidgetKind.Dropdown),
        ["brightness"] = nameof(ControlPointWidgetKind.Slider),
        ["contrast"] = nameof(ControlPointWidgetKind.Slider),
        ["saturation"] = nameof(ControlPointWidgetKind.Slider),
        ["sharpness"] = nameof(ControlPointWidgetKind.Slider),
        ["hue"] = nameof(ControlPointWidgetKind.Slider),
        ["gamma"] = nameof(ControlPointWidgetKind.Slider),
        ["manualSharpness"] = nameof(ControlPointWidgetKind.Slider),
        ["denoise"] = nameof(ControlPointWidgetKind.Slider),
        ["wdrStrength"] = nameof(ControlPointWidgetKind.Slider),
        ["whiteLight"] = nameof(ControlPointWidgetKind.Slider),
        ["infrared"] = nameof(ControlPointWidgetKind.Slider),
        ["motionSensitivity"] = nameof(ControlPointWidgetKind.Slider),
        ["mirror"] = nameof(ControlPointWidgetKind.Toggle),
        ["flip"] = nameof(ControlPointWidgetKind.Toggle),
        ["audioEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["osdChannelNameEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["osdDateTimeEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["osdDisplayWeek"] = nameof(ControlPointWidgetKind.Toggle),
        ["motionEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["alarmEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["alarmBuzzer"] = nameof(ControlPointWidgetKind.Toggle),
        ["eseeEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["ntpEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["dhcpMode"] = nameof(ControlPointWidgetKind.Toggle),
        ["image"] = nameof(ControlPointWidgetKind.StructuredPanel),
        ["osd"] = nameof(ControlPointWidgetKind.TextInput),
        ["osdChannelNameText"] = nameof(ControlPointWidgetKind.TextInput),
        ["osdDateFormat"] = nameof(ControlPointWidgetKind.Dropdown),
        ["osdTimeFormat"] = nameof(ControlPointWidgetKind.Dropdown),
        ["bitrate"] = nameof(ControlPointWidgetKind.NumericInput),
        ["frameRate"] = nameof(ControlPointWidgetKind.NumericInput),
        ["keyframeInterval"] = nameof(ControlPointWidgetKind.NumericInput),
        ["audioBitRate"] = nameof(ControlPointWidgetKind.NumericInput),
        ["audioSampleRate"] = nameof(ControlPointWidgetKind.NumericInput),
        ["ip"] = nameof(ControlPointWidgetKind.TextInput),
        ["netmask"] = nameof(ControlPointWidgetKind.TextInput),
        ["gateway"] = nameof(ControlPointWidgetKind.TextInput),
        ["dns"] = nameof(ControlPointWidgetKind.TextInput),
        ["ports"] = nameof(ControlPointWidgetKind.TextInput),
        ["ntpServerDomain"] = nameof(ControlPointWidgetKind.TextInput),
        ["eseeId"] = nameof(ControlPointWidgetKind.TextInput),
        ["apSsid"] = nameof(ControlPointWidgetKind.TextInput),
        ["apPsk"] = nameof(ControlPointWidgetKind.TextInput),
        ["apChannel"] = nameof(ControlPointWidgetKind.TextInput),
        ["privacyMaskEnabled"] = nameof(ControlPointWidgetKind.Toggle),
        ["privacyMaskX"] = nameof(ControlPointWidgetKind.TextInput),
        ["privacyMaskY"] = nameof(ControlPointWidgetKind.TextInput),
        ["privacyMaskWidth"] = nameof(ControlPointWidgetKind.TextInput),
        ["privacyMaskHeight"] = nameof(ControlPointWidgetKind.TextInput),
        ["alarmInputActiveState"] = nameof(ControlPointWidgetKind.TextInput),
        ["alarmOutputActiveState"] = nameof(ControlPointWidgetKind.TextInput),
        ["alarmPulseDuration"] = nameof(ControlPointWidgetKind.TextInput)
    };

    public async Task<ControlPointInventoryReport?> GetReportAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var groupedResults = await store.GetGroupedRetestResultsAsync(deviceId, 1000, cancellationToken);
        var firmware = fields.Select(static item => item.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";
        var constraints = await store.GetFieldConstraintProfilesAsync(firmware, cancellationToken);
        var dependencyProfiles = await store.GetDependencyMatrixProfilesAsync(firmware, cancellationToken);
        var dependencyRules = dependencyProfiles.SelectMany(static profile => profile.Rules).ToList();
        var sdkCatalog = groupedConfigService.GetSdkFieldCatalog();

        var inventory = new List<ControlPointInventoryItem>();
        foreach (var contract in contracts)
        {
            inventory.Add(BuildRootItem(device, firmware, contract, dependencyRules));
            foreach (var field in contract.Fields)
            {
                inventory.Add(BuildContractFieldItem(device, firmware, contract, field, fields, constraints, dependencyRules, groupedResults));
            }
        }

        foreach (var sdkField in sdkCatalog)
        {
            if (inventory.Any(item =>
                item.FieldKey.Equals(sdkField.FieldKey, StringComparison.OrdinalIgnoreCase)
                && item.Endpoint.Equals(NormalizeEndpoint(sdkField.EndpointPattern), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            inventory.Add(BuildSdkOnlyItem(device, firmware, sdkField, fields, constraints, dependencyRules, groupedResults));
        }

        var ordered = inventory
            .GroupBy(item => $"{item.Family}|{item.ContractKey}|{item.Endpoint}|{item.FieldKey}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ContractKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ControlPointInventoryReport
        {
            DeviceId = deviceId,
            IpAddress = device.IpAddress ?? string.Empty,
            FirmwareFingerprint = firmware,
            Families = ordered
                .GroupBy(static item => item.Family, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ControlPointInventoryFamily
                {
                    Family = group.Key,
                    Controls = group.ToList()
                })
                .ToList(),
            AmbiguousControls = ordered.Where(static item => item.ControlType is null || !string.IsNullOrWhiteSpace(item.ExactBlocker)).ToList()
        };
    }

    private static ControlPointInventoryItem BuildRootItem(
        DeviceIdentity device,
        string firmware,
        EndpointContract contract,
        IReadOnlyCollection<FieldDependencyRule> dependencyRules)
    {
        var descriptor = new ControlPointDescriptor
        {
            FieldKey = contract.ContractKey,
            DisplayName = contract.GroupName,
            ContractKey = contract.ContractKey,
            Endpoint = NormalizeEndpoint(contract.Endpoint),
            SourcePath = contract.ObjectShape.RootPath,
            Kind = ContractFieldKind.Object,
            Writable = contract.Fields.Any(static field => field.Writable),
            ExpertOnly = contract.ExpertOnly,
            FullObjectWriteRequired = contract.ObjectShape.FullObjectWriteRequired,
            PartialWriteAllowed = contract.ObjectShape.PartialWriteAllowed,
            IsSyntheticRoot = true,
            DependencyRules = dependencyRules.Where(rule => contract.Fields.Any(field => field.Key.Equals(rule.PrimaryFieldKey, StringComparison.OrdinalIgnoreCase))).ToArray()
        };
        var typing = ControlPointClassifier.Classify(descriptor);
        return new ControlPointInventoryItem
        {
            DeviceId = device.Id,
            FirmwareFingerprint = firmware,
            Family = MapFamily(contract.ContractKey, contract.Endpoint, null),
            ContractKey = contract.ContractKey,
            Endpoint = NormalizeEndpoint(contract.Endpoint),
            WrapperObjectName = InferWrapperObjectName(contract.ContractKey, contract.Endpoint, contract.ObjectShape.RootPath),
            FieldKey = contract.ContractKey,
            DisplayName = $"{contract.GroupName} Object",
            ReadWriteState = contract.Fields.Any(static field => field.Writable) ? "GroupedObject" : "ReadOnlyObject",
            Ownership = NormalizeEndpoint(contract.Endpoint),
            LiveEvidence = contract.TruthState.ToString(),
            PrimitiveType = typing.PrimitiveType,
            ControlType = typing.ControlType,
            Traits = typing.Traits,
            AllowedValues = typing.AllowedValues,
            Min = null,
            Max = null,
            RequiredFormat = typing.RequiredFormat,
            ValuesBounded = typing.ValuesBounded,
            InterFieldDependent = typing.InterFieldDependent,
            GroupedWriteRequired = typing.GroupedWriteRequired,
            WriteShape = typing.WriteShape,
            RecommendedWidget = typing.RecommendedWidget,
            ExistingWidget = string.Empty,
            ExistingWidgetMismatch = false,
            NormalUiEligible = typing.NormalUiEligible,
            ExactBlocker = typing.TypeBlocker ?? string.Empty
        };
    }

    private static ControlPointInventoryItem BuildContractFieldItem(
        DeviceIdentity device,
        string firmware,
        EndpointContract contract,
        ContractField contractField,
        IReadOnlyCollection<NormalizedSettingField> fields,
        IReadOnlyCollection<FieldConstraintProfile> constraints,
        IReadOnlyCollection<FieldDependencyRule> dependencyRules,
        IReadOnlyCollection<GroupedUnsupportedRetestResult> groupedResults)
    {
        var field = fields
            .Where(item => item.FieldKey.Equals(contractField.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CapturedAt)
            .FirstOrDefault();
        var constraint = constraints.FirstOrDefault(item =>
            item.FieldKey.Equals(contractField.Key, StringComparison.OrdinalIgnoreCase)
            && item.ContractKey.Equals(contract.ContractKey, StringComparison.OrdinalIgnoreCase));
        var grouped = groupedResults
            .Where(item => item.FieldKey.Equals(contractField.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CapturedAt)
            .FirstOrDefault();
        var descriptor = new ControlPointDescriptor
        {
            FieldKey = contractField.Key,
            DisplayName = contractField.DisplayName,
            ContractKey = contract.ContractKey,
            Endpoint = NormalizeEndpoint(field?.SourceEndpoint ?? contract.Endpoint),
            SourcePath = contractField.SourcePath,
            Kind = contractField.Kind,
            Writable = contractField.Writable,
            ExpertOnly = contract.ExpertOnly || contractField.ExpertOnly,
            FullObjectWriteRequired = contract.ObjectShape.FullObjectWriteRequired,
            PartialWriteAllowed = contract.ObjectShape.PartialWriteAllowed,
            EnumValues = MergeValues(contractField.EnumValues.Select(static value => value.Value), constraint?.SupportedValues),
            Min = constraint?.Min ?? contractField.Validation.Min,
            Max = constraint?.Max ?? contractField.Validation.Max,
            DependencyRules = dependencyRules.Where(rule => rule.PrimaryFieldKey.Equals(contractField.Key, StringComparison.OrdinalIgnoreCase)).ToArray(),
            GroupedClassification = grouped?.Classification,
            GroupedBehavior = grouped?.Behavior,
            ExistingWidget = CurrentWidgetByFieldKey.GetValueOrDefault(contractField.Key)
        };
        var typing = ControlPointClassifier.Classify(descriptor);
        return new ControlPointInventoryItem
        {
            DeviceId = device.Id,
            FirmwareFingerprint = firmware,
            Family = MapFamily(contract.ContractKey, contract.Endpoint, null),
            ContractKey = contract.ContractKey,
            Endpoint = NormalizeEndpoint(field?.SourceEndpoint ?? contract.Endpoint),
            WrapperObjectName = InferWrapperObjectName(contract.ContractKey, contract.Endpoint, contractField.SourcePath),
            FieldKey = contractField.Key,
            DisplayName = contractField.DisplayName,
            ReadWriteState = DetermineReadWriteState(field, contractField, grouped),
            Ownership = DetermineOwnership(field, grouped, contract.Endpoint),
            LiveEvidence = DetermineLiveEvidence(field, contractField, grouped),
            PrimitiveType = typing.PrimitiveType,
            ControlType = typing.ControlType,
            Traits = typing.Traits,
            AllowedValues = typing.AllowedValues,
            Min = descriptor.Min,
            Max = descriptor.Max,
            RequiredFormat = typing.RequiredFormat,
            ValuesBounded = typing.ValuesBounded,
            InterFieldDependent = typing.InterFieldDependent,
            GroupedWriteRequired = typing.GroupedWriteRequired,
            WriteShape = typing.WriteShape,
            RecommendedWidget = typing.RecommendedWidget,
            ExistingWidget = descriptor.ExistingWidget ?? string.Empty,
            ExistingWidgetMismatch = IsWidgetMismatch(descriptor.ExistingWidget, typing.RecommendedWidget),
            NormalUiEligible = typing.NormalUiEligible,
            ExactBlocker = typing.TypeBlocker ?? string.Empty
        };
    }

    private static ControlPointInventoryItem BuildSdkOnlyItem(
        DeviceIdentity device,
        string firmware,
        SdkFieldDefinition sdkField,
        IReadOnlyCollection<NormalizedSettingField> fields,
        IReadOnlyCollection<FieldConstraintProfile> constraints,
        IReadOnlyCollection<FieldDependencyRule> dependencyRules,
        IReadOnlyCollection<GroupedUnsupportedRetestResult> groupedResults)
    {
        var field = fields
            .Where(item => item.FieldKey.Equals(sdkField.FieldKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CapturedAt)
            .FirstOrDefault();
        var constraint = constraints.FirstOrDefault(item => item.FieldKey.Equals(sdkField.FieldKey, StringComparison.OrdinalIgnoreCase));
        var grouped = groupedResults
            .Where(item => item.FieldKey.Equals(sdkField.FieldKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CapturedAt)
            .FirstOrDefault();
        var descriptor = new ControlPointDescriptor
        {
            FieldKey = sdkField.FieldKey,
            DisplayName = sdkField.DisplayName,
            ContractKey = $"sdk:{sdkField.GroupKind}",
            Endpoint = NormalizeEndpoint(field?.SourceEndpoint ?? sdkField.EndpointPattern),
            SourcePath = sdkField.SourcePath,
            Kind = sdkField.Kind,
            Writable = sdkField.Writable,
            FullObjectWriteRequired = true,
            EnumValues = MergeValues(sdkField.EnumValues, constraint?.SupportedValues),
            Min = constraint?.Min ?? sdkField.Min,
            Max = constraint?.Max ?? sdkField.Max,
            DependencyRules = dependencyRules.Where(rule => rule.PrimaryFieldKey.Equals(sdkField.FieldKey, StringComparison.OrdinalIgnoreCase)).ToArray(),
            GroupedClassification = grouped?.Classification,
            GroupedBehavior = grouped?.Behavior,
            ExistingWidget = CurrentWidgetByFieldKey.GetValueOrDefault(sdkField.FieldKey)
        };
        var typing = ControlPointClassifier.Classify(descriptor);
        return new ControlPointInventoryItem
        {
            DeviceId = device.Id,
            FirmwareFingerprint = firmware,
            Family = MapFamily(null, sdkField.EndpointPattern, sdkField.GroupKind),
            ContractKey = descriptor.ContractKey,
            Endpoint = NormalizeEndpoint(field?.SourceEndpoint ?? sdkField.EndpointPattern),
            WrapperObjectName = InferWrapperObjectName(descriptor.ContractKey, sdkField.EndpointPattern, sdkField.SourcePath),
            FieldKey = sdkField.FieldKey,
            DisplayName = sdkField.DisplayName,
            ReadWriteState = field is null ? (sdkField.Writable ? "SdkCatalogCandidate" : "SdkCatalogReadOnlyCandidate") : DetermineReadWriteState(field, null, grouped),
            Ownership = field?.SourceEndpoint ?? NormalizeEndpoint(sdkField.EndpointPattern),
            LiveEvidence = string.IsNullOrWhiteSpace(sdkField.SourceEvidence) ? "sdk-catalog" : sdkField.SourceEvidence,
            PrimitiveType = typing.PrimitiveType,
            ControlType = typing.ControlType,
            Traits = typing.Traits,
            AllowedValues = typing.AllowedValues,
            Min = descriptor.Min,
            Max = descriptor.Max,
            RequiredFormat = typing.RequiredFormat,
            ValuesBounded = typing.ValuesBounded,
            InterFieldDependent = typing.InterFieldDependent,
            GroupedWriteRequired = typing.GroupedWriteRequired,
            WriteShape = typing.WriteShape,
            RecommendedWidget = typing.RecommendedWidget,
            ExistingWidget = descriptor.ExistingWidget ?? string.Empty,
            ExistingWidgetMismatch = IsWidgetMismatch(descriptor.ExistingWidget, typing.RecommendedWidget),
            NormalUiEligible = typing.NormalUiEligible,
            ExactBlocker = typing.TypeBlocker ?? string.Empty
        };
    }

    private static string DetermineReadWriteState(NormalizedSettingField? field, ContractField? contractField, GroupedUnsupportedRetestResult? grouped)
    {
        if (grouped is not null)
        {
            return grouped.Classification switch
            {
                ForcedFieldClassification.Writable => "Writable",
                ForcedFieldClassification.WritableNeedsCommitTrigger => "WritableNeedsCommitTrigger",
                ForcedFieldClassification.DelayedApply => "DelayedApply",
                ForcedFieldClassification.ReadableOnly => "ReadableOnly",
                ForcedFieldClassification.RequiresGroupedWrite => "RequiresGroupedWrite",
                ForcedFieldClassification.RequiresCommitTrigger => "RequiresCommitTrigger",
                ForcedFieldClassification.Ignored => "Ignored",
                ForcedFieldClassification.Unsupported => "Unsupported",
                _ => grouped.Classification.ToString()
            };
        }

        if (field is null)
        {
            return contractField?.Writable == true ? "CatalogOnlyCandidate" : "CatalogOnlyReadOnly";
        }

        if (field.WriteVerified)
        {
            return field.PersistsAfterReboot ? "WritablePersistent" : "Writable";
        }

        if (field.ReadVerified)
        {
            return contractField?.Writable == true ? "ReadableOnly" : "ReadOnly";
        }

        return "Unverified";
    }

    private static string DetermineOwnership(NormalizedSettingField? field, GroupedUnsupportedRetestResult? grouped, string contractEndpoint)
    {
        if (grouped is not null && !string.IsNullOrWhiteSpace(grouped.SourceEndpoint))
        {
            return NormalizeEndpoint(grouped.SourceEndpoint);
        }

        if (field is not null && !string.IsNullOrWhiteSpace(field.SourceEndpoint))
        {
            return NormalizeEndpoint(field.SourceEndpoint);
        }

        return NormalizeEndpoint(contractEndpoint);
    }

    private static string DetermineLiveEvidence(NormalizedSettingField? field, ContractField contractField, GroupedUnsupportedRetestResult? grouped)
    {
        if (grouped is not null)
        {
            return $"{grouped.Classification}|{grouped.Behavior}";
        }

        if (field is not null)
        {
            return $"{field.Validity}|{field.SupportState}|read={field.ReadVerified}|write={field.WriteVerified}";
        }

        return contractField.Evidence.Source;
    }

    private static string InferWrapperObjectName(string? contractKey, string endpoint, string sourcePath)
    {
        var cleanedPath = sourcePath.Trim();
        if (cleanedPath.StartsWith("$.", StringComparison.Ordinal))
        {
            cleanedPath = cleanedPath[2..];
        }
        else if (cleanedPath.StartsWith("$", StringComparison.Ordinal))
        {
            cleanedPath = cleanedPath[1..];
        }

        if (string.IsNullOrWhiteSpace(cleanedPath) || cleanedPath.Equals("$", StringComparison.Ordinal))
        {
            return contractKey switch
            {
                not null when contractKey.StartsWith("video.input", StringComparison.OrdinalIgnoreCase) => "VideoInputChannel",
                not null when contractKey.StartsWith("video.encode", StringComparison.OrdinalIgnoreCase) => "VideoEncodeChannel",
                not null when contractKey.StartsWith("image.ircut", StringComparison.OrdinalIgnoreCase) => "IrCutFilter",
                not null when contractKey.StartsWith("image.", StringComparison.OrdinalIgnoreCase) => "Image",
                not null when contractKey.StartsWith("network.wireless", StringComparison.OrdinalIgnoreCase) => "Wireless",
                not null when contractKey.StartsWith("network.", StringComparison.OrdinalIgnoreCase) => "Network",
                not null when contractKey.StartsWith("audio.", StringComparison.OrdinalIgnoreCase) => "Audio",
                not null when contractKey.StartsWith("storage.", StringComparison.OrdinalIgnoreCase) => "Storage",
                not null when contractKey.StartsWith("alarm.", StringComparison.OrdinalIgnoreCase) => "Alarm",
                not null when contractKey.StartsWith("video.overlay", StringComparison.OrdinalIgnoreCase) => "Overlay",
                _ => endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "Root"
            };
        }

        var segment = cleanedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(segment))
        {
            return "Root";
        }

        var bracket = segment.IndexOf('[', StringComparison.Ordinal);
        return bracket >= 0 ? segment[..bracket] : segment;
    }

    private static string MapFamily(string? contractKey, string endpoint, GroupedConfigKind? groupKind)
    {
        if (!string.IsNullOrWhiteSpace(contractKey))
        {
            if (contractKey.StartsWith("video.input", StringComparison.OrdinalIgnoreCase))
            {
                return "VideoInput";
            }

            if (contractKey.StartsWith("video.encode", StringComparison.OrdinalIgnoreCase))
            {
                return "VideoEncode";
            }

            if (contractKey.StartsWith("image.ircut", StringComparison.OrdinalIgnoreCase))
            {
                return "IrCutFilter";
            }

            if (contractKey.StartsWith("image.", StringComparison.OrdinalIgnoreCase))
            {
                return "Image";
            }

            if (contractKey.StartsWith("video.overlay", StringComparison.OrdinalIgnoreCase))
            {
                return "Overlay / OSD";
            }

            if (contractKey.StartsWith("audio.", StringComparison.OrdinalIgnoreCase))
            {
                return "Audio";
            }

            if (contractKey.StartsWith("video.snapshot", StringComparison.OrdinalIgnoreCase))
            {
                return "Snapshot";
            }

            if (contractKey.StartsWith("network.wireless", StringComparison.OrdinalIgnoreCase))
            {
                return "Wifi";
            }

            if (contractKey.StartsWith("network.", StringComparison.OrdinalIgnoreCase))
            {
                return "Network";
            }

            if (contractKey.StartsWith("users.", StringComparison.OrdinalIgnoreCase)
                || contractKey.StartsWith("system.time", StringComparison.OrdinalIgnoreCase)
                || contractKey.StartsWith("system.device", StringComparison.OrdinalIgnoreCase))
            {
                return "User";
            }

            if (contractKey.StartsWith("alarm.", StringComparison.OrdinalIgnoreCase)
                || contractKey.Contains("motion", StringComparison.OrdinalIgnoreCase))
            {
                return "Alarm";
            }

            if (contractKey.StartsWith("storage.", StringComparison.OrdinalIgnoreCase))
            {
                return "Storage";
            }
        }

        return groupKind switch
        {
            GroupedConfigKind.ImageConfig => endpoint.Contains("irCut", StringComparison.OrdinalIgnoreCase) ? "IrCutFilter" : "Image",
            GroupedConfigKind.VideoEncodeConfig => "VideoEncode",
            GroupedConfigKind.NetworkConfig => "Network",
            GroupedConfigKind.WifiConfig => "Wifi",
            GroupedConfigKind.UserConfig => "User",
            GroupedConfigKind.AlarmConfig => "Alarm",
            GroupedConfigKind.StorageConfig => "Storage",
            _ => "Misc"
        };
    }

    private static IReadOnlyCollection<string> MergeValues(IEnumerable<string>? primary, IEnumerable<string>? secondary)
        => (primary ?? [])
            .Concat(secondary ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsWidgetMismatch(string? existingWidget, ControlPointWidgetKind recommended)
        => !string.IsNullOrWhiteSpace(existingWidget)
            && !existingWidget.Equals(recommended.ToString(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEndpoint(string endpoint)
        => endpoint
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);
}
