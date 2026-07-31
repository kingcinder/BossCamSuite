using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Recordings: host-side continuous recording (NVR-lite). Shows running jobs,
/// the segment index, lets you start/stop jobs, refresh the index, export a
/// clip from a time range (copy-first concat) and run housekeeping/reconcile.
/// </summary>
public sealed partial class RecordingsViewModel : SectionViewModelBase
{
    public RecordingsViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Recordings";
    public override string Glyph => "\U0001F4FA";
    public override string Explain =>
        "Recordings runs the host-side continuous recorder. Start a recording for " +
        "the selected camera (or all cameras), watch running jobs, browse indexed " +
        "segments, export a clip from a time range, and run housekeeping to keep " +
        "disk usage within retention limits.";

    [ObservableProperty]
    private ObservableCollection<RecordingJob> _jobs = [];

    [ObservableProperty]
    private ObservableCollection<RecordingSegment> _segments = [];

    [ObservableProperty]
    private ObservableCollection<RecordingProfile> _profiles = [];

    [ObservableProperty]
    private string _resultText = "Recording jobs not loaded yet.";

    [ObservableProperty]
    private DateTimeOffset _exportStart = DateTimeOffset.Now.AddHours(-1);

    [ObservableProperty]
    private DateTimeOffset _exportEnd = DateTimeOffset.Now;

    [ObservableProperty]
    private string _exportPath = string.Empty;

    [ObservableProperty]
    private string _housekeepingResult = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public override async Task ActivateAsync()
    {
        if (Jobs.Count == 0)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Jobs = new ObservableCollection<RecordingJob>(await Api.GetRecordingJobsAsync() ?? []);
            Profiles = new ObservableCollection<RecordingProfile>(await Api.GetRecordingProfilesAsync() ?? []);
            Segments = new ObservableCollection<RecordingSegment>(await Api.GetRecordingIndexAsync(40) ?? []);
            ResultText = $"{Jobs.Count} job(s), {Segments.Count} indexed segment(s).";
        }
        catch (Exception ex)
        {
            ResultText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartSelectedAsync()
    {
        if (SelectedDevice is null)
        {
            ResultText = "Select a camera on the left to start its recording.";
            return;
        }

        IsBusy = true;
        try
        {
            var job = await Api.StartRecordingAsync(SelectedDevice.Id);
            // Refresh first so the action outcome (not the refresh summary) is
            // what the operator sees in ResultText.
            await RefreshAsync();
            ResultText = job is null
                ? "Start returned no job."
                : $"Recording started for {SelectedDevice.DisplayName} (role={job.SourceRole ?? "\u2014"}).";
        }
        catch (Exception ex)
        {
            ResultText = $"Start failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        IsBusy = true;
        try
        {
            var jobs = await Api.StartAllRecordingsAsync();
            await RefreshAsync();
            ResultText = $"Started {jobs?.Count ?? 0} recording(s).";
        }
        catch (Exception ex)
        {
            ResultText = $"Start-all failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        IsBusy = true;
        try
        {
            var jobs = await Api.StopAllRecordingsAsync();
            await RefreshAsync();
            ResultText = $"Stopped {jobs?.Count ?? 0} recording(s).";
        }
        catch (Exception ex)
        {
            ResultText = $"Stop-all failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopJobAsync(RecordingJob job)
    {
        if (job is null) return;
        try
        {
            var stopped = await Api.StopRecordingAsync(job.Id);
            await RefreshAsync();
            ResultText = stopped is null ? "Stop returned no job." : $"Stopped {stopped.Id}.";
        }
        catch (Exception ex)
        {
            ResultText = $"Stop failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshIndexAsync()
    {
        IsBusy = true;
        try
        {
            var segments = await Api.RefreshRecordingIndexAsync();
            Segments = new ObservableCollection<RecordingSegment>(segments ?? []);
            ResultText = $"Index refreshed: {Segments.Count} segment(s).";
        }
        catch (Exception ex)
        {
            ResultText = $"Index refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportClipAsync()
    {
        if (SelectedDevice is null)
        {
            ResultText = "Select a camera on the left to export its clip.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Api.ExportClipAsync(new ClipExportRequest
            {
                DeviceId = SelectedDevice.Id,
                StartTime = ExportStart,
                EndTime = ExportEnd,
                OutputPath = ExportPath ?? string.Empty
            });
            ResultText = result is null
                ? "Export returned no result."
                : result.Success
                    ? $"Exported {result.OutputPath} ({result.Bytes} bytes, {(result.ReEncoded ? "re-encoded" : "copy-first")})."
                    : $"Export failed: {result.Message}";
        }
        catch (Exception ex)
        {
            ResultText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunHousekeepingAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Api.RunHousekeepingAsync();
            HousekeepingResult = result is null
                ? "Housekeeping completed (no summary)."
                : $"Deleted {result.FilesDeleted} file(s), {result.BytesDeleted} bytes across {result.ProfilesChecked} profile(s).";
            ResultText = HousekeepingResult;
        }
        catch (Exception ex)
        {
            HousekeepingResult = $"Housekeeping failed: {ex.Message}";
            ResultText = HousekeepingResult;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReconcileAsync()
    {
        IsBusy = true;
        try
        {
            var jobs = await Api.ReconcileRecordingsAsync();
            await RefreshAsync();
            ResultText = $"Reconcile finished: {jobs?.Count ?? 0} job(s) now tracked.";
        }
        catch (Exception ex)
        {
            ResultText = $"Reconcile failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StallCheckAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Api.CheckStalledRecordingsAsync();
            ResultText = result is null
                ? "Stall check completed (no summary)."
                : $"Stall check: {result.Stalled} stalled job(s) handled (autoRestart={result.AutoRestart}).";
        }
        catch (Exception ex)
        {
            ResultText = $"Stall check failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
