using BossCam.Contracts;

namespace BossCam.Desktop.Avalonia.Services;

/// <summary>
/// Abstraction over the BossCamService HTTP API. Enables unit testing
/// of <see cref="ViewModels.MainWindowViewModel"/> without a live server.
/// </summary>
public interface IBossCamApiClient : IDisposable
{
    /// <summary>GET /api/devices → all registered devices.</summary>
    Task<List<DeviceIdentity>> GetDevicesAsync();

    /// <summary>GET /api/devices/{id}/live-info → optional extended device info.</summary>
    Task<System.Text.Json.JsonElement?> GetLiveInfoAsync(Guid deviceId);

    /// <summary>GET /api/devices/{id}/snapshot → raw JPEG bytes (or null on failure).</summary>
    Task<byte[]?> GetSnapshotAsync(Guid deviceId);

    /// <summary>POST /api/storage/save-snapshot/{id} → true if saved.</summary>
    Task<bool> SaveSnapshotAsync(Guid deviceId);
}
