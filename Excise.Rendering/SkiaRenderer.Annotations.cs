using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;

namespace Excise.Rendering;

internal partial class RenderContext
{
    private AcroFormAppearanceDefaults? _acroFormDefaults;

    private AcroFormAppearanceDefaults AcroFormDefaults
        => _acroFormDefaults ??= new AcroFormAppearanceDefaults(_page.Document);

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
        IReadOnlyList<Excise.Core.Document.PdfAnnotation> annots;
        try { annots = _page.GetAnnotations(); }
        catch { return; }
        var selections = AnnotationAppearancePolicy.SelectVisibleAnnotations(
            annots,
            _options,
            _options.Diagnostics);
        foreach (var selection in selections)
        {
            var annot = selection.Annotation;

            // §12.5.2 /CA — the annotation's constant opacity, applied to
            // stroke AND fill, and to a baked /AP appearance stream just as
            // much as to a synthesized one. It is therefore applied HERE,
            // wrapping the whole per-annotation draw, rather than inside the
            // synthesis switch: a fix in the switch would have left every
            // /AP-bearing annotation opaque, which is most real-world ones.
            //
            // A layer rather than per-paint alpha, deliberately. Multiplying
            // alpha into each paint composites stroke over fill INSIDE the
            // group, so where a border overlaps its own interior the overlap
            // reads darker than either — measured on the committed
            // annotation-property-probe, that is not what mutool or pdftocairo
            // draw. Compositing the finished annotation once is (§11.6.6, the
            // same rule an /ExtGState transparency group follows).
            var opacityLayer = BeginAnnotationOpacityLayer(annot);
            try
            {
            // #1021: the fillable-field tint, Acrobat's "Highlight Existing
            // Fields". Drawn UNDER the field's own appearance so it never
            // obscures a value, and only when asked — see the warning on
            // RenderOptions.HighlightFormFields about export paths.
            if (selection.IsFieldOrLink && _options.HighlightFormFields &&
                annot.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Widget)
            {
                DrawFieldHighlight(annot);
            }

            var appearance = AnnotationAppearancePolicy.ResolveNormalAppearance(
                annot,
                _page.Document,
                _options.Diagnostics);
            if (appearance == null)
            {
                // No baked /AP /N stream — synthesize a minimal default
                // appearance for the subtypes a majority of independent
                // renderers draw. WHICH subtypes, under WHICH conditions, and
                // on what measured evidence is not decided here or in prose:
                // it is one row per (subtype, state, condition) in
                // tests/annotation-synthesis-policy.json, re-measured by
                // AnnotationSynthesisPolicyGateTests (#993).
                // Without this, signature widgets
                // and unfilled form fields are invisible and PDFs look
                // visibly less complete than in Acrobat / Preview /
                // Chrome.
                RenderDefaultAppearance(annot);
                continue;
            }

            var plan = AnnotationAppearanceExecution.Plan(
                annot,
                appearance,
                _page.Document,
                _options.AntiAlias);
            if (plan.Diagnostic != null)
                _options.Diagnostics?.Add(plan.Diagnostic);

            if (plan.Disposition == AnnotationAppearanceDisposition.Synthesize)
            {
                RenderDefaultAppearance(annot);
                continue;
            }

            if (plan.Disposition == AnnotationAppearanceDisposition.Skip)
                continue;

            ExecuteAnnotationAppearance(plan.Request);
            }
            finally
            {
                if (opacityLayer != null)
                {
                    _canvas.Restore();
                    opacityLayer.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Applies the already-reviewed appearance geometry and executes the Form
    /// XObject through the same operator pipeline used by page content.
    /// </summary>
    private void ExecuteAnnotationAppearance(AnnotationAppearanceExecutionRequest request)
    {
        _canvas.Save();
        try
        {
            _canvas.ClipRect(
                request.ClipRect,
                SKClipOperation.Intersect,
                request.AntiAlias);
            var fitMatrix = request.FitMatrix;
            _canvas.Concat(in fitMatrix);
            RenderFormXObject(request.Appearance);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // One malformed overlay must not discard already-rendered page content.
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// VIEWER CHROME, never page content (#1021). A translucent tint over a
    /// form field's <c>/Rect</c> so a user can see what is fillable.
    /// </summary>
    /// <remarks>
    /// Translucent and drawn UNDER the field's own appearance, so a filled
    /// value stays legible — the point is to show where the fields ARE, not to
    /// repaint them. Nothing in the file asks for this, which is exactly why it
    /// is off by default and must never be enabled on an export path.
    /// </remarks>
    private void DrawFieldHighlight(Excise.Core.Document.PdfAnnotation annot)
    {
        float x1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
        float y1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
        float x2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
        float y2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
        if (x2 <= x1 || y2 <= y1) return;

        // PDF-space coordinates: the canvas already carries the page transform,
        // which is why RenderDefaultAppearance draws with raw /Rect values too.
        var rect = new SKRect(x1, y1, x2, y2);
        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Fill,
            // Acrobat's field highlight is a pale blue at low opacity.
            Color = new SKColor(0x33, 0x66, 0xCC, 0x30),
        };
        _canvas.DrawRect(rect, paint);
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
    /// <item><c>/Link</c>: border at the §12.5.6.5 / Table 168 width
    ///   (see <see cref="EffectiveLinkBorderWidth"/>), in the annotation's
    ///   <c>/C</c> colour when present and black otherwise.</item>
    /// <item><c>/Square</c> / <c>/Circle</c>: stroked rectangle / ellipse
    ///   using <c>/C</c> + <c>/BS</c>.</item>
    /// </list>
    /// </summary>
    private void RenderDefaultAppearance(Excise.Core.Document.PdfAnnotation annot)
    {
        // PDF Y-up Rect; normalize so min < max.
        float rx1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
        float ry1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
        float rx2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
        float ry2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
        // §12.5.6.4 says /Text is drawn at a FIXED size regardless of /Rect —
        // true of every OTHER reader, which draws a small icon (mutool 495
        // inked px, pdftocairo 917, Ghostscript 1388 on veraPDF
        // 6-3-3-t01-pass-a.pdf's degenerate /Rect [50 110 50 110]). excise's
        // OWN viewer deliberately deviates (#1794): RenderStickyNoteDefault
        // draws a real post-it-sized card with the note's /Contents wrapped
        // and visible on it. See issue #1795 for generating a real /AP so
        // other readers see the post-it look too; until then they still fall
        // back to the small icon §12.5.6.4 describes, because no /AP exists.
        //
        // #1797: the card is drawn at the linked /Popup's OWN /Rect when one
        // is present, NOT annot.Rect — annot.Rect is the note's true anchor
        // location and must never visibly move just because the card was
        // dragged elsewhere (§12.5.6.14 gives /Popup its own independent
        // /Rect for exactly this). A note with no popup (a foreign producer's
        // file, or one authored before #1797) falls back to its own /Rect,
        // unchanged from #1794's behavior.
        if (annot.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Text)
        {
            if (annot.PopupRect is { } popupRect)
            {
                rx1 = (float)Math.Min(popupRect.Left, popupRect.Right);
                ry1 = (float)Math.Min(popupRect.Bottom, popupRect.Top);
                rx2 = (float)Math.Max(popupRect.Left, popupRect.Right);
                ry2 = (float)Math.Max(popupRect.Bottom, popupRect.Top);
            }

            // A rect SMALLER than the fixed icon size — a foreign producer's
            // genuinely degenerate/zero rect, or a pre-#1794 icon-sized note
            // — is still normalised up to it: shrinking a post-it card that
            // small would draw nothing legible, and the icon-size floor is
            // what the viewer's own hit-test (PdfViewerControl.ContainsPoint)
            // clamps to for the same reason, so the two must not drift.
            const float noteSize = (float)Excise.Core.Document.PdfAnnotation.TextIconSize;
            if (rx2 - rx1 < noteSize || ry2 - ry1 < noteSize)
            {
                rx2 = rx1 + noteSize;
                ry1 = ry2 - noteSize;
            }
        }

        if (rx2 - rx1 < 0.5f || ry2 - ry1 < 0.5f) return;

        var rect = new SKRect(rx1, ry1, rx2, ry2);

        switch (AnnotationAppearancePolicy.SelectSynthesis(annot.Subtype))
        {
            case AnnotationSynthesisKind.Widget:
                RenderWidgetDefault(annot, rect);
                break;
            case AnnotationSynthesisKind.Link:
                RenderLinkDefault(annot, rect);
                break;
            case AnnotationSynthesisKind.StickyNote:
                RenderStickyNoteDefault(annot, rect);
                break;
            case AnnotationSynthesisKind.Square:
                RenderShapeDefault(annot, rect, isEllipse: false);
                break;
            case AnnotationSynthesisKind.Circle:
                RenderShapeDefault(annot, rect, isEllipse: true);
                break;
            case AnnotationSynthesisKind.FreeText:
                RenderFreeTextDefault(annot, rect);
                break;
            case AnnotationSynthesisKind.TextMarkup:
                RenderTextMarkupDefault(annot, rect);
                break;

            // Geometry annotations (#885). These declare their shape
            // explicitly — /L, /Vertices, /InkList — and PdfAnnotationParser
            // already extracts all three into LineEndpoints, Vertices and
            // InkStrokes. The geometry was sitting there unused: the renderer
            // simply had no case for these subtypes, so the page came out
            // blank while mutool and pdftocairo drew the shape.
            //
            // Nothing is being invented here. Unlike FreeText (which needs text
            // layout) or Stamp/Text (which need viewer-specific icon art), the
            // annotation states exactly what to draw, so synthesising it is
            // reading the file rather than guessing at it.
            case AnnotationSynthesisKind.Line:
                RenderLineDefault(annot);
                break;
            case AnnotationSynthesisKind.Polygon:
                RenderVertexShapeDefault(annot, close: true);
                break;
            case AnnotationSynthesisKind.PolyLine:
                RenderVertexShapeDefault(annot, close: false);
                break;
            case AnnotationSynthesisKind.Ink:
                RenderInkDefault(annot);
                break;
            case AnnotationSynthesisKind.None:
                break;
        }
    }

    /// <summary>
    /// Stroke paint shared by the geometry annotations, built from the
    /// annotation's own /C colour and /BS width.
    /// </summary>
    /// <remarks>
    /// Returns null when /C is absent. That is deliberate: with no colour the
    /// spec gives nothing to draw with, and picking one would be inventing
    /// content — the failure mode #878 exists to prevent. Viewers differ here
    /// and none of the corpus fixtures omit /C.
    /// </remarks>
    private SKPaint? CreateAnnotationStrokePaint(Excise.Core.Document.PdfAnnotation annot)
    {
        if (annot.Color is not { } color) return null;
        var (r, g, b) = color;
        // A zero or missing /BS /W means "no border" for some subtypes, but for
        // Line/Polygon/Ink it is routinely absent and 1.0 is the spec default.
        var width = (float)(annot.BorderWidth ?? 1.0);
        if (width <= 0) width = 1.0f;

        var dash = CreateBorderDashEffect(annot);
        return new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            // A dashed border wants BUTT ends: round caps bleed each dash into
            // the gap either side, which at a 3pt width closes a [6 3] pattern
            // into a solid line and undoes the thing being drawn.
            StrokeCap = dash != null ? SKStrokeCap.Butt : SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = RgbToColor(r, g, b),
            PathEffect = dash,
        };
    }

    /// <summary>
    /// The <c>/BS</c> dash pattern (§12.5.4 Table 166), or null for a solid
    /// border. #1073: <c>/S /D</c> rendered solid — excise honoured <c>/W</c>
    /// and ignored <c>/S</c> and <c>/D</c> entirely, so a dashed callout box
    /// came out as a solid one.
    ///
    /// <para>The array is in POINTS, which is also this canvas's unit, so it
    /// needs no scaling — the canvas carries the dpi transform.</para>
    /// </summary>
    private static SKPathEffect? CreateBorderDashEffect(Excise.Core.Document.PdfAnnotation annot)
    {
        if (!string.Equals(annot.BorderStyle, "D", StringComparison.Ordinal)) return null;

        var pattern = annot.BorderDashPattern;
        // §12.5.4: an omitted /D defaults to [3]. A single value means equal
        // dash and gap, which Skia needs spelled out as two.
        var dashes = pattern is { Count: > 0 }
            ? pattern.Select(d => (float)d).Where(d => d >= 0).ToArray()
            : new[] { 3f };
        if (dashes.Length == 0) return null;
        if (dashes.Length == 1) dashes = new[] { dashes[0], dashes[0] };
        // Skia requires an even count; drop a trailing odd entry rather than
        // throwing on a file that states one.
        if (dashes.Length % 2 != 0) dashes = dashes[..^1];
        if (dashes.Length == 0 || dashes.All(d => d <= 0)) return null;

        // CACHED, not created per call. Callers dispose the SKPaint, and
        // SKPaint.Dispose does NOT dispose its PathEffect — creating one per
        // annotation would leak a native object on every page render. Dash
        // patterns repeat heavily within a document, so the cache stays tiny.
        var key = string.Join(',', dashes);
        return DashEffects.GetOrAdd(key, _ => SKPathEffect.CreateDash(dashes, 0f));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKPathEffect>
        DashEffects = new();

    /// <summary>Line annotation (§12.5.6.7): draw /L as a segment.</summary>
    private void RenderLineDefault(Excise.Core.Document.PdfAnnotation annot)
    {
        if (annot.LineEndpoints is not { } l) return;
        using var paint = CreateAnnotationStrokePaint(annot);
        if (paint == null) return;
        _canvas.DrawLine((float)l.X1, (float)l.Y1, (float)l.X2, (float)l.Y2, paint);
    }

    /// <summary>
    /// Polygon (<paramref name="close"/> true) and PolyLine (false), §12.5.6.9.
    ///
    /// <para>#1069: only a Polygon has an INTERIOR, and only a Polygon fills it
    /// with <c>/IC</c>. excise stroked and never filled, so a Polygon over page
    /// content showed the content through — a shape that obscures in every
    /// other viewer was transparent here. Same defect shape as #1055 for
    /// Square/Circle, and the same fix order: interior first, stroke over it
    /// (§12.5.6.8 Table 178).</para>
    ///
    /// <para><b>An /IC with no /C still draws.</b> Measured at 150 dpi on a
    /// Polygon carrying /IC and no /C: mutool 42,777 inked px, pdftocairo
    /// 44,102, excise 0. Both fill it. This does NOT contradict #889 and
    /// <c>ShapeAnnotationSynthesisTests.ShapeWithoutColor_DrawsNothing</c>,
    /// which pins the case where there is NO colour of any kind — with neither
    /// /C nor /IC there is genuinely nothing to draw with, and mutool agrees.
    /// Here the file states an interior colour and excise was ignoring it.</para>
    ///
    /// <para>⚠️ The issue also claimed the path was not closed. It was:
    /// <c>path.Close()</c> was already here and the stroked outline matches both
    /// oracles edge for edge. That half of #1069 was my misreading of a
    /// low-resolution render, re-measured before implementing rather than
    /// "fixed".</para>
    /// </summary>
    private void RenderVertexShapeDefault(Excise.Core.Document.PdfAnnotation annot, bool close)
    {
        if (annot.Vertices is not { Count: >= 2 } verts) return;

        using var path = new SKPath();
        path.MoveTo((float)verts[0].X, (float)verts[0].Y);
        for (int i = 1; i < verts.Count; i++)
            path.LineTo((float)verts[i].X, (float)verts[i].Y);
        if (close) path.Close();

        // A PolyLine is an open path and has no interior to fill; /IC is a
        // Polygon entry (§12.5.6.9 Table 179).
        if (close && annot.InteriorColor is { } interior)
        {
            var (ir, ig, ib) = interior;
            using var fill = new SKPaint
            {
                IsAntialias = _options.AntiAlias,
                Style = SKPaintStyle.Fill,
                Color = RgbToColor(ir, ig, ib),
            };
            _canvas.DrawPath(path, fill);
        }

        using var paint = CreateAnnotationStrokePaint(annot);
        if (paint == null) return;
        _canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Ink annotation (§12.5.6.13): /InkList is a list of strokes, each a flat
    /// list of points. Drawn as polylines rather than smoothed curves — the
    /// spec describes interpolation as producer-defined, and a polyline through
    /// the declared points is the reading that invents least.
    /// </summary>
    private void RenderInkDefault(Excise.Core.Document.PdfAnnotation annot)
    {
        if (annot.InkStrokes is not { Count: > 0 } strokes) return;
        using var paint = CreateAnnotationStrokePaint(annot);
        if (paint == null) return;

        foreach (var stroke in strokes)
        {
            if (stroke.Count == 0) continue;
            if (stroke.Count == 1)
            {
                // A single point still marks the page in every viewer that
                // renders Ink at all; a zero-length polyline would not.
                _canvas.DrawPoint((float)stroke[0].X, (float)stroke[0].Y, paint);
                continue;
            }
            using var path = new SKPath();
            path.MoveTo((float)stroke[0].X, (float)stroke[0].Y);
            for (int i = 1; i < stroke.Count; i++)
                path.LineTo((float)stroke[i].X, (float)stroke[i].Y);
            _canvas.DrawPath(path, paint);
        }
    }

    /// <summary>
    /// Render a default appearance for a Widget annotation that lacks
    /// <c>/AP</c>. Two distinct cases:
    ///
    /// <list type="number">
    /// <item><b>Signature widgets (<c>/FT /Sig</c>) with no <c>/MK</c>:</b>
    ///   nothing. #885 drew a blue "sign here" placeholder border on the
    ///   whole /Rect; #1005 measured it and removed it. Of the three engines
    ///   that vote on a Widget row (pdfbox and pdfium abstain structurally),
    ///   poppler and Ghostscript draw NOTHING for an unsigned /FT /Sig, and
    ///   the one that draws — mutool — draws a 23x5 mark in the field's
    ///   top-left corner, not a border. So the placeholder both elected an
    ///   outlier and drew something the outlier does not draw. A signature
    ///   field with /MK styling still gets that styling, by the rule below.
    ///   If the GUI wants unsigned fields flagged, that is an editor overlay,
    ///   not ink in the rendered page.</item>
    /// <item><b>Other widgets (<c>/Tx</c>, <c>/Btn</c>, <c>/Ch</c>) with
    ///   <c>/MK</c> styling:</b> render background and/or border using
    ///   the explicitly-supplied colors. Skip when no /MK is set —
    ///   text fields in unfilled forms (IRS-1040, passport renewals,
    ///   etc.) are intentionally invisible at print time and adding
    ///   our own borders here makes excise's output diverge from mutool
    ///   by ~10% on real-world form PDFs.</item>
    /// <item><b>Checkboxes whose <c>/AS</c> is on:</b> a check mark, and no
    ///   box (#972). See the measurement table at the call site — the
    ///   chrome half of this rule and the check-mark half were confused
    ///   for each other once and the comment there says how.</item>
    /// </list>
    /// </summary>
    private void RenderWidgetDefault(Excise.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        var fieldType = annot.RawDictionary.GetNameOrNull("FT");
        var mk = annot.RawDictionary.GetOptional("MK") is { } mkObj
            ? _page.Document.Resolve(mkObj) as Excise.Core.Primitives.PdfDictionary
            : null;

        var bgColor = mk != null ? ParseColorArray(mk.GetOptional("BG")) : null;
        var bcColor = mk != null ? ParseColorArray(mk.GetOptional("BC")) : null;
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

        // WHAT A /FT /Btn WITH NO /AP GETS (#972), and what the previous
        // answer got wrong.
        //
        // This block used to draw an unconditional blue BOX for every button,
        // justified by "measured on pdf.js checkbox_no_appearance.pdf — mutool
        // 233 inked px, pdftocairo 229, excise 0". The measurement was real and
        // the conclusion drawn from it was not: those 233/229 pixels are a
        // CHECK MARK, not a box. Neither renderer draws any box at all. So
        // excise ended up inking a rectangle nobody else draws while still
        // missing the only thing they do draw — the majority-scored corpus gate
        // (#932) reads that as 12 tiles missing and 50 tiles invented.
        //
        // Re-measured at 72 dpi over one synthesized /FT /Btn widget per case,
        // /Rect [50 50 100 100], no /AP anywhere (inked px; Ghostscript draws
        // nothing in any of these and is omitted):
        //
        //   case                      mutool  pdftocairo  pdftoppm
        //   /AS on   (V=Yes AS=Yes)      322         320       320   <- a check
        //   /AS off  (V=Off AS=Off)        0           0         0
        //   /V on but NO /AS               0           0         0
        //   pushbutton (Ff bit 17)         0           0         0
        //   radio on   (Ff bit 16)       468           0         0
        //
        // Three rules follow, and each is what a MAJORITY does:
        //   * an ON checkbox draws a check mark — corroborated 3/3;
        //   * an OFF checkbox draws NOTHING, no box — corroborated 3/3;
        //   * the state comes from /AS ALONE. A /V of /Yes with no /AS draws
        //     nothing anywhere, which matches the normal-appearance policy
        //     already refuses to guess a state.
        // Radio is deliberately NOT implemented: one renderer of three draws a
        // dot and the other two draw nothing, so implementing it means electing
        // an outlier — the #875 trap that #889 exists to avoid. Filed
        // separately. Pushbutton draws nothing anywhere, so it stays blank.
        //
        // The /MK chrome below is unchanged and still applies to a button that
        // carries one (mutool honours /MK here; Poppler ignores it and draws
        // only the check).
        //
        // A /FT /Sig with no /MK gets NOTHING (#1005). It used to reach the
        // block below through an `isSignature ||`, which stroked a blue
        // placeholder rectangle over the whole /Rect on the strength of one
        // engine of three — and that engine draws a 23x5 corner mark, not a
        // border. Row widget.sig.unsigned in the policy table carries the
        // measurement; #885's "sign here" rationale is reversed there.
        bool isCheckbox = fieldType == "Btn" && !IsPushButtonWidget(annot) && !IsRadioWidget(annot);

        if (hasExplicitStyle)
        {
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

                // Border: ONLY when /MK /BC states one. A /MK that sets a
                // background and no border colour used to get the same
                // invented medium-blue stroke the signature placeholder got,
                // and it is invented for the same reason: measured at 72 dpi
                // on /MK << /BG [1 1 0] >> alone (row widget.mk.bg-only),
                // mutool, pdftocairo, pdftoppm and Ghostscript each ink
                // exactly the 50x50 fill and no border at all.
                if (bcColor.HasValue)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = borderWidth;
                    paint.Color = bcColor.Value;
                    _canvas.DrawRect(rect, paint);
                }
            }
            finally
            {
                _canvas.Restore();
            }
        }

        if (isCheckbox && IsWidgetAppearanceStateOn(annot))
            DrawSynthesizedCheckMark(rect);
    }

    /// <summary>
    /// True when the widget's <c>/AS</c> names an ON state — present and not
    /// <c>Off</c>. Absent <c>/AS</c> is NOT treated as on: a checkbox whose
    /// only evidence is <c>/V /Yes</c> draws nothing in mutool, pdftocairo or
    /// pdftoppm, and guessing a state is what put a tick in every empty box of
    /// a blank IRS W-9 once already (see <see cref="AnnotationAppearancePolicy"/>).
    /// </summary>
    private static bool IsWidgetAppearanceStateOn(Excise.Core.Document.PdfAnnotation annot)
    {
        var state = annot.RawDictionary.GetNameOrNull("AS");
        return state != null && state != "Off";
    }

    /// <summary>Field flag bit 16 (value 0x8000) — a radio group, §12.7.4.2.</summary>
    private bool IsRadioWidget(Excise.Core.Document.PdfAnnotation annot)
        => (GetInheritedFieldFlags(annot) & 0x8000) != 0;

    /// <summary>Field flag bit 17 (value 0x10000) — a push button, §12.7.4.2.</summary>
    private bool IsPushButtonWidget(Excise.Core.Document.PdfAnnotation annot)
        => (GetInheritedFieldFlags(annot) & 0x10000) != 0;

    /// <summary>
    /// <c>/Ff</c> from the widget, or from the nearest ancestor field that
    /// states one (§12.7.4.2 makes /Ff inheritable, and a widget merged into
    /// its field commonly carries it on the parent). The walk is depth-capped
    /// so a /Parent cycle cannot hang the renderer.
    /// </summary>
    private int GetInheritedFieldFlags(Excise.Core.Document.PdfAnnotation annot)
    {
        var dict = annot.RawDictionary;
        for (int depth = 0; depth < 32 && dict != null; depth++)
        {
            if (dict.GetOptional("Ff") is { } ffObj
                && _page.Document.Resolve(ffObj) is Excise.Core.Primitives.PdfInteger ff)
                return (int)ff.Value;

            var parent = dict.GetOptional("Parent");
            dict = parent == null
                ? null
                : _page.Document.Resolve(parent) as Excise.Core.Primitives.PdfDictionary;
        }
        return 0;
    }

    /// <summary>
    /// The check mark an ON checkbox with no <c>/AP</c> gets. Drawn as a
    /// stroked polyline rather than a ZapfDingbats glyph so it does not depend
    /// on a font being installed — the shape is what the oracles agree on, and
    /// their own two check marks differ from each other by 5 px vertically
    /// (mutool's bbox on the reference fixture is (58,105)-(91,140),
    /// pdftocairo's (58,110)-(91,145) over the same (50,100)-(100,150) rect).
    /// This aims at the middle of that, not at either one: excise is not
    /// chasing pixel parity, only drawing the mark the majority draws where
    /// they draw it.
    /// </summary>
    private void DrawSynthesizedCheckMark(SKRect rect)
    {
        float side = Math.Min(rect.Width, rect.Height);
        if (side <= 0) return;

        // Both oracles inset the glyph to about two thirds of the box.
        float inset = side * 0.19f;
        var box = new SKRect(
            rect.Left + inset, rect.Top + inset,
            rect.Right - inset, rect.Bottom - inset);

        // `box` is in PDF space (Y UP, see RenderDefaultAppearance), so
        // box.Top is the visually LOWER edge. Building the polyline in screen
        // sense here draws a caret instead of a tick, which is exactly what
        // the first version of this did.
        using var path = new SKPath();
        path.MoveTo(box.Left, box.Bottom - box.Height * 0.55f);
        path.LineTo(box.Left + box.Width * 0.35f, box.Top);
        path.LineTo(box.Right, box.Bottom);

        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, side * 0.11f),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = SKColors.Black,
        };
        _canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Stroke a Square or Circle annotation outline using its /C color
    /// and /BS width. These annotations are rare without /AP — most
    /// authoring tools bake an appearance — but the few that don't
    /// fall back here.
    /// </summary>
    /// <summary>
    /// Appearance for a /FreeText note that ships no /AP (§12.5.6.6).
    ///
    /// <para>⚠️ This method used to draw the BOX ONLY and deliberately skip the
    /// note's text, on the reasoning that "the oracles disagree sharply about
    /// it ... so there is no agreed answer to copy". That measurement was real
    /// but OVER-GENERALISED from two fixtures. Re-measured across three cases,
    /// the oracles agree in two of them and only diverge in the third:</para>
    ///
    /// <list type="number">
    ///   <item><b>No /DA</b> — pdfium <c>freetext_annotation_without_da.pdf</c>
    ///     (/C present, /Rect 50x25): mutool 1250 px, pdftocairo 1250 px, i.e.
    ///     exactly the whole rectangle. Both FILL with /C and draw neither
    ///     border nor text. Unchanged here.</item>
    ///   <item><b>Simple /DA + single-line /Contents</b> — the checked-in
    ///     <c>annotation-property-probe</c> / <c>visible-annotation-demo</c>
    ///     fixtures: both oracles fill with /C, STROKE A BORDER IN THE /DA
    ///     COLOUR, and draw the text. Sampled at 200 dpi, the border is exactly
    ///     (0,0,255) for <c>/DA (0 0 1 rg ...)</c> and the interior exactly
    ///     (242,242,204) for <c>/C [0.95 0.95 0.8]</c> in BOTH. That is an
    ///     agreement, and this method now follows it.</item>
    ///   <item><b>Multi-line / complex script</b> — pdf.js
    ///     <c>freetext_no_appearance.pdf</c> (multi-line RTL Arabic in a
    ///     UTF-16BE /Contents). At 150 dpi mutool inks 20,152 px and pdfium
    ///     4,926, both clipped to /Rect; Ghostscript inks 21,318 and overflows
    ///     it; poppler inks 63. That is 3 of 4 engines drawing. Since #1363 such
    ///     /Contents are shaped and typeset over every line
    ///     (<see cref="TypesetFreeTextContents"/>). Contents that need no shaping
    ///     still draw only their first line.</item>
    /// </list>
    ///
    /// <para>Why it matters that the text appears at all: a FreeText is the one
    /// markup subtype whose content is meant to be legible on the page without
    /// opening a popup. Drawing the box and not the text renders it as an empty
    /// coloured rectangle — the reader can see something is there and cannot
    /// read it, which is worse than not drawing it (#1070).</para>
    ///
    /// <para>The text goes through <see cref="RenderTextFieldValue"/> — the same
    /// /DA execution, font resolution, §12.7.4.3 auto-size, /Q alignment and
    /// /Rect clipping the widget path already uses — with the AcroForm /DA
    /// fallback turned OFF, because a FreeText's /DA is its own (§12.5.6.6).</para>
    /// </summary>
    private void RenderFreeTextDefault(Excise.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        // A STATED width of 0 means no border (§12.5.4: /Border is [h v w], and
        // w is the width). Forcing it to 1 drew a box the file explicitly
        // declined — measured on pdf.js bug1871353.pdf, whose FreeText carries
        // /Border [0 0 0] and no /C: the oracle majority inks 6 tiles (its two
        // glyphs and nothing else) where excise inked 34, 31 of them extra.
        // Only an ABSENT width defaults to 1.
        var statedWidth = annot.BorderWidth;
        var drawBorder = statedWidth is null || statedWidth.Value > 0;
        float borderWidth = drawBorder ? (float)(statedWidth ?? 1.0) : 0f;
        if (borderWidth <= 0) borderWidth = 1.0f;

        // For a FreeText, /C is the BACKGROUND (§12.5.6.6) — unlike most
        // subtypes, where it is the border colour.
        using var paint = new SKPaint { IsAntialias = _options.AntiAlias };
        if (annot.Color is { } color)
        {
            var (r, g, b) = color;
            paint.Style = SKPaintStyle.Fill;
            paint.Color = RgbToColor(r, g, b);
            _canvas.DrawRect(rect, paint);
        }

        var da = annot.RawDictionary.GetStringOrNull("DA");
        if (string.IsNullOrWhiteSpace(da))
        {
            // Case 1. With no /DA there is nothing to style text with and both
            // oracles draw neither border nor text — so a /C-only annotation
            // stays a plain filled rectangle, and one with no /C at all still
            // gets an outline so it is not invisible.
            if (annot.Color is null && drawBorder)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = borderWidth;
                paint.Color = SKColors.Black;
                _canvas.DrawRect(rect, paint);
            }
            return;
        }

        // Border, in the /DA colour — measured, see the table above.
        if (drawBorder)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = borderWidth;
            paint.Color = DefaultAppearanceColor(da!) ?? SKColors.Black;
            _canvas.DrawRect(rect, paint);
        }

        var contents = annot.RawDictionary.GetStringOrNull("Contents");
        if (string.IsNullOrEmpty(contents)) return;

        // #1363: a /Contents that needs complex-script shaping (Arabic joining,
        // Hebrew and Arabic RTL, Indic reordering) goes through the typesetter.
        // Every line is shaped with HarfBuzz, with font fallback per run, then
        // wrapped to /Rect and clipped to it (policy row
        // freetext.complex-script-contents).
        //
        // The test runs on the WHOLE string, not only the first line, so a
        // Latin first line over an Arabic second line is typeset as one block
        // instead of silently dropping the Arabic.
        //
        // Why this branch used to draw nothing. Measured on pdf.js
        // freetext_no_appearance.pdf (/Border [0 0 0], no /C, 14 lines of RTL
        // Arabic) at 150 dpi: mutool 20,152 inked px clipped to /Rect,
        // Ghostscript 21,318 overflowing /Rect by 133 px, pdfium 4,926 inside
        // /Rect, poppler 63, excise 0. An early cut drew a row of tofu, which is
        // worse than the empty box it replaced: an empty box reads as "an
        // annotation is here", tofu reads as "this document is corrupt". So
        // the typesetter still FAILS CLOSED. With no covering font, a .notdef
        // from the shaper, or no native HarfBuzz, nothing is drawn.
        //
        // ⚠️ #1381 is why this guard no longer tests `ch > 0xFF`. With
        // /Border [0 0 0] the border is legitimately suppressed above, so
        // suppressing the text too made the annotation vanish ENTIRELY. On
        // pdf.js bug1865341.pdf ("Załącznik") one Polish ł removed the whole
        // annotation. Latin Extended, Greek, Cyrillic and CJK need no shaping
        // and stay on the single-line path below, whose layout is unchanged.
        if (RequiresComplexShaping(contents!))
        {
            TypesetFreeTextContents(annot, rect, contents!);
            return;
        }

        // Single-line path. Only the first line is drawn: multi-line layout
        // for scripts that need no shaping has not been measured against the
        // oracles, and #1363 changed only the complex-script branch.
        var firstLine = contents!.Split('\r', '\n')[0];
        if (firstLine.Length == 0) return;

        RenderTextFieldValue(annot, rect, firstLine,
            useAcroFormDaFallback: false, topAlign: true);
    }

    /// <summary>
    /// Leading for the complex-script FreeText path. mutool's trace of
    /// freetext_no_appearance.pdf steps baselines 12 units for
    /// <c>/DA (/Helv 10 Tf)</c>.
    /// </summary>
    private const float ComplexScriptLeading = 1.2f;

    /// <summary>
    /// Size used for <c>0 Tf</c> on the complex-script path. §12.7.4.3's
    /// auto-size fits ONE line to the field, which has no defined meaning for
    /// a wrapped block, so this is a fixed default rather than a fit.
    /// </summary>
    private const float ComplexScriptAutoSize = 12f;

    /// <summary>
    /// Draws a FreeText <c>/Contents</c> that needs complex-script shaping
    /// (#1363), using <see cref="Fonts.AnnotationTypesetter"/>.
    /// </summary>
    /// <remarks>
    /// <para>The setup matches <see cref="RenderTextFieldValue"/>. The AcroForm
    /// <c>/DR</c> is pushed so a <c>/DA</c> font name resolves. <c>/DA</c> runs
    /// against a fresh text state, and the text state, colours and font are
    /// restored afterwards. Everything is clipped to <c>/Rect</c>. The glyphs do
    /// NOT go through <c>RenderText</c>: it lays out one code point per glyph,
    /// and shaped glyph IDs from a fallback face have no PDF encoding behind
    /// them.</para>
    ///
    /// <para>Placement follows mutool's trace of the same fixture. Leading is
    /// <see cref="ComplexScriptLeading"/>. Lines honour <c>/Q</c> as written, so
    /// an RTL paragraph under <c>/Q 0</c> is LEFT-aligned (ragged right), which
    /// is how mutool draws it. The first baseline uses the single-line path's
    /// top-aligned rule.</para>
    ///
    /// <para>A negative <c>/DA</c> size (a mirrored block) is not handled on
    /// this path and draws nothing, with a diagnostic.</para>
    ///
    /// <para>Render-only. Nothing is written back to the annotation, so no
    /// synthesised <c>/AP</c> can reach a saved file as a new text
    /// carrier.</para>
    /// </remarks>
    private void TypesetFreeTextContents(Excise.Core.Document.PdfAnnotation annot, SKRect rect, string contents)
    {
        if (IsOptionalContentSuppressed || _type3ClipOnlyPass)
            return;

        var da = annot.RawDictionary.GetStringOrNull("DA") ?? "";
        _resourcesStack.Push(AcroFormDefaults.Resources);
        _canvas.Save();
        var savedTextState = CloneTextState();
        var savedFillColor = _state.FillColor;
        var savedStrokeColor = _state.StrokeColor;
        var savedFont = _currentFont;
        try
        {
            _canvas.ClipRect(rect, SKClipOperation.Intersect, _options.AntiAlias);

            _textState = new TextState();
            ExecuteContentBytes(Encoding.Latin1.GetBytes(da));
            if (_currentFont?.Typeface == null)
                _currentFont = ResolveRenderFont("Helvetica", null);
            var primary = _currentFont?.Typeface;
            if (primary == null)
                return;

            float statedSize = _textState.FontSize;
            if (statedSize < 0f)
            {
                AddDiagnostic("FreeText /Contents not drawn: a negative /DA font size is not " +
                              "supported on the complex-script path (#1363).");
                return;
            }

            float fontSize = statedSize > 0.001f ? statedSize : ComplexScriptAutoSize;
            var mode = _textState.RenderMode;
            bool fill = TextRenderModeFills(mode);
            bool stroke = TextRenderModeStrokes(mode);
            if (!fill && !stroke)
                return;

            const float pad = 2f;
            IReadOnlyList<Fonts.AnnotationTypesetter.TypesetLine>? lines;
            string? failure;

            // The whole typeset runs under the process-wide typeface lock:
            // SKShaper opens the typeface's font data and the fallback asks the
            // font manager, and neither is safe under concurrent typeface work.
            lock (_typefaceLoadLock)
            {
                Func<int, SKTypeface?> fallback;
                if (_options.DisableSystemFontFallback)
                {
                    fallback = static _ => null;
                }
                else
                {
                    var family = primary.FamilyName;
                    fallback = codepoint => SKFontManager.Default.MatchCharacter(family, codepoint);
                }

                lines = Fonts.AnnotationTypesetter.Typeset(
                    contents, primary, fontSize, Math.Max(rect.Width - 2f * pad, 1f), fallback, out failure);
            }

            if (lines == null)
            {
                AddDiagnostic($"FreeText /Contents not drawn: {failure} (#1363).");
                return;
            }

            using var fillPaint = fill ? CreateTextPaint(SKPaintStyle.Fill, _state.FillColor, _state.FillAlpha) : null;
            using var strokePaint = stroke ? CreateTextPaint(SKPaintStyle.Stroke, _state.StrokeColor, _state.StrokeAlpha) : null;

            int q = annot.RawDictionary.GetInt("Q", 0);
            float baseline = BaselineForTopAlignedLineBox(rect, primary, fontSize, pad);
            float leading = fontSize * ComplexScriptLeading;
            foreach (var line in lines)
            {
                // rect.Top is the LOW edge in this Y-up space. Once a line's em
                // box is wholly below it, it and every later line are clipped.
                if (baseline + fontSize < rect.Top)
                    break;

                if (line.Runs.Count > 0)
                {
                    float x = q switch
                    {
                        1 => rect.Left + (rect.Width - line.Width) * 0.5f,
                        2 => rect.Right - pad - line.Width,
                        _ => rect.Left + pad,
                    };
                    DrawTypesetLine(line, x, baseline, fontSize, fillPaint, strokePaint);
                }

                baseline -= leading;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A malformed /DA or a shaping failure must not kill the page.
            AddDiagnostic($"FreeText /Contents not drawn: {ex.GetType().Name}: {ex.Message} (#1363).");
        }
        finally
        {
            _textState = savedTextState;
            _state.FillColor = savedFillColor;
            _state.StrokeColor = savedStrokeColor;
            _currentFont = savedFont;
            _canvas.Restore();
            _resourcesStack.Pop();
        }
    }

    /// <summary>
    /// Fills (and strokes) one typeset line from its glyph OUTLINES, through
    /// the current soft mask, as <see cref="FillTextUsingGlyphPath"/> does for
    /// page text (#710). A run whose face has no outlines (bitmap-only) is drawn
    /// as a positioned glyph blob instead.
    /// </summary>
    private void DrawTypesetLine(
        Fonts.AnnotationTypesetter.TypesetLine line, float x, float baseline, float fontSize,
        SKPaint? fillPaint, SKPaint? strokePaint)
    {
        _canvas.Save();
        try
        {
            // The canvas is PDF space with Y up. Glyph outlines and HarfBuzz
            // offsets are Y down, so flip about the baseline.
            _canvas.Translate(x, baseline);
            _canvas.Scale(1f, -1f);

            foreach (var run in line.Runs)
            {
                using var font = CreateTextFont(run.Typeface, fontSize);
                using var path = new SKPath();
                for (int g = 0; g < run.Glyphs.Length; g++)
                {
                    using var glyphPath = font.GetGlyphPath(run.Glyphs[g]);
                    if (glyphPath != null && !glyphPath.IsEmpty)
                        path.AddPath(glyphPath, run.Positions[g].X, run.Positions[g].Y, SKPathAddMode.Append);
                }

                if (!path.IsEmpty)
                {
                    if (fillPaint != null)
                        RenderWithCurrentSoftMask(() => _canvas.DrawPath(path, fillPaint), fillPaint);
                    if (strokePaint != null)
                        RenderWithCurrentSoftMask(() => _canvas.DrawPath(path, strokePaint), strokePaint);
                    continue;
                }

                using var blob = BuildPositionedGlyphBlob(run.Glyphs, run.Positions, font);
                if (blob == null)
                    continue;
                if (fillPaint != null)
                    RenderWithCurrentSoftMask(() => _canvas.DrawText(blob, 0, 0, fillPaint), fillPaint);
                if (strokePaint != null)
                    RenderWithCurrentSoftMask(() => _canvas.DrawText(blob, 0, 0, strokePaint), strokePaint);
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// Whether a string contains any character from a script whose correct
    /// display needs a shaping engine — cursive joining, reordering, or
    /// mandatory ligature formation — rather than one glyph laid out per code
    /// point (#1381).
    /// </summary>
    /// <remarks>
    /// This is the narrow replacement for a blanket "any code point above
    /// U+00FF" test. It is deliberately a SCRIPT question, not a coverage
    /// question: a font that lacks a glyph draws .notdef, which is a visible,
    /// honest "there is text here that I cannot show"; an unshaped Arabic run
    /// draws plausible-looking WRONG text, which is not.
    ///
    /// The ranges are the shaping-required blocks, and nothing else: Hebrew and
    /// Arabic (plus Syriac, Thaana, N'Ko and the Arabic presentation forms),
    /// the Indic and Brahmi-derived blocks through Sinhala, and the South-East
    /// Asian scripts with reordering vowels — Thai, Lao, Tibetan, Myanmar and
    /// Khmer. Latin Extended, Greek, Cyrillic, Armenian, Georgian, CJK, Hangul
    /// and Kana are all one-glyph-per-code-point and fall through to be drawn.
    /// </remarks>
    private static bool RequiresComplexShaping(string text)
    {
        foreach (var ch in text)
        {
            if (ch is (>= '\u0590' and <= '\u08FF')   // Hebrew, Arabic, Syriac, Thaana, N'Ko
                   or (>= '\u0900' and <= '\u0DFF')   // Devanagari through Sinhala
                   or (>= '\u0E00' and <= '\u0FFF')   // Thai, Lao, Tibetan
                   or (>= '\u1000' and <= '\u109F')   // Myanmar
                   or (>= '\u1780' and <= '\u17FF')   // Khmer
                   or (>= '\uFB1D' and <= '\uFDFF')   // Hebrew/Arabic presentation forms A
                   or (>= '\uFE70' and <= '\uFEFE'))  // Arabic presentation forms B
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The fill colour a <c>/DA</c> string sets, by RUNNING it through the
    /// real content-stream executor rather than pattern-matching it. A /DA is
    /// a content-stream fragment, so <c>g</c>, <c>rg</c>, <c>k</c> and an
    /// <c>/CS cs</c> + <c>sc</c> pair are all legal ways to say "blue", and a
    /// regex would understand exactly one of them.
    /// </summary>
    private SKColor? DefaultAppearanceColor(string da)
    {
        var savedFill = _state.FillColor;
        var savedStroke = _state.StrokeColor;
        var savedTextState = CloneTextState();
        try
        {
            _textState = new TextState();
            ExecuteContentBytes(Encoding.Latin1.GetBytes(da));
            return _state.FillColor;
        }
        catch
        {
            return null;   // Malformed /DA: fall back to black, never throw.
        }
        finally
        {
            _state.FillColor = savedFill;
            _state.StrokeColor = savedStroke;
            _textState = savedTextState;
        }
    }

    /// <summary>
    /// Border for a /Link that ships no /AP (§12.5.6.5). The width comes from
    /// <see cref="EffectiveLinkBorderWidth"/>, which resolves the §12.5.6.5 /
    /// Table 168 defaults rather than requiring the file to state one.
    /// </summary>
    /// <remarks>
    /// Link previously had an empty case, justified as "links without /C are
    /// intentionally invisible in print, matching every commercial viewer",
    /// with the concern that synthesising borders would obscure page content
    /// when a producer writes a large /Border width.
    ///
    /// The first half is measurably wrong. On isartor-6-6-1-t01-fail-a.pdf —
    /// a /Link with /BS &lt;&lt; /W 2 &gt;&gt;, /Border [0 0 2] and NO /C —
    /// at the scan's own 150 dpi:
    ///
    ///     pdftocairo  5973 inked px, black, bbox x81..626 y231..297
    ///     ghostscript 5950 inked px, black, bbox x83..624 y233..295
    ///     mutool         0
    ///     excise         0
    ///
    /// Both drawing renderers stroke a black 2pt rectangle on the annotation
    /// /Rect, matching /BS /W exactly. Two of three is a basis; the earlier
    /// two-oracle reading that called Link a mere renderer split was taken at
    /// 72 dpi WITHOUT Ghostscript, and adding the third opinion flipped it.
    ///
    /// The measured per-condition table is
    /// <c>tests/annotation-synthesis-policy.json</c> (rows <c>link.*</c>), and
    /// it is re-measured by AnnotationSynthesisPolicyGateTests — not restated
    /// here, because a free-text comment nothing re-measures is exactly what
    /// #993 removes.
    /// </remarks>
    private void RenderLinkDefault(Excise.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        var width = EffectiveLinkBorderWidth(annot);
        if (width <= 0)
            return;

        // The border must FIT the rectangle it borders — a stroke wider than
        // half the smaller side has already swallowed the annotation it is
        // supposed to outline.
        //
        // This is the one link rung where excise draws LESS than the majority,
        // and it is a deliberate deviation rather than a reading of the
        // evidence. Re-measured on a printable /Border [0 0 112] over a 100x100
        // /Rect (row link.width-exceeds-half-the-rect):
        //
        //     pdftocairo  40000 px, bbox (0,0)-(200,200)   the WHOLE PAGE
        //     ghostscript 11926 px, bbox (38,38)-(163,163) a bounded frame
        //     mutool          0
        //
        // Two of three draw something, so a bare majority rule would have
        // excise draw too — but they disagree by 3.4x on how much ink and do
        // not agree on the geometry at all: one covers the page, the other
        // clamps to a frame of its own choosing. There is no agreed picture to
        // copy, only a choice of which renderer to imitate, and the page
        // content underneath loses either way. Recorded as a deviation in
        // tests/annotation-synthesis-policy.json (with #885's original hazard
        // note) rather than silently obeyed or silently ignored.
        //
        // An earlier version of this comment cited pdf.js bug1552113 with
        // "ghostscript 0" as proof that Ghostscript refuses absurd borders.
        // That file's link carries no /F, so Ghostscript skipped the
        // annotation entirely — an abstention, not a refusal.
        float limit = Math.Min(rect.Width, rect.Height) / 2f;
        if (limit <= 0 || width > limit)
        {
            _options.Diagnostics?.Add(
                $"Link border width {width} exceeds half the annotation's smaller side " +
                $"({limit:0.#}); not drawn — it would cover the page rather than outline the link.");
            return;
        }

        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)width,
        };

        if (annot.Color is { } color)
        {
            var (r, g, b) = color;
            paint.Color = RgbToColor(r, g, b);
        }
        else
        {
            // Both oracles draw black when /C is absent.
            paint.Color = SKColors.Black;
        }

        _canvas.DrawRect(rect, paint);
    }

    /// <summary>
    /// The border width a /Link with no /AP is stroked with, resolving the
    /// spec's defaults instead of requiring the file to state one (#987).
    ///
    /// The ladder, and the measured verdict for each rung (72 dpi, one
    /// synthesized /Link per condition, /F 4 so Ghostscript does not abstain —
    /// see tests/annotation-synthesis-policy.json rows <c>link.*</c>):
    ///
    ///   /BS or /Border states a width  -> that width          poppler+gs draw
    ///   /BS present, no /W             -> 1 (Table 168)       poppler+gs draw
    ///   /Border present but unusable   -> 0, nothing drawn     nobody draws
    ///   neither key present            -> 1 (§12.5.6.5)       poppler+gs draw
    ///
    /// The last rung is the one #885 got wrong, and the correction matters
    /// because it is the COMMON case: it keyed on PdfAnnotation.BorderWidth
    /// being non-null ("only a file that explicitly asks for a visible border
    /// gets one"), decided on isartor-6-6-1-t01-fail-a.pdf, which does state
    /// /BS &lt;&lt; /W 2 &gt;&gt;. The no-key-at-all case was never measured.
    ///
    /// The apparent counter-evidence — pdfium bug_821454.pdf, where Ghostscript
    /// draws nothing on links with no /Border and no /BS — is not about borders
    /// at all: those annotations carry no /F, and Ghostscript renders only
    /// PRINTABLE annotations. Reading that zero as a "draws nothing" vote is
    /// the same class of error as reading a check mark's pixel count as a box.
    ///
    /// The third rung is not pedantry: /Border [0 0] (fewer than the three
    /// required entries) leaves BorderWidth null exactly as an absent /Border
    /// does, and poppler and Ghostscript BOTH draw nothing for it. A file that
    /// wrote a broken /Border has not asked for the default.
    /// </summary>
    private double EffectiveLinkBorderWidth(Excise.Core.Document.PdfAnnotation annot)
    {
        if (annot.BorderWidth is { } stated) return stated;

        var raw = annot.RawDictionary;
        if (raw.GetOptional("BS") != null) return 1.0;
        if (raw.GetOptional("Border") != null) return 0.0;
        return 1.0;
    }

    /// <summary>
    /// Sticky-note icon for a /Text annotation with no /AP (§12.5.6.4),
    /// selected by <c>/Name</c>.
    /// </summary>
    /// <remarks>
    /// All three reference renderers draw a marker, which is what made the
    /// absence a defect rather than a matter of taste — measured at 150 dpi on
    /// veraPDF 6-3-3-t01-pass-a.pdf: mutool 495 inked px, pdftocairo 917,
    /// Ghostscript 1388, excise 0.
    ///
    /// <para>They emphatically do NOT agree on the ARTWORK: mutool draws black
    /// strokes, pdftocairo a grey-green fill, Ghostscript grey plus black. The
    /// spec names the icon (§12.5.6.4 Table 172) and says nothing about how it
    /// is drawn. So these are excise's own glyphs, deliberately, and the gate
    /// asserts they are DISTINCT from one another rather than that they match
    /// anybody — a pixel comparison against mutool would be asserting a house
    /// style we did not choose and do not want.</para>
    ///
    /// <para>#1071: every <c>/Name</c> previously drew the same two-bar glyph,
    /// so a Help marker and an Insert marker were indistinguishable on the
    /// page. The icon is the ONLY thing a /Text annotation draws, so that was
    /// the whole of its visible meaning.</para>
    ///
    /// <para>Geometry is expressed in a 0..1 box via <c>P(u, v)</c> with v
    /// measured from the icon's VISUAL TOP, so every glyph scales with the
    /// 17pt marker and stays crisp at any DPI. Note the canvas here is Y-UP —
    /// <c>rect.Top</c> is the visually LOW edge — which is why v is subtracted
    /// from <c>rect.Bottom</c>.</para>
    /// </remarks>
    /// <summary>
    /// A real post-it-sized card with <c>/Contents</c> wrapped and drawn
    /// directly on it (#1794), replacing the old ~17pt icon-with-glyph
    /// rendering (§12.5.6.4's <c>/Name</c> Table 172 no longer selects
    /// anything here — see the removed switch this replaced in git history,
    /// and issue #1795 for restoring per-icon meaning via a real <c>/AP</c>
    /// other readers can show). <paramref name="rect"/> is already sized by
    /// <see cref="RenderDefaultAppearance"/>: the annotation's actual /Rect
    /// for a real card, or the fixed icon size only for a genuinely
    /// degenerate/undersized one.
    /// </summary>
    private void RenderStickyNoteDefault(Excise.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        var fill = annot.Color is { } c
            ? RgbToColor(c.Item1, c.Item2, c.Item3)
            : new SKColor(0xFF, 0xF5, 0x9D); // classic post-it yellow
        var border = DarkenColor(fill, 0.80f);

        // A soft drop shadow, offset down-right, so the card reads as
        // sitting ON the page rather than painted flat onto it (#1794's
        // "recognisably a Post-it" visual goal).
        using (var shadow = new SKPaint
               {
                   IsAntialias = _options.AntiAlias,
                   Style = SKPaintStyle.Fill,
                   Color = new SKColor(0, 0, 0, 0x38),
                   ImageFilter = SKImageFilter.CreateBlur(
                       Math.Max(0.5f, rect.Width * 0.03f), Math.Max(0.5f, rect.Height * 0.03f)),
               })
        {
            var offset = Math.Max(1f, Math.Min(rect.Width, rect.Height) * 0.03f);
            var shadowRect = new SKRect(
                rect.Left + offset, rect.Top + offset, rect.Right + offset, rect.Bottom + offset);
            _canvas.DrawRoundRect(
                new SKRoundRect(shadowRect, rect.Width * 0.06f, rect.Height * 0.06f), shadow);
        }

        var round = new SKRoundRect(rect, rect.Width * 0.06f, rect.Height * 0.06f);
        using (var body = new SKPaint { IsAntialias = _options.AntiAlias, Style = SKPaintStyle.Fill, Color = fill })
            _canvas.DrawRoundRect(round, body);
        using (var outline = new SKPaint
               {
                   IsAntialias = _options.AntiAlias,
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = Math.Max(1f, Math.Min(rect.Width, rect.Height) * 0.012f),
                   Color = border,
               })
            _canvas.DrawRoundRect(round, outline);

        var contents = annot.Contents;
        if (string.IsNullOrEmpty(contents))
            return;

        // #1363/#1381's rule applies here too: tofu is worse than an empty
        // card. A card with no legible text still shows AS a note (the
        // filled/bordered shape above), same failure mode as FreeText's
        // "fail closed" — see TypesetFreeTextContents for the precedent this
        // deliberately does NOT reuse (that path drives the full HarfBuzz
        // typesetter through a /DA; a sticky note has no /DA to typeset
        // against, so it declines rather than inventing one — #1795 tracks
        // giving notes real text layout via /AP).
        if (RequiresComplexShaping(contents))
        {
            AddDiagnostic(
                "Sticky note /Contents not drawn: complex-script text needs the FreeText " +
                "typesetter path, not yet wired for /Text (#1794/#1795).");
            return;
        }

        DrawStickyNoteText(rect, contents, DarkenColor(fill, 0.35f));
    }

    /// <summary>
    /// Simple greedy word-wrap for a sticky note's /Contents — no /DA, no
    /// complex-script shaping (see the caller's fail-closed check), just
    /// Helvetica top-aligned lines clipped to the card. Deliberately NOT the
    /// FreeText content-stream/typesetter machinery: a /Text annotation
    /// carries no /DA to typeset against, so there is no font/size/colour to
    /// read from the file — Helvetica and a size proportional to the card are
    /// excise's OWN viewer choice, same category of choice as the card's
    /// colours (#1794's design decision, not a spec-derived value).
    /// </summary>
    private void DrawStickyNoteText(SKRect cardRect, string contents, SKColor textColor)
    {
        var typeface = ResolveRenderFont("Helvetica", null).Typeface;
        if (typeface == null)
            return; // fail closed — no covering font, nothing legible to draw

        var padding = Math.Max(2f, Math.Min(cardRect.Width, cardRect.Height) * 0.08f);
        var textRect = new SKRect(
            cardRect.Left + padding, cardRect.Top + padding,
            cardRect.Right - padding, cardRect.Bottom - padding);
        if (textRect.Width < 2f || textRect.Height < 2f)
            return;

        var fontSize = Math.Clamp(cardRect.Height * 0.11f, 6f, 22f);
        using var font = new SKFont(typeface, fontSize) { Subpixel = true };
        using var paint = new SKPaint { IsAntialias = _options.AntiAlias, Color = textColor };

        var lineHeight = fontSize * 1.25f;
        var lines = WrapStickyNoteText(contents, font, paint, textRect.Width);
        var maxLines = Math.Max(1, (int)(textRect.Height / lineHeight));

        // #1797: the resting card is a flat raster — it cannot scroll. When
        // there is more text than fits, say so rather than silently clipping
        // mid-word; click the card to open it and read/edit the rest.
        var truncated = lines.Count > maxLines;
        var visibleLines = truncated ? maxLines : lines.Count;

        _canvas.Save();
        try
        {
            _canvas.ClipRect(cardRect, SKClipOperation.Intersect, _options.AntiAlias);
            var baselineY = textRect.Top + fontSize;
            for (var i = 0; i < visibleLines; i++)
            {
                var isLastVisible = truncated && i == visibleLines - 1;
                var line = isLastVisible ? TruncateWithEllipsis(lines[i], font, paint, textRect.Width) : lines[i];
                _canvas.DrawText(line, textRect.Left, baselineY, font, paint);
                baselineY += lineHeight;
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// Shortens <paramref name="line"/> so <c>line + "…"</c> fits
    /// <paramref name="maxWidth"/> — the last visible line of a card whose
    /// text overflows it (#1797). Falls back to just appending "…" if even
    /// one character plus the ellipsis cannot fit; a card too small to show
    /// anything legible already returned before reaching here.
    /// </summary>
    private static string TruncateWithEllipsis(string line, SKFont font, SKPaint paint, float maxWidth)
    {
        const string ellipsis = "…";
        if (font.MeasureText(line + ellipsis, paint) <= maxWidth) return line + ellipsis;

        var end = line.Length;
        while (end > 0 && font.MeasureText(line[..end] + ellipsis, paint) > maxWidth) end--;
        return end > 0 ? line[..end] + ellipsis : ellipsis;
    }

    /// <summary>
    /// Greedy word wrap to <paramref name="maxWidth"/>, honouring explicit
    /// line breaks in the source text (§7.9.7 permits CR, LF or CRLF within a
    /// PDF text string). A single word wider than <paramref name="maxWidth"/>
    /// is left on its own (clipped) line rather than broken mid-word — this
    /// is a card of short annotation text, not a paragraph layout engine.
    /// </summary>
    private static List<string> WrapStickyNoteText(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\r', '\n'))
        {
            if (rawLine.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = string.Empty;
            foreach (var word in rawLine.Split(' '))
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (current.Length > 0 && font.MeasureText(candidate, paint) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>Multiply RGB by <paramref name="factor"/> (&lt; 1 darkens) — the post-it card's border/text derive from its fill this way rather than a fixed colour, so a /C-coloured note still looks coherent.</summary>
    private static SKColor DarkenColor(SKColor color, float factor) => new(
        (byte)Math.Clamp(color.Red * factor, 0, 255),
        (byte)Math.Clamp(color.Green * factor, 0, 255),
        (byte)Math.Clamp(color.Blue * factor, 0, 255),
        color.Alpha);

    private void RenderShapeDefault(
        Excise.Core.Document.PdfAnnotation annot, SKRect rect, bool isEllipse)
    {
        // /IC FIRST — the interior is painted, then the border stroked over it
        // (§12.5.6.8 Table 178). excise ignored /IC entirely until #1055: on
        // Okular's annotation-square-circle-without-appearance.pdf both mutool
        // and pdftocairo fill the interior grey and excise left it white, so a
        // shape that OBSCURES page content in every other viewer showed the
        // text underneath here. That matters for a redaction reviewer, who is
        // deciding from the rendered page.
        if (annot.InteriorColor is { } interior)
        {
            var (ir, ig, ib) = interior;
            using var fill = new SKPaint
            {
                IsAntialias = _options.AntiAlias,
                Style = SKPaintStyle.Fill,
                Color = RgbToColor(ir, ig, ib),
            };
            if (isEllipse) _canvas.DrawOval(rect, fill);
            else _canvas.DrawRect(rect, fill);
        }

        // The border needs /C; an annotation may legitimately carry /IC alone,
        // in which case the fill above is the whole appearance.
        if (annot.Color is not { } color) return;
        var (r, g, b) = color;
        float borderWidth = (float)(annot.BorderWidth ?? 1.0);

        // §12.5.6.8: "the rectangle or ellipse shall be inset from the
        // annotation's Rect by the border width" — so the OUTER edge of the
        // stroke lands on /Rect. Skia strokes CENTRED on the path, so the path
        // has to move in by half the width for the ink to land there (#1073).
        //
        // excise stroked centred on /Rect, putting half the border outside it.
        // Measured, and it scaled exactly with the width:
        //
        //     border   /Rect px    mutool     pdftocairo   excise (before)
        //     1 pt     194 x 138   194 x 138  194 x 138    197 x 142
        //     3 pt     312 x 125   313 x 124  313 x 125    319 x 131
        //
        // Not cosmetic: /Rect is the geometry the redaction scrubber uses to
        // decide which annotations a redaction covers, so ink outside it was
        // ink the removal path did not know about. With the inset the drawn
        // ink lands inside /Rect and /Rect-based intersection is sufficient.
        //
        // Square and Circle ONLY. §12.5.6.8's inset language is specific to
        // these two; Polygon, PolyLine, Line and Ink draw on their own declared
        // geometry, not on /Rect, and insetting those would move ink the file
        // positions explicitly.
        var half = borderWidth / 2f;
        var stroked = new SKRect(
            rect.Left + half, rect.Top + half, rect.Right - half, rect.Bottom - half);
        // A border thicker than the annotation collapses the inset rect; fall
        // back to /Rect rather than drawing an inverted one.
        if (stroked.Width <= 0 || stroked.Height <= 0) stroked = rect;

        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            StrokeCap = SKStrokeCap.Butt,
            Color = RgbToColor(r, g, b),
            PathEffect = CreateBorderDashEffect(annot),
        };
        if (isEllipse) _canvas.DrawOval(stroked, paint);
        else _canvas.DrawRect(stroked, paint);
    }

    /// <summary>
    /// Render a text-markup annotation when the PDF omits /AP /N.
    /// This intentionally stays simple: exact quad geometry is already
    /// reduced to per-quad boxes by PdfAnnotationParser, which is enough
    /// for the common no-appearance highlight/comment fixtures.
    /// </summary>
    private void RenderTextMarkupDefault(
        Excise.Core.Document.PdfAnnotation annot, SKRect fallbackRect)
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
                    case Excise.Core.Document.PdfAnnotationSubtype.Highlight:
                        paint.Style = SKPaintStyle.Fill;
                        paint.BlendMode = SKBlendMode.Multiply;
                        // NOT WithAlpha(..., AnnotationOpacityAlpha) any more:
                        // /CA is applied once, by the layer the dispatch loop
                        // opens (#1072). Applying it here too multiplied it in
                        // twice, so a Highlight at /CA 0.5 drew at 0.25.
                        paint.Color = baseColor;
                        // The ends overshoot the quad, and by HOW MUCH is
                        // measured, not chosen (#1004). Every engine that draws
                        // a highlight rounds its ends past the /QuadPoints; the
                        // overshoot scales with the quad's HEIGHT and ignores
                        // its width. Measured at 72 dpi on a 100 pt quad, ink
                        // bbox px past each end:
                        //
                        //   quad height     8    10    20    40
                        //   mutool          2     2     4     8
                        //   pdftocairo      2     2     4     8
                        //   pdftoppm        2     2     4     8
                        //   ghostscript     1     2     3     5
                        //   pdfbox          2     2     5     8
                        //   pdfium          0     0     0     0
                        //   height/5      1.6     2     4     8
                        //
                        // So height/5 is what mutool, poppler and pdfbox draw
                        // and it sits inside the spread of every engine that
                        // draws anything. This used to be min(height,width)/2 —
                        // 10 px on the 20 px quad above, wider than any oracle
                        // on both sides, and on a narrow quad it shrank with
                        // the WIDTH, which no engine does (measured on a
                        // 10x40 quad: mutool/poppler/pdfbox still overshoot 8).
                        var overshoot = box.Height * 0.2f;
                        var highlightBox = box;
                        highlightBox.Inflate(overshoot, 0);
                        _canvas.DrawRoundRect(highlightBox, overshoot, overshoot, paint);
                        paint.BlendMode = SKBlendMode.SrcOver;
                        break;

                    case Excise.Core.Document.PdfAnnotationSubtype.Underline:
                        DrawMarkupLine(box, baseColor, box.Top + box.Height * 0.12f, paint);
                        break;

                    case Excise.Core.Document.PdfAnnotationSubtype.StrikeOut:
                        DrawMarkupLine(box, baseColor, box.MidY, paint);
                        break;

                    case Excise.Core.Document.PdfAnnotationSubtype.Squiggly:
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

    private static SKRect NormalizeAnnotationRect(Excise.Core.Document.PdfRectangle rect)
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

    private static SKColor AnnotationMarkupColor(Excise.Core.Document.PdfAnnotation annot)
    {
        if (annot.Color is { } color)
        {
            var (r, g, b) = color;
            return RgbToColor(r, g, b);
        }

        // YELLOW for Highlight, black for the rest — and this is a DELIBERATE
        // divergence from mutool and pdftocairo, both of which paint an
        // uncoloured Highlight black.
        //
        // §12.5.6.10 defines Highlight as markup that "shall appear as
        // highlights" over text. §12.5.5 leaves the appearance unspecified when
        // there is no /AP, so nothing here is prescribed — but a BLACK
        // highlight is not an under-specified highlight, it is the opposite of
        // one: it hides the very text it exists to draw attention to.
        //
        // Rendered over three lines of body text, mutool's uncoloured Highlight
        // is indistinguishable from a redaction bar. For a tool whose entire
        // purpose is redaction, showing a reviewer a black bar over text that
        // has NOT been redacted is the most dangerous thing this renderer could
        // do — it invites exactly the wrong conclusion about what the document
        // still contains.
        //
        // An earlier version of this took mutool's black on the reasoning that
        // an unstated colour means the initial graphics state (§8.6.8,
        // DeviceGray 0). That was a description of what mutool does dressed up
        // as a rule; the spec does not say a Highlight defaults to black. Where
        // the spec is silent, excise picks the reading that serves the reader,
        // and #1015's "do not elect a renderer" cuts the same way: copying an
        // engine here would import a behaviour that is actively harmful in this
        // context.
        return annot.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Highlight
            ? new SKColor(255, 255, 0)
            : SKColors.Black;
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

    /// <summary>
    /// Start a transparency layer for §12.5.2 <c>/CA</c>, or return null when
    /// the annotation is fully opaque and no layer is needed. The caller must
    /// Restore and dispose what it returns.
    /// </summary>
    private SKPaint? BeginAnnotationOpacityLayer(Excise.Core.Document.PdfAnnotation annot)
    {
        var alpha = AnnotationOpacityAlpha(annot);
        if (alpha >= 255) return null;

        // The paint must outlive the SaveLayer: Skia composites the layer with
        // it at RESTORE time, not at save time, so disposing it early would
        // composite with a dead object.
        var paint = new SKPaint { Color = new SKColor(0, 0, 0, alpha) };
        _canvas.SaveLayer(paint);
        return paint;
    }

    private static byte AnnotationOpacityAlpha(Excise.Core.Document.PdfAnnotation annot)
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
    private SKColor? ParseColorArray(Excise.Core.Primitives.PdfObject? obj)
    {
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        if (resolved is not Excise.Core.Primitives.PdfArray arr || arr.Count == 0) return null;
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
    /// <param name="useAcroFormDaFallback">
    /// Widgets inherit the AcroForm-level <c>/DA</c> when they state none
    /// (§12.7.3.3). A FreeText does NOT — §12.5.6.6 puts <c>/DA</c> on the
    /// annotation, and borrowing the form's would invent styling from an
    /// unrelated dictionary. So the fallback is a parameter, not a constant.
    /// </param>
    private void RenderTextFieldValue(
        Excise.Core.Document.PdfAnnotation annot, SKRect rect, string value,
        bool useAcroFormDaFallback = true, bool topAlign = false)
    {
        var acroFormDefaults = AcroFormDefaults;

        // An ABSENT /DA is not a reason to drop the value (#889).
        //
        // §12.7.3.3 makes /DA required in the AcroForm dictionary, so a file
        // with none is malformed — but the thing to be drawn is still fully
        // defined by /V, and this method already knows how to cope: twenty
        // lines below, a /DA that parses to no font falls back to Helvetica.
        // Returning early here meant "no /DA at all" was handled WORSE than
        // "/DA present but useless", which is backwards.
        //
        // Measured on pdfium calculate.pdf (two /FT /Tx widgets, /V (5) and
        // /V (2), no /AP, no /MK, and no /DA anywhere in the file):
        //
        //     mutool 61, ghostscript 46, excise 0, pdftocairo 0
        //
        // Two of the three independent engines draw the value. That majority
        // is what settles it — see #889, where a 1-1 split between mutool and
        // pdftocairo was explicitly NOT treated as grounds to change anything.
        //
        // Empty string rather than null: ExecuteContentBytes on it is a no-op,
        // so the auto-size and Helvetica fallback paths run exactly as they do
        // for a /DA that sets no font.
        var da = annot.RawDictionary.GetStringOrNull("DA")
                 ?? (useAcroFormDaFallback ? acroFormDefaults.DefaultAppearance : null)
                 ?? "";

        _resourcesStack.Push(acroFormDefaults.Resources);
        _canvas.Save();
        try
        {
            // A synthesized value is CLIPPED to the widget's /Rect (#991).
            //
            // Measured at 72 dpi on a /V that overflows its field
            // (row widget.tx.value-wider-than-the-rect):
            //
            //     mutool      513 px, bbox right edge 149  \ clip to /Rect
            //     pdftocairo  515 px, bbox right edge 150  /
            //     ghostscript 751 px, bbox right edge 199  -> runs off the field
            //     excise      775 px, bbox right edge 200  -> ran off the PAGE
            //
            // Two engines of three clip. Without this the negative-size fix
            // below would paint a mirrored string clean across the page, which
            // is the one output nobody produces.
            _canvas.ClipRect(rect, SKClipOperation.Intersect, _options.AntiAlias);
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

                // ZERO means auto-size (§12.7.4.3). NEGATIVE does not — it is a
                // real size that mirrors the glyphs through the text-space
                // origin, exactly as in a page content stream (#970), and
                // treating it as "no size given" silently rendered the value
                // upright at an unrelated size (#991).
                //
                // Measured at 72 dpi on /DA (0 0 0 rg /Helv -12 Tf) over
                // /Rect [50 60 150 90], /Q 1 (rows widget.tx.value-*-size*):
                // every engine draws the value MIRRORED — mutool, pdftocairo
                // and pdftoppm land it in the same band as the upright value
                // with the column profile reversed and the row profile
                // flipped, Ghostscript mirrors it 8 px lower. Nobody draws
                // what excise drew.
                //
                // The font has to be resolved BEFORE the size is decided,
                // because auto-size is a fit against this typeface's own
                // metrics and this string's own advance.
                //
                // A malformed/empty /DA (no Tf) leaves _currentFont exactly
                // as it was before this method ran — possibly null (first
                // text ever on the page). Resolve a plain Helvetica fallback
                // the same way any other font resolves, rather than patching
                // a single field on an immutable ResolvedRenderFont. Note that
                // no Tf at all leaves _textState.FontSize at its default 12,
                // NOT at 0, so a field with no /DA anywhere takes the branch
                // below rather than the auto-size one — which is what the
                // oracles do with it (row widget.tx.value-no-da-anywhere:
                // mutool and Ghostscript draw it at exactly the size they draw
                // an explicit /Helv 12 Tf).
                if (_currentFont?.Typeface == null)
                    _currentFont = ResolveRenderFont("Helvetica", null);

                const float padX = 2f;
                float fontSize = Math.Abs(_textState.FontSize) > 0.001f
                    ? _textState.FontSize
                    : AutoFitFontSize(value, rect, _currentFont?.Typeface, padX);

                // Measure text to compute alignment. Use the active
                // typeface so the width matches what we're about to draw.
                //
                // SIGNED width, measured at |size|: a negative size advances
                // LEFTWARDS, so the run occupies [x - w, x] rather than
                // [x, x + w], and the alignment arithmetic below only lands
                // where the oracles land if the width carries that sign. All
                // three /Q cases were checked against mutool and pdftocairo at
                // 72 dpi and reproduce their placement to the pixel: /Q 0 puts
                // the mirrored run left of the field (clipped to a sliver),
                // /Q 1 centres it over the same box as the upright value,
                // /Q 2 pushes it right of the field (a sliver at the far edge).
                // SKFont itself is given the absolute size — a negative one is
                // not a valid Skia text size.
                using var measureFont = new SKFont(_currentFont!.Typeface!, Math.Abs(fontSize));
                using var measurePaint = new SKPaint();
                float textWidth = measureFont.MeasureText(value, measurePaint)
                                  * (fontSize < 0 ? -1f : 1f);

                int q = annot.RawDictionary.GetInt("Q", 0);
                float textX;
                if (q == 1)      textX = rect.Left + (rect.Width - textWidth) * 0.5f;
                // Keep a negative-size, right-aligned run one padding unit
                // inside its clip.  Its glyphs advance leftward, and rounding
                // the substituted platform font's measured advance to the
                // clip boundary otherwise made the required visible remnant
                // disappear on FreeType while CoreText happened to retain one
                // antialiased pixel.  This is placement/clip geometry, not a
                // platform-font rendering decision (#970, #991).
                else if (q == 2) textX = rect.Right - textWidth - padX - (fontSize < 0 ? padX : 0f);
                else             textX = rect.Left + padX;

                // Vertical baseline: centre the font's OWN line box inside the
                // rect, read from the resolved typeface — the same metrics
                // AutoFitFontSize uses to pick the size. The previous rule
                // approximated the cap height at a flat fontSize * 0.7, which
                // is not any real font's, and drew the value 2-5 px above
                // every engine with the error scaling by size (#1016).
                float textY = topAlign
                    ? BaselineForTopAlignedLineBox(rect, _currentFont?.Typeface, fontSize, padX)
                    : BaselineForCentredLineBox(rect, _currentFont?.Typeface, fontSize);

                // Drive RenderText through the standard text-block path.
                _inTextBlock = true;
                _textState.TextMatrixA = 1; _textState.TextMatrixB = 0;
                _textState.TextMatrixC = 0; _textState.TextMatrixD = 1;
                _textState.TextMatrixE = textX;
                _textState.TextMatrixF = textY;
                _textState.LineMatrixE = textX;
                _textState.LineMatrixF = textY;
                _textState.FontSize = fontSize;

                // Latin-1 round-trip into bytes — same shape as a Tj operand.
                // RenderText then handles cmap / encoding for the resolved
                // typeface, and uses the bytes for the PDF-/Widths and
                // byte→glyph-ID layout branches.
                //
                // ⚠️ Only while the value SURVIVES that round-trip (#1381).
                // Encoding.Latin1 substitutes '?' for anything above U+00FF, so
                // a Polish "Załącznik" reached RenderText as "Za?a?cznik" —
                // the byte-indexed branches then laid out the substitutes, not
                // the text. There is no PDF font encoding to honour here: this
                // is a SYNTHESISED appearance and excise picked the typeface
                // itself, so the string is the authority and the bytes are a
                // lossy detour. Passing null makes RenderText take its Unicode
                // branch, which draws the string and measures it with the same
                // typeface.
                //
                // Restricted to values that actually need it so no widget whose
                // value is Latin-1 changes layout at all — those keep the
                // /Widths-driven positioning that was measured against mutool
                // and pdftocairo in #584 and #1003.
                var representable = true;
                foreach (var ch in value)
                {
                    if (ch > 0xFF) { representable = false; break; }
                }

                RenderText(value, representable ? Encoding.Latin1.GetBytes(value) : null);
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
    /// The size for a <c>/DA</c> that states <c>0 Tf</c> — §12.7.4.3's
    /// auto-size: fit the value to the field (#1003).
    ///
    /// <para>Two limits, and the SMALLER wins: the string's own advance must
    /// fit the field's width, and the font's own line box (ascent + descent)
    /// must fit its height. Both are read off the resolved typeface rather
    /// than assumed, so a wide font shrinks where a narrow one does not.</para>
    ///
    /// <para>This replaced <c>min(rect.Height × 0.75, 16)</c> — a height-only
    /// heuristic, capped, that ignored the value and the field width alike.
    /// Measured at 72 dpi, ink bbox of <c>/V (Mountain)</c> with
    /// <c>/DA (/Helv 0 Tf)</c>:</para>
    ///
    /// <code>
    ///   field      mutool    pdftocairo  ghostscript   old excise   this
    ///   100x30     94x18     92x18       90x18         64x12        93x17
    ///   100x100    94x18     92x18       (136x57)      64x12        93x17
    ///   30x60      24x15     24x14       (57x34)       21x12        23x13
    /// </code>
    ///
    /// <para>The 100x100 row is the one that settles the rule: mutool and
    /// poppler draw the value at exactly the size they use in the 30 pt-tall
    /// field, so the fit is bounded by the WIDTH, not by the height, and no
    /// absolute cap is involved. Ghostscript is the outlier — it overflows
    /// the field rather than fitting to it (parenthesised above), which is
    /// the same disagreement it shows on row
    /// widget.tx.value-wider-than-the-rect.</para>
    /// </summary>
    /// <summary>
    /// Baseline that centres the font's line box (ascent + descent) inside
    /// <paramref name="rect"/>, in the same upward-positive space the caller's
    /// text matrix uses.
    ///
    /// Falls back to the historic cap-height approximation when there is no
    /// typeface to measure — a guess is still better than dropping the value,
    /// and there is nothing to compute a fit from.
    /// </summary>
    private static float BaselineForCentredLineBox(SKRect rect, SKTypeface? typeface, float fontSize)
    {
        if (typeface == null)
            return rect.Top + (rect.Height + fontSize * 0.7f) * 0.5f - fontSize * 0.5f;

        using var probe = new SKFont(typeface, 1f);
        var metrics = probe.Metrics;           // per em; Ascent is negative
        float ascent = -metrics.Ascent;
        float descent = metrics.Descent;
        float lineBox = ascent + descent;
        if (lineBox <= 0.001f)
            return rect.Top + (rect.Height + fontSize * 0.7f) * 0.5f - fontSize * 0.5f;

        // rect.Top is the LOW edge in this space (the caller's matrix grows
        // upward), so the baseline sits a descender plus half the slack above
        // it.
        return rect.Top + descent * fontSize + (rect.Height - lineBox * fontSize) * 0.5f;
    }

    /// <summary>
    /// Baseline for text that starts at the TOP of the box rather than being
    /// centred in it — a FreeText note flows from its first line down
    /// (§12.5.6.6), where a form field's single value sits centred.
    ///
    /// <para>Measured: both mutool and pdftocairo place the probe fixture's
    /// FreeText string against the top edge; centring it put excise's text
    /// roughly half the box lower than either.</para>
    /// </summary>
    private static float BaselineForTopAlignedLineBox(
        SKRect rect, SKTypeface? typeface, float fontSize, float padY)
    {
        // rect.Top is the LOW edge in this space (the caller's matrix grows
        // upward), so "the top of the box" is rect.Bottom, and the first
        // baseline hangs one ascent below it.
        if (typeface == null)
            return rect.Bottom - padY - fontSize * 0.75f;

        using var probe = new SKFont(typeface, 1f);
        float ascent = -probe.Metrics.Ascent;      // per em; Ascent is negative
        if (ascent <= 0.001f)
            return rect.Bottom - padY - fontSize * 0.75f;

        return rect.Bottom - padY - ascent * fontSize;
    }

    private static float AutoFitFontSize(string value, SKRect rect, SKTypeface? typeface, float padX)
    {
        // No typeface to measure against: keep a size that is at least
        // visible rather than guessing at a fit that cannot be computed.
        if (typeface == null) return Math.Max(rect.Height * 0.85f, 4f);

        using var probe = new SKFont(typeface, 1f);
        using var paint = new SKPaint();

        var metrics = probe.Metrics;            // per em, Ascent is negative
        float lineBox = metrics.Descent - metrics.Ascent;
        float byHeight = lineBox > 0.001f ? rect.Height / lineBox : rect.Height * 0.85f;

        float unitWidth = probe.MeasureText(value, paint);
        float available = Math.Max(rect.Width - 2f * padX, 1f);
        float byWidth = unitWidth > 0.001f ? available / unitWidth : byHeight;

        // The floor is the old one: a value long enough to want sub-4 pt text
        // is drawn at 4 pt and clipped to the field, which is what the
        // clip above is for.
        return Math.Max(Math.Min(byHeight, byWidth), 4f);
    }

    /// <summary>
    /// Pull a string out of a /V or similar value object — handles both
    /// PDF string literals (most common) and PDF names (rare).
    /// </summary>
    private static string? ExtractStringFromObject(Excise.Core.Primitives.PdfObject? obj)
    {
        return obj switch
        {
            Excise.Core.Primitives.PdfString s => s.Value,
            Excise.Core.Primitives.PdfName n => n.Value,
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
