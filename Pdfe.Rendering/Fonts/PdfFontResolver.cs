using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Pdfe.Core.Text;

namespace Pdfe.Rendering.Fonts;

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
                           ?? "WinAnsiEncoding";

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
