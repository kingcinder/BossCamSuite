using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Control;
using BossCam.NativeBridge;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

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

        foreach (var existing in device.TransportProfiles.Where(static profile => profile.Kind is TransportKind.Rtsp or TransportKind.RtspOverHttp or TransportKind.FlvOverHttp or TransportKind.Rtmp or TransportKind.OnvifRtsp))
        {
            sources.Add(new VideoSourceDescriptor
            {
                Kind = existing.Kind,
                Url = existing.Address,
                Rank = existing.Rank,
                DisplayName = existing.Kind.ToString(),
                RequiresTunnel = existing.IsRemote,
                Metadata = existing.Metadata
            });
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds) };
        try
        {
            var endpoint = $"http://{device.IpAddress}:{device.Port}/NetSDK/Stream/channel/0";
            var raw = await client.GetStringAsync(endpoint, cancellationToken);
            var node = HttpControlAdapterBase.TryParseNode(raw);
            foreach (var url in ExtractUrls(node, raw))
            {
                sources.Add(new VideoSourceDescriptor
                {
                    Kind = MapKind(url),
                    Url = url,
                    Rank = RankFor(url),
                    DisplayName = Path.GetFileName(url),
                    Metadata = new Dictionary<string, string> { ["source"] = "/NetSDK/Stream/channel/0" }
                });
            }
        }
        catch
        {
        }

        if (!sources.Any(static source => source.Kind == TransportKind.Rtsp))
        {
            sources.Add(new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = $"rtsp://{device.IpAddress}:554",
                Rank = 15,
                DisplayName = "RTSP default"
            });
        }

        return sources
            .GroupBy(static source => $"{source.Kind}:{source.Url}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static source => source.Rank)
            .ToList();
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
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }

        IReadOnlyCollection<VideoSourceDescriptor> sources =
        [
            new VideoSourceDescriptor
            {
                Kind = TransportKind.BubbleFlv,
                Url = $"http://{device.IpAddress}:{device.Port}/bubble/live?ch=1&stream=0",
                Rank = 40,
                DisplayName = "Vendor FLV",
                Metadata = new Dictionary<string, string> { ["path"] = "/bubble/live?ch=1&stream=0" }
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
