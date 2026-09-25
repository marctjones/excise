using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Content;

/// <summary>
/// Represents a single PDF content stream operator with its operands.
/// ISO 32000-2:2020 Section 8.2.
/// </summary>
/// <remarks>
/// Content streams are sequences of operators, each preceded by its operands.
/// For example: "100 200 m" is a MoveTo operator with operands [100, 200].
/// </remarks>
public class ContentOperator
{
    /// <summary>
    /// The operator name (e.g., "Tj", "cm", "re", "m", "l").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The operands preceding this operator.
    /// </summary>
    public IReadOnlyList<PdfObject> Operands { get; }

    /// <summary>
    /// The category of this operator.
    /// </summary>
    public OperatorCategory Category { get; }

    /// <summary>
    /// The bounding box of this operator in page coordinates (if calculable).
    /// Null for operators that don't have spatial extent (e.g., state changes).
    /// </summary>
    public PdfRectangle? BoundingBox { get; internal set; }

    /// <summary>
    /// For text operators, the extracted text content.
    /// </summary>
    public string? TextContent { get; internal set; }

    /// <summary>
    /// For text-showing operators (Tj/TJ/'/") parsed with operator metadata:
    /// the total pen displacement this operator produced along the writing
    /// direction, in TJ-adjustment units (thousandths of the active font
    /// size, §9.4.3; horizontal scaling excluded because a TJ number is
    /// itself scaled by Th at execution, so the factor cancels). Positive =
    /// forward advance. Under the same ambient text state, <c>[-value] TJ</c>
    /// replays the advance exactly — which is how a redaction that removes
    /// this operator keeps later runs in the same BT block from shifting
    /// (issue #758). Null when unknown: metadata pass disabled, operator
    /// built synthetically, or a degenerate zero font size makes the
    /// spacing terms inexpressible as a TJ number.
    /// </summary>
    internal double? TextAdvanceThousandths { get; set; }

    /// <summary>
    /// Graphics CTM active when this operator executed (§8.3.4). Populated
    /// for every operator when the stream was parsed with operator metadata
    /// on (the default), not only text-showing operators -- a caller wanting
    /// page-space coordinates for a path-construction operator (<c>m/l/c/v/
    /// y/h/re</c>) transforms its raw <see cref="Operands"/> through this,
    /// the same way redaction cancels it to recover page-space letter
    /// geometry from a text-showing operator. Null when operator metadata
    /// wasn't computed for this parse (<c>ComputeOperatorMetadata = false</c>,
    /// e.g. the renderer's own tokenizer-only walk, #598).
    /// </summary>
    public ContentTransform? GraphicsTransform { get; internal set; }

    /// <summary>Text matrix active when this text-showing operator began.</summary>
    internal ContentTransform? TextTransform { get; set; }

    /// <summary>
    /// Text state active when this text-showing operator began, after the
    /// implicit side effects of <c>'</c>/<c>"</c>: what redaction rebuilds the
    /// operator's surviving glyphs under (#1830). Null when operator metadata
    /// was not computed or the operator was built synthetically.
    /// </summary>
    internal ContentStreamWalker.TextStateSnapshot? TextState { get; set; }

    /// <summary>
    /// For inline-image operators (<c>BI</c>), the raw binary image data
    /// that appeared between <c>ID</c> and <c>EI</c> in the source content
    /// stream. The single <see cref="Operands"/> entry holds the image
    /// parameter dictionary; this holds the pixel bytes. Null for every
    /// other operator. Preserving these bytes is what makes a content-stream
    /// round-trip (parse → rewrite) lossless for inline images — without it
    /// the data would be silently dropped. See <see cref="ContentStreamWriter"/>.
    /// </summary>
    public byte[]? InlineImageData { get; internal set; }

    /// <summary>
    /// Where this operator came from in the source bytes: <c>[SourceStart,
    /// SourceEnd)</c>, or <c>SourceStart &lt; 0</c> when unknown (an operator
    /// built synthetically, or parsed without
    /// <see cref="ContentStreamParser.TrackSourceSpans"/>).
    ///
    /// <para>Spans are CONTIGUOUS: one operator's span begins exactly where the
    /// previous one's ended, so it carries the whitespace and comments that
    /// preceded it. Concatenating every span of an unedited stream reproduces
    /// the source byte for byte, which is what lets
    /// <see cref="ContentStreamWriter"/> re-emit untouched operators verbatim
    /// instead of re-serializing them (#1093).</para>
    /// </summary>
    internal int SourceStart { get; set; } = -1;

    /// <summary>
    /// The exact byte array <see cref="SourceStart"/> indexes into. The writer
    /// requires reference identity with the array it is splicing against, so an
    /// operator that came from a DIFFERENT parse — a form XObject's own content
    /// stream inlined into a page, say — can never have bytes copied out of the
    /// wrong array at the right offsets. Null when no span was recorded.
    /// </summary>
    internal byte[]? SourceArray { get; set; }

    /// <inheritdoc cref="SourceStart"/>
    internal int SourceEnd { get; set; } = -1;

    /// <summary>
    /// A hash of this operator's serialized form, taken when the span above was
    /// recorded. The writer re-computes it and re-serializes on any mismatch,
    /// so an operator whose operands were mutated IN PLACE after parsing can
    /// never have its pre-mutation bytes copied back into the file.
    ///
    /// <para>This is not defensive decoration. <c>MarkedContentCarrierScrubber</c>
    /// removes an <c>/ActualText</c> carrier by calling <c>Remove</c> on a
    /// parsed <c>BDC</c> operand dictionary — the #636 leak carrier. Copying
    /// that operator's original bytes would put the scrubbed text straight back
    /// into the redacted file (#1093).</para>
    /// </summary>
    internal UInt128 SourceFingerprint { get; set; }

    /// <summary>Whether a usable source span was recorded for this operator.</summary>
    internal bool HasSourceSpan => SourceStart >= 0 && SourceEnd >= SourceStart && SourceArray != null;

    /// <summary>
    /// Creates a new content operator.
    /// </summary>
    public ContentOperator(string name, IReadOnlyList<PdfObject> operands)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Operands = operands ?? Array.Empty<PdfObject>();
        Category = CategorizeOperator(name);
        TextContent = RawTextOperand;
    }

    /// <summary>
    /// The string a text-showing operator draws, as the undecoded character codes
    /// in its operand; null for any other operator or when there is none. Read
    /// from the END of the stack, as <see cref="ContentStreamWalker"/> trims it
    /// (§9.4.3), so this agrees with what the walker draws.
    /// </summary>
    internal string? RawTextOperand
    {
        get
        {
            switch (Name)
            {
                case "Tj" or "'" when Operands.Count >= 1:
                case "\"" when Operands.Count >= 3:
                    return (Operands[^1] as PdfString)?.Value;
                case "TJ" when Operands.Count >= 1 && Operands[^1] is PdfArray arr:
                    var sb = new System.Text.StringBuilder();
                    foreach (var item in arr)
                        if (item is PdfString s) sb.Append(s.Value);
                    return sb.Length > 0 ? sb.ToString() : null;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Creates a new content operator with no operands.
    /// </summary>
    public ContentOperator(string name) : this(name, Array.Empty<PdfObject>())
    {
    }

    #region Operand Accessors

    /// <summary>
    /// Get a numeric operand by index.
    /// </summary>
    public double GetNumber(int index)
    {
        if (index < 0 || index >= Operands.Count)
            return 0;

        return Operands[index] switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => 0
        };
    }

    /// <summary>
    /// Get a name operand by index (without leading slash).
    /// </summary>
    public string GetName(int index)
    {
        if (index < 0 || index >= Operands.Count)
            return "";

        return Operands[index] is PdfName n ? n.Value : "";
    }

    /// <summary>
    /// Get a string operand by index.
    /// </summary>
    public string GetString(int index)
    {
        if (index < 0 || index >= Operands.Count)
            return "";

        return Operands[index] is PdfString s ? s.Value : "";
    }

    /// <summary>
    /// Get an array operand by index.
    /// </summary>
    public PdfArray? GetArray(int index)
    {
        if (index < 0 || index >= Operands.Count)
            return null;

        return Operands[index] as PdfArray;
    }

    #endregion

    /// <summary>
    /// Check if this operator's bounding box intersects with a rectangle.
    /// </summary>
    public bool IntersectsWith(PdfRectangle rect) =>
        BoundingBox is { } box && box.IntersectsWith(rect);

    /// <summary>
    /// Check if this operator's bounding box is contained within a rectangle.
    /// </summary>
    public bool IsContainedIn(PdfRectangle rect) =>
        BoundingBox is { } box && rect.Contains(box);

    #region Factory Methods - Graphics State

    /// <summary>
    /// Create a save graphics state operator (q).
    /// </summary>
    public static ContentOperator SaveState() => new("q");

    /// <summary>
    /// Create a restore graphics state operator (Q).
    /// </summary>
    public static ContentOperator RestoreState() => new("Q");

    /// <summary>
    /// Create a transformation matrix operator (cm).
    /// </summary>
    public static ContentOperator Transform(double a, double b, double c, double d, double e, double f)
        => new("cm", new PdfObject[]
        {
            new PdfReal(a), new PdfReal(b), new PdfReal(c),
            new PdfReal(d), new PdfReal(e), new PdfReal(f)
        });

    /// <summary>
    /// Create a line width operator (w).
    /// </summary>
    public static ContentOperator SetLineWidth(double width)
        => new("w", new PdfObject[] { new PdfReal(width) });

    #endregion

    #region Factory Methods - Path Construction

    /// <summary>
    /// Create a move-to operator (m).
    /// </summary>
    public static ContentOperator MoveTo(double x, double y)
        => new("m", new PdfObject[] { new PdfReal(x), new PdfReal(y) });

    /// <summary>
    /// Create a line-to operator (l).
    /// </summary>
    public static ContentOperator LineTo(double x, double y)
        => new("l", new PdfObject[] { new PdfReal(x), new PdfReal(y) });

    /// <summary>
    /// Create a cubic Bezier curve operator (c).
    /// </summary>
    public static ContentOperator CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        => new("c", new PdfObject[]
        {
            new PdfReal(x1), new PdfReal(y1),
            new PdfReal(x2), new PdfReal(y2),
            new PdfReal(x3), new PdfReal(y3)
        });

    /// <summary>
    /// Create a rectangle operator (re).
    /// </summary>
    public static ContentOperator Rectangle(double x, double y, double width, double height)
        => new("re", new PdfObject[]
        {
            new PdfReal(x), new PdfReal(y),
            new PdfReal(width), new PdfReal(height)
        });

    /// <summary>
    /// Create a rectangle operator (re) from a PdfRectangle.
    /// </summary>
    public static ContentOperator Rectangle(PdfRectangle rect)
        => Rectangle(rect.Left, rect.Bottom, rect.Width, rect.Height);

    /// <summary>
    /// Create a close path operator (h).
    /// </summary>
    public static ContentOperator ClosePath() => new("h");

    #endregion

    #region Factory Methods - Path Painting

    /// <summary>
    /// Create a stroke operator (S).
    /// </summary>
    public static ContentOperator Stroke() => new("S");

    /// <summary>
    /// Create a close and stroke operator (s).
    /// </summary>
    public static ContentOperator CloseAndStroke() => new("s");

    /// <summary>
    /// Create a fill operator using non-zero winding rule (f).
    /// </summary>
    public static ContentOperator Fill() => new("f");

    /// <summary>
    /// Create a fill operator using even-odd rule (f*).
    /// </summary>
    public static ContentOperator FillEvenOdd() => new("f*");

    /// <summary>
    /// Create a fill and stroke operator (B).
    /// </summary>
    public static ContentOperator FillAndStroke() => new("B");

    /// <summary>
    /// Create an end path without painting operator (n).
    /// </summary>
    public static ContentOperator EndPath() => new("n");

    #endregion

    #region Factory Methods - Color

    /// <summary>
    /// Create a grayscale fill color operator (g).
    /// </summary>
    public static ContentOperator SetFillGray(double gray)
        => new("g", new PdfObject[] { new PdfReal(gray) });

    /// <summary>
    /// Create a grayscale stroke color operator (G).
    /// </summary>
    public static ContentOperator SetStrokeGray(double gray)
        => new("G", new PdfObject[] { new PdfReal(gray) });

    /// <summary>
    /// Create an RGB fill color operator (rg).
    /// </summary>
    public static ContentOperator SetFillRgb(double r, double g, double b)
        => new("rg", new PdfObject[] { new PdfReal(r), new PdfReal(g), new PdfReal(b) });

    /// <summary>
    /// Create an RGB stroke color operator (RG).
    /// </summary>
    public static ContentOperator SetStrokeRgb(double r, double g, double b)
        => new("RG", new PdfObject[] { new PdfReal(r), new PdfReal(g), new PdfReal(b) });

    /// <summary>
    /// Create a black fill color operator.
    /// </summary>
    public static ContentOperator SetFillBlack() => SetFillGray(0);

    /// <summary>
    /// Create a white fill color operator.
    /// </summary>
    public static ContentOperator SetFillWhite() => SetFillGray(1);

    #endregion

    #region Factory Methods - Text

    /// <summary>
    /// Create a begin text operator (BT).
    /// </summary>
    public static ContentOperator BeginText() => new("BT");

    /// <summary>
    /// Create an end text operator (ET).
    /// </summary>
    public static ContentOperator EndText() => new("ET");

    /// <summary>
    /// Create a text font operator (Tf).
    /// </summary>
    public static ContentOperator SetFont(string fontName, double size)
        => new("Tf", new PdfObject[] { new PdfName(fontName), new PdfReal(size) });

    /// <summary>
    /// Create a text position operator (Td).
    /// </summary>
    public static ContentOperator TextPosition(double tx, double ty)
        => new("Td", new PdfObject[] { new PdfReal(tx), new PdfReal(ty) });

    /// <summary>
    /// Create a text matrix operator (Tm).
    /// </summary>
    public static ContentOperator TextMatrix(double a, double b, double c, double d, double e, double f)
        => new("Tm", new PdfObject[]
        {
            new PdfReal(a), new PdfReal(b), new PdfReal(c),
            new PdfReal(d), new PdfReal(e), new PdfReal(f)
        });

    /// <summary>
    /// Create a show text operator (Tj).
    /// </summary>
    public static ContentOperator ShowText(string text)
        => new("Tj", new PdfObject[] { new PdfString(text) });

    #endregion

    #region Factory Methods - XObject

    /// <summary>
    /// Create an XObject invocation operator (Do).
    /// </summary>
    public static ContentOperator InvokeXObject(string name)
        => new("Do", new PdfObject[] { new PdfName(name) });

    #endregion

    /// <summary>
    /// Categorize an operator by name.
    /// </summary>
    private static OperatorCategory CategorizeOperator(string name)
    {
        return name switch
        {
            // Graphics state
            "q" or "Q" or "cm" or "w" or "J" or "j" or "M" or "d" or "ri" or "i" or "gs"
                => OperatorCategory.GraphicsState,

            // Path construction
            "m" or "l" or "c" or "v" or "y" or "h" or "re"
                => OperatorCategory.PathConstruction,

            // Path painting
            "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n"
                => OperatorCategory.PathPainting,

            // Clipping
            "W" or "W*"
                => OperatorCategory.Clipping,

            // Text state
            "Tc" or "Tw" or "Tz" or "TL" or "Tf" or "Tr" or "Ts"
                => OperatorCategory.TextState,

            // Text positioning
            "Td" or "TD" or "Tm" or "T*"
                => OperatorCategory.TextPositioning,

            // Text showing
            "Tj" or "TJ" or "'" or "\""
                => OperatorCategory.TextShowing,

            // Text object
            "BT" or "ET"
                => OperatorCategory.TextObject,

            // Color
            "CS" or "cs" or "SC" or "SCN" or "sc" or "scn" or
            "G" or "g" or "RG" or "rg" or "K" or "k"
                => OperatorCategory.Color,

            // Shading
            "sh"
                => OperatorCategory.Shading,

            // Images/XObjects
            "BI" or "ID" or "EI" or "Do"
                => OperatorCategory.XObject,

            // Marked content
            "MP" or "DP" or "BMC" or "BDC" or "EMC"
                => OperatorCategory.MarkedContent,

            // Compatibility
            "BX" or "EX"
                => OperatorCategory.Compatibility,

            // Type 3 font glyph metrics (§9.6.5). Handled by the walker's
            // arity table and by ContentStreamParser's /d1 bbox reader since
            // #980, but uncategorised until #1702 -- so they reported
            // Category=Unknown, indistinguishable from an operator excise has
            // never heard of.
            "d0" or "d1"
                => OperatorCategory.Type3GlyphMetrics,

            _ => OperatorCategory.Unknown
        };
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (Operands.Count == 0)
            return Name;

        var operandStr = string.Join(" ", Operands.Select(FormatOperand));
        return $"{operandStr} {Name}";
    }

    private static string FormatOperand(PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => i.Value.ToString(),
            PdfReal r => PdfNumberFormatter.Format(r.Value),
            PdfName n => "/" + n.Value,
            PdfString s => "(" + EscapeString(s.Value) + ")",
            PdfArray a => "[" + string.Join(" ", a.Select(FormatOperand)) + "]",
            _ => obj.ToString() ?? ""
        };
    }

    private static string EscapeString(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

/// <summary>
/// A PDF transformation matrix [A B C D E F] (§8.3.4), page-space CTM shape:
/// (x', y') = (A·x + C·y + E, B·x + D·y + F).
/// </summary>
public readonly record struct ContentTransform(
    double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity matrix. <c>default</c> is the ZERO matrix.</summary>
    internal static ContentTransform Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>The matrix of a <c>cm</c> operator's six operands.</summary>
    internal static ContentTransform FromOperands(ContentOperator op) => new(
        op.GetNumber(0), op.GetNumber(1), op.GetNumber(2),
        op.GetNumber(3), op.GetNumber(4), op.GetNumber(5));

    /// <summary>A six-number <c>/Matrix</c> array; identity when absent or short. A non-number reads as 0.</summary>
    internal static ContentTransform FromArray(PdfArray? array)
    {
        if (array == null || array.Count < 6) return Identity;
        double At(int i) => array[i].TryGetNumber(out var v) ? v : 0;
        return new(At(0), At(1), At(2), At(3), At(4), At(5));
    }

    /// <summary>
    /// This matrix followed by <paramref name="other"/>: applying the result maps
    /// a point through this and then through <paramref name="other"/>. A
    /// <c>cm</c> folds in as <c>local.Multiply(ctm)</c> (§8.3.4).
    /// </summary>
    internal ContentTransform Multiply(ContentTransform other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        E * other.A + F * other.C + other.E,
        E * other.B + F * other.D + other.F);

    /// <summary>Axis-aligned extent of the four corners of <paramref name="rect"/> mapped through this matrix.</summary>
    internal PdfRectangle TransformBounds(PdfRectangle rect)
    {
        var (x0, y0) = TransformPoint(rect.Left, rect.Bottom);
        var (x1, y1) = TransformPoint(rect.Right, rect.Bottom);
        var (x2, y2) = TransformPoint(rect.Left, rect.Top);
        var (x3, y3) = TransformPoint(rect.Right, rect.Top);
        return new PdfRectangle(
            Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
            Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
            Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)),
            Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
    }

    /// <summary>Where an image lands: §8.9.5 maps every image onto the unit square.</summary>
    internal PdfRectangle UnitSquareBounds() => TransformBounds(new PdfRectangle(0, 0, 1, 1));

    internal bool TryInvert(out ContentTransform inverse)
    {
        var determinant = A * D - B * C;
        if (Math.Abs(determinant) < 1e-12)
        {
            inverse = default;
            return false;
        }

        inverse = new ContentTransform(
            D / determinant,
            -B / determinant,
            -C / determinant,
            A / determinant,
            (C * F - D * E) / determinant,
            (B * E - A * F) / determinant);
        return true;
    }

    /// <summary>
    /// Applies this matrix to a point: (x', y') = (A·x + C·y + E, B·x + D·y + F).
    /// Public so callers resolving page-space coordinates from
    /// <see cref="ContentOperator.GraphicsTransform"/> use the library's
    /// implementation instead of re-deriving it (issue #1436).
    /// </summary>
    public (double X, double Y) TransformPoint(double x, double y) =>
        (x * A + y * C + E, x * B + y * D + F);
}

/// <summary>
/// Categories of PDF content stream operators.
/// </summary>
public enum OperatorCategory
{
    /// <summary>Unknown or unrecognized operator.</summary>
    Unknown,
    /// <summary>Graphics state operators (q, Q, cm, w, etc.).</summary>
    GraphicsState,
    /// <summary>Path construction operators (m, l, c, re, etc.).</summary>
    PathConstruction,
    /// <summary>Path painting operators (S, f, B, etc.).</summary>
    PathPainting,
    /// <summary>Clipping operators (W, W*).</summary>
    Clipping,
    /// <summary>Text state operators (Tf, Tc, Tw, etc.).</summary>
    TextState,
    /// <summary>Text positioning operators (Td, Tm, T*, etc.).</summary>
    TextPositioning,
    /// <summary>Text showing operators (Tj, TJ, etc.).</summary>
    TextShowing,
    /// <summary>Text object delimiters (BT, ET).</summary>
    TextObject,
    /// <summary>Color operators (g, rg, k, etc.).</summary>
    Color,
    /// <summary>Shading operator (sh).</summary>
    Shading,
    /// <summary>XObject and inline image operators (Do, BI/ID/EI).</summary>
    XObject,
    /// <summary>Marked content operators (BMC, EMC, etc.).</summary>
    MarkedContent,
    /// <summary>Compatibility operators (BX, EX).</summary>
    Compatibility,
    /// <summary>Type 3 font glyph-metric operators (d0, d1) -- ISO 32000-2 §9.6.5.</summary>
    Type3GlyphMetrics
}
