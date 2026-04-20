using System.Net.Sockets;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class SemanticTrustService(
    IApplicationStore store,
    IEndpointContractCatalog contractCatalog,
    SettingsService settingsService,
    ILogger<SemanticTrustService> logger)
{
    public SemanticWriteStatus Classify(
        WriteResult write,
        JsonNode? intended,
        JsonNode? baseline,
        JsonNode? immediate,
        JsonNode? delayed,
        JsonNode? reboot,
        ContractField field)
    {
        if (!write.Success)
        {
            return write.StatusCode is >= 400 ? SemanticWriteStatus.Rejected : SemanticWriteStatus.TransportFailed;
        }

        if (!write.PostReadVerified || immediate is null)
        {
            return SemanticWriteStatus.ShapeMismatch;
        }

        if (JsonNode.DeepEquals(immediate, baseline))
        {
            return SemanticWriteStatus.AcceptedNoChange;
        }

        if (JsonNode.DeepEquals(immediate, intended))
        {
            if (delayed is not null && JsonNode.DeepEquals(delayed, immediate))
            {
                return SemanticWriteStatus.PersistedAfterDelay;
            }

            if (reboot is not null)
            {
                return JsonNode.DeepEquals(reboot, immediate)
                    ? SemanticWriteStatus.PersistedAfterReboot
                    : SemanticWriteStatus.LostAfterReboot;
            }

            return SemanticWriteStatus.AcceptedChanged;
        }

        if (field.Kind is ContractFieldKind.Number or ContractFieldKind.Integer)
        {
            var intendedDecimal = TryToDecimal(intended);
            var actualDecimal = TryToDecimal(immediate);
            if (intendedDecimal is not null && actualDecimal is not null)
            {
                return field.Validation.Min is decimal min && field.Validation.Max is decimal max && actualDecimal >= min && actualDecimal <= max
                    ? SemanticWriteStatus.AcceptedClamped
                    : SemanticWriteStatus.AcceptedTranslated;
            }
        }

        if (delayed is not null && JsonNode.DeepEquals(delayed, baseline))
        {
            return SemanticWriteStatus.AcceptedChangedThenReverted;
        }

        return SemanticWriteStatus.AcceptedTranslated;
    }

    public async Task<SemanticWriteObservation> CaptureObservationAsync(
        Guid deviceId,
        EndpointContract contract,
        ContractField field,
        WriteResult write,
        JsonNode? intended,
        JsonNode? baseline,
        JsonNode? immediate,
        JsonNode? delayed,
        JsonNode? reboot,
        JsonObject context,
        CancellationToken cancellationToken)
    {
        var status = Classify(write, intended, baseline, immediate, delayed, reboot, field);
        var observation = new SemanticWriteObservation
        {
            DeviceId = deviceId,
            FirmwareFingerprint = await ResolveFirmwareFingerprintAsync(deviceId, cancellationToken),
            Endpoint = contract.Endpoint,
            Method = contract.Method,
            ContractKey = contract.ContractKey,
            FieldKey = field.Key,
            DisruptionClass = field.DisruptionClass != DisruptionClass.Unknown ? field.DisruptionClass : contract.DisruptionClass,
            IntendedValue = intended?.DeepClone(),
            BaselineValue = baseline?.DeepClone(),
            ImmediateValue = immediate?.DeepClone(),
            DelayedValue = delayed?.DeepClone(),
            RebootValue = reboot?.DeepClone(),
            Status = status,
            Context = (JsonObject)context.DeepClone()
        };

        await store.SaveSemanticWriteObservationsAsync([observation], cancellationToken);
        await UpdateConstraintProfileFromObservationAsync(observation, field, cancellationToken);
        await RebuildDependencyMatricesAsync(observation.FirmwareFingerprint, cancellationToken);
        return observation;
    }

    public async Task<IReadOnlyCollection<SemanticWriteObservation>> GetSemanticHistoryAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
        => await store.GetSemanticWriteObservationsAsync(deviceId, Math.Max(1, limit), cancellationToken);

    public async Task<IReadOnlyCollection<FieldConstraintProfile>> GetConstraintProfilesAsync(string? firmwareFingerprint, CancellationToken cancellationToken)
        => await store.GetFieldConstraintProfilesAsync(firmwareFingerprint, cancellationToken);

    public async Task<IReadOnlyCollection<DependencyMatrixProfile>> GetDependencyMatricesAsync(string? firmwareFingerprint, CancellationToken cancellationToken)
        => await store.GetDependencyMatrixProfilesAsync(firmwareFingerprint, cancellationToken);

    public async Task<ConstraintDiscoveryResult?> DiscoverConstraintsAsync(ConstraintDiscoveryRequest request, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var fingerprint = await ResolveFirmwareFingerprintAsync(device.Id, cancellationToken);
        var fields = await store.GetNormalizedSettingFieldsAsync(device.Id, cancellationToken);
        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var selected = fields
            .Where(field => request.FieldKeys.Count == 0 || request.FieldKeys.Contains(field.FieldKey, StringComparer.OrdinalIgnoreCase))
            .Where(field => field.GroupKind is TypedSettingGroupKind.VideoImage or TypedSettingGroupKind.NetworkWireless or TypedSettingGroupKind.UsersMaintenance)
            .GroupBy(field => field.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(field => field.CapturedAt).First())
            .ToList();

        var observations = new List<SemanticWriteObservation>();
        foreach (var field in selected)
        {
            var contract = contracts.FirstOrDefault(candidate => candidate.ContractKey.Equals(field.ContractKey, StringComparison.OrdinalIgnoreCase));
            var contractField = contract?.Fields.FirstOrDefault(candidate => candidate.Key.Equals(field.FieldKey, StringComparison.OrdinalIgnoreCase));
            if (contract is null || contractField is null || !contractField.Writable)
            {
                continue;
            }

            if (!request.IncludeNetworkChanging && (contract.DisruptionClass == DisruptionClass.NetworkChanging || contractField.DisruptionClass == DisruptionClass.NetworkChanging))
            {
                continue;
            }

            var candidates = BuildDiscoveryCandidates(field.TypedValue, contractField);
            foreach (var candidate in candidates)
            {
                try
                {
                    var plan = new WritePlan
                    {
                        GroupName = contract.GroupName,
                        Endpoint = field.SourceEndpoint,
                        Method = contract.Method,
                        AdapterName = field.AdapterName,
                        Payload = BuildSingleFieldPayload(contractField.SourcePath, candidate),
                        RequireWriteVerification = !request.ExpertOverride
                    };

                    var result = await settingsService.WriteAsync(device.Id, plan, cancellationToken);
                    if (result is null)
                    {
                        continue;
                    }

                    var immediate = TryGetPathValue(result.PostWriteValue, contractField.SourcePath);
                    JsonNode? delayed = null;
                    if (request.DelaySeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(request.DelaySeconds), cancellationToken);
                        var snapshot = await settingsService.ReadAsync(device.Id, cancellationToken);
                        var endpointValue = snapshot?.Groups.SelectMany(static group => group.Values.Values)
                            .FirstOrDefault(item => string.Equals(item.SourceEndpoint ?? item.Key, field.SourceEndpoint, StringComparison.OrdinalIgnoreCase));
                        delayed = TryGetPathValue(endpointValue?.Value, contractField.SourcePath);
                    }

                    observations.Add(await CaptureObservationAsync(
                        device.Id,
                        contract,
                        contractField,
                        result,
                        candidate,
                        field.TypedValue,
                        immediate,
                        delayed,
                        null,
                        BuildContext(fields),
                        cancellationToken));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Constraint discovery write failed for {Field} on {Device}", field.FieldKey, device.DisplayName);
                }
            }
        }

        var profiles = await store.GetFieldConstraintProfilesAsync(fingerprint, cancellationToken);
        return new ConstraintDiscoveryResult
        {
            DeviceId = device.Id,
            FirmwareFingerprint = fingerprint,
            UpdatedProfiles = profiles,
            Observations = observations,
            Notes = observations.Count == 0
                ? "No safe candidate writes executed for selected fields."
                : $"Captured {observations.Count} semantic observations."
        };
    }

    public async Task<NetworkRecoveryResult> RecoverNetworkAsync(NetworkRecoveryContext context, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.PreviousControlUrl))
        {
            candidates.Add(context.PreviousControlUrl);
        }
        if (!string.IsNullOrWhiteSpace(context.PredictedControlUrl))
        {
            candidates.Add(context.PredictedControlUrl);
            candidates.AddRange(BuildSubnetNeighborCandidates(context.PredictedControlUrl));
        }

        var probed = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseHostPort(candidate, out var host, out var port))
            {
                continue;
            }

            probed.Add(candidate);
            if (await ProbeTcpAsync(host, port, cancellationToken))
            {
                return new NetworkRecoveryResult
                {
                    DeviceId = context.DeviceId,
                    Recovered = true,
                    ReachableUrl = candidate,
                    Notes = "Recovered connectivity using candidate URL.",
                    ProbedUrls = probed
                };
            }
        }

        return new NetworkRecoveryResult
        {
            DeviceId = context.DeviceId,
            Recovered = false,
            Notes = "Recovery probes failed. Manual subnet scan and credential check required.",
            ProbedUrls = probed
        };
    }

    private async Task UpdateConstraintProfileFromObservationAsync(SemanticWriteObservation observation, ContractField field, CancellationToken cancellationToken)
    {
        var existing = (await store.GetFieldConstraintProfilesAsync(observation.FirmwareFingerprint, cancellationToken))
            .FirstOrDefault(profile => profile.FieldKey.Equals(observation.FieldKey, StringComparison.OrdinalIgnoreCase)
                && profile.ContractKey.Equals(observation.ContractKey, StringComparison.OrdinalIgnoreCase));

        var supported = (existing?.SupportedValues ?? [])
            .Concat(ToValueStrings(observation.ImmediateValue))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var numbers = new List<decimal>();
        if (existing?.Min is decimal existingMin)
        {
            numbers.Add(existingMin);
        }
        if (existing?.Max is decimal existingMax)
        {
            numbers.Add(existingMax);
        }
        var actualNumeric = TryToDecimal(observation.ImmediateValue);
        if (actualNumeric is not null)
        {
            numbers.Add(actualNumeric.Value);
        }

        var min = numbers.Count == 0 ? field.Validation.Min : numbers.Min();
        var max = numbers.Count == 0 ? field.Validation.Max : numbers.Max();
        var quality = observation.Status is SemanticWriteStatus.AcceptedChanged
            or SemanticWriteStatus.AcceptedClamped
            or SemanticWriteStatus.PersistedAfterDelay
            or SemanticWriteStatus.PersistedAfterReboot
            ? EvidenceQuality.Proven
            : EvidenceQuality.Inferred;

        var profile = (existing ?? new FieldConstraintProfile
        {
            FirmwareFingerprint = observation.FirmwareFingerprint,
            FieldKey = observation.FieldKey,
            ContractKey = observation.ContractKey
        }) with
        {
            SupportedValues = supported,
            Min = min,
            Max = max,
            Notes = observation.Status.ToString(),
            Quality = quality,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveFieldConstraintProfilesAsync([profile], cancellationToken);
    }

    private async Task RebuildDependencyMatricesAsync(string firmwareFingerprint, CancellationToken cancellationToken)
    {
        var observations = (await store.GetSemanticWriteObservationsAsync(null, 5000, cancellationToken))
            .Where(item => item.FirmwareFingerprint.Equals(firmwareFingerprint, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rules = new List<FieldDependencyRule>();

        var resolutionRows = observations
            .Where(item => item.FieldKey.Equals("resolution", StringComparison.OrdinalIgnoreCase)
                && item.Context.TryGetPropertyValue("codec", out _))
            .ToList();
        foreach (var group in resolutionRows.GroupBy(item => item.Context["codec"]?.ToJsonString().Trim('"') ?? "unknown", StringComparer.OrdinalIgnoreCase))
        {
            var accepted = group
                .Where(item => item.Status is SemanticWriteStatus.AcceptedChanged or SemanticWriteStatus.PersistedAfterDelay or SemanticWriteStatus.PersistedAfterReboot)
                .Select(item => item.ImmediateValue?.ToJsonString().Trim('"'))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (accepted.Count == 0)
            {
                continue;
            }

            rules.Add(new FieldDependencyRule
            {
                PrimaryFieldKey = "resolution",
                DependsOnFieldKey = "codec",
                DependsOnValues = [group.Key],
                AllowedPrimaryValues = accepted,
                Notes = "Learned from semantic writes.",
                Quality = EvidenceQuality.Proven
            });
        }

        var fpsRows = observations
            .Where(item => item.FieldKey.Equals("frameRate", StringComparison.OrdinalIgnoreCase)
                && item.Context.TryGetPropertyValue("resolution", out _)
                && item.Context.TryGetPropertyValue("codec", out _))
            .ToList();
        foreach (var group in fpsRows.GroupBy(item => $"{item.Context["codec"]}|{item.Context["resolution"]}", StringComparer.OrdinalIgnoreCase))
        {
            var fpsAccepted = group
                .Where(item => item.Status is SemanticWriteStatus.AcceptedChanged or SemanticWriteStatus.PersistedAfterDelay or SemanticWriteStatus.PersistedAfterReboot)
                .Select(item => item.ImmediateValue?.ToJsonString().Trim('"'))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var parts = group.Key.Split('|', 2);
            if (fpsAccepted.Count == 0 || parts.Length != 2)
            {
                continue;
            }

            rules.Add(new FieldDependencyRule
            {
                PrimaryFieldKey = "frameRate",
                DependsOnFieldKey = "resolution",
                DependsOnValues = [parts[1].Trim('"')],
                AllowedPrimaryValues = fpsAccepted,
                Notes = $"Codec={parts[0].Trim('\"')}",
                Quality = EvidenceQuality.Proven
            });
        }

        var profileRows = observations
            .Where(item => item.FieldKey.Equals("profile", StringComparison.OrdinalIgnoreCase)
                && item.Context.TryGetPropertyValue("codec", out _))
            .ToList();
        foreach (var group in profileRows.GroupBy(item => item.Context["codec"]?.ToJsonString().Trim('"') ?? "unknown", StringComparer.OrdinalIgnoreCase))
        {
            var accepted = group
                .Where(item => item.Status is SemanticWriteStatus.AcceptedChanged or SemanticWriteStatus.PersistedAfterDelay or SemanticWriteStatus.PersistedAfterReboot)
                .Select(item => item.ImmediateValue?.ToJsonString().Trim('"'))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (accepted.Count == 0)
            {
                continue;
            }

            rules.Add(new FieldDependencyRule
            {
                PrimaryFieldKey = "profile",
                DependsOnFieldKey = "codec",
                DependsOnValues = [group.Key],
                AllowedPrimaryValues = accepted,
                Notes = "Learned from semantic writes.",
                Quality = EvidenceQuality.Proven
            });
        }

        var bitrateRows = observations
            .Where(item => item.FieldKey.Equals("bitrate", StringComparison.OrdinalIgnoreCase)
                && item.Context.TryGetPropertyValue("codec", out _))
            .ToList();
        foreach (var group in bitrateRows.GroupBy(item => item.Context["codec"]?.ToJsonString().Trim('"') ?? "unknown", StringComparer.OrdinalIgnoreCase))
        {
            var accepted = group
                .Where(item => item.Status is SemanticWriteStatus.AcceptedChanged or SemanticWriteStatus.AcceptedClamped or SemanticWriteStatus.PersistedAfterDelay or SemanticWriteStatus.PersistedAfterReboot)
                .Select(item => item.ImmediateValue?.ToJsonString().Trim('"'))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (accepted.Count == 0)
            {
                continue;
            }

            rules.Add(new FieldDependencyRule
            {
                PrimaryFieldKey = "bitrate",
                DependsOnFieldKey = "codec",
                DependsOnValues = [group.Key],
                AllowedPrimaryValues = accepted,
                Notes = "Learned from semantic writes.",
                Quality = EvidenceQuality.Proven
            });
        }

        // static safety dependencies for network/user flows
        rules.Add(new FieldDependencyRule
        {
            PrimaryFieldKey = "ip",
            DependsOnFieldKey = "dhcpMode",
            DependsOnValues = ["false"],
            AllowedPrimaryValues = ["<ipv4-required-with-gateway-dns>"],
            Notes = "Static mode requires gateway and DNS for safe management.",
            Quality = EvidenceQuality.Inferred
        });
        rules.Add(new FieldDependencyRule
        {
            PrimaryFieldKey = "apPsk",
            DependsOnFieldKey = "wirelessMode",
            DependsOnValues = ["AP"],
            AllowedPrimaryValues = ["8-63 chars"],
            Notes = "AP mode requires PSK and SSID.",
            Quality = EvidenceQuality.Inferred
        });

        var matrix = new DependencyMatrixProfile
        {
            FirmwareFingerprint = firmwareFingerprint,
            GroupName = "Top3",
            Rules = rules,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.SaveDependencyMatrixProfilesAsync([matrix], cancellationToken);
    }

    private static JsonObject BuildContext(IReadOnlyCollection<NormalizedSettingField> fields)
    {
        var ctx = new JsonObject();
        foreach (var key in new[] { "codec", "resolution", "profile", "bitrate", "frameRate", "dhcpMode", "wirelessMode", "apMode" })
        {
            var value = fields.Where(field => field.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(static field => field.CapturedAt).FirstOrDefault()?.TypedValue;
            if (value is not null)
            {
                ctx[key] = value.DeepClone();
            }
        }

        return ctx;
    }

    private static JsonObject BuildSingleFieldPayload(string sourcePath, JsonNode? value)
    {
        var root = new JsonObject();
        SetPathValue(root, sourcePath, value?.DeepClone());
        return root;
    }

    private static IEnumerable<JsonNode?> BuildDiscoveryCandidates(JsonNode? baseline, ContractField field)
    {
        if (field.Kind == ContractFieldKind.Enum && field.EnumValues.Count > 0)
        {
            return field.EnumValues.Select(item => (JsonNode?)JsonValue.Create(item.Value)).Take(6).ToList();
        }

        if (field.Kind is ContractFieldKind.Number or ContractFieldKind.Integer)
        {
            var current = TryToDecimal(baseline) ?? field.Validation.Min ?? 0;
            var min = field.Validation.Min ?? Math.Max(0, current - 10);
            var max = field.Validation.Max ?? (current + 10);
            var step = field.Kind == ContractFieldKind.Integer ? 1 : 5;
            return new JsonNode?[]
            {
                JsonValue.Create(Convert.ToDecimal(Math.Clamp(current - step, min, max))),
                JsonValue.Create(Convert.ToDecimal(Math.Clamp(current + step, min, max)))
            };
        }

        return [];
    }

    private async Task<string> ResolveFirmwareFingerprintAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        return fields.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? "unknown-firmware";
    }

    private static IEnumerable<string> ToValueStrings(JsonNode? node)
    {
        if (node is null)
        {
            yield break;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    yield return item.ToJsonString().Trim('"');
                }
            }
            yield break;
        }

        yield return node.ToJsonString().Trim('"');
    }

    private static decimal? TryToDecimal(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var decimalValue))
            {
                return decimalValue;
            }
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
            if (value.TryGetValue<double>(out var doubleValue))
            {
                return Convert.ToDecimal(doubleValue);
            }
        }

        if (node is not null && decimal.TryParse(node.ToJsonString().Trim('"'), out var parsed))
        {
            return parsed;
        }

        return null;
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
                current[key] = value;
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

    private static bool TryParseHostPort(string url, out string host, out int port)
    {
        host = string.Empty;
        port = 80;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port <= 0 ? 80 : uri.Port;
        return true;
    }

    private static async Task<bool> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> BuildSubnetNeighborCandidates(string predictedControlUrl)
    {
        if (!TryParseHostPort(predictedControlUrl, out var host, out var port))
        {
            return [];
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            return [];
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return [];
        }

        var candidates = new List<string>();
        for (var delta = -2; delta <= 2; delta++)
        {
            if (delta == 0)
            {
                continue;
            }

            var value = bytes[3] + delta;
            if (value is <= 0 or >= 255)
            {
                continue;
            }

            var neighbor = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{value}";
            candidates.Add($"http://{neighbor}:{port}");
        }

        return candidates;
    }
}



