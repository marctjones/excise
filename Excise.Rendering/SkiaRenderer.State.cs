using Excise.Rendering.Transparency;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Graphics state for rendering.
/// </summary>
internal class GraphicsState
{
    public SKColor FillColor { get; set; } = SKColors.Black;
    public SKColor StrokeColor { get; set; } = SKColors.Black;
    public DeviceCmykColor? FillDeviceCmyk { get; set; } = new(0, 0, 0, 1);
    public DeviceCmykColor? StrokeDeviceCmyk { get; set; } = new(0, 0, 0, 1);
    public double LineWidth { get; set; } = 1;
    public float FillAlpha { get; set; } = 1.0f;
    public float StrokeAlpha { get; set; } = 1.0f;
    public int LineCap { get; set; } = 0;  // 0=Butt, 1=Round, 2=Square
    public int LineJoin { get; set; } = 0; // 0=Miter, 1=Round, 2=Bevel
    public float MiterLimit { get; set; } = 10.0f;
    public string FillColorSpace { get; set; } = "DeviceGray";
    public string StrokeColorSpace { get; set; } = "DeviceGray";
    public string? FillPatternName { get; set; }
    // Overprint control (ISO 32000-1 §8.6.7, ExtGState /OP, /op, /OPM — #634).
    // OP gates strokes, op gates fills; OPM 1 ("nonzero overprint mode") makes
    // a zero DeviceCMYK source component leave that colorant of the backdrop
    // unchanged instead of painting 0. Defaults per Table 58: both flags
    // false, OPM 0.
    public bool StrokeOverprint { get; set; }
    public bool FillOverprint { get; set; }
    public int OverprintMode { get; set; }
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;
    public Excise.Core.Primitives.PdfObject? SoftMask { get; set; }
    public SKMatrix CurrentTransform { get; set; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    // Dash pattern (PDF `d` operator): intervals in user-space units and a phase
    // offset. Null/empty means a solid line. ISO 32000-1 §8.4.3.6.
    public float[]? DashArray { get; set; }
    public float DashPhase { get; set; }

    /// <summary>
    /// The §8.4.1 Table 52 text parameters captured by the <c>q</c> that pushed
    /// this state, restored by the matching <c>Q</c> (#986). Null on a state
    /// that was never pushed by <c>q</c> (the initial state, and the states the
    /// nested-stream sites clone by hand).
    /// </summary>
    public TextParameterSnapshot? SavedTextParameters { get; set; }

    public GraphicsState Clone()
    {
        return new GraphicsState
        {
            FillColor = FillColor,
            StrokeColor = StrokeColor,
            FillDeviceCmyk = FillDeviceCmyk,
            StrokeDeviceCmyk = StrokeDeviceCmyk,
            LineWidth = LineWidth,
            FillAlpha = FillAlpha,
            StrokeAlpha = StrokeAlpha,
            LineCap = LineCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit,
            FillColorSpace = FillColorSpace,
            StrokeColorSpace = StrokeColorSpace,
            FillPatternName = FillPatternName,
            StrokeOverprint = StrokeOverprint,
            FillOverprint = FillOverprint,
            OverprintMode = OverprintMode,
            BlendMode = BlendMode,
            CurrentTransform = CurrentTransform,
            DashArray = DashArray,            // replaced wholesale by `d`, never mutated in place -> safe to share
            DashPhase = DashPhase,
            SoftMask = SoftMask,
            SavedTextParameters = SavedTextParameters,
        };
    }
}

/// <summary>
/// The §8.4.1 Table 52 text parameters — <c>Tf</c> (font and size), <c>Tc</c>,
/// <c>Tw</c>, <c>Tz</c>, <c>TL</c>, <c>Ts</c>, <c>Tr</c> — plus the resolved
/// font <c>Tf</c> derived from the font dictionary. Table 52 puts these in the
/// GRAPHICS state, so <c>q</c> saves them and <c>Q</c> restores them (#986).
///
/// <para>The resolved font travels with the name and size for the reason
/// <see cref="Excise.Core.Content.ContentStreamWalker"/>'s own snapshot gives
/// (#983): restoring the NAME alone would leave the renderer reporting "F1 @
/// 12" while drawing glyphs out of the bracketed font's typeface, widths and
/// encoding — a worse failure than not restoring at all. It is an
/// immutable-per-<c>Tf</c> reference, so this is a pointer copy and no font
/// re-resolution happens on restore.</para>
///
/// <para>The text MATRIX and line matrix are deliberately ABSENT. They are
/// §9.4.1 text-OBJECT state, not Table 52 graphics state: <c>BT</c> resets
/// them, and a <c>q</c>/<c>Q</c> pair that appears inside a text object (which
/// §8.2 does not permit, but real producers emit) leaves the pen where it is in
/// mupdf and poppler. Ghostscript rewinds it, so this is a measured 2-1 split
/// between reference implementations on a construct the spec disallows, not a
/// unanimous reading — see
/// <c>GraphicsStateTextParameterRenderingTests.QQ_InsideATextObject_DoesNotRewindThePen</c>.</para>
/// </summary>
internal readonly record struct TextParameterSnapshot(
    string FontName,
    float FontSize,
    float CharSpacing,
    float WordSpacing,
    float HorizontalScale,
    float TextLeading,
    float TextRise,
    int RenderMode,
    Fonts.ResolvedRenderFont? Font);

/// <summary>
/// Text state for rendering text operators.
/// </summary>
internal class TextState
{
    public string FontName { get; set; } = "";
    public float FontSize { get; set; } = 12;
    public float CharSpacing { get; set; } = 0;
    public float WordSpacing { get; set; } = 0;
    public float HorizontalScale { get; set; } = 100;
    public float TextLeading { get; set; } = 0;
    public float TextRise { get; set; } = 0;
    public int RenderMode { get; set; } = 0; // 0 = fill, 1 = stroke, 2 = fill+stroke

    // Text matrix components (Tm operator sets this)
    public float TextMatrixA { get; set; } = 1;
    public float TextMatrixB { get; set; } = 0;
    public float TextMatrixC { get; set; } = 0;
    public float TextMatrixD { get; set; } = 1;
    public float TextMatrixE { get; set; } = 0; // X position
    public float TextMatrixF { get; set; } = 0; // Y position

    // Line matrix (start of current line)
    public float LineMatrixE { get; set; } = 0;
    public float LineMatrixF { get; set; } = 0;

    public void Reset()
    {
        TextMatrixA = 1;
        TextMatrixB = 0;
        TextMatrixC = 0;
        TextMatrixD = 1;
        TextMatrixE = 0;
        TextMatrixF = 0;
        LineMatrixE = 0;
        LineMatrixF = 0;
    }

    public TextState Clone()
    {
        return new TextState
        {
            FontName = FontName,
            FontSize = FontSize,
            CharSpacing = CharSpacing,
            WordSpacing = WordSpacing,
            HorizontalScale = HorizontalScale,
            TextLeading = TextLeading,
            TextRise = TextRise,
            RenderMode = RenderMode,
            TextMatrixA = TextMatrixA,
            TextMatrixB = TextMatrixB,
            TextMatrixC = TextMatrixC,
            TextMatrixD = TextMatrixD,
            TextMatrixE = TextMatrixE,
            TextMatrixF = TextMatrixF,
            LineMatrixE = LineMatrixE,
            LineMatrixF = LineMatrixF
        };
    }
}
