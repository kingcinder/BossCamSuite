using Avalonia.Media.Imaging;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// One auto-loading camera tile on the Live View landing board. Polls the camera's
/// snapshot on a 2s timer (the same cadence as the shell's live picture) so every
/// starred camera streams onto the landing page at startup with zero interaction.
/// Star toggle, selection and fullscreen commands route through the shell so the
/// star set stays server-synced (mirrors the SPA's Live Wall tiles).
/// </summary>
public sealed partial class BoardTileViewModel : ObservableObject, IDisposable
{
    private readonly IBossCamApiClient _api;
    private readonly MainWindowViewModel _shell;
    private Timer? _snapshotTimer;
    private bool _disposed;

    public BoardTileViewModel(IBossCamApiClient api, MainWindowViewModel shell, DeviceIdentity device)
    {
        _api = api;
        _shell = shell;
        Device = device;
        _snapshotTimer = new Timer(async _ => await PollSnapshotAsync(), null, 0, 2000);
        _shell.StarsChanged += OnStarsChanged;
    }

    public DeviceIdentity Device { get; }

    public string DisplayName => Device.DisplayName ?? Device.IpAddress ?? Device.Id.ToString();

    public string IpAddress => Device.IpAddress ?? string.Empty;

    [ObservableProperty]
    private Bitmap? _liveFrame;

    /// <summary>Consecutive snapshot failures (drives the offline badge).</summary>
    private int _snapshotFailures;

    /// <summary>
    /// Offline badge shown when the camera stopped answering snapshots — the feed
    /// watchdog (scripts/video-feed-watchdog.sh) monitors the same signal and applies
    /// its repair tree (probe → reconnect → restart recording → hunt → page).
    /// Null (not empty) when healthy so the NotNullToBoolConverter keeps the badge
    /// hidden on tiles that are streaming.
    /// </summary>
    public string? TileStateText => _snapshotFailures >= 3 ? "● offline — watchdog active" : null;

    public bool IsStarred => _shell.IsStarred(Device.Id);

    /// <summary>Hollow ☆ / gold ★ glyph for the tile's star button.</summary>
    public string StarGlyph => IsStarred ? "★" : "☆";

    /// <summary>True while the tile is the shell's selected camera (drives the detail pane below).</summary>
    public bool IsSelected => _shell.SelectedDevice?.Id == Device.Id;

    private void OnStarsChanged()
    {
        OnPropertyChanged(nameof(IsStarred));
        OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>Refreshes IsSelected when the shell's selection changes (toggled by the shell).</summary>
    internal void RefreshSelectionState() => OnPropertyChanged(nameof(IsSelected));

    [RelayCommand]
    private void ToggleStar() => _shell.ToggleStarCommand.Execute(Device);

    [RelayCommand]
    private void Select() => _shell.SelectedDevice = Device;

    [RelayCommand]
    private void OpenFullscreen() => _shell.OpenFullscreenCommand.Execute(Device);

    private async Task PollSnapshotAsync()
    {
        if (_disposed)
        {
            return;
        }

        var bytes = await _api.GetSnapshotAsync(Device.Id);
        if (bytes is { Length: > 100 })
        {
            var wasOffline = TileStateText is not null;
            _snapshotFailures = 0;
            if (wasOffline)
            {
                // Camera came back: hide the offline badge.
                OnPropertyChanged(nameof(TileStateText));
            }
            using var ms = new MemoryStream(bytes);
            LiveFrame = new Bitmap(ms);
        }
        else
        {
            // Camera not answering: keep counting so the tile shows its offline badge.
            // The snapshot layer stays dark rather than showing a stale frame.
            if (_snapshotFailures < int.MaxValue)
            {
                _snapshotFailures++;
            }
            if (_snapshotFailures == 3)
            {
                OnPropertyChanged(nameof(TileStateText));
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _shell.StarsChanged -= OnStarsChanged;
        _snapshotTimer?.Dispose();
        _snapshotTimer = null;
    }
}
