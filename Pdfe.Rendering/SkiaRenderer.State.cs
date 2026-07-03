using SkiaSharp;

namespace Pdfe.Rendering;

internal readonly record struct DeviceCmykColor(double C, double M, double Y, double K);

internal sealed class DeviceCmykBackdrop
{
    private readonly byte[] _cyan;
    private readonly byte[] _magenta;
    private readonly byte[] _yellow;
    private readonly byte[] _black;
    private readonly byte[] _alpha;

    public DeviceCmykBackdrop(int width, int height)
    {
        Width = width;
        Height = height;
        var count = checked(width * height);
        _cyan = new byte[count];
        _magenta = new byte[count];
        _yellow = new byte[count];
        _black = new byte[count];
        _alpha = new byte[count];
    }

    public int Width { get; }
    public int Height { get; }

    public DeviceCmykColor Get(int x, int y)
    {
        var i = Index(x, y);
        return new DeviceCmykColor(
            _cyan[i] / 255.0,
            _magenta[i] / 255.0,
            _yellow[i] / 255.0,
            _black[i] / 255.0);
    }

    public double GetAlpha(int x, int y) => _alpha[Index(x, y)] / 255.0;

    public void CompositeSourceOver(int x, int y, DeviceCmykColor source, double alpha)
    {
        var i = Index(x, y);
        var sourceAlpha = Math.Clamp(alpha, 0, 1);
        var backdropAlpha = _alpha[i] / 255.0;
        var outputAlpha = sourceAlpha + (backdropAlpha * (1 - sourceAlpha));
        if (outputAlpha <= 1e-9)
        {
            _cyan[i] = 0;
            _magenta[i] = 0;
            _yellow[i] = 0;
            _black[i] = 0;
            _alpha[i] = 0;
            return;
        }

        _cyan[i] = BlendByte(_cyan[i], source.C, sourceAlpha, backdropAlpha, outputAlpha);
        _magenta[i] = BlendByte(_magenta[i], source.M, sourceAlpha, backdropAlpha, outputAlpha);
        _yellow[i] = BlendByte(_yellow[i], source.Y, sourceAlpha, backdropAlpha, outputAlpha);
        _black[i] = BlendByte(_black[i], source.K, sourceAlpha, backdropAlpha, outputAlpha);
        _alpha[i] = ToByte(outputAlpha);
    }

    public void Set(int x, int y, DeviceCmykColor color, double alpha = 1)
    {
        var i = Index(x, y);
        _cyan[i] = ToByte(color.C);
        _magenta[i] = ToByte(color.M);
        _yellow[i] = ToByte(color.Y);
        _black[i] = ToByte(color.K);
        _alpha[i] = ToByte(alpha);
    }

    public DeviceCmykBackdrop Clone()
    {
        var clone = new DeviceCmykBackdrop(Width, Height);
        Array.Copy(_cyan, clone._cyan, _cyan.Length);
        Array.Copy(_magenta, clone._magenta, _magenta.Length);
        Array.Copy(_yellow, clone._yellow, _yellow.Length);
        Array.Copy(_black, clone._black, _black.Length);
        Array.Copy(_alpha, clone._alpha, _alpha.Length);
        return clone;
    }

    private int Index(int x, int y) => y * Width + x;

    private static byte BlendByte(
        byte backdrop,
        double source,
        double sourceAlpha,
        double backdropAlpha,
        double outputAlpha)
    {
        var value = ((backdrop / 255.0 * backdropAlpha * (1 - sourceAlpha)) +
                     (Math.Clamp(source, 0, 1) * sourceAlpha)) /
                    outputAlpha;
        return (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0, 1) * 255), 0, 255);
}

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
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;
    public Pdfe.Core.Primitives.PdfObject? SoftMask { get; set; }
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
            BlendMode = BlendMode,
            CurrentTransform = CurrentTransform,
            DashArray = DashArray,            // replaced wholesale by `d`, never mutated in place -> safe to share
            DashPhase = DashPhase,
            SoftMask = SoftMask,
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
