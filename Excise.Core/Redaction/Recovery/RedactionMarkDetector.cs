using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587 — enumerates the redactions a document ADMITS to, independently of
/// whether any channel recovered what is under them.
///
/// <para><b>Why this is a separate pass and not a by-product of recovery.</b>
/// Every channel reports what it FOUND, so a report built only from channel
/// output can say "8 findings" over a document with 40 redactions and read as
/// success. Marks are the denominator. A mark with no finding is the most
/// important row in the report — it is the part of the redaction that held —
/// and it exists only if marks are counted on their own.</para>
///
/// <para><b>What counts as a mark.</b> Three things the page states outright:
/// an opaque dark filled rectangle in the content stream, a <c>/Redact</c>
/// annotation (§12.5.6.23, applied or — far more often — not), and a shape or
/// markup annotation with an opaque dark interior. A fourth kind,
/// <see cref="RedactionMarkKind.EmptiedRegion"/>, is INFERRED from a channel
/// (the width residue) rather than painted, so it is synthesised by
/// <see cref="RecoveryReportBuilder"/>, not here — this class only reads what
/// is on the page.</para>
///
/// <para><b>Gating.</b> A filled rectangle is also a table rule, a cell
/// background, a page border and a header band, so darkness alone would bury
/// the report in furniture. A fill qualifies when it is dark, larger than a
/// glyph and smaller than a substantial share of the page. That trades recall
/// for signal deliberately: a mark the geometry filter drops is not silently
/// lost, because any text under it still surfaces as an UNLINKED finding —
/// the report loses the attribution, never the leak.</para>
/// </summary>
public static class RedactionMarkDetector
{
    /// <summary>Luminance at or below this counts as a redaction-dark fill.</summary>
    private const double DarkLuminance = 0.45;

    /// <summary>A fill covering more of the page than this is a background, not a mark.</summary>
    private const double MaxPageAreaFraction = 0.4;

    /// <summary>Smaller than this in either dimension is a rule or a glyph-scale artefact.</summary>
    private const double MinSidePt = 2.0;

    /// <summary>Form XObjects nest, and a self-referencing one is a real corpus shape.</summary>
    private const int MaxFormDepth = 8;

    /// <summary>ContentTransform declares no identity; this is it.</summary>
    private static readonly ContentTransform Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Scan every page.</summary>
    public static IReadOnlyList<RedactionMark> Detect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var marks = new List<RedactionMark>();
        for (var p = 1; p <= document.PageCount; p++)
            marks.AddRange(DetectPage(document.GetPage(p), p));
        return marks;
    }

    /// <summary>Scan one page. Ids are "p{page}m{n}", stable for one report.</summary>
    public static IReadOnlyList<RedactionMark> DetectPage(PdfPage page, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(page);
        var found = new List<(PdfRectangle Rect, RedactionMarkKind Kind, string Description)>();
        CollectContentFills(page, found);
        CollectAnnotations(page, found);

        var marks = new List<RedactionMark>(found.Count);
        var index = 0;
        foreach (var (rect, kind, description) in found)
        {
            // Two producers drawing the same box (a /Redact annotation whose
            // appearance stream also paints the fill) is one redaction, not two.
            if (marks.Any(m => m.Kind == kind && NearlySame(m.Rect, rect))) continue;
            marks.Add(new RedactionMark(
                $"p{pageNumber}m{++index}", pageNumber, rect.Normalize(), kind, description));
        }
        return marks;
    }

    private static void CollectContentFills(
        PdfPage page, List<(PdfRectangle, RedactionMarkKind, string)> found)
        => CollectFills(page, null, Identity, found, depth: 0);

    /// <summary>
    /// Dark fills in a content stream, in PAGE space. <paramref name="outer"/>
    /// is the transform mapping this stream's coordinates onto the page —
    /// identity for the page's own content, the composed Do CTM × /Matrix for a
    /// Form XObject.
    /// </summary>
    private static void CollectFills(
        PdfPage page,
        PdfStream? form,
        ContentTransform outer,
        List<(PdfRectangle, RedactionMarkKind, string)> found,
        int depth)
    {
        IReadOnlyList<ContentOperator> ops;
        try
        {
            ops = form == null
                ? page.GetContentStream().Operators
                : new ContentStreamParser(form.DecodedData, page).Parse().Operators;
        }
        catch
        {
            // A page whose content will not parse has no readable marks. It is
            // not mark-free, and saying so is the honest outcome: the caller
            // sees zero marks on a page the channels may still produce
            // unlinked findings for.
            return;
        }

        var pageArea = Math.Max(1e-6, page.CropBox.Width * page.CropBox.Height);
        // §8.6.8: the initial fill colour IS black, and a producer that draws a
        // redaction bar without setting one is relying on exactly that.
        //
        // ⚠️ This used to start WHITE, with `fillSet` skipping any fill drawn
        // before a colour operator, on the reasoning that "a producer that never
        // sets one is not drawing a redaction". That was assumed, not measured,
        // and it is false on the most famous failed redaction there is: every
        // black bar in the Manafort breach-response filing (D.D.C. 1:17-cr-00201
        // #471, 2019-01-08) is `x y w -h re f` with no colour operator in scope,
        // and excise reported ZERO marks on a document whose bars are plainly
        // visible and whose text pdftotext reads straight out (#1617).
        //
        // What keeps the furniture out is SIZE, not the colour operator: the
        // same file's text underlines are 0.48-1.2pt tall and MinSidePt already
        // rejects them, while the bars are 13.8pt.
        // #1624: §8.4.2 q/Q save and restore the fill colour. This used to be a
        // bare local, so a colour set inside a block leaked past its Q.
        var fillState = new FillColourState(0, 0, 0);

        foreach (var op in ops)
        {
            // q/Q and every colour operator, in one place (#1624).
            if (fillState.Apply(op)) continue;

            switch (op.Name)
            {
                case "Do":
                {
                    // #1606: a covering box drawn INSIDE a Form XObject. The
                    // page content stream holds only the Do, so without
                    // recursing the box is not a mark and the text under it
                    // reads as a redaction that held. Text inside a form is
                    // already in page.Letters (TextExtractor recurses), so
                    // finding the MARK is all that is missing.
                    if (op.Operands.Count == 0) break;
                    var xname = op.GetName(0);
                    // §8.10.1: resolve against the CURRENT form's /Resources
                    // first, then the page's. `form` is null at page level
                    // (#1666).
                    if (page.GetXObject(xname, form) is not PdfStream nested) break;
                    if (nested.GetNameOrNull("Subtype") != "Form") break;
                    if (op.GraphicsTransform is not { } ctm) break;
                    CollectFormFills(page, nested, ctm, pageArea, found, depth);
                    break;
                }

                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                {
                    if (op.BoundingBox is not { } box) break;
                    var fill = new Rgb(fillState.Current.R, fillState.Current.G, fillState.Current.B);
                    if (Luminance(fill) > DarkLuminance) break;
                    var r = Transform(box, outer).Normalize();
                    if (r.Width < MinSidePt || r.Height < MinSidePt) break;
                    if (r.Width * r.Height > MaxPageAreaFraction * pageArea) break;
                    found.Add((r,
                        depth == 0 ? RedactionMarkKind.FilledBox : RedactionMarkKind.FormXObjectBox,
                        depth == 0
                            ? $"{DescribeColor(fill)} filled rectangle"
                            : $"{DescribeColor(fill)} filled rectangle inside a Form XObject"));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// #1606 — recurse into a Form XObject, composing its <c>/Matrix</c>
    /// (§8.10.1) with the CTM in force at the <c>Do</c>. Bounded: forms nest,
    /// and a self-referencing form is a real corpus shape.
    /// </summary>
    private static void CollectFormFills(
        PdfPage page,
        PdfStream form,
        ContentTransform ctm,
        double pageArea,
        List<(PdfRectangle, RedactionMarkKind, string)> found,
        int depth)
    {
        if (depth >= MaxFormDepth) return;

        var matrix = form.GetOptional("Matrix") is PdfArray m && m.Count == 6
            ? new ContentTransform(
                m.GetNumber(0), m.GetNumber(1), m.GetNumber(2),
                m.GetNumber(3), m.GetNumber(4), m.GetNumber(5))
            : Identity;

        try { CollectFills(page, form, Compose(matrix, ctm), found, depth + 1); }
        catch { /* a form whose content will not parse contributes no marks */ }
    }

    /// <summary>Matrix product: <paramref name="inner"/> then <paramref name="outer"/>.</summary>
    private static ContentTransform Compose(ContentTransform inner, ContentTransform outer) => new(
        inner.A * outer.A + inner.B * outer.C,
        inner.A * outer.B + inner.B * outer.D,
        inner.C * outer.A + inner.D * outer.C,
        inner.C * outer.B + inner.D * outer.D,
        inner.E * outer.A + inner.F * outer.C + outer.E,
        inner.E * outer.B + inner.F * outer.D + outer.F);

    /// <summary>Axis-aligned bounds of a rectangle mapped through a transform.</summary>
    private static PdfRectangle Transform(PdfRectangle rect, ContentTransform m)
    {
        if (m.Equals(Identity)) return rect;
        var r = rect.Normalize();
        var corners = new[]
        {
            m.TransformPoint(r.Left, r.Bottom), m.TransformPoint(r.Right, r.Bottom),
            m.TransformPoint(r.Left, r.Top), m.TransformPoint(r.Right, r.Top),
        };
        return new PdfRectangle(
            corners.Min(c => c.X), corners.Min(c => c.Y),
            corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    private static void CollectAnnotations(
        PdfPage page, List<(PdfRectangle, RedactionMarkKind, string)> found)
    {
        IReadOnlyList<PdfAnnotation> annots;
        try { annots = page.GetAnnotations(); }
        catch { return; }

        foreach (var annot in annots)
        {
            if (annot.Subtype == PdfAnnotationSubtype.Redact)
            {
                // §12.5.6.23: /QuadPoints are the regions to redact; /Rect is
                // their bounding box. Where quads exist they are the marks —
                // one annotation over three lines is three redactions, and
                // collapsing them to the bounding box would invent a mark over
                // the text between the lines.
                var quads = annot.QuadPoints;
                if (quads is { Count: > 0 })
                {
                    foreach (var quad in quads)
                        found.Add((quad, RedactionMarkKind.RedactAnnotation,
                            "/Redact annotation quad"));
                }
                else
                {
                    found.Add((annot.Rect, RedactionMarkKind.RedactAnnotation,
                        "/Redact annotation"));
                }
                continue;
            }

            // A shape or markup annotation filled dark enough to hide text is a
            // redaction drawn the lazy way — the page content is untouched and
            // the "redaction" is a viewer-side overlay anyone can delete.
            if (annot.Subtype is not (PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle
                or PdfAnnotationSubtype.Polygon or PdfAnnotationSubtype.Highlight))
                continue;

            var colour = annot.InteriorColor ?? annot.Color;
            if (colour is not { } c) continue;
            var rgb = new Rgb(c.R, c.G, c.B);
            if (Luminance(rgb) > DarkLuminance) continue;
            var rect = annot.Rect.Normalize();
            if (rect.Width < MinSidePt || rect.Height < MinSidePt) continue;
            found.Add((rect, RedactionMarkKind.ShapeAnnotation,
                $"/{annot.Subtype} annotation with {DescribeColor(rgb)} interior"));
        }
    }

    private static bool TryReadComponents(ContentOperator op, out Rgb rgb)
    {
        rgb = default;
        var numbers = new List<double>();
        foreach (var operand in op.Operands)
        {
            switch (operand)
            {
                case PdfInteger i: numbers.Add(i.Value); break;
                case PdfReal r: numbers.Add(r.Value); break;
                default: return false;   // a pattern name: not a device colour
            }
        }
        switch (numbers.Count)
        {
            case 1: rgb = new Rgb(numbers[0], numbers[0], numbers[0]); return true;
            case 3: rgb = new Rgb(numbers[0], numbers[1], numbers[2]); return true;
            case 4: rgb = FromCmyk(numbers[0], numbers[1], numbers[2], numbers[3]); return true;
            default: return false;
        }
    }

    private static Rgb FromCmyk(double c, double m, double y, double k)
        => new((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));

    private static double Luminance(Rgb c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static string DescribeColor(Rgb c)
    {
        if (c.R < 0.1 && c.G < 0.1 && c.B < 0.1) return "black";
        if (Math.Abs(c.R - c.G) < 0.05 && Math.Abs(c.G - c.B) < 0.05) return "grey";
        return string.Create(CultureInfo.InvariantCulture,
            $"rgb({c.R:F2},{c.G:F2},{c.B:F2})");
    }

    /// <summary>Same mark drawn twice: within a point on every edge.</summary>
    private static bool NearlySame(PdfRectangle a, PdfRectangle b)
    {
        var x = a.Normalize();
        var y = b.Normalize();
        return Math.Abs(x.Left - y.Left) < 1 && Math.Abs(x.Right - y.Right) < 1
            && Math.Abs(x.Bottom - y.Bottom) < 1 && Math.Abs(x.Top - y.Top) < 1;
    }

    private readonly record struct Rgb(double R, double G, double B);
}
