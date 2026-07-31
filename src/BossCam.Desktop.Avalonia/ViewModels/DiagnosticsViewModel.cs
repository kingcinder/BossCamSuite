using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Diagnostics: write-audit log, endpoint transcripts and probe sessions — the
/// evidence trail behind everything the service has done to your cameras.
/// </summary>
public sealed partial class DiagnosticsViewModel : SectionViewModelBase
{
    public DiagnosticsViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Diagnostics";
    public override string Glyph => "\U0001F52C";
    public override string Explain =>
        "Diagnostics is the evidence drawer: recent write-audit entries, captured " +
        "endpoint transcripts (requests and responses) and probe sessions with " +
        "their per-stage results. Use it to see exactly what the service has done " +
        "and what the cameras answered.";

    [ObservableProperty]
    private ObservableCollection<WriteAuditEntry> _audit = [];

    [ObservableProperty]
    private ObservableCollection<EndpointTranscript> _transcripts = [];

    [ObservableProperty]
    private ObservableCollection<ProbeSession> _sessions = [];

    [ObservableProperty]
    private string _resultText = "Diagnostics not loaded.";

    public override async Task ActivateAsync()
    {
        if (Audit.Count == 0)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var deviceId = SelectedDevice?.Id;
        try
        {
            Audit = new ObservableCollection<WriteAuditEntry>(await Api.GetAuditEntriesAsync(deviceId, 50) ?? []);
        }
        catch (Exception ex)
        {
            ResultText = $"Audit failed: {ex.Message}";
        }

        try
        {
            Transcripts = new ObservableCollection<EndpointTranscript>(await Api.GetTranscriptsAsync(deviceId, 50) ?? []);
        }
        catch (Exception ex)
        {
            ResultText += $"\nTranscripts failed: {ex.Message}";
        }

        try
        {
            Sessions = new ObservableCollection<ProbeSession>(await Api.GetProbeSessionsAsync(deviceId, 50) ?? []);
        }
        catch (Exception ex)
        {
            ResultText += $"\nProbe sessions failed: {ex.Message}";
        }

        ResultText = $"Loaded {Audit.Count} audit entry(s), {Transcripts.Count} transcript(s), {Sessions.Count} session(s).";
        SetStatus(ResultText);
    }

    [RelayCommand]
    private async Task StartProbeSessionAsync()
    {
        if (SelectedDevice is null)
        {
            ResultText = "Select a camera on the left to start a probe session.";
            return;
        }

        try
        {
            var session = await Api.StartProbeSessionAsync(new ProbeSessionRequest
            {
                DeviceId = SelectedDevice.Id,
                Mode = ProbeStageMode.SafeReadOnly,
                IncludePersistenceChecks = false
            });
            ResultText = session is null
                ? "Probe session start returned no session."
                : $"Probe session {session.Id} started ({session.Status}).";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ResultText = $"Probe session failed: {ex.Message}";
        }
    }
}
