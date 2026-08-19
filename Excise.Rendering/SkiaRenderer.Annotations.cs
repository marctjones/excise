using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;

namespace Excise.Rendering;

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
        IReadOnlyList<Excise.Core.Document.PdfAnnotation> annots;
        try { annots = _page.GetAnnotations(); }
        catch { return; }
        if (annots.Count == 0) return;

        foreach (var annot in annots)
        {
            // Skip annotations the spec says shouldn't be displayed.
            // Print=4 is fine — that's an opt-in for *also* including
            // the annotation in printed output, not a "screen only" flag.
            var f = annot.Flags;
            if ((f & (Excise.Core.Document.PdfAnnotationFlags.Hidden
                    | Excise.Core.Document.PdfAnnotationFlags.NoView)) != 0)
                continue;

            // Invisible (bit 1) is NARROWER than its name, and reading it as
            // "never draw" is wrong. §12.5.3 Table 165:
            //
            //   "If set, do not display the annotation if it does not belong to
            //    one of the standard annotation types AND no annotation handler
            //    is available. If clear, display such an unsupported annotation
            //    using an appearance stream specified by its appearance
            //    dictionary, if any."
            //
            // So the flag only ever governs annotations of a NON-STANDARD
            // subtype. For a standard one — /Line, /Circle, /Square … — it has
            // no effect at all, and an /AP present on such an annotation must
            // still be drawn.
            //
            // Treating it as an unconditional skip blanked two conformance
            // fixtures that exist to test exactly this: veraPDF 6-3-2-t02-fail-a
            // (/Line, Invisible+Print, /AP /N present) and isartor-6-5-3-t02-fail-d
            // (/Circle, same shape). mutool and pdftocairo both draw them.
            //
            // PdfAnnotationSubtype.Unknown is precisely "not one of the standard
            // types", so it is the only case where the flag applies.
            if ((f & Excise.Core.Document.PdfAnnotationFlags.Invisible) != 0 &&
                annot.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Unknown)
            {
                _options.Diagnostics?.Add(
                    "Annotation of a non-standard subtype has the Invisible flag and no " +
                    "handler; not drawn (§12.5.3).");
                continue;
            }

            var appearance = ResolveAppearanceN(annot);
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

            // Annotation /Rect (PDF stores [llx lly urx ury], but some
            // producers swap pairs — normalize both ways). Resolved BEFORE the
            // appearance /BBox because it is also the fallback for a form that
            // declares no usable one.
            float rx1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
            float ry1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
            float rx2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
            float ry2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
            if (rx2 <= rx1 || ry2 <= ry1)
            {
                _options.Diagnostics?.Add(
                    $"Annotation /{annot.Subtype} has a degenerate /Rect; appearance not drawn.");
                continue;
            }

            // Appearance /BBox.
            //
            // Two shapes used to hit a silent `continue` here, dropping the
            // annotation with no diagnostic at all (#888):
            //
            //   • /BBox is an INDIRECT REFERENCE (pdfium bug_1658.pdf). The
            //     old `is not PdfArray` test inspected the reference object
            //     itself and failed. RenderFormXObjectInner — one call away,
            //     on the very same stream — already resolved this key through
            //     ResolveArray; only this path did not. That asymmetry is the
            //     whole bug.
            //   • /BBox is ABSENT (pdfium bug_861842.pdf). §8.10.2 makes it
            //     REQUIRED on a form XObject, so such a form is invalid and
            //     there is no geometry to honour.
            //
            // The second case was first "fixed" here by synthesising a /BBox
            // from the annotation /Rect. That was wrong, and the oracles said
            // so: on a hand-authored BBox-less form both mutool and pdftocairo
            // draw NOTHING, pdftocairo reporting "Syntax Error: Bad form
            // bounding box". They still show bug_861842 because they fall back
            // to the WIDGET's own chrome — border, background, /MK — not
            // because they repair the form.
            //
            // So: do not invent geometry. Fall back to the same default
            // appearance synthesis used when there is no /AP at all, which is
            // what actually makes the annotation visible in other readers.
            // Silently dropping it remains the one unacceptable option — excise
            // is a redaction tool, and an annotation the reviewer never sees is
            // content they cannot decide about while it still reaches the
            // recipient.
            var bboxArr = ResolveArray(appearance, "BBox");
            if (bboxArr == null || bboxArr.Count < 4 ||
                !TryGetArrayNumber(bboxArr, 0, out var bx1Value) ||
                !TryGetArrayNumber(bboxArr, 1, out var by1Value) ||
                !TryGetArrayNumber(bboxArr, 2, out var bx2Value) ||
                !TryGetArrayNumber(bboxArr, 3, out var by2Value))
            {
                _options.Diagnostics?.Add(
                    $"Annotation /{annot.Subtype} appearance has no usable /BBox " +
                    "(required by §8.10.2); drawing the default appearance instead.");
                RenderDefaultAppearance(annot);
                continue;
            }
            float bMinX = (float)Math.Min(bx1Value, bx2Value);
            float bMinY = (float)Math.Min(by1Value, by2Value);
            float bMaxX = (float)Math.Max(bx1Value, bx2Value);
            float bMaxY = (float)Math.Max(by1Value, by2Value);
            if (bMaxX <= bMinX || bMaxY <= bMinY)
            {
                _options.Diagnostics?.Add(
                    $"Annotation /{annot.Subtype} appearance /BBox is degenerate; " +
                    "drawing the default appearance instead.");
                RenderDefaultAppearance(annot);
                continue;
            }

            // /Matrix may be indirect for the same reason /BBox may be.
            var formMatrix = SKMatrix.Identity;
            if (ResolveArray(appearance, "Matrix") is { Count: >= 6 } mArr)
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
            if (bbMaxX <= bbMinX || bbMaxY <= bbMinY)
            {
                _options.Diagnostics?.Add(
                    $"Annotation /{annot.Subtype} appearance /BBox collapses to zero area " +
                    "once /Matrix is applied; nothing drawn.");
                continue;
            }

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
    private Excise.Core.Primitives.PdfStream? ResolveAppearanceN(Excise.Core.Document.PdfAnnotation annot)
    {
        var apObj = annot.RawDictionary.GetOptional("AP");
        if (apObj == null) return null;
        if (_page.Document.Resolve(apObj) is not Excise.Core.Primitives.PdfDictionary ap) return null;
        var nObj = ap.GetOptional("N");
        if (nObj == null) return null;
        var resolved = _page.Document.Resolve(nObj);

        if (resolved is Excise.Core.Primitives.PdfStream stream)
            return stream;

        if (resolved is Excise.Core.Primitives.PdfDictionary stateDict)
        {
            var stateName = annot.RawDictionary.GetNameOrNull("AS");
            if (stateName != null)
            {
                var stateObj = stateDict.GetOptional(stateName);
                if (stateObj != null &&
                    _page.Document.Resolve(stateObj) is Excise.Core.Primitives.PdfStream s)
                    return s;

                // /AS NAMES A STATE THAT /AP /N DOES NOT DEFINE — draw nothing.
                //
                // This is not a malformed file, it is the normal way an OFF
                // checkbox is written. §12.5.5: /AS selects the appearance from
                // the sub-dictionary, and producers routinely omit /Off from /N
                // precisely because "off" means there is nothing to draw. IRS
                // Form W-9 is written exactly this way:
                //
                //   /AP << /D << /1 12 0 R /Off 11 0 R >>
                //          /N << /1 13 0 R >> >>     <- no /Off
                //   /AS /Off   /V /Off
                //
                // Falling through to "first usable entry" here picked /1 — the
                // CHECKED appearance — and drew a tick in every unchecked box on
                // a blank federal form. mutool renders the same page with empty
                // boxes. Found by looking at a screenshot, not by a test.
                return null;
            }

            // No /AS at all. A single-entry /N is unambiguous; anything else is
            // a guess, and guessing which state a form field is in is how the
            // bug above happened. Kept narrow deliberately.
            foreach (var kvp in stateDict)
            {
                if (_page.Document.Resolve(kvp.Value) is Excise.Core.Primitives.PdfStream s)
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
        // /Text is drawn at a FIXED size and its /Rect is ignored for sizing
        // (§12.5.6.4: "the annotation shall be drawn at a fixed size regardless
        // of the magnification"). Producers therefore write a degenerate rect
        // and mean it — veraPDF 6-3-3-t01-pass-a.pdf has /Rect [50 110 50 110],
        // zero by zero. The guard below rejected that before anything could
        // draw, while mutool (495 inked px), pdftocairo (917) and Ghostscript
        // (1388) all place a ~16pt icon anchored at that point. This is the
        // one subtype where a zero-size rect is normal rather than malformed,
        // so it is normalised before the guard rather than exempted from it.
        if (annot.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Text)
        {
            const float noteSize = 17f; // oracles measure 16.3-18pt
            rx2 = rx1 + noteSize;
            ry1 = ry2 - noteSize;
        }

        if (rx2 - rx1 < 0.5f || ry2 - ry1 < 0.5f) return;

        var rect = new SKRect(rx1, ry1, rx2, ry2);

        switch (annot.Subtype)
        {
            case Excise.Core.Document.PdfAnnotationSubtype.Widget:
                RenderWidgetDefault(annot, rect);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Link:
                RenderLinkDefault(annot, rect);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Text:
                RenderStickyNoteDefault(annot, rect);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Square:
                RenderShapeDefault(annot, rect, isEllipse: false);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Circle:
                RenderShapeDefault(annot, rect, isEllipse: true);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.FreeText:
                RenderFreeTextDefault(annot, rect);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Highlight:
            case Excise.Core.Document.PdfAnnotationSubtype.Underline:
            case Excise.Core.Document.PdfAnnotationSubtype.Squiggly:
            case Excise.Core.Document.PdfAnnotationSubtype.StrikeOut:
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
            case Excise.Core.Document.PdfAnnotationSubtype.Line:
                RenderLineDefault(annot);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Polygon:
                RenderVertexShapeDefault(annot, close: true);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.PolyLine:
                RenderVertexShapeDefault(annot, close: false);
                break;
            case Excise.Core.Document.PdfAnnotationSubtype.Ink:
                RenderInkDefault(annot);
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
        return new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = RgbToColor(r, g, b),
        };
    }

    /// <summary>Line annotation (§12.5.6.7): draw /L as a segment.</summary>
    private void RenderLineDefault(Excise.Core.Document.PdfAnnotation annot)
    {
        if (annot.LineEndpoints is not { } l) return;
        using var paint = CreateAnnotationStrokePaint(annot);
        if (paint == null) return;
        _canvas.DrawLine((float)l.X1, (float)l.Y1, (float)l.X2, (float)l.Y2, paint);
    }

    /// <summary>
    /// Polygon and PolyLine (§12.5.6.9): the same /Vertices list, closed or
    /// open. Interior colour (/IC) is not filled — the parser does not surface
    /// it, and stroking alone matches what viewers show for these without an
    /// /AP stream.
    /// </summary>
    private void RenderVertexShapeDefault(Excise.Core.Document.PdfAnnotation annot, bool close)
    {
        if (annot.Vertices is not { Count: >= 2 } verts) return;
        using var paint = CreateAnnotationStrokePaint(annot);
        if (paint == null) return;

        using var path = new SKPath();
        path.MoveTo((float)verts[0].X, (float)verts[0].Y);
        for (int i = 1; i < verts.Count; i++)
            path.LineTo((float)verts[i].X, (float)verts[i].Y);
        if (close) path.Close();
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
    /// Resolve and cache the AcroForm <c>/DR</c> resources dict (where
    /// the document's interactive form keeps its default fonts) plus
    /// the AcroForm <c>/DA</c> default-appearance string. Both are used
    /// when a widget annotation lacks its own <c>/AP</c> and falls back
    /// to drawing the field value through the variable-text path.
    /// Cached per-render-context so we don't re-resolve per widget.
    /// </summary>
    private Excise.Core.Primitives.PdfDictionary? _acroFormDr;
    private string? _acroFormDa;
    private bool _acroFormResolved;
    private void ResolveAcroFormResources()
    {
        if (_acroFormResolved) return;
        _acroFormResolved = true;
        var afObj = _page.Document.Catalog.GetOptional("AcroForm");
        if (afObj == null) return;
        if (_page.Document.Resolve(afObj) is not Excise.Core.Primitives.PdfDictionary af) return;
        _acroFormDa = af.GetStringOrNull("DA");
        var drObj = af.GetOptional("DR");
        if (drObj == null) return;
        _acroFormDr = _page.Document.Resolve(drObj) as Excise.Core.Primitives.PdfDictionary;
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
        //     nothing anywhere, which matches how ResolveAppearanceN above
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
    /// a blank IRS W-9 once already (see <see cref="ResolveAppearanceN"/>).
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
    /// Minimum-viable appearance for a /FreeText note that ships no /AP
    /// (§12.5.6.6). FreeText had no case at all, so these pages came out
    /// blank while both reference renderers drew them.
    ///
    /// Measured, rather than assumed, on the corpus fixtures:
    ///
    ///   pdfium freetext_annotation_without_da.pdf  (/C present, /Rect 50x25)
    ///       mutool 1250 px, pdftocairo 1250 px — exactly the whole rectangle,
    ///       i.e. both FILL it with /C.
    ///   pdf.js bug1865341.pdf                      (no /C, /DA present)
    ///       mutool 212, pdftocairo 161 — an outline, not a fill.
    ///
    /// So: fill with /C when the annotation supplies one, otherwise outline.
    /// The note's TEXT is deliberately not laid out here — that needs full /DA
    /// + /RC variable-text handling, and the oracles disagree sharply about it
    /// (on freetext_no_appearance.pdf mutool inks 6067 px and pdftocairo 24),
    /// so there is no agreed answer to copy. Showing the reviewer that an
    /// annotation is THERE is the property that matters for redaction; what it
    /// says is already reachable through /Contents.
    /// </summary>
    private void RenderFreeTextDefault(Excise.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        float borderWidth = (float)(annot.BorderWidth ?? 1.0);
        if (borderWidth <= 0) borderWidth = 1.0f;

        using var paint = new SKPaint { IsAntialias = _options.AntiAlias };
        if (annot.Color is { } color)
        {
            var (r, g, b) = color;
            paint.Style = SKPaintStyle.Fill;
            paint.Color = RgbToColor(r, g, b);
            _canvas.DrawRect(rect, paint);
            return;
        }

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = borderWidth;
        paint.Color = SKColors.Black;
        _canvas.DrawRect(rect, paint);
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
    /// Sticky-note icon for a /Text annotation with no /AP (§12.5.6.4).
    /// </summary>
    /// <remarks>
    /// All three reference renderers draw one, which is what makes this a
    /// defect rather than a matter of taste — measured at 150 dpi on veraPDF
    /// 6-3-3-t01-pass-a.pdf: mutool 495 inked px, pdftocairo 917, Ghostscript
    /// 1388, excise 0.
    ///
    /// They emphatically do NOT agree on the ARTWORK: mutool draws black
    /// strokes, pdftocairo a grey-green (186,189,182) fill, Ghostscript grey
    /// plus black. So no attempt is made to reproduce any one of them. What
    /// they agree on — and all this draws — is that a ~16pt marker appears at
    /// the annotation's anchor point.
    ///
    /// That is the property that matters here: excise is a redaction tool, and
    /// a note the reviewer never sees is a note they cannot decide about, while
    /// its /Contents still ships to the recipient. Showing THAT one is present
    /// is the job; reproducing Acrobat's icon is not.
    /// </remarks>
    private void RenderStickyNoteDefault(Excise.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        var fill = annot.Color is { } c
            ? RgbToColor(c.Item1, c.Item2, c.Item3)
            : new SKColor(0xFF, 0xE1, 0x6B); // the usual note yellow

        using var body = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Fill,
            Color = fill,
        };
        using var border = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = SKColors.Black,
        };

        var round = new SKRoundRect(rect, rect.Width * 0.15f, rect.Height * 0.15f);
        _canvas.DrawRoundRect(round, body);
        _canvas.DrawRoundRect(round, border);

        // Two rules, so the marker reads as a note rather than a plain swatch.
        float inset = rect.Width * 0.22f;
        float y1 = rect.Top + rect.Height * 0.38f;
        float y2 = rect.Top + rect.Height * 0.60f;
        _canvas.DrawLine(rect.Left + inset, y1, rect.Right - inset, y1, border);
        _canvas.DrawLine(rect.Left + inset, y2, rect.Right - inset, y2, border);
    }

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
                        paint.Color = WithAlpha(baseColor, AnnotationOpacityAlpha(annot));
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

        // BLACK for every markup subtype, Highlight included.
        //
        // THE RULE, and it is not "copy mutool": when the file prescribes no
        // colour, paint with the INITIAL GRAPHICS STATE colour. §8.6.8 makes
        // that DeviceGray 0 — black. A renderer synthesizing an appearance
        // simply does not set a colour, so it gets the default one.
        //
        // That is why mutool, pdftocairo and excise's own FreeText and Link
        // paths all land on black without agreeing to: none of them chose it.
        // excise's markup path did choose — "the usual highlighter yellow" —
        // and a chosen default is an invented one.
        //
        // Measured on a /Highlight with no /C, same fixture, 150 dpi:
        //     excise      (255,255,0) x16848
        //     mutool      (0,0,0)     x16397
        //     pdftocairo  (0,0,0)     x16438
        //
        // Identical geometry, ~450px apart on a 17,000px shape — the ONLY
        // difference was the colour excise invented. Project decision
        // 2026-08-18: where the file states no colour and the engines differ,
        // take mutool's.
        return SKColors.Black;
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

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
    private void RenderTextFieldValue(
        Excise.Core.Document.PdfAnnotation annot, SKRect rect, string value)
    {
        ResolveAcroFormResources();

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
        var da = annot.RawDictionary.GetStringOrNull("DA") ?? _acroFormDa ?? "";

        _resourcesStack.Push(_acroFormDr);
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
                else if (q == 2) textX = rect.Right - textWidth - padX;
                else             textX = rect.Left + padX;

                // Vertical baseline: centre the font's OWN line box inside the
                // rect, read from the resolved typeface — the same metrics
                // AutoFitFontSize uses to pick the size. The previous rule
                // approximated the cap height at a flat fontSize * 0.7, which
                // is not any real font's, and drew the value 2-5 px above
                // every engine with the error scaling by size (#1016).
                float textY = BaselineForCentredLineBox(
                    rect, _currentFont?.Typeface, fontSize);

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
