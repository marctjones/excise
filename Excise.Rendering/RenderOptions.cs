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
    /// Show COMMENT annotations — Text notes, FreeText, the text-markup family,
    /// shapes, Ink, Stamp, FileAttachment, Caret. Review clutter a reader may
    /// reasonably want out of the way (#1021).
    /// </summary>
    /// <remarks>
    /// Two groups rather than one switch or twenty-three: for a redaction tool
    /// the useful split is "content I must decide about" against "review markup
    /// I may want hidden". Field VALUES are content; a reviewer's sticky notes
    /// are not. Per-subtype checkboxes were considered and rejected as more UI
    /// than this audience needs.
    /// </remarks>
    public bool ShowCommentAnnotations { get; init; } = true;

    /// <summary>
    /// Show FORM FIELDS and LINKS — <c>/Widget</c> and <c>/Link</c>. Kept apart
    /// from comments because a field's value is page content a reviewer has to
    /// see, even when review markup is hidden (#1021).
    /// </summary>
    public bool ShowFieldAndLinkAnnotations { get; init; } = true;

    /// <summary>
    /// AUDIT MODE: draw annotations that <c>/F</c> Hidden or NoView says to
    /// suppress (§12.5.3).
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately renders what NO conforming viewer shows, so it is off by
    /// default and must stay a distinct control rather than part of the normal
    /// visibility toggles. It exists because "there is something here the viewer
    /// is not showing you" is exactly what a person redacting a document needs
    /// to know — closer in purpose to <c>HiddenTextDetector</c> than to
    /// rendering. It must never be set on an export path.
    /// </remarks>
    public bool RevealHiddenAnnotations { get; init; }

    /// <summary>
    /// Tint form fields so a user can see what is fillable — Acrobat's
    /// "Highlight Existing Fields". Viewer chrome, not page content (#1021).
    /// </summary>
    /// <remarks>
    /// ⚠️ OFF by default, and it must never be set on an export or print path.
    /// A tinted field background baked into an exported raster is invented ink
    /// in a file that will be shared — the thing #1005 removed. A redaction tool
    /// must be able to show the page as it really is.
    /// </remarks>
    public bool HighlightFormFields { get; init; }

    /// <summary>
    /// Let go of the image sample bytes this render decoded, once they are no
    /// longer needed (#1468). Off by default.
    /// </summary>
    /// <remarks>
    /// <para>An image XObject's decoded samples live on its stream object, and
    /// the document caches that object until it closes — so without this every
    /// image a caller has ever rendered stays inflated in memory. For a caller
    /// that renders each page once and moves on (thumbnail pre-warm, a one-page
    /// export) that is pure retention: Altona's single page pins ~1 GiB.</para>
    ///
    /// <para>With it set, an image or mask stream this render caused to decode
    /// is released as soon as its bitmap is cached for the render, and any
    /// still held are released when the render ends. Streams that were already
    /// decoded when the render started are left alone, and so is any stream
    /// whose bytes were written by something other than the decoder — a
    /// redacted image is never reverted. Output pixels do not change; the cost
    /// is decoding again if the same image is needed again.</para>
    ///
    /// <para>⚠️ Leave it off where the same page is rendered repeatedly. The
    /// interactive viewer draws a page as several band renders, and releasing
    /// after each would inflate a large image once per band.</para>
    /// </remarks>
    public bool ReleaseDecodedImageSamples { get; init; }

    /// <summary>
    /// Optional sink that receives every image and mask stream whose samples
    /// this render read (#1492) — an image XObject, its <c>/SMask</c> or
    /// explicit <c>/Mask</c>, wherever it was drawn from: page content, a form,
    /// a pattern, a soft-mask group, an annotation appearance. Filled once, when
    /// the render ends (also when it throws), on the rendering thread.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ReleaseDecodedImageSamples"/> this records a stream
    /// whether or not it was already decoded: it answers "which samples does
    /// this page use", which is what lets the interactive viewer keep a
    /// realized page's samples pinned across its band renders and release them
    /// once the page is no longer realized. Recording the render's actual reads
    /// means no static walk has to mirror form, pattern, Type3, appearance and
    /// mask resolution, or optional content.
    /// </remarks>
    internal ICollection<Excise.Core.Primitives.PdfStream>? ImageSampleStreamSink { get; init; }

    /// <summary>
    /// Optional diagnostic sink for recoverable rendering warnings, such as
    /// malformed page content skipped on best-effort viewer paths.
    /// </summary>
    internal ICollection<string>? Diagnostics { get; init; }
}
