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
    /// Creates a new content operator.
    /// </summary>
    public ContentOperator(string name, IReadOnlyList<PdfObject> operands)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Operands = operands ?? Array.Empty<PdfObject>();
        Category = CategorizeOperator(name);
        TextContent = ExtractTextContent(name, Operands);
    }

    private static string? ExtractTextContent(string name, IReadOnlyList<PdfObject> operands)
    {
        switch (name)
        {
            case "Tj":
            case "'":
                return operands.Count >= 1 ? (operands[0] as PdfString)?.Value : null;
            case "\"":
                return operands.Count >= 3 ? (operands[2] as PdfString)?.Value : null;
            case "TJ":
                if (operands.Count >= 1 && operands[0] is PdfArray arr)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var item in arr)
                        if (item is PdfString s) sb.Append(s.Value);
                    return sb.Length > 0 ? sb.ToString() : null;
                }
                return null;
            default:
                return null;
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
    public bool IntersectsWith(PdfRectangle rect)
    {
        if (BoundingBox == null)
            return false;

        var a = BoundingBox.Value.Normalize();
        var b = rect.Normalize();

        return a.Left < b.Right && a.Right > b.Left &&
               a.Bottom < b.Top && a.Top > b.Bottom;
    }

    /// <summary>
    /// Check if this operator's bounding box is contained within a rectangle.
    /// </summary>
    public bool IsContainedIn(PdfRectangle rect)
    {
        if (BoundingBox == null)
            return false;

        var a = BoundingBox.Value.Normalize();
        var b = rect.Normalize();

        return a.Left >= b.Left && a.Right <= b.Right &&
               a.Bottom >= b.Bottom && a.Top <= b.Top;
    }

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
            PdfReal r => r.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
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
    Compatibility
}
