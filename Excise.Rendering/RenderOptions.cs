using SkiaSharp;

namespace Excise.Rendering;

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

    /// <summary>
    /// Whether to draw the page's annotations. Default is <c>true</c>.
    ///
    /// Turning this OFF renders the page content stream alone. That is not a
    /// fidelity setting — annotations are genuinely part of what a conforming
    /// viewer shows (§12.5), and five of the six reference renderers draw them
    /// by default. It exists because "what is actually in the page, and what is
    /// overlaid on top of it" are different questions, and for a redaction tool
    /// the difference matters: a FreeText annotation LOOKS like page content and
    /// is not, while a Widget's value is real text that lives outside the
    /// content stream entirely.
    /// </summary>
    public bool RenderAnnotations { get; init; } = true;

    /// <summary>
    /// Optional diagnostic sink for recoverable rendering warnings, such as
    /// malformed page content skipped on best-effort viewer paths.
    /// </summary>
    internal ICollection<string>? Diagnostics { get; init; }
}
