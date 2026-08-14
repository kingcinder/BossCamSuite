using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class EndpointContractCatalogService(
    IApplicationStore store,
    ILogger<EndpointContractCatalogService> logger) : IEndpointContractCatalog
{
    // Declared BEFORE SeedContracts: C# initializes static fields in declaration order, and
    // BuildSeedContracts() references OnvifDeviceScope at static-init time — if this field came
    // after SeedContracts it would still be null while the ONVIF seed contracts were built, and
    // ScopeMatches would NRE on every null-scope contract (regression seen on 5523-W pass).
    // ONVIF-discovered devices (GenericOnvif / WVC) only — never NetSDK "IPC" devices. Shared
    // across every ONVIF seed contract so DeviceType scoping cannot drift between call sites.
    private static readonly ContractScope OnvifDeviceScope = new()
    {
        FirmwareFingerprintPattern = "*",
        DeviceType = "ONVIF"
    };

    private static readonly IReadOnlyCollection<EndpointContract> SeedContracts = BuildSeedContracts();

    public async Task<IReadOnlyCollection<EndpointContract>> GetContractsAsync(CancellationToken cancellationToken)
    {
        var existing = await store.GetEndpointContractsAsync(cancellationToken);
        var merged = existing
            .Concat(SeedContracts)
            .GroupBy(contract => contract.ContractKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        if (existing.Count == 0 || merged.Count != existing.Count || !merged.All(contract => existing.Any(current => current.ContractKey.Equals(contract.ContractKey, StringComparison.OrdinalIgnoreCase) && current.Scope == contract.Scope)))
        {
            await store.SaveEndpointContractsAsync(merged, cancellationToken);
            logger.LogInformation("Upserted {Count} endpoint contracts from seed+store merge", merged.Count);
        }
        return merged;
    }

    public async Task<IReadOnlyCollection<EndpointContract>> GetContractsForDeviceAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var all = await GetContractsAsync(cancellationToken);
        var fingerprint = $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";
        var scoped = all.Where(contract => ScopeMatches(contract.Scope, device, fingerprint)).ToList();
        var fixtures = await store.GetContractFixturesAsync(device.Id, 5000, cancellationToken);
        if (fixtures.Count == 0)
        {
            return scoped;
        }

        // Inferred -> Proven promotion is fixture-driven and firmware-scoped.
        return scoped.Select(contract => ApplyFixtureEvidence(contract, fixtures)).ToList();
    }

    public EndpointContract? MatchContract(string endpoint, string method, IEnumerable<EndpointContract> contracts)
    {
        var normalized = NormalizeEndpoint(endpoint);
        return contracts.FirstOrDefault(contract =>
            contract.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
            && EndpointPatternMatches(contract.Endpoint, normalized));
    }

    private static bool ScopeMatches(ContractScope scope, DeviceIdentity device, string fingerprint)
    {
        if (!WildcardMatch(scope.FirmwareFingerprintPattern, fingerprint))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(scope.DeviceType) && !string.Equals(scope.DeviceType, device.DeviceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // If hardware model is unknown at runtime, do not over-filter inferred contracts.
        if (!string.IsNullOrWhiteSpace(scope.HardwareModelPattern)
            && !string.IsNullOrWhiteSpace(device.HardwareModel)
            && !WildcardMatch(scope.HardwareModelPattern, device.HardwareModel))
        {
            return false;
        }

        return true;
    }

    private static bool EndpointPatternMatches(string pattern, string endpoint)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(endpoint, regex, RegexOptions.IgnoreCase);
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static string NormalizeEndpoint(string endpoint)
        => endpoint
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

    private static EndpointContract ApplyFixtureEvidence(EndpointContract contract, IReadOnlyCollection<EndpointContractFixture> fixtures)
    {
        var contractFixtures = fixtures
            .Where(fixture => fixture.ContractKey.Equals(contract.ContractKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contractFixtures.Count == 0)
        {
            return contract;
        }

        var fields = contract.Fields.Select(field =>
        {
            var matchedFixture = contractFixtures.FirstOrDefault(fixture => TryGetPathValue(fixture.ResponseBody, field.SourcePath) is not null);
            if (matchedFixture is null)
            {
                return field;
            }

            return field with
            {
                Evidence = field.Evidence with
                {
                    TruthState = ContractTruthState.Proven,
                    Source = "live-fixture",
                    FixturePath = matchedFixture.FixturePath,
                    ObservedAt = matchedFixture.CapturedAt,
                    Notes = "Promoted from transcript-backed fixture evidence"
                }
            };
        }).ToList();

        var truthState = fields.All(field => field.Evidence.TruthState == ContractTruthState.Proven)
            ? ContractTruthState.Proven
            : fields.Any(field => field.Evidence.TruthState == ContractTruthState.Proven)
                ? ContractTruthState.Inferred
                : contract.TruthState;

        return contract with
        {
            Fields = fields,
            TruthState = truthState
        };
    }

    private static JsonNode? TryGetPathValue(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var cleaned = path.Trim();
        if (cleaned.StartsWith("$.", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }
        else if (cleaned.StartsWith("$", StringComparison.Ordinal))
        {
            cleaned = cleaned[1..];
        }

        JsonNode? current = root;
        foreach (var segment in cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is null)
            {
                return null;
            }

            if (segment.Contains('[', StringComparison.Ordinal))
            {
                var name = segment[..segment.IndexOf('[', StringComparison.Ordinal)];
                var indexText = segment[(segment.IndexOf('[', StringComparison.Ordinal) + 1)..segment.IndexOf(']', StringComparison.Ordinal)];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out current))
                    {
                        return null;
                    }
                }

                if (!int.TryParse(indexText, out var index) || current is not JsonArray arr || index < 0 || index >= arr.Count)
                {
                    return null;
                }

                current = arr[index];
            }
            else
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
                {
                    return null;
                }
            }
        }

        return current;
    }

    private static IReadOnlyCollection<EndpointContract> BuildSeedContracts()
    {
        // Keep scope broad when hardware model is missing; field evidence still tracks proven vs inferred.
        var scope = new ContractScope { FirmwareFingerprintPattern = "*", HardwareModelPattern = "*" };
        // ONVIF imaging contracts apply only to ONVIF-discovered devices (GenericOnvif / WVC); the
        // OnvifImagingControlAdapter emits these endpoints so the SPA Features panel surfaces the
        // real field→SOAP controls instead of dumping them into "unmapped:onvif:*" diagnostics.
        var onvifScope = OnvifDeviceScope;
        return
        [
            // Video / Image
            new EndpointContract
            {
                ContractKey = "video.input.channel.0",
                Endpoint = "/NetSDK/Video/input/channel/*",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false, RequiredRootFields = ["id", "enabled"] },
                Fields =
                [
                    NumericField("brightness", "Brightness", "$.brightnessLevel", 0, 100),
                    NumericField("contrast", "Contrast", "$.contrastLevel", 0, 100),
                    NumericField("saturation", "Saturation", "$.saturationLevel", 0, 100),
                    NumericField("sharpness", "Sharpness", "$.sharpnessLevel", 0, 100),
                    NumericField("hue", "Hue", "$.hueLevel", 0, 100),
                    NumericField("gamma", "Gamma", "$.gammaLevel", 0, 100),
                    new ContractField
                    {
                        Key = "mirror",
                        DisplayName = "Mirror",
                        SourcePath = "$.mirrorEnabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "live-observed" }
                    },
                    new ContractField
                    {
                        Key = "flip",
                        DisplayName = "Flip",
                        SourcePath = "$.flipEnabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "live-observed" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.encode.channel",
                Endpoint = "/NetSDK/Video/encode/channel/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    NumericField("bitrate", "Bitrate", "$.constantBitRate", 64, 16384),
                    NumericField("frameRate", "Frame Rate", "$.frameRate", 1, 60),
                    NumericField("keyframeInterval", "Keyframe Interval", "$.keyFrameInterval", 1, 240),
                    EnumField("codec", "Codec", "$.codecType", ["H.264", "H.265", "H.264+", "H.265+", "MJPEG"]),
                    EnumField("profile", "Profile", "$.h264Profile", ["baseline", "main", "high"]),
                    EnumField("bitrateMode", "Bitrate Mode", "$.bitRateControlType", ["CBR", "VBR"]),
                    EnumField("definition", "Definition", "$.definition", ["auto", "fluency", "HD", "BD"]),
                    StringField("resolution", "Resolution", "$.resolution")
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.encode.channel.keyframe",
                Endpoint = "/netsdk/video/encode/channel/*/requestKeyFrame",
                Method = "POST",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                ExpertOnly = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    new ContractField
                    {
                        Key = "requestKeyframe",
                        DisplayName = "Request Keyframe",
                        SourcePath = "$.requestKeyframe",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "ipcamsuite-mining",
                            Notes = "Observed channel-indexed keyframe trigger endpoint in NetSdk strings."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "audio.input.channel",
                Endpoint = "/NetSDK/Audio/input/channel/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "audioEnabled",
                        DisplayName = "Audio Enabled",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    NumericField("audioInputVolume", "Audio Input Volume", "$.volume", 0, 100)
                ]
            },
            new EndpointContract
            {
                ContractKey = "audio.encode.channel",
                Endpoint = "/NetSDK/Audio/encode/channel/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "audioEnabled",
                        DisplayName = "Audio Enabled",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    NumericField("audioBitRate", "Audio Bitrate", "$.bitRate", 8, 320),
                    NumericField("audioSampleRate", "Audio Sample Rate", "$.sampleRate", 8000, 96000)
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.profile",
                Endpoint = "/NetSDK/Image/*",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    NumericField("denoise", "Denoise", "$.denoise3d.denoise3dStrength", 0, 5),
                    NumericField("manualSharpness", "Manual Sharpness", "$.manualSharpness.sharpnessLevel", 0, 255),
                    NumericField("wdrStrength", "WDR Strength", "$.WDR.WDRStrength", 1, 5),
                    new ContractField
                    {
                        Key = "wdr",
                        DisplayName = "WDR",
                        SourcePath = "$.WDR.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "live-observed" }
                    },
                    EnumField("dayNight", "Day/Night", "$.irCutFilter.irCutMode", ["auto", "daylight", "night", "ir", "light", "smart"]),
                    EnumField("irMode", "IR Mode", "$.irCutFilter.irCutMode", ["auto", "daylight", "night", "ir", "light", "smart"]),
                    EnumField("irCut", "IR Cut", "$.irCutFilter.irCutMode", ["auto", "daylight", "night", "ir", "light", "smart"]),
                    EnumField("irCutMethod", "IR Cut Method", "$.irCutFilter.irCutControlMode", ["software", "hardware"]),
                    EnumField("sceneMode", "Scene Mode", "$.sceneMode", ["auto", "indoor", "outdoor"]),
                    EnumField("exposure", "Exposure", "$.exposureMode", ["auto", "bright", "dark"]),
                    EnumField("awb", "AWB", "$.awbMode", ["auto", "indoor", "outdoor"]),
                    EnumField("lowlight", "Lowlight", "$.lowlightMode", ["close", "only night", "day-night", "auto"]),
                    NumericField("whiteLight", "White Light", "$.whiteLightLevel", 0, 100),
                    NumericField("infrared", "Infrared", "$.infraRedLevel", 0, 100),
                    StringField("osd", "OSD", "$.osd.title")
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.overlay.channelName",
                Endpoint = "/NetSDK/Video/encode/channel/*/channelNameOverlay[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "osdChannelNameEnabled",
                        DisplayName = "Channel Name Overlay",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    StringField("osdChannelNameText", "Channel Name Text", "$.name")
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.overlay.datetime",
                Endpoint = "/NetSDK/Video/encode/channel/*/datetimeOverlay[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "osdDateTimeEnabled",
                        DisplayName = "Date/Time Overlay",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    EnumField("osdDateFormat", "Date Format", "$.dateFormat", ["YYYY/MM/DD", "MM/DD/YYYY", "DD/MM/YYYY", "YYYY-MM-DD", "MM-DD-YYYY", "DD-MM-YYYY"]),
                    EnumField("osdTimeFormat", "Time Format", "$.timeFormat", ["12", "24"]),
                    new ContractField
                    {
                        Key = "osdDisplayWeek",
                        DisplayName = "Display Weekday",
                        SourcePath = "$.displayWeek",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.snapshot.channel",
                Endpoint = "/NetSDK/Video/encode/channel/*/snapShot[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("snapShotImageType", "Snapshot Type", "$.snapShotImageType", ["JPEG", "BMP"]),
                    NumericField("captureWidth", "Capture Width", "$.captureWidth", 320, 4096)
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.manualSharpness",
                Endpoint = "/NetSDK/Image/manualSharpness[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    NumericField("manualSharpness", "Manual Sharpness", "$.sharpnessLevel", 0, 255)
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.wdr",
                Endpoint = "/NetSDK/Image/wdr",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "wdr",
                        DisplayName = "WDR",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "live-observed" }
                    },
                    NumericField("wdrStrength", "WDR Strength", "$.WDRStrength", 1, 5)
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.denoise3d",
                Endpoint = "/NetSDK/Image/denoise3d",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    NumericField("denoise", "Denoise", "$.denoise3dStrength", 0, 5)
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.ircut",
                Endpoint = "/NetSDK/Image/irCutfilter",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("irCut", "IR Cut", "$.irCutMode", ["auto", "daylight", "night", "ir", "light", "smart"]),
                    EnumField("irMode", "IR Mode", "$.irCutMode", ["auto", "daylight", "night", "ir", "light", "smart"]),
                    EnumField("irCutMethod", "IR Cut Method", "$.irCutControlMode", ["software", "hardware"])
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.whiteLight.private",
                Endpoint = "/NetSDK/Factory?cmd=WhiteLightCtrl",
                Method = "PUT",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                ExpertOnly = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    NumericField("whiteLight", "White Light", "$.whiteLightLevel", 0, 100) with { ExpertOnly = true },
                    new ContractField
                    {
                        Key = "whiteLightTypeIndex",
                        DisplayName = "White Light Type Index",
                        SourcePath = "$.typeIndex",
                        Kind = ContractFieldKind.Integer,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Validation = new ContractValidationRule { Min = 0, Max = 8 },
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "ipcamsuite-mainset",
                            Notes = "Mapped from iOemWhitrLightTypeIndex."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "image.infrared.private",
                Endpoint = "/NetSDK/Factory?cmd=InfraRedCtrl",
                Method = "PUT",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                ExpertOnly = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    NumericField("infrared", "Infrared", "$.infraRedLevel", 0, 100) with { ExpertOnly = true },
                    new ContractField
                    {
                        Key = "infraredTypeIndex",
                        DisplayName = "Infrared Type Index",
                        SourcePath = "$.typeIndex",
                        Kind = ContractFieldKind.Integer,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Validation = new ContractValidationRule { Min = 0, Max = 8 },
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "ipcamsuite-mainset",
                            Notes = "Mapped from iOemInFraRedTypeIndex."
                        }
                    }
                ]
            },
            // ── ONVIF imaging (GenericOnvif / WVC) ─────────────────────────
            // Single-field contracts whose trailing key segment equals the ONVIF field key so
            // OnvifImagingControlAdapter.ResolveFieldKey derives the exact SetImagingSettings
            // element to write. ONVIF imaging scalars are signed -100..100 on the wire.
            OnvifImagingContract("brightness", "Brightness", "$.brightness", -100, 100),
            OnvifImagingContract("contrast", "Contrast", "$.contrast", -100, 100),
            OnvifImagingContract("saturation", "Saturation", "$.saturation", -100, 100),
            OnvifImagingContract("sharpness", "Sharpness", "$.sharpness", -100, 100),
            OnvifImagingContract("gamma", "Gamma", "$.gamma", -100, 100),
            OnvifModeContract("exposure", "Exposure", "$.exposure", ["MANUAL", "AUTO"]),
            OnvifModeContract("awb", "AWB", "$.awb", ["MANUAL", "AUTO"]),
            OnvifModeContract("wdr", "WDR", "$.wdr", ["AUTO", "ON", "OFF"]),
            OnvifModeContract("daynight", "Day/Night", "$.daynight", ["AUTO", "ON", "OFF"]),
            new EndpointContract
            {
                ContractKey = "video.onvif.profiles",
                Endpoint = "onvif:GetProfiles",
                Method = "GET",
                Surface = ContractSurface.OnvifSoap,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = onvifScope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false },
                Fields =
                [
                    StringField("profile", "Profile", "$.profile", writable: false),
                    StringField("resolution", "Resolution", "$.resolution", writable: false),
                    NumericField("frameRate", "Frame Rate", "$.frameRate", 1, 120) with { Writable = false }
                ]
            },
            new EndpointContract
            {
                ContractKey = "device.onvif.info",
                Endpoint = "onvif:GetDeviceInformation",
                Method = "GET",
                Surface = ContractSurface.OnvifSoap,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = onvifScope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false },
                Fields =
                [
                    StringField("manufacturer", "Manufacturer", "$.manufacturer", writable: false),
                    StringField("model", "Model", "$.model", writable: false),
                    StringField("firmware", "Firmware", "$.firmware", writable: false),
                    StringField("serial", "Serial", "$.serial", writable: false)
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.privacy.mask",
                Endpoint = "/NetSDK/Video/input/channel/*/privacyMask/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "privacyMaskEnabled",
                        DisplayName = "Privacy Mask Enabled",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    NumericField("privacyMaskX", "Privacy Mask X", "$.regionX", 0, 100),
                    NumericField("privacyMaskY", "Privacy Mask Y", "$.regionY", 0, 100),
                    NumericField("privacyMaskWidth", "Privacy Mask Width", "$.regionWidth", 0, 100),
                    NumericField("privacyMaskHeight", "Privacy Mask Height", "$.regionHeight", 0, 100)
                ]
            },
            new EndpointContract
            {
                ContractKey = "motion.detection.channel",
                Endpoint = "/NetSDK/Video/motionDetection/channel/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "motionEnabled",
                        DisplayName = "Motion Enabled",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    EnumField("motionType", "Motion Type", "$.detectionType", ["grid", "region"]),
                    NumericField("motionSensitivity", "Motion Sensitivity", "$.detectionGrid.sensitivityLevel", 0, 100),
                    NumericField("motionAlarmDuration", "Motion Alarm Duration", "$.mdalarmduration", 0, 300),
                    new ContractField
                    {
                        Key = "motionAlarm",
                        DisplayName = "Motion Alarm Trigger",
                        SourcePath = "$.mdalarm",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "nvr-sdk-v1.1.0.8",
                            Notes = "HISI_DETECTIONINFO.mdalarm"
                        }
                    },
                    new ContractField
                    {
                        Key = "motionBuzzer",
                        DisplayName = "Motion Buzzer",
                        SourcePath = "$.mdbuzzer",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "nvr-sdk-v1.1.0.8",
                            Notes = "HISI_DETECTIONINFO.mdbuzzer"
                        }
                    },
                    NumericField("videoLossAlarmDuration", "Video Loss Alarm Duration", "$.vlalarmduration", 0, 300),
                    new ContractField
                    {
                        Key = "videoLossAlarm",
                        DisplayName = "Video Loss Alarm Trigger",
                        SourcePath = "$.vlalarm",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "nvr-sdk-v1.1.0.8",
                            Notes = "HISI_DETECTIONINFO.vlalarm"
                        }
                    },
                    new ContractField
                    {
                        Key = "videoLossBuzzer",
                        DisplayName = "Video Loss Buzzer",
                        SourcePath = "$.vlbuzzer",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "nvr-sdk-v1.1.0.8",
                            Notes = "HISI_DETECTIONINFO.vlbuzzer"
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "alarm.input.channel",
                Endpoint = "/NetSDK/IO/alarmInput/channel/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("alarmInputDefaultState", "Alarm Input Default State", "$.active.defaultState", ["high", "low"]),
                    EnumField("alarmInputActiveState", "Alarm Input Active State", "$.active.activeState", ["high", "low"])
                ]
            },
            new EndpointContract
            {
                ContractKey = "alarm.output.channel",
                Endpoint = "/NetSDK/IO/alarmOutput/channel/*[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("alarmOutputDefaultState", "Alarm Output Default State", "$.active.defaultState", ["high", "low"]),
                    EnumField("alarmOutputActiveState", "Alarm Output Active State", "$.active.activeState", ["high", "low"]),
                    NumericField("alarmDuration", "Alarm Duration", "$.alarmduration", 0, 300),
                    new ContractField
                    {
                        Key = "alarmEnabled",
                        DisplayName = "Alarm Enabled",
                        SourcePath = "$.alarm",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "nvr-sdk-v1.1.0.8",
                            Notes = "HISI_SENSORINFO.alarm"
                        }
                    },
                    new ContractField
                    {
                        Key = "alarmBuzzer",
                        DisplayName = "Alarm Buzzer",
                        SourcePath = "$.buzzer",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "nvr-sdk-v1.1.0.8",
                            Notes = "HISI_SENSORINFO.buzzer"
                        }
                    },
                    NumericField("alarmPulseDuration", "Alarm Pulse Duration", "$.pulseDuration", 1000, 300000)
                ]
            },
            // Network / Wireless
            new EndpointContract
            {
                ContractKey = "network.interfaces",
                Endpoint = "/NetSDK/Network/interface*",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.NetworkChanging,
                RequiresRebootToTakeEffect = true,
                PersistenceExpectedAfterReboot = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false, RequiredRootFields = ["ip", "netmask", "gateway"] },
                Fields =
                [
                    IpField("ip", "IP Address", "$.ip", true),
                    IpField("netmask", "Netmask", "$.netmask", true),
                    IpField("gateway", "Gateway", "$.gateway"),
                    IpField("dns", "DNS", "$.dns"),
                    EnumField("addressingType", "Addressing Type", "$.lan.addressingType", ["static", "dynamic"]),
                    new ContractField
                    {
                        Key = "ports",
                        DisplayName = "HTTP Port",
                        SourcePath = "$.httpPort",
                        Kind = ContractFieldKind.Port,
                        Writable = true,
                        DisruptionClass = DisruptionClass.NetworkChanging,
                        Validation = new ContractValidationRule { Min = 1, Max = 65535 },
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
                    },
                    new ContractField
                    {
                        Key = "dhcpMode",
                        DisplayName = "DHCP Mode",
                        SourcePath = "$.dhcp",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.NetworkChanging,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
                    },
                    StringField("esee", "ESEE", "$.esee", false, DisruptionClass.NetworkChanging),
                    StringField("ntpServerDomain", "NTP Server", "$.ntpServerDomain")
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.esee",
                Endpoint = "/NetSDK/Network/Esee",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.NetworkChanging,
                RequiresRebootToTakeEffect = true,
                PersistenceExpectedAfterReboot = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "eseeEnabled",
                        DisplayName = "ESEE Enabled",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.NetworkChanging,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipcamsuite-endpoint-catalog" }
                    },
                    StringField("eseeId", "ESEE ID", "$.eseeId")
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.wireless",
                Endpoint = "/NetSDK/Network/wireless*",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.NetworkChanging,
                RequiresRebootToTakeEffect = true,
                PersistenceExpectedAfterReboot = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("wirelessMode", "Wireless Mode", "$.wirelessMode", ["Station", "AP", "Disabled"]),
                    EnumField("wirelessModeSdk", "Wireless Mode (SDK)", "$.wirelessMode", ["none", "accessPoint", "stationMode"]),
                    EnumField("apMode", "AP Mode", "$.ap.mode", ["Off", "On"]),
                    StringField("apSsid", "AP SSID", "$.ap.ssid", false, DisruptionClass.NetworkChanging),
                    new ContractField
                    {
                        Key = "apPsk",
                        DisplayName = "AP PSK",
                        SourcePath = "$.ap.psk",
                        Kind = ContractFieldKind.Password,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.NetworkChanging,
                        Validation = new ContractValidationRule { MinLength = 8, MaxLength = 63, Sensitive = true },
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
                    },
                    new ContractField
                    {
                        Key = "apChannel",
                        DisplayName = "AP Channel",
                        SourcePath = "$.ap.channel",
                        Kind = ContractFieldKind.Integer,
                        Writable = true,
                        DisruptionClass = DisruptionClass.NetworkChanging,
                        Validation = new ContractValidationRule { Min = 1, Max = 14 },
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.time.ntp",
                Endpoint = "/NetSDK/System/time/ntp[/properties]",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "ntpEnabled",
                        DisplayName = "NTP Enabled",
                        SourcePath = "$.ntpEnabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipc-sdk-v1.4" }
                    },
                    StringField("ntpServerDomain", "NTP Server", "$.ntpServerDomain")
                ]
            },
            new EndpointContract
            {
                ContractKey = "storage.sdcard.status",
                Endpoint = "/NetSDK/SDCard/status",
                Method = "GET",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.StoragePlayback,
                GroupName = "Storage / Playback",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = false },
                Fields =
                [
                    StringField("sdStatus", "SD Card Status", "$.status", writable: false)
                ]
            },
            new EndpointContract
            {
                ContractKey = "storage.sdcard.media.search",
                Endpoint = "/NetSDK/SDCard/media/search",
                Method = "GET",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.StoragePlayback,
                GroupName = "Storage / Playback",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = false },
                Fields =
                [
                    StringField("sdMediaType", "SD Media Type", "$.type", writable: false)
                ]
            },
            // Users / Maintenance
            new EndpointContract
            {
                ContractKey = "system.device.info",
                Endpoint = "/NetSDK/System/deviceInfo",
                Method = "GET",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Proven,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = false },
                Fields =
                [
                    StringField("serial", "Serial", "$.serial", false),
                    StringField("model", "Model", "$.model", false),
                    StringField("firmware", "Firmware", "$.firmware", false),
                    StringField("mac", "MAC", "$.mac", false),
                    StringField("eseeId", "ESEE ID", "$.eseeId", false)
                ]
            },
            new EndpointContract
            {
                ContractKey = "users.private.list",
                Endpoint = "/user/user_list.xml",
                Method = "GET",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "userList",
                        DisplayName = "User List",
                        SourcePath = "$.users",
                        Kind = ContractFieldKind.Array,
                        Writable = false,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "private-manifest" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "users.private.password",
                Endpoint = "/user/set_pass.xml",
                Method = "POST",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                ExpertOnly = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    StringField("username", "Username", "$.username") with { ExpertOnly = true },
                    new ContractField
                    {
                        Key = "newPassword",
                        DisplayName = "New Password",
                        SourcePath = "$.newPassword",
                        Kind = ContractFieldKind.Password,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Validation = new ContractValidationRule { MinLength = 8, MaxLength = 63, Sensitive = true },
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "ipcamsuite-private-manifest" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "maintenance.reboot",
                Endpoint = "/NetSDK/System/operation/reboot",
                Method = "PUT",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Reboot,
                ExpertOnly = true,
                RequiresRebootToTakeEffect = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    new ContractField
                    {
                        Key = "reboot",
                        DisplayName = "Reboot",
                        SourcePath = "$.reboot",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.Reboot,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "private-manifest" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "maintenance.reboot.legacy",
                Endpoint = "/netsdk/Reboot",
                Method = "POST",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Reboot,
                ExpertOnly = true,
                RequiresRebootToTakeEffect = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    new ContractField
                    {
                        Key = "reboot",
                        DisplayName = "Reboot",
                        SourcePath = "$.reboot",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.Reboot,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "eseecloud-js",
                            Notes = "Observed from reboot flow in NvrRemoteSettingsController."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "maintenance.factory.default",
                Endpoint = "/NetSDK/System/operation/default",
                Method = "PUT",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.FactoryReset,
                ExpertOnly = true,
                RequiresRebootToTakeEffect = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    new ContractField
                    {
                        Key = "factoryDefault",
                        DisplayName = "Factory Default",
                        SourcePath = "$.factoryDefault",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.FactoryReset,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "private-manifest" }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "maintenance.firmware.upload",
                Endpoint = "/onlineupgrade",
                Method = "POST",
                Surface = ContractSurface.PrivateCgiXml,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                ExpertOnly = true,
                RequiresRebootToTakeEffect = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
                Fields =
                [
                    new ContractField
                    {
                        Key = "firmwareBlob",
                        DisplayName = "Firmware Upload",
                        SourcePath = "$.firmware",
                        Kind = ContractFieldKind.Opaque,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "eseecloud-js",
                            Notes = "Observed upload target in firmware upgrade flow."
                        }
                    }
                ]
            },
            // ── Firmware-proven 5523-W surface (anyka_ipc 3.6.103.5721106 string table) ──────
            // Every endpoint + field name below was extracted verbatim from the camera binary
            // (RESTful_NetSDK*_OnPut/_OnGet handlers, $.fieldProperty descriptor strings, and
            // [%s:%d] log formats). These are the real wire keys, not SDK-doc guesses.
            new EndpointContract
            {
                ContractKey = "system.ledpwm",
                Endpoint = "/NetSDK/System/ledpwm",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "ledPwmSwitch",
                        DisplayName = "LED PWM Switch",
                        SourcePath = "$.ledPwm.switch",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "[%s:%d]ledPwm.switch: %d — RESTful_NetSDKSystemLedPwm_OnPut. LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} on 5523-W 3.6.60 — gated/write-only on this model."
                        }
                    },
                    new ContractField
                    {
                        Key = "ledPwmProject",
                        DisplayName = "LED PWM Project",
                        SourcePath = "$.ledPwm.project",
                        Kind = ContractFieldKind.Integer,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Validation = new ContractValidationRule { Min = 0, Max = 64 },
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "[%s:%d]ledPwm.nProject: %d — NK_Enum_MapN1LedPwmProduct."
                        }
                    },
                    new ContractField
                    {
                        Key = "ledPwmChannelCount",
                        DisplayName = "LED PWM Channel Count",
                        SourcePath = "$.ledPwm.nChannelCount",
                        Kind = ContractFieldKind.Integer,
                        Writable = false,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "[%s:%d]ledPwm.nChannelCount: %d — read-only."
                        }
                    },
                    new ContractField
                    {
                        Key = "ledPwmChannelInfo",
                        DisplayName = "LED PWM Channel Info",
                        SourcePath = "$.channelInfo",
                        Kind = ContractFieldKind.Array,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "$.channelInfo[%d].{type,channel,num,numMotion,schedule[%d]} — KP2PCFG_MakeLedPwm."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.ledpwm.channelInfo",
                Endpoint = "/NetSDK/System/ledpwm/ChannelInfo",
                Method = "GET",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "ledPwmChannelInfo",
                        DisplayName = "LED PWM Channel Info",
                        SourcePath = "$.channelInfo",
                        Kind = ContractFieldKind.Array,
                        Writable = false,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "RESTful_NetSDKSystemLedPwmChannelInfo_OnGet. LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} — gated on this model."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.alarm.schedule",
                Endpoint = "/NetSDK/System/AlarmSchedule",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "alarmScheduleEnabled",
                        DisplayName = "Alarm Schedule Enabled",
                        SourcePath = "$.AlarmSchedule[0].Enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "$.AlarmSchedule[%d].Enabled — RESTful_NetSDKSystemAlarmSchedule handler. LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} (bare, ?id=0, /0) — not served live on 5523-W 3.6.60."
                        }
                    },
                    NumericField("alarmScheduleWeekday", "Alarm Schedule Weekday", "$.AlarmSchedule[0].Weekday", 0, 127),
                    StringField("alarmScheduleBegin", "Alarm Schedule Begin", "$.AlarmSchedule[0].BeginTime"),
                    StringField("alarmScheduleEnd", "Alarm Schedule End", "$.AlarmSchedule[0].EndTime")
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.record.schedule",
                Endpoint = "/NetSDK/System/RecordSchedule",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.StoragePlayback,
                GroupName = "Storage / Playback",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "recordScheduleEnabled",
                        DisplayName = "Record Schedule Enabled",
                        SourcePath = "$.RecordSchedule[0].Enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "$.RecordSchedule[%d].Enabled — RESTful_NetSDKSystemRecordSchedule handler. LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} — not served live on 5523-W 3.6.60."
                        }
                    },
                    EnumField("recordScheduleType", "Record Type", "$.RecordSchedule[0].RecType", ["manual", "schedule", "alarm", "alarmAndSchedule"]) with
                    {
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "RecType opt[0..3] proven by firmware; the four string labels are inferred (match Hikvision convention) — confirm against a live device before relying on writes."
                        }
                    },
                    NumericField("recordScheduleWeekday", "Record Schedule Weekday", "$.RecordSchedule[0].Weekday", 0, 127),
                    StringField("recordScheduleBegin", "Record Schedule Begin", "$.RecordSchedule[0].BeginTime"),
                    StringField("recordScheduleEnd", "Record Schedule End", "$.RecordSchedule[0].EndTime")
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.face.detection",
                Endpoint = "/NetSDK/Video/FaceDetection",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "faceDetectionSupported",
                        DisplayName = "Face Detection Supported",
                        SourcePath = "$.SupportFaceDetect",
                        Kind = ContractFieldKind.Boolean,
                        Writable = false,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "$.SupportFaceDetect / $.Capabilities.SupportFaceDetect — RESTful_NetSDKVideoFaceDetect_OnGet. LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} — FaceDetection not served live on this model."
                        }
                    },
                    NumericField("faceDetectionMaxNum", "Face Detection Max", "$.MaxFaceDetectNum", 0, 32) with { Writable = false },
                    new ContractField
                    {
                        Key = "faceDetectionEnabled",
                        DisplayName = "Face Detection",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "firmware-string", Notes = "onVideoFaceDetect event; enable flag is the writable lever." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.human.detect",
                Endpoint = "/NetSDK/Video/HumanDetect",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "humanDetectEnabled",
                        DisplayName = "Human Detection",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: payload {enabled, drawRegion, sensitivityStep}; $.enabled is the writable lever." }
                    },
                    new ContractField
                    {
                        Key = "humanDetectDrawRegion",
                        DisplayName = "Draw Region",
                        SourcePath = "$.drawRegion",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: drawRegion=false observed. SupportHumanDetect/MaxHumanDetectNum are NOT in the payload (capability-flag guesses)." }
                    },
                    EnumField("humanDetectSensitivity", "Sensitivity Step", "$.sensitivityStep", ["normal"]) with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: observed value 'normal'." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "video.cordon",
                Endpoint = "/NetSDK/Video/cordon",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "cordonEnabled",
                        DisplayName = "Cordon Enabled",
                        SourcePath = "$.enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Proven,
                            Source = "live-2026-08-11",
                            Notes = "SETTLED LIVE: GET returns {id, enabled, type, sensitivityLevel, maxLines, line[], maxcolumns, maxrows, width, height, grid[]}. $.enabled is the writable lever; bEnableCordon/enCordonType/stCordonLinelist are WRONG names."
                        }
                    },
                    EnumField("cordonType", "Cordon Type", "$.type", ["region", "line"]) with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: type='region' observed; 'line' likely sibling." }
                    },
                    NumericField("cordonSensitivity", "Cordon Sensitivity", "$.sensitivityLevel", 0, 100) with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: sensitivityLevel=80 observed." }
                    },
                    new ContractField
                    {
                        Key = "cordonLines",
                        DisplayName = "Cordon Lines",
                        SourcePath = "$.line",
                        Kind = ContractFieldKind.Array,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: line[] of {beginX,beginY,endX,endY}; maxLines observed. stCordonLinelist WRONG." }
                    },
                    new ContractField
                    {
                        Key = "cordonGrid",
                        DisplayName = "Cordon Grid",
                        SourcePath = "$.grid",
                        Kind = ContractFieldKind.Array,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: grid[] of 32x24 cells (maxcolumns/maxrows); stCordonArealist WRONG." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.time.rtc",
                Endpoint = "/NetSDK/System/time/rtc",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "rtc",
                        DisplayName = "RTC",
                        SourcePath = "$",
                        Kind = ContractFieldKind.Integer,
                        Writable = true,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: GET returns a bare unix-seconds int (1786493574); PUT accepts ONLY the bare scalar (object forms -> statusCode 6 Invalid Document). The $.rtc object key is WRONG on this firmware." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.time.timezone",
                Endpoint = "/NetSDK/System/time/timeZone",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("timeZone", "Time Zone", "$", ["GMT-11:00", "GMT-10:00", "GMT-09:00", "GMT-08:00", "GMT-07:00", "GMT-06:00", "GMT-05:00", "GMT-04:30", "GMT-04:00", "GMT-03:30", "GMT-03:00", "GMT-02:00", "GMT-01:00", "GMT+01:00", "GMT+02:00", "GMT+03:00", "GMT+03:30", "GMT+04:00", "GMT+04:30", "GMT+05:00", "GMT+05:30", "GMT+05:45", "GMT+06:00", "GMT+06:30", "GMT+07:00", "GMT+08:00", "GMT+09:00", "GMT+09:30", "GMT+10:00", "GMT+11:00", "GMT+12:00", "GMT+13:00"]) with
                    {
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Proven,
                            Source = "live-2026-08-11",
                            Notes = "SETTLED LIVE: GET returns a bare string \"GMT+08:00\"; PUT accepts ONLY the bare string (object forms -> statusCode 6 Invalid Document). GMT offset values extracted from firmware; live unit sits at GMT+08:00."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.time.calendarStyle",
                Endpoint = "/NetSDK/System/time/calendarStyle",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    EnumField("calendarStyle", "Calendar Style", "$.calendarStyle", ["general", "lunar"]) with
                    {
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Proven,
                            Source = "live-2026-08-11",
                            Notes = "SETTLED LIVE: GET returns bare string \"general\" (no object wrapper) but the only VERIFIED write is the object form {\"calendarStyle\":\"general\"} -> statusCode 0 OK — so keep the $.calendarStyle object key (unlike rtc/timeZone where bare writes were proven). The earlier inferred 'Gregorian'/'Lunar' labels do NOT match the wire value; 'lunar' is the likely sibling but unverified."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.gb28181",
                Endpoint = "/NetSDK/System/gb28181",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                RequiresRebootToTakeEffect = true,
                PersistenceExpectedAfterReboot = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "gb28181SipPort",
                        DisplayName = "GB28181 SIP Port",
                        SourcePath = "$.sipPort",
                        Kind = ContractFieldKind.Port,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Validation = new ContractValidationRule { Min = 1, Max = 65535 },
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: sipPort=5060 observed." }
                    },
                    NumericField("gb28181SipServerport", "GB28181 SIP Server Port", "$.sipServerport", 1, 65535) with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: sipServerport=5060 observed — the one key that matched the original contract." }
                    },
                    StringField("gb28181ServerAddr", "GB28181 Server Address", "$.sipServeraddr"),
                    StringField("gb28181Username", "GB28181 Username", "$.sipUsername"),
                    new ContractField
                    {
                        Key = "gb28181Password",
                        DisplayName = "GB28181 Password",
                        SourcePath = "$.sipUserpass",
                        Kind = ContractFieldKind.Password,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: sipUserpass observed; Password kind so audit redaction treats it as a secret." }
                    },
                    NumericField("gb28181RegisterInterval", "Register Interval", "$.registerInterval", 1, 86400) with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: registerInterval=60 observed." }
                    },
                    NumericField("gb28181Heartbeat", "Heartbeat Cycle", "$.heartbeatCycle", 1, 3600) with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: heartbeatCycle=20 observed. No enable toggle in the live doc — bGB28181/GB28181_Server/ServerPort are WRONG." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.gat1400",
                Endpoint = "/NetSDK/System/gat1400",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                RequiresRebootToTakeEffect = true,
                PersistenceExpectedAfterReboot = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "gat1400Enabled",
                        DisplayName = "GAT1400 Enabled",
                        SourcePath = "$.bGAT1400",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Proven,
                            Source = "live-2026-08-11",
                            Notes = "SETTLED LIVE: GET returns {statusCode:3, 'Device Not Support'} — this 5523-W model does not implement GAT1400. bGAT1400 was an inferred toggle mirroring bGB28181; do not surface as a writable control."
                        }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.ftp",
                Endpoint = "/NetSDK/FTP",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "ftpScheduleEnabled",
                        DisplayName = "FTP Schedule Enabled",
                        SourcePath = "$.ScheduleEnabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "$.ScheduleEnabled / $.ScheduleScheme[%d] — FTP schedule form. LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} — FTP not served live on this model."
                        }
                    },
                    new ContractField
                    {
                        Key = "ftpSchedule",
                        DisplayName = "FTP Schedule",
                        SourcePath = "$.schedule",
                        Kind = ContractFieldKind.Array,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "firmware-string", Notes = "$.schedule / $.stFtpSchedule[%d]." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.rtmp",
                Endpoint = "/NetSDK/RTMP",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    StringField("rtmpUrl", "RTMP URL", "$.rtmpUrl") with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} — RTMP not served live on 5523-W 3.6.60." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.wireless.signal",
                Endpoint = "/NetSDK/Network/wireless/stationSignal",
                Method = "GET",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "stationSignal",
                        DisplayName = "Station Signal (dBm)",
                        SourcePath = "$",
                        Kind = ContractFieldKind.Integer,
                        Writable = false,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: GET returns a BARE int RSSI in dBm (e.g. -48). $.SignalStrength / $.stationsignal object keys are WRONG — the wire document is the bare number (read-only)." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "network.port",
                Endpoint = "/NetSDK/Network/port",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                Scope = scope,
                DisruptionClass = DisruptionClass.NetworkChanging,
                RequiresRebootToTakeEffect = true,
                PersistenceExpectedAfterReboot = true,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "portId",
                        DisplayName = "Port ID",
                        SourcePath = "$[0].id",
                        Kind = ContractFieldKind.Integer,
                        Writable = false,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: GET returns ARRAY [{id:1, portname:'unisual', value:80}]; PUT of the same array round-trips (HTTP 200)." }
                    },
                    StringField("portName", "Port Name", "$[0].portname") with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: portname='unisual' (web/data port id 1)." }
                    },
                    new ContractField
                    {
                        Key = "portValue",
                        DisplayName = "Port Value",
                        SourcePath = "$[0].value",
                        Kind = ContractFieldKind.Port,
                        Writable = true,
                        DisruptionClass = DisruptionClass.NetworkChanging,
                        Validation = new ContractValidationRule { Min = 1, Max = 65535 },
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: value=80 observed; $.httpPort/$.rtspPort/$.onvifPort are WRONG — the real key is 'value' per named port entry." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.device.name",
                Endpoint = "/NetSDK/System/deviceInfo/deviceName",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    StringField("deviceName", "Device Name", "$") with
                    {
                        Writable = false,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: GET returns BARE string \"5523-W\"; PUT on this subpath (object or bare) returns HTTP 500 Device Error — read-only subpath; write goes through the full /NetSDK/System/deviceInfo document (deviceName lives inside deviceInfo)." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.device.address",
                Endpoint = "/NetSDK/System/deviceInfo/deviceAddress",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.UsersMaintenance,
                GroupName = "Users / Maintenance",
                Scope = scope,
                DisruptionClass = DisruptionClass.Safe,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "deviceAddress",
                        DisplayName = "Device Address",
                        SourcePath = "$",
                        Kind = ContractFieldKind.Integer,
                        Writable = false,
                        DisruptionClass = DisruptionClass.Safe,
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: GET returns BARE int (1) — an index/location code, not a text address. Read-only via subpath." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.alarm.tone",
                Endpoint = "/NetSDK/System/AlarmTone",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "alarmToneEnabled",
                        DisplayName = "Alarm Tone Enabled",
                        SourcePath = "$.AlarmTone[0].Enabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Proven,
                            Source = "live-2026-08-11",
                            Notes = "SETTLED LIVE: GET returns {statusCode:3, 'Device Not Support'} — this 5523-W model does not implement AlarmTone. Do not surface as a writable control."
                        }
                    },
                    StringField("alarmTone", "Alarm Tone", "$.AlarmTone[0].tone") with
                    {
                        Evidence = new ContractEvidence { TruthState = ContractTruthState.Proven, Source = "live-2026-08-11", Notes = "SETTLED LIVE: endpoint reports Device Not Support." }
                    }
                ]
            },
            new EndpointContract
            {
                ContractKey = "system.alarm.scheduleV2",
                Endpoint = "/NetSDK/System/AlarmScheduleV2",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.MotionPrivacyAlarms,
                GroupName = "Motion / Privacy / Alarms",
                Scope = scope,
                DisruptionClass = DisruptionClass.ServiceImpacting,
                TruthState = ContractTruthState.Inferred,
                ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = true, PartialWriteAllowed = false },
                Fields =
                [
                    new ContractField
                    {
                        Key = "alarmScheduleV2Enabled",
                        DisplayName = "Alarm Schedule V2 Enabled",
                        SourcePath = "$.ScheduleEnabled",
                        Kind = ContractFieldKind.Boolean,
                        Writable = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "V2 payload uses the ScheduleEnabled/ScheduleScheme family (mirrors FTP schedule form). LIVE 2026-08-11: GET returns HTTP 500 {statusCode:2 'Device Error'} — gated on this model."
                        }
                    },
                    new ContractField
                    {
                        Key = "alarmScheduleV2",
                        DisplayName = "Alarm Schedule V2",
                        SourcePath = "$.ScheduleScheme",
                        Kind = ContractFieldKind.Array,
                        Writable = true,
                        ExpertOnly = true,
                        DisruptionClass = DisruptionClass.ServiceImpacting,
                        Evidence = new ContractEvidence
                        {
                            TruthState = ContractTruthState.Inferred,
                            Source = "firmware-string",
                            Notes = "$.ScheduleScheme[%d] time-segment array; composite payload."
                        }
                    }
                ]
            }
        ];
    }

    private static ContractField NumericField(string key, string name, string path, decimal min, decimal max)
        => new()
        {
            Key = key,
            DisplayName = name,
            SourcePath = path,
            Kind = ContractFieldKind.Number,
            Writable = true,
            DisruptionClass = DisruptionClass.Safe,
            Validation = new ContractValidationRule { Min = min, Max = max },
            Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
        };

    private static EndpointContract OnvifImagingContract(string fieldKey, string displayName, string path, decimal min, decimal max)
        => new()
        {
            ContractKey = $"image.onvif.{fieldKey}",
            Endpoint = "onvif:GetImagingSettings",
            Method = "PUT",
            Surface = ContractSurface.OnvifSoap,
            GroupKind = TypedSettingGroupKind.VideoImage,
            GroupName = "Video / Image",
            Scope = OnvifDeviceScope,
            DisruptionClass = DisruptionClass.Safe,
            TruthState = ContractTruthState.Inferred,
            ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
            Fields =
            [
                NumericField(fieldKey, displayName, path, min, max) with
                {
                    Evidence = new ContractEvidence
                    {
                        TruthState = ContractTruthState.Inferred,
                        Source = "onvif-imaging",
                        Notes = "ONVIF imaging scalar (signed -100..100)."
                    }
                }
            ]
        };

    private static EndpointContract OnvifModeContract(string fieldKey, string displayName, string path, IReadOnlyCollection<string> modes)
        => new()
        {
            ContractKey = $"image.onvif.{fieldKey}",
            Endpoint = "onvif:GetImagingSettings",
            Method = "PUT",
            Surface = ContractSurface.OnvifSoap,
            GroupKind = TypedSettingGroupKind.VideoImage,
            GroupName = "Video / Image",
            Scope = OnvifDeviceScope,
            DisruptionClass = DisruptionClass.Safe,
            TruthState = ContractTruthState.Inferred,
            ObjectShape = new ContractObjectShape { RootPath = "$", FullObjectWriteRequired = false, PartialWriteAllowed = true },
            Fields =
            [
                EnumField(fieldKey, displayName, path, modes) with
                {
                    Evidence = new ContractEvidence
                    {
                        TruthState = ContractTruthState.Inferred,
                        Source = "onvif-imaging",
                        Notes = "ONVIF imaging mode element (Exposure/WhiteBalance/WideDynamicRange/IrCutFilter)."
                    }
                }
            ]
        };

    private static ContractField EnumField(string key, string name, string path, IReadOnlyCollection<string> values, bool required = false, bool expertOnly = false)
        => new()
        {
            Key = key,
            DisplayName = name,
            SourcePath = path,
            Kind = ContractFieldKind.Enum,
            Required = required,
            Writable = true,
            ExpertOnly = expertOnly,
            DisruptionClass = DisruptionClass.Safe,
            EnumValues = values.Select(value => new ContractEnumValue { Value = value, TruthState = ContractTruthState.Inferred }).ToList(),
            Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
        };

    private static ContractField StringField(string key, string name, string path, bool writable = true, DisruptionClass disruptionClass = DisruptionClass.Safe)
        => new()
        {
            Key = key,
            DisplayName = name,
            SourcePath = path,
            Kind = ContractFieldKind.String,
            Writable = writable,
            DisruptionClass = disruptionClass,
            Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
        };

    private static ContractField IpField(string key, string name, string path, bool required = false)
        => new()
        {
            Key = key,
            DisplayName = name,
            SourcePath = path,
            Kind = ContractFieldKind.IpAddress,
            Writable = true,
            Required = required,
            DisruptionClass = DisruptionClass.NetworkChanging,
            Validation = new ContractValidationRule { Regex = @"^([0-9]{1,3}\.){3}[0-9]{1,3}$" },
            Evidence = new ContractEvidence { TruthState = ContractTruthState.Inferred, Source = "manifest" }
        };
}
