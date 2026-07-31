using CommunityToolkit.Mvvm.ComponentModel;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Shared base for main-window sections. Gives every section access to the
/// HTTP API client and the shell (for the shared device selection), plus a
/// helper to surface status text on the main window status bar.
/// </summary>
public abstract class SectionViewModelBase : ObservableObject, ISectionViewModel
{
    protected SectionViewModelBase(IBossCamApiClient api, MainWindowViewModel shell)
    {
        Api = api;
        Shell = shell;
    }

    protected IBossCamApiClient Api { get; }
    public MainWindowViewModel Shell { get; }

    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract string Explain { get; }

    /// <summary>Hook called when the section becomes visible.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    protected void SetStatus(string text) => Shell.StatusText = text;

    /// <summary>Shared device selection — null when no camera is selected.</summary>
    protected Contracts.DeviceIdentity? SelectedDevice => Shell.SelectedDevice;

    protected static string Fmt(object? value) => value?.ToString() ?? "\u2014";
}
