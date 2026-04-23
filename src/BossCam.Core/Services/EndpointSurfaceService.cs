using System.Text.Json.Nodes;
using BossCam.Contracts;

namespace BossCam.Core;

public sealed class EndpointSurfaceService(
    IApplicationStore store,
    IEndpointContractCatalog contractCatalog)
{
    public async Task<EndpointSurfaceReport?> GetReportAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var snapshot = await store.GetSettingsSnapshotAsync(deviceId, cancellationToken);
        var normalized = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var firmware = normalized.Select(static field => field.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

        var rawValues = snapshot?.Groups.SelectMany(static group => group.Values.Values).ToList() ?? [];
        var endpoints = contracts
            .Select(contract => BuildItem(deviceId, contract, rawValues))
            .OrderBy(static item => item.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ContractKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new EndpointSurfaceReport
        {
            DeviceId = deviceId,
            IpAddress = device.IpAddress ?? string.Empty,
            FirmwareFingerprint = firmware,
            Endpoints = endpoints
        };
    }

    private static EndpointSurfaceItem BuildItem(Guid deviceId, EndpointContract contract, IReadOnlyCollection<SettingValue> rawValues)
    {
        var matched = rawValues
            .Select(value => new { Value = value, Score = EndpointMatchScore(contract.Endpoint, value.SourceEndpoint ?? value.Key) })
            .Where(candidate => candidate.Score is not null)
            .OrderByDescending(static candidate => candidate.Score!.Value)
            .ThenByDescending(static candidate => PayloadScore(candidate.Value))
            .ThenByDescending(static candidate => EndpointPriorityScore(candidate.Value.SourceEndpoint ?? candidate.Value.Key))
            .ThenByDescending(static candidate => candidate.Value.CapturedAt)
            .Select(static candidate => candidate.Value)
            .FirstOrDefault();
        var currentPayload = matched?.Value?.DeepClone();
        var suggested = currentPayload as JsonObject is not null
            ? (JsonObject)((JsonObject)currentPayload).DeepClone()
            : BuildDefaultPayload(contract);
        return new EndpointSurfaceItem
        {
            DeviceId = deviceId,
            Family = MapFamily(contract.ContractKey, contract.Endpoint),
            GroupName = contract.GroupName,
            ContractKey = contract.ContractKey,
            Endpoint = NormalizeEndpoint(matched?.SourceEndpoint ?? matched?.Key ?? contract.Endpoint),
            Method = contract.Method,
            Surface = contract.Surface.ToString(),
            WrapperObjectName = InferWrapperObjectName(contract.ContractKey, contract.Endpoint, contract.ObjectShape.RootPath),
            AuthMode = contract.AuthMode,
            ExpertOnly = contract.ExpertOnly,
            Writable = contract.Fields.Any(static field => field.Writable),
            RequiresConfirmation = contract.DisruptionClass is DisruptionClass.Reboot or DisruptionClass.FactoryReset or DisruptionClass.ServiceImpacting or DisruptionClass.NetworkChanging,
            DisruptionClass = contract.DisruptionClass.ToString(),
            TruthState = contract.TruthState.ToString(),
            CurrentPayload = currentPayload,
            SuggestedPayload = suggested,
            CurrentPayloadAvailable = currentPayload is not null,
            SupportsExecution = !contract.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || currentPayload is not null,
            Notes = contract.Fields.Count == 0
                ? "No mapped fields."
                : string.Join(", ", contract.Fields.Select(static field => field.Key))
        };
    }

    private static JsonObject BuildDefaultPayload(EndpointContract contract)
    {
        var root = new JsonObject();
        foreach (var requiredRootField in contract.ObjectShape.RequiredRootFields)
        {
            root[requiredRootField] = requiredRootField.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                ? JsonValue.Create(true)
                : requiredRootField.Equals("id", StringComparison.OrdinalIgnoreCase)
                    ? JsonValue.Create(1)
                    : JsonValue.Create(string.Empty);
        }

        foreach (var field in contract.Fields.Where(static field => field.Writable))
        {
            SetPathValue(root, field.SourcePath, BuildFieldDefault(field));
        }

        return root;
    }

    private static JsonNode? BuildFieldDefault(ContractField field)
        => field.Kind switch
        {
            ContractFieldKind.Boolean => JsonValue.Create(true),
            ContractFieldKind.Enum => JsonValue.Create(field.EnumValues.FirstOrDefault()?.Value ?? string.Empty),
            ContractFieldKind.Integer or ContractFieldKind.Port => JsonValue.Create((int)(field.Validation.Min ?? 1)),
            ContractFieldKind.Number => JsonValue.Create(field.Validation.Min ?? 0m),
            ContractFieldKind.IpAddress => JsonValue.Create("0.0.0.0"),
            ContractFieldKind.Object => new JsonObject(),
            ContractFieldKind.Array => new JsonArray(),
            _ => JsonValue.Create(string.Empty)
        };

    private static int? EndpointMatchScore(string pattern, string endpoint)
    {
        var normalizedPattern = NormalizeEndpoint(pattern);
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var endpointCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedEndpoint };
        if (normalizedEndpoint.EndsWith("/properties", StringComparison.OrdinalIgnoreCase))
        {
            endpointCandidates.Add(normalizedEndpoint[..^"/properties".Length]);
        }

        if (endpointCandidates.Contains(normalizedPattern))
        {
            return 1000;
        }

        var patternSegments = normalizedPattern.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var endpointCandidate in endpointCandidates)
        {
            var endpointSegments = endpointCandidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (patternSegments.Length != endpointSegments.Length)
            {
                continue;
            }

            var score = 0;
            var matched = true;
            for (var i = 0; i < patternSegments.Length; i++)
            {
                if (patternSegments[i] == "*")
                {
                    score += 25;
                    continue;
                }

                if (!patternSegments[i].Equals(endpointSegments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }

                score += 100;
            }

            if (matched)
            {
                return score;
            }
        }

        return null;
    }

    private static int PayloadScore(SettingValue value)
    {
        if (value.Value is JsonObject)
        {
            return 100;
        }

        if (value.Value is JsonArray)
        {
            return 70;
        }

        if (value.Value is JsonValue scalar)
        {
            var text = scalar.ToJsonString();
            if (text.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || text.Contains("404", StringComparison.OrdinalIgnoreCase))
            {
                return -1000;
            }

            if (text.StartsWith("\"/9j/", StringComparison.OrdinalIgnoreCase))
            {
                return -750;
            }
        }

        return 0;
    }

    private static int EndpointPriorityScore(string endpoint)
    {
        var normalized = NormalizeEndpoint(endpoint);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("channel", StringComparison.OrdinalIgnoreCase)
                && (segments[i + 1].Equals("101", StringComparison.OrdinalIgnoreCase)
                    || segments[i + 1].Equals("1", StringComparison.OrdinalIgnoreCase)))
            {
                return 25;
            }
        }

        if (normalized.Contains("/channels", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return 0;
    }

    private static string NormalizeEndpoint(string endpoint)
        => endpoint
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

    private static string MapFamily(string contractKey, string endpoint)
    {
        if (contractKey.StartsWith("video.input", StringComparison.OrdinalIgnoreCase))
        {
            return "VideoInput";
        }

        if (contractKey.StartsWith("video.encode", StringComparison.OrdinalIgnoreCase))
        {
            return "VideoEncode";
        }

        if (contractKey.StartsWith("audio.", StringComparison.OrdinalIgnoreCase))
        {
            return "Audio";
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

        if (contractKey.StartsWith("maintenance.", StringComparison.OrdinalIgnoreCase))
        {
            return "Maintenance";
        }

        return endpoint;
    }

    private static string InferWrapperObjectName(string contractKey, string endpoint, string rootPath)
    {
        var cleanedPath = rootPath.Trim();
        if (cleanedPath.StartsWith("$.", StringComparison.Ordinal))
        {
            cleanedPath = cleanedPath[2..];
        }
        else if (cleanedPath.StartsWith("$", StringComparison.Ordinal))
        {
            cleanedPath = cleanedPath[1..];
        }

        if (string.IsNullOrWhiteSpace(cleanedPath))
        {
            if (contractKey.StartsWith("video.input", StringComparison.OrdinalIgnoreCase))
            {
                return "VideoInputChannel";
            }

            if (contractKey.StartsWith("video.encode", StringComparison.OrdinalIgnoreCase))
            {
                return "VideoEncodeChannel";
            }

            if (contractKey.StartsWith("image.", StringComparison.OrdinalIgnoreCase))
            {
                return "Image";
            }

            if (contractKey.StartsWith("network.", StringComparison.OrdinalIgnoreCase))
            {
                return "Network";
            }
        }

        return endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "Root";
    }

    private static void SetPathValue(JsonObject root, string path, JsonNode? value)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
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
