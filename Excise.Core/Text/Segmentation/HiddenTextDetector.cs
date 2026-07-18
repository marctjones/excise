using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// A piece of text that is present in the PDF content stream but
/// visually hidden by an opaque object (filled rectangle, image, etc.)
/// painted on top of it later in the stream.
/// </summary>
/// <remarks>
/// This is the "redaction by black box" failure mode — the author
/// thought they redacted the text by drawing over it, but the bytes
/// are still there and any extractor will find them. Tools that
/// receive such a PDF should be able to detect and surface the leak.
/// </remarks>
public sealed record HiddenTextRecord(
    int PageNumber,             // 1-based
    string Text,
    PdfRectangle BoundingBox,   // page-space (bottom-left origin)
    string HiddenBy);           // e.g. "black filled rectangle", "image /Im0"

/// <summary>
/// Scans a PDF for text that is structurally present but visually
/// occluded by a later-drawn opaque object in the same content stream.
/// </summary>
/// <remarks>
/// <para>Algorithm:</para>
/// <list type="number">
///   <item>Walk the page's operator list in order, tracking the current
///     transformation matrix (via <c>q</c>/<c>Q</c>/<c>cm</c>) and fill
///     color (<c>rg</c>/<c>g</c>/<c>k</c>).</item>
///   <item>For each text-showing op (<c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>),
///     resolve its glyphs via <see cref="LetterFinder"/> and record
///     bounding box + stream index.</item>
///   <item>For each opaque-fill op (<c>f</c>/<c>F</c>/<c>f*</c>/<c>B</c>/
///     <c>B*</c>/<c>b</c>/<c>b*</c>) and image invocation (<c>Do</c> on an
///     Image XObject), record bounding box + stream index.</item>
///   <item>Emit a <see cref="HiddenTextRecord"/> for every text entry
///     whose bbox is substantially covered by an obstruction appearing
///     later in the stream.</item>
/// </list>
/// <para>Deferred: Form XObject recursion, clipping-path analysis,
/// transparency-group compositing — any of these can mask text in
/// ways this v1 doesn't see.</para>
/// </remarks>
public static class HiddenTextDetector
{
    /// <summary>Scan every page of <paramref name="document"/>.</summary>
    public static IReadOnlyList<HiddenTextRecord> Scan(PdfDocument document)
    {
        var all = new List<HiddenTextRecord>();
        for (int p = 1; p <= document.PageCount; p++)
            all.AddRange(ScanPage(document.GetPage(p), p));
        return all;
    }

    /// <summary>
    /// Scan a single page. <paramref name="pageNumber"/> is the 1-based
    /// page number to record in emitted records.
    /// </summary>
    public static IReadOnlyList<HiddenTextRecord> ScanPage(PdfPage page, int pageNumber = 1)
    {
        var records = new List<HiddenTextRecord>();
        var ops = page.GetContentStream().Operators;
        var letters = page.Letters;
        if (ops.Count == 0 || letters.Count == 0) return records;

        var textEntries = new List<TextEntry>();
        var obstructions = new List<Obstruction>();

        var ctm = Matrix23.Identity;
        var ctmStack = new Stack<Matrix23>();
        var fillRgb = new Rgb(1, 1, 1); // white — default non-obstructive
        var currentPath = new List<PdfRectangle>();
        var finder = new LetterFinder();

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Name)
            {
                case "q":
                    ctmStack.Push(ctm);
                    break;
                case "Q":
                    if (ctmStack.Count > 0) ctm = ctmStack.Pop();
                    break;
                case "cm":
                    if (op.Operands.Count >= 6)
                    {
                        var local = new Matrix23(
                            op.GetNumber(0), op.GetNumber(1),
                            op.GetNumber(2), op.GetNumber(3),
                            op.GetNumber(4), op.GetNumber(5));
                        ctm = local.Multiply(ctm);
                    }
                    break;

                case "rg":
                    if (op.Operands.Count >= 3)
                        fillRgb = new Rgb(op.GetNumber(0), op.GetNumber(1), op.GetNumber(2));
                    break;
                case "g":
                    if (op.Operands.Count >= 1)
                    {
                        var v = op.GetNumber(0);
                        fillRgb = new Rgb(v, v, v);
                    }
                    break;
                case "k":
                    if (op.Operands.Count >= 4)
                    {
                        // CMYK → rough RGB for opacity/darkness screening only.
                        double c = op.GetNumber(0), m = op.GetNumber(1),
                               y = op.GetNumber(2), k = op.GetNumber(3);
                        fillRgb = new Rgb(
                            (1 - c) * (1 - k),
                            (1 - m) * (1 - k),
                            (1 - y) * (1 - k));
                    }
                    break;

                case "re":
                    if (op.Operands.Count >= 4)
                    {
                        var x = op.GetNumber(0);
                        var y = op.GetNumber(1);
                        var w = op.GetNumber(2);
                        var h = op.GetNumber(3);
                        currentPath.Add(TransformRect(ctm, x, y, w, h));
                    }
                    break;

                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                    if (IsOpaqueObstructive(fillRgb))
                    {
                        foreach (var rect in currentPath)
                            obstructions.Add(new Obstruction(i, $"{DescribeColor(fillRgb)} filled rectangle", rect));
                    }
                    currentPath.Clear();
                    break;

                case "S":
                case "s":
                case "n":
                    // Stroke-only or no-op — doesn't hide underlying text.
                    currentPath.Clear();
                    break;

                case "Tj":
                case "TJ":
                case "'":
                case "\"":
                {
                    var text = op.TextContent ?? ExtractText(op);
                    if (string.IsNullOrEmpty(text)) break;
                    var matches = finder.FindOperationLetters(text, letters);
                    if (matches.Count == 0) break;
                    textEntries.Add(new TextEntry(i, text, BoundingBoxOf(matches)));
                    break;
                }

                case "Do":
                    if (op.Operands.Count > 0)
                    {
                        var name = op.GetName(0);
                        var xobj = page.GetXObject(name);
                        if (xobj is PdfStream s && s.GetNameOrNull("Subtype") == "Image")
                        {
                            obstructions.Add(new Obstruction(i, $"image /{name}", TransformedUnitSquare(ctm)));
                        }
                    }
                    break;

                case "BI":
                    // Inline image (#354): fills the CTM-mapped unit square,
                    // same as a named image XObject — count it as an obstruction
                    // so text drawn underneath it is flagged as hidden.
                    obstructions.Add(new Obstruction(i, "inline image", TransformedUnitSquare(ctm)));
                    break;
            }
        }

        // Pairing: for each text entry, does any later obstruction cover
        // it by majority area? One hit is enough; don't report duplicates.
        foreach (var t in textEntries)
        {
            foreach (var o in obstructions)
            {
                if (o.Index <= t.Index) continue;
                if (CoversMajority(o.Bbox, t.Bbox))
                {
                    records.Add(new HiddenTextRecord(pageNumber, t.Text, t.Bbox, o.Description));
                    break;
                }
            }
        }

        return records;
    }

    /// <summary>
    /// True if <paramref name="area"/> covers at least half of
    /// <paramref name="text"/> by area. "Half" chosen over "full" so
    /// partial-overlap redactions (which still leak a visible fragment)
    /// still register as hidden for audit purposes.
    /// </summary>
    private static bool CoversMajority(PdfRectangle area, PdfRectangle text)
    {
        var a = area.Normalize();
        var t = text.Normalize();
        if (!a.IntersectsWith(t)) return false;

        double left = Math.Max(a.Left, t.Left);
        double right = Math.Min(a.Right, t.Right);
        double bottom = Math.Max(a.Bottom, t.Bottom);
        double top = Math.Min(a.Top, t.Top);
        double interArea = Math.Max(0, right - left) * Math.Max(0, top - bottom);

        double textArea = Math.Max(1e-6, (t.Right - t.Left) * (t.Top - t.Bottom));
        return interArea / textArea >= 0.5;
    }

    /// <summary>
    /// An RGB fill is "obstructive" for audit purposes when it's not
    /// effectively white. White-on-white fills are decorative; everything
    /// darker is a candidate redaction-by-overlay.
    /// </summary>
    private static bool IsOpaqueObstructive(Rgb c)
        => !(c.R >= 0.95 && c.G >= 0.95 && c.B >= 0.95);

    private static string DescribeColor(Rgb c)
    {
        if (c.R == 0 && c.G == 0 && c.B == 0) return "black";
        if (Math.Abs(c.R - c.G) < 1e-6 && Math.Abs(c.G - c.B) < 1e-6)
            return c.R == 0 ? "black" : $"gray({c.R:F2})";
        return $"rgb({c.R:F2},{c.G:F2},{c.B:F2})";
    }

    private static PdfRectangle BoundingBoxOf(List<LetterMatch> matches)
    {
        double left = double.MaxValue, bottom = double.MaxValue;
        double right = double.MinValue, top = double.MinValue;
        foreach (var m in matches)
        {
            var r = m.Letter.GlyphRectangle;
            if (r.Left < left) left = r.Left;
            if (r.Bottom < bottom) bottom = r.Bottom;
            if (r.Right > right) right = r.Right;
            if (r.Top > top) top = r.Top;
        }
        return new PdfRectangle(left, bottom, right, top);
    }

    private static string ExtractText(ContentOperator op)
    {
        if (op.Operands.Count == 0) return "";
        if ((op.Name == "Tj" || op.Name == "'" || op.Name == "\"")
            && op.Operands[^1] is PdfString s) return s.Value;
        if (op.Name == "TJ" && op.Operands[0] is PdfArray arr)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in arr)
                if (item is PdfString ps) sb.Append(ps.Value);
            return sb.ToString();
        }
        return "";
    }

    /// <summary>
    /// AABB of a rectangle in user space after being transformed by
    /// <paramref name="m"/>. Used for <c>re</c> paths drawn under the
    /// current CTM.
    /// </summary>
    private static PdfRectangle TransformRect(Matrix23 m, double x, double y, double w, double h)
    {
        var c0 = m.Transform(x, y);
        var c1 = m.Transform(x + w, y);
        var c2 = m.Transform(x, y + h);
        var c3 = m.Transform(x + w, y + h);
        double minX = Math.Min(Math.Min(c0.x, c1.x), Math.Min(c2.x, c3.x));
        double maxX = Math.Max(Math.Max(c0.x, c1.x), Math.Max(c2.x, c3.x));
        double minY = Math.Min(Math.Min(c0.y, c1.y), Math.Min(c2.y, c3.y));
        double maxY = Math.Max(Math.Max(c0.y, c1.y), Math.Max(c2.y, c3.y));
        return new PdfRectangle(minX, minY, maxX, maxY);
    }

    /// <summary>AABB of the unit square transformed by <paramref name="m"/>.</summary>
    private static PdfRectangle TransformedUnitSquare(Matrix23 m)
        => TransformRect(m, 0, 0, 1, 1);

    private readonly record struct TextEntry(int Index, string Text, PdfRectangle Bbox);
    private readonly record struct Obstruction(int Index, string Description, PdfRectangle Bbox);
    private readonly record struct Rgb(double R, double G, double B);

    /// <summary>Minimal 2×3 affine (PDF spec 8.3.3).</summary>
    private readonly struct Matrix23
    {
        public readonly double A, B, C, D, E, F;
        public Matrix23(double a, double b, double c, double d, double e, double f)
        { A = a; B = b; C = c; D = d; E = e; F = f; }
        public static Matrix23 Identity => new(1, 0, 0, 1, 0, 0);
        public (double x, double y) Transform(double x, double y)
            => (A * x + C * y + E, B * x + D * y + F);
        public Matrix23 Multiply(Matrix23 o) => new(
            A * o.A + B * o.C, A * o.B + B * o.D,
            C * o.A + D * o.C, C * o.B + D * o.D,
            E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);
    }
}
