using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// SD / NVR playback: query the selected camera's on-SD recording index by a
/// time range. Distinct from host-side continuous recording — this talks to the
/// camera's own storage. Search returns a structured file list for download.
/// </summary>
public sealed partial class PlaybackViewModel : SectionViewModelBase
{
    public PlaybackViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "SD Playback";
    public override string Glyph => "\U0001F4C0";
    public override string Explain =>
        "SD Playback searches the selected camera's own memory card for recordings " +
        "in a time range. This is different from host continuous recording — these " +
        "files live on the camera. Use Find File / Get File by Time to list what's " +
        "on the card; the service can pull files to the host storage folders.";

    [ObservableProperty]
    private DateTimeOffset _beginTime = DateTimeOffset.Now.AddHours(-24);

    [ObservableProperty]
    private DateTimeOffset _endTime = DateTimeOffset.Now;

    [ObservableProperty]
    private string _cursor = string.Empty;

    [ObservableProperty]
    private string _resultText = "SD playback not run yet.";

    [ObservableProperty]
    private bool _isBusy;

    public override Task ActivateAsync() => Task.CompletedTask;

    [RelayCommand]
    private async Task FindFileAsync()
    {
        await RunAsync(async () => await Api.PlaybackFindFileAsync(DeviceId(), BeginTime, EndTime, Cursor));
    }

    [RelayCommand]
    private async Task FindNextFileAsync()
    {
        await RunAsync(async () => await Api.PlaybackFindNextFileAsync(DeviceId(), BeginTime, EndTime, Cursor));
    }

    [RelayCommand]
    private async Task GetFileByTimeAsync()
    {
        await RunAsync(async () => await Api.PlaybackGetFileByTimeAsync(DeviceId(), BeginTime, EndTime));
    }

    [RelayCommand]
    private async Task PlaybackByTimeAsync()
    {
        await RunAsync(async () => await Api.PlaybackByTimeAsync(DeviceId(), BeginTime, EndTime));
    }

    [RelayCommand]
    private async Task FindCloseAsync()
    {
        await RunAsync(async () => await Api.PlaybackFindCloseAsync(DeviceId()));
    }

    private Guid DeviceId()
    {
        if (SelectedDevice is not null)
        {
            return SelectedDevice.Id;
        }
        throw new InvalidOperationException("Select a camera on the left first.");
    }

    private async Task RunAsync(Func<Task<NvrPlaybackCallResult>> call)
    {
        if (SelectedDevice is null)
        {
            ResultText = "Select a camera on the left first.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await call();
            ResultText = result is null
                ? "Playback call returned no result."
                : result.Success
                    ? $"OK ({result.Operation}): {result.Message ?? "completed"}"
                    : $"Failed ({result.Operation}): {result.Message}";
            SetStatus(ResultText);
        }
        catch (Exception ex)
        {
            ResultText = $"Playback failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
