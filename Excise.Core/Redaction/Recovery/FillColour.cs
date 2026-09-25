using System;
using System.Globalization;
using Excise.Core.ColorSpaces;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// A fill colour as device RGB: the one colour vocabulary of the redaction audit
/// (#1830). Each detector keeps its own darkness threshold; what it compares is this.
/// </summary>
internal readonly record struct FillColour(double R, double G, double B)
{
    /// <summary>§10.4.2.2 gray level: the weights §11.3.5.3 <c>Lum</c> and the renderer use.</summary>
    public double Luminance => 0.3 * R + 0.59 * G + 0.11 * B;

    public bool IsDark(double maxLuminance) => Luminance <= maxLuminance;

    public bool IsNearlyWhite => R >= 0.95 && G >= 0.95 && B >= 0.95;

    /// <summary>DeviceCMYK as the renderer paints it, so the audit and the page agree on what is dark.</summary>
    public static FillColour FromCmyk(double c, double m, double y, double k)
    {
        var (r, g, b) = PdfColorConverter.CmykToRgb(c, m, y, k, PdfColorConverter.CmykPolicy.ProcessScreenPreview);
        return new FillColour(r, g, b);
    }

    /// <summary>K-only black previews as (0.14, 0.12, 0.13), and is still called black.</summary>
    public string Describe()
        => R < 0.15 && G < 0.15 && B < 0.15 ? "black"
        : Math.Abs(R - G) < 0.05 && Math.Abs(G - B) < 0.05
            ? string.Create(CultureInfo.InvariantCulture, $"gray({R:F2})")
            : string.Create(CultureInfo.InvariantCulture, $"rgb({R:F2},{G:F2},{B:F2})");
}
