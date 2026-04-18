using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.NativeBridge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Control;

public sealed class OwnedRemoteCommandAdapter(
    IOptions<BossCamRuntimeOptions> options,
    IApplicationStore store,
    ILogger<OwnedRemoteCommandAdapter> logger) : IControlAdapter
{
    public string Name => nameof(OwnedRemoteCommandAdapter);
    public int Priority => 30;
    public TransportKind TransportKind => TransportKind.RemoteCommand;

    public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => Task.FromResult(!string.IsNullOrWhiteSpace(device.EseeId));

    public async Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var manifests = await store.GetProtocolManifestsAsync(cancellationToken);
        var remoteManifest = manifests.FirstOrDefault(static manifest => manifest.ManifestId.Contains("eseecloud_remote", StringComparison.OrdinalIgnoreCase));
        return new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.EseeJuanP2P, TransportKind.Kp2p, TransportKind.LinkVision, TransportKind.RemoteCommand],
            SupportedSettingGroups =
            [
                "DeviceInfo",
                "AlarmSetting.MotionDetection",
                "devCoverSetting",
                "Osd.Title",
                "SystemOperation.TimeSync",
                "SystemOperation.DaylightSavingTime",
                "videoManager",
                "PromptSounds",
                "TfcardManager",
                "WirelessManager",
                "RecordManager",
                "DoorbellManager",
                "FisheyeSetting",
                "ledPwm",
                "ChannelManager",
                "UserManager"
            ],
            SupportedEndpointPaths = remoteManifest?.Endpoints.Select(static endpoint => endpoint.Path).ToList() ?? [],
            SupportedMaintenanceOperations = [MaintenanceOperation.PasswordReset.ToString(), MaintenanceOperation.RefreshUsers.ToString()],
            Notes = new Dictionary<string, string>
            {
                ["remoteEndpoint"] = options.Value.RemoteCommandEndpoint ?? string.Empty,
                ["probe"] = "Remote envelope adapter available when a remote relay endpoint is configured."
            }
        };
    }

    public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => ReadAsync(device, cancellationToken);

    public async Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope(device, "get", null, null);
        var response = await SendEnvelopeAsync(envelope, cancellationToken);
        var envelopeNode = JsonNode.Parse(JsonSerializer.Serialize(envelope));
        var group = new SettingGroup
        {
            Name = "RemoteCommand",
            DisplayName = "RemoteCommand",
            RawPayload = response?.Response ?? envelopeNode,
            Values = new Dictionary<string, SettingValue>
            {
                ["Envelope"] = new() { Key = "Envelope", DisplayName = "Envelope", Value = envelopeNode, ValueKind = SettingValueKind.Object },
                ["Response"] = new() { Key = "Response", DisplayName = "Response", Value = response?.Response, ValueKind = SettingValueKind.Object }
            }
        };
        return new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name, Groups = [group] };
    }

    public async Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
    {
        var payload = new JsonObject();
        if (plan.Payload is not null)
        {
            payload[plan.GroupName] = plan.Payload.DeepClone();
        }
        var envelope = BuildEnvelope(device, "set", payload, null);
        var response = await SendEnvelopeAsync(envelope, cancellationToken);
        return new WriteResult
        {
            Success = response?.Success == true,
            AdapterName = Name,
            Response = response?.Response,
            Message = response?.Message ?? "Remote relay endpoint is not configured."
        };
    }

    public async Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
    {
        var remotePayload = new JsonObject
        {
            ["Operation"] = operation.ToString(),
            ["Payload"] = payload?.DeepClone()
        };
        var envelope = BuildEnvelope(device, "set", remotePayload, operation.ToString());
        var response = await SendEnvelopeAsync(envelope, cancellationToken);
        return new MaintenanceResult
        {
            Success = response?.Success == true,
            AdapterName = Name,
            Operation = operation,
            Response = response?.Response,
            Message = response?.Message ?? "Remote relay endpoint is not configured."
        };
    }

    private RemoteCommandEnvelope BuildEnvelope(DeviceIdentity device, string method, JsonObject? payload, string? api)
    {
        var ipCam = payload ?? new JsonObject
        {
            ["DeviceInfo"] = new JsonObject(),
            ["AlarmSetting"] = new JsonObject { ["MotionDetection"] = new JsonObject() },
            ["devCoverSetting"] = new JsonArray(),
            ["Osd"] = new JsonObject { ["Title"] = new JsonObject() },
            ["SystemOperation"] = new JsonObject
            {
                ["TimeSync"] = new JsonObject(),
                ["DaylightSavingTime"] = new JsonObject { ["week"] = new JsonArray(new JsonObject(), new JsonObject()) }
            },
            ["videoManager"] = new JsonObject(),
            ["PromptSounds"] = new JsonObject(),
            ["TfcardManager"] = new JsonObject { ["TFcard_recordSchedule"] = new JsonArray() },
            ["WirelessManager"] = new JsonObject(),
            ["RecordManager"] = new JsonObject(),
            ["DoorbellManager"] = new JsonObject(),
            ["FisheyeSetting"] = new JsonObject { ["FixParam"] = new JsonArray() },
            ["ledPwm"] = new JsonObject { ["channelInfo"] = new JsonArray() },
            ["ChannelManager"] = new JsonObject { ["ChannelList"] = 0, ["Operation"] = "GetChannel" },
            ["UserManager"] = new JsonObject()
        };

        return new RemoteCommandEnvelope
        {
            Version = "1.0.0",
            Method = method,
            CapabilitySet = new JsonObject(),
            IPCam = ipCam,
            Dev = "IPCam",
            Api = api,
            Authorization = new RemoteAuthorization
            {
                Username = device.LoginName,
                Password = device.Password,
                Verify = string.Empty
            }
        };
    }

    private async Task<RemoteCommandResult?> SendEnvelopeAsync(RemoteCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.RemoteCommandEndpoint))
        {
            return new RemoteCommandResult { Success = false, Message = "BossCam:RemoteCommandEndpoint is not configured.", Response = JsonNode.Parse(JsonSerializer.Serialize(envelope)) };
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds) };
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.RemoteCommandEndpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return new RemoteCommandResult
            {
                Success = response.IsSuccessStatusCode,
                Endpoint = options.Value.RemoteCommandEndpoint,
                Message = raw,
                Response = HttpControlAdapterBase.TryParseNode(raw)
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Remote command relay failed");
            return new RemoteCommandResult { Success = false, Endpoint = options.Value.RemoteCommandEndpoint, Message = ex.Message };
        }
    }
}

public sealed class NativeFallbackAdapter(
    IOptions<BossCamRuntimeOptions> options,
    ILogger<NativeFallbackAdapter> logger) : IControlAdapter
{
    public string Name => nameof(NativeFallbackAdapter);
    public int Priority => 40;
    public TransportKind TransportKind => TransportKind.NativeFallback;

    public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var libraries = NativeLibraryCatalog.Discover(options.Value.IpcamSuiteDirectory, options.Value.EseeCloudDirectory);
        return Task.FromResult(libraries.Any(static library => library.Exists));
    }

    public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var libraries = NativeLibraryCatalog.Discover(options.Value.IpcamSuiteDirectory, options.Value.EseeCloudDirectory);
        logger.LogDebug("Native fallback probe examined {Count} libraries", libraries.Count);
        return Task.FromResult(new CapabilityMap
        {
            DeviceId = device.Id,
            PrimaryControlAdapter = Name,
            ControlAdapters = [Name],
            VideoTransportKinds = [TransportKind.NativeFallback, TransportKind.EseeJuanP2P, TransportKind.Kp2p, TransportKind.LinkVision],
            SupportedSettingGroups = ["NativeDiagnostics"],
            SupportedEndpointPaths = libraries.Select(static library => library.Path).ToList(),
            SupportedMaintenanceOperations = [],
            Notes = libraries.ToDictionary(static library => library.Name, static library => library.Exists ? library.Path : "missing", StringComparer.OrdinalIgnoreCase)
        });
    }

    public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        => ReadAsync(device, cancellationToken);

    public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var libraries = NativeLibraryCatalog.Discover(options.Value.IpcamSuiteDirectory, options.Value.EseeCloudDirectory);
        var payload = new JsonObject();
        foreach (var library in libraries)
        {
            payload[library.Name] = new JsonObject
            {
                ["path"] = library.Path,
                ["exists"] = library.Exists,
                ["role"] = library.Role
            };
        }

        return Task.FromResult(new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = Name,
            Groups =
            [
                new SettingGroup
                {
                    Name = "NativeDiagnostics",
                    DisplayName = "NativeDiagnostics",
                    RawPayload = payload,
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["Libraries"] = new() { Key = "Libraries", DisplayName = "Libraries", Value = payload, ValueKind = SettingValueKind.Object }
                    }
                }
            ]
        });
    }

    public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
        => Task.FromResult(new WriteResult { Success = false, AdapterName = Name, Message = "Native fallback bridge is cataloged but not implemented as a callable C ABI in this pass." });

    public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
        => Task.FromResult(new MaintenanceResult { Success = false, AdapterName = Name, Operation = operation, Message = "Native fallback bridge is cataloged but not implemented as a callable C ABI in this pass." });
}
