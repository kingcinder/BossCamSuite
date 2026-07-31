using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Attached-property bridge that gives ANY control an explainer popup.
///
/// <code>
///   &lt;Button Info.Explanation="Starts a new recording session for the selected camera."&gt;Record&lt;/Button&gt;
/// </code>
///
/// The <see cref="ExplainerPopupService"/> attached to a window watches pointer
/// hover and focus, then shows the explanation over/near the control. Static menu
/// titles use the same property so nothing is left unexplained.
/// </summary>
/// <remarks>
/// This class is deliberately not <c>static</c>: Avalonia's typed
/// <c>RegisterAttached&lt;TOwner, …&gt;</c> requires a non-static owner type
/// (static types cannot be used as generic type arguments). The constructor is
/// private because the class is only used as an attached-property owner.
/// </remarks>
public sealed class InfoExplainer
{
    private InfoExplainer() { }

    /// <summary>Long-form explanation shown in the explainer popup.</summary>
    public static readonly AttachedProperty<string?> ExplanationProperty =
        AvaloniaProperty.RegisterAttached<InfoExplainer, Control, string?>("Explanation", null);

    /// <summary>Short bold title for the popup (defaults to the control content/tag).</summary>
    public static readonly AttachedProperty<string?> TitleProperty =
        AvaloniaProperty.RegisterAttached<InfoExplainer, Control, string?>("Title", null);

    /// <summary>When true the popup is omitted for this control (e.g. decorative borders).</summary>
    public static readonly AttachedProperty<bool> NoExplainerProperty =
        AvaloniaProperty.RegisterAttached<InfoExplainer, Control, bool>("NoExplainer", false);

    public static void SetExplanation(AvaloniaObject element, string? value) => element.SetValue(ExplanationProperty, value);
    public static string? GetExplanation(AvaloniaObject element) => element.GetValue(ExplanationProperty);

    public static void SetTitle(AvaloniaObject element, string? value) => element.SetValue(TitleProperty, value);
    public static string? GetTitle(AvaloniaObject element) => element.GetValue(TitleProperty);

    public static void SetNoExplainer(AvaloniaObject element, bool value) => element.SetValue(NoExplainerProperty, value);
    public static bool GetNoExplainer(AvaloniaObject element) => element.GetValue(NoExplainerProperty);

    /// <summary>
    /// Finds the nearest control in the ancestor chain that carries an explanation.
    /// Returns null when nothing (up to and including the root) is explained.
    /// </summary>
    public static (Control Control, string Explanation, string? Title)? FindExplanation(Control? start)
    {
        for (var current = start; current is not null; current = current.GetVisualParent() as Control)
        {
            if (GetNoExplainer(current))
            {
                continue;
            }

            var text = GetExplanation(current);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return (current, text, GetTitle(current));
            }
        }

        return null;
    }
}
