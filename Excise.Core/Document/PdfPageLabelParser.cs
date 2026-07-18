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
        var pageLabelsObj = doc.Catalog.GetOptional("PageLabels");
        if (pageLabelsObj == null) return result;

        var pageLabelsRoot = doc.Resolve(pageLabelsObj) as PdfDictionary;
        if (pageLabelsRoot == null) return result;

        // Number tree format: /Nums array [index dict index dict ...]
        // or /Kids array of subtrees (each with /Nums)
        WalkNumberTree(doc, pageLabelsRoot, result);
        return result;
    }

    /// <summary>
    /// Walk a PDF number tree recursively (§7.9.7).
    /// Leaves have /Nums array: [key value key value ...]
    /// Branches have /Kids array pointing to subtrees.
    /// </summary>
    private static void WalkNumberTree(
        PdfDocument doc,
        PdfDictionary node,
        Dictionary<int, PdfPageLabel> result)
    {
        // Leaf: /Nums array
        var numsObj = node.GetOptional("Nums");
        if (numsObj != null && doc.Resolve(numsObj) is PdfArray numsArr)
        {
            for (int i = 0; i + 1 < numsArr.Count; i += 2)
            {
                // First element is the key (page index as integer)
                if (!TryGetInteger(numsArr[i], out var pageIndex)) continue;

                // Second element is the value (label dictionary)
                var labelDict = doc.Resolve(numsArr[i + 1]) as PdfDictionary;
                if (labelDict != null)
                {
                    var label = ParseLabelDict(labelDict);
                    result[pageIndex] = label;
                }
            }
        }

        // Branch: /Kids array of subtrees
        var kidsObj = node.GetOptional("Kids");
        if (kidsObj != null && doc.Resolve(kidsObj) is PdfArray kidsArr)
        {
            foreach (var kidObj in kidsArr)
            {
                var kidDict = doc.Resolve(kidObj) as PdfDictionary;
                if (kidDict != null)
                    WalkNumberTree(doc, kidDict, result);
            }
        }
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
