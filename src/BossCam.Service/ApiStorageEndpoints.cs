using System.Text.Json;
using BossCam.Contracts;
using BossCam.Core;
using Microsoft.Extensions.Options;

namespace BossCam.Service;

/// <summary>
/// Maps operator media storage endpoints: get/set storage paths and save snapshot to disk.
/// The storage-root allowlist prevents leaked-LAN-token path traversal.
/// </summary>
public static class ApiStorageEndpoints
{
    public static WebApplication MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/storage/paths", () => Results.Ok(LoadMediaStoragePaths()));

        app.MapPost("/api/storage/paths", (MediaStoragePaths paths, IOptions<BossCamRuntimeOptions> runtime) =>
        {
            if (string.IsNullOrWhiteSpace(paths.ContinuousRecordings)
                || string.IsNullOrWhiteSpace(paths.Highlights)
                || string.IsNullOrWhiteSpace(paths.Snapshots))
            {
                return Results.BadRequest(new { error = "ContinuousRecordings, Highlights, and Snapshots paths are required." });
            }

            var storageRoot = ResolveStorageRoot(runtime.Value.StorageRoot);
            MediaStoragePaths normalized;
            try
            {
                normalized = NormalizeAndValidateStoragePaths(paths, storageRoot);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            Directory.CreateDirectory(normalized.ContinuousRecordings);
            Directory.CreateDirectory(normalized.Highlights);
            Directory.CreateDirectory(normalized.Snapshots);
            File.WriteAllText(MediaStorageConfigPath(), JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            return Results.Ok(normalized);
        });

        app.MapPost("/api/storage/save-snapshot/{id:guid}", async (Guid id, IApplicationStore store, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            var device = await store.GetDeviceAsync(id, ct);
            if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
            {
                return Results.NotFound(new { error = "Device not found." });
            }

            var paths = LoadMediaStoragePaths();
            Directory.CreateDirectory(paths.Snapshots);

            var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
            var password = device.Password ?? string.Empty;
            var port = device.Port <= 0 ? 80 : device.Port;
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));

            using var client = httpClientFactory.CreateClient("snapshot");
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{device.IpAddress}:{port}/NetSDK/Video/encode/channel/101/snapShot");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            using var response = await client.SendAsync(request, ct);

            byte[] bytes;
            if (response.IsSuccessStatusCode)
            {
                bytes = await response.Content.ReadAsByteArrayAsync(ct);
            }
            else
            {
                return Results.StatusCode((int)response.StatusCode);
            }

            if (bytes.Length < 500)
            {
                return Results.BadRequest(new { error = "Snapshot payload too small." });
            }

            var name = $"{(device.IpAddress ?? id.ToString("N")).Replace('.', '_')}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.jpg";
            var filePath = Path.Combine(paths.Snapshots, name);
            await File.WriteAllBytesAsync(filePath, bytes, ct);
            return Results.Ok(new { path = filePath, bytes = bytes.Length });
        });

        return app;
    }

    // ---- Storage helper functions (copied from Program.cs) ----

    private static string MediaStorageConfigPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BossCamSuite");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "media-storage.json");
    }

    private static MediaStoragePaths DefaultMediaStoragePaths()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BossCamSuite");
        return new MediaStoragePaths
        {
            ContinuousRecordings = Path.Combine(root, "recordings", "continuous"),
            Highlights = Path.Combine(root, "recordings", "highlights"),
            Snapshots = Path.Combine(root, "snapshots")
        };
    }

    private static MediaStoragePaths LoadMediaStoragePaths()
    {
        var path = MediaStorageConfigPath();
        if (!File.Exists(path))
        {
            var defaults = DefaultMediaStoragePaths();
            Directory.CreateDirectory(defaults.ContinuousRecordings);
            Directory.CreateDirectory(defaults.Highlights);
            Directory.CreateDirectory(defaults.Snapshots);
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            return defaults;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<MediaStoragePaths>(File.ReadAllText(path));
            return loaded ?? DefaultMediaStoragePaths();
        }
        catch
        {
            return DefaultMediaStoragePaths();
        }
    }

    private static string ResolveStorageRoot(string configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot.Trim());
        }

        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BossCamSuite");
        return Path.Combine(dataRoot, "recordings");
    }

    private static MediaStoragePaths NormalizeAndValidateStoragePaths(MediaStoragePaths paths, string storageRoot)
    {
        var canonicalRoot = storageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string Canonicalize(string field, string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new InvalidOperationException($"{field} path is required.");
            }

            var resolved = Path.GetFullPath(input.Trim());
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolved.StartsWith(canonicalRoot, comparison))
            {
                throw new InvalidOperationException(
                    $"{field} path '{resolved}' is outside the configured storage root '{canonicalRoot}'. " +
                    "Configure BossCam:StorageRoot to widen the allowed region, or submit paths under it.");
            }
            return resolved;
        }

        return new MediaStoragePaths
        {
            ContinuousRecordings = Canonicalize(nameof(paths.ContinuousRecordings), paths.ContinuousRecordings),
            Highlights = Canonicalize(nameof(paths.Highlights), paths.Highlights),
            Snapshots = Canonicalize(nameof(paths.Snapshots), paths.Snapshots)
        };
    }
}
