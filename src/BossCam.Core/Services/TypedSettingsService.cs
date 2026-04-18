using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class TypedSettingsService(
    IApplicationStore store,
    SettingsService settingsService,
    ILogger<TypedSettingsService> logger)
{
    private static readonly IReadOnlyCollection<FieldDefinition> FieldDefinitions = BuildFieldDefinitions();
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

        var fields = new List<NormalizedSettingField>();
        foreach (var group in snapshot.Groups)
        {
            foreach (var value in group.Values.Values)
            {
                var endpoint = value.SourceEndpoint ?? value.Key;
                var validation = validations.GetValueOrDefault(endpoint);
                var flattened = FlattenNode(value.Value, endpoint);

                foreach (var definition in FieldDefinitions)
                {
                    var match = flattened.FirstOrDefault(item => definition.Match(item.Path));
                    if (match is null)
                    {
                        continue;
                    }

                    fields.Add(new NormalizedSettingField
                    {
                        DeviceId = deviceId,
                        GroupKind = definition.GroupKind,
                        GroupName = definition.GroupName,
                        FieldKey = definition.FieldKey,
                        DisplayName = definition.DisplayName,
                        AdapterName = snapshot.AdapterName,
                        ParserName = definition.ParserName,
                        TypedValue = definition.Convert(match.Value),
                        RawValue = match.Value?.DeepClone(),
                        SourceEndpoint = endpoint,
                        FirmwareFingerprint = validation?.FirmwareFingerprint,
                        Validity = ResolveValidity(validation),
                        Confidence = ResolveConfidence(validation),
                        ReadVerified = validation?.ReadVerified == true,
                        WriteVerified = validation?.WriteVerified == true,
                        PersistsAfterReboot = validation?.PersistsAfterReboot == true,
                        ExpertOnly = definition.ExpertOnly || validation?.DisruptionClass is DisruptionClass.FactoryReset or DisruptionClass.FirmwareUpgrade,
                        DisruptionClass = validation?.DisruptionClass ?? definition.DisruptionClass
                    });
                }
            }
        }

        var deduped = fields
            .GroupBy(field => $"{field.DeviceId:N}:{field.GroupKind}:{field.FieldKey}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(field => field.ReadVerified).ThenByDescending(field => field.WriteVerified).ThenByDescending(field => field.CapturedAt).First())
            .ToList();

        await store.SaveNormalizedSettingFieldsAsync(deduped, cancellationToken);
        logger.LogInformation("Normalized {Count} fields for device {DeviceId}", deduped.Count, deviceId);
        return ToSnapshots(deviceId, snapshot.AdapterName, deduped);
    }

    public async Task<IReadOnlyCollection<TypedSettingGroupSnapshot>> GetTypedSettingsAsync(Guid deviceId, CancellationToken cancellationToken)
        => ToSnapshots(deviceId, string.Empty, await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken));

    public async Task<WriteResult?> ApplyTypedFieldAsync(Guid deviceId, string fieldKey, JsonNode? value, bool expertOverride, CancellationToken cancellationToken)
    {
        var field = (await store.GetNormalizedSettingFieldsAsync(deviceId, cancellationToken))
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

        var payload = new JsonObject { [field.FieldKey] = value?.DeepClone() };
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

    private static IEnumerable<FlattenedNode> FlattenNode(JsonNode? node, string endpoint)
    {
        if (node is null)
        {
            yield break;
        }

        var stack = new Stack<FlattenedNode>();
        stack.Push(new FlattenedNode(endpoint, node));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Value is JsonObject obj)
            {
                foreach (var property in obj)
                {
                    stack.Push(new FlattenedNode($"{current.Path}.{property.Key}", property.Value));
                }
            }
            else if (current.Value is JsonArray arr)
            {
                for (var index = 0; index < arr.Count; index++)
                {
                    stack.Push(new FlattenedNode($"{current.Path}[{index}]", arr[index]));
                }
            }
            else
            {
                yield return current;
            }
        }
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

    private static IReadOnlyCollection<FieldDefinition> BuildFieldDefinitions()
        =>
        [
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "codec", "Codec", "scalar", DisruptionClass.Safe, false, ["codec"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "profile", "Profile", "scalar", DisruptionClass.Safe, false, ["profile"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "resolution", "Resolution", "scalar", DisruptionClass.Safe, false, ["resolution"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "bitrateMode", "Bitrate Mode", "scalar", DisruptionClass.Safe, false, ["bitratemode"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "bitrate", "Bitrate", "number", DisruptionClass.Safe, false, ["bitrate"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "frameRate", "Frame Rate", "number", DisruptionClass.Safe, false, ["framerate"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "keyframeInterval", "Keyframe Interval", "number", DisruptionClass.Safe, false, ["gop"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "brightness", "Brightness", "number", DisruptionClass.Safe, false, ["brightness"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "contrast", "Contrast", "number", DisruptionClass.Safe, false, ["contrast"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "hue", "Hue", "number", DisruptionClass.Safe, false, ["hue"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "saturation", "Saturation", "number", DisruptionClass.Safe, false, ["saturation"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "sharpness", "Sharpness", "number", DisruptionClass.Safe, false, ["sharpness"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "denoise", "Denoise", "number", DisruptionClass.Safe, false, ["denoise"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "wdr", "WDR", "scalar", DisruptionClass.Safe, false, ["wdr"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "dayNight", "Day/Night", "scalar", DisruptionClass.Safe, false, ["daynight"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "irCut", "IR Cut", "scalar", DisruptionClass.Safe, false, ["ircut"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "whiteLight", "White Light", "scalar", DisruptionClass.Safe, false, ["whitelight"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "infrared", "Infrared", "scalar", DisruptionClass.Safe, false, ["infrared"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "exposure", "Exposure", "scalar", DisruptionClass.Safe, false, ["exposure"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "awb", "AWB", "scalar", DisruptionClass.Safe, false, ["awb"]),
            new(TypedSettingGroupKind.VideoImage, "Video / Image", "osd", "OSD", "scalar", DisruptionClass.Safe, false, ["osd"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "ip", "IP Address", "scalar", DisruptionClass.NetworkChanging, false, [".ip"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "netmask", "Netmask", "scalar", DisruptionClass.NetworkChanging, false, ["netmask"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "gateway", "Gateway", "scalar", DisruptionClass.NetworkChanging, false, ["gateway"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "dns", "DNS", "scalar", DisruptionClass.NetworkChanging, false, ["dns"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "ports", "Ports", "number", DisruptionClass.NetworkChanging, false, ["port"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "dhcpMode", "DHCP Mode", "scalar", DisruptionClass.NetworkChanging, false, ["dhcp"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "esee", "ESEE", "scalar", DisruptionClass.NetworkChanging, false, ["esee"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "wirelessMode", "Wireless Mode", "scalar", DisruptionClass.NetworkChanging, false, ["wirelessmode"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apMode", "AP Mode", "scalar", DisruptionClass.NetworkChanging, false, ["apmode"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apSsid", "AP SSID", "scalar", DisruptionClass.NetworkChanging, false, ["ssid"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apPsk", "AP PSK", "scalar", DisruptionClass.NetworkChanging, true, ["psk"]),
            new(TypedSettingGroupKind.NetworkWireless, "Network / Wireless", "apChannel", "AP Channel", "number", DisruptionClass.NetworkChanging, false, ["channel"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "userList", "User List", "scalar", DisruptionClass.ServiceImpacting, true, ["user"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "serial", "Serial", "scalar", DisruptionClass.Safe, false, ["serial"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "model", "Model", "scalar", DisruptionClass.Safe, false, ["model"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "firmware", "Firmware", "scalar", DisruptionClass.Safe, false, ["firmware"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "mac", "MAC", "scalar", DisruptionClass.Safe, false, ["mac"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "reboot", "Reboot", "scalar", DisruptionClass.Reboot, true, ["reboot"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "factoryDefault", "Factory Default", "scalar", DisruptionClass.FactoryReset, true, ["default"]),
            new(TypedSettingGroupKind.UsersMaintenance, "Users / Maintenance", "firmwareUpgrade", "Firmware Upgrade", "scalar", DisruptionClass.FirmwareUpgrade, true, ["upgrade"]),
            new(TypedSettingGroupKind.MotionPrivacyAlarms, "Motion / Privacy / Alarms", "motionEnable", "Motion Enable", "scalar", DisruptionClass.Safe, false, ["motion"]),
            new(TypedSettingGroupKind.MotionPrivacyAlarms, "Motion / Privacy / Alarms", "motionSensitivity", "Motion Sensitivity", "number", DisruptionClass.Safe, false, ["sensitivity"]),
            new(TypedSettingGroupKind.MotionPrivacyAlarms, "Motion / Privacy / Alarms", "privacyMask", "Privacy Mask", "scalar", DisruptionClass.Safe, false, ["privacy"]),
            new(TypedSettingGroupKind.MotionPrivacyAlarms, "Motion / Privacy / Alarms", "alarmInput", "Alarm Input", "scalar", DisruptionClass.Safe, false, ["alarminput"]),
            new(TypedSettingGroupKind.MotionPrivacyAlarms, "Motion / Privacy / Alarms", "alarmOutput", "Alarm Output", "scalar", DisruptionClass.Safe, false, ["alarmoutput"]),
            new(TypedSettingGroupKind.PtzOptics, "PTZ / Optics", "ptzMove", "PTZ Move", "scalar", DisruptionClass.ServiceImpacting, false, ["ptz"]),
            new(TypedSettingGroupKind.PtzOptics, "PTZ / Optics", "preset", "Preset", "scalar", DisruptionClass.ServiceImpacting, false, ["preset"]),
            new(TypedSettingGroupKind.PtzOptics, "PTZ / Optics", "zoom", "Zoom", "number", DisruptionClass.ServiceImpacting, false, ["zoom"]),
            new(TypedSettingGroupKind.PtzOptics, "PTZ / Optics", "focus", "Focus", "number", DisruptionClass.ServiceImpacting, false, ["focus"]),
            new(TypedSettingGroupKind.PtzOptics, "PTZ / Optics", "iris", "Iris", "number", DisruptionClass.ServiceImpacting, false, ["iris"]),
            new(TypedSettingGroupKind.StoragePlayback, "Storage / Playback", "sdCardState", "SD Card", "scalar", DisruptionClass.Safe, false, ["sdcard"]),
            new(TypedSettingGroupKind.StoragePlayback, "Storage / Playback", "storageFormat", "Format", "scalar", DisruptionClass.ServiceImpacting, true, ["format"]),
            new(TypedSettingGroupKind.StoragePlayback, "Storage / Playback", "recordingMetadata", "Recording Metadata", "scalar", DisruptionClass.Safe, false, ["record"]),
            new(TypedSettingGroupKind.StoragePlayback, "Storage / Playback", "playbackSearch", "Playback Search", "scalar", DisruptionClass.Safe, false, ["search"])
        ];

    private static IReadOnlyCollection<EditorHint> BuildEditorHints()
        =>
        [
            new() { FieldKey = "codec", Label = "Codec", EditorKind = "enum", EnumValues = new JsonArray("H264", "H265"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "profile", Label = "Profile", EditorKind = "enum", EnumValues = new JsonArray("Baseline", "Main", "High"), DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "bitrate", Label = "Bitrate", EditorKind = "number", Min = 64, Max = 16384, Unit = "kbps", DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "frameRate", Label = "Frame Rate", EditorKind = "number", Min = 1, Max = 60, Unit = "fps", DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "brightness", Label = "Brightness", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "contrast", Label = "Contrast", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "saturation", Label = "Saturation", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "hue", Label = "Hue", EditorKind = "number", Min = 0, Max = 100, DisruptionClass = DisruptionClass.Safe },
            new() { FieldKey = "ip", Label = "IP Address", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "gateway", Label = "Gateway", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "dns", Label = "DNS", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "ports", Label = "Ports", EditorKind = "number", Min = 1, Max = 65535, DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "apSsid", Label = "AP SSID", EditorKind = "text", DisruptionClass = DisruptionClass.NetworkChanging },
            new() { FieldKey = "apPsk", Label = "AP PSK", EditorKind = "password", DisruptionClass = DisruptionClass.NetworkChanging, ExpertOnly = true },
            new() { FieldKey = "reboot", Label = "Reboot", EditorKind = "button", DisruptionClass = DisruptionClass.Reboot, ExpertOnly = true },
            new() { FieldKey = "factoryDefault", Label = "Factory Default", EditorKind = "button", DisruptionClass = DisruptionClass.FactoryReset, ExpertOnly = true }
        ];

    private sealed record FlattenedNode(string Path, JsonNode? Value);
    private sealed record FieldDefinition(
        TypedSettingGroupKind GroupKind,
        string GroupName,
        string FieldKey,
        string DisplayName,
        string ParserName,
        DisruptionClass DisruptionClass,
        bool ExpertOnly,
        IReadOnlyCollection<string> Tokens)
    {
        public bool Match(string path)
        {
            var lower = path.ToLowerInvariant();
            return Tokens.All(token => lower.Contains(token.ToLowerInvariant(), StringComparison.Ordinal));
        }

        public JsonNode? Convert(JsonNode? node)
        {
            if (ParserName.Equals("number", StringComparison.OrdinalIgnoreCase) && node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var intValue))
                {
                    return JsonValue.Create(intValue);
                }

                if (value.TryGetValue<decimal>(out var decimalValue))
                {
                    return JsonValue.Create(decimalValue);
                }
            }

            return node?.DeepClone();
        }
    }
}
