using Excise.Core.Primitives;

namespace Excise.Core.Graphics;

/// <summary>Shape at the ends of open subpaths and dashes (the <c>J</c> operator, ISO 32000-2 §8.4.3.3).</summary>
public enum PdfLineCap
{
    /// <summary>Square end at the endpoint (spec default).</summary>
    Butt = 0,
    /// <summary>Semicircle centred on the endpoint.</summary>
    Round = 1,
    /// <summary>Square end projecting half the line width past the endpoint.</summary>
    Square = 2
}

/// <summary>Shape at the corners of stroked paths (the <c>j</c> operator, ISO 32000-2 §8.4.3.4).</summary>
public enum PdfLineJoin
{
    /// <summary>Mitered corner, bevelled past the miter limit (spec default).</summary>
    Miter = 0,
    /// <summary>Rounded corner.</summary>
    Round = 1,
    /// <summary>Bevelled corner.</summary>
    Bevel = 2
}

/// <summary>
/// Represents a pen for stroking paths in PDF graphics. Immutable: the optional
/// stroke state is set once with an object initializer, e.g.
/// <c>new PdfPen(PdfColor.Black, 0.5) { DashArray = [3, 3], Opacity = 0.35 }</c>.
/// Every optional property defaults to the ISO 32000-2 initial graphics state, and a
/// pen that leaves them at their defaults emits no operators for them.
/// </summary>
public class PdfPen
{
    private readonly IReadOnlyList<double>? _dashArray;
    private readonly double _dashPhase;
    private readonly PdfLineCap _lineCap;
    private readonly PdfLineJoin _lineJoin;
    private readonly double _miterLimit = 10;
    private readonly double _opacity = 1;

    /// <summary>
    /// The stroke color.
    /// </summary>
    public PdfColor Color { get; }

    /// <summary>
    /// The line width in points.
    /// </summary>
    public double Width { get; }

    /// <summary>
    /// Lengths of alternating dashes and gaps in user-space units (the <c>d</c>
    /// operator, ISO 32000-2 §8.4.3.6). <c>null</c> or empty strokes a solid line and
    /// reads back as <c>null</c>.
    /// </summary>
    /// <exception cref="ArgumentException">An element is negative or not finite, or all are zero.</exception>
    public IReadOnlyList<double>? DashArray
    {
        get => _dashArray;
        init
        {
            if (value is null || value.Count == 0)
            {
                _dashArray = null;
                return;
            }
            if (value.Any(v => !double.IsFinite(v) || v < 0) || value.All(v => v == 0))
                throw new ArgumentException("Dash lengths must be nonnegative and not all zero (ISO 32000-2 §8.4.3.6).", nameof(DashArray));
            _dashArray = Array.AsReadOnly(value.ToArray());
        }
    }

    /// <summary>
    /// Distance into the dash pattern at which stroking starts, in user-space units.
    /// Ignored for a solid line.
    /// </summary>
    public double DashPhase
    {
        get => _dashPhase;
        init => _dashPhase = double.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(DashPhase), value, "Dash phase must be finite.");
    }

    /// <summary>Line cap style (default <see cref="PdfLineCap.Butt"/>).</summary>
    public PdfLineCap LineCap
    {
        get => _lineCap;
        init => _lineCap = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(LineCap), value, null);
    }

    /// <summary>Line join style (default <see cref="PdfLineJoin.Miter"/>).</summary>
    public PdfLineJoin LineJoin
    {
        get => _lineJoin;
        init => _lineJoin = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(LineJoin), value, null);
    }

    /// <summary>
    /// Maximum ratio of miter length to line width before a miter join is bevelled
    /// (the <c>M</c> operator; default 10). The ratio is never below 1, so neither is
    /// a meaningful limit.
    /// </summary>
    public double MiterLimit
    {
        get => _miterLimit;
        init => _miterLimit = value >= 1 && double.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MiterLimit), value, "Miter limit must be a finite number of at least 1.");
    }

    /// <summary>
    /// Stroke opacity from 0 (transparent) to 1 (opaque, the default), clamped to that
    /// range. Written as the <c>/CA</c> entry of a page ExtGState resource.
    /// </summary>
    public double Opacity
    {
        get => _opacity;
        init => _opacity = double.IsNaN(value)
            ? throw new ArgumentOutOfRangeException(nameof(Opacity), value, null)
            : Math.Clamp(value, 0, 1);
    }

    /// <summary>
    /// Creates a pen with the specified color and width.
    /// </summary>
    public PdfPen(PdfColor color, double width = 1)
    {
        Color = color;
        Width = Math.Max(0, width);
    }

    // Standard pens
    public static readonly PdfPen Black = new(PdfColor.Black);
    public static readonly PdfPen White = new(PdfColor.White);
    public static readonly PdfPen Red = new(PdfColor.Red);

    /// <summary>
    /// Generates PDF operators to set this pen as stroke color.
    /// </summary>
    internal string GetStrokeColorOperator()
    {
        if (Color.IsGrayscale)
        {
            // Use 'G' for grayscale stroking color
            return $"{PdfNumberFormatter.Format(Color.R)} G";
        }
        else
        {
            // Use 'RG' for RGB stroking color
            return $"{PdfNumberFormatter.Format(Color.R)} {PdfNumberFormatter.Format(Color.G)} {PdfNumberFormatter.Format(Color.B)} RG";
        }
    }

    /// <summary>
    /// Generates PDF operator to set line width.
    /// </summary>
    internal string GetLineWidthOperator()
    {
        return $"{PdfNumberFormatter.Format(Width)} w";
    }
}
