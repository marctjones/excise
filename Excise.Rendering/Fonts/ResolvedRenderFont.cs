using Excise.Core.Text;
using SkiaSharp;

namespace Excise.Rendering.Fonts;

/// <summary>
/// The complete per-<c>Tf</c> font state the renderer needs to draw text:
/// the PDF-dictionary-level data in <see cref="Pdf"/>
/// (subtype, encoding, widths, ToUnicode — no SkiaSharp dependency, shareable
/// with text extraction if that becomes a shared model later) plus everything
/// specific to rendering with Skia — the loaded typeface, byte/CID-to-glyph
/// maps, and CID width/encoding data.
///
/// Replaces the 18 separate <c>_current*</c> fields that used to be set
/// together in <c>SetFont</c> and read individually all over
/// <c>SkiaRenderer.Text.cs</c> (#513). Covers all three font classes the
/// renderer draws: simple fonts, Type0/CID fonts, and Type3.
///
/// <b>Must stay immutable.</b> Nested content execution (Form XObjects,
/// tiling patterns, Type3 glyph procs) saves the active instance by
/// reference and restores it afterwards
/// (<c>_currentFont = saved</c>) — that only works as a snapshot if nothing
/// mutates a <see cref="ResolvedRenderFont"/> after it's built. Always
/// construct a new instance in the resolver; never add mutable members.
///
/// Deliberately NOT cached per font dictionary across <c>Tf</c> calls (unlike
/// <see cref="Typeface"/>/<see cref="ByteToGlyph"/>/<see cref="CffCidToGlyph"/>,
/// which reference the renderer's existing per-dict caches). Caching the
/// whole resolved object would change both the timing of encoding/width-map
/// construction and who owns the <see cref="SKTypeface"/> disposal lifecycle
/// — a different risk class from this field-collapse. Left as a follow-up.
/// </summary>
internal sealed record ResolvedRenderFont(
    ResolvedPdfFont Pdf,
    char[]? CodeToUnicode,
    Dictionary<char, byte>? UnicodeToCode,
    string?[]? CodeToGlyphName,
    SKTypeface? Typeface,
    ushort[]? ByteToGlyph,
    bool HasEmbeddedProgram,
    bool HasRawType1Program,
    Dictionary<int, float>? CidWidths,
    float CidDefaultWidth,
    bool CidUseUnicodeCmap,
    CidCMap? CidEncodingCMap,
    ushort[]? CidToGidMap,
    Dictionary<int, int>? CffCidToGlyph,
    IReadOnlyList<string> Diagnostics)
{
    // Flat forwarding properties onto the wrapped ResolvedPdfFont, so call
    // sites read _currentFont.Widths / .Dictionary / .IsType0 / etc. directly
    // instead of _currentFont.Pdf.Widths — matching the old flat _current*
    // access pattern call sites already use, to keep the port mechanical.
    public Excise.Core.Primitives.PdfDictionary? Dictionary => Pdf.Dictionary;
    public float[]? Widths => Pdf.Widths;
    public int FirstChar => Pdf.FirstChar;
    public float MissingWidth => Pdf.MissingWidth;
    public string EncodingName => Pdf.EncodingName;
    public bool IsType0 => Pdf.IsType0;
    public bool IsType3 => Pdf.IsType3;
}
