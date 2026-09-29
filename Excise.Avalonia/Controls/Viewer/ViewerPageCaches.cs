using System;
using System.Collections.Generic;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The per-page annotation and link lists hover and click hit-testing read, for
/// both views (#1842). Hit-testing runs on every pointer move, and re-parsing
/// <c>/Annots</c> per move is off the table, so each page's lists are parsed
/// once and kept until the viewer clears them. Link lists are tiny (a few
/// rects), so an unbounded per-document dictionary is fine where bitmaps were
/// not (#667).
/// </summary>
/// <remarks>
/// One owner for what used to be three caches: the single-page link cache (one
/// page), the continuous link cache (every page) and the annotation cache
/// (#1074). A page that fails to parse caches an empty list, as each of them did.
/// </remarks>
internal sealed class ViewerPageCaches
{
    private readonly Func<PdfDocument?> _document;
    private readonly Dictionary<int, IReadOnlyList<PdfLink>> _links = new();
    private readonly Dictionary<int, IReadOnlyList<PdfAnnotation>> _annotations = new();

    internal ViewerPageCaches(Func<PdfDocument?> document) => _document = document;

    /// <summary>The page's links; empty when there is no document or the page does not parse.</summary>
    internal IReadOnlyList<PdfLink> Links(int pageNumber)
    {
        if (_links.TryGetValue(pageNumber, out var cached))
            return cached;

        IReadOnlyList<PdfLink> links;
        try
        {
            links = _document()?.GetPage(pageNumber).GetLinks() ?? (IReadOnlyList<PdfLink>)Array.Empty<PdfLink>();
        }
        catch
        {
            links = Array.Empty<PdfLink>();
        }
        _links[pageNumber] = links;
        return links;
    }

    /// <summary>The page's annotations; empty when there is no document or the page does not parse.</summary>
    internal IReadOnlyList<PdfAnnotation> Annotations(int pageNumber)
    {
        if (_annotations.TryGetValue(pageNumber, out var cached))
            return cached;

        IReadOnlyList<PdfAnnotation> annots;
        try
        {
            annots = _document()?.GetPage(pageNumber).GetAnnotations()
                     ?? (IReadOnlyList<PdfAnnotation>)Array.Empty<PdfAnnotation>();
        }
        catch
        {
            annots = Array.Empty<PdfAnnotation>();
        }
        _annotations[pageNumber] = annots;
        return annots;
    }

    /// <summary>Drop every page's links.</summary>
    internal void ClearLinks() => _links.Clear();

    /// <summary>
    /// Drop every page's annotations. The cached <see cref="PdfAnnotation"/>
    /// wrappers captured their <c>/Rect</c> and <c>/Contents</c> when parsed, so an
    /// in-place edit (a sticky note moved or retyped, #1794) is invisible to
    /// hit-testing until this runs.
    /// </summary>
    internal void ClearAnnotations() => _annotations.Clear();

    /// <summary>Drop everything (a different document, or its content changed).</summary>
    internal void Clear()
    {
        ClearLinks();
        ClearAnnotations();
    }
}
