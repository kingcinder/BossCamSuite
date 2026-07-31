using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Desktop.Avalonia.Models;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Highlights: the on-screen highlight board — select a camera, page through
/// the board, switch the preferred stream and record the selected tile.
/// </summary>
public sealed partial class HighlightsViewModel : SectionViewModelBase
{
    public HighlightsViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Highlights";
    public override string Glyph => "\u2728";
    public override string Explain =>
        "Highlights is the operator board for watching cameras full-screen. Select " +
        "a camera to make it the highlight, page between tiles with Next/Previous, " +
        "choose the preferred stream, and Record Selected to capture the current tile.";

    [ObservableProperty]
    private HighlightBoardSnapshot? _board;

    [ObservableProperty]
    private string _streamMode = "main";

    /// <summary>Choices for the preferred-stream combo box.</summary>
    public IReadOnlyList<string> StreamModes { get; } = ["main", "sub"];

    [ObservableProperty]
    private string _resultText = "Highlight board not loaded.";

    public override async Task ActivateAsync()
    {
        if (Board is null)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            Board = await Api.GetHighlightsAsync();
            ResultText = $"Board: {Board?.Tiles?.Count ?? 0} tile(s), selected index {Board?.SelectedIndex ?? -1}.";
        }
        catch (Exception ex)
        {
            ResultText = $"Board load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SelectDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            ResultText = "Select a camera on the left to make it the highlight.";
            return;
        }

        try
        {
            Board = await Api.SelectHighlightAsync(SelectedDevice.Id);
            ResultText = $"Highlight set to {SelectedDevice.DisplayName}.";
        }
        catch (Exception ex)
        {
            ResultText = $"Select failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        try
        {
            Board = await Api.HighlightNextAsync();
        }
        catch (Exception ex)
        {
            ResultText = $"Next failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        try
        {
            Board = await Api.HighlightPrevAsync();
        }
        catch (Exception ex)
        {
            ResultText = $"Previous failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StreamAsync()
    {
        try
        {
            Board = await Api.HighlightStreamAsync(StreamMode);
            ResultText = $"Preferred stream set to {StreamMode}.";
        }
        catch (Exception ex)
        {
            ResultText = $"Stream switch failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RecordSelectedAsync()
    {
        try
        {
            var result = await Api.RecordSelectedHighlightAsync();
            ResultText = result is null ? "Record command sent." : $"Record result: {result}";
        }
        catch (Exception ex)
        {
            ResultText = $"Record failed: {ex.Message}";
        }
    }
}
