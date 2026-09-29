using System;
using System.Collections.Generic;
using Excise.Core.Document;
using Excise.Core.Text;

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

    PdfViewMode ViewMode { get; }

    /// <summary>The display's device-pixel ratio, or the test override (#682).</summary>
    double RenderScaling { get; }

    ReadingOrderStrategy ReadingOrderStrategy { get; }

    WhitespaceMode WhitespaceMode { get; }

    /// <summary>Any page's form fields, for the continuous slots (#1807).</summary>
    Func<int, IReadOnlyList<PdfField>>? PageFormFieldsProvider { get; }

    // The five annotation-render flags. Read on the UI thread at the point a render
    // captures them (the "read the styled property HERE" rule of the band render).
    bool ShowAnnotations { get; }
    bool ShowCommentAnnotations { get; }
    bool ShowFieldAndLinkAnnotations { get; }
    bool RevealHiddenAnnotations { get; }
    bool HighlightFormFields { get; }
}
