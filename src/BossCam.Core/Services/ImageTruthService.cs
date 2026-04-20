using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class ImageTruthService(
    IApplicationStore store,
    TypedSettingsService typedSettingsService,
    IEndpointContractCatalog contractCatalog,
    ILogger<ImageTruthService> logger)
{
    private static readonly Dictionary<string, string> FixtureFieldAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brightnessLevel"] = "brightness",
        ["contrastLevel"] = "contrast",
        ["saturationLevel"] = "saturation",
        ["sharpnessLevel"] = "sharpness",
        ["constantBitRate"] = "bitrate",
        ["keyFrameInterval"] = "keyframeInterval"
    };

    private static readonly string[] PriorityFieldOrder =
    [
        "brightness", "contrast", "saturation", "sharpness", "hue", "gamma", "wdr", "denoise", "exposure",
        "dayNight", "irMode", "whiteLight", "irCut", "mirror", "flip", "osd", "resolution", "codec", "frameRate", "keyframeInterval", "bitrate"
    ];

    public async Task<ImageTruthSweepResult?> RunImageTruthSweepAsync(Guid deviceId, bool includeBehaviorMapping, bool refreshFromDevice, string? exportRoot, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var inventory = await DiscoverInventoryAsync(deviceId, refreshFromDevice, cancellationToken);
        var testSet = await BuildWritableTestSetAsync(deviceId, inventory, cancellationToken);
        IReadOnlyCollection<ImageFieldBehaviorMap> maps = [];
        if (includeBehaviorMapping && testSet is not null && testSet.Cases.Count > 0)
        {
            maps = await MapBehaviorAsync(deviceId, testSet, cancellationToken);
        }
        var needsFixtureBehavior = maps.Count == 0
            || maps.All(static map => string.IsNullOrWhiteSpace(map.RecommendedRange) || map.RecommendedRange.Equals("unverified", StringComparison.OrdinalIgnoreCase));
        if (needsFixtureBehavior)
        {
            maps = await BuildBehaviorMapsFromFixturesAsync(device, testSet, cancellationToken);
            if (maps.Count > 0)
            {
                await store.SaveImageBehaviorMapsAsync(maps, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(exportRoot))
        {
            await ExportArtifactsAsync(device, inventory, testSet, maps, exportRoot!, cancellationToken);
        }

        var output = new ImageTruthSweepResult
        {
            DeviceId = deviceId,
            FirmwareFingerprint = BuildFirmwareFingerprint(device, inventory),
            Inventory = inventory,
            WritableTestSet = testSet?.Cases ?? [],
            BehaviorMaps = maps,
            Notes = BuildSummary(inventory, testSet, maps)
        };
        logger.LogInformation("Image truth sweep complete for {Device} inventory={Inventory} writable={Writable} behaviorMaps={Maps}", device.DisplayName, output.Inventory.Count, output.WritableTestSet.Count, output.BehaviorMaps.Count);
        return output;
    }

    public async Task<IReadOnlyCollection<ImageControlInventoryItem>> DiscoverInventoryAsync(Guid deviceId, bool refreshFromDevice, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var groups = await typedSettingsService.NormalizeDeviceAsync(deviceId, refreshFromDevice, cancellationToken);
        var imageFields = groups.Where(static group => group.GroupKind == TypedSettingGroupKind.VideoImage)
            .SelectMany(static group => group.Fields)
            .GroupBy(static field => field.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static field => field.CapturedAt).First())
            .ToDictionary(static field => field.FieldKey, StringComparer.OrdinalIgnoreCase);
        var semantic = await store.GetSemanticWriteObservationsAsync(deviceId, 2000, cancellationToken);
        var contracts = (await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken))
            .Where(static contract => contract.GroupKind == TypedSettingGroupKind.VideoImage)
            .ToList();
        var fixtureRows = await LoadFixtureRowsForIpAsync(device.IpAddress, cancellationToken);
        var liveEvidenceRows = await LoadLiveSemanticEvidenceRowsForIpAsync(device.IpAddress, cancellationToken);
        var firmware = BuildFirmwareFingerprint(device, imageFields.Values);

        var candidateKeys = new HashSet<string>(PriorityFieldOrder, StringComparer.OrdinalIgnoreCase);
        foreach (var key in contracts.SelectMany(static contract => contract.Fields.Select(static field => field.Key)))
        {
            _ = candidateKeys.Add(key);
        }
        foreach (var key in imageFields.Keys)
        {
            _ = candidateKeys.Add(key);
        }

        var inventory = new List<ImageControlInventoryItem>();
        foreach (var key in candidateKeys.OrderBy(OrderField))
        {
            imageFields.TryGetValue(key, out var field);
            var contract = contracts.FirstOrDefault(item => item.Fields.Any(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
            var contractField = contract?.Fields.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var semanticHistory = semantic
                .Where(item => item.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static item => item.Timestamp)
                .ToList();
            var lastSemantic = semanticHistory.FirstOrDefault();
            var status = ResolveInventoryStatus(field, contractField, lastSemantic);
            var decision = DetermineClassification(status, field, contract, contractField, semanticHistory);
            inventory.Add(new ImageControlInventoryItem
            {
                DeviceId = deviceId,
                FirmwareFingerprint = firmware,
                FieldKey = key,
                DisplayName = field?.DisplayName ?? contractField?.DisplayName ?? key,
                SourceEndpoint = field?.SourceEndpoint ?? contract?.Endpoint ?? string.Empty,
                SourcePath = field?.RawSourcePath ?? contractField?.SourcePath ?? "$",
                Readable = field is not null && field.Validity != FieldValidityState.Invalid,
                Writable = field?.WriteVerified == true || (contractField?.Writable == true && field?.SupportState == ContractSupportState.Supported),
                WriteVerified = field?.WriteVerified == true,
                SupportState = field?.SupportState ?? ResolveSupport(contractField),
                TruthState = field?.TruthState ?? contractField?.Evidence.TruthState ?? ContractTruthState.Unverified,
                Status = status,
                CandidateClassification = decision.Classification,
                PromotedToUi = ShouldPromoteToUi(status, decision.Classification, field, contractField),
                ReasonCodes = decision.ReasonCodes,
                Notes = BuildInventoryNote(field, contractField, lastSemantic, status, decision),
                CapturedAt = DateTimeOffset.UtcNow
            });
        }

        if (liveEvidenceRows.Count > 0)
        {
            inventory = ApplyLiveEvidenceOverrides(inventory, liveEvidenceRows);
        }

        foreach (var fixture in fixtureRows.Where(static row => row.Verified))
        {
            var key = NormalizeFixtureFieldKey(fixture.Field);
            var existing = inventory.FirstOrDefault(item => item.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                inventory.Add(new ImageControlInventoryItem
                {
                    DeviceId = deviceId,
                    FirmwareFingerprint = firmware,
                    FieldKey = key,
                    DisplayName = key,
                    SourceEndpoint = fixture.Endpoint ?? "/netsdk/video/input/channel/1",
                    SourcePath = "$." + fixture.Field,
                    Readable = true,
                    Writable = true,
                    WriteVerified = true,
                    SupportState = ContractSupportState.Supported,
                    TruthState = ContractTruthState.Proven,
                    Status = ImageInventoryStatus.Writable,
                    CandidateClassification = HiddenCandidateClassification.Writable,
                    PromotedToUi = true,
                    ReasonCodes = ["fixture_proven_write_readback"],
                    Notes = "Fixture-proven write/readback from 2026-04-19 capture."
                });
                continue;
            }

            if (!existing.WriteVerified || existing.Status != ImageInventoryStatus.Writable)
            {
                inventory.Remove(existing);
                inventory.Add(existing with
                {
                    Writable = true,
                    WriteVerified = true,
                    SupportState = ContractSupportState.Supported,
                    TruthState = ContractTruthState.Proven,
                    Status = ImageInventoryStatus.Writable,
                    CandidateClassification = HiddenCandidateClassification.Writable,
                    PromotedToUi = true,
                    ReasonCodes = ["fixture_proven_write_readback"],
                    Notes = string.IsNullOrWhiteSpace(existing.Notes)
                        ? "Fixture-proven write/readback from 2026-04-19 capture."
                        : $"{existing.Notes} | fixture-proven write/readback"
                });
            }
        }

        await store.SaveImageControlInventoryAsync(inventory, cancellationToken);
        return inventory;
    }

    public async Task<ImageWritableTestSetProfile?> BuildWritableTestSetAsync(Guid deviceId, IReadOnlyCollection<ImageControlInventoryItem>? inventory, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var source = inventory ?? await store.GetImageControlInventoryAsync(deviceId, cancellationToken);
        if (source.Count == 0)
        {
            return null;
        }

        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var fixtureRows = await LoadFixtureRowsForIpAsync(device.IpAddress, cancellationToken);
        var cases = new List<ImageWritableTestCase>();
        foreach (var item in source.Where(static entry => entry.Status == ImageInventoryStatus.Writable))
        {
            var field = fields.Where(candidate => candidate.FieldKey.Equals(item.FieldKey, StringComparison.OrdinalIgnoreCase)).OrderByDescending(static candidate => candidate.CapturedAt).FirstOrDefault();
            var contract = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field?.ContractKey, StringComparison.OrdinalIgnoreCase));
            var contractField = contract?.Fields.FirstOrDefault(candidate => candidate.Key.Equals(item.FieldKey, StringComparison.OrdinalIgnoreCase));
            if (field is null || contractField is null || !contractField.Writable)
            {
                continue;
            }

            cases.Add(new ImageWritableTestCase
            {
                FieldKey = item.FieldKey,
                DisplayName = item.DisplayName,
                ContractKey = field.ContractKey ?? string.Empty,
                SourceEndpoint = field.SourceEndpoint,
                SourcePath = contractField.SourcePath,
                BaselineValue = field.TypedValue?.DeepClone(),
                CandidateValues = BuildCandidates(field.TypedValue, contractField),
                Notes = "Auto-generated safe reversible candidates."
            });
        }

        var profile = new ImageWritableTestSetProfile
        {
            DeviceId = deviceId,
            FirmwareFingerprint = BuildFirmwareFingerprint(device, source),
            Cases = cases,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (profile.Cases.Count == 0 && fixtureRows.Count > 0)
        {
            var fallback = fixtureRows
                .Where(static row => row.Verified)
                .GroupBy(row => NormalizeFixtureFieldKey(row.Field), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    var attempted = group.Select(row => row.Attempt).Distinct().Select(static value => (JsonNode?)JsonValue.Create(value)).ToList();
                    return new ImageWritableTestCase
                    {
                        FieldKey = NormalizeFixtureFieldKey(first.Field),
                        DisplayName = NormalizeFixtureFieldKey(first.Field),
                        ContractKey = "fixture-proven",
                        SourceEndpoint = first.Endpoint ?? "/netsdk/video/input/channel/1",
                        SourcePath = "$." + first.Field,
                        BaselineValue = JsonValue.Create(first.Orig),
                        CandidateValues = attempted,
                        Notes = "Derived from fixture-proven write/readback rows."
                    };
                })
                .ToList();
            profile = profile with { Cases = fallback };
        }

        await store.SaveImageWritableTestSetAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<IReadOnlyCollection<ImageFieldBehaviorMap>> MapBehaviorAsync(Guid deviceId, ImageWritableTestSetProfile testSet, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var maps = new List<ImageFieldBehaviorMap>();
        foreach (var test in testSet.Cases.Where(static candidate => candidate.CandidateValues.Count > 0))
        {
            var points = new List<ImageBehaviorPoint>();
            foreach (var candidate in test.CandidateValues)
            {
                var write = await typedSettingsService.ApplyTypedFieldAsync(deviceId, test.FieldKey, candidate?.DeepClone(), expertOverride: false, cancellationToken);
                var immediate = await ReadCurrentFieldValueAsync(deviceId, test.FieldKey, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var delayed1 = await ReadCurrentFieldValueAsync(deviceId, test.FieldKey, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var delayed3 = await ReadCurrentFieldValueAsync(deviceId, test.FieldKey, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var delayed5 = await ReadCurrentFieldValueAsync(deviceId, test.FieldKey, cancellationToken);
                var semantic = write?.SemanticStatus ?? SemanticWriteStatus.Uncertain;
                var behavior = ClassifyBehavior(test.FieldKey, test.BaselineValue, immediate, delayed1, delayed3, delayed5, semantic);
                var commit = ClassifyCommitBehavior(test.BaselineValue, candidate, immediate, delayed1, delayed3, delayed5, semantic);
                var metric = BuildOperationalMetric(test.FieldKey, test.BaselineValue, immediate);
                points.Add(new ImageBehaviorPoint
                {
                    AttemptedValue = candidate?.DeepClone(),
                    BaselineValue = test.BaselineValue?.DeepClone(),
                    ImmediateValue = immediate?.DeepClone(),
                    Delayed1sValue = delayed1?.DeepClone(),
                    Delayed3sValue = delayed3?.DeepClone(),
                    Delayed5sValue = delayed5?.DeepClone(),
                    SemanticStatus = semantic,
                    BehaviorClass = behavior,
                    CommitBehavior = commit,
                    OperationalMetric = metric,
                    Notes = write?.Message ?? string.Empty
                });
            }

            maps.Add(BuildBehaviorMap(deviceId, testSet.FirmwareFingerprint, test, points));
        }

        await store.SaveImageBehaviorMapsAsync(maps, cancellationToken);
        return maps;
    }

    public Task<IReadOnlyCollection<ImageControlInventoryItem>> GetInventoryAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetImageControlInventoryAsync(deviceId, cancellationToken);

    public Task<ImageWritableTestSetProfile?> GetWritableTestSetAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetImageWritableTestSetAsync(deviceId, cancellationToken);

    public Task<IReadOnlyCollection<ImageFieldBehaviorMap>> GetBehaviorMapsAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetImageBehaviorMapsAsync(deviceId, cancellationToken);

    private static int OrderField(string key)
    {
        var index = Array.FindIndex(PriorityFieldOrder, candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 999;
    }

    private static ContractSupportState ResolveSupport(ContractField? field)
    {
        if (field is null)
        {
            return ContractSupportState.Uncertain;
        }

        if (!field.Writable)
        {
            return ContractSupportState.Supported;
        }

        return field.Evidence.TruthState == ContractTruthState.Unverified ? ContractSupportState.Uncertain : ContractSupportState.Unsupported;
    }

    private static string BuildFirmwareFingerprint(DeviceIdentity device, IEnumerable<ImageControlInventoryItem> inventory)
        => inventory.Select(static item => item.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
           ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

    private static string BuildFirmwareFingerprint(DeviceIdentity device, IEnumerable<NormalizedSettingField> fields)
        => fields.Select(static item => item.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
           ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

    private static ImageInventoryStatus ResolveInventoryStatus(NormalizedSettingField? field, ContractField? contractField, SemanticWriteObservation? semantic)
    {
        if (field is null)
        {
            return contractField is null ? ImageInventoryStatus.Uncertain : ImageInventoryStatus.HiddenAdjacentCandidate;
        }

        if (field.WriteVerified)
        {
            return ImageInventoryStatus.Writable;
        }

        if (semantic?.Status is SemanticWriteStatus.AcceptedNoChange or SemanticWriteStatus.AcceptedChangedThenReverted)
        {
            return ImageInventoryStatus.TransportSuccessNoSemanticChange;
        }

        if (semantic?.Status is SemanticWriteStatus.Rejected or SemanticWriteStatus.TransportFailed or SemanticWriteStatus.ContractViolation)
        {
            return ImageInventoryStatus.Blocked;
        }

        if (field.ReadVerified)
        {
            return ImageInventoryStatus.Readable;
        }

        return ImageInventoryStatus.Uncertain;
    }

    private static string BuildInventoryNote(
        NormalizedSettingField? field,
        ContractField? contractField,
        SemanticWriteObservation? semantic,
        ImageInventoryStatus status,
        ClassificationDecision decision)
    {
        if (status == ImageInventoryStatus.HiddenAdjacentCandidate)
        {
            return $"Contract field exists but endpoint payload did not expose this path in latest read. reasons={string.Join(",", decision.ReasonCodes)}";
        }

        if (semantic is not null)
        {
            return $"Last semantic status: {semantic.Status}. reasons={string.Join(",", decision.ReasonCodes)}";
        }

        if (field is not null)
        {
            return $"Validity={field.Validity}, Support={field.SupportState}, Truth={field.TruthState}. reasons={string.Join(",", decision.ReasonCodes)}";
        }

        var note = contractField?.Evidence.Notes ?? string.Empty;
        return string.IsNullOrWhiteSpace(note)
            ? $"reasons={string.Join(",", decision.ReasonCodes)}"
            : $"{note}. reasons={string.Join(",", decision.ReasonCodes)}";
    }

    private static ClassificationDecision DetermineClassification(
        ImageInventoryStatus status,
        NormalizedSettingField? field,
        EndpointContract? contract,
        ContractField? contractField,
        IReadOnlyCollection<SemanticWriteObservation> semantics)
    {
        var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var latest = semantics.OrderByDescending(static item => item.Timestamp).FirstOrDefault();
        var rejectionCount = semantics.Count(IsSemanticRejection);
        var noChangeCount = semantics.Count(IsNoSemanticChange);
        var readOnly = field is not null && field.ReadVerified && !field.WriteVerified;
        var hasEndpointCandidate = contract is not null || contractField is not null;
        var hasAdjacentPayloadEvidence = field is not null;
        var hasAuthEvidence = field?.ReadVerified == true
            || field?.WriteVerified == true
            || semantics.Any(static item => !ContainsAuthFailure(item));
        var hasPrivateCandidate = contract?.Surface == ContractSurface.PrivateCgiXml;
        var disruption = field?.DisruptionClass
            ?? contractField?.DisruptionClass
            ?? contract?.DisruptionClass
            ?? DisruptionClass.Unknown;

        if (status == ImageInventoryStatus.Writable)
        {
            return new ClassificationDecision(HiddenCandidateClassification.Writable, ["proven_writable"]);
        }

        if (status == ImageInventoryStatus.Readable)
        {
            reasons.Add("surfaced_read_only");
            return new ClassificationDecision(HiddenCandidateClassification.ReadableOnly, reasons.ToList());
        }

        if (latest?.Status == SemanticWriteStatus.AcceptedChangedThenReverted)
        {
            reasons.Add("requires_commit_trigger");
            return new ClassificationDecision(HiddenCandidateClassification.RequiresCommitTrigger, reasons.ToList());
        }

        if (status == ImageInventoryStatus.TransportSuccessNoSemanticChange || noChangeCount > 0)
        {
            reasons.Add("transport_success_no_semantic_change");
            return new ClassificationDecision(HiddenCandidateClassification.Ignored, reasons.ToList());
        }

        if (disruption is DisruptionClass.NetworkChanging or DisruptionClass.ServiceImpacting or DisruptionClass.Reboot or DisruptionClass.FactoryReset or DisruptionClass.FirmwareUpgrade)
        {
            reasons.Add("dangerous_disruption_class");
            if (hasPrivateCandidate)
            {
                reasons.Add("private_path_candidate");
            }

            return new ClassificationDecision(HiddenCandidateClassification.Dangerous, reasons.ToList());
        }

        if (hasPrivateCandidate)
        {
            reasons.Add("private_path_candidate");
            if (!hasAuthEvidence)
            {
                reasons.Add("auth_or_session_missing");
            }
            if (!hasAdjacentPayloadEvidence)
            {
                reasons.Add("not_surfaced_in_live_payload");
            }

            return new ClassificationDecision(HiddenCandidateClassification.PrivatePathCandidate, reasons.ToList());
        }

        if (readOnly)
        {
            reasons.Add("surfaced_read_only");
            return new ClassificationDecision(HiddenCandidateClassification.ReadableOnly, reasons.ToList());
        }

        if (!hasAuthEvidence && semantics.Count == 0)
        {
            reasons.Add("auth_or_session_missing");
            reasons.Add("no_semantic_proof");
            return new ClassificationDecision(HiddenCandidateClassification.NoSemanticProof, reasons.ToList());
        }

        if (status == ImageInventoryStatus.Blocked || rejectionCount > 0)
        {
            reasons.Add("repeated_live_rejection");
            if (!hasEndpointCandidate)
            {
                reasons.Add("no_endpoint_candidate_found");
                return new ClassificationDecision(HiddenCandidateClassification.LikelyUnsupported, reasons.ToList());
            }

            if (!hasAdjacentPayloadEvidence)
            {
                reasons.Add("not_surfaced_in_live_payload");
            }

            var repeatedAcrossCandidates = rejectionCount >= 3;
            if (repeatedAcrossCandidates && !hasAdjacentPayloadEvidence && hasAuthEvidence)
            {
                return new ClassificationDecision(HiddenCandidateClassification.UnsupportedOnFirmware, reasons.ToList());
            }

            if (rejectionCount >= 2 && (!hasAuthEvidence || hasPrivateCandidate || !hasAdjacentPayloadEvidence))
            {
                return new ClassificationDecision(HiddenCandidateClassification.LikelyUnsupported, reasons.ToList());
            }

            return new ClassificationDecision(HiddenCandidateClassification.RejectedByFirmware, reasons.ToList());
        }

        if (status == ImageInventoryStatus.HiddenAdjacentCandidate)
        {
            reasons.Add("not_surfaced_in_live_payload");
            return new ClassificationDecision(HiddenCandidateClassification.HiddenAdjacentCandidate, reasons.ToList());
        }

        if (!hasEndpointCandidate)
        {
            reasons.Add("no_endpoint_candidate_found");
            return new ClassificationDecision(HiddenCandidateClassification.HiddenAdjacentCandidate, reasons.ToList());
        }

        reasons.Add("no_semantic_proof");
        if (!hasAdjacentPayloadEvidence)
        {
            reasons.Add("not_surfaced_in_live_payload");
        }

        return new ClassificationDecision(HiddenCandidateClassification.NoSemanticProof, reasons.ToList());
    }

    private static bool ShouldPromoteToUi(
        ImageInventoryStatus status,
        HiddenCandidateClassification classification,
        NormalizedSettingField? field,
        ContractField? contractField)
    {
        if (classification == HiddenCandidateClassification.Writable || classification == HiddenCandidateClassification.ReadableOnly)
        {
            if (field?.TruthState == ContractTruthState.Proven || contractField?.Evidence.TruthState == ContractTruthState.Proven)
            {
                return true;
            }

            // Allow promotion of legacy-proven fields that are currently inferred in contract but validated in runtime.
            return field?.ReadVerified == true || field?.WriteVerified == true;
        }

        return status == ImageInventoryStatus.Writable;
    }

    private static bool IsSemanticRejection(SemanticWriteObservation observation)
        => observation.Status is SemanticWriteStatus.TransportFailed
            or SemanticWriteStatus.Rejected
            or SemanticWriteStatus.ContractViolation
            or SemanticWriteStatus.ShapeMismatch
            or SemanticWriteStatus.Uncertain;

    private static bool IsNoSemanticChange(SemanticWriteObservation observation)
        => observation.Status is SemanticWriteStatus.AcceptedNoChange
            or SemanticWriteStatus.AcceptedChangedThenReverted;

    private static bool ContainsAuthFailure(SemanticWriteObservation observation)
    {
        var note = observation.Notes ?? string.Empty;
        return note.Contains("401", StringComparison.OrdinalIgnoreCase)
               || note.Contains("403", StringComparison.OrdinalIgnoreCase)
               || note.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || note.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
               || note.Contains("digest", StringComparison.OrdinalIgnoreCase) && note.Contains("failed", StringComparison.OrdinalIgnoreCase)
               || note.Contains("auth", StringComparison.OrdinalIgnoreCase) && note.Contains("missing", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ClassificationDecision(HiddenCandidateClassification Classification, IReadOnlyCollection<string> ReasonCodes);

    private static IReadOnlyCollection<JsonNode?> BuildCandidates(JsonNode? baseline, ContractField field)
    {
        if (field.Kind == ContractFieldKind.Enum)
        {
            var current = baseline?.ToJsonString().Trim('"');
            return field.EnumValues.Select(static value => value.Value)
                .Where(value => !string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToList();
        }

        var number = TryToDecimal(baseline);
        if (field.Kind is ContractFieldKind.Integer or ContractFieldKind.Number && number is decimal currentNumber)
        {
            var min = field.Validation.Min ?? Math.Max(0, currentNumber - 5);
            var max = field.Validation.Max ?? currentNumber + 5;
            var step = field.Kind == ContractFieldKind.Integer ? 1m : 2m;
            var candidates = new List<decimal>
            {
                Clamp(currentNumber - step, min, max),
                Clamp(currentNumber + step, min, max),
                Clamp(currentNumber - step * 2, min, max),
                Clamp(currentNumber + step * 2, min, max)
            }.Distinct().ToList();
            return candidates.Select(value => (JsonNode?)JsonValue.Create(field.Kind == ContractFieldKind.Integer ? (int)value : value)).ToList();
        }

        return [];
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
        => Math.Min(max, Math.Max(min, value));

    private async Task<JsonNode?> ReadCurrentFieldValueAsync(Guid deviceId, string fieldKey, CancellationToken cancellationToken)
    {
        var groups = await typedSettingsService.NormalizeDeviceAsync(deviceId, refreshFromDevice: true, cancellationToken);
        return groups.Where(static group => group.GroupKind == TypedSettingGroupKind.VideoImage)
            .SelectMany(static group => group.Fields)
            .Where(field => field.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static field => field.CapturedAt)
            .Select(static field => field.TypedValue?.DeepClone())
            .FirstOrDefault();
    }

    private static ImageBehaviorClass ClassifyBehavior(
        string fieldKey,
        JsonNode? baseline,
        JsonNode? immediate,
        JsonNode? delayed1,
        JsonNode? delayed3,
        JsonNode? delayed5,
        SemanticWriteStatus semantic)
    {
        if (semantic is SemanticWriteStatus.TransportFailed or SemanticWriteStatus.Rejected)
        {
            return ImageBehaviorClass.Rejected;
        }

        if (semantic == SemanticWriteStatus.AcceptedClamped)
        {
            return ImageBehaviorClass.Clamped;
        }

        if (semantic == SemanticWriteStatus.AcceptedNoChange)
        {
            return ImageBehaviorClass.Ignored;
        }

        var baseNum = TryToDecimal(baseline);
        var nowNum = TryToDecimal(immediate);
        if (baseNum is null || nowNum is null)
        {
            return semantic is SemanticWriteStatus.AcceptedChanged ? ImageBehaviorClass.ModerateChange : ImageBehaviorClass.Uncertain;
        }

        var delta = Math.Abs(nowNum.Value - baseNum.Value);
        if (delta == 0)
        {
            return ImageBehaviorClass.NoObservableChange;
        }

        if (fieldKey is "brightness" or "contrast")
        {
            if (nowNum.Value <= 5)
            {
                return ImageBehaviorClass.CatastrophicDark;
            }

            if (nowNum.Value >= 95)
            {
                return ImageBehaviorClass.CatastrophicBright;
            }
        }

        if (TryToDecimal(delayed1) is decimal d1 && TryToDecimal(delayed3) is decimal d3 && TryToDecimal(delayed5) is decimal d5)
        {
            if (d1 != d3 && d3 == d5)
            {
                return ImageBehaviorClass.TemporarySpikeThenSettles;
            }
            if (d1 != d3 && d3 != d5)
            {
                return ImageBehaviorClass.Unstable;
            }
        }

        if (delta <= 1)
        {
            return ImageBehaviorClass.MinorChange;
        }
        if (delta <= 4)
        {
            return ImageBehaviorClass.ModerateChange;
        }

        return ImageBehaviorClass.ThresholdJump;
    }

    private static ImageCommitBehavior ClassifyCommitBehavior(
        JsonNode? baseline,
        JsonNode? intended,
        JsonNode? immediate,
        JsonNode? delayed1,
        JsonNode? delayed3,
        JsonNode? delayed5,
        SemanticWriteStatus semantic)
    {
        if (JsonNode.DeepEquals(immediate, intended))
        {
            return ImageCommitBehavior.ImmediateApplied;
        }
        if (JsonNode.DeepEquals(delayed1, intended) || JsonNode.DeepEquals(delayed3, intended) || JsonNode.DeepEquals(delayed5, intended))
        {
            return ImageCommitBehavior.DelayedApplied;
        }
        if (semantic == SemanticWriteStatus.AcceptedNoChange && JsonNode.DeepEquals(immediate, baseline))
        {
            return ImageCommitBehavior.StoredOnly;
        }
        if (semantic == SemanticWriteStatus.AcceptedChangedThenReverted)
        {
            return ImageCommitBehavior.RequiresSecondaryTrigger;
        }
        if (semantic == SemanticWriteStatus.LostAfterReboot)
        {
            return ImageCommitBehavior.RequiresReboot;
        }
        return ImageCommitBehavior.Unknown;
    }

    private static OperationalImageMetric BuildOperationalMetric(string fieldKey, JsonNode? baseline, JsonNode? immediate)
    {
        var before = TryToDouble(baseline);
        var after = TryToDouble(immediate);
        if (before is null || after is null)
        {
            return new OperationalImageMetric
            {
                CaptureMode = "unavailable",
                Notes = "Preview/snapshot operational metric is not available for this endpoint flow."
            };
        }

        var blackBefore = before.Value <= 5 ? 1d : 0d;
        var blackAfter = after.Value <= 5 ? 1d : 0d;
        var whiteBefore = before.Value >= 95 ? 1d : 0d;
        var whiteAfter = after.Value >= 95 ? 1d : 0d;
        var spreadBefore = fieldKey.Equals("contrast", StringComparison.OrdinalIgnoreCase) ? before.Value : Math.Abs(before.Value - 50);
        var spreadAfter = fieldKey.Equals("contrast", StringComparison.OrdinalIgnoreCase) ? after.Value : Math.Abs(after.Value - 50);
        return new OperationalImageMetric
        {
            CaptureMode = "value-proxy",
            LuminanceMeanBefore = before,
            LuminanceMeanAfter = after,
            ContrastSpreadBefore = spreadBefore,
            ContrastSpreadAfter = spreadAfter,
            BlackClipBefore = blackBefore,
            BlackClipAfter = blackAfter,
            WhiteClipBefore = whiteBefore,
            WhiteClipAfter = whiteAfter,
            Notes = "Proxy metrics derived from accepted image control values; raw frame histogram capture not exposed by current transport."
        };
    }

    private static ImageFieldBehaviorMap BuildBehaviorMap(Guid deviceId, string firmware, ImageWritableTestCase test, IReadOnlyCollection<ImageBehaviorPoint> points)
    {
        var acceptedNumbers = points
            .Where(point => point.BehaviorClass is ImageBehaviorClass.MinorChange or ImageBehaviorClass.ModerateChange or ImageBehaviorClass.NoObservableChange)
            .Select(point => TryToDecimal(point.ImmediateValue))
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .ToList();
        var thresholds = points
            .Where(point => point.BehaviorClass is ImageBehaviorClass.ThresholdJump)
            .Select(point => TryToDecimal(point.ImmediateValue))
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .Distinct()
            .OrderBy(static value => value)
            .ToList();
        var catastrophic = points
            .Where(point => point.BehaviorClass is ImageBehaviorClass.CatastrophicBright or ImageBehaviorClass.CatastrophicDark)
            .Select(point => TryToDecimal(point.ImmediateValue))
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .Distinct()
            .OrderBy(static value => value)
            .ToList();

        var safeMin = acceptedNumbers.Count == 0 ? (decimal?)null : acceptedNumbers.Min();
        var safeMax = acceptedNumbers.Count == 0 ? (decimal?)null : acceptedNumbers.Max();
        var recommended = safeMin is not null && safeMax is not null ? $"{safeMin:0.##}-{safeMax:0.##}" : "unverified";
        var trigger = points.Any(point => point.CommitBehavior == ImageCommitBehavior.RequiresSecondaryTrigger)
            ? "secondary-setting-toggle"
            : points.Any(point => point.CommitBehavior == ImageCommitBehavior.DelayedApplied)
                ? "delayed-commit-observed"
                : "none-observed";
        var truth = points.Any(point => point.BehaviorClass is not ImageBehaviorClass.Uncertain && point.SemanticStatus is not SemanticWriteStatus.Unverified)
            ? ContractTruthState.Proven
            : ContractTruthState.Inferred;

        return new ImageFieldBehaviorMap
        {
            DeviceId = deviceId,
            FirmwareFingerprint = firmware,
            FieldKey = test.FieldKey,
            DisplayName = test.DisplayName,
            ContractKey = test.ContractKey,
            SourceEndpoint = test.SourceEndpoint,
            SourcePath = test.SourcePath,
            Points = points,
            SafeMin = safeMin,
            SafeMax = safeMax,
            Thresholds = thresholds,
            CatastrophicValues = catastrophic,
            RecommendedRange = recommended,
            TriggerSequence = trigger,
            TruthState = truth,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static decimal? TryToDecimal(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

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

        var raw = node.ToJsonString().Trim('"');
        return decimal.TryParse(raw, out var fallback) ? fallback : null;
    }

    private static double? TryToDouble(JsonNode? node)
    {
        var num = TryToDecimal(node);
        return num is null ? null : (double)num.Value;
    }

    private async Task ExportArtifactsAsync(
        DeviceIdentity device,
        IReadOnlyCollection<ImageControlInventoryItem> inventory,
        ImageWritableTestSetProfile? testSet,
        IReadOnlyCollection<ImageFieldBehaviorMap> maps,
        string exportRoot,
        CancellationToken cancellationToken)
    {
        var firmware = BuildFirmwareFingerprint(device, inventory).Replace('|', '_');
        var baseDir = Path.Combine(exportRoot, "5523w", firmware);
        Directory.CreateDirectory(baseDir);
        await File.WriteAllTextAsync(Path.Combine(baseDir, "image-inventory.json"), JsonSerializer.Serialize(inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(baseDir, "image-writable-test-set.json"), JsonSerializer.Serialize(testSet, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(baseDir, "image-behavior-maps.json"), JsonSerializer.Serialize(maps, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
    }

    private static string BuildSummary(IReadOnlyCollection<ImageControlInventoryItem> inventory, ImageWritableTestSetProfile? testSet, IReadOnlyCollection<ImageFieldBehaviorMap> maps)
    {
        var readable = inventory.Count(item => item.Status == ImageInventoryStatus.Readable);
        var writable = inventory.Count(item => item.Status == ImageInventoryStatus.Writable);
        var blocked = inventory.Count(item => item.Status == ImageInventoryStatus.Blocked);
        var thresholds = maps.Sum(map => map.Thresholds.Count);
        return $"Inventory: readable={readable}, writable={writable}, blocked={blocked}. TestCases={testSet?.Cases.Count ?? 0}. ThresholdMarkers={thresholds}.";
    }

    private async Task<IReadOnlyCollection<ImageFieldBehaviorMap>> BuildBehaviorMapsFromFixturesAsync(DeviceIdentity device, ImageWritableTestSetProfile? testSet, CancellationToken cancellationToken)
    {
        var fixtureRows = await LoadFixtureRowsForIpAsync(device.IpAddress, cancellationToken);
        if (fixtureRows.Count == 0)
        {
            return [];
        }

        var firmware = testSet?.FirmwareFingerprint ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";
        var maps = new List<ImageFieldBehaviorMap>();
        foreach (var group in fixtureRows.GroupBy(row => NormalizeFixtureFieldKey(row.Field), StringComparer.OrdinalIgnoreCase))
        {
            var points = group.Select(row => new ImageBehaviorPoint
            {
                AttemptedValue = JsonValue.Create(row.Attempt),
                BaselineValue = JsonValue.Create(row.Orig),
                ImmediateValue = JsonValue.Create(row.Actual),
                Delayed1sValue = JsonValue.Create(row.Actual),
                Delayed3sValue = JsonValue.Create(row.Actual),
                Delayed5sValue = JsonValue.Create(row.Actual),
                SemanticStatus = row.Verified ? SemanticWriteStatus.AcceptedChanged : SemanticWriteStatus.AcceptedNoChange,
                BehaviorClass = row.Verified ? ImageBehaviorClass.MinorChange : ImageBehaviorClass.Ignored,
                CommitBehavior = row.Verified ? ImageCommitBehavior.ImmediateApplied : ImageCommitBehavior.Unknown,
                OperationalMetric = BuildOperationalMetric(group.Key, JsonValue.Create(row.Orig), JsonValue.Create(row.Actual)),
                Notes = $"fixture-status={row.Status}"
            }).ToList();

            var test = testSet?.Cases.FirstOrDefault(item => item.FieldKey.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                ?? new ImageWritableTestCase
                {
                    FieldKey = group.Key,
                    DisplayName = group.Key,
                    ContractKey = "fixture-proven",
                    SourceEndpoint = group.First().Endpoint ?? "/netsdk/video/input/channel/1",
                    SourcePath = "$." + group.First().Field
                };
            maps.Add(BuildBehaviorMap(device.Id, firmware, test, points));
        }

        return maps;
    }

    private sealed record FixtureBehaviorRow(string Field, decimal Orig, decimal Attempt, decimal Actual, bool Verified, int Status, string? Endpoint);
    private sealed record LiveSemanticEvidenceRow(
        string Field,
        string Phase,
        bool Readable,
        string Status,
        string? Endpoint,
        string? Method,
        string? Classification,
        string? ReasonCode);

    private async Task<List<FixtureBehaviorRow>> LoadFixtureRowsForIpAsync(string? ipAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return [];
        }

        var files = ResolveFixtureFiles();
        var rows = new List<FixtureBehaviorRow>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken)) as JsonArray;
                if (root is null)
                {
                    continue;
                }

                foreach (var node in root.OfType<JsonObject>())
                {
                    var ip = node["ip"]?.GetValue<string>();
                    if (!string.Equals(ip, ipAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var field = node["field"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        continue;
                    }

                    if (!TryToDecimal(node["orig"], out var orig) || !TryToDecimal(node["attempt"], out var attempt) || !TryToDecimal(node["actual"], out var actual))
                    {
                        continue;
                    }

                    var verified = node["verified"]?.GetValue<bool>() == true;
                    var status = node["status"]?.GetValue<int?>() ?? node["writeStatus"]?.GetValue<int?>() ?? 0;
                    var endpoint = node["endpoint"]?.GetValue<string>();
                    rows.Add(new FixtureBehaviorRow(field, orig, attempt, actual, verified, status, endpoint));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse fixture file {FixtureFile}", file);
            }
        }

        return rows;
    }

    private static IReadOnlyCollection<string> ResolveFixtureFiles()
    {
        var roots = new List<string>();
        var current = Directory.GetCurrentDirectory();
        roots.Add(current);
        roots.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")));
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate1 = Path.Combine(root, "fixtures", "5523w", "firmware-unknown", "2026-04-19-video-extended-results.json");
            var candidate2 = Path.Combine(root, "fixtures", "5523w", "firmware-unknown", "2026-04-19-topgroup-write-readback.json");
            if (File.Exists(candidate1))
            {
                files.Add(candidate1);
            }
            if (File.Exists(candidate2))
            {
                files.Add(candidate2);
            }
        }

        return files.ToList();
    }

    private static IReadOnlyCollection<string> ResolveLiveSemanticEvidenceFiles()
    {
        var roots = new List<string>();
        var current = Directory.GetCurrentDirectory();
        roots.Add(current);
        roots.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")));
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var evidenceDir = Path.Combine(root, "artifacts", "5523w");
            if (!Directory.Exists(evidenceDir))
            {
                continue;
            }

            foreach (var path in Directory.GetFiles(evidenceDir, "live-image-targeted-semantic-*.json", SearchOption.TopDirectoryOnly))
            {
                files.Add(path);
            }
            foreach (var path in Directory.GetFiles(evidenceDir, "encode-shape-matrix-*.json", SearchOption.TopDirectoryOnly))
            {
                files.Add(path);
            }
            foreach (var path in Directory.GetFiles(evidenceDir, "encode-shape-summary-*.json", SearchOption.TopDirectoryOnly))
            {
                files.Add(path);
            }
        }

        return files.ToList();
    }

    private async Task<List<LiveSemanticEvidenceRow>> LoadLiveSemanticEvidenceRowsForIpAsync(string? ipAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return [];
        }

        var files = ResolveLiveSemanticEvidenceFiles();
        var rows = new List<LiveSemanticEvidenceRow>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken)) as JsonArray;
                if (root is null)
                {
                    continue;
                }

                foreach (var node in root.OfType<JsonObject>())
                {
                    var ip = node["ip"]?.GetValue<string>();
                    if (!string.Equals(ip, ipAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var field = node["field"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        continue;
                    }

                    var phase = node["phase"]?.GetValue<string>() ?? "read";
                    var status = node["status"]?.GetValue<string>() ?? "Unknown";
                    var readable = node["readable"]?.GetValue<bool>() == true;
                    var endpoint = node["endpoint"]?.GetValue<string>();
                    var method = node["method"]?.GetValue<string>();
                    var classification = node["classification"]?.GetValue<string>();
                    var reasonCode = node["reasonCode"]?.GetValue<string>();
                    rows.Add(new LiveSemanticEvidenceRow(field, phase, readable, status, endpoint, method, classification, reasonCode));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse live semantic evidence file {EvidenceFile}", file);
            }
        }

        return rows;
    }

    private static List<ImageControlInventoryItem> ApplyLiveEvidenceOverrides(List<ImageControlInventoryItem> inventory, IReadOnlyCollection<LiveSemanticEvidenceRow> evidenceRows)
    {
        if (inventory.Count == 0 || evidenceRows.Count == 0)
        {
            return inventory;
        }

        var output = new List<ImageControlInventoryItem>(inventory.Count);
        foreach (var item in inventory)
        {
            var fieldEvidence = evidenceRows
                .Where(row => row.Field.Equals(item.FieldKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fieldEvidence.Count == 0)
            {
                output.Add(item);
                continue;
            }

            var explicitClassifications = fieldEvidence
                .Where(static row => !string.IsNullOrWhiteSpace(row.Classification))
                .Select(static row => row.Classification!)
                .ToList();
            var hasReadable = fieldEvidence.Any(static row => row.Phase.Equals("read", StringComparison.OrdinalIgnoreCase) && row.Readable);
            var writeStatuses = fieldEvidence
                .Where(static row => row.Phase.Equals("write", StringComparison.OrdinalIgnoreCase))
                .Select(static row => row.Status)
                .ToList();
            var hasWritable = writeStatuses.Any(static status => status.Equals("Writable", StringComparison.OrdinalIgnoreCase));
            var hasIgnored = writeStatuses.Any(static status => status.Equals("Ignored", StringComparison.OrdinalIgnoreCase));
            var hasRejected = writeStatuses.Any(static status => status.Equals("Rejected", StringComparison.OrdinalIgnoreCase));
            var hasPrivate = fieldEvidence.Any(static row => row.Status.Equals("PrivatePathCandidate", StringComparison.OrdinalIgnoreCase));

            var updated = item;
            if (explicitClassifications.Count > 0)
            {
                updated = ApplyExplicitClassification(updated, explicitClassifications, fieldEvidence);
                output.Add(updated);
                continue;
            }
            if (hasWritable)
            {
                updated = updated with
                {
                    Readable = true,
                    Writable = true,
                    WriteVerified = true,
                    SupportState = ContractSupportState.Supported,
                    TruthState = ContractTruthState.Proven,
                    Status = ImageInventoryStatus.Writable,
                    CandidateClassification = HiddenCandidateClassification.Writable,
                    PromotedToUi = true,
                    ReasonCodes = ["live_semantic_write_readback"],
                    Notes = "Live semantic write/readback verified."
                };
            }
            else if (hasIgnored)
            {
                updated = updated with
                {
                    Readable = hasReadable || item.Readable,
                    Writable = false,
                    WriteVerified = false,
                    SupportState = ContractSupportState.Uncertain,
                    TruthState = ContractTruthState.Inferred,
                    Status = ImageInventoryStatus.TransportSuccessNoSemanticChange,
                    CandidateClassification = HiddenCandidateClassification.Ignored,
                    PromotedToUi = false,
                    ReasonCodes = ["transport_success_no_semantic_change"],
                    Notes = "Write accepted but semantic readback unchanged in live probe."
                };
            }
            else if (hasRejected)
            {
                updated = updated with
                {
                    Readable = hasReadable || item.Readable,
                    Writable = false,
                    WriteVerified = false,
                    SupportState = ContractSupportState.Uncertain,
                    TruthState = ContractTruthState.Inferred,
                    Status = ImageInventoryStatus.Blocked,
                    CandidateClassification = HiddenCandidateClassification.RejectedByFirmware,
                    PromotedToUi = false,
                    ReasonCodes = ["repeated_live_rejection"],
                    Notes = "Live probe returned explicit write rejection."
                };
            }
            else if (hasPrivate)
            {
                updated = updated with
                {
                    Readable = hasReadable || item.Readable,
                    Writable = false,
                    WriteVerified = false,
                    SupportState = ContractSupportState.Uncertain,
                    TruthState = ContractTruthState.Inferred,
                    Status = ImageInventoryStatus.HiddenAdjacentCandidate,
                    CandidateClassification = HiddenCandidateClassification.PrivatePathCandidate,
                    PromotedToUi = false,
                    ReasonCodes = ["private_path_candidate"],
                    Notes = "Live probe observed private/OEM candidate path."
                };
            }
            else if (hasReadable)
            {
                updated = updated with
                {
                    Readable = true,
                    Writable = false,
                    WriteVerified = false,
                    SupportState = ContractSupportState.Uncertain,
                    TruthState = ContractTruthState.Inferred,
                    Status = ImageInventoryStatus.Readable,
                    CandidateClassification = HiddenCandidateClassification.ReadableOnly,
                    PromotedToUi = true,
                    ReasonCodes = ["live_read_probe"],
                    Notes = "Field observed in live readable payload."
                };
            }

            output.Add(updated);
        }

        return output;
    }

    private static ImageControlInventoryItem ApplyExplicitClassification(
        ImageControlInventoryItem current,
        IReadOnlyCollection<string> classifications,
        IReadOnlyCollection<LiveSemanticEvidenceRow> evidenceRows)
    {
        HiddenCandidateClassification? Pick(params HiddenCandidateClassification[] ordered)
        {
            foreach (var candidate in ordered)
            {
                if (classifications.Any(value => value.Equals(candidate.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }

            return null;
        }

        var chosen = Pick(
            HiddenCandidateClassification.Writable,
            HiddenCandidateClassification.RequiresCommitTrigger,
            HiddenCandidateClassification.AltWriteShapeRequired,
            HiddenCandidateClassification.Ignored,
            HiddenCandidateClassification.RejectedByFirmware,
            HiddenCandidateClassification.PrivatePathCandidate,
            HiddenCandidateClassification.LikelyUnsupported,
            HiddenCandidateClassification.UnsupportedOnFirmware,
            HiddenCandidateClassification.ReadableOnly,
            HiddenCandidateClassification.NoSemanticProof,
            HiddenCandidateClassification.HiddenAdjacentCandidate,
            HiddenCandidateClassification.Uncertain) ?? HiddenCandidateClassification.Uncertain;
        var reasons = evidenceRows
            .Select(static row => row.ReasonCode)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        var endpoint = evidenceRows
            .Select(static row => row.Endpoint)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? current.SourceEndpoint;
        var method = evidenceRows
            .Select(static row => row.Method)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var notes = method is null
            ? $"Live targeted classification={chosen}"
            : $"Live targeted classification={chosen} via {method}";

        return chosen switch
        {
            HiddenCandidateClassification.Writable => current with
            {
                Readable = true,
                Writable = true,
                WriteVerified = true,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Supported,
                TruthState = ContractTruthState.Proven,
                Status = ImageInventoryStatus.Writable,
                CandidateClassification = chosen,
                PromotedToUi = true,
                ReasonCodes = reasons.Length == 0 ? ["live_semantic_write_readback"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.ReadableOnly => current with
            {
                Readable = true,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.Readable,
                CandidateClassification = chosen,
                PromotedToUi = true,
                ReasonCodes = reasons.Length == 0 ? ["live_read_probe"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.RequiresCommitTrigger => current with
            {
                Readable = true,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.TransportSuccessNoSemanticChange,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["requires_commit_trigger"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.AltWriteShapeRequired => current with
            {
                Readable = true,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.Uncertain,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["alt_write_shape_required"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.Ignored => current with
            {
                Readable = true,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.TransportSuccessNoSemanticChange,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["transport_success_no_semantic_change"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.PrivatePathCandidate => current with
            {
                Readable = true,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.HiddenAdjacentCandidate,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["private_path_candidate"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.RejectedByFirmware => current with
            {
                Readable = current.Readable,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.Blocked,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["repeated_live_rejection"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.LikelyUnsupported => current with
            {
                Readable = current.Readable,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.Uncertain,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["repeated_live_failure_incomplete_evidence"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.UnsupportedOnFirmware => current with
            {
                Readable = false,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Unsupported,
                TruthState = ContractTruthState.Inferred,
                Status = ImageInventoryStatus.Blocked,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["repeated_live_failure_no_adjacent_evidence"] : reasons,
                Notes = notes
            },
            HiddenCandidateClassification.NoSemanticProof => current with
            {
                Readable = current.Readable,
                Writable = false,
                WriteVerified = false,
                SourceEndpoint = endpoint,
                SupportState = ContractSupportState.Uncertain,
                TruthState = ContractTruthState.Unverified,
                Status = ImageInventoryStatus.Uncertain,
                CandidateClassification = chosen,
                PromotedToUi = false,
                ReasonCodes = reasons.Length == 0 ? ["no_semantic_proof"] : reasons,
                Notes = notes
            },
            _ => current with
            {
                SourceEndpoint = endpoint,
                CandidateClassification = chosen,
                ReasonCodes = reasons.Length == 0 ? current.ReasonCodes : reasons,
                Notes = notes
            }
        };
    }

    private static string NormalizeFixtureFieldKey(string raw)
        => FixtureFieldAlias.TryGetValue(raw, out var mapped) ? mapped : raw;

    private static bool TryToDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<decimal>(out value))
            {
                return true;
            }
            if (jsonValue.TryGetValue<double>(out var d))
            {
                value = Convert.ToDecimal(d);
                return true;
            }
            if (jsonValue.TryGetValue<int>(out var i))
            {
                value = i;
                return true;
            }
            if (jsonValue.TryGetValue<string>(out var s) && decimal.TryParse(s, out value))
            {
                return true;
            }
        }

        return decimal.TryParse(node.ToJsonString().Trim('"'), out value);
    }
}
