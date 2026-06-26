using Pdfe.Core.Primitives;

namespace Pdfe.Rendering.Fonts;

internal sealed record ResolvedPdfFont(
    string ResourceName,
    PdfDictionary? Dictionary,
    string Subtype,
    string BaseFont,
    string EncodingName,
    PdfDictionary? EncodingDictionary,
    IReadOnlyDictionary<int, string>? ToUnicodeMap,
    float[]? Widths,
    int FirstChar,
    float MissingWidth,
    PdfDictionary? FontDescriptor)
{
    public bool IsType0 => Subtype == "Type0";
    public bool IsType3 => Subtype == "Type3";
}
