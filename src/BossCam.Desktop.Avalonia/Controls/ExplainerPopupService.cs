using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Shows a styled explainer popup over any control that carries
/// <see cref="InfoExplainer.ExplanationProperty"/> whenever the pointer hovers it
/// or it receives keyboard focus — including static menu titles. Attach once per
/// window with <c>ExplainerPopupService.Attach(window)</c>.
///
/// The popup is a lightweight <see cref="Popup"/> whose placement target is the
/// explained control, so it appears directly over/near the button or field it
/// describes. It hides on pointer leave / focus loss.
/// </summary>
public sealed class ExplainerPopupService : IDisposable
{
    private readonly Popup _popup;
    private readonly Border _body;
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly Window _window;
    private readonly Panel? _hostPanel;
    private Control? _activeTarget;

    private ExplainerPopupService(Window window, Panel? hostPanel)
    {
        _window = window;
        _hostPanel = hostPanel;

        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        _text = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#d5d5e0")),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(_title);
        stack.Children.Add(_text);

        _body = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#20203a")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4a4a78")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 3, Blur = 12, Color = Color.Parse("#00000088") }),
            Child = stack
        };

        _popup = new Popup
        {
            Child = _body,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            IsHitTestVisible = false
        };

        window.Closed += (_, _) => Dispose();

        // Pointer hover: use preview so the popup (non-hit-testable) never steals events.
        window.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerExitedEvent, OnPointerExited, RoutingStrategies.Tunnel);
        // Keyboard focus (Tab navigation / accessibility).
        window.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble);
        window.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Attach the explainer service to a window. Safe to call once per window.
    /// When <paramref name="hostPanel"/> is supplied the popup is hosted there
    /// (recommended: a full-bleed overlay panel spanning every grid row) so the
    /// popup never distorts the window's layout. Falls back to the window content.
    /// </summary>
    public static ExplainerPopupService Attach(Window window, Panel? hostPanel = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var service = new ExplainerPopupService(window, hostPanel);
        // Host the popup in the window's visual tree so it renders above the content.
        window.Opened += (_, _) => service.EnsureHosted();
        return service;
    }

    private void EnsureHosted()
    {
        if (_hostPanel is not null)
        {
            if (!_hostPanel.Children.Contains(_popup))
            {
                _hostPanel.Children.Add(_popup);
            }
            return;
        }
        if (_window.Content is Panel panel && !panel.Children.Contains(_popup))
        {
            panel.Children.Add(_popup);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var target = _window.InputHitTest(e.GetPosition(_window)) as Control;
        ShowFor(target);
    }

    private void ShowFor(Control? target)
    {
        if (target is null)
        {
            Hide();
            return;
        }

        var found = InfoExplainer.FindExplanation(target);
        if (found is null)
        {
            Hide();
            return;
        }

        var (control, explanation, title) = found.Value;
        if (ReferenceEquals(_activeTarget, control) && _popup.IsOpen)
        {
            return;
        }

        _title.Text = title ?? InferTitle(control);
        _text.Text = explanation;

        _popup.PlacementTarget = control;
        _popup.HorizontalOffset = 8;
        _popup.VerticalOffset = 6;

        // Ensure the popup is hosted in a live visual tree before opening.
        if (_popup.Parent is null)
        {
            EnsureHosted();
        }

        _popup.IsOpen = true;
        _activeTarget = control;
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) => Hide();

    private void OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is Control control)
        {
            ShowFor(control);
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e) => Hide();

    private void Hide()
    {
        _popup.IsOpen = false;
        _activeTarget = null;
    }

    private static string? InferTitle(Control control)
        => control switch
        {
            Button b => b.Content?.ToString(),
            TextBlock t => t.Text,
            MenuItem m => m.Header?.ToString(),
            TabItem t => t.Header?.ToString(),
            _ => null
        };

    public void Dispose()
    {
        // RemoveHandler(RoutedEvent, Delegate) unsubscribes regardless of the
        // routing strategy used at AddHandler time.
        _window.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _window.RemoveHandler(InputElement.PointerExitedEvent, OnPointerExited);
        _window.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
        _window.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
        _popup.IsOpen = false;
    }
}
