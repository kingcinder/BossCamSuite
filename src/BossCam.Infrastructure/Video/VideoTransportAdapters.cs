using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Control;
using BossCam.NativeBridge;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Builds authenticated 5523-W stream URLs proven against live firmware 3.6.103.5721106:
/// RTSP main <c>rtsp://user:pass@ip:554/11</c>, sub <c>/12</c> (Digest auth, Happytime RTSP).
/// Bubble live HTTP <c>/bubble/live?ch=1&amp;stream=0</c> (content-type video/bubble).
/// Snapshot JPEG <c>/NetSDK/Video/encode/channel/101/snapShot</c>.
/// </summary>
public sealed class StreamDescriptorAdapter(IOptions<BossCamRuntimeOptions> options) : IVideoTransportAdapter
{
    public string Name => nameof(StreamDescriptorAdapter);
    public TransportKind TransportKind => TransportKind.LanRest;
    public int Priority => 10;

    public async Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var sources = new List<VideoSourceDescriptor>();
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return sources;
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var authPrefix = BuildAuthPrefix(user, password);
        var port = device.Port <= 0 ? 80 : device.Port;

        foreach (var existing in device.TransportProfiles.Where(static profile => profile.Kind is TransportKind.Rtsp or TransportKind.RtspOverHttp or TransportKind.FlvOverHttp or TransportKind.Rtmp or TransportKind.OnvifRtsp))
        {
            sources.Add(new VideoSourceDescriptor
            {
                Kind = existing.Kind,
                Url = InjectCredentialsIfMissing(existing.Address, user, password),
                Rank = existing.Rank,
                DisplayName = existing.Kind.ToString(),
                RequiresTunnel = existing.IsRemote,
                Metadata = existing.Metadata
            });
        }

        // Proven 5523-W / Juan-family paths (high-res main is ch0_0.264 HEVC 2560x1920).
        if (LooksLikeJuanNetSdkFamily(device))
        {
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{authPrefix}{device.IpAddress}:554/ch0_0.264",
                Rank = 0,
                DisplayName = "RTSP main high-res (ch0_0.264)",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "main",
                    ["path"] = "/ch0_0.264",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "101",
                    ["highRes"] = "true",
                    ["resolution"] = "2560x1920"
                }
            });
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{authPrefix}{device.IpAddress}:554/ch0_1.264",
                Rank = 50,
                DisplayName = "RTSP sub (ch0_1.264)",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "sub",
                    ["path"] = "/ch0_1.264",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "102",
                    ["highRes"] = "false",
                    ["resolution"] = "704x480"
                }
            });
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{authPrefix}{device.IpAddress}:554/11",
                Rank = 4,
                DisplayName = "RTSP /11 alias",
                Metadata = new Dictionary<string, string>
                {
                    ["stream"] = "main",
                    ["path"] = "/11",
                    ["auth"] = "digest",
                    ["encodeChannel"] = "101",
                    ["highRes"] = "true"
                }
            });

            // NetSDK snapShot is often sub-resolution; keep for tiles only.
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.LanRest,
                Url = $"http://{authPrefix}{device.IpAddress}:{port}/NetSDK/Video/encode/channel/101/snapShot",
                Rank = 25,
                DisplayName = "JPEG snapshot (NetSDK)",
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = "snapshot",
                    ["contentType"] = "image/jpg",
                    ["endpoint"] = "/NetSDK/Video/encode/channel/101/snapShot",
                    ["highRes"] = "false"
                }
            });
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds) };
        try
        {
            var endpoint = $"http://{device.IpAddress}:{port}/NetSDK/Stream/channel/0";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var credential = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credential);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                var node = HttpControlAdapterBase.TryParseNode(raw);
                foreach (var url in ExtractUrls(node, raw))
                {
                    sources.Add(new VideoSourceDescriptor
                    {
                        Kind = MapKind(url),
                        Url = InjectCredentialsIfMissing(url, user, password),
                        Rank = RankFor(url),
                        DisplayName = Path.GetFileName(url),
                        Metadata = new Dictionary<string, string> { ["source"] = "/NetSDK/Stream/channel/0" }
                    });
                }
            }
        }
        catch
        {
        }

        return sources
            .GroupBy(static source => $"{source.Kind}:{source.Url}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static source => source.Rank)
            .ToList();
    }

    internal static bool LooksLikeJuanNetSdkFamily(DeviceIdentity device)
    {
        var model = device.HardwareModel ?? string.Empty;
        var name = device.Name ?? string.Empty;
        var type = device.DeviceType ?? string.Empty;
        if (model.Contains("5523", StringComparison.OrdinalIgnoreCase)
            || name.Contains("5523", StringComparison.OrdinalIgnoreCase)
            || type.Equals("IPC", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(device.EseeId))
        {
            return true;
        }

        // Explicit transport profile already pointing at NetSDK/bubble also qualifies.
        return device.TransportProfiles.Any(static p =>
            p.Kind is TransportKind.LanRest or TransportKind.BubbleFlv
            || p.Address.Contains("/NetSDK/", StringComparison.OrdinalIgnoreCase)
            || p.Address.Contains("/bubble/", StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildAuthPrefix(string user, string password)
        => $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";

    internal static string InjectCredentialsIfMissing(string url, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains('@', StringComparison.Ordinal))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (uri.Scheme is not ("rtsp" or "http" or "https" or "rtmp"))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = user,
            Password = password
        };
        return builder.Uri.ToString();
    }

    private static IEnumerable<string> ExtractUrls(JsonNode? node, string raw)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node is not null)
        {
            Walk(node, urls);
        }

        foreach (var token in raw.Split(['"', '\r', '\n', ' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(token.Trim());
            }
        }

        return urls;
    }

    private static void Walk(JsonNode node, ISet<string> urls)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(text);
                }
                break;
            case JsonObject obj:
                foreach (var child in obj)
                {
                    if (child.Value is not null)
                    {
                        Walk(child.Value, urls);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Walk(child, urls);
                    }
                }
                break;
        }
    }

    private static TransportKind MapKind(string url)
        => url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ? TransportKind.Rtsp
            : url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ? TransportKind.Rtmp
            : url.Contains("flv", StringComparison.OrdinalIgnoreCase) ? TransportKind.FlvOverHttp
            : url.Contains("rtspoverhttp", StringComparison.OrdinalIgnoreCase) ? TransportKind.RtspOverHttp
            : TransportKind.LanRest;

    private static int RankFor(string url)
        => MapKind(url) switch
        {
            TransportKind.Rtsp => 10,
            TransportKind.RtspOverHttp => 11,
            TransportKind.FlvOverHttp => 12,
            TransportKind.Rtmp => 13,
            _ => 20
        };
}

public sealed class BubbleFlvAdapter : IVideoTransportAdapter
{
    public string Name => nameof(BubbleFlvAdapter);
    public TransportKind TransportKind => TransportKind.BubbleFlv;
    public int Priority => 20;

    public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress) || !StreamDescriptorAdapter.LooksLikeJuanNetSdkFamily(device))
        {
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }

        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var password = device.Password ?? string.Empty;
        var authPrefix = StreamDescriptorAdapter.BuildAuthPrefix(user, password);
        var port = device.Port <= 0 ? 80 : device.Port;

        // Proven live: content-type video/bubble on both 5523-W units.
        IReadOnlyCollection<VideoSourceDescriptor> sources =
        [
            new VideoSourceDescriptor
            {
                Kind = TransportKind.BubbleFlv,
                Url = $"http://{authPrefix}{device.IpAddress}:{port}/bubble/live?ch=1&stream=0",
                Rank = 30,
                DisplayName = "Bubble live main",
                Metadata = new Dictionary<string, string>
                {
                    ["path"] = "/bubble/live?ch=1&stream=0",
                    ["stream"] = "main",
                    ["contentType"] = "video/bubble"
                }
            },
            new VideoSourceDescriptor
            {
                Kind = TransportKind.BubbleFlv,
                Url = $"http://{authPrefix}{device.IpAddress}:{port}/bubble/live?ch=1&stream=1",
                Rank = 31,
                DisplayName = "Bubble live sub",
                Metadata = new Dictionary<string, string>
                {
                    ["path"] = "/bubble/live?ch=1&stream=1",
                    ["stream"] = "sub",
                    ["contentType"] = "video/bubble"
                }
            }
        ];
        return Task.FromResult(sources);
    }
}

public sealed class EseeJuanP2PAdapter(IOptions<BossCamRuntimeOptions> options) : IVideoTransportAdapter
{
    public string Name => nameof(EseeJuanP2PAdapter);
    public TransportKind TransportKind => TransportKind.EseeJuanP2P;
    public int Priority => 30;

    public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.EseeId))
        {
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }

        IReadOnlyCollection<VideoSourceDescriptor> sources =
        [
            new VideoSourceDescriptor
            {
                Kind = TransportKind.EseeJuanP2P,
                Url = $"esee://{device.EseeId}",
                Rank = 60,
                DisplayName = "ESEE/Juan P2P",
                RequiresTunnel = true,
                Metadata = new Dictionary<string, string> { ["library"] = Path.Combine(options.Value.EseeCloudDirectory, "juanclient-new.dll") }
            }
        ];
        return Task.FromResult(sources);
    }
}

public sealed class Kp2pAdapter(IOptions<BossCamRuntimeOptions> options) : IVideoTransportAdapter
{
    public string Name => nameof(Kp2pAdapter);
    public TransportKind TransportKind => TransportKind.Kp2p;
    public int Priority => 40;

    public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var library = NativeLibraryCatalog.Discover(options.Value.IpcamSuiteDirectory, options.Value.EseeCloudDirectory).FirstOrDefault(static entry => entry.Name.Equals("P2PSDKClient.dll", StringComparison.OrdinalIgnoreCase));
        if (library is null || !library.Exists || string.IsNullOrWhiteSpace(device.EseeId))
        {
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }

        IReadOnlyCollection<VideoSourceDescriptor> sources =
        [
            new VideoSourceDescriptor
            {
                Kind = TransportKind.Kp2p,
                Url = $"kp2p://{device.EseeId}",
                Rank = 70,
                DisplayName = "KP2P",
                RequiresTunnel = true,
                Metadata = new Dictionary<string, string> { ["library"] = library.Path }
            }
        ];
        return Task.FromResult(sources);
    }
}

public sealed class LinkVisionAdapter(IOptions<BossCamRuntimeOptions> options) : IVideoTransportAdapter
{
    public string Name => nameof(LinkVisionAdapter);
    public TransportKind TransportKind => TransportKind.LinkVision;
    public int Priority => 50;

    public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var library = NativeLibraryCatalog.Discover(options.Value.IpcamSuiteDirectory, options.Value.EseeCloudDirectory).FirstOrDefault(static entry => entry.Name.Equals("LinkVisionGetUrl.dll", StringComparison.OrdinalIgnoreCase));
        if (library is null || !library.Exists)
        {
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }

        var identifier = device.EseeId ?? device.DeviceId;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }

        IReadOnlyCollection<VideoSourceDescriptor> sources =
        [
            new VideoSourceDescriptor
            {
                Kind = TransportKind.LinkVision,
                Url = $"linkvision://{identifier}",
                Rank = 80,
                DisplayName = "LinkVision",
                RequiresTunnel = true,
                Metadata = new Dictionary<string, string> { ["library"] = library.Path }
            }
        ];
        return Task.FromResult(sources);
    }
}
