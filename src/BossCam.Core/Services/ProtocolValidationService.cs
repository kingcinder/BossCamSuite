using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Headers;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class ProtocolValidationService(
    IEnumerable<IControlAdapter> controlAdapters,
    IEndpointContractCatalog contractCatalog,
    IApplicationStore store,
    ILogger<ProtocolValidationService> logger)
{
    private static readonly string[] SafeMutationKeys =
    [
        "brightness",
        "contrast",
        "saturation",
        "sharpness",
        "hue",
        "bitrate",
        "frameRate",
        "brightnessLevel",
        "contrastLevel",
        "saturationLevel",
        "sharpnessLevel",
        "hueLevel",
        "constantBitRate",
        "enabled",
        "flipEnabled",
        "mirrorEnabled",
        "codecType",
        "h264Profile",
        "resolution",
        "irCutMode",
        "awbMode",
        "exposureMode",
        "denoise3dStrength",
        "WDRStrength"
    ];
    private static readonly string[] KnownLanReadFallbacks =
    [
        "/netsdk/system/deviceInfo",
        "/netsdk/image",
        "/netsdk/image/properties",
        "/netsdk/image/wdr",
        "/netsdk/image/denoise3d",
        "/netsdk/image/manualSharpness",
        "/netsdk/image/irCutfilter",
        "/netsdk/video/input/channel/1",
        "/netsdk/video/encode/channel/101/properties",
        "/netsdk/video/encode/channel/102/properties",
        "/netsdk/network/interface/1",
        "/netsdk/network/interface/4",
        "/netsdk/network/esee",
        "/user/user_list.xml",
        "/cgi-bin/gw2.cgi"
    ];

    private sealed record DeepProbeOutcome(
        bool ReadVerified,
        bool WriteVerified,
        bool SemanticChanged,
        string? AuthMode,
        string? Notes,
        JsonNode? PreRead,
        JsonNode? PostRead,
        IReadOnlyCollection<EndpointTranscript> Transcripts);

    public async Task<CapabilityProbeResult?> ValidateDeviceAsync(Guid deviceId, ValidationRunOptions? options, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        return await ValidateDeviceAsync(device, options ?? new ValidationRunOptions(), cancellationToken);
    }

    public async Task<CapabilityProbeResult> ValidateDeviceAsync(DeviceIdentity device, ValidationRunOptions options, CancellationToken cancellationToken)
    {
        var manifests = await store.GetProtocolManifestsAsync(cancellationToken);
        var contracts = await contractCatalog.GetContractsForDeviceAsync(device, cancellationToken);
        var allResults = new List<EndpointValidationResult>();
        var allTranscripts = new List<EndpointTranscript>();

        foreach (var adapter in controlAdapters.OrderBy(static adapter => adapter.Priority))
        {
            if (!string.IsNullOrWhiteSpace(options.AdapterName) && !adapter.Name.Equals(options.AdapterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (adapter.TransportKind is not (TransportKind.LanRest or TransportKind.LanPrivateHttp))
            {
                continue;
            }

            bool canHandle;
            try
            {
                canHandle = await adapter.CanHandleAsync(device, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Validation skip: can-handle failed for adapter {Adapter} device {Device}", adapter.Name, device.DisplayName);
                continue;
            }

            if (!canHandle)
            {
                continue;
            }

            var endpoints = SelectEndpointsForAdapter(adapter, manifests, contracts);
            foreach (var endpoint in endpoints)
            {
                var result = await ValidateEndpointAsync(device, adapter, endpoint, options, cancellationToken);
                allResults.Add(result.Result);
                allTranscripts.AddRange(result.Transcripts);
            }
        }

        await store.SaveEndpointValidationResultsAsync(allResults, cancellationToken);
        if (options.CaptureTranscripts)
        {
            await store.SaveEndpointTranscriptsAsync(allTranscripts, cancellationToken);
        }

        return new CapabilityProbeResult
        {
            DeviceId = device.Id,
            AdapterName = options.AdapterName ?? "all-lan-http-adapters",
            FirmwareFingerprint = BuildFirmwareFingerprint(device),
            Endpoints = allResults,
            Transcripts = allTranscripts
        };
    }

    public Task<IReadOnlyCollection<EndpointValidationResult>> GetValidationResultsAsync(Guid deviceId, CancellationToken cancellationToken)
        => store.GetEndpointValidationResultsAsync(deviceId, cancellationToken);

    public Task<IReadOnlyCollection<EndpointTranscript>> GetTranscriptsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken)
        => store.GetEndpointTranscriptsAsync(deviceId, limit, cancellationToken);

    public async Task<bool> IsEndpointWriteVerifiedAsync(Guid deviceId, string adapterName, string endpoint, CancellationToken cancellationToken)
    {
        var results = await store.GetEndpointValidationResultsAsync(deviceId, cancellationToken);
        return results.Any(result =>
            result.AdapterName.Equals(adapterName, StringComparison.OrdinalIgnoreCase) &&
            result.Endpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase) &&
            result.WriteVerified);
    }

    private static IReadOnlyCollection<ProtocolEndpoint> SelectEndpointsForAdapter(IControlAdapter adapter, IReadOnlyCollection<ProtocolManifest> manifests, IReadOnlyCollection<EndpointContract> contracts)
    {
        var contracted = contracts
            .Where(contract => adapter.TransportKind switch
            {
                TransportKind.LanRest => contract.Surface == ContractSurface.NetSdkRest,
                TransportKind.LanPrivateHttp => contract.Surface == ContractSurface.PrivateCgiXml,
                _ => false
            })
            .Select(contract =>
            {
                var methods = contract.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? new List<string> { "GET" }
                    : new List<string> { "GET", contract.Method };
                return new ProtocolEndpoint
                {
                    Path = contract.Endpoint,
                    Tag = contract.GroupName,
                    Methods = methods,
                    Safety = contract.DisruptionClass.ToString()
                };
            })
            .ToList();
        var synthetic = BuildSyntheticTopGroupEndpoints(adapter.TransportKind);
        var merged = contracted
            .Concat(synthetic)
            .GroupBy(static endpoint => $"{endpoint.Path}|{string.Join(',', endpoint.Methods)}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (merged.Any())
        {
            return merged;
        }

        if (adapter.TransportKind == TransportKind.LanRest)
        {
            return manifests
                .SelectMany(static manifest => manifest.Endpoints)
                .Where(static endpoint => endpoint.Path.StartsWith("/NetSDK/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return manifests
            .Where(static manifest => manifest.ManifestId.Contains("ipcamsuite_private", StringComparison.OrdinalIgnoreCase) || manifest.Name.Contains("ipcamsuite", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static manifest => manifest.Endpoints)
            .ToList();
    }

    private static IReadOnlyCollection<ProtocolEndpoint> BuildSyntheticTopGroupEndpoints(TransportKind transportKind)
    {
        if (transportKind == TransportKind.LanRest)
        {
            return
            [
                new ProtocolEndpoint { Path = "/netsdk/system/deviceInfo", Tag = "Device", Methods = ["GET"], Safety = "read-only" },
                new ProtocolEndpoint { Path = "/netsdk/image", Tag = "Image", Methods = ["GET", "PUT"], Safety = "safe-write" },
                new ProtocolEndpoint { Path = "/netsdk/image/properties", Tag = "Image", Methods = ["GET", "PUT"], Safety = "safe-write" },
                new ProtocolEndpoint { Path = "/netsdk/video/encode/channel/101/properties", Tag = "Video", Methods = ["GET", "PUT"], Safety = "safe-write" },
                new ProtocolEndpoint { Path = "/netsdk/video/encode/channel/102/properties", Tag = "Video", Methods = ["GET", "PUT"], Safety = "safe-write" },
                new ProtocolEndpoint { Path = "/netsdk/video/input/channel/1", Tag = "Video", Methods = ["GET", "PUT"], Safety = "safe-write" },
                new ProtocolEndpoint { Path = "/netsdk/network/interface/1", Tag = "Network", Methods = ["GET", "PUT"], Safety = "network-changing" },
                new ProtocolEndpoint { Path = "/netsdk/network/interface/4", Tag = "Wireless", Methods = ["GET", "PUT"], Safety = "network-changing" },
                new ProtocolEndpoint { Path = "/netsdk/network/esee", Tag = "Network", Methods = ["GET", "PUT"], Safety = "network-changing" }
            ];
        }

        return
        [
            new ProtocolEndpoint { Path = "/user/user_list.xml", Tag = "Users", Methods = ["GET"], Safety = "read-only" },
            new ProtocolEndpoint { Path = "/NetSDK/Image/irCutfilter", Tag = "Image", Methods = ["GET", "PUT"], Safety = "safe-write" },
            new ProtocolEndpoint { Path = "/NetSDK/Factory?cmd=WhiteLightCtrl", Tag = "Image", Methods = ["GET", "PUT"], Safety = "safe-write" },
            new ProtocolEndpoint { Path = "/NetSDK/Factory?cmd=InfraRedCtrl", Tag = "Image", Methods = ["GET", "PUT"], Safety = "safe-write" },
            new ProtocolEndpoint { Path = "/user/set_pass.xml", Tag = "Users", Methods = ["POST"], Safety = "service-impacting" },
            new ProtocolEndpoint { Path = "/user/add_user.xml", Tag = "Users", Methods = ["POST"], Safety = "service-impacting" },
            new ProtocolEndpoint { Path = "/user/del_user.xml", Tag = "Users", Methods = ["POST"], Safety = "service-impacting" }
        ];
    }

    private async Task<(EndpointValidationResult Result, IReadOnlyCollection<EndpointTranscript> Transcripts)> ValidateEndpointAsync(
        DeviceIdentity device,
        IControlAdapter adapter,
        ProtocolEndpoint endpoint,
        ValidationRunOptions options,
        CancellationToken cancellationToken)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpoint.Path);
        var methods = endpoint.Methods.Count == 0 ? ["GET"] : endpoint.Methods.Select(static method => method.ToUpperInvariant()).ToList();
        var readMethod = methods.Contains("GET", StringComparer.OrdinalIgnoreCase) ? "GET" : methods[0];
        var writeMethod = methods.FirstOrDefault(static method => method is "PUT" or "POST" or "DELETE");
        var disruption = ParseDisruption(endpoint.Safety, endpoint.Tag, normalizedEndpoint);
        var transcripts = new List<EndpointTranscript>();

        var readResult = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            Endpoint = normalizedEndpoint,
            GroupName = endpoint.Tag,
            Method = readMethod,
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, readMethod, null, readResult, BuildFirmwareFingerprint(device), "pre-read"));

        var readVerified = readResult.Success;
        var writeVerified = false;
        var rollbackSupported = false;
        var persistsAfterReboot = false;
        var status = readVerified ? "confirmed-read" : "read-failed";
        string? notes = null;
        var preReadNode = readResult.Response?.DeepClone();
        JsonNode? postReadNode = null;

        if (writeMethod is not null)
        {
            if (!options.AttemptWrites)
            {
                status = "read-only-run";
                notes = "Write attempt disabled for this stage.";
            }
            else if (!options.IncludeUnsafeWrites && disruption is not (DisruptionClass.Safe or DisruptionClass.Transient))
            {
                status = "unsafe-skipped";
                notes = $"Write skipped due to disruption class {disruption}.";
            }
            else if (options.AllowedDisruptionClasses.Count > 0 && !options.AllowedDisruptionClasses.Contains(disruption))
            {
                status = "stage-skip";
                notes = $"Write skipped for stage; disruption class {disruption} not in allowed set.";
            }
            else
            {
                var writePayload = BuildWritePayload(preReadNode);
                var writeResult = await adapter.ApplyAsync(device, new WritePlan
                {
                    AdapterName = adapter.Name,
                    Endpoint = normalizedEndpoint,
                    GroupName = endpoint.Tag,
                    Method = writeMethod,
                    Payload = writePayload,
                    SnapshotBeforeWrite = false,
                    RequireWriteVerification = false
                }, cancellationToken);

                transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, writeMethod, writePayload?.ToJsonString(), writeResult, BuildFirmwareFingerprint(device), "write-attempt", beforeValue: preReadNode));

                if (readVerified)
                {
                    var postReadResult = await adapter.ApplyAsync(device, new WritePlan
                    {
                        AdapterName = adapter.Name,
                        Endpoint = normalizedEndpoint,
                        GroupName = endpoint.Tag,
                        Method = "GET",
                        SnapshotBeforeWrite = false,
                        RequireWriteVerification = false
                    }, cancellationToken);

                    postReadNode = postReadResult.Response?.DeepClone();
                    transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, "GET", null, postReadResult, BuildFirmwareFingerprint(device), "post-read", beforeValue: preReadNode, afterValue: postReadNode));
                    writeVerified = writeResult.Success && postReadResult.Success;
                }
                else
                {
                    writeVerified = writeResult.Success;
                }

                status = writeVerified ? "confirmed-write" : "write-failed";

                if (options.IncludeRollbackChecks && writeVerified && preReadNode is JsonObject preObject && postReadNode is not null && !JsonNode.DeepEquals(preReadNode, postReadNode) && writeMethod is not "DELETE")
                {
                    var rollbackResult = await adapter.ApplyAsync(device, new WritePlan
                    {
                        AdapterName = adapter.Name,
                        Endpoint = normalizedEndpoint,
                        GroupName = endpoint.Tag,
                        Method = writeMethod,
                        Payload = preObject,
                        SnapshotBeforeWrite = false,
                        RequireWriteVerification = false
                    }, cancellationToken);

                    rollbackSupported = rollbackResult.Success;
                    transcripts.Add(ToTranscript(device, adapter.Name, normalizedEndpoint, writeMethod, preObject.ToJsonString(), rollbackResult, BuildFirmwareFingerprint(device), "rollback-attempt", beforeValue: postReadNode, afterValue: preReadNode));
                }
            }
        }

        if (options.IncludePersistenceChecks && writeVerified)
        {
            notes = AppendNote(notes, "Persistence check requested but not executed automatically in this pass.");
            persistsAfterReboot = false;
        }

        var deepProbe = await TryDeepProbeEndpointAsync(device, adapter.Name, normalizedEndpoint, endpoint.Tag, readMethod, writeMethod, disruption, preReadNode, options, cancellationToken);
        transcripts.AddRange(deepProbe.Transcripts);
        if (!readVerified && deepProbe.ReadVerified)
        {
            readVerified = true;
            status = "confirmed-read-deep";
            notes = AppendNote(notes, "Deep probe unlocked readable endpoint.");
        }
        if (!writeVerified && deepProbe.WriteVerified)
        {
            writeVerified = true;
            status = deepProbe.SemanticChanged ? "confirmed-write-semantic" : "confirmed-write-deep";
            notes = AppendNote(notes, "Deep probe found writable endpoint path.");
            postReadNode = deepProbe.PostRead?.DeepClone() ?? postReadNode;
        }
        if (!string.IsNullOrWhiteSpace(deepProbe.Notes))
        {
            notes = AppendNote(notes, deepProbe.Notes);
        }
        var authMode = deepProbe.AuthMode ?? "basic-or-digest";

        return (new EndpointValidationResult
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Endpoint = normalizedEndpoint,
            Method = writeMethod ?? readMethod,
            AuthMode = authMode,
            FirmwareFingerprint = BuildFirmwareFingerprint(device),
            ReadVerified = readVerified,
            WriteVerified = writeVerified,
            PersistsAfterReboot = persistsAfterReboot,
            RollbackSupported = rollbackSupported,
            DisruptionClass = disruption,
            RequestTemplateHash = BuildRequestTemplateHash(BuildWritePayload(preReadNode)),
            Status = status,
            Notes = notes
        }, transcripts);
    }

    private static EndpointTranscript ToTranscript(
        DeviceIdentity device,
        string adapterName,
        string endpoint,
        string method,
        string? requestBody,
        WriteResult result,
        string? firmwareFingerprint,
        string notes,
        JsonNode? beforeValue = null,
        JsonNode? afterValue = null)
        => new()
        {
            DeviceId = device.Id,
            AdapterName = adapterName,
            Endpoint = endpoint,
            Method = method,
            RequestBody = requestBody,
            ResponseBody = result.Message,
            StatusCode = result.StatusCode,
            ParsedResponse = result.Response?.DeepClone(),
            FirmwareFingerprint = firmwareFingerprint,
            Success = result.Success,
            BeforeValue = beforeValue?.DeepClone(),
            AfterValue = afterValue?.DeepClone(),
            Notes = notes
        };

    private static JsonObject? BuildWritePayload(JsonNode? preReadNode)
    {
        if (preReadNode is JsonObject preObject)
        {
            return preObject;
        }

        return new JsonObject();
    }

    private async Task<DeepProbeOutcome> TryDeepProbeEndpointAsync(
        DeviceIdentity device,
        string adapterName,
        string endpoint,
        string? groupTag,
        string readMethod,
        string? writeMethod,
        DisruptionClass disruption,
        JsonNode? baselineNode,
        ValidationRunOptions options,
        CancellationToken cancellationToken)
    {
        var transcripts = new List<EndpointTranscript>();
        var authContexts = await BuildDeepAuthContextsAsync(device, cancellationToken);
        var deepReadVerified = false;
        var deepWriteVerified = false;
        var semanticChanged = false;
        string? winningAuth = null;
        string? notes = null;
        JsonNode? deepPre = baselineNode?.DeepClone();
        JsonNode? deepPost = null;

        foreach (var context in authContexts)
        {
            DeepProbeResponse? successfulRead = null;
            foreach (var readEndpoint in BuildReadEndpointCandidates(endpoint, groupTag))
            {
                var readProbe = await SendDeepProbeAsync(device, readEndpoint, readMethod, null, "application/json", context, cancellationToken);
                var readSuccess = IsRawSemanticSuccess(readProbe.StatusCode, readProbe.RawContent, readProbe.ParsedResponse);
                transcripts.Add(ToTranscript(
                    device,
                    adapterName,
                    readProbe.EffectiveEndpoint ?? readEndpoint,
                    readMethod,
                    null,
                    new WriteResult
                    {
                        Success = readSuccess,
                        StatusCode = (int?)readProbe.StatusCode,
                        Message = readProbe.RawContent,
                        Response = readProbe.ParsedResponse
                    },
                    BuildFirmwareFingerprint(device),
                    $"deep-read auth={context.Mode}"));

                if (!readSuccess)
                {
                    continue;
                }

                successfulRead = readProbe;
                deepReadVerified = true;
                winningAuth = context.Mode;
                deepPre = readProbe.ParsedResponse?.DeepClone() ?? deepPre;
                break;
            }

            if (successfulRead is null)
            {
                continue;
            }

            if (writeMethod is null || !options.AttemptWrites || !CanAttemptDeepWrite(disruption, options))
            {
                break;
            }

            if (!TryBuildMutationPayload(successfulRead.EffectiveEndpoint ?? endpoint, deepPre, out var mutationPath, out var originalValue, out var mutatedValue, out var payloadVariants))
            {
                notes = AppendNote(notes, $"No safe mutation candidate for endpoint {endpoint}.");
                continue;
            }

            foreach (var writeEndpoint in BuildWriteEndpointCandidates(endpoint, successfulRead.EffectiveEndpoint ?? endpoint))
            {
                var writeMethods = BuildWriteMethodCandidates(writeMethod, writeEndpoint);
                foreach (var candidateMethod in writeMethods)
                foreach (var variant in payloadVariants)
                {
                    var writeProbe = await SendDeepProbeAsync(device, writeEndpoint, candidateMethod, variant.Payload, variant.ContentType, context, cancellationToken);
                    var writeSuccess = IsRawSemanticSuccess(writeProbe.StatusCode, writeProbe.RawContent, writeProbe.ParsedResponse);
                    transcripts.Add(ToTranscript(
                        device,
                        adapterName,
                        writeProbe.EffectiveEndpoint ?? writeEndpoint,
                        candidateMethod,
                        variant.Payload?.ToJsonString(),
                        new WriteResult
                        {
                            Success = writeSuccess,
                            StatusCode = (int?)writeProbe.StatusCode,
                            Message = writeProbe.RawContent,
                            Response = writeProbe.ParsedResponse
                        },
                        BuildFirmwareFingerprint(device),
                        $"deep-write auth={context.Mode} method={candidateMethod} content={variant.ContentType}",
                        beforeValue: deepPre));

                    if (!writeSuccess)
                    {
                        continue;
                    }

                    var verifyRead = await SendDeepProbeAsync(device, successfulRead.EffectiveEndpoint ?? endpoint, readMethod, null, "application/json", context, cancellationToken);
                    deepPost = verifyRead.ParsedResponse?.DeepClone();
                    var actualValue = TryGetPathValue(verifyRead.ParsedResponse, mutationPath);
                    semanticChanged = actualValue is not null && !JsonNode.DeepEquals(actualValue, originalValue);
                    var matchedIntended = actualValue is not null && JsonNode.DeepEquals(actualValue, mutatedValue);
                    deepWriteVerified = IsRawSemanticSuccess(verifyRead.StatusCode, verifyRead.RawContent, verifyRead.ParsedResponse) && (semanticChanged || matchedIntended);

                    transcripts.Add(ToTranscript(
                        device,
                        adapterName,
                        verifyRead.EffectiveEndpoint ?? (successfulRead.EffectiveEndpoint ?? endpoint),
                        readMethod,
                        null,
                        new WriteResult
                        {
                            Success = deepWriteVerified,
                            StatusCode = (int?)verifyRead.StatusCode,
                            Message = verifyRead.RawContent,
                            Response = verifyRead.ParsedResponse
                        },
                        BuildFirmwareFingerprint(device),
                        $"deep-post-read auth={context.Mode} mutationPath={mutationPath}",
                        beforeValue: deepPre,
                        afterValue: deepPost));

                    if (deepWriteVerified)
                    {
                        notes = AppendNote(notes, $"Deep write verified via {context.Mode} at {mutationPath}.");
                        break;
                    }
                }

                if (deepWriteVerified)
                {
                    break;
                }
            }

            if (deepWriteVerified)
            {
                break;
            }
        }

        if (!deepReadVerified)
        {
            notes = AppendNote(notes, "No readable variant found across auth/endpoint permutations.");
        }

        return new DeepProbeOutcome(deepReadVerified, deepWriteVerified, semanticChanged, winningAuth, notes, deepPre, deepPost, transcripts);
    }

    private static bool CanAttemptDeepWrite(DisruptionClass disruption, ValidationRunOptions options)
    {
        if (options.IncludeUnsafeWrites)
        {
            return true;
        }

        if (options.AllowedDisruptionClasses.Count > 0 && !options.AllowedDisruptionClasses.Contains(disruption))
        {
            return false;
        }

        return disruption is DisruptionClass.Safe or DisruptionClass.Transient or DisruptionClass.Unknown;
    }

    private sealed record DeepAuthContext(string Mode, NetworkCredential? Credential, string? CookieHeader, string? Token, int? PortOverride = null);
    private sealed record DeepProbeResponse(HttpStatusCode? StatusCode, string? RawContent, JsonNode? ParsedResponse, string? CookieHeader, string? EffectiveEndpoint = null);
    private sealed record MutationPayloadVariant(JsonObject? Payload, string ContentType);

    private async Task<IReadOnlyCollection<DeepAuthContext>> BuildDeepAuthContextsAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var contexts = new List<DeepAuthContext>();
        var ports = BuildPortCandidates(device.Port);
        foreach (var credential in BuildCredentialCandidates(device))
        {
            foreach (var port in ports)
            {
                contexts.Add(new DeepAuthContext($"basic:{credential.UserName}:p{port}", credential, null, null, port));
                contexts.Add(new DeepAuthContext($"digest:{credential.UserName}:p{port}", credential, null, null, port));
            }
        }

        var sessions = await TryCreateSessionContextsAsync(device, BuildCredentialCandidates(device), cancellationToken);
        contexts.AddRange(sessions);

        return contexts
            .GroupBy(context => context.Mode, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static IReadOnlyCollection<NetworkCredential> BuildCredentialCandidates(DeviceIdentity device)
    {
        var candidates = new List<NetworkCredential>();
        if (!string.IsNullOrWhiteSpace(device.LoginName))
        {
            candidates.Add(new NetworkCredential(device.LoginName, device.Password ?? string.Empty));
        }

        candidates.Add(new NetworkCredential("admin", device.Password ?? string.Empty));
        candidates.Add(new NetworkCredential("admin", string.Empty));
        candidates.Add(new NetworkCredential("admin", "admin"));
        candidates.Add(new NetworkCredential("admin", "123456"));
        candidates.Add(new NetworkCredential("root", "12345"));

        return candidates
            .GroupBy(candidate => $"{candidate.UserName}:{candidate.Password}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static IReadOnlyCollection<string> BuildReadEndpointCandidates(string endpoint, string? groupTag)
    {
        var candidates = new List<string> { endpoint };
        if (!endpoint.Contains("netsdk", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/netsdk/system/deviceInfo");
        }

        candidates.Add(endpoint.Replace("/NetSDK/", "/netsdk/", StringComparison.OrdinalIgnoreCase));
        candidates.Add(endpoint.Replace("/NetSDK/", "/netsdk/", StringComparison.OrdinalIgnoreCase).Replace("/channles", "/channels", StringComparison.OrdinalIgnoreCase));
        candidates.Add(endpoint.Replace("/NetSDK/", "/netsdk/", StringComparison.OrdinalIgnoreCase).Replace("/ID", "/101", StringComparison.OrdinalIgnoreCase));

        if (endpoint.Contains("/Image", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/netsdk/image");
            candidates.Add("/netsdk/image/properties");
        }

        if (endpoint.Contains("/Video", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/netsdk/video/encode/channel/101/properties");
            candidates.Add("/netsdk/video/encode/channel/102/properties");
            candidates.Add("/netsdk/video/input/channel/1");
        }

        if (endpoint.Contains("/Network", StringComparison.OrdinalIgnoreCase) || groupTag?.Contains("Network", StringComparison.OrdinalIgnoreCase) == true)
        {
            candidates.Add("/netsdk/network/interface/1");
            candidates.Add("/netsdk/network/interface/4");
            candidates.Add("/netsdk/network/esee");
        }

        if (groupTag?.Contains("User", StringComparison.OrdinalIgnoreCase) == true || endpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/user/user_list.xml");
            candidates.Add("/user/get_sn_num");
        }

        candidates.AddRange(KnownLanReadFallbacks);

        return candidates
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyCollection<string> BuildWriteEndpointCandidates(string requestedEndpoint, string successfulReadEndpoint)
    {
        var candidates = new List<string> { requestedEndpoint, successfulReadEndpoint };
        candidates.Add(requestedEndpoint.Replace("/NetSDK/", "/netsdk/", StringComparison.OrdinalIgnoreCase));
        candidates.Add(successfulReadEndpoint.Replace("/NetSDK/", "/netsdk/", StringComparison.OrdinalIgnoreCase));

        if (requestedEndpoint.Contains("/Image", StringComparison.OrdinalIgnoreCase) || successfulReadEndpoint.Contains("/image", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/netsdk/image");
            candidates.Add("/netsdk/image/properties");
        }

        if (requestedEndpoint.Contains("/Video", StringComparison.OrdinalIgnoreCase) || successfulReadEndpoint.Contains("/video", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/netsdk/video/encode/channel/101/properties");
            candidates.Add("/netsdk/video/encode/channel/102/properties");
        }

        if (requestedEndpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase) || successfulReadEndpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("/user/set_pass.xml");
            candidates.Add("/user/add_user.xml");
            candidates.Add("/user/del_user.xml");
        }

        return candidates
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyCollection<string> BuildWriteMethodCandidates(string preferredMethod, string endpoint)
    {
        var methods = new List<string> { preferredMethod };
        if (endpoint.Contains("/user/", StringComparison.OrdinalIgnoreCase))
        {
            methods.Add("GET");
            methods.Add("POST");
        }
        else if (preferredMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            methods.Add("POST");
        }
        else if (preferredMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            methods.Add("PUT");
        }

        return methods
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyCollection<DeepAuthContext>> TryCreateSessionContextsAsync(
        DeviceIdentity device,
        IReadOnlyCollection<NetworkCredential> credentials,
        CancellationToken cancellationToken)
    {
        var sessions = new List<DeepAuthContext>();

        foreach (var credential in credentials)
        {
            foreach (var loginAttempt in BuildLoginAttempts(credential))
            {
                foreach (var port in BuildPortCandidates(device.Port))
                {
                    var loginContext = new DeepAuthContext($"login:{credential.UserName}:p{port}", credential, null, null, port);
                    var response = await SendDeepProbeAsync(device, loginAttempt.Endpoint, loginAttempt.Method, loginAttempt.Payload, loginAttempt.ContentType, loginContext, cancellationToken);
                    if (!IsRawSemanticSuccess(response.StatusCode, response.RawContent, response.ParsedResponse))
                    {
                        continue;
                    }

                    var token = ExtractToken(response.ParsedResponse, response.RawContent);
                    if (!string.IsNullOrWhiteSpace(response.CookieHeader))
                    {
                        sessions.Add(new DeepAuthContext($"session-cookie:{credential.UserName}:p{port}", credential, response.CookieHeader, token, port));
                    }
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        sessions.Add(new DeepAuthContext($"session-token:{credential.UserName}:p{port}", credential, response.CookieHeader, token, port));
                    }
                }
            }
        }

        return sessions;
    }

    private sealed record LoginAttempt(string Endpoint, string Method, JsonObject? Payload, string ContentType);

    private static IReadOnlyCollection<LoginAttempt> BuildLoginAttempts(NetworkCredential credential)
    {
        var jsonPayloads = new[]
        {
            new JsonObject { ["username"] = credential.UserName, ["password"] = credential.Password },
            new JsonObject { ["name"] = credential.UserName, ["pwd"] = credential.Password }
        };
        var attempts = new List<LoginAttempt>
        {
            new("/login.cgi", "POST", jsonPayloads[0], "application/json"),
            new("/cgi-bin/login.cgi", "POST", jsonPayloads[0], "application/json"),
            new("/user/login", "POST", jsonPayloads[0], "application/json"),
            new("/login.xml", "POST", jsonPayloads[1], "application/json"),
            new("/NetSDK/System/login", "POST", jsonPayloads[0], "application/json")
        };

        var gwXmls = new[]
        {
            $"<juan ver=\"1.0\" seq=\"0\"><login user=\"{XmlEscape(credential.UserName)}\" password=\"{XmlEscape(credential.Password)}\"><verify/></login></juan>",
            $"<juan ver=\"1.0\" seq=\"0\"><login user=\"{XmlEscape(credential.UserName)}\" password=\"{XmlEscape(credential.Password)}\"><verify=\"\"/></login></juan>",
            $"<juan ver=\"1.0\" seq=\"0\"><conf type=\"set\" user=\"{XmlEscape(credential.UserName)}\" password=\"{XmlEscape(credential.Password)}\"/></juan>"
        };
        foreach (var xml in gwXmls)
        {
            attempts.Add(new($"/cgi-bin/gw2.cgi?f=j&xml={Uri.EscapeDataString(xml)}", "GET", null, "application/json"));
            attempts.Add(new("/cgi-bin/gw2.cgi", "POST", new JsonObject { ["xml"] = xml, ["f"] = "j" }, "application/x-www-form-urlencoded"));
        }

        attempts.Add(new("/cgi-bin/hi3510/getnonce.cgi", "GET", null, "application/json"));
        return attempts;
    }

    private static IReadOnlyCollection<int> BuildPortCandidates(int basePort)
    {
        var ports = new List<int>();
        if (basePort is > 0 and <= 65535)
        {
            ports.Add(basePort);
        }
        if (!ports.Contains(80))
        {
            ports.Add(80);
        }
        if (!ports.Contains(8080))
        {
            ports.Add(8080);
        }
        return ports.Distinct().ToList();
    }

    private async Task<DeepProbeResponse> SendDeepProbeAsync(
        DeviceIdentity device,
        string endpoint,
        string method,
        JsonObject? payload,
        string contentType,
        DeepAuthContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return new DeepProbeResponse(null, null, null, null, endpoint);
        }

        var effectiveEndpoint = BuildEffectiveEndpoint(endpoint, method, payload, context);

        using var handler = new HttpClientHandler();
        if (context.Mode.StartsWith("digest:", StringComparison.OrdinalIgnoreCase) && context.Credential is not null)
        {
            handler.Credentials = context.Credential;
            handler.PreAuthenticate = false;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var targetPort = context.PortOverride ?? device.Port;
        var uri = new Uri($"http://{device.IpAddress}:{targetPort}{effectiveEndpoint}", UriKind.Absolute);
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.UserAgent.ParseAdd("IPCamSuite/1.0");
        request.Headers.Add("X-Vendor-Client", "IPCamSuite");
        if (context.Mode.StartsWith("basic:", StringComparison.OrdinalIgnoreCase) && context.Credential is not null)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.Credential.UserName}:{context.Credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        if (!string.IsNullOrWhiteSpace(context.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", context.CookieHeader);
        }

        if (!string.IsNullOrWhiteSpace(context.Token))
        {
            request.Headers.TryAddWithoutValidation("X-Session-Id", context.Token);
            request.Headers.TryAddWithoutValidation("X-Auth-Token", context.Token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        }

        if (payload is not null)
        {
            if (contentType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                var pairs = payload.Select(property => $"{Uri.EscapeDataString(property.Key)}={Uri.EscapeDataString(property.Value?.ToJsonString().Trim('\"') ?? string.Empty)}");
                request.Content = new StringContent(string.Join("&", pairs), Encoding.UTF8, contentType);
            }
            else if (contentType.Equals("application/xml", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new StringContent(ToSimpleXml(payload), Encoding.UTF8, contentType);
            }
            else
            {
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, contentType);
            }
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = TryParseRawNode(raw);
            var cookieHeader = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(cookie => cookie.Split(';', 2)[0]))
                : null;
            return new DeepProbeResponse(response.StatusCode, raw, parsed, cookieHeader, effectiveEndpoint);
        }
        catch
        {
            return new DeepProbeResponse(null, null, null, null, effectiveEndpoint);
        }
    }

    private static string BuildEffectiveEndpoint(string endpoint, string method, JsonObject? payload, DeepAuthContext context)
    {
        var effective = endpoint;
        if (effective.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(effective, UriKind.Absolute, out var absolute))
            {
                effective = absolute.PathAndQuery;
            }
        }

        if (context.Credential is null)
        {
            return effective;
        }

        if (effective.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
        {
            effective = AppendQuery(effective, "username", context.Credential.UserName);
            effective = AppendQuery(effective, "password", context.Credential.Password ?? string.Empty);
            if (payload is not null && (effective.Contains("add_user.xml", StringComparison.OrdinalIgnoreCase)
                || effective.Contains("del_user.xml", StringComparison.OrdinalIgnoreCase)
                || effective.Contains("set_pass.xml", StringComparison.OrdinalIgnoreCase)))
            {
                effective = AppendQuery(effective, "content", BuildUserContentXml(effective, payload, context.Credential));
            }
        }

        if (effective.Contains("/cgi-bin/gw2.cgi", StringComparison.OrdinalIgnoreCase) && !effective.Contains("xml=", StringComparison.OrdinalIgnoreCase))
        {
            var loginXml = $"<juan ver=\"1.0\" seq=\"0\"><login user=\"{XmlEscape(context.Credential.UserName)}\" password=\"{XmlEscape(context.Credential.Password)}\"><verify=\"\"/></login></juan>";
            effective = AppendQuery(effective, "f", "j");
            effective = AppendQuery(effective, "xml", loginXml);
        }

        if (effective.StartsWith("/NetSDK/", StringComparison.OrdinalIgnoreCase))
        {
            effective = effective.Replace("/NetSDK/", "/netsdk/", StringComparison.OrdinalIgnoreCase);
        }

        return effective;
    }

    private static string BuildUserContentXml(string endpoint, JsonObject payload, NetworkCredential credential)
    {
        if (endpoint.Contains("add_user.xml", StringComparison.OrdinalIgnoreCase))
        {
            var userName = payload["name"]?.ToJsonString().Trim('"') ?? "operator";
            var password = payload["password"]?.ToJsonString().Trim('"') ?? "123456";
            return $"<user><add_user name='{XmlEscape(userName)}' password='{XmlEscape(password)}' admin='' premit_live='yes' premit_setting='' premit_playback='' /></user>";
        }
        if (endpoint.Contains("del_user.xml", StringComparison.OrdinalIgnoreCase))
        {
            var userName = payload["name"]?.ToJsonString().Trim('"') ?? "operator";
            return $"<user><del_user name='{XmlEscape(userName)}' /></user>";
        }
        if (endpoint.Contains("set_pass.xml", StringComparison.OrdinalIgnoreCase))
        {
            var oldPass = payload["old_pass"]?.ToJsonString().Trim('"') ?? (credential.Password ?? string.Empty);
            var newPass = payload["new_pass"]?.ToJsonString().Trim('"') ?? oldPass;
            return $"<user><set_pass old_pass='{XmlEscape(oldPass)}' new_pass='{XmlEscape(newPass)}' /></user>";
        }
        return $"<user>{XmlEscape(payload.ToJsonString())}</user>";
    }

    private static string AppendQuery(string endpoint, string key, string? value)
    {
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{endpoint}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? string.Empty)}";
    }

    private static bool IsRawSemanticSuccess(HttpStatusCode? statusCode, string? raw, JsonNode? parsed)
    {
        if (statusCode is null || (int)statusCode is < 200 or >= 300)
        {
            return false;
        }

        var body = raw ?? string.Empty;
        if (body.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase)
            || body.Contains("ret=\"sorry\"", StringComparison.OrdinalIgnoreCase)
            || body.Contains("check in falied", StringComparison.OrdinalIgnoreCase)
            || body.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parsed is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("statusCode", out var codeNode)
                && codeNode is not null
                && int.TryParse(codeNode.ToJsonString().Trim('"'), out var status)
                && status != 0)
            {
                return false;
            }
            if (obj.TryGetPropertyValue("ret", out var retNode)
                && retNode is not null
                && retNode.ToJsonString().Trim('"').Equals("sorry", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonNode? TryParseRawNode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch
            {
            }
        }

        return JsonValue.Create(raw);
    }

    private static bool TryBuildMutationPayload(
        string endpoint,
        JsonNode? baseline,
        out string mutationPath,
        out JsonNode? originalValue,
        out JsonNode? mutatedValue,
        out IReadOnlyCollection<MutationPayloadVariant> payloads)
    {
        mutationPath = "$";
        originalValue = null;
        mutatedValue = null;
        payloads = [];
        if (baseline is not JsonObject baselineObject)
        {
            return false;
        }

        var mutablePath = FindMutablePath(baselineObject, "$");
        if (string.IsNullOrWhiteSpace(mutablePath))
        {
            return false;
        }

        var current = TryGetPathValue(baselineObject, mutablePath);
        var next = BuildMutatedValue(current);
        if (next is null)
        {
            return false;
        }

        var jsonPayload = (JsonObject)baselineObject.DeepClone();
        jsonPayload = SanitizePayloadForEndpoint(endpoint, jsonPayload);
        SetPathValue(jsonPayload, mutablePath, next.DeepClone());

        mutationPath = mutablePath;
        originalValue = current?.DeepClone();
        mutatedValue = next.DeepClone();

        var key = mutationPath.Split('.').Last();
        var valueText = next.ToJsonString().Trim('"');
        var endpointPayloads = new List<MutationPayloadVariant>
        {
            new(jsonPayload, "application/json"),
            new(new JsonObject { [key] = next.DeepClone() }, "application/x-www-form-urlencoded"),
            new(new JsonObject { [key] = next.DeepClone() }, "application/xml")
        };

        if (endpoint.Contains("cgi-bin", StringComparison.OrdinalIgnoreCase) && !endpoint.Contains("?", StringComparison.OrdinalIgnoreCase))
        {
            endpointPayloads.Add(new(new JsonObject { [key] = JsonValue.Create(valueText) }, "application/x-www-form-urlencoded"));
        }

        payloads = endpointPayloads;
        return true;
    }

    private static string? FindMutablePath(JsonObject root, string prefix)
    {
        foreach (var property in root)
        {
            var currentPath = $"{prefix}.{property.Key}".Replace("..", ".");
            if (SafeMutationKeys.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                return currentPath;
            }

            if (property.Value is JsonObject nested)
            {
                var nestedPath = FindMutablePath(nested, currentPath);
                if (!string.IsNullOrWhiteSpace(nestedPath))
                {
                    return nestedPath;
                }
            }
        }

        return null;
    }

    private static JsonObject SanitizePayloadForEndpoint(string endpoint, JsonObject payload)
    {
        if (endpoint.Contains("/video/encode/channel/", StringComparison.OrdinalIgnoreCase))
        {
            var cleaned = new JsonObject();
            foreach (var property in payload)
            {
                if (property.Key.EndsWith("Property", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                cleaned[property.Key] = property.Value?.DeepClone();
            }
            return cleaned;
        }

        if (endpoint.Contains("/network/esee", StringComparison.OrdinalIgnoreCase)
            && payload.TryGetPropertyValue("enabled", out var enabledNode))
        {
            return new JsonObject
            {
                ["enabled"] = enabledNode?.DeepClone()
            };
        }

        return payload;
    }

    private static JsonNode? BuildMutatedValue(JsonNode? current)
    {
        if (current is JsonValue node)
        {
            if (node.TryGetValue<int>(out var integer))
            {
                return JsonValue.Create(integer + (integer >= 100 ? -1 : 1));
            }
            if (node.TryGetValue<decimal>(out var number))
            {
                return JsonValue.Create(number + (number >= 100 ? -1 : 1));
            }
            if (node.TryGetValue<bool>(out var flag))
            {
                return JsonValue.Create(!flag);
            }
        }

        var raw = current?.ToJsonString().Trim('"');
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var mapped = raw.ToLowerInvariant() switch
            {
                "auto" => "manual",
                "manual" => "auto",
                "off" => "on",
                "on" => "off",
                "day" => "night",
                "night" => "day",
                "light" => "auto",
                "indoor" => "outdoor",
                "outdoor" => "indoor",
                "h.264" => "H.265",
                "h.265" => "H.264",
                "h.264+" => "H.265+",
                "h.265+" => "H.264+",
                "main" => "high",
                "high" => "main",
                "baseline" => "main",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                return JsonValue.Create(mapped);
            }
        }
        if (int.TryParse(raw, out var parsed))
        {
            return JsonValue.Create(parsed + (parsed >= 100 ? -1 : 1));
        }
        if (bool.TryParse(raw, out var parsedBool))
        {
            return JsonValue.Create(!parsedBool);
        }

        return null;
    }

    private static string ToSimpleXml(JsonObject payload)
    {
        var body = string.Join(string.Empty, payload.Select(property =>
            $"<{property.Key}>{XmlEscape(property.Value?.ToJsonString().Trim('\"') ?? string.Empty)}</{property.Key}>"));
        return $"<Request>{body}</Request>";
    }

    private static string XmlEscape(string? value)
        => System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private static string? ExtractToken(JsonNode? parsed, string? raw)
    {
        if (parsed is JsonObject obj)
        {
            foreach (var key in new[] { "token", "session", "sid", "SessionID", "authToken" })
            {
                if (obj.TryGetPropertyValue(key, out var value) && value is not null)
                {
                    var token = value.ToJsonString().Trim('"');
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var markers = new[] { "token=", "sid=", "session=" };
        foreach (var marker in markers)
        {
            var index = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var tail = raw[(index + marker.Length)..];
                var stop = tail.IndexOfAny(['&', ';', '"', '\n', '\r', ' ']);
                var token = stop > 0 ? tail[..stop] : tail;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }

        var sessionMatch = Regex.Match(raw, @"\+\+session\+\+(?<sid>\d+)\+\+", RegexOptions.IgnoreCase);
        if (sessionMatch.Success)
        {
            return sessionMatch.Groups["sid"].Value;
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
                current[key] = value?.DeepClone();
                return;
            }

            current[key] ??= new JsonObject();
            if (current[key] is JsonObject nested)
            {
                current = nested;
            }
            else
            {
                return;
            }
        }
    }

    private static string NormalizeEndpoint(string endpoint)
        => endpoint
            .Replace("[/properties]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[/properties]", string.Empty, StringComparison.Ordinal)
            .Replace("/ID", "/0", StringComparison.OrdinalIgnoreCase)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

    private static DisruptionClass ParseDisruption(string? safety, string? tag, string endpoint)
    {
        var text = $"{safety} {tag} {endpoint}".ToLowerInvariant();
        if (text.Contains("firmware"))
        {
            return DisruptionClass.FirmwareUpgrade;
        }

        if (text.Contains("factory"))
        {
            return DisruptionClass.FactoryReset;
        }

        if (text.Contains("reboot"))
        {
            return DisruptionClass.Reboot;
        }

        if (text.Contains("network"))
        {
            return DisruptionClass.NetworkChanging;
        }

        if (text.Contains("service-impacting"))
        {
            return DisruptionClass.ServiceImpacting;
        }

        if (text.Contains("safe-write"))
        {
            return DisruptionClass.Safe;
        }

        if (text.Contains("read-only"))
        {
            return DisruptionClass.Unknown;
        }

        return DisruptionClass.Unknown;
    }

    private static string BuildFirmwareFingerprint(DeviceIdentity device)
        => $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";

    private static string? BuildRequestTemplateHash(JsonObject? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return Convert.ToHexString(bytes);
    }

    private static string AppendNote(string? existing, string note)
        => string.IsNullOrWhiteSpace(existing) ? note : $"{existing} {note}";
}
