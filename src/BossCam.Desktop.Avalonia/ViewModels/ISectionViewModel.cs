namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// A navigable section of the main window. The shell binds its left-hand
/// navigation to a list of these and calls <see cref="ActivateAsync"/> each
/// time a section is selected, so data loads lazily on first visit.
/// </summary>
public interface ISectionViewModel
{
    /// <summary>Navigation title shown in the sidebar.</summary>
    string Title { get; }

    /// <summary>Sidebar glyph (emoji) shown next to the title.</summary>
    string Glyph { get; }

    /// <summary>One-line explainer shown in the sidebar tooltip/popup.</summary>
    string Explain { get; }

    /// <summary>Called when the section becomes visible; loads its data.</summary>
    Task ActivateAsync();
}
