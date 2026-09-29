using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The viewer state a view reads but does not own (#1842,
/// <c>docs/architecture/pdf-viewer-control-architecture.md</c> §3.1 principle 3).
/// The viewer implements it with its own styled properties; a view never declares
/// a second <c>StyledProperty</c> for the same value, so the once-per-process class
/// handlers stay single. Members are added as the views that read them move.
/// </summary>
internal interface IViewerState
{
    PdfDocument? Document { get; }

    /// <summary>The 1-based anchor page (in continuous view, the page at the viewport top, #1650).</summary>
    int CurrentPage { get; }

    double ZoomLevel { get; }

    InteractionMode InteractionMode { get; }
}
