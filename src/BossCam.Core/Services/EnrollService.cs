using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Core;

/// <summary>
/// One-click fleet enrollment. The pipeline:
///  1. resolves credentials (inline → named profile → brand env var → generic env var),
///  2. probes NetSDK REST deviceInfo over <see cref="NetSdkPortCandidates"/> (recorded port → :80),
///  3. merges the identity MAC-first via <see cref="DeviceIdentityMerger"/> and persists it,
///  4. ranks video sources through <see cref="TransportBroker"/> and probes the top RTSP candidates
///     for playability (bounded, so a flaky Wi-Fi camera cannot stall enrollment),
///  5. persists last-good control port / URLs back onto the identity,
///  6. optionally starts a continuous recording job on the best playable source (snapshot pipeline
///     when no RTSP answers) and reports the outcome per step — never throws on a camera problem.
/// Credentials are never echoed back; returned URLs are redacted.
/// </summary>
public sealed class EnrollService(
    IApplicationStore store,
    IHttpClientFactory httpClientFactory,
    TransportBroker transportBroker,
    RecordingService recordingService,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<EnrollService> logger)
{
    private static readonly TimeSpan RtspProbeTimeout = TimeSpan.FromSeconds(2);
    private const int MaxRtspProbes = 3;

    public async Task<EnrollDeviceResult> EnrollDeviceAsync(EnrollDeviceRequest request, CancellationToken cancellationToken)
    {
        var steps = new List<EnrollStepResult>();
        if (string.IsNullOrWhiteSpace(request.IpAddress))
        {
            return new EnrollDeviceResult
            {
                Enrolled = false,
                IpAddress = request.IpAddress ?? string.Empty,
                Steps = [new EnrollStepResult { Step = "identity", Success = false, Message = "IpAddress is required." }]
            };
        }

        var user = string.IsNullOrWhiteSpace(request.LoginName) ? "admin" : request.LoginName;
        var password = ResolvePassword(request);

        // Step 1 — credential availability (never retry forever; one clear answer).
        if (string.IsNullOrEmpty(password))
        {
            return new EnrollDeviceResult
            {
                Enrolled = false,
                IpAddress = request.IpAddress,
                CredentialProfile = request.CredentialProfile,
                Steps =
                [
                    new EnrollStepResult
                    {
                        Step = "credentials",
                        Success = false,
                        Message = $"No password supplied and no credential profile/env password resolved for '{request.CredentialProfile ?? "default"}'. Set the password in the request or export BOSSCAM_CRED_{NormalizeProfileName(request.CredentialProfile)}_PASSWORD / BOSSCAM_PASSWORD."
                    }
                ]
            };
        }

        // Step 2 — NetSDK REST probe over the recorded-port → :80 fallback.
        var probeStep = await ProbeNetSdkAsync(request, user, password, steps, cancellationToken);
        if (probeStep.AuthFailed)
        {
            return new EnrollDeviceResult
            {
                Enrolled = false,
                IpAddress = request.IpAddress,
                CredentialProfile = request.CredentialProfile,
                Steps = steps
            };
        }

        // Step 3 — merge + persist identity.
        var device = BuildIdentity(request, user, password, probeStep);
        var existing = await FindExistingAsync(request.IpAddress, device.MacAddress, cancellationToken);
        var merged = existing is null ? device : DeviceIdentityMerger.MergePair(existing, device);
        merged = merged with
        {
            HttpControlPort = probeStep.HttpControlPort > 0 ? probeStep.HttpControlPort : merged.HttpControlPort,
            LastGoodControlUrl = probeStep.LastGoodControlUrl ?? merged.LastGoodControlUrl,
            LinkHint = request.LinkHint is LinkHint hint && hint != LinkHint.Unknown ? hint : merged.LinkHint,
            ContinuousRecord = merged.ContinuousRecord || request.StartContinuousRecord,
            DiscoveredAt = merged.DiscoveredAt == default ? DateTimeOffset.UtcNow : merged.DiscoveredAt
        };
        await store.UpsertDevicesAsync([merged], cancellationToken);
        steps.Add(new EnrollStepResult
        {
            Step = "identity",
            Success = true,
            Message = $"Persisted as {merged.DisplayName} ({(existing is null ? "new" : "merged")}, port {(probeStep.HttpControlPort > 0 ? probeStep.HttpControlPort.ToString() : "unknown")})"
        });

        // Step 4 — rank sources and probe playability (bounded).
        var (chosenUrl, role, playablePort, degradedReason) = await SelectPlayableSourceAsync(merged, steps, cancellationToken);

        // Step 5 — persist learned ports/URLs (port-learning pass).
        if (chosenUrl is not null || probeStep.HttpControlPort > 0)
        {
            var learned = merged with
            {
                HttpControlPort = probeStep.HttpControlPort > 0 ? probeStep.HttpControlPort : merged.HttpControlPort,
                LastGoodControlUrl = probeStep.LastGoodControlUrl ?? merged.LastGoodControlUrl,
                LastGoodRtspUrl = chosenUrl is null ? merged.LastGoodRtspUrl : RedactUrlCredentials(chosenUrl),
                RtspPort = playablePort is > 0 ? playablePort : merged.RtspPort
            };
            await store.UpsertDevicesAsync([learned], cancellationToken);
            merged = learned;
        }

        // Step 6 — optional continuous recording.
        string? jobId = null;
        if (request.StartContinuousRecord)
        {
            var recordStep = await TryStartContinuousAsync(merged, chosenUrl, cancellationToken);
            jobId = recordStep.JobId;
            steps.Add(recordStep.Step);
        }

        return new EnrollDeviceResult
        {
            DeviceId = merged.Id,
            IpAddress = request.IpAddress,
            Enrolled = true,
            DisplayName = merged.DisplayName,
            HardwareModel = merged.HardwareModel,
            HttpControlPort = merged.HttpControlPort,
            CredentialProfile = request.CredentialProfile,
            Steps = steps,
            ChosenSourceUrl = chosenUrl is null ? null : RedactUrlCredentials(chosenUrl),
            SourceRole = role,
            DegradedReason = degradedReason,
            ContinuousJobId = jobId
        };
    }

    public async Task<IReadOnlyCollection<EnrollDeviceResult>> EnrollManyAsync(IEnumerable<EnrollDeviceRequest> requests, CancellationToken cancellationToken)
    {
        var results = new List<EnrollDeviceResult>();
        foreach (var request in requests)
        {
            results.Add(await EnrollDeviceAsync(request, cancellationToken));
        }

        return results;
    }

    // ── pipeline helpers ───────────────────────────────────────────────────────────────

    private async Task<(bool AuthFailed, int HttpControlPort, string? LastGoodControlUrl, JsonObject? Info)> ProbeNetSdkAsync(
        EnrollDeviceRequest request,
        string user,
        string password,
        List<EnrollStepResult> steps,
        CancellationToken cancellationToken)
    {
        var ports = NetSdkPortCandidates.For(request.Port is > 0 ? request.Port.Value : 80);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        var sawAuthFailure = false;
        foreach (var port in ports)
        {
            try
            {
                using var client = httpClientFactory.CreateClient("probe");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.Value.HttpTimeoutSeconds));
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"http://{request.IpAddress}:{port}/NetSDK/System/deviceInfo");
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                using var response = await client.SendAsync(httpRequest, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The recorded port can be the ONVIF/media service, which may 401 a plain Basic
                    // header even when the NetSDK REST surface on :80 accepts the same credentials
                    // (5523-W: ONVIF 8888/8899 vs HTTP 80). Remember the 401 and keep probing the
                    // fallback; only declare auth failure when no candidate port succeeds.
                    sawAuthFailure = true;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                if (JsonNode.Parse(raw) is not JsonObject info || !LooksLikeNetSdkDeviceInfo(info))
                {
                    // 200 from an unrelated service on the fallback port is not our control plane.
                    continue;
                }

                steps.Add(new EnrollStepResult
                {
                    Step = "netsdk-probe",
                    Success = true,
                    Message = $"deviceInfo served on :{port}"
                });
                return (false, port, $"http://{request.IpAddress}:{port}/NetSDK/System/deviceInfo", info);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Enroll NetSDK probe failed on {Ip}:{Port}", request.IpAddress, port);
            }
        }

        if (sawAuthFailure)
        {
            steps.Add(new EnrollStepResult
            {
                Step = "auth",
                Success = false,
                Message = $"Authentication rejected on {request.IpAddress}:{string.Join(", ", ports.Select(static p => $":{p}"))}. Check the username/password (no retry loop)."
            });
            return (true, 0, null, null);
        }

        steps.Add(new EnrollStepResult
        {
            Step = "netsdk-probe",
            Success = false,
            Message = $"No NetSDK REST answer on {string.Join(", ", ports.Select(static p => $":{p}"))}. Device may be ONVIF-only or offline."
        });
        return (false, 0, null, null);
    }

    private static DeviceIdentity BuildIdentity(EnrollDeviceRequest request, string user, string password, (bool AuthFailed, int HttpControlPort, string? LastGoodControlUrl, JsonObject? Info) probe)
    {
        var info = probe.Info;
        var mac = info?["macAddress"]?.GetValue<string>() ?? info?["mac"]?.GetValue<string>();
        return new DeviceIdentity
        {
            IpAddress = request.IpAddress,
            Port = request.Port is > 0 ? request.Port.Value : 80,
            HttpControlPort = probe.HttpControlPort,
            LoginName = user,
            Password = password,
            Name = request.DisplayName ?? info?["deviceName"]?.GetValue<string>() ?? $"Camera {request.IpAddress}",
            HardwareModel = request.HardwareModel ?? info?["model"]?.GetValue<string>(),
            FirmwareVersion = info?["firmwareVersion"]?.GetValue<string>(),
            DeviceId = info?["serialNumber"]?.GetValue<string>(),
            EseeId = info?["eseeID"]?.GetValue<string>(),
            MacAddress = mac,
            DeviceType = "IPC",
            LinkHint = request.LinkHint ?? LinkHint.Unknown,
            ContinuousRecord = request.StartContinuousRecord,
            LastGoodControlUrl = probe.LastGoodControlUrl,
            DiscoveredAt = DateTimeOffset.UtcNow,
            TransportProfiles =
            [
                new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{request.IpAddress}:{(probe.HttpControlPort > 0 ? probe.HttpControlPort : 80)}", Rank = 5 },
                new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{request.IpAddress}:554/ch0_0.264", Rank = 10 },
                new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = $"http://{request.IpAddress}:8888/onvif/device_service", Rank = 15 }
            ]
        };
    }

    private static string NormalizeProfileName(string? profile)
        => (profile ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_');

    /// <summary>
    /// Accepts a JSON body as NetSDK deviceInfo only when it carries a recognizable camera field —
    /// otherwise a 200 from an unrelated web service on the fallback port would be misattributed
    /// to the camera (the same bar the subnet-scan audit raised for discovery).
    /// </summary>
    private static bool LooksLikeNetSdkDeviceInfo(JsonObject info)
    {
        foreach (var key in new[] { "serialNumber", "deviceName", "macAddress", "firmwareVersion", "model" })
        {
            if (info.Any(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<DeviceIdentity?> FindExistingAsync(string ipAddress, string? macAddress, CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(macAddress))
        {
            var byMac = devices.FirstOrDefault(device => string.Equals(device.MacAddress, macAddress, StringComparison.OrdinalIgnoreCase));
            if (byMac is not null)
            {
                return byMac;
            }
        }

        return devices.FirstOrDefault(device => string.Equals(device.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(string? ChosenUrl, string? Role, int? PlayablePort, string? DegradedReason)> SelectPlayableSourceAsync(
        DeviceIdentity device,
        List<EnrollStepResult> steps,
        CancellationToken cancellationToken)
    {
        var sources = await transportBroker.GetSourcesAsync(device.Id, cancellationToken);
        if (sources.Count == 0)
        {
            steps.Add(new EnrollStepResult { Step = "sources", Success = false, Message = "No video transport adapter produced a source." });
            return (null, "snapshot", null, "No video source found — recording will use the snapshot pipeline");
        }

        // Probe the highest-ranked RTSP candidates only, to keep enrollment bounded on flaky Wi-Fi.
        var rtspCandidates = sources
            .Where(static source => source.Url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static source => source.Rank)
            .Take(MaxRtspProbes)
            .ToList();

        string? playable = null;
        int? playablePort = null;
        string? role = null;
        var attempted = new List<string>();
        foreach (var candidate in rtspCandidates)
        {
            var (host, port) = ParseRtspHostPort(candidate.Url);
            if (host is null)
            {
                continue;
            }

            attempted.Add(candidate.Url);
            if (await RtspProbe.ProbeAsync(host, port, cancellationToken, RtspProbeTimeout))
            {
                playable = candidate.Url;
                playablePort = port;
                role = candidate.Metadata.TryGetValue("stream", out var stream) && stream == "sub" ? "sub" : "main";
                break;
            }
        }

        if (playable is null)
        {
            steps.Add(new EnrollStepResult
            {
                Step = "sources",
                Success = false,
                Message = $"No RTSP source answered the playability probe ({attempted.Count} tried, 2s bound each). Live/record will use the snapshot pipeline."
            });
            return (null, "snapshot", null, "No playable RTSP source — recording will use the snapshot pipeline");
        }

        steps.Add(new EnrollStepResult
        {
            Step = "sources",
            Success = true,
            Message = $"Playable {role} RTSP source at :{playablePort}"
        });
        return (playable, role, playablePort, null);
    }

    private async Task<(string? JobId, EnrollStepResult Step)> TryStartContinuousAsync(DeviceIdentity device, string? chosenUrl, CancellationToken cancellationToken)
    {
        try
        {
            var job = await recordingService.StartAsync(new RecordingStartRequest
            {
                DeviceId = device.Id,
                SourceUrl = chosenUrl
            }, cancellationToken);
            return (job.Id.ToString(), new EnrollStepResult
            {
                Step = "continuous-record",
                Success = true,
                Message = $"Job {job.Id:N} running via {job.Mode} pipeline ({(job.SourceRole ?? "main")})"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Enroll continuous record start failed for {Device}", device.DisplayName);
            return (null, new EnrollStepResult
            {
                Step = "continuous-record",
                Success = false,
                Message = ex.Message
            });
        }
    }

    /// <summary>
    /// Resolves a password for the request: inline → BOSSCAM_CRED_&lt;PROFILE&gt;_PASSWORD →
    /// brand env (BOSSCAM_LOREX_PASSWORD / BOSSCAM_WVC_PASSWORD by model) → BOSSCAM_PASSWORD.
    /// Never throws and never loops — the caller reports a clear failure when nothing resolves.
    /// </summary>
    private static string? ResolvePassword(EnrollDeviceRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            return request.Password;
        }

        var profile = NormalizeProfileName(request.CredentialProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var profilePassword = Environment.GetEnvironmentVariable($"BOSSCAM_CRED_{profile}_PASSWORD");
            if (!string.IsNullOrWhiteSpace(profilePassword))
            {
                return profilePassword;
            }
        }

        var model = (request.HardwareModel ?? string.Empty).ToLowerInvariant();
        if (model.Contains("5523", StringComparison.Ordinal) || model.Contains("juan", StringComparison.Ordinal) || model.Contains("guangzhou", StringComparison.Ordinal))
        {
            var juan = Environment.GetEnvironmentVariable("BOSSCAM_JUAN_PASSWORD")
                ?? Environment.GetEnvironmentVariable("BOSSCAM_LOREX_PASSWORD");
            if (!string.IsNullOrWhiteSpace(juan))
            {
                return juan;
            }
        }

        if (model.Contains("wvc", StringComparison.Ordinal) || model.Contains("wansview", StringComparison.Ordinal))
        {
            var wvc = Environment.GetEnvironmentVariable("BOSSCAM_WVC_PASSWORD");
            if (!string.IsNullOrWhiteSpace(wvc))
            {
                return wvc;
            }
        }

        return Environment.GetEnvironmentVariable("BOSSCAM_PASSWORD");
    }

    private static (string? Host, int Port) ParseRtspHostPort(string url)
    {
        var schemeIndex = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return (null, 554);
        }

        var rest = url[(schemeIndex + 3)..];
        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            rest = rest[(atIndex + 1)..];
        }

        var slashIndex = rest.IndexOf('/');
        if (slashIndex >= 0)
        {
            rest = rest[..slashIndex];
        }

        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(rest[(colonIndex + 1)..], out var port))
        {
            return (rest[..colonIndex], port);
        }

        return (rest, 554);
    }

    private static string RedactUrlCredentials(string url)
    {
        var schemeIndex = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return url;
        }

        var rest = url[(schemeIndex + 3)..];
        var atIndex = rest.IndexOf('@');
        if (atIndex < 0)
        {
            return url;
        }

        return $"{url[..(schemeIndex + 3)]}***@{rest[(atIndex + 1)..]}";
    }
}
