using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Removes image invocations whose page-space bounding box overlaps a
/// redaction rectangle — both named image XObjects (<c>Do</c>) and
/// inline images (<c>BI…ID…EI</c>). Complements <see cref="GlyphRemover"/>,
/// which handles text. Together they cover the commonly-leakable content
/// types on a PDF page.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm: walk the content stream while tracking the current
/// transformation matrix (CTM) through <c>q</c>/<c>Q</c>/<c>cm</c>. When
/// a <c>Do</c> op is hit, resolve the named XObject in the page's
/// <c>/Resources /XObject</c> dictionary; if the subtype is
/// <c>/Image</c>, use the CTM-transformed unit square as the image's
/// page-space quad, AABB that, and test the chosen overlap strategy
/// against the redaction area. If it hits, drop the <c>Do</c> op. A
/// <c>BI</c> op is treated the same way — it always fills the unit square
/// mapped by the CTM — and dropping it removes the embedded pixel bytes
/// the parser captured on <see cref="ContentOperator.InlineImageData"/>
/// (#354).
/// </para>
/// <para>
/// Form XObjects (non-image Do targets) pass through unchanged; redacting
/// the contents of a form would require recursing into its own content
/// stream, which is a larger change (tracked in #355).
/// </para>
/// </remarks>
internal static class ImageRedactor
{
    /// <summary>
    /// Return a new operator list with image <c>Do</c>s that overlap
    /// <paramref name="redactionArea"/> removed.
    /// </summary>
    /// <param name="operations">Input operator list; not mutated.</param>
    /// <param name="page">Page whose <c>/Resources /XObject</c> names the
    /// <c>Do</c> target is looked up in.</param>
    /// <param name="redactionArea">Redaction rectangle in page-space
    /// (content-stream) coordinates.</param>
    /// <param name="strategy">Which overlap rule decides removal.</param>
    /// <param name="removedCount">Set to the number of image ops dropped.</param>
    public static List<ContentOperator> ProcessOperations(
        IReadOnlyList<ContentOperator> operations,
        PdfPage page,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy,
        out int removedCount)
        => ProcessOperations(operations, page, redactionArea, strategy, out removedCount, out _);

    /// <summary>
    /// As <see cref="ProcessOperations(IReadOnlyList{ContentOperator}, PdfPage, PdfRectangle, GlyphRemovalStrategy, out int)"/>,
    /// additionally reporting how many images were REGION-redacted in place
    /// (#1195) rather than dropped wholesale. Asserting this fired is what keeps
    /// the fail-secure fallback from silently masking "the region path never ran".
    /// </summary>
    public static List<ContentOperator> ProcessOperations(
        IReadOnlyList<ContentOperator> operations,
        PdfPage page,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy,
        out int removedCount,
        out int regionEditedCount)
        => ProcessOperations(operations, page, redactionArea, strategy,
            out removedCount, out regionEditedCount, touchedImages: null);

    /// <summary>
    /// As above, additionally adding every image stream this pass region-edited
    /// or dropped to <paramref name="touchedImages"/> (#1493). Both replace the
    /// image on THIS page only; any other page (or another <c>Do</c> on this
    /// one) that still references the same stream keeps the original pixels,
    /// and <c>RedactText</c> reports those references instead of hiding them.
    /// </summary>
    public static List<ContentOperator> ProcessOperations(
        IReadOnlyList<ContentOperator> operations,
        PdfPage page,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy,
        out int removedCount,
        out int regionEditedCount,
        List<PdfStream>? touchedImages)
    {
        removedCount = 0;
        regionEditedCount = 0;
        var output = new List<ContentOperator>(operations.Count);

        var ctm = ContentTransform.Identity;
        var ctmStack = new Stack<ContentTransform>();

        foreach (var op in operations)
        {
            switch (op.Name)
            {
                case "q":
                    ctmStack.Push(ctm);
                    output.Add(op);
                    continue;
                case "Q":
                    if (ctmStack.Count > 0) ctm = ctmStack.Pop();
                    output.Add(op);
                    continue;
                case "cm":
                    if (op.Operands.Count >= 6)
                    {
                        // Concat: new-CTM = local × old-CTM (PDF spec 8.3.4).
                        ctm = ContentTransform.FromOperands(op).Multiply(ctm);
                    }
                    output.Add(op);
                    continue;
                case "Do":
                    if (ShouldRemoveImageDo(op, page, ctm, redactionArea, strategy))
                    {
                        // #1493: region-edited or dropped, the image is replaced
                        // on this page only. Record it so the pages still drawing
                        // the original can be reported.
                        var image = ResolveImageStream(op, page);
                        if (image != null)
                            touchedImages?.Add(image);

                        // #1195: if the redaction area only PARTIALLY covers the
                        // image, try to destroy just the covered samples instead
                        // of dropping the whole image. Fail-secure: any decline
                        // falls through to whole-Do removal below.
                        if (!redactionArea.Contains(ctm.UnitSquareBounds())
                            && image != null
                            && ImageRegionRedactor.TryRegionRedact(
                                   page, image, ctm.A, ctm.B, ctm.C, ctm.D, ctm.E, ctm.F,
                                   redactionArea, out var newName))
                        {
                            output.Add(new ContentOperator(
                                "Do", new PdfObject[] { new PdfName(newName) }));
                            regionEditedCount++;
                            continue;
                        }

                        removedCount++;
                        continue; // drop it
                    }
                    output.Add(op);
                    continue;
                case "BI":
                    // Inline image (#354): it fills the unit square mapped by
                    // the current CTM, exactly like a named image XObject. Drop
                    // the whole BI…ID…EI operator (and its embedded bytes) when
                    // that quad overlaps the redaction area.
                    if (strategy.Selects(ctm.UnitSquareBounds(), redactionArea))
                    {
                        removedCount++;
                        continue; // drop it — embedded pixel data goes with it
                    }
                    output.Add(op);
                    continue;
                default:
                    output.Add(op);
                    continue;
            }
        }

        return output;
    }

    public static void PruneUnusedImageXObjects(PdfPage page, IReadOnlyList<ContentOperator> survivingPageOperations)
    {
        var resources = page.Resources;
        // #1050: RESOLVE. /Resources /XObject is routinely an indirect
        // reference, and the non-resolving read returned null — so this method
        // returned early and pruned NOTHING. The redacted image XObject then
        // survived in the saved file, recoverable with `mutool extract`. Same
        // shape as #1040's second leak, on the raster path instead of the text
        // one.
        var xobjects = resources != null
            ? resources.ResolveDictionary(page.Document, "XObject")
            : null;
        if (xobjects == null)
            return;

        var survivingNames = new HashSet<string>(
            survivingPageOperations
                .Where(op => op.Name == "Do" && op.Operands.Count > 0)
                .Select(op => op.GetName(0))
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!),
            StringComparer.Ordinal);

        foreach (var key in xobjects.Keys.Select(k => k.Value).ToArray())
        {
            if (survivingNames.Contains(key))
                continue;

            var xobject = xobjects.GetOptional(key);
            if (xobject == null ||
                page.Document.Resolve(xobject) is not PdfStream stream ||
                !string.Equals(stream.GetNameOrNull("Subtype"), "Image", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsUsedByOtherPageSharingXObjectDictionary(page, xobjects, key))
                continue;

            xobjects.Remove(key);
        }
    }

    private static bool IsUsedByOtherPageSharingXObjectDictionary(
        PdfPage redactedPage,
        PdfDictionary xobjects,
        string name)
    {
        for (var pageNumber = 1; pageNumber <= redactedPage.Document.PageCount; pageNumber++)
        {
            if (pageNumber == redactedPage.PageNumber)
                continue;

            var page = redactedPage.Document.GetPage(pageNumber);
            // Resolved on both sides, or two pages sharing one /XObject dict
            // through a reference would compare unequal and the shared-use
            // check would wrongly conclude the object is unused.
            var otherXObjects = page.Resources?.ResolveDictionary(page.Document, "XObject");
            if (!ReferenceEquals(otherXObjects, xobjects))
                continue;

            if (ContentUsesXObjectName(page.GetContentStream().Operators, name))
                return true;
        }

        return false;
    }

    private static bool ContentUsesXObjectName(IReadOnlyList<ContentOperator> operations, string name) =>
        operations.Any(op =>
            op.Name == "Do" &&
            op.Operands.Count > 0 &&
            string.Equals(op.GetName(0), name, StringComparison.Ordinal));

    /// <summary>The image XObject a <c>Do</c> targets, or null if it is not an image.</summary>
    private static PdfStream? ResolveImageStream(ContentOperator op, PdfPage page)
    {
        if (op.Operands.Count == 0) return null;
        var name = op.GetName(0);
        if (string.IsNullOrEmpty(name)) return null;
        if (page.GetXObject(name) is not PdfStream stream) return null;
        return string.Equals(stream.GetNameOrNull("Subtype"), "Image", StringComparison.Ordinal)
            ? stream : null;
    }

    private static bool ShouldRemoveImageDo(
        ContentOperator op,
        PdfPage page,
        ContentTransform ctm,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy)
    {
        if (op.Operands.Count == 0) return false;
        string? name = op.GetName(0);
        if (string.IsNullOrEmpty(name)) return false;

        var xobject = page.GetXObject(name);
        if (xobject is not PdfStream stream) return false;

        var subtype = stream.GetNameOrNull("Subtype");
        if (!string.Equals(subtype, "Image", StringComparison.Ordinal))
            return false;

        // The image occupies the unit square in its own object space. The
        // CTM at the Do site maps that square into page space.
        return strategy.Selects(ctm.UnitSquareBounds(), redactionArea);
    }
}
