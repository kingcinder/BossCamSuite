using Avalonia.Controls;
using Avalonia.Input;
using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia.Views;

/// <summary>
/// Immersive fullscreen camera window — the desktop mirror of the SPA's CameraFullscreen.
/// Interaction model (letter-for-letter with the web overlay):
///  • double-click exits fullscreen (restores the previous view);
///  • spacebar toggles audio output;
///  • a single click pulls up the banner of camera option tiles along the bottom;
///  • Escape, Backspace, or a second slow click dismisses the banner/menus.
/// Click-vs-double-click is disambiguated with a short timer (like the SPA's 240 ms
/// debounce) so a double-click is never eaten by the banner toggle.
/// The pointer handler is attached to the video STAGE only — banner/menu button clicks
/// are sibling elements and never bubble through it, so opening a menu cannot be
/// immediately undone by the debounced single-click toggle.
/// </summary>
public partial class FullscreenCameraWindow : Window
{
    private readonly object _clickLock = new();
    private IDisposable? _clickTimer;

    public FullscreenCameraWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is FullscreenCameraViewModel vm)
        {
            vm.RequestClose += Close;
            vm.RequestOpenSection += OnRequestOpenSection;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            // Focus before the (potentially slow) manifest fetch so the window is
            // keyboard-ready immediately and Space is reserved for the audio toggle.
            Stage.Focus();
            await vm.StartAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CancelPendingClick();
        if (DataContext is FullscreenCameraViewModel vm)
        {
            vm.RequestClose -= Close;
            vm.RequestOpenSection -= OnRequestOpenSection;
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.Dispose();
        }
    }

    /// <summary>
    /// A menu-sheet tile click navigates the shell section without changing
    /// BannerVisible/ActiveMenu, so it would otherwise leave focus on that Button and
    /// let a later Space re-activate it instead of toggling audio. Return focus to the
    /// stage after any section navigation.
    /// </summary>
    private void OnRequestOpenSection(string _) => Stage.Focus();

    /// <summary>
    /// Returns keyboard focus to the stage whenever the banner or menu sheet becomes
    /// visible, so the spacebar is always reserved for the audio toggle and cannot be
    /// swallowed by a focused banner tile button.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FullscreenCameraViewModel.BannerVisible)
            || e.PropertyName == nameof(FullscreenCameraViewModel.ActiveMenu))
        {
            if (DataContext is FullscreenCameraViewModel vm
                && (vm.BannerVisible || vm.ActiveMenu is not null))
            {
                Stage.Focus();
            }
        }
    }

    /// <summary>Attached to the video stage Grid only (see XAML).</summary>
    private void OnStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed is false)
        {
            return;
        }

        // Double-click → exit fullscreen (restore previous size). The second click of a
        // double-click always carries ClickCount == 2, which lets us cancel any pending
        // single-click banner toggle before it fires.
        if (e.ClickCount >= 2)
        {
            CancelPendingClick();
            Close();
            return;
        }

        if (DataContext is not FullscreenCameraViewModel vm)
        {
            return;
        }

        // Single click: debounce so a slow second click hides the banner again (the SPA
        // uses the same 240 ms window). A fast second click becomes the double-click above.
        CancelPendingClick();
        lock (_clickLock)
        {
            _clickTimer = DispatcherTimerOnce(TimeSpan.FromMilliseconds(240), () =>
            {
                lock (_clickLock)
                {
                    _clickTimer = null;
                }
                vm.ToggleBannerCommand.Execute(null);
            });
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FullscreenCameraViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                _ = vm.ToggleAudioCommand.ExecuteAsync(null);
                break;
            case Key.Escape:
            case Key.Back:
                e.Handled = true;
                vm.DismissMenusCommand.Execute(null);
                break;
        }
    }

    private void CancelPendingClick()
    {
        lock (_clickLock)
        {
            _clickTimer?.Dispose();
            _clickTimer = null;
        }
    }

    /// <summary>
    /// One-shot UI-thread timer. <see cref="global::Avalonia.Threading.DispatcherTimer"/> runs
    /// on the dispatcher, so no cross-thread Post is needed and disposal stops pending ticks
    /// (an already-posted tick is harmless — the VM simply toggles a banner state).
    /// </summary>
    private static IDisposable DispatcherTimerOnce(TimeSpan interval, Action action)
    {
        var timer = new global::Avalonia.Threading.DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerDisposable(timer);
    }

    private sealed class TimerDisposable(global::Avalonia.Threading.DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
