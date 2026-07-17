using System.Globalization;
using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Graphics;

/// <summary>
/// Text alignment options for DrawString.
/// </summary>
public enum TextAlignment
{
    /// <summary>Left aligned (default).</summary>
    Left,
    /// <summary>Center aligned.</summary>
    Center,
    /// <summary>Right aligned.</summary>
    Right
}

/// <summary>
/// Represents a size in PDF units (points).
/// </summary>
public readonly record struct PdfSize(double Width, double Height)
{
    /// <summary>An empty size.</summary>
    public static readonly PdfSize Empty = new(0, 0);

    /// <inheritdoc />
    public override string ToString() => $"{Width:F2} x {Height:F2}";
}

/// <summary>
/// Result of <see cref="PdfGraphics.DrawText"/>: how much vertical space the
/// drawn text consumed, and any text that did not fit the box.
/// </summary>
/// <param name="UsedHeight">Vertical space consumed, in points.</param>
/// <param name="Overflow">Text that didn't fit (newline-joined), or <c>null</c> if all fit.</param>
public readonly record struct TextLayoutResult(double UsedHeight, string? Overflow)
{
    /// <summary>True when some text didn't fit the box.</summary>
    public bool HasOverflow => Overflow != null;
}

/// <summary>
/// Provides graphics drawing operations for a PDF page.
/// Generates PDF content stream operators for drawing shapes, text, and images.
/// </summary>
public class PdfGraphics : IDisposable
{
    private readonly PdfPage _page;
    private readonly StringBuilder _operators;
    private bool _disposed;

    /// <summary>
    /// Creates a graphics context for the specified page.
    /// </summary>
    internal PdfGraphics(PdfPage page)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _operators = new StringBuilder();
    }

    #region State Management

    /// <summary>
    /// Saves the current graphics state (q operator).
    /// </summary>
    public void SaveState()
    {
        ThrowIfDisposed();
        _operators.AppendLine("q");
    }

    /// <summary>
    /// Restores the previous graphics state (Q operator).
    /// </summary>
    public void RestoreState()
    {
        ThrowIfDisposed();
        _operators.AppendLine("Q");
    }

    #endregion

    #region Marked Content (tagged PDF)

    /// <summary>
    /// Open a marked-content sequence tagged for the structure tree:
    /// <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC</c> (PDF §14.6 / §14.8). Pair with
    /// <see cref="EndMarkedContent"/>.
    /// </summary>
    public void BeginMarkedContent(string tag, int mcid)
    {
        ThrowIfDisposed();
        _operators.AppendLine($"/{tag} <</MCID {mcid}>> BDC");
    }

    /// <summary>
    /// Open an artifact marked-content sequence (<c>/Artifact BDC</c>) for purely
    /// decorative content that is excluded from the structure tree — required by
    /// PDF/UA so every piece of content is either tagged or an artifact. Pair
    /// with <see cref="EndMarkedContent"/>.
    /// </summary>
    public void BeginArtifact()
    {
        ThrowIfDisposed();
        // BMC (not BDC) — a property-less artifact takes no properties operand.
        _operators.AppendLine("/Artifact BMC");
    }

    /// <summary>Close the most recent marked-content sequence (<c>EMC</c>).</summary>
    public void EndMarkedContent()
    {
        ThrowIfDisposed();
        _operators.AppendLine("EMC");
    }

    #endregion

    #region Transformations

    /// <summary>
    /// Translates the coordinate system.
    /// </summary>
    public void Translate(double tx, double ty)
    {
        ThrowIfDisposed();
        // Translation matrix: [1 0 0 1 tx ty]
        _operators.AppendLine($"1 0 0 1 {Fmt(tx)} {Fmt(ty)} cm");
    }

    /// <summary>
    /// Scales the coordinate system.
    /// </summary>
    public void Scale(double sx, double sy)
    {
        ThrowIfDisposed();
        // Scale matrix: [sx 0 0 sy 0 0]
        _operators.AppendLine($"{Fmt(sx)} 0 0 {Fmt(sy)} 0 0 cm");
    }

    /// <summary>
    /// Rotates the coordinate system by the specified angle in degrees.
    /// </summary>
    public void Rotate(double degrees)
    {
        ThrowIfDisposed();
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        // Rotation matrix: [cos sin -sin cos 0 0]
        _operators.AppendLine($"{Fmt(cos)} {Fmt(sin)} {Fmt(-sin)} {Fmt(cos)} 0 0 cm");
    }

    /// <summary>
    /// Applies a transformation matrix.
    /// </summary>
    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        ThrowIfDisposed();
        _operators.AppendLine($"{Fmt(a)} {Fmt(b)} {Fmt(c)} {Fmt(d)} {Fmt(e)} {Fmt(f)} cm");
    }

    #endregion

    #region Rectangle Drawing

    /// <summary>
    /// Draws a filled rectangle.
    /// </summary>
    public void DrawRectangle(double x, double y, double width, double height, PdfBrush fill)
    {
        DrawRectangle(x, y, width, height, fill, null);
    }

    /// <summary>
    /// Draws a rectangle with optional fill and stroke.
    /// </summary>
    public void DrawRectangle(double x, double y, double width, double height, PdfBrush? fill, PdfPen? stroke)
    {
        ThrowIfDisposed();

        // Set colors
        if (fill != null)
        {
            _operators.AppendLine(fill.GetFillColorOperator());
        }

        if (stroke != null)
        {
            _operators.AppendLine(stroke.GetStrokeColorOperator());
            _operators.AppendLine(stroke.GetLineWidthOperator());
        }

        // Draw rectangle path
        _operators.AppendLine($"{Fmt(x)} {Fmt(y)} {Fmt(width)} {Fmt(height)} re");

        // Fill and/or stroke
        if (fill != null && stroke != null)
        {
            _operators.AppendLine("B"); // Fill and stroke
        }
        else if (fill != null)
        {
            _operators.AppendLine("f"); // Fill only
        }
        else if (stroke != null)
        {
            _operators.AppendLine("S"); // Stroke only
        }
    }

    #endregion

    #region Line Drawing

    /// <summary>
    /// Draws a line from (x1,y1) to (x2,y2).
    /// </summary>
    public void DrawLine(double x1, double y1, double x2, double y2, PdfPen pen)
    {
        ThrowIfDisposed();

        _operators.AppendLine(pen.GetStrokeColorOperator());
        _operators.AppendLine(pen.GetLineWidthOperator());
        _operators.AppendLine($"{Fmt(x1)} {Fmt(y1)} m");
        _operators.AppendLine($"{Fmt(x2)} {Fmt(y2)} l");
        _operators.AppendLine("S");
    }

    #endregion

    #region Path Operations

    /// <summary>
    /// Begins a new path.
    /// </summary>
    public void BeginPath()
    {
        ThrowIfDisposed();
        // Path operations follow
    }

    /// <summary>
    /// Moves the current point to (x, y) without drawing.
    /// </summary>
    public void MoveTo(double x, double y)
    {
        ThrowIfDisposed();
        _operators.AppendLine($"{Fmt(x)} {Fmt(y)} m");
    }

    /// <summary>
    /// Draws a line from the current point to (x, y).
    /// </summary>
    public void LineTo(double x, double y)
    {
        ThrowIfDisposed();
        _operators.AppendLine($"{Fmt(x)} {Fmt(y)} l");
    }

    /// <summary>
    /// Draws a cubic Bezier curve.
    /// </summary>
    /// <param name="x1">First control point X</param>
    /// <param name="y1">First control point Y</param>
    /// <param name="x2">Second control point X</param>
    /// <param name="y2">Second control point Y</param>
    /// <param name="x3">End point X</param>
    /// <param name="y3">End point Y</param>
    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        ThrowIfDisposed();
        _operators.AppendLine($"{Fmt(x1)} {Fmt(y1)} {Fmt(x2)} {Fmt(y2)} {Fmt(x3)} {Fmt(y3)} c");
    }

    /// <summary>
    /// Closes the current subpath by drawing a line to the starting point.
    /// </summary>
    public void ClosePath()
    {
        ThrowIfDisposed();
        _operators.AppendLine("h");
    }

    /// <summary>
    /// Strokes the current path.
    /// </summary>
    public void Stroke(PdfPen pen)
    {
        ThrowIfDisposed();
        _operators.AppendLine(pen.GetStrokeColorOperator());
        _operators.AppendLine(pen.GetLineWidthOperator());
        _operators.AppendLine("S");
    }

    /// <summary>
    /// Fills the current path.
    /// </summary>
    public void Fill(PdfBrush brush)
    {
        ThrowIfDisposed();
        _operators.AppendLine(brush.GetFillColorOperator());
        _operators.AppendLine("f");
    }

    /// <summary>
    /// Fills and strokes the current path.
    /// </summary>
    public void FillAndStroke(PdfBrush brush, PdfPen pen)
    {
        ThrowIfDisposed();
        _operators.AppendLine(brush.GetFillColorOperator());
        _operators.AppendLine(pen.GetStrokeColorOperator());
        _operators.AppendLine(pen.GetLineWidthOperator());
        _operators.AppendLine("B");
    }

    #endregion

    #region Text Drawing

    /// <summary>
    /// Draws text at the specified position.
    /// </summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="font">The font to use.</param>
    /// <param name="brush">The brush for text color.</param>
    /// <param name="x">X coordinate (in PDF coordinates, bottom-left origin).</param>
    /// <param name="y">Y coordinate (in PDF coordinates, bottom-left origin).</param>
    public void DrawString(string text, PdfFont font, PdfBrush brush, double x, double y)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(text))
            return;

        // Ensure font is registered in page resources
        var fontName = _page.AddFont(font);

        // Set fill color
        _operators.AppendLine(brush.GetFillColorOperator());

        // Begin text block
        _operators.AppendLine("BT");

        // Set font and size
        _operators.AppendLine($"/{fontName} {Fmt(font.Size)} Tf");

        // Position text (Td moves from current position)
        _operators.AppendLine($"{Fmt(x)} {Fmt(y)} Td");

        // Draw the text
        _operators.AppendLine($"{font.EncodeString(text)} Tj");

        // End text block
        _operators.AppendLine("ET");
    }

    /// <summary>
    /// Draws text at the specified position with alignment options.
    /// </summary>
    public void DrawString(string text, PdfFont font, PdfBrush brush, double x, double y, TextAlignment alignment)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(text))
            return;

        // Calculate alignment offset
        var width = font.MeasureWidth(text);
        var alignedX = alignment switch
        {
            TextAlignment.Center => x - width / 2,
            TextAlignment.Right => x - width,
            _ => x // Left alignment (default)
        };

        DrawString(text, font, brush, alignedX, y);
    }

    /// <summary>
    /// Draws invisible text (render mode 3 — neither fill nor stroke) scaled
    /// horizontally via <c>Tz</c> so its rendered width matches
    /// <paramref name="targetWidth"/>. Used to lay an OCR text layer over a
    /// raster scan: nothing is painted, but the glyphs occupy the word's
    /// true bounding box, so search/selection/redaction land in the right
    /// place (#627). No-ops (writes nothing) if <paramref name="text"/> is
    /// empty, contains a character <paramref name="font"/> can't represent
    /// (see <see cref="PdfFont.CanEncodeFully"/> — writing a lossy
    /// <c>?</c> would silently corrupt search, worse than omitting the
    /// word), or its natural width at this font size is ~0.
    /// </summary>
    public void DrawInvisibleText(string text, PdfFont font, double x, double y, double targetWidth)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(font);

        if (string.IsNullOrEmpty(text)) return;
        if (targetWidth <= 0) return;
        if (!font.CanEncodeFully(text)) return;

        var naturalWidth = font.MeasureWidth(text);
        if (naturalWidth <= 0.001) return;

        var scale = Math.Clamp(100.0 * targetWidth / naturalWidth, 10.0, 400.0);

        var fontName = _page.AddFont(font);

        _operators.AppendLine("BT");
        _operators.AppendLine($"/{fontName} {Fmt(font.Size)} Tf");
        _operators.AppendLine("3 Tr");
        _operators.AppendLine($"{Fmt(scale)} Tz");
        _operators.AppendLine($"{Fmt(x)} {Fmt(y)} Td");
        _operators.AppendLine($"{font.EncodeString(text)} Tj");
        // Tr/Tz are text state, not part of the q/Q graphics-state stack —
        // they'd otherwise leak into any later DrawString/DrawText call in
        // this (or a future) PdfGraphics session on the same content stream.
        _operators.AppendLine("0 Tr");
        _operators.AppendLine("100 Tz");
        _operators.AppendLine("ET");
    }

    /// <summary>
    /// Measures the size of a string when rendered with the specified font.
    /// </summary>
    public static PdfSize MeasureString(string text, PdfFont font)
    {
        if (string.IsNullOrEmpty(text))
            return new PdfSize(0, 0);

        return new PdfSize(font.MeasureWidth(text), font.LineHeight);
    }

    /// <summary>
    /// Measures <paramref name="text"/> when word-wrapped to <paramref name="maxWidth"/>
    /// points: width is the widest resulting line (≤ maxWidth), height is the total
    /// stacked line height. <paramref name="lineSpacing"/> is a multiple of the font
    /// size (default 1.2).
    /// </summary>
    public static PdfSize MeasureText(string text, PdfFont font, double maxWidth, double lineSpacing = 1.2)
    {
        ArgumentNullException.ThrowIfNull(font);
        var lines = TextWrapper.Wrap(text ?? string.Empty, font, maxWidth).ToList();
        double w = 0;
        foreach (var line in lines)
            w = Math.Max(w, font.MeasureWidth(line));
        return new PdfSize(w, lines.Count * font.Size * lineSpacing);
    }

    /// <summary>
    /// Draws word-wrapped, multi-line text inside <paramref name="bounds"/> (PDF
    /// coordinates, bottom-left origin). Lines flow from the top of the box; any
    /// that don't fit vertically are returned as overflow so a caller can
    /// continue on the next page. Honors hard line breaks and embedded fonts.
    /// </summary>
    /// <returns>
    /// The height actually used and the overflow text that didn't fit
    /// (<c>null</c> when everything fit).
    /// </returns>
    public TextLayoutResult DrawText(
        string text,
        PdfFont font,
        PdfBrush brush,
        PdfRectangle bounds,
        TextAlignment alignment = TextAlignment.Left,
        double lineSpacing = 1.2)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(brush);

        double lineHeight = font.Size * lineSpacing;
        var lines = TextWrapper.Wrap(text ?? string.Empty, font, bounds.Width).ToList();

        double x = alignment switch
        {
            TextAlignment.Center => bounds.Left + bounds.Width / 2,
            TextAlignment.Right => bounds.Left + bounds.Width,
            _ => bounds.Left
        };

        double y = bounds.Top;            // top of the text box (PDF coords)
        double used = 0;
        int drawn = 0;
        foreach (var line in lines)
        {
            if (y - lineHeight < bounds.Bottom)
                break;                    // no vertical room for another line
            double baseline = y - font.Ascender;
            DrawString(line, font, brush, x, baseline, alignment);
            y -= lineHeight;
            used += lineHeight;
            drawn++;
        }

        string? overflow = drawn < lines.Count
            ? string.Join("\n", lines.Skip(drawn))
            : null;
        return new TextLayoutResult(used, overflow);
    }

    #endregion

    #region Output

    /// <summary>
    /// Gets the generated PDF operators as a string.
    /// </summary>
    public string GetOperators()
    {
        return _operators.ToString();
    }

    /// <summary>
    /// Flushes the graphics operations to the page's content stream.
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();

        if (_operators.Length == 0)
            return;

        // Get existing content
        var existingContent = _page.GetContentStreamBytes();
        var existingText = Encoding.Latin1.GetString(existingContent);

        // Append new operators
        var newContent = existingText + "\n" + _operators.ToString();
        var newBytes = Encoding.Latin1.GetBytes(newContent);

        // Update page content
        _page.SetContentStreamBytes(newBytes);

        // Clear the buffer
        _operators.Clear();
    }

    #endregion

    #region Private Helpers

    private static string Fmt(double value)
    {
        // Format number without unnecessary trailing zeros
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfGraphics));
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            // Flush any remaining operations
            if (_operators.Length > 0)
                Flush();
            _disposed = true;
        }
    }
}
