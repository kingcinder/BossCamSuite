using System.Text.Json;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Test double for <see cref="IBossCamApiClient"/>. Returns pre-configured
/// responses so ViewModel tests are deterministic and fast.
/// </summary>
public sealed class TestBossCamApiClient : IBossCamApiClient
{
    public List<DeviceIdentity>? DevicesResult { get; set; }
    public JsonElement? LiveInfoResult { get; set; }
    public byte[]? SnapshotResult { get; set; }
    public bool SaveSnapshotResult { get; set; } = true;
    public int GetDevicesCallCount { get; private set; }
    public int GetLiveInfoCallCount { get; private set; }
    public int GetSnapshotCallCount { get; private set; }
    public int SaveSnapshotCallCount { get; private set; }

    public Task<List<DeviceIdentity>> GetDevicesAsync()
    {
        GetDevicesCallCount++;
        // null result by default => throw so tests that forget to set up
        // results hit a clear failure, and the catch-path test can verify it.
        return DevicesResult is null
            ? throw new HttpRequestException("Simulated API failure (DevicesResult was null)")
            : Task.FromResult(DevicesResult);
    }

    public Task<JsonElement?> GetLiveInfoAsync(Guid deviceId)
    {
        GetLiveInfoCallCount++;
        return Task.FromResult(LiveInfoResult);
    }

    public Task<byte[]?> GetSnapshotAsync(Guid deviceId)
    {
        GetSnapshotCallCount++;
        return Task.FromResult(SnapshotResult);
    }

    public Task<bool> SaveSnapshotAsync(Guid deviceId)
    {
        SaveSnapshotCallCount++;
        return Task.FromResult(SaveSnapshotResult);
    }

    public void Dispose() { }
}
