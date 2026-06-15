using SkiaSharp;

namespace Pdfe.Rendering;

/// <summary>
/// Graphics state for rendering.
/// </summary>
internal class GraphicsState
{
    public SKColor FillColor { get; set; } = SKColors.Black;
    public SKColor StrokeColor { get; set; } = SKColors.Black;
    public double LineWidth { get; set; } = 1;
    public float FillAlpha { get; set; } = 1.0f;
    public float StrokeAlpha { get; set; } = 1.0f;
    public int LineCap { get; set; } = 0;  // 0=Butt, 1=Round, 2=Square
    public int LineJoin { get; set; } = 0; // 0=Miter, 1=Round, 2=Bevel
    public float MiterLimit { get; set; } = 10.0f;
    public string FillColorSpace { get; set; } = "DeviceGray";
    public string StrokeColorSpace { get; set; } = "DeviceGray";
    public string? FillPatternName { get; set; }
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;
    public SKMatrix CurrentTransform { get; set; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    // Dash pattern (PDF `d` operator): intervals in user-space units and a phase
    // offset. Null/empty means a solid line. ISO 32000-1 §8.4.3.6.
    public float[]? DashArray { get; set; }
    public float DashPhase { get; set; }

    public GraphicsState Clone()
    {
        return new GraphicsState
        {
            FillColor = FillColor,
            StrokeColor = StrokeColor,
            LineWidth = LineWidth,
            FillAlpha = FillAlpha,
            StrokeAlpha = StrokeAlpha,
            LineCap = LineCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit,
            FillColorSpace = FillColorSpace,
            StrokeColorSpace = StrokeColorSpace,
            FillPatternName = FillPatternName,
            BlendMode = BlendMode,
            CurrentTransform = CurrentTransform,
            DashArray = DashArray,            // replaced wholesale by `d`, never mutated in place -> safe to share
            DashPhase = DashPhase
        };
    }
}

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
