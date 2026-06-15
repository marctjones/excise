using SkiaSharp;

namespace Pdfe.Rendering;

/// <summary>
/// Options for PDF page rendering.
/// </summary>
public record RenderOptions
{
    /// <summary>
    /// Maximum output pixels allowed for one rendered page. Prevents hostile or
    /// unusual PDFs from requesting native bitmaps large enough to exhaust memory.
    /// </summary>
    public const long DefaultMaxPixelCount = 256L * 1024L * 1024L;

    /// <summary>
    /// Resolution in dots per inch. Default is 150.
    /// </summary>
    public int Dpi { get; init; } = 150;

    /// <summary>
    /// Background color. Default is white.
    /// </summary>
    public SKColor BackgroundColor { get; init; } = SKColors.White;

    /// <summary>
    /// Whether to use anti-aliasing. Default is true.
    /// </summary>
    public bool AntiAlias { get; init; } = true;

    /// <summary>
    /// Optional clip rectangle (in page points).
    /// </summary>
    public SKRect? ClipRect { get; init; }

    /// <summary>
    /// Maximum output pixels allowed for one rendered page.
    /// </summary>
    public long MaxPixelCount { get; init; } = DefaultMaxPixelCount;
}
