namespace Excise.Core.Graphics;

/// <summary>
/// Represents a color in PDF graphics operations.
/// Supports both grayscale and RGB color spaces.
/// </summary>
public readonly struct PdfColor : IEquatable<PdfColor>
{
    /// <summary>
    /// Red component (0-1).
    /// </summary>
    public double R { get; }

    /// <summary>
    /// Green component (0-1).
    /// </summary>
    public double G { get; }

    /// <summary>
    /// Blue component (0-1).
    /// </summary>
    public double B { get; }

    /// <summary>
    /// Whether this is a grayscale color (R=G=B).
    /// </summary>
    public bool IsGrayscale => R == G && G == B;

    /// <summary>
    /// Creates an RGB color.
    /// </summary>
    public PdfColor(double r, double g, double b)
    {
        R = Math.Clamp(r, 0, 1);
        G = Math.Clamp(g, 0, 1);
        B = Math.Clamp(b, 0, 1);
    }

    /// <summary>
    /// Creates a grayscale color.
    /// </summary>
    public static PdfColor FromGray(double gray)
    {
        var g = Math.Clamp(gray, 0, 1);
        return new PdfColor(g, g, g);
    }

    /// <summary>
    /// Creates a color from RGB bytes (0-255).
    /// </summary>
    public static PdfColor FromRgb(byte r, byte g, byte b)
    {
        return new PdfColor(r / 255.0, g / 255.0, b / 255.0);
    }

    // Standard colors
    public static readonly PdfColor Black = FromGray(0);
    public static readonly PdfColor White = FromGray(1);
    public static readonly PdfColor Red = new(1, 0, 0);
    public static readonly PdfColor Green = new(0, 1, 0);
    public static readonly PdfColor Blue = new(0, 0, 1);
    public static readonly PdfColor Yellow = new(1, 1, 0);
    public static readonly PdfColor Cyan = new(0, 1, 1);
    public static readonly PdfColor Magenta = new(1, 0, 1);

    public bool Equals(PdfColor other) =>
        Math.Abs(R - other.R) < 0.0001 &&
        Math.Abs(G - other.G) < 0.0001 &&
        Math.Abs(B - other.B) < 0.0001;

    public override bool Equals(object? obj) => obj is PdfColor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(R, G, B);

    public static bool operator ==(PdfColor left, PdfColor right) => left.Equals(right);
    public static bool operator !=(PdfColor left, PdfColor right) => !left.Equals(right);

    public override string ToString() =>
        IsGrayscale ? $"Gray({R:F2})" : $"RGB({R:F2}, {G:F2}, {B:F2})";
}
