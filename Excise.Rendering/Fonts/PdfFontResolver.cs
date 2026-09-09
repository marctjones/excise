using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Excise.Core.Text;

namespace Excise.Rendering.Fonts;

internal static class PdfFontResolver
{
    public static ResolvedPdfFont Resolve(string resourceName, PdfDictionary? fontDictionary, PdfDocument? document = null)
    {
        var subtype = fontDictionary?.GetNameOrNull("Subtype") ?? string.Empty;
        var baseFont = fontDictionary?.GetNameOrNull("BaseFont") ?? "Helvetica";
        var encodingDictionary = fontDictionary != null
            ? ResolveAs<PdfDictionary>(document, fontDictionary.GetOptional("Encoding"))
            : null;
        var encodingName = fontDictionary?.GetNameOrNull("Encoding")
                           ?? encodingDictionary?.GetNameOrNull("BaseEncoding")
                           ?? GetDefaultEncodingName(baseFont);

        float[]? widths = null;
        var firstChar = 0;
        var missingWidth = 0f;
        PdfDictionary? descriptor = null;
        IReadOnlyDictionary<int, string>? toUnicodeMap = null;
        if (fontDictionary != null)
        {
            var widthsArray = ResolveAs<PdfArray>(document, fontDictionary.GetOptional("Widths"));
            if (widthsArray != null && widthsArray.Count > 0)
            {
                firstChar = fontDictionary.GetInt("FirstChar", 0);
                widths = new float[widthsArray.Count];
                for (var i = 0; i < widthsArray.Count; i++)
                    widths[i] = (float)widthsArray.GetNumber(i);
            }
            else if (subtype == "Type1" && StandardFontMetrics.TryGetWidth(baseFont, 'A', out _))
            {
                // Standard-14 font, no /Widths array (ISO 32000-2 9.6.2.2: not
                // required for these -- a reader is expected to fall back to
                // the font's own built-in AFM metrics). subtype == "Type1" is
                // required, not just a name match: /BaseFont defaults to
                // "Helvetica" when absent (line above), and a Type3 font with
                // no /BaseFont at all -- common, since its CharProcs define
                // everything -- would otherwise get AFM Helvetica widths
                // instead of its own real per-glyph d0/d1 metrics (caught by
                // RenderPage_Type3Font_MissingWidths_AdvancesByCharProcWx).
                // Without this fallback at all, every
                // per-glyph cursor-advance site in SkiaRenderer.Text.cs that
                // gates on currentFont.Widths != null silently stopped
                // applying Tc/Tw within a string for exactly the common case
                // (plain Helvetica/Times/Courier with no /Widths) -- caught
                // by a mutool differential test, not by any existing test,
                // because everything downstream was internally consistent
                // with itself. Same authority (GetWidthOrFallback) already
                // used by the shared content walk (Excise.Core), so
                // rendering and extraction/redaction agree on these widths.
                firstChar = 0;
                widths = new float[256];
                for (var code = 0; code < 256; code++)
                    widths[code] = (float)StandardFontMetrics.GetWidthOrFallback(baseFont, code);
            }

            descriptor = ResolveAs<PdfDictionary>(document, fontDictionary.GetOptional("FontDescriptor"));
            if (descriptor != null)
                missingWidth = (float)descriptor.GetNumber("MissingWidth", 0);

            toUnicodeMap = TryLoadToUnicodeMap(fontDictionary, document);
        }

        return new ResolvedPdfFont(
            resourceName,
            fontDictionary,
            subtype,
            baseFont,
            encodingName,
            encodingDictionary,
            toUnicodeMap,
            widths,
            firstChar,
            missingWidth,
            descriptor);
    }

    public static PdfDictionary? ResolveDescendantFont(ResolvedPdfFont font, PdfDocument document)
    {
        if (font.Dictionary == null)
            return null;

        var descendants = ResolveAs<PdfArray>(document, font.Dictionary.GetOptional("DescendantFonts"));
        return descendants is { Count: > 0 }
            ? ResolveAs<PdfDictionary>(document, descendants[0])
            : null;
    }

    public static PdfArray? ResolveArray(PdfDocument document, PdfDictionary dictionary, string key)
        => ResolveAs<PdfArray>(document, dictionary.GetOptional(key));

    public static PdfDictionary? ResolveDictionary(PdfDocument document, PdfDictionary dictionary, string key)
        => ResolveAs<PdfDictionary>(document, dictionary.GetOptional(key));

    private static T? ResolveAs<T>(PdfDocument? document, PdfObject? obj)
        where T : PdfObject
    {
        if (obj == null)
            return null;

        var resolved = document != null ? document.Resolve(obj) : obj;
        return resolved as T;
    }

    private static string GetDefaultEncodingName(string baseFont)
    {
        var bareName = baseFont;
        if (bareName.Length >= 8 && bareName[6] == '+')
            bareName = bareName[7..];

        if (bareName.Contains("ZapfDingbats", StringComparison.OrdinalIgnoreCase)
            || bareName.Contains("Dingbat", StringComparison.OrdinalIgnoreCase))
            return "ZapfDingbatsEncoding";

        return "WinAnsiEncoding";
    }

    private static IReadOnlyDictionary<int, string>? TryLoadToUnicodeMap(
        PdfDictionary fontDictionary,
        PdfDocument? document)
    {
        var toUnicodeObj = fontDictionary.GetOptional("ToUnicode");
        if (toUnicodeObj == null)
            return null;

        try
        {
            return ResolveAs<PdfStream>(document, toUnicodeObj) is { } stream
                ? ToUnicodeCMapParser.Parse(stream.DecodedData)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
