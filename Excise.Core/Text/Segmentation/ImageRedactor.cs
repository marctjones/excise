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
    {
        removedCount = 0;
        regionEditedCount = 0;
        var output = new List<ContentOperator>(operations.Count);

        var ctm = Matrix23.Identity;
        var ctmStack = new Stack<Matrix23>();

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
                        var local = new Matrix23(
                            op.GetNumber(0), op.GetNumber(1),
                            op.GetNumber(2), op.GetNumber(3),
                            op.GetNumber(4), op.GetNumber(5));
                        // Concat: new-CTM = local × old-CTM (PDF spec 8.3.4).
                        ctm = local.Multiply(ctm);
                    }
                    output.Add(op);
                    continue;
                case "Do":
                    if (ShouldRemoveImageDo(op, page, ctm, redactionArea, strategy))
                    {
                        // #1195: if the redaction area only PARTIALLY covers the
                        // image, try to destroy just the covered samples instead
                        // of dropping the whole image. Fail-secure: any decline
                        // falls through to whole-Do removal below.
                        var quad = TransformedUnitSquareAabb(ctm);
                        if (!QuadFullyInside(quad, redactionArea)
                            && ResolveImageStream(op, page) is { } image
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
                    if (OverlapsByStrategy(
                            TransformedUnitSquareAabb(ctm), redactionArea, strategy))
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

    /// <summary>True when <paramref name="quad"/> lies entirely within <paramref name="area"/>.</summary>
    private static bool QuadFullyInside(PdfRectangle quad, PdfRectangle area)
    {
        var q = quad.Normalize();
        var a = area.Normalize();
        return a.Contains(q.Left, q.Bottom) && a.Contains(q.Right, q.Top)
            && a.Contains(q.Left, q.Top) && a.Contains(q.Right, q.Bottom);
    }

    private static bool ShouldRemoveImageDo(
        ContentOperator op,
        PdfPage page,
        Matrix23 ctm,
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
        var bbox = TransformedUnitSquareAabb(ctm);
        return OverlapsByStrategy(bbox, redactionArea, strategy);
    }

    /// <summary>
    /// Axis-aligned bounding box of the unit square (0,0)-(1,1) after
    /// being transformed by <paramref name="m"/>.
    /// </summary>
    private static PdfRectangle TransformedUnitSquareAabb(Matrix23 m)
    {
        var p00 = m.Transform(0, 0);
        var p10 = m.Transform(1, 0);
        var p01 = m.Transform(0, 1);
        var p11 = m.Transform(1, 1);

        double minX = Math.Min(Math.Min(p00.x, p10.x), Math.Min(p01.x, p11.x));
        double maxX = Math.Max(Math.Max(p00.x, p10.x), Math.Max(p01.x, p11.x));
        double minY = Math.Min(Math.Min(p00.y, p10.y), Math.Min(p01.y, p11.y));
        double maxY = Math.Max(Math.Max(p00.y, p10.y), Math.Max(p01.y, p11.y));

        return new PdfRectangle(minX, minY, maxX, maxY);
    }

    private static bool OverlapsByStrategy(
        PdfRectangle bbox, PdfRectangle area, GlyphRemovalStrategy strategy)
    {
        var b = bbox.Normalize();
        var a = area.Normalize();
        if (!b.IntersectsWith(a)) return false;

        bool fullyContained =
            a.Contains(b.Left, b.Bottom) && a.Contains(b.Right, b.Top) &&
            a.Contains(b.Left, b.Top) && a.Contains(b.Right, b.Bottom);

        return strategy switch
        {
            GlyphRemovalStrategy.FullyContained => fullyContained,
            GlyphRemovalStrategy.CenterPoint => a.Contains(
                (b.Left + b.Right) * 0.5, (b.Bottom + b.Top) * 0.5),
            _ => true, // AnyOverlap
        };
    }

    /// <summary>
    /// Minimal 2×3 affine matrix (PDF spec 8.3.3). Row-major layout as
    /// stored in a <c>cm</c> operator: <c>a b c d e f</c>.
    /// </summary>
    private readonly struct Matrix23
    {
        public readonly double A, B, C, D, E, F;

        public Matrix23(double a, double b, double c, double d, double e, double f)
        { A = a; B = b; C = c; D = d; E = e; F = f; }

        public static Matrix23 Identity => new(1, 0, 0, 1, 0, 0);

        /// <summary>
        /// Transform the point (x, y) by this matrix.
        /// Per PDF spec: x' = a*x + c*y + e, y' = b*x + d*y + f.
        /// </summary>
        public (double x, double y) Transform(double x, double y)
            => (A * x + C * y + E, B * x + D * y + F);

        /// <summary>
        /// Matrix multiply: <c>this × other</c>. Used to fold a local
        /// <c>cm</c> into the existing CTM.
        /// </summary>
        public Matrix23 Multiply(Matrix23 o) => new(
            A * o.A + B * o.C,
            A * o.B + B * o.D,
            C * o.A + D * o.C,
            C * o.B + D * o.D,
            E * o.A + F * o.C + o.E,
            E * o.B + F * o.D + o.F);
    }
}
