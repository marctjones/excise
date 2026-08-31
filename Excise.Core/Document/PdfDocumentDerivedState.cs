namespace Excise.Core.Document;

/// <summary>
/// Names the document sources whose parsed or projected views may need to be
/// rebuilt after a supported mutation. This is an internal cache contract, not
/// a public dirty-state API or a second document graph.
/// </summary>
[Flags]
internal enum PdfDocumentDerivedStateScope
{
    None = 0,
    PageTree = 1 << 0,
    CatalogActionsAndNames = 1 << 1,
    StructureAndTagging = 1 << 2,
    OptionalContent = 1 << 3,
    Attachments = 1 << 4,
    Metadata = 1 << 5,
    PageLabels = 1 << 6,
}

public partial class PdfDocument
{
    /// <summary>
    /// Invalidates only projections derived from the named source scopes.
    /// Supported mutation paths call this after changing the authoritative PDF
    /// dictionaries; direct edits through the public raw dictionaries cannot be
    /// intercepted without a breaking API change.
    /// </summary>
    /// <remarks>
    /// Cache/source/scope inventory:
    /// <list type="bullet">
    /// <item><c>_pages</c>: <c>/Catalog/Pages</c>; its stable
    /// <see cref="PageCollection"/> refreshes its internal list itself after a
    /// <see cref="PdfDocumentDerivedStateScope.PageTree"/> mutation.</item>
    /// <item><c>_pagesByDict</c>: page dictionary to current page number;
    /// <see cref="PdfDocumentDerivedStateScope.PageTree"/>.</item>
    /// <item><c>_namedDestinationsCache</c>: <c>/Dests</c> and
    /// <c>/Names/Dests</c> plus current page order;
    /// <see cref="PdfDocumentDerivedStateScope.CatalogActionsAndNames"/> or
    /// <see cref="PdfDocumentDerivedStateScope.PageTree"/>.</item>
    /// <item><c>_structureTree</c>: <c>/StructTreeRoot</c> plus page-position
    /// projections; <see cref="PdfDocumentDerivedStateScope.StructureAndTagging"/>
    /// or <see cref="PdfDocumentDerivedStateScope.PageTree"/>.</item>
    /// <item><c>_isTaggedPdf</c>: <c>/MarkInfo/Marked</c>;
    /// <see cref="PdfDocumentDerivedStateScope.StructureAndTagging"/>.</item>
    /// <item><c>_ocgs</c> and <c>_ocgConfig</c>: <c>/OCProperties</c>;
    /// <see cref="PdfDocumentDerivedStateScope.OptionalContent"/>.</item>
    /// <item><c>_embeddedFiles</c>: <c>/Names/EmbeddedFiles</c> and
    /// <c>/AF</c>; <see cref="PdfDocumentDerivedStateScope.Attachments"/>.</item>
    /// <item><c>_pageLabelCache</c>: <c>/PageLabels</c>;
    /// <see cref="PdfDocumentDerivedStateScope.PageLabels"/>.</item>
    /// <item><c>_openActionCache</c>/<c>_openActionParsed</c>,
    /// <c>_additionalActionsCache</c>, and <c>_documentJavaScriptCache</c>:
    /// <c>/OpenAction</c>, <c>/AA</c>, and <c>/Names/JavaScript</c>;
    /// <see cref="PdfDocumentDerivedStateScope.CatalogActionsAndNames"/>.</item>
    /// <item>Info, language, and XMP metadata are read directly from their
    /// dictionaries and have no memoized view today. The
    /// <see cref="PdfDocumentDerivedStateScope.Metadata"/> scope keeps their
    /// supported mutation boundary explicit if a projection is added later.</item>
    /// </list>
    /// </remarks>
    internal void InvalidateDerivedState(PdfDocumentDerivedStateScope scopes)
    {
        if ((scopes & PdfDocumentDerivedStateScope.PageTree) != 0)
        {
            _pagesByDict = null;
            _namedDestinationsCache = null;
            _structureTree = null;
        }

        if ((scopes & PdfDocumentDerivedStateScope.CatalogActionsAndNames) != 0)
        {
            _namedDestinationsCache = null;
            _openActionCache = null;
            _openActionParsed = false;
            _additionalActionsCache = null;
            _documentJavaScriptCache = null;
        }

        if ((scopes & PdfDocumentDerivedStateScope.StructureAndTagging) != 0)
        {
            _structureTree = null;
            _isTaggedPdf = null;
            _pagesByDict = null;
        }

        if ((scopes & PdfDocumentDerivedStateScope.OptionalContent) != 0)
        {
            _ocgs = null;
            _ocgConfig = null;
        }

        if ((scopes & PdfDocumentDerivedStateScope.Attachments) != 0)
            _embeddedFiles = null;

        if ((scopes & PdfDocumentDerivedStateScope.PageLabels) != 0)
            _pageLabelCache = null;
    }
}
