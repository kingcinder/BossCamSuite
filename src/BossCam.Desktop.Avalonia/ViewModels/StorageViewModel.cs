using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Storage: view and edit the media storage folders (continuous recordings,
/// highlights, snapshots) used by the recording pipeline.
/// </summary>
public sealed partial class StorageViewModel : SectionViewModelBase
{
    public StorageViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Storage";
    public override string Glyph => "\U0001F4BE";
    public override string Explain =>
        "Storage shows the folders BossCamSuite writes media to: continuous " +
        "recordings, highlight clips and snapshots. Edit the paths and press Save; " +
        "the service starts writing to the new locations immediately.";

    [ObservableProperty]
    private string _continuousRecordings = string.Empty;

    [ObservableProperty]
    private string _highlights = string.Empty;

    [ObservableProperty]
    private string _snapshots = string.Empty;

    [ObservableProperty]
    private string _resultText = "Storage paths not loaded.";

    public override async Task ActivateAsync()
    {
        if (string.IsNullOrEmpty(ContinuousRecordings))
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var paths = await Api.GetStoragePathsAsync();
            if (paths is null)
            {
                ResultText = "Service returned no storage paths.";
                return;
            }
            ContinuousRecordings = paths.ContinuousRecordings ?? string.Empty;
            Highlights = paths.Highlights ?? string.Empty;
            Snapshots = paths.Snapshots ?? string.Empty;
            ResultText = "Storage paths loaded.";
        }
        catch (Exception ex)
        {
            ResultText = $"Storage paths load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var saved = await Api.SaveStoragePathsAsync(new Contracts.MediaStoragePaths
            {
                ContinuousRecordings = ContinuousRecordings?.Trim() ?? string.Empty,
                Highlights = Highlights?.Trim() ?? string.Empty,
                Snapshots = Snapshots?.Trim() ?? string.Empty
            });
            ResultText = saved is null
                ? "Save completed (no confirmation payload)."
                : "Storage paths saved.";
            SetStatus("Storage paths saved.");
        }
        catch (Exception ex)
        {
            ResultText = $"Save failed: {ex.Message}";
        }
    }
}
