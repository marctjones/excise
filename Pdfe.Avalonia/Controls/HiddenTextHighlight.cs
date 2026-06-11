using Avalonia;
using Pdfe.Core.Document;

namespace Pdfe.Avalonia.Controls;

/// <summary>
/// How a piece of hidden text was discovered. Drives the overlay
/// color in the GUI: <see cref="Structural"/> = yellow (we know the
/// exact characters from the content stream); <see cref="DifferentialOcr"/>
/// = orange (recovered from the underlying raster — confidence is
/// OCR-typical, not exact).
/// </summary>
public enum HiddenTextSource
{
    Structural,
    DifferentialOcr,
}

/// <summary>
/// One piece of text found on the current page that is present in the
/// PDF content stream but visually hidden by an overlay. Bounds carry their
/// coordinate space so the viewer can convert them into its current overlay
/// coordinate system.
/// </summary>
public sealed record HiddenTextHighlight(
    string Text,
    PdfPageRect Bounds,
    string HiddenBy,
    HiddenTextSource Source = HiddenTextSource.Structural)
{
    /// <summary>
    /// Legacy convenience for callers that still provide viewer-space bounds.
    /// Prefer <see cref="Bounds"/> and <see cref="PdfCoordinateMapper"/>.
    /// </summary>
    public Rect ScreenBounds => new(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
}
