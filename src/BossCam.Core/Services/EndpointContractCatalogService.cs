using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class EndpointContractCatalogService(
    IApplicationStore store,
    ILogger<EndpointContractCatalogService> logger) : IEndpointContractCatalog
{
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
                    StringField("exposure", "Exposure", "$.exposureMode"),
                    StringField("awb", "AWB", "$.awbMode"),
                    StringField("osd", "OSD", "$.osd.title")
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
                    }
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
            // Network / Wireless
            new EndpointContract
            {
                ContractKey = "network.interfaces",
                Endpoint = "/NetSDK/Network/interfaces*",
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
                    StringField("esee", "ESEE", "$.esee", false, DisruptionClass.NetworkChanging)
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
                    }
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
