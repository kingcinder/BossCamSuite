using System.Net.Http.Json;
using System.Text.Json;
using BossCam.Contracts;

namespace BossCam.Desktop.Avalonia.Services;

/// <summary>
/// Production implementation of <see cref="IBossCamApiClient"/> that talks to
/// the BossCamService HTTP API at a configurable base address.
/// </summary>
public sealed class HttpBossCamApiClient : IBossCamApiClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// Creates a client pointing at the given <paramref name="baseAddress"/>.
    /// </summary>
    public HttpBossCamApiClient(string baseAddress = "http://127.0.0.1:5317")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Internal constructor that takes an existing <see cref="HttpClient"/>.
    /// Used by the DI container or advanced setups.
    /// </summary>
    internal HttpBossCamApiClient(HttpClient http) => _http = http;

    public async Task<List<DeviceIdentity>> GetDevicesAsync()
    {
        var devices = await _http.GetFromJsonAsync<List<DeviceIdentity>>("/api/devices");
        return devices ?? [];
    }

    public async Task<JsonElement?> GetLiveInfoAsync(Guid deviceId)
    {
        try
        {
            return await _http.GetFromJsonAsync<JsonElement>($"/api/devices/{deviceId}/live-info");
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> GetSnapshotAsync(Guid deviceId)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var res = await _http.GetAsync($"/api/devices/{deviceId}/snapshot?t={ts}");
            if (res.IsSuccessStatusCode)
            {
                var bytes = await res.Content.ReadAsByteArrayAsync();
                return bytes.Length > 100 ? bytes : null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SaveSnapshotAsync(Guid deviceId)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync($"/api/storage/save-snapshot/{deviceId}", "{}");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
