namespace Excise.App.ViewModels;

/// <summary>
/// Which text-markup annotation a finished text selection becomes while
/// <see cref="MainWindowViewModel.IsMarkupAnnotationMode"/> is armed. Mirrors
/// <see cref="ShapeAnnotationKind"/>'s role for the shape family: arm the
/// tool first, then the ordinary gesture (here, a text-selection drag) — not
/// a separate "select first, then click a button" step.
/// </summary>
public enum MarkupAnnotationKind
{
    /// <summary>The finished selection -> Highlight.</summary>
    Highlight,

    /// <summary>The finished selection -> Underline.</summary>
    Underline,

    /// <summary>The finished selection -> StrikeOut.</summary>
    StrikeOut,

    /// <summary>The finished selection -> Squiggly.</summary>
    Squiggly,
}
