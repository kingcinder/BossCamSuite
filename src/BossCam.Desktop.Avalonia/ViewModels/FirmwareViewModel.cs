using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Firmware &amp; contracts: firmware artifacts and capability profiles, the
/// endpoint contract catalog with fixtures, and protocol manifests. This is the
/// lab/advanced surface for extending what the suite knows about cameras.
/// </summary>
public sealed partial class FirmwareViewModel : SectionViewModelBase
{
    public FirmwareViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Firmware";
    public override string Glyph => "\U0001F4E6";
    public override string Explain =>
        "Firmware & Contracts is the lab surface: registered firmware artifacts, " +
        "capability profiles per firmware fingerprint, the typed endpoint contract " +
        "catalog with its fixtures, and the protocol manifests that drive discovery. " +
        "Typical operators won't need this tab.";

    [ObservableProperty]
    private ObservableCollection<FirmwareArtifact> _artifacts = [];

    [ObservableProperty]
    private ObservableCollection<FirmwareCapabilityProfile> _capabilities = [];

    [ObservableProperty]
    private ObservableCollection<EndpointContract> _contracts = [];

    [ObservableProperty]
    private ObservableCollection<ProtocolManifest> _protocols = [];

    [ObservableProperty]
    private string _resultText = "Firmware data not loaded.";

    [ObservableProperty]
    private string _firmwarePath = string.Empty;

    public override async Task ActivateAsync()
    {
        if (Artifacts.Count == 0)
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
            Artifacts = new ObservableCollection<FirmwareArtifact>(await Api.GetFirmwareArtifactsAsync() ?? []);
        }
        catch (Exception ex)
        {
            ResultText = $"Firmware artifacts failed: {ex.Message}";
        }

        try
        {
            Capabilities = new ObservableCollection<FirmwareCapabilityProfile>(await Api.GetFirmwareCapabilitiesAsync() ?? []);
        }
        catch (Exception ex)
        {
            ResultText += $"\nCapabilities failed: {ex.Message}";
        }

        try
        {
            Contracts = new ObservableCollection<EndpointContract>(await Api.GetContractEndpointsAsync(deviceId) ?? []);
        }
        catch (Exception ex)
        {
            ResultText += $"\nContracts failed: {ex.Message}";
        }

        try
        {
            Protocols = new ObservableCollection<ProtocolManifest>(await Api.GetProtocolsAsync() ?? []);
        }
        catch (Exception ex)
        {
            ResultText += $"\nProtocols failed: {ex.Message}";
        }

        ResultText = $"Loaded {Artifacts.Count} artifact(s), {Capabilities.Count} profile(s), {Contracts.Count} contract(s), {Protocols.Count} protocol(s).";
        SetStatus(ResultText);
    }

    [RelayCommand]
    private async Task RegisterFirmwareAsync()
    {
        if (string.IsNullOrWhiteSpace(FirmwarePath))
        {
            ResultText = "Enter a path to a firmware file first.";
            return;
        }

        try
        {
            var artifact = await Api.RegisterFirmwareAsync(FirmwarePath.Trim());
            ResultText = artifact is null
                ? "Firmware registered (no artifact returned)."
                : $"Registered {artifact.FileName} ({artifact.SizeBytes} bytes).";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ResultText = $"Firmware register failed: {ex.Message}";
        }
    }
}
