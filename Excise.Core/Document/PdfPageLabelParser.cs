using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Parser for PDF page labels (ISO 32000-2:2020 §12.4.2).
/// The /Catalog may contain a /PageLabels entry → number tree mapping page indices to label dicts.
/// Each label dict contains optional /S (style), /P (prefix), and /St (start number).
/// </summary>
internal static class PdfPageLabelParser
{
    /// <summary>
    /// Parse the /PageLabels number tree from the catalog.
    /// Returns a dictionary mapping page index (0-based) → PdfPageLabel.
    /// Returns empty dictionary if no /PageLabels defined.
    /// </summary>
    public static Dictionary<int, PdfPageLabel> ParsePageLabels(PdfDocument doc)
    {
        var result = new Dictionary<int, PdfPageLabel>();
        foreach (var (key, value) in PdfNumberTree.Enumerate(doc, doc.Catalog.GetOptional("PageLabels")))
        {
            if (TryGetInteger(key, out var pageIndex) && doc.Resolve(value) is PdfDictionary labelDict)
                result[pageIndex] = ParseLabelDict(labelDict);
        }
        return result;
    }

    /// <summary>
    /// Parse a single label dictionary.
    /// May contain /S (style), /P (prefix), /St (start number).
    /// </summary>
    private static PdfPageLabel ParseLabelDict(PdfDictionary dict)
    {
        var style = dict.GetNameOrNull("S");
        var prefix = dict.GetStringOrNull("P");
        var startNum = 1;

        if (dict.GetOptional("St") is PdfInteger stInt)
            startNum = (int)stInt.Value;
        else if (dict.GetOptional("St") is PdfReal stReal)
            startNum = (int)stReal.Value;

        var labelStyle = style switch
        {
            "D" => PdfPageLabelStyle.Decimal,
            "R" => PdfPageLabelStyle.UppercaseRoman,
            "r" => PdfPageLabelStyle.LowercaseRoman,
            "A" => PdfPageLabelStyle.UppercaseLetters,
            "a" => PdfPageLabelStyle.LowercaseLetters,
            _ => PdfPageLabelStyle.None
        };

        return new PdfPageLabel(prefix, labelStyle, startNum);
    }

    private static bool TryGetInteger(PdfObject obj, out int value)
    {
        if (obj is PdfInteger i)
        {
            value = i;  // Implicit conversion from PdfInteger to int
            return true;
        }
        if (obj is PdfReal r)
        {
            value = (int)r.Value;
            return true;
        }
        value = 0;
        return false;
    }
}
