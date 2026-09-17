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
    {
        IReadOnlyList<ContentOperator> ops;
        try
        {
            ops = page.GetContentStream().Operators;
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
        var fill = new Rgb(1, 1, 1);   // §8.6.8: the initial colour is black, but a
                                       // producer that never sets one is not drawing
                                       // a redaction; white keeps the default inert.
        var fillSet = false;

        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "g" when op.Operands.Count >= 1:
                {
                    var v = op.GetNumber(0);
                    fill = new Rgb(v, v, v);
                    fillSet = true;
                    break;
                }
                case "rg" when op.Operands.Count >= 3:
                    fill = new Rgb(op.GetNumber(0), op.GetNumber(1), op.GetNumber(2));
                    fillSet = true;
                    break;
                case "k" when op.Operands.Count >= 4:
                    fill = FromCmyk(op.GetNumber(0), op.GetNumber(1), op.GetNumber(2), op.GetNumber(3));
                    fillSet = true;
                    break;
                case "sc":
                case "scn":
                    // §8.6.8 operands depend on the current colour space, which
                    // this pass does not track. Numeric operands are read the
                    // way the component count implies; a pattern name (the
                    // /P1 scn form) leaves the fill alone rather than guessing.
                    if (TryReadComponents(op, out var scn))
                    {
                        fill = scn;
                        fillSet = true;
                    }
                    break;
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                {
                    if (!fillSet || op.BoundingBox is not { } box) break;
                    if (Luminance(fill) > DarkLuminance) break;
                    var r = box.Normalize();
                    if (r.Width < MinSidePt || r.Height < MinSidePt) break;
                    if (r.Width * r.Height > MaxPageAreaFraction * pageArea) break;
                    found.Add((r, RedactionMarkKind.FilledBox,
                        $"{DescribeColor(fill)} filled rectangle"));
                    break;
                }
            }
        }
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
