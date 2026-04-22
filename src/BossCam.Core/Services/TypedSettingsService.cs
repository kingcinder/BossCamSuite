using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class TypedSettingsService(
    IApplicationStore store,
    SettingsService settingsService,
    PersistenceVerificationService persistenceVerificationService,
    SemanticTrustService semanticTrustService,
    IEndpointContractCatalog contractCatalog,
    ILogger<TypedSettingsService> logger)
{
    public async Task<IReadOnlyCollection<TypedSettingGroupSnapshot>> NormalizeDeviceAsync(Guid deviceId, bool refreshFromDevice, CancellationToken cancellationToken)
    {
        if (refreshFromDevice)
        {
            _ = await settingsService.ReadAsync(deviceId, cancellationToken);
        }

        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var snapshot = await settingsService.GetLastSnapshotAsync(deviceId, cancellationToken);
        if (snapshot is null)
        {
            return [];
        }

        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var validations = (await store.GetEndpointValidationResultsAsync(deviceId, cancellationToken))
            .GroupBy(static result => NormalizeEndpoint(result.Endpoint), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(result => result.CapturedAt).First(), StringComparer.OrdinalIgnoreCase);

        var normalized = new List<NormalizedSettingField>();
        foreach (var group in snapshot.Groups)
        {
            foreach (var value in group.Values.Values)
            {
                if (IsSemanticErrorPayload(value.Value))
                {
                    continue;
                }

                var endpoint = NormalizeEndpoint(value.SourceEndpoint ?? value.Key);
                var matched = contracts.Where(contract => EndpointPatternMatches(contract.Endpoint, endpoint)).ToList();
                if (matched.Count == 0)
                {
                    normalized.Add(new NormalizedSettingField
                    {
                        DeviceId = deviceId,
                        GroupKind = TypedSettingGroupKind.Diagnostics,
                        GroupName = "Diagnostics",
                        FieldKey = $"unmapped:{endpoint}",
                        DisplayName = $"Unmapped Endpoint {endpoint}",
                        AdapterName = snapshot.AdapterName,
                        ParserName = "ContractMissing",
                        SourceEndpoint = endpoint,
                        RawSourcePath = "$.",
                        RawValue = value.Value?.DeepClone(),
                        TypedValue = value.Value?.DeepClone(),
                        Validity = FieldValidityState.Invalid,
                        SupportState = ContractSupportState.Unsupported,
                        TruthState = ContractTruthState.Unverified,
                        Confidence = "none"
                    });
                    continue;
                }

                foreach (var contract in matched)
                {
                    var method = contract.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "GET" : contract.Method;
                    var validation = validations.GetValueOrDefault(endpoint);
                    foreach (var field in contract.Fields)
                    {
                        var rawNode = TryGetPathValue(value.Value, field.SourcePath);
                        if (rawNode is null)
                        {
                            if (field.Required)
                            {
                                normalized.Add(BuildInvalidField(deviceId, snapshot.AdapterName, endpoint, contract, field, "Required path missing"));
                            }
                            continue;
                        }

                        var conversion = ConvertValue(rawNode, field);
                        if (!conversion.Success)
                        {
                            normalized.Add(BuildInvalidField(deviceId, snapshot.AdapterName, endpoint, contract, field, conversion.Message, rawNode));
                            continue;
                        }

                        normalized.Add(new NormalizedSettingField
                        {
                            DeviceId = deviceId,
                            GroupKind = contract.GroupKind,
                            GroupName = contract.GroupName,
                            FieldKey = field.Key,
                            DisplayName = field.DisplayName,
                            AdapterName = snapshot.AdapterName,
                            ParserName = $"Contract:{contract.ContractKey}:{method}",
                            TypedValue = conversion.Value,
                            RawValue = rawNode.DeepClone(),
                            SourceEndpoint = endpoint,
                            RawSourcePath = field.SourcePath,
                            ContractKey = contract.ContractKey,
                            FirmwareFingerprint = validation?.FirmwareFingerprint ?? BuildFirmwareFingerprint(device),
                            Validity = ResolveValidity(validation, field),
                            Confidence = ResolveConfidence(validation, field),
                            TruthState = field.Evidence.TruthState,
                            SupportState = ResolveSupportState(validation, field),
                            ReadVerified = validation?.ReadVerified == true,
                            WriteVerified = validation?.WriteVerified == true && field.Writable,
                            PersistsAfterReboot = validation?.PersistsAfterReboot == true,
                            PersistenceExpectedAfterReboot = field.PersistExpectedAfterReboot || contract.PersistenceExpectedAfterReboot,
                            ExpertOnly = contract.ExpertOnly || field.ExpertOnly,
                            DisruptionClass = field.DisruptionClass != DisruptionClass.Unknown ? field.DisruptionClass : contract.DisruptionClass
                        });
                    }
                }
            }
        }

        var deduped = normalized
            .GroupBy(field => $"{field.DeviceId:N}:{field.SourceEndpoint}:{field.FieldKey}:{field.ContractKey}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(field => field.CapturedAt).ThenByDescending(field => field.ReadVerified).ThenByDescending(field => field.WriteVerified).First())
            .ToList();

        await store.SaveNormalizedSettingFieldsAsync(deduped, cancellationToken);
        logger.LogInformation("Contract-driven normalization produced {Count} fields for {DeviceId}", deduped.Count, deviceId);
        var firmware = deduped.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var constraints = await store.GetFieldConstraintProfilesAsync(firmware, cancellationToken);
        var dependencies = await store.GetDependencyMatrixProfilesAsync(firmware, cancellationToken);
        return ToSnapshots(deviceId, snapshot.AdapterName, deduped, contracts, constraints, dependencies);
    }

    public async Task<IReadOnlyCollection<TypedSettingGroupSnapshot>> GetTypedSettingsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        var contracts = device is null ? [] : await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var firmware = fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var constraints = await store.GetFieldConstraintProfilesAsync(firmware, cancellationToken);
        var dependencies = await store.GetDependencyMatrixProfilesAsync(firmware, cancellationToken);
        return ToSnapshots(deviceId, string.Empty, fields, contracts, constraints, dependencies);
    }

    public async Task<WriteResult?> ApplyTypedFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool expertOverride, CancellationToken cancellationToken)
    {
        var results = await ApplyTypedChangesAsync(deviceId, [new TypedFieldChange(fieldKey, value)], expertOverride, cancellationToken);
        return results.FirstOrDefault();
    }

    public async Task<IReadOnlyCollection<WriteResult>> ApplyTypedChangesAsync(Guid deviceId, IReadOnlyCollection<TypedFieldChange> changes, bool expertOverride, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var latestFields = fields
            .GroupBy(field => field.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(field => field.CapturedAt).First(), StringComparer.OrdinalIgnoreCase);

        var resolvedChanges = new List<(NormalizedSettingField Field, EndpointContract Contract, ContractField ContractField, JsonNode? Value)>();
        var blocked = new List<WriteResult>();

        foreach (var change in changes)
        {
            if (!latestFields.TryGetValue(change.FieldKey, out var field))
            {
                blocked.Add(new WriteResult { Success = false, Message = $"Unknown field '{change.FieldKey}'.", SemanticStatus = SemanticWriteStatus.ContractViolation });
                continue;
            }

            if (!expertOverride && (!field.WriteVerified || field.SupportState != ContractSupportState.Supported || field.Validity == FieldValidityState.Invalid))
            {
                blocked.Add(new WriteResult
                {
                    Success = false,
                    Message = $"Field '{change.FieldKey}' blocked: write is not proven and supported.",
                    SemanticStatus = SemanticWriteStatus.ContractViolation,
                    ContractKey = field.ContractKey,
                    ContractViolations = ["Write verification missing or field unsupported"]
                });
                continue;
            }

            var contract = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase)
                && EndpointPatternMatches(candidate.Endpoint, field.SourceEndpoint));
            if (contract is null)
            {
                blocked.Add(new WriteResult { Success = false, Message = $"No endpoint contract matched field '{change.FieldKey}'.", SemanticStatus = SemanticWriteStatus.ContractViolation, ContractKey = field.ContractKey });
                continue;
            }

            var contractField = contract.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
            if (contractField is null)
            {
                blocked.Add(new WriteResult { Success = false, Message = $"Contract field mapping missing for '{change.FieldKey}'.", SemanticStatus = SemanticWriteStatus.ContractViolation, ContractKey = contract.ContractKey });
                continue;
            }

            var converted = ConvertValue(change.Value ?? JsonValue.Create(string.Empty), contractField);
            if (!converted.Success)
            {
                blocked.Add(new WriteResult
                {
                    Success = false,
                    Message = converted.Message,
                    SemanticStatus = SemanticWriteStatus.ContractViolation,
                    ContractKey = contract.ContractKey,
                    ContractViolations = [converted.Message ?? "invalid value"]
                });
                continue;
            }

            resolvedChanges.Add((field, contract, contractField, converted.Value));
        }

        var writes = new List<WriteResult>(blocked);
        var grouped = resolvedChanges.GroupBy(item => $"{item.Field.SourceEndpoint}|{item.Contract.ContractKey}", StringComparer.OrdinalIgnoreCase);
        foreach (var group in grouped)
        {
            var seed = group.First();
            var endpointFields = fields
                .Where(candidate => candidate.SourceEndpoint.Equals(seed.Field.SourceEndpoint, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.ContractKey, seed.Contract.ContractKey, StringComparison.OrdinalIgnoreCase))
                .GroupBy(candidate => candidate.FieldKey, StringComparer.OrdinalIgnoreCase)
                .Select(static fieldGroup => fieldGroup.OrderByDescending(candidate => candidate.CapturedAt).First())
                .ToList();

            var changeMap = group.ToDictionary(item => item.Field.FieldKey, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var payloadBuild = await BuildContractPayloadAsync(deviceId, seed.Field.SourceEndpoint, seed.Contract, endpointFields, changeMap, expertOverride, cancellationToken);
            if (!payloadBuild.Success || payloadBuild.Payload is null)
            {
                writes.Add(new WriteResult
                {
                    Success = false,
                    Message = payloadBuild.Message,
                    SemanticStatus = SemanticWriteStatus.ContractViolation,
                    ContractKey = seed.Contract.ContractKey,
                    ContractViolations = payloadBuild.Validation.Errors
                });
                continue;
            }

            var plan = new WritePlan
            {
                GroupName = seed.Contract.GroupName,
                Endpoint = seed.Field.SourceEndpoint,
                Method = seed.Contract.Method,
                AdapterName = seed.Field.AdapterName,
                Payload = payloadBuild.Payload,
                SnapshotBeforeWrite = true,
                RequireWriteVerification = !expertOverride,
                ContractKey = seed.Contract.ContractKey,
                SensitivePaths = seed.Contract.Fields.Where(static item => item.Validation.Sensitive).Select(static item => item.SourcePath).ToList()
            };

            var write = await settingsService.WriteAsync(deviceId, plan, cancellationToken);
            if (write is null)
            {
                continue;
            }

            if (!write.Success)
            {
                writes.Add(write with
                {
                    SemanticStatus = write.StatusCode is >= 400 ? SemanticWriteStatus.Rejected : SemanticWriteStatus.TransportFailed,
                    ContractKey = seed.Contract.ContractKey,
                    ContractViolations = payloadBuild.Validation.Errors
                });
                continue;
            }

            var perFieldStatuses = new List<string>();
            var endpointSemantic = SemanticWriteStatus.AcceptedChanged;
            var semanticContext = BuildSemanticContext(fields);
            foreach (var item in group)
            {
                var baseline = TryGetPathValue(write.PreWriteValue, item.ContractField.SourcePath);
                var actual = TryGetPathValue(write.PostWriteValue, item.ContractField.SourcePath);
                JsonNode? delayed = null;
                if (item.Contract.DisruptionClass == DisruptionClass.Safe || item.ContractField.DisruptionClass == DisruptionClass.Safe)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    var delayedSnapshot = await settingsService.ReadAsync(deviceId, cancellationToken);
                    var delayedEndpoint = delayedSnapshot?.Groups.SelectMany(static value => value.Values.Values)
                        .FirstOrDefault(candidate => NormalizeEndpoint(candidate.SourceEndpoint ?? candidate.Key).Equals(NormalizeEndpoint(seed.Field.SourceEndpoint), StringComparison.OrdinalIgnoreCase));
                    delayed = TryGetPathValue(delayedEndpoint?.Value, item.ContractField.SourcePath);
                }

                var observation = await semanticTrustService.CaptureObservationAsync(
                    deviceId,
                    item.Contract,
                    item.ContractField,
                    write,
                    item.Value,
                    baseline,
                    actual,
                    delayed,
                    null,
                    semanticContext,
                    cancellationToken);
                var semantic = observation.Status;
                if (endpointSemantic == SemanticWriteStatus.AcceptedChanged)
                {
                    endpointSemantic = semantic;
                }
                perFieldStatuses.Add($"{item.Field.FieldKey}:{semantic}");
            }

            if (group.Any(item => item.Contract.DisruptionClass == DisruptionClass.NetworkChanging || item.ContractField.DisruptionClass == DisruptionClass.NetworkChanging))
            {
                var previousUrl = BuildControlUrl(device.IpAddress, TryGetPathValue(write.PreWriteValue, "$.httpPort"));
                var predictedUrl = BuildControlUrl(GetPotentialIpFromPostWrite(write.PostWriteValue, device.IpAddress), TryGetPathValue(write.PostWriteValue, "$.httpPort"));
                var recovery = await semanticTrustService.RecoverNetworkAsync(new NetworkRecoveryContext
                {
                    DeviceId = deviceId,
                    PreviousIp = device.IpAddress,
                    PreviousGateway = TryGetPathValue(write.PreWriteValue, "$.gateway")?.ToJsonString().Trim('\"'),
                    PreviousDns = TryGetPathValue(write.PreWriteValue, "$.dns")?.ToJsonString().Trim('\"'),
                    PreviousControlUrl = previousUrl,
                    PredictedControlUrl = predictedUrl
                }, cancellationToken);

                perFieldStatuses.Add(recovery.Recovered
                    ? $"networkRecovery:recovered:{recovery.ReachableUrl}"
                    : "networkRecovery:failed");

                if (!recovery.Recovered)
                {
                    endpointSemantic = SemanticWriteStatus.Uncertain;
                }
            }

            writes.Add(write with
            {
                SemanticStatus = endpointSemantic,
                ContractKey = seed.Contract.ContractKey,
                Message = string.Join("; ", perFieldStatuses),
                ContractViolations = payloadBuild.Validation.Errors
            });
        }

        foreach (var result in writes)
        {
            await store.AddAuditEntryAsync(new WriteAuditEntry
            {
                DeviceId = deviceId,
                AdapterName = result.AdapterName,
                Operation = "TypedApply",
                Endpoint = string.Empty,
                RequestContent = null,
                ResponseContent = result.Message,
                Success = result.Success,
                SemanticStatus = result.SemanticStatus,
                BlockReason = result.Success ? null : result.Message
            }, cancellationToken);
        }

        return writes;
    }

    public async Task<IReadOnlyCollection<PersistenceEligibleField>> GetPersistenceEligibleFieldsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return [];
        }

        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        return fields
            .GroupBy(field => field.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(field => field.CapturedAt).First())
            .Select(field =>
            {
                var contract = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase));
                var contractField = contract?.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
                return new PersistenceEligibleField
                {
                    FieldKey = field.FieldKey,
                    DisplayName = field.DisplayName,
                    Endpoint = field.SourceEndpoint,
                    ContractKey = field.ContractKey ?? string.Empty,
                    RequiresRebootToTakeEffect = contract?.RequiresRebootToTakeEffect == true,
                    PersistenceExpectedAfterReboot = contractField?.PersistExpectedAfterReboot == true || contract?.PersistenceExpectedAfterReboot == true,
                    ExpertOnly = field.ExpertOnly,
                    WriteVerified = field.WriteVerified,
                    Supported = field.SupportState == ContractSupportState.Supported
                };
            })
            .Where(static item => item.PersistenceExpectedAfterReboot)
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PersistenceVerificationResult?> VerifyPersistenceForFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool rebootForVerification, bool expertOverride, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var field = fields.Where(item => item.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase)).OrderByDescending(static item => item.CapturedAt).FirstOrDefault();
        if (field is null)
        {
            return new PersistenceVerificationResult { DeviceId = deviceId, Endpoint = fieldKey, Notes = "Unknown field." };
        }

        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var contract = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase)
            && EndpointPatternMatches(candidate.Endpoint, field.SourceEndpoint));
        if (contract is null)
        {
            return new PersistenceVerificationResult { DeviceId = deviceId, Endpoint = field.SourceEndpoint, Notes = "No matching contract." };
        }

        var contractField = contract.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
        if (contractField is null)
        {
            return new PersistenceVerificationResult { DeviceId = deviceId, Endpoint = field.SourceEndpoint, Notes = "No matching contract field." };
        }

        var converted = ConvertValue(value ?? field.TypedValue ?? JsonValue.Create(string.Empty), contractField);
        if (!converted.Success)
        {
            return new PersistenceVerificationResult { DeviceId = deviceId, Endpoint = field.SourceEndpoint, Notes = converted.Message };
        }

        var endpointFields = fields
            .Where(candidate => candidate.SourceEndpoint.Equals(field.SourceEndpoint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ContractKey, contract.ContractKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => candidate.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(candidate => candidate.CapturedAt).First())
            .ToList();
        var payloadBuild = await BuildContractPayloadAsync(deviceId, field.SourceEndpoint, contract, endpointFields, new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase) { [field.FieldKey] = converted.Value }, expertOverride, cancellationToken);
        if (!payloadBuild.Success || payloadBuild.Payload is null)
        {
            return new PersistenceVerificationResult { DeviceId = deviceId, Endpoint = field.SourceEndpoint, Notes = payloadBuild.Message };
        }

        return await persistenceVerificationService.VerifyAsync(new PersistenceVerificationRequest
        {
            DeviceId = deviceId,
            AdapterName = field.AdapterName,
            Endpoint = field.SourceEndpoint,
            Method = contract.Method,
            Payload = payloadBuild.Payload,
            FieldKey = field.FieldKey,
            FieldSourcePath = contractField.SourcePath,
            IntendedValue = converted.Value?.DeepClone(),
            RebootForVerification = rebootForVerification
        }, cancellationToken);
    }

    private static string? GetPotentialIpFromPostWrite(JsonNode? postWriteNode, string? fallbackIp)
    {
        var ip = TryGetPathValue(postWriteNode, "$.ip")?.ToJsonString().Trim('"');
        return string.IsNullOrWhiteSpace(ip) ? fallbackIp : ip;
    }

    private static string? BuildControlUrl(string? ip, JsonNode? portNode)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var portRaw = portNode?.ToJsonString().Trim('"');
        if (!int.TryParse(portRaw, out var port))
        {
            port = 80;
        }

        return $"http://{ip}:{port}";
    }

    private static JsonObject BuildSemanticContext(IReadOnlyCollection<NormalizedSettingField> fields)
    {
        var context = new JsonObject();
        foreach (var key in new[] { "codec", "profile", "resolution", "bitrate", "frameRate", "dhcpMode", "wirelessMode", "apMode" })
        {
            var value = fields
                .Where(field => field.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static field => field.CapturedAt)
                .FirstOrDefault()
                ?.TypedValue;
            if (value is not null)
            {
                context[key] = value.DeepClone();
            }
        }

        return context;
    }

    private async Task<(bool Success, JsonObject? Payload, string? Message, ContractValidationResult Validation)> BuildContractPayloadAsync(
        Guid deviceId,
        string sourceEndpoint,
        EndpointContract contract,
        IReadOnlyCollection<NormalizedSettingField> endpointFields,
        IReadOnlyDictionary<string, JsonNode?> changes,
        bool expertOverride,
        CancellationToken cancellationToken)
    {
        JsonObject payload;
        var snapshot = await settingsService.GetLastSnapshotAsync(deviceId, cancellationToken);
        var endpointNode = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(value => NormalizeEndpoint(value.SourceEndpoint ?? value.Key).Equals(NormalizeEndpoint(sourceEndpoint), StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (endpointNode is JsonObject snapshotObject)
        {
            payload = (JsonObject)snapshotObject.DeepClone();
        }
        else
        {
            payload = [];
        }

        foreach (var field in endpointFields)
        {
            var mapped = contract.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
            if (mapped is null)
            {
                continue;
            }

            var valueToSet = changes.TryGetValue(field.FieldKey, out var changedValue) ? changedValue : field.TypedValue;
            var converted = ConvertValue(valueToSet ?? JsonValue.Create(string.Empty), mapped);
            if (!converted.Success)
            {
                // keep payload build deterministic; validation handles final block semantics
                continue;
            }

            SetPathValue(payload, mapped.SourcePath, converted.Value?.DeepClone());
        }

        foreach (var change in changes)
        {
            var changed = contract.Fields.FirstOrDefault(candidate => candidate.Key.Equals(change.Key, StringComparison.OrdinalIgnoreCase));
            if (changed is null)
            {
                continue;
            }

            SetPathValue(payload, changed.SourcePath, change.Value?.DeepClone());
        }

        var validation = ValidateContractPayload(contract, payload, changes.Keys.ToList(), expertOverride);
        return validation.IsValid || !validation.Blocked
            ? (true, payload, null, validation)
            : (false, null, "Payload blocked by contract validation.", validation);
    }

    private ContractValidationResult ValidateContractPayload(EndpointContract contract, JsonObject payload, IReadOnlyCollection<string> changedFields, bool expertOverride)
    {
        var errors = new List<string>();

        if (!contract.ObjectShape.PartialWriteAllowed && changedFields.Count > 1 && !contract.ObjectShape.FullObjectWriteRequired)
        {
            errors.Add("partial multi-field write attempted on endpoint without partial-write support");
        }

        foreach (var required in contract.ObjectShape.RequiredRootFields)
        {
            if (TryGetPathValue(payload, $"$.{required}") is null)
            {
                errors.Add($"required root field '{required}' missing");
            }
        }

        foreach (var requiredField in contract.Fields.Where(static item => item.Required))
        {
            if (TryGetPathValue(payload, requiredField.SourcePath) is null)
            {
                errors.Add($"required field '{requiredField.Key}' missing at {requiredField.SourcePath}");
            }
        }

        if (contract.ObjectShape.FullObjectWriteRequired && payload.Count == 0)
        {
            errors.Add("full object payload required but snapshot payload is empty");
        }

        foreach (var contractField in contract.Fields)
        {
            var node = TryGetPathValue(payload, contractField.SourcePath);
            if (node is null)
            {
                if (contractField.Required)
                {
                    errors.Add($"required field '{contractField.Key}' missing");
                }
                continue;
            }

            var converted = ConvertValue(node, contractField);
            if (!converted.Success)
            {
                errors.Add($"{contractField.Key}: {converted.Message}");
            }
        }

        if (contract.ContractKey.Equals("network.interfaces", StringComparison.OrdinalIgnoreCase))
        {
            var dhcp = TryGetPathValue(payload, "$.dhcp");
            var dhcpEnabled = dhcp is not null && bool.TryParse(dhcp.ToJsonString().Trim('"'), out var parsedDhcp) && parsedDhcp;
            if (!dhcpEnabled)
            {
                if (TryGetPathValue(payload, "$.gateway") is null)
                {
                    errors.Add("gateway is required when dhcpMode is false");
                }
                if (TryGetPathValue(payload, "$.dns") is null)
                {
                    errors.Add("dns is required when dhcpMode is false");
                }
            }
        }

        if (contract.ContractKey.Equals("network.wireless", StringComparison.OrdinalIgnoreCase))
        {
            var wirelessMode = TryGetPathValue(payload, "$.wirelessMode")?.ToJsonString().Trim('"');
            var apMode = TryGetPathValue(payload, "$.ap.mode")?.ToJsonString().Trim('"');
            var apEnabled = string.Equals(wirelessMode, "AP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(apMode, "On", StringComparison.OrdinalIgnoreCase);
            if (apEnabled)
            {
                var apSsid = TryGetPathValue(payload, "$.ap.ssid")?.ToJsonString().Trim('"');
                var apPsk = TryGetPathValue(payload, "$.ap.psk")?.ToJsonString().Trim('"');
                if (string.IsNullOrWhiteSpace(apSsid))
                {
                    errors.Add("apSsid is required when AP mode is enabled");
                }
                if (string.IsNullOrWhiteSpace(apPsk))
                {
                    errors.Add("apPsk is required when AP mode is enabled");
                }
            }
        }

        var blocked = errors.Count > 0 && !expertOverride;
        return new ContractValidationResult
        {
            IsValid = errors.Count == 0,
            Blocked = blocked,
            ExpertOverrideUsed = expertOverride,
            ContractKey = contract.ContractKey,
            Endpoint = contract.Endpoint,
            Errors = errors
        };
    }

    private static NormalizedSettingField BuildInvalidField(Guid deviceId, string adapterName, string endpoint, EndpointContract contract, ContractField field, string? reason, JsonNode? rawValue = null)
        => new()
        {
            DeviceId = deviceId,
            GroupKind = contract.GroupKind,
            GroupName = contract.GroupName,
            FieldKey = field.Key,
            DisplayName = field.DisplayName,
            AdapterName = adapterName,
            ParserName = $"Contract:{contract.ContractKey}:Invalid",
            SourceEndpoint = endpoint,
            RawSourcePath = field.SourcePath,
            RawValue = rawValue?.DeepClone(),
            TypedValue = rawValue?.DeepClone(),
            ContractKey = contract.ContractKey,
            Validity = FieldValidityState.Invalid,
            SupportState = ContractSupportState.Uncertain,
            TruthState = field.Evidence.TruthState,
            Confidence = reason ?? "invalid"
        };

    private static (bool Success, JsonNode? Value, string? Message) ConvertValue(JsonNode source, ContractField field)
    {
        try
        {
            return field.Kind switch
            {
                ContractFieldKind.Number => TryParseNumber(source, field),
                ContractFieldKind.Integer => TryParseInteger(source, field),
                ContractFieldKind.Boolean => TryParseBoolean(source),
                ContractFieldKind.Enum => TryParseEnum(source, field),
                ContractFieldKind.IpAddress => TryParseIp(source),
                ContractFieldKind.Port => TryParsePort(source),
                _ => (true, source.DeepClone(), null)
            };
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static (bool Success, JsonNode? Value, string? Message) TryParseNumber(JsonNode source, ContractField field)
    {
        if (source is not JsonValue node || !node.TryGetValue<decimal>(out var value))
        {
            if (!decimal.TryParse(source.ToJsonString().Trim('"'), out value))
            {
                return (false, null, "expected numeric value");
            }
        }

        if (field.Validation.Min is decimal min && value < min)
        {
            return (false, null, $"value below min {min}");
        }

        if (field.Validation.Max is decimal max && value > max)
        {
            return (false, null, $"value above max {max}");
        }

        return (true, JsonValue.Create(value), null);
    }

    private static (bool Success, JsonNode? Value, string? Message) TryParseInteger(JsonNode source, ContractField field)
    {
        var number = TryParseNumber(source, field);
        if (!number.Success || number.Value is null)
        {
            return number;
        }

        if (number.Value is JsonValue node && node.TryGetValue<decimal>(out var value) && value % 1 != 0)
        {
            return (false, null, "expected integer value");
        }

        return (true, JsonValue.Create((int)decimal.Parse(number.Value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)), null);
    }

    private static (bool Success, JsonNode? Value, string? Message) TryParseBoolean(JsonNode source)
    {
        if (source is JsonValue node && node.TryGetValue<bool>(out var value))
        {
            return (true, JsonValue.Create(value), null);
        }

        if (bool.TryParse(source.ToJsonString().Trim('"'), out value))
        {
            return (true, JsonValue.Create(value), null);
        }

        return (false, null, "expected boolean value");
    }

    private static (bool Success, JsonNode? Value, string? Message) TryParseEnum(JsonNode source, ContractField field)
    {
        var raw = source.ToJsonString().Trim('"');
        if (field.EnumValues.Count > 0 && !field.EnumValues.Any(candidate => candidate.Value.Equals(raw, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, null, $"value '{raw}' outside enum domain");
        }

        return (true, JsonValue.Create(raw), null);
    }

    private static (bool Success, JsonNode? Value, string? Message) TryParseIp(JsonNode source)
    {
        var raw = source.ToJsonString().Trim('"');
        return IPAddress.TryParse(raw, out _)
            ? (true, JsonValue.Create(raw), null)
            : (false, null, "invalid IP address");
    }

    private static (bool Success, JsonNode? Value, string? Message) TryParsePort(JsonNode source)
    {
        var parsed = TryParseInteger(source, new ContractField { Validation = new ContractValidationRule { Min = 1, Max = 65535 } });
        return parsed;
    }

    private static IReadOnlyCollection<TypedSettingGroupSnapshot> ToSnapshots(
        Guid deviceId,
        string adapterName,
        IReadOnlyCollection<NormalizedSettingField> fields,
        IReadOnlyCollection<EndpointContract> contracts,
        IReadOnlyCollection<FieldConstraintProfile> constraints,
        IReadOnlyCollection<DependencyMatrixProfile> dependencies)
        => fields
            .GroupBy(static field => field.GroupKind)
            .Select(group => new TypedSettingGroupSnapshot
            {
                DeviceId = deviceId,
                AdapterName = adapterName,
                GroupKind = group.Key,
                GroupName = group.First().GroupName,
                FirmwareFingerprint = group.Select(field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                Fields = group.OrderBy(static field => field.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                EditorHints = BuildEditorHints(group, contracts, constraints, dependencies)
            })
            .OrderBy(static group => group.GroupKind)
            .ToList();

    private static IReadOnlyCollection<EditorHint> BuildEditorHints(
        IGrouping<TypedSettingGroupKind, NormalizedSettingField> group,
        IReadOnlyCollection<EndpointContract> contracts,
        IReadOnlyCollection<FieldConstraintProfile> constraints,
        IReadOnlyCollection<DependencyMatrixProfile> dependencies)
    {
        var output = new List<EditorHint>();
        foreach (var field in group)
        {
            var contractField = contracts
                .FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase))
                ?.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
            var constraint = constraints.FirstOrDefault(profile => profile.FieldKey.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase)
                && profile.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase));
            var dependencyRules = dependencies
                .SelectMany(static profile => profile.Rules)
                .Where(rule => rule.PrimaryFieldKey.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var enumValues = constraint?.SupportedValues.Count > 0
                ? constraint.SupportedValues.Select(value => JsonValue.Create(value)).ToArray()
                : contractField?.EnumValues.Count > 0
                    ? contractField.EnumValues.Select(item => JsonValue.Create(item.Value)).ToArray()
                    : Array.Empty<JsonNode?>();
            var descriptor = new ControlPointDescriptor
            {
                FieldKey = field.FieldKey,
                DisplayName = field.DisplayName,
                ContractKey = field.ContractKey ?? string.Empty,
                Endpoint = field.SourceEndpoint,
                SourcePath = contractField?.SourcePath ?? field.RawSourcePath,
                Kind = contractField?.Kind ?? InferKind(field.TypedValue),
                Writable = field.WriteVerified || contractField?.Writable == true,
                ExpertOnly = field.ExpertOnly || contractField?.ExpertOnly == true,
                FullObjectWriteRequired = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase))?.ObjectShape.FullObjectWriteRequired ?? true,
                PartialWriteAllowed = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase))?.ObjectShape.PartialWriteAllowed ?? false,
                EnumValues = enumValues
                    .OfType<JsonValue>()
                    .Select(static value => value.ToJsonString().Trim('"'))
                    .ToArray(),
                Min = constraint?.Min ?? contractField?.Validation.Min,
                Max = constraint?.Max ?? contractField?.Validation.Max,
                DependencyRules = dependencyRules
            };
            var typing = ControlPointClassifier.Classify(descriptor);

            output.Add(new EditorHint
            {
                FieldKey = field.FieldKey,
                Label = field.DisplayName,
                EditorKind = ControlPointClassifier.ToLegacyEditorKind(typing),
                EnumValues = enumValues.Length > 0 ? new JsonArray(enumValues) : null,
                Min = descriptor.Min,
                Max = descriptor.Max,
                DisruptionClass = field.DisruptionClass,
                ExpertOnly = field.ExpertOnly,
                Writable = field.WriteVerified,
                ContractKey = field.ContractKey,
                TruthState = contractField?.Evidence.TruthState ?? ContractTruthState.Unverified,
                PrimitiveType = typing.PrimitiveType,
                ControlType = typing.ControlType,
                ControlTraits = typing.Traits,
                RecommendedWidget = typing.RecommendedWidget,
                NormalUiEligible = typing.NormalUiEligible,
                TypeBlocker = typing.TypeBlocker,
                Unit = dependencyRules.Count == 0
                    ? null
                    : string.Join(" | ", dependencyRules.Select(static rule => $"{rule.DependsOnFieldKey}:{string.Join("/", rule.DependsOnValues)}=>{string.Join("/", rule.AllowedPrimaryValues)}"))
            });
        }

        return output;
    }

    private static ContractFieldKind InferKind(JsonNode? value)
    {
        return value switch
        {
            JsonObject => ContractFieldKind.Object,
            JsonArray => ContractFieldKind.Array,
            JsonValue node when node.TryGetValue<bool>(out _) => ContractFieldKind.Boolean,
            JsonValue node when node.TryGetValue<int>(out _) => ContractFieldKind.Integer,
            JsonValue node when node.TryGetValue<decimal>(out _) => ContractFieldKind.Number,
            _ => ContractFieldKind.String
        };
    }

    private static ContractSupportState ResolveSupportState(EndpointValidationResult? validation, ContractField field)
    {
        if (!field.Writable)
        {
            return ContractSupportState.Supported;
        }

        if (validation?.WriteVerified == true)
        {
            return ContractSupportState.Supported;
        }

        return field.Evidence.TruthState == ContractTruthState.Unverified ? ContractSupportState.Uncertain : ContractSupportState.Unsupported;
    }

    private static FieldValidityState ResolveValidity(EndpointValidationResult? validation, ContractField field)
    {
        if (validation is null)
        {
            return field.Evidence.TruthState == ContractTruthState.Unverified ? FieldValidityState.Unverified : FieldValidityState.Inferred;
        }

        if (validation.ReadVerified && validation.WriteVerified)
        {
            return FieldValidityState.Proven;
        }

        return validation.ReadVerified ? FieldValidityState.Inferred : FieldValidityState.Unverified;
    }

    private static string ResolveConfidence(EndpointValidationResult? validation, ContractField field)
    {
        if (validation is null)
        {
            return field.Evidence.TruthState.ToString().ToLowerInvariant();
        }

        if (validation.ReadVerified && validation.WriteVerified && validation.PersistsAfterReboot)
        {
            return "high";
        }

        if (validation.ReadVerified && validation.WriteVerified)
        {
            return "medium";
        }

        return validation.ReadVerified ? "low" : "unverified";
    }

    private static bool EndpointPatternMatches(string pattern, string endpoint)
    {
        var regex = "^" + Regex.Escape(NormalizeEndpoint(pattern)).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(NormalizeEndpoint(endpoint), regex, RegexOptions.IgnoreCase);
    }

    private static string NormalizeEndpoint(string endpoint)
        => endpoint
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

    private static string BuildFirmwareFingerprint(DeviceIdentity device)
        => $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

    private static JsonNode? TryGetPathValue(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = ParsePath(path);
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is null)
            {
                return null;
            }

            if (segment.Index is int index)
            {
                if (current is not JsonArray arr || index < 0 || index >= arr.Count)
                {
                    return null;
                }

                current = arr[index];
            }
            else
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name!, out var next))
                {
                    return null;
                }

                current = next;
            }
        }

        return current;
    }

    private static bool IsSemanticErrorPayload(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("statusCode", out var codeNode)
                && codeNode is not null
                && int.TryParse(codeNode.ToJsonString().Trim('"'), out var code)
                && code != 0)
            {
                return true;
            }

            if (obj.TryGetPropertyValue("ret", out var retNode)
                && retNode is not null
                && retNode.ToJsonString().Trim('"').Equals("sorry", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            return raw.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("check in falied", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void SetPathValue(JsonObject root, string path, JsonNode? value)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var segments = ParsePath(path);
        JsonNode current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLeaf = i == segments.Count - 1;

            if (segment.Index is int index)
            {
                if (current is not JsonArray arr)
                {
                    return;
                }

                while (arr.Count <= index)
                {
                    arr.Add(null);
                }

                if (isLeaf)
                {
                    arr[index] = value?.DeepClone();
                    return;
                }

                arr[index] ??= segments[i + 1].Index is int ? new JsonArray() : new JsonObject();
                current = arr[index]!;
            }
            else
            {
                if (current is not JsonObject obj)
                {
                    return;
                }

                if (isLeaf)
                {
                    obj[segment.Name!] = value?.DeepClone();
                    return;
                }

                obj[segment.Name!] ??= segments[i + 1].Index is int ? new JsonArray() : new JsonObject();
                current = obj[segment.Name!]!;
            }
        }
    }

    private static List<PathSegment> ParsePath(string path)
    {
        var cleaned = path.Trim();
        if (cleaned.StartsWith("$.", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }
        else if (cleaned.StartsWith("$", StringComparison.Ordinal))
        {
            cleaned = cleaned[1..];
        }

        var segments = new List<PathSegment>();
        foreach (var raw in cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Contains('[', StringComparison.Ordinal))
            {
                var name = raw[..raw.IndexOf('[', StringComparison.Ordinal)];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    segments.Add(new PathSegment(name, null));
                }

                var indexText = raw[(raw.IndexOf('[', StringComparison.Ordinal) + 1)..raw.IndexOf(']', StringComparison.Ordinal)];
                if (int.TryParse(indexText, out var index))
                {
                    segments.Add(new PathSegment(null, index));
                }
            }
            else
            {
                segments.Add(new PathSegment(raw, null));
            }
        }

        return segments;
    }

    private sealed record PathSegment(string? Name, int? Index);
}


