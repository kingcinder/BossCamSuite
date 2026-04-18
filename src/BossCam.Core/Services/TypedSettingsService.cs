using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class TypedSettingsService(
    IApplicationStore store,
    SettingsService settingsService,
    ILogger<TypedSettingsService> logger)
{
    private static readonly IReadOnlyCollection<EndpointParserDefinition> EndpointParsers = BuildEndpointParsers();
    private static readonly IReadOnlyCollection<EditorHint> EditorHints = BuildEditorHints();

    public async Task<IReadOnlyCollection<TypedSettingGroupSnapshot>> NormalizeDeviceAsync(Guid deviceId, bool refreshFromDevice, CancellationToken cancellationToken)
    {
        if (refreshFromDevice)
        {
            _ = await settingsService.ReadAsync(deviceId, cancellationToken);
        }

        var snapshot = await settingsService.GetLastSnapshotAsync(deviceId, cancellationToken);
        if (snapshot is null)
        {
            return [];
        }

        var validations = (await store.GetEndpointValidationResultsAsync(deviceId, cancellationToken))
            .GroupBy(static result => result.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(result => result.CapturedAt).First(), StringComparer.OrdinalIgnoreCase);

        var normalized = new List<NormalizedSettingField>();
        foreach (var group in snapshot.Groups)
        {
            foreach (var value in group.Values.Values)
            {
                var endpoint = value.SourceEndpoint ?? value.Key;
                var parser = EndpointParsers.FirstOrDefault(definition => definition.IsMatch(endpoint));
                if (parser is null)
                {
                    continue;
                }

                var validation = validations.GetValueOrDefault(endpoint);
                foreach (var fieldParser in parser.Fields)
                {
                    var source = TryGetPathValue(value.Value, fieldParser.RawSourcePath);
                    if (source is null)
                    {
                        continue;
                    }

                    normalized.Add(new NormalizedSettingField
                    {
                        DeviceId = deviceId,
                        GroupKind = fieldParser.GroupKind,
                        GroupName = fieldParser.GroupName,
                        FieldKey = fieldParser.FieldKey,
                        DisplayName = fieldParser.DisplayName,
                        AdapterName = snapshot.AdapterName,
                        ParserName = $"{parser.Name}:{fieldParser.ParserName}",
                        TypedValue = fieldParser.Convert(source),
                        RawValue = source.DeepClone(),
                        SourceEndpoint = endpoint,
                        RawSourcePath = fieldParser.RawSourcePath,
                        FirmwareFingerprint = validation?.FirmwareFingerprint,
                        Validity = ResolveValidity(validation),
                        Confidence = ResolveConfidence(validation),
                        ReadVerified = validation?.ReadVerified == true,
                        WriteVerified = validation?.WriteVerified == true,
                        PersistsAfterReboot = validation?.PersistsAfterReboot == true,
                        ExpertOnly = fieldParser.ExpertOnly || validation?.DisruptionClass is DisruptionClass.FactoryReset or DisruptionClass.FirmwareUpgrade,
                        DisruptionClass = validation?.DisruptionClass ?? fieldParser.DisruptionClass
                    });
                }
            }
        }

        var deduped = normalized
            .GroupBy(field => $"{field.DeviceId:N}:{field.SourceEndpoint}:{field.FieldKey}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(field => field.ReadVerified).ThenByDescending(field => field.WriteVerified).ThenByDescending(field => field.CapturedAt).First())
            .ToList();

        await store.SaveNormalizedSettingFieldsAsync(deduped, cancellationToken);
        logger.LogInformation("Endpoint-aware normalization produced {Count} fields for {DeviceId}", deduped.Count, deviceId);
        return ToSnapshots(deviceId, snapshot.AdapterName, deduped);
    }

    public async Task<IReadOnlyCollection<TypedSettingGroupSnapshot>> GetTypedSettingsAsync(Guid deviceId, CancellationToken cancellationToken)
        => ToSnapshots(deviceId, string.Empty, await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken));

    public async Task<WriteResult?> ApplyTypedFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool expertOverride, CancellationToken cancellationToken)
    {
        var fields = await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken);
        var field = fields
            .OrderByDescending(static item => item.CapturedAt)
            .FirstOrDefault(item => item.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            return new WriteResult { Success = false, Message = $"Unknown field '{fieldKey}'." };
        }

        if (!expertOverride && !field.WriteVerified)
        {
            return new WriteResult { Success = false, Message = $"Field '{fieldKey}' is not write-verified. Use expert override or validate first." };
        }

        var endpointFields = fields
            .Where(candidate => candidate.SourceEndpoint.Equals(field.SourceEndpoint, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => candidate.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(candidate => candidate.CapturedAt).First())
            .ToList();

        var payload = await BuildEndpointPayloadAsync(deviceId, field.SourceEndpoint, endpointFields, fieldKey, value, cancellationToken);
        return await settingsService.WriteAsync(deviceId, new WritePlan
        {
            GroupName = field.GroupName,
            Endpoint = field.SourceEndpoint,
            Method = "PUT",
            AdapterName = field.AdapterName,
            Payload = payload,
            SnapshotBeforeWrite = true,
            RequireWriteVerification = !expertOverride
        }, cancellationToken);
    }

    private async Task<JsonObject> BuildEndpointPayloadAsync(
        Guid deviceId,
        string endpoint,
        IReadOnlyCollection<NormalizedSettingField> endpointFields,
        string changedFieldKey,
        JsonNode? changedValue,
        CancellationToken cancellationToken)
    {
        JsonObject payload;
        var snapshot = await settingsService.GetLastSnapshotAsync(deviceId, cancellationToken);
        var endpointNode = snapshot?.Groups
            .SelectMany(static group => group.Values.Values)
            .FirstOrDefault(value => (value.SourceEndpoint ?? value.Key).Equals(endpoint, StringComparison.OrdinalIgnoreCase))
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
            var valueToSet = field.FieldKey.Equals(changedFieldKey, StringComparison.OrdinalIgnoreCase) ? changedValue : field.TypedValue;
            SetPathValue(payload, field.RawSourcePath, valueToSet?.DeepClone());
        }

        return payload;
    }

    private static IReadOnlyCollection<TypedSettingGroupSnapshot> ToSnapshots(Guid deviceId, string adapterName, IReadOnlyCollection<NormalizedSettingField> fields)
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
                EditorHints = EditorHints.Where(hint => group.Any(field => field.FieldKey.Equals(hint.FieldKey, StringComparison.OrdinalIgnoreCase))).ToList()
            })
            .OrderBy(static group => group.GroupKind)
            .ToList();

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

    private static FieldValidityState ResolveValidity(EndpointValidationResult? validation)
    {
        if (validation is null)
        {
            return FieldValidityState.Unverified;
        }

        if (validation.ReadVerified && validation.WriteVerified)
        {
            return FieldValidityState.Proven;
        }

        return validation.ReadVerified ? FieldValidityState.Inferred : FieldValidityState.Unverified;
    }

    private static string ResolveConfidence(EndpointValidationResult? validation)
    {
        if (validation is null)
        {
            return "unverified";
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

    private static IReadOnlyCollection<EndpointParserDefinition> BuildEndpointParsers()
        =>
        [
            new EndpointParserDefinition(
                "video-channel-parser",
                endpoint => endpoint.Contains("/NetSDK/Video/input/channel/", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("/NetSDK/Video/encode/channel/", StringComparison.OrdinalIgnoreCase),
                [
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "codec", "Codec", "$.codec", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "profile", "Profile", "$.profile", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "resolution", "Resolution", "$.resolution", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "bitrateMode", "Bitrate Mode", "$.bitrateMode", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "bitrate", "Bitrate", "$.bitrate", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "frameRate", "Frame Rate", "$.frameRate", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "keyframeInterval", "Keyframe Interval", "$.gop", "number", DisruptionClass.Safe)
                ]),
            new EndpointParserDefinition(
                "image-parser",
                endpoint => endpoint.Contains("/NetSDK/Image", StringComparison.OrdinalIgnoreCase),
                [
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "brightness", "Brightness", "$.brightness", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "contrast", "Contrast", "$.contrast", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "hue", "Hue", "$.hue", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "saturation", "Saturation", "$.saturation", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "sharpness", "Sharpness", "$.sharpness", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "denoise", "Denoise", "$.denoise", "number", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "wdr", "WDR", "$.wdr", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "dayNight", "Day/Night", "$.dayNight", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "irCut", "IR Cut", "$.irCut", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "whiteLight", "White Light", "$.whiteLight", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "infrared", "Infrared", "$.infrared", "enum", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "exposure", "Exposure", "$.exposure", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "awb", "AWB", "$.awb", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.VideoImage, "Video / Image", "osd", "OSD", "$.osd.title", "string", DisruptionClass.Safe)
                ]),
            new EndpointParserDefinition(
                "network-parser",
                endpoint => endpoint.Contains("/NetSDK/Network/interfaces", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("/NetSDK/Network/Dns", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("/NetSDK/Network/Ports", StringComparison.OrdinalIgnoreCase),
                [
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "ip", "IP Address", "$.ip", "ip", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "netmask", "Netmask", "$.netmask", "ip", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "gateway", "Gateway", "$.gateway", "ip", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "dns", "DNS", "$.dns", "ip", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "ports", "HTTP Port", "$.httpPort", "number", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "dhcpMode", "DHCP Mode", "$.dhcp", "bool", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "esee", "ESEE", "$.esee", "string", DisruptionClass.NetworkChanging)
                ]),
            new EndpointParserDefinition(
                "wireless-parser",
                endpoint => endpoint.Contains("wireless", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("/NetSDK/Factory", StringComparison.OrdinalIgnoreCase),
                [
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "wirelessMode", "Wireless Mode", "$.wirelessMode", "enum", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apMode", "AP Mode", "$.ap.mode", "enum", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apSsid", "AP SSID", "$.ap.ssid", "string", DisruptionClass.NetworkChanging),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apPsk", "AP PSK", "$.ap.psk", "string", DisruptionClass.NetworkChanging, ExpertOnly: true),
                    new FieldParser(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apChannel", "AP Channel", "$.ap.channel", "number", DisruptionClass.NetworkChanging)
                ]),
            new EndpointParserDefinition(
                "users-parser",
                endpoint => endpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("/NetSDK/System/deviceInfo", StringComparison.OrdinalIgnoreCase),
                [
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "userList", "User List", "$.users", "array", DisruptionClass.ServiceImpacting, ExpertOnly: true),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "serial", "Serial", "$.serial", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "model", "Model", "$.model", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "firmware", "Firmware", "$.firmware", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "mac", "MAC", "$.mac", "string", DisruptionClass.Safe),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "reboot", "Reboot", "$.reboot", "bool", DisruptionClass.Reboot, ExpertOnly: true),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "factoryDefault", "Factory Default", "$.factoryDefault", "bool", DisruptionClass.FactoryReset, ExpertOnly: true),
                    new FieldParser(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "firmwareUpgrade", "Firmware Upgrade", "$.upgrade", "string", DisruptionClass.FirmwareUpgrade, ExpertOnly: true)
                ])
        ];

    private static IReadOnlyCollection<EditorHint> BuildEditorHints()
        =>
        [
            new() { FieldKey = "codec", Label = "Codec", EditorKind = "enum", EnumValues = new JsonArray("H264", "H265"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "profile", Label = "Profile", EditorKind = "enum", EnumValues = new JsonArray("Baseline", "Main", "High"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "dayNight", Label = "Day/Night", EditorKind = "enum", EnumValues = new JsonArray("Auto", "Day", "Night"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "wdr", Label = "WDR", EditorKind = "enum", EnumValues = new JsonArray("Off", "On"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "irCut", Label = "IR Cut", EditorKind = "enum", EnumValues = new JsonArray("Auto", "Day", "Night"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "bitrate", Label = "Bitrate", EditorKind = "number", Min = 64, Max = 16384, Unit = "kbps", DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "frameRate", Label = "Frame Rate", EditorKind = "number", Min = 1, Max = 60, Unit = "fps", DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "brightness", Label = "Brightness", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "contrast", Label = "Contrast", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "saturation", Label = "Saturation", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "hue", Label = "Hue", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "sharpness", Label = "Sharpness", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "ip", Label = "IP Address", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "netmask", Label = "Netmask", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "gateway", Label = "Gateway", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "dns", Label = "DNS", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "ports", Label = "Ports", EditorKind = "number", Min = 1, Max = 65535, DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "apSsid", Label = "AP SSID", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "apPsk", Label = "AP PSK", EditorKind = "password", DisruptionClass = DisruptionClass.NetworkChanging, ExpertOnly = true },
            new() { FieldKey = "apChannel", Label = "AP Channel", EditorKind = "number", Min = 1, Max = 14, DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "reboot", Label = "Reboot", EditorKind = "button", DisruptionClass = DisruptionClass.Reboot, ExpertOnly = true },
            new() { FieldKey = "factoryDefault", Label = "Factory Default", EditorKind = "button", DisruptionClass = DisruptionClass.FactoryReset, ExpertOnly = true }
        ];

    private sealed record EndpointParserDefinition(string Name, Func<string, bool> IsMatch, IReadOnlyCollection<FieldParser> Fields);

    private sealed record FieldParser(
        TypedSettingGroupKind GroupKind,
        string GroupName,
        string FieldKey,
        string DisplayName,
        string RawSourcePath,
        string ParserName,
        DisruptionClass DisruptionClass,
        bool ExpertOnly = false)
    {
        public JsonNode? Convert(JsonNode source)
        {
            if (ParserName.Equals("number", StringComparison.OrdinalIgnoreCase) && source is JsonValue value)
            {
                if (value.TryGetValue<decimal>(out var num))
                {
                    return JsonValue.Create(num);
                }
            }

            if (ParserName.Equals("bool", StringComparison.OrdinalIgnoreCase) && source is JsonValue boolValue && boolValue.TryGetValue<bool>(out var flag))
            {
                return JsonValue.Create(flag);
            }

            return source.DeepClone();
        }
    }

    private sealed record PathSegment(string? Name, int? Index);
}
