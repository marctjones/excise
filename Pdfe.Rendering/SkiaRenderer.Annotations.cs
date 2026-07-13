using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using SkiaSharp;

namespace Pdfe.Rendering;

internal partial class RenderContext
{
    /// <summary>
    /// Render every visible annotation on the page on top of the main
    /// content. Each annotation's <c>/AP /N</c> stream is a Form XObject
    /// in the appearance's own coordinate space; we compute the matrix
    /// that maps its <c>/BBox</c> (transformed by /Matrix) onto the
    /// annotation's <c>/Rect</c> per ISO 32000-2 §12.5.5, then dispatch
    /// the appearance through the existing Form XObject pipeline.
    /// Annotations without an /AP entry are skipped — synthesizing a
    /// default appearance from /Subtype-specific properties (sticky-note
    /// icon, link rectangles, etc.) is handled separately, if at all.
    /// </summary>
    private void RenderAnnotations()
    {
        IReadOnlyList<Pdfe.Core.Document.PdfAnnotation> annots;
        try { annots = _page.GetAnnotations(); }
        catch { return; }
        if (annots.Count == 0) return;

        foreach (var annot in annots)
        {
            // Skip annotations the spec says shouldn't be displayed.
            // Print=4 is fine — that's an opt-in for *also* including
            // the annotation in printed output, not a "screen only" flag.
            var f = annot.Flags;
            if ((f & (Pdfe.Core.Document.PdfAnnotationFlags.Hidden
                    | Pdfe.Core.Document.PdfAnnotationFlags.NoView
                    | Pdfe.Core.Document.PdfAnnotationFlags.Invisible)) != 0)
                continue;

            var appearance = ResolveAppearanceN(annot);
            if (appearance == null)
            {
                // No baked /AP /N stream — synthesize a minimal default
                // appearance for the subtypes commercial viewers
                // routinely show (interactive widgets and shape
                // annotations). Link annotations remain an interactive
                // overlay unless the PDF supplies a real /AP stream;
                // synthesizing borders can obscure page content when a
                // producer writes a large /Border width.
                // Without this, signature widgets
                // and unfilled form fields are invisible and PDFs look
                // visibly less complete than in Acrobat / Preview /
                // Chrome.
                RenderDefaultAppearance(annot);
                continue;
            }

            // Appearance bbox + matrix.
            if (appearance.GetOptional("BBox") is not Pdfe.Core.Primitives.PdfArray bboxArr ||
                bboxArr.Count < 4) continue;
            if (!TryGetArrayNumber(bboxArr, 0, out var bx1Value) ||
                !TryGetArrayNumber(bboxArr, 1, out var by1Value) ||
                !TryGetArrayNumber(bboxArr, 2, out var bx2Value) ||
                !TryGetArrayNumber(bboxArr, 3, out var by2Value)) continue;
            float bx1 = (float)bx1Value;
            float by1 = (float)by1Value;
            float bx2 = (float)bx2Value;
            float by2 = (float)by2Value;
            float bMinX = Math.Min(bx1, bx2);
            float bMinY = Math.Min(by1, by2);
            float bMaxX = Math.Max(bx1, bx2);
            float bMaxY = Math.Max(by1, by2);

            var formMatrix = SKMatrix.Identity;
            if (appearance.GetOptional("Matrix") is Pdfe.Core.Primitives.PdfArray mArr && mArr.Count >= 6)
            {
                formMatrix = GetMatrix(mArr);
            }

            // Transform the four bbox corners through the form's /Matrix
            // and take the axis-aligned bounding box of the result. Spec
            // step from §12.5.5: "a quadrilateral whose corners are the
            // four corners of BBox transformed by Matrix … then the
            // smallest rectangle enclosing those four points."
            var p1 = formMatrix.MapPoint(new SKPoint(bMinX, bMinY));
            var p2 = formMatrix.MapPoint(new SKPoint(bMaxX, bMinY));
            var p3 = formMatrix.MapPoint(new SKPoint(bMaxX, bMaxY));
            var p4 = formMatrix.MapPoint(new SKPoint(bMinX, bMaxY));
            float bbMinX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
            float bbMinY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
            float bbMaxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
            float bbMaxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
            if (bbMaxX <= bbMinX || bbMaxY <= bbMinY) continue;

            // Annotation /Rect (PDF stores [llx lly urx ury], but some
            // producers swap pairs — normalize both ways).
            float rx1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
            float ry1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
            float rx2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
            float ry2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
            if (rx2 <= rx1 || ry2 <= ry1) continue;

            // A = scale + translate that maps the AABB of the transformed
            // bbox onto Rect. RenderFormXObject will additionally concat
            // the form's own Matrix, so the final on-page transform is
            // A · Matrix, which by construction takes BBox → Rect.
            float sx = (rx2 - rx1) / (bbMaxX - bbMinX);
            float sy = (ry2 - ry1) / (bbMaxY - bbMinY);
            float tx = rx1 - bbMinX * sx;
            float ty = ry1 - bbMinY * sy;
            var fitMatrix = new SKMatrix(sx, 0, tx, 0, sy, ty, 0, 0, 1);

            _canvas.Save();
            try
            {
                _canvas.ClipRect(new SKRect(rx1, ry1, rx2, ry2), SKClipOperation.Intersect, _options.AntiAlias);
                _canvas.Concat(in fitMatrix);
                RenderFormXObject(appearance);
            }
            catch
            {
                // Never let one malformed annotation kill the rest of
                // the page; it's strictly an overlay on top of content
                // we've already successfully rendered.
            }
            finally
            {
                _canvas.Restore();
            }
        }
    }

    /// <summary>
    /// Resolve <paramref name="annot"/>'s normal appearance to a Form
    /// XObject stream. <c>/AP /N</c> is either:
    /// <list type="bullet">
    /// <item>a single stream — used regardless of state, or</item>
    /// <item>a dictionary keyed by state name (Off / Yes / etc.) where
    ///   <c>/AS</c> picks the active entry — Widget annotations and
    ///   appearance-stateful ones use this.</item>
    /// </list>
    /// Returns null when no usable appearance is present.
    /// </summary>
    private Pdfe.Core.Primitives.PdfStream? ResolveAppearanceN(Pdfe.Core.Document.PdfAnnotation annot)
    {
        var apObj = annot.RawDictionary.GetOptional("AP");
        if (apObj == null) return null;
        if (_page.Document.Resolve(apObj) is not Pdfe.Core.Primitives.PdfDictionary ap) return null;
        var nObj = ap.GetOptional("N");
        if (nObj == null) return null;
        var resolved = _page.Document.Resolve(nObj);

        if (resolved is Pdfe.Core.Primitives.PdfStream stream)
            return stream;

        if (resolved is Pdfe.Core.Primitives.PdfDictionary stateDict)
        {
            var stateName = annot.RawDictionary.GetNameOrNull("AS");
            if (stateName != null)
            {
                var stateObj = stateDict.GetOptional(stateName);
                if (stateObj != null &&
                    _page.Document.Resolve(stateObj) is Pdfe.Core.Primitives.PdfStream s)
                    return s;
            }
            // No /AS or unknown state — fall through to first usable entry.
            foreach (var kvp in stateDict)
            {
                if (_page.Document.Resolve(kvp.Value) is Pdfe.Core.Primitives.PdfStream s)
                    return s;
            }
        }
        return null;
    }

    /// <summary>
    /// Synthesize a minimum-viable visual for an annotation without
    /// <c>/AP /N</c>. Modeled after what Acrobat / Preview / Chrome show
    /// for interactive PDFs — a colored rectangle around the field —
    /// not a full reproduction of the field's would-be value (we don't
    /// interpret /DA + /V here; that's a substantial separate feature).
    /// Covers:
    /// <list type="bullet">
    /// <item><c>/Widget</c>: form-field highlight rectangle (background
    ///   from <c>/MK /BG</c> if present, border from <c>/MK /BC</c>
    ///   plus the <c>/BS</c> width, falling back to a neutral
    ///   light-blue field highlight similar to Acrobat's default).</item>
    /// <item><c>/Link</c>: thin border using the annotation's <c>/C</c>
    ///   color when present (links without /C are intentionally
    ///   invisible in print, matching every commercial viewer).</item>
    /// <item><c>/Square</c> / <c>/Circle</c>: stroked rectangle / ellipse
    ///   using <c>/C</c> + <c>/BS</c>.</item>
    /// </list>
    /// </summary>
    private void RenderDefaultAppearance(Pdfe.Core.Document.PdfAnnotation annot)
    {
        // PDF Y-up Rect; normalize so min < max.
        float rx1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
        float ry1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
        float rx2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
        float ry2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
        if (rx2 - rx1 < 0.5f || ry2 - ry1 < 0.5f) return;

        var rect = new SKRect(rx1, ry1, rx2, ry2);

        switch (annot.Subtype)
        {
            case Pdfe.Core.Document.PdfAnnotationSubtype.Widget:
                RenderWidgetDefault(annot, rect);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Link:
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Square:
                RenderShapeDefault(annot, rect, isEllipse: false);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Circle:
                RenderShapeDefault(annot, rect, isEllipse: true);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Highlight:
            case Pdfe.Core.Document.PdfAnnotationSubtype.Underline:
            case Pdfe.Core.Document.PdfAnnotationSubtype.Squiggly:
            case Pdfe.Core.Document.PdfAnnotationSubtype.StrikeOut:
                RenderTextMarkupDefault(annot, rect);
                break;
        }
    }

    /// <summary>
    /// Resolve and cache the AcroForm <c>/DR</c> resources dict (where
    /// the document's interactive form keeps its default fonts) plus
    /// the AcroForm <c>/DA</c> default-appearance string. Both are used
    /// when a widget annotation lacks its own <c>/AP</c> and falls back
    /// to drawing the field value through the variable-text path.
    /// Cached per-render-context so we don't re-resolve per widget.
    /// </summary>
    private Pdfe.Core.Primitives.PdfDictionary? _acroFormDr;
    private string? _acroFormDa;
    private bool _acroFormResolved;
    private void ResolveAcroFormResources()
    {
        if (_acroFormResolved) return;
        _acroFormResolved = true;
        var afObj = _page.Document.Catalog.GetOptional("AcroForm");
        if (afObj == null) return;
        if (_page.Document.Resolve(afObj) is not Pdfe.Core.Primitives.PdfDictionary af) return;
        _acroFormDa = af.GetStringOrNull("DA");
        var drObj = af.GetOptional("DR");
        if (drObj == null) return;
        _acroFormDr = _page.Document.Resolve(drObj) as Pdfe.Core.Primitives.PdfDictionary;
    }

    /// <summary>
    /// Render a default appearance for a Widget annotation that lacks
    /// <c>/AP</c>. Two distinct cases:
    ///
    /// <list type="number">
    /// <item><b>Signature widgets (<c>/FT /Sig</c>):</b> draw a visible
    ///   placeholder border so the user can see "sign here." This
    ///   matches mutool's behaviour and what every commercial viewer
    ///   does for unsigned signature fields. Color comes from
    ///   <c>/MK /BC</c> when set, falling back to a neutral border
    ///   tone that's visible against white but not jarring.</item>
    /// <item><b>Other widgets (<c>/Tx</c>, <c>/Btn</c>, <c>/Ch</c>) with
    ///   <c>/MK</c> styling:</b> render background and/or border using
    ///   the explicitly-supplied colors. Skip when no /MK is set —
    ///   text fields in unfilled forms (IRS-1040, passport renewals,
    ///   etc.) are intentionally invisible at print time and adding
    ///   our own borders here makes pdfe's output diverge from mutool
    ///   by ~10% on real-world form PDFs.</item>
    /// </list>
    /// </summary>
    private void RenderWidgetDefault(Pdfe.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        var fieldType = annot.RawDictionary.GetNameOrNull("FT");
        var mk = annot.RawDictionary.GetOptional("MK") is { } mkObj
            ? _page.Document.Resolve(mkObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;

        var bgColor = mk != null ? ParseColorArray(mk.GetOptional("BG")) : null;
        var bcColor = mk != null ? ParseColorArray(mk.GetOptional("BC")) : null;
        bool isSignature = fieldType == "Sig";
        bool hasExplicitStyle = bgColor.HasValue || bcColor.HasValue;

        // Text fields with a value /V should render the value even
        // without /AP — common in unflattened filled forms (Acrobat,
        // Foxit and mutool all do this). Pull /V and route through the
        // variable-text path before falling back to the empty-field
        // policy.
        if (fieldType == "Tx")
        {
            var rawV = annot.RawDictionary.GetOptional("V");
            string? value = rawV != null
                ? ExtractStringFromObject(_page.Document.Resolve(rawV))
                : null;
            if (!string.IsNullOrEmpty(value))
            {
                RenderTextFieldValue(annot, rect, value!);
                if (!hasExplicitStyle) return;
            }
        }

        // Only signature fields get a synthesized "sign here" placeholder
        // border. Text / button / choice widgets in unfilled forms are
        // routinely emitted without /AP and are intentionally invisible
        // until filled — mutool, Poppler and Foxit all leave them blank
        // unless the author opted into /MK styling.
        if (!isSignature && !hasExplicitStyle) return;

        float borderWidth = (float)(annot.BorderWidth ?? 1.0);
        _canvas.Save();
        try
        {
            using var paint = new SKPaint { IsAntialias = _options.AntiAlias };

            if (bgColor.HasValue)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = bgColor.Value;
                _canvas.DrawRect(rect, paint);
            }

            // Border: use /MK /BC when supplied. For signature fields
            // without /MK, fall back to a neutral medium-blue tone —
            // the goal is "user can see the field exists," not pixel
            // parity with any specific viewer.
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = borderWidth;
            paint.Color = bcColor ?? new SKColor(0x66, 0x99, 0xFF, 0xFF);
            _canvas.DrawRect(rect, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// Stroke a Square or Circle annotation outline using its /C color
    /// and /BS width. These annotations are rare without /AP — most
    /// authoring tools bake an appearance — but the few that don't
    /// fall back here.
    /// </summary>
    private void RenderShapeDefault(
        Pdfe.Core.Document.PdfAnnotation annot, SKRect rect, bool isEllipse)
    {
        if (annot.Color is not { } color) return;
        var (r, g, b) = color;
        float borderWidth = (float)(annot.BorderWidth ?? 1.0);

        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            Color = RgbToColor(r, g, b),
        };
        if (isEllipse) _canvas.DrawOval(rect, paint);
        else _canvas.DrawRect(rect, paint);
    }

    /// <summary>
    /// Render a text-markup annotation when the PDF omits /AP /N.
    /// This intentionally stays simple: exact quad geometry is already
    /// reduced to per-quad boxes by PdfAnnotationParser, which is enough
    /// for the common no-appearance highlight/comment fixtures.
    /// </summary>
    private void RenderTextMarkupDefault(
        Pdfe.Core.Document.PdfAnnotation annot, SKRect fallbackRect)
    {
        var boxes = annot.QuadPoints is { Count: > 0 }
            ? annot.QuadPoints.Select(NormalizeAnnotationRect)
            : new[] { fallbackRect };

        var baseColor = AnnotationMarkupColor(annot);
        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        _canvas.Save();
        try
        {
            foreach (var box in boxes)
            {
                if (box.Width < 0.5f || box.Height < 0.5f)
                    continue;

                switch (annot.Subtype)
                {
                    case Pdfe.Core.Document.PdfAnnotationSubtype.Highlight:
                        paint.Style = SKPaintStyle.Fill;
                        paint.BlendMode = SKBlendMode.Multiply;
                        paint.Color = WithAlpha(baseColor, AnnotationOpacityAlpha(annot));
                        var radius = Math.Min(box.Height * 0.5f, box.Width * 0.5f);
                        var highlightBox = box;
                        highlightBox.Inflate(radius, 0);
                        _canvas.DrawRoundRect(highlightBox, radius, radius, paint);
                        paint.BlendMode = SKBlendMode.SrcOver;
                        break;

                    case Pdfe.Core.Document.PdfAnnotationSubtype.Underline:
                        DrawMarkupLine(box, baseColor, box.Top + box.Height * 0.12f, paint);
                        break;

                    case Pdfe.Core.Document.PdfAnnotationSubtype.StrikeOut:
                        DrawMarkupLine(box, baseColor, box.MidY, paint);
                        break;

                    case Pdfe.Core.Document.PdfAnnotationSubtype.Squiggly:
                        DrawMarkupSquiggly(box, baseColor, paint);
                        break;
                }
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private static SKRect NormalizeAnnotationRect(Pdfe.Core.Document.PdfRectangle rect)
    {
        float rx1 = (float)Math.Min(rect.Left, rect.Right);
        float ry1 = (float)Math.Min(rect.Bottom, rect.Top);
        float rx2 = (float)Math.Max(rect.Left, rect.Right);
        float ry2 = (float)Math.Max(rect.Bottom, rect.Top);
        return new SKRect(rx1, ry1, rx2, ry2);
    }

    private void DrawMarkupLine(SKRect box, SKColor color, float y, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.BlendMode = SKBlendMode.SrcOver;
        paint.Color = WithAlpha(color, 230);
        paint.StrokeWidth = Math.Clamp(box.Height * 0.08f, 1.0f, 3.0f);
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        _canvas.DrawLine(box.Left, y, box.Right, y, paint);
    }

    private void DrawMarkupSquiggly(SKRect box, SKColor color, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.BlendMode = SKBlendMode.SrcOver;
        paint.Color = WithAlpha(color, 230);
        paint.StrokeWidth = Math.Clamp(box.Height * 0.06f, 1.0f, 2.5f);
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;

        float amplitude = Math.Clamp(box.Height * 0.08f, 1.0f, 3.0f);
        float step = Math.Max(2.0f, amplitude * 2.0f);
        float baseline = box.Top + box.Height * 0.16f;
        using var path = new SKPath();
        path.MoveTo(box.Left, baseline);

        bool up = true;
        for (float x = box.Left + step; x <= box.Right; x += step)
        {
            path.LineTo(x, baseline + (up ? amplitude : -amplitude));
            up = !up;
        }
        path.LineTo(box.Right, baseline);
        _canvas.DrawPath(path, paint);
    }

    private static SKColor AnnotationMarkupColor(Pdfe.Core.Document.PdfAnnotation annot)
    {
        if (annot.Color is { } color)
        {
            var (r, g, b) = color;
            return RgbToColor(r, g, b);
        }

        return annot.Subtype == Pdfe.Core.Document.PdfAnnotationSubtype.Highlight
            ? new SKColor(255, 255, 0)
            : SKColors.Black;
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

    private static byte AnnotationOpacityAlpha(Pdfe.Core.Document.PdfAnnotation annot)
    {
        var opacity = annot.RawDictionary.GetNumber("CA", 1.0);
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
            opacity = 1.0;
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        return (byte)Math.Round(opacity * 255.0);
    }

    /// <summary>
    /// Parse a PDF color array (1, 3, or 4 components — gray / RGB /
    /// CMYK) into an SKColor. Returns null when the value isn't a valid
    /// array of numbers.
    /// </summary>
    private SKColor? ParseColorArray(Pdfe.Core.Primitives.PdfObject? obj)
    {
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        if (resolved is not Pdfe.Core.Primitives.PdfArray arr || arr.Count == 0) return null;
        try
        {
            switch (arr.Count)
            {
                case 1:
                    return GrayToColor(arr.GetNumber(0));
                case 3:
                    return RgbToColor(arr.GetNumber(0), arr.GetNumber(1), arr.GetNumber(2));
                case 4:
                    return CmykToColor(
                        arr.GetNumber(0), arr.GetNumber(1),
                        arr.GetNumber(2), arr.GetNumber(3));
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Render the <c>/V</c> value of a text field widget that has no
    /// <c>/AP /N</c> to fall back on. Mirrors the variable-text
    /// algorithm from PDF 32000-2 §12.7.4.3:
    ///
    /// <list type="number">
    /// <item>Pick a default-appearance string — widget's <c>/DA</c> if
    ///   set, else the AcroForm-level <c>/DA</c>.</item>
    /// <item>Push the AcroForm <c>/DR</c> resources so font names in
    ///   <c>/DA</c> resolve (the widget's own /Resources are usually
    ///   empty for unfilled fields).</item>
    /// <item>Tokenize <c>/DA</c> and execute its operators against a
    ///   fresh text state — sets the active font, size, fill colour.</item>
    /// <item>Position the value text inside the rect with horizontal
    ///   alignment from <c>/Q</c> (0=left, 1=center, 2=right) and
    ///   vertical centering.</item>
    /// <item>Draw the string via the regular RenderText path so font
    ///   substitution / cmap / CID handling all share the same code.</item>
    /// </list>
    /// </summary>
    private void RenderTextFieldValue(
        Pdfe.Core.Document.PdfAnnotation annot, SKRect rect, string value)
    {
        ResolveAcroFormResources();

        var da = annot.RawDictionary.GetStringOrNull("DA") ?? _acroFormDa;
        if (string.IsNullOrEmpty(da)) return;

        // Auto-size 0 in /DA means "fit text to height" per spec; pick a
        // pragmatic default (75% of rect height, capped at 16pt) so the
        // value is at least visible. Real Acrobat does iterative fitting
        // — we approximate.
        float autoSize = Math.Min(rect.Height * 0.75f, 16f);
        if (autoSize < 4f) autoSize = 4f;

        _resourcesStack.Push(_acroFormDr);
        _canvas.Save();
        try
        {
            // Save and reset the text state so /DA's Tf / g / rg etc.
            // don't leak back into the page-level text state we've been
            // accumulating.
            var savedTextState = CloneTextState();
            var savedFillColor = _state.FillColor;
            var savedStrokeColor = _state.StrokeColor;
            var savedFont = _currentFont;
            try
            {
                _textState = new TextState();

                // Run /DA — sets _textState.FontName/FontSize, fill colour, etc.
                ExecuteContentBytes(Encoding.Latin1.GetBytes(da!));

                float fontSize = _textState.FontSize > 0.001f
                    ? _textState.FontSize : autoSize;
                // A malformed/empty /DA (no Tf) leaves _currentFont exactly
                // as it was before this method ran — possibly null (first
                // text ever on the page). Resolve a plain Helvetica fallback
                // the same way any other font resolves, rather than patching
                // a single field on an immutable ResolvedRenderFont.
                if (_currentFont?.Typeface == null)
                    _currentFont = ResolveRenderFont("Helvetica", null);

                // Measure text to compute alignment. Use the active
                // typeface so the width matches what we're about to draw.
                using var measureFont = new SKFont(_currentFont!.Typeface!, fontSize);
                using var measurePaint = new SKPaint();
                float textWidth = measureFont.MeasureText(value, measurePaint);

                int q = annot.RawDictionary.GetInt("Q", 0);
                const float padX = 2f;
                float textX;
                if (q == 1)      textX = rect.Left + (rect.Width - textWidth) * 0.5f;
                else if (q == 2) textX = rect.Right - textWidth - padX;
                else             textX = rect.Left + padX;

                // Vertical baseline: center the cap-height inside the
                // rect. fontSize × 0.3 puts the baseline below center
                // by roughly the descender's worth, which looks about
                // right for typical fonts at typical sizes.
                float textY = rect.Top + (rect.Height + fontSize * 0.7f) * 0.5f
                              - fontSize * 0.5f;

                // Drive RenderText through the standard text-block path.
                _inTextBlock = true;
                _textState.TextMatrixA = 1; _textState.TextMatrixB = 0;
                _textState.TextMatrixC = 0; _textState.TextMatrixD = 1;
                _textState.TextMatrixE = textX;
                _textState.TextMatrixF = textY;
                _textState.LineMatrixE = textX;
                _textState.LineMatrixF = textY;
                _textState.FontSize = fontSize;

                // Latin-1 round-trip into bytes — same shape as a Tj
                // operand. RenderText then handles cmap / encoding for
                // the resolved typeface.
                var bytes = Encoding.Latin1.GetBytes(value);
                RenderText(value, bytes);
                EndText();
            }
            finally
            {
                _textState = savedTextState;
                _state.FillColor = savedFillColor;
                _state.StrokeColor = savedStrokeColor;
                _currentFont = savedFont;
            }
        }
        catch
        {
            // A malformed /DA shouldn't kill the rest of the page; the
            // widget just stays unrendered.
        }
        finally
        {
            _canvas.Restore();
            _resourcesStack.Pop();
        }
    }

    /// <summary>
    /// Pull a string out of a /V or similar value object — handles both
    /// PDF string literals (most common) and PDF names (rare).
    /// </summary>
    private static string? ExtractStringFromObject(Pdfe.Core.Primitives.PdfObject? obj)
    {
        return obj switch
        {
            Pdfe.Core.Primitives.PdfString s => s.Value,
            Pdfe.Core.Primitives.PdfName n => n.Value,
            _ => null,
        };
    }

    private TextState CloneTextState()
    {
        return new TextState
        {
            FontName = _textState.FontName,
            FontSize = _textState.FontSize,
            CharSpacing = _textState.CharSpacing,
            WordSpacing = _textState.WordSpacing,
            HorizontalScale = _textState.HorizontalScale,
            TextLeading = _textState.TextLeading,
            TextRise = _textState.TextRise,
            RenderMode = _textState.RenderMode,
            TextMatrixA = _textState.TextMatrixA,
            TextMatrixB = _textState.TextMatrixB,
            TextMatrixC = _textState.TextMatrixC,
            TextMatrixD = _textState.TextMatrixD,
            TextMatrixE = _textState.TextMatrixE,
            TextMatrixF = _textState.TextMatrixF,
            LineMatrixE = _textState.LineMatrixE,
            LineMatrixF = _textState.LineMatrixF,
        };
    }
}
