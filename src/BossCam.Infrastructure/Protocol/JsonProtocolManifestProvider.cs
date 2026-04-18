using System.Text.Json;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Protocol;

public sealed class JsonProtocolManifestProvider(
    IOptions<BossCamRuntimeOptions> options,
    ILogger<JsonProtocolManifestProvider> logger) : IProtocolManifestProvider
{
    public async Task<IReadOnlyCollection<ProtocolManifest>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.ProtocolAssetsPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return [];
        }

        var manifests = new List<ProtocolManifest>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly).OrderBy(static file => file, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    manifests.Add(ConvertEndpointCatalog(file, root));
                    continue;
                }

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("manifestId", out _))
                {
                    var manifest = JsonSerializer.Deserialize<ProtocolManifest>(root.GetRawText(), SerializerOptions());
                    if (manifest is not null)
                    {
                        manifests.Add(manifest);
                    }
                    continue;
                }

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("paths", out var paths))
                {
                    manifests.Add(ConvertOpenApi(file, paths));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load protocol manifest from {File}", file);
            }
        }

        return manifests;
    }

    private static ProtocolManifest ConvertEndpointCatalog(string file, JsonElement root)
    {
        var endpoints = new List<ProtocolEndpoint>();
        foreach (var item in root.EnumerateArray())
        {
            var methods = item.TryGetProperty("methods", out var methodsElement) && methodsElement.ValueKind == JsonValueKind.Array
                ? methodsElement.EnumerateArray().Select(static value => value.GetString() ?? string.Empty).Where(static value => value.Length > 0).ToList()
                : [];

            string? description = null;
            string? requestSchema = null;
            string? responseSchema = null;
            if (item.TryGetProperty("details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.Object)
            {
                var methodDetails = detailsElement.EnumerateObject().FirstOrDefault().Value;
                if (methodDetails.ValueKind == JsonValueKind.Object)
                {
                    description = TryGetString(methodDetails, "description");
                    requestSchema = TryGetString(methodDetails, "content");
                    responseSchema = TryGetString(methodDetails, "success_return");
                }
            }

            endpoints.Add(new ProtocolEndpoint
            {
                Path = TryGetString(item, "endpoint") ?? string.Empty,
                Tag = TryGetString(item, "tag") ?? Path.GetFileNameWithoutExtension(file),
                Methods = methods,
                Description = description ?? TryGetString(item, "title"),
                RequestSchema = requestSchema,
                ResponseSchema = responseSchema,
                Safety = methods.Any(static method => method is "PUT" or "POST" or "DELETE") ? "safe-write" : "read-only"
            });
        }

        return new ProtocolManifest
        {
            ManifestId = Path.GetFileNameWithoutExtension(file),
            Name = Path.GetFileName(file),
            Source = file,
            Family = "NETSDK V1.4 extracted endpoint catalog",
            AuthMode = "basic-or-digest",
            Endpoints = endpoints
        };
    }

    private static ProtocolManifest ConvertOpenApi(string file, JsonElement paths)
    {
        var endpoints = new List<ProtocolEndpoint>();
        foreach (var pathEntry in paths.EnumerateObject())
        {
            var methods = new List<string>();
            string? description = null;
            foreach (var methodEntry in pathEntry.Value.EnumerateObject())
            {
                methods.Add(methodEntry.Name.ToUpperInvariant());
                description ??= TryGetString(methodEntry.Value, "summary") ?? TryGetString(methodEntry.Value, "description");
            }

            endpoints.Add(new ProtocolEndpoint
            {
                Path = pathEntry.Name,
                Tag = "OpenApi",
                Methods = methods,
                Description = description,
                Safety = methods.Any(static method => method is "PUT" or "POST" or "DELETE") ? "safe-write" : "read-only"
            });
        }

        return new ProtocolManifest
        {
            ManifestId = Path.GetFileNameWithoutExtension(file),
            Name = Path.GetFileName(file),
            Source = file,
            Family = "NETSDK V1.4 OpenAPI draft",
            AuthMode = "basic-or-digest",
            Endpoints = endpoints
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonSerializerOptions SerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }
}
