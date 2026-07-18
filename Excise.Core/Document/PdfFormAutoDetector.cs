using Excise.Core.Content;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// A field rectangle suggested by <see cref="PdfFormAutoDetector"/>. The
/// caller decides whether to materialize it as an actual field via
/// <see cref="AcroFormAuthoring"/>.
/// </summary>
public sealed record SuggestedField(
    PdfFieldType FieldType,
    PdfRectangle Rect,
    int PageNumber,
    string SuggestedName,
    string Reason);

/// <summary>
/// Heuristic detector for likely form-field locations in a PDF page that
/// doesn't yet have an AcroForm. Scans the page content stream for shapes
/// that conventionally indicate fillable areas:
///
///   • Long, thin horizontal strokes  → text field above the line
///   • Small rectangle outlines       → checkbox
///
/// This is the equivalent of Acrobat's "Prepare Form" auto-detection and
/// is intentionally pragmatic, not exhaustive — it should hit the common
/// case (Word-exported forms with underline placeholders) and miss
/// gracefully on layouts it doesn't understand. The user is expected to
/// review and adjust the suggestions before committing.
/// </summary>
public static class PdfFormAutoDetector
{
    private const double MinTextFieldLineLength = 50.0;   // pt — anything shorter is probably decoration
    private const double MaxTextFieldLineHeight = 2.0;    // pt — visually a thin line, not a box
    private const double TextFieldHeightAboveLine = 16.0; // pt — typed-in text height
    private const double MinCheckboxSize = 6.0;
    private const double MaxCheckboxSize = 24.0;
    private const double CheckboxAspectTolerance = 0.4;   // 0 = exact square; 0.4 = pretty loose

    /// <summary>
    /// Scan one page. Returns suggested fields in stream order.
    /// </summary>
    public static IReadOnlyList<SuggestedField> ScanPage(PdfPage page, int pageNumber = 1)
    {
        var ops = page.GetContentStream().Operators;
        if (ops.Count == 0) return Array.Empty<SuggestedField>();

        var suggestions = new List<SuggestedField>();
        int textCounter = 1;
        int checkCounter = 1;

        // Path state (we only need a tiny subset for the heuristic).
        var ctm = Matrix.Identity;
        var ctmStack = new Stack<Matrix>();
        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        bool isSimpleLine = false;
        var rectPath = new List<(double x, double y, double w, double h)>();
        bool hasNonLineOp = false;

        void ResetPath()
        {
            isSimpleLine = false;
            hasNonLineOp = false;
            rectPath.Clear();
        }

        foreach (var op in ops)
        {
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
                        var local = new Matrix(
                            op.GetNumber(0), op.GetNumber(1),
                            op.GetNumber(2), op.GetNumber(3),
                            op.GetNumber(4), op.GetNumber(5));
                        ctm = local.Multiply(ctm);
                    }
                    break;

                case "m":
                    if (op.Operands.Count >= 2)
                    {
                        startX = curX = op.GetNumber(0);
                        startY = curY = op.GetNumber(1);
                        isSimpleLine = true;
                        hasNonLineOp = false;
                        rectPath.Clear();
                    }
                    break;
                case "l":
                    if (op.Operands.Count >= 2)
                    {
                        // After more than one segment, this is no longer a
                        // single horizontal line — disqualify.
                        if (curX != startX || curY != startY)
                        {
                            // already had a previous l — multi-segment path
                            isSimpleLine = false;
                        }
                        curX = op.GetNumber(0);
                        curY = op.GetNumber(1);
                    }
                    break;
                case "re":
                    if (op.Operands.Count >= 4)
                    {
                        rectPath.Add((
                            op.GetNumber(0),
                            op.GetNumber(1),
                            op.GetNumber(2),
                            op.GetNumber(3)));
                    }
                    break;
                case "c":
                case "v":
                case "y":
                case "h":
                    hasNonLineOp = true;
                    break;

                case "S":
                case "s":
                    // Stroke-only paint — what underline/outline glyphs use.
                    AnalysePaintedPath(
                        suggestions, pageNumber,
                        isSimpleLine, hasNonLineOp,
                        startX, startY, curX, curY,
                        rectPath, ctm,
                        ref textCounter, ref checkCounter);
                    ResetPath();
                    break;

                case "f": case "F": case "f*":
                case "B": case "B*":
                case "b": case "b*":
                case "n":
                    // Filled or no-op paths aren't form-field placeholders;
                    // they'd be opaque and cover content.
                    ResetPath();
                    break;
            }
        }

        return suggestions;
    }

    /// <summary>Convenience: scan every page.</summary>
    public static IReadOnlyList<SuggestedField> Scan(PdfDocument doc)
    {
        var all = new List<SuggestedField>();
        for (int p = 1; p <= doc.PageCount; p++)
            all.AddRange(ScanPage(doc.GetPage(p), p));
        return all;
    }

    /// <summary>
    /// Apply <paramref name="suggestions"/> to <paramref name="document"/>,
    /// materialising each as a real form field via <see cref="AcroFormAuthoring"/>.
    /// Returns the count of fields actually created.
    /// </summary>
    public static int Apply(PdfDocument document, IEnumerable<SuggestedField> suggestions)
    {
        int count = 0;
        foreach (var s in suggestions)
        {
            try
            {
                _ = s.FieldType switch
                {
                    PdfFieldType.Text   => document.AddTextField(s.PageNumber, s.Rect, s.SuggestedName),
                    PdfFieldType.Button => document.AddCheckBox(s.PageNumber, s.Rect, s.SuggestedName),
                    _ => throw new NotSupportedException($"Auto-detect doesn't materialise {s.FieldType}."),
                };
                count++;
            }
            catch (ArgumentException)
            {
                // Duplicate names or bad rects — skip and keep going.
            }
        }
        return count;
    }

    private static void AnalysePaintedPath(
        List<SuggestedField> suggestions,
        int pageNumber,
        bool isSimpleLine, bool hasNonLineOp,
        double mX, double mY, double endX, double endY,
        List<(double x, double y, double w, double h)> rectPath,
        Matrix ctm,
        ref int textCounter, ref int checkCounter)
    {
        // Single rectangle stroke → checkbox candidate.
        if (rectPath.Count == 1 && !hasNonLineOp && !isSimpleLine)
        {
            var (x, y, w, h) = rectPath[0];
            var transformed = TransformRect(ctm, x, y, w, h);
            if (LooksLikeCheckbox(transformed))
            {
                suggestions.Add(new SuggestedField(
                    PdfFieldType.Button,
                    transformed,
                    pageNumber,
                    SuggestedName: $"Checkbox{checkCounter++}",
                    Reason: $"square outline {transformed.Width:0.#}x{transformed.Height:0.#}pt"));
            }
            return;
        }

        // Single horizontal line → text-field candidate.
        if (isSimpleLine && !hasNonLineOp && rectPath.Count == 0)
        {
            var (a, b) = TransformPoint(ctm, mX, mY);
            var (c, d) = TransformPoint(ctm, endX, endY);
            double minX = Math.Min(a, c), maxX = Math.Max(a, c);
            double minY = Math.Min(b, d), maxY = Math.Max(b, d);
            double length = maxX - minX;
            double thickness = maxY - minY;

            if (length >= MinTextFieldLineLength && thickness <= MaxTextFieldLineHeight)
            {
                // Place the editable rect just above the line.
                var rect = new PdfRectangle(
                    Left:   minX,
                    Bottom: maxY + 1,
                    Right:  maxX,
                    Top:    maxY + 1 + TextFieldHeightAboveLine);
                suggestions.Add(new SuggestedField(
                    PdfFieldType.Text,
                    rect,
                    pageNumber,
                    SuggestedName: $"Text{textCounter++}",
                    Reason: $"underline {length:0.#}pt long"));
            }
        }
    }

    private static bool LooksLikeCheckbox(PdfRectangle r)
    {
        var w = r.Width;
        var h = r.Height;
        if (w < MinCheckboxSize || h < MinCheckboxSize) return false;
        if (w > MaxCheckboxSize || h > MaxCheckboxSize) return false;
        if (w == 0 || h == 0) return false;
        var aspect = Math.Abs(w / h - 1.0);
        return aspect <= CheckboxAspectTolerance;
    }

    private static PdfRectangle TransformRect(Matrix m, double x, double y, double w, double h)
    {
        var (x0, y0) = TransformPoint(m, x, y);
        var (x1, y1) = TransformPoint(m, x + w, y + h);
        return new PdfRectangle(
            Math.Min(x0, x1), Math.Min(y0, y1),
            Math.Max(x0, x1), Math.Max(y0, y1));
    }

    private static (double X, double Y) TransformPoint(Matrix m, double x, double y)
        => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

    private readonly struct Matrix
    {
        public readonly double A, B, C, D, E, F;
        public Matrix(double a, double b, double c, double d, double e, double f)
        { A = a; B = b; C = c; D = d; E = e; F = f; }
        public static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        public Matrix Multiply(Matrix o) => new(
            A * o.A + B * o.C,
            A * o.B + B * o.D,
            C * o.A + D * o.C,
            C * o.B + D * o.D,
            E * o.A + F * o.C + o.E,
            E * o.B + F * o.D + o.F);
    }
}
