using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Graphics;

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

    /// <summary>
    /// Path-construction operators since the last painting operator. ISO 32000-2
    /// §8.2 (Figure 9) admits only path-construction and clipping operators inside a
    /// path object, so the path is held back until Stroke/Fill/FillAndStroke has
    /// written the colour and line state it paints with (#1851).
    /// </summary>
    private readonly StringBuilder _pendingPath = new();

    /// <summary>
    /// The graphics-state parameters a pen or brush can change beyond colour and width.
    /// They persist until set again, so each is written only when it differs from what
    /// this context last set, and <see cref="Dispose"/> puts back the ISO 32000-2
    /// initial values so a later context on the same page starts from them.
    /// </summary>
    private record struct PaintState(
        IReadOnlyList<double>? Dash, double DashPhase, PdfLineCap Cap, PdfLineJoin Join,
        double MiterLimit, double StrokeAlpha, double FillAlpha)
    {
        public static readonly PaintState Initial = new(null, 0, PdfLineCap.Butt, PdfLineJoin.Miter, 10, 1, 1);
    }

    private PaintState _state = PaintState.Initial;
    private readonly Stack<PaintState> _savedStates = new();
    private bool _disposed;

    /// <summary>
    /// Append one content-stream operator line with a deterministic '\n'
    /// terminator. StringBuilder.AppendLine uses Environment.NewLine, which
    /// is "\r\n" on Windows — that made excise-authored content streams
    /// byte-different across platforms (same document, different saved
    /// bytes) and broke line-based tooling that splits on '\n'. PDF treats
    /// both as whitespace, but deterministic output is strictly better for
    /// reproducible saves, hashing, and tests. Always use this instead of
    /// _operators.AppendLine.
    /// </summary>
    private void EmitLine(string line)
    {
        _operators.Append(line);
        _operators.Append('\n');
    }

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
        EmitLine("q");
        _savedStates.Push(_state);
    }

    /// <summary>
    /// Restores the previous graphics state (Q operator).
    /// </summary>
    public void RestoreState()
    {
        ThrowIfDisposed();
        EmitLine("Q");
        if (_savedStates.Count > 0)
            _state = _savedStates.Pop();
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
        EmitLine($"/{tag} <</MCID {mcid}>> BDC");
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
        EmitLine("/Artifact BMC");
    }

    /// <summary>Close the most recent marked-content sequence (<c>EMC</c>).</summary>
    public void EndMarkedContent()
    {
        ThrowIfDisposed();
        EmitLine("EMC");
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
        EmitLine($"1 0 0 1 {Fmt(tx)} {Fmt(ty)} cm");
    }

    /// <summary>
    /// Scales the coordinate system.
    /// </summary>
    public void Scale(double sx, double sy)
    {
        ThrowIfDisposed();
        // Scale matrix: [sx 0 0 sy 0 0]
        EmitLine($"{Fmt(sx)} 0 0 {Fmt(sy)} 0 0 cm");
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
        EmitLine($"{Fmt(cos)} {Fmt(sin)} {Fmt(-sin)} {Fmt(cos)} 0 0 cm");
    }

    /// <summary>
    /// Applies a transformation matrix.
    /// </summary>
    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        ThrowIfDisposed();
        EmitLine($"{Fmt(a)} {Fmt(b)} {Fmt(c)} {Fmt(d)} {Fmt(e)} {Fmt(f)} cm");
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
            EmitLine(fill.GetFillColorOperator());
        }

        if (stroke != null)
        {
            ApplyPen(stroke);
        }

        ApplyOpacity(stroke?.Opacity, fill?.Opacity);

        // Draw rectangle path
        EmitLine($"{Fmt(x)} {Fmt(y)} {Fmt(width)} {Fmt(height)} re");

        // Fill and/or stroke
        if (fill != null && stroke != null)
        {
            EmitLine("B"); // Fill and stroke
        }
        else if (fill != null)
        {
            EmitLine("f"); // Fill only
        }
        else if (stroke != null)
        {
            EmitLine("S"); // Stroke only
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

        ApplyPen(pen);
        ApplyOpacity(pen.Opacity, null);
        EmitLine($"{Fmt(x1)} {Fmt(y1)} m");
        EmitLine($"{Fmt(x2)} {Fmt(y2)} l");
        EmitLine("S");
    }

    #endregion

    #region Ellipse and Arc Drawing

    /// <summary>
    /// Draws the ellipse inscribed in the box <see cref="DrawRectangle(double, double, double, double, PdfBrush?, PdfPen?)"/>
    /// takes: (<paramref name="x"/>, <paramref name="y"/>) is its lower-left corner in PDF user
    /// space (y up). The outline is four cubic Bezier quarter arcs. Nothing is drawn when both
    /// <paramref name="fill"/> and <paramref name="stroke"/> are null.
    /// </summary>
    public void DrawEllipse(double x, double y, double width, double height, PdfBrush? fill, PdfPen? stroke)
    {
        ThrowIfDisposed();
        if (fill == null && stroke == null)
            return;

        AppendArc(x + width / 2, y + height / 2, width / 2, height / 2, 0, 360);
        ClosePath();
        if (fill != null && stroke != null)
            FillAndStroke(fill, stroke);
        else if (fill != null)
            Fill(fill);
        else
            Stroke(stroke!);
    }

    /// <summary>
    /// Draws a circle of <paramref name="radius"/> centred on (<paramref name="cx"/>, <paramref name="cy"/>);
    /// see <see cref="DrawEllipse"/>.
    /// </summary>
    public void DrawCircle(double cx, double cy, double radius, PdfBrush? fill, PdfPen? stroke) =>
        DrawEllipse(cx - radius, cy - radius, 2 * radius, 2 * radius, fill, stroke);

    /// <summary>
    /// Strokes an open arc of the ellipse <see cref="DrawEllipse"/> would draw in the same box.
    /// Angles are in degrees from the ellipse's centre, 0 pointing along +x; a positive
    /// <paramref name="sweepAngleDegrees"/> runs counter-clockwise in PDF user space (y up),
    /// so 0 to 90 is the upper-right quarter. A sweep beyond ±360 is clamped to a full turn.
    /// </summary>
    public void DrawArc(double x, double y, double width, double height,
        double startAngleDegrees, double sweepAngleDegrees, PdfPen stroke)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stroke);
        AppendArc(x + width / 2, y + height / 2, width / 2, height / 2,
            startAngleDegrees, Math.Clamp(sweepAngleDegrees, -360, 360));
        Stroke(stroke);
    }

    /// <summary>
    /// Start a subpath at the arc's first point and approximate the arc with one cubic Bezier
    /// per piece of at most 90 degrees. Each piece of angle θ puts its control points along the
    /// end tangents at 4/3·tan(θ/4) of the radius, which for θ = 90 is the familiar 0.5523.
    /// </summary>
    private void AppendArc(double cx, double cy, double rx, double ry, double startDegrees, double sweepDegrees)
    {
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepDegrees) / 90 - 1e-9));
        double step = sweepDegrees / pieces * Math.PI / 180;
        double k = 4.0 / 3 * Math.Tan(step / 4);
        double angle = startDegrees * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);

        MoveTo(cx + rx * cos, cy + ry * sin);
        for (int i = 0; i < pieces; i++)
        {
            angle += step;
            double cosEnd = Math.Cos(angle), sinEnd = Math.Sin(angle);
            CurveTo(
                cx + rx * (cos - k * sin), cy + ry * (sin + k * cos),
                cx + rx * (cosEnd + k * sinEnd), cy + ry * (sinEnd - k * cosEnd),
                cx + rx * cosEnd, cy + ry * sinEnd);
            (cos, sin) = (cosEnd, sinEnd);
        }
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
        AppendPath($"{Fmt(x)} {Fmt(y)} m");
    }

    /// <summary>
    /// Draws a line from the current point to (x, y).
    /// </summary>
    public void LineTo(double x, double y)
    {
        ThrowIfDisposed();
        AppendPath($"{Fmt(x)} {Fmt(y)} l");
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
        AppendPath($"{Fmt(x1)} {Fmt(y1)} {Fmt(x2)} {Fmt(y2)} {Fmt(x3)} {Fmt(y3)} c");
    }

    /// <summary>
    /// Closes the current subpath by drawing a line to the starting point.
    /// </summary>
    public void ClosePath()
    {
        ThrowIfDisposed();
        AppendPath("h");
    }

    /// <summary>
    /// Strokes the current path.
    /// </summary>
    public void Stroke(PdfPen pen)
    {
        ThrowIfDisposed();
        ApplyPen(pen);
        ApplyOpacity(pen.Opacity, null);
        EmitPath("S");
    }

    /// <summary>
    /// Fills the current path.
    /// </summary>
    public void Fill(PdfBrush brush)
    {
        ThrowIfDisposed();
        EmitLine(brush.GetFillColorOperator());
        ApplyOpacity(null, brush.Opacity);
        EmitPath("f");
    }

    /// <summary>
    /// Fills and strokes the current path.
    /// </summary>
    public void FillAndStroke(PdfBrush brush, PdfPen pen)
    {
        ThrowIfDisposed();
        EmitLine(brush.GetFillColorOperator());
        ApplyPen(pen);
        ApplyOpacity(pen.Opacity, brush.Opacity);
        EmitPath("B");
    }

    /// <summary>
    /// Intersects the clipping region with the current path under the nonzero winding rule
    /// (<c>W n</c>) and ends the path without painting it. Later drawing is confined to the
    /// region until the enclosing <see cref="RestoreState"/>, the only way to widen it again:
    /// call this between <see cref="SaveState"/> and <see cref="RestoreState"/>. A clip set
    /// outside them also confines every later <see cref="PdfGraphics"/> on the same page,
    /// since each one appends to the same content stream and <see cref="Dispose"/> cannot undo it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No path has been built since the last paint or clip.</exception>
    public void Clip() => ClipPath("W");

    /// <summary>
    /// <see cref="Clip"/> under the even-odd rule (<c>W* n</c>): a subpath inside another
    /// cuts a hole in the region.
    /// </summary>
    /// <exception cref="InvalidOperationException">No path has been built since the last paint or clip.</exception>
    public void ClipEvenOdd() => ClipPath("W*");

    /// <summary>
    /// Adds the rectangle to the current path and <see cref="Clip"/>s to it; the same
    /// SaveState/RestoreState scoping applies.
    /// </summary>
    public void ClipRectangle(double x, double y, double width, double height)
    {
        ThrowIfDisposed();
        AppendPath($"{Fmt(x)} {Fmt(y)} {Fmt(width)} {Fmt(height)} re");
        Clip();
    }

    private void ClipPath(string clipOperator)
    {
        ThrowIfDisposed();
        if (_pendingPath.Length == 0)
            throw new InvalidOperationException("Build a path with MoveTo/LineTo/CurveTo before clipping to it.");

        // §8.5.4: W/W* sits after the last path-construction operator and before the
        // painting operator that ends the path; n ends it without painting.
        AppendPath(clipOperator);
        EmitPath("n");
    }

    private void AppendPath(string line)
    {
        _pendingPath.Append(line);
        _pendingPath.Append('\n');
    }

    /// <summary>
    /// Write the pen's stroke colour and width, then whichever of its dash, cap, join
    /// and miter limit differ from the current state.
    /// </summary>
    private void ApplyPen(PdfPen pen)
    {
        EmitLine(pen.GetStrokeColorOperator());
        EmitLine(pen.GetLineWidthOperator());
        SetLineStyle(pen.DashArray, pen.DashPhase, pen.LineCap, pen.LineJoin, pen.MiterLimit);
    }

    private void SetLineStyle(IReadOnlyList<double>? dash, double phase, PdfLineCap cap, PdfLineJoin join, double miterLimit)
    {
        // §8.4.3.6: an empty dash array strokes solid and takes phase 0.
        if (dash == null)
            phase = 0;

        bool sameDash = dash == null ? _state.Dash == null : _state.Dash != null && dash.SequenceEqual(_state.Dash);
        if (!sameDash || phase != _state.DashPhase)
        {
            EmitLine($"[{string.Join(' ', (dash ?? []).Select(Fmt))}] {Fmt(phase)} d");
            _state = _state with { Dash = dash, DashPhase = phase };
        }
        if (cap != _state.Cap)
        {
            EmitLine($"{(int)cap} J");
            _state = _state with { Cap = cap };
        }
        if (join != _state.Join)
        {
            EmitLine($"{(int)join} j");
            _state = _state with { Join = join };
        }
        if (miterLimit != _state.MiterLimit)
        {
            EmitLine($"{Fmt(miterLimit)} M");
            _state = _state with { MiterLimit = miterLimit };
        }
    }

    /// <summary>
    /// Select an ExtGState setting the stroking (<c>/CA</c>) and non-stroking
    /// (<c>/ca</c>) alpha that differ from the current state; <c>null</c> leaves one as is.
    /// </summary>
    private void ApplyOpacity(double? strokeAlpha, double? fillAlpha)
    {
        double? stroke = strokeAlpha is { } s && s != _state.StrokeAlpha ? s : null;
        double? fill = fillAlpha is { } f && f != _state.FillAlpha ? f : null;
        if (stroke == null && fill == null)
            return;

        EmitLine($"/{_page.AddOpacityState(stroke, fill)} gs");
        _state = _state with
        {
            StrokeAlpha = stroke ?? _state.StrokeAlpha,
            FillAlpha = fill ?? _state.FillAlpha
        };
    }

    /// <summary>Write the pending path and the operator that paints (and ends) it.</summary>
    private void EmitPath(string paintOperator)
    {
        _operators.Append(_pendingPath);
        _pendingPath.Clear();
        EmitLine(paintOperator);
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
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> contains a character <paramref name="font"/> cannot
    /// represent (#1671). Nothing is written; use an embedded font for such text.
    /// </exception>
    public void DrawString(string text, PdfFont font, PdfBrush brush, double x, double y)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(text))
            return;

        // #1671: EncodeString writes '?' for a character the font cannot
        // represent. Refuse instead, before anything is emitted.
        font.EnsureCanEncode(text, "text");

        // Ensure font is registered in page resources
        var fontName = _page.AddFont(font);

        // Set fill color
        EmitLine(brush.GetFillColorOperator());
        ApplyOpacity(null, brush.Opacity);

        // Begin text block
        EmitLine("BT");

        // Set font and size
        EmitLine($"/{fontName} {Fmt(font.Size)} Tf");

        // Position text (Td moves from current position)
        EmitLine($"{Fmt(x)} {Fmt(y)} Td");

        // Draw the text
        EmitLine($"{font.EncodeString(text)} Tj");

        // End text block
        EmitLine("ET");
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

        EmitLine("BT");
        EmitLine($"/{fontName} {Fmt(font.Size)} Tf");
        EmitLine("3 Tr");
        EmitLine($"{Fmt(scale)} Tz");
        EmitLine($"{Fmt(x)} {Fmt(y)} Td");
        EmitLine($"{font.EncodeString(text)} Tj");
        // Tr/Tz are text state, not part of the q/Q graphics-state stack —
        // they'd otherwise leak into any later DrawString/DrawText call in
        // this (or a future) PdfGraphics session on the same content stream.
        EmitLine("0 Tr");
        EmitLine("100 Tz");
        EmitLine("ET");
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

        // #1671: check the whole text before drawing any of it. Dispose flushes
        // whatever was emitted, so a line-by-line refusal would leave the lines
        // before the bad one written into the page.
        font.EnsureCanEncode(text, "text");

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
    /// Gets the generated PDF operators as a string. A path under construction is
    /// not included until a Stroke/Fill/FillAndStroke call paints it.
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

    private static string Fmt(double value) => PdfNumberFormatter.Format(value);

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
            // A path never painted is ended with the no-op painting operator `n`,
            // so the stream holds no unterminated path object (§8.5.3.1).
            if (_pendingPath.Length > 0)
                EmitPath("n");

            var initial = PaintState.Initial;
            SetLineStyle(initial.Dash, initial.DashPhase, initial.Cap, initial.Join, initial.MiterLimit);
            ApplyOpacity(initial.StrokeAlpha, initial.FillAlpha);

            // Flush any remaining operations
            if (_operators.Length > 0)
                Flush();
            _disposed = true;
        }
    }
}
