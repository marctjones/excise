using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Parser for PDF action dictionaries (ISO 32000-2:2020 §12.6) reached from
/// /Catalog/OpenAction, /Catalog/AA, a page's /AA, and /Catalog/Names/JavaScript.
/// Read-only: builds a <see cref="PdfAction"/> model and never executes anything.
/// </summary>
internal static class PdfActionParser
{
    /// <summary>
    /// Chains longer than this are truncated rather than followed further —
    /// generous enough for any legitimate /Next chain, small enough that a
    /// crafted or accidental cycle of indirect references can't recurse forever.
    /// </summary>
    private const int MaxChainDepth = 32;

    /// <summary>
    /// Parse a top-level action reference. Accepts either an action dictionary
    /// (/S ...) or — for legacy /OpenAction only — a bare destination array
    /// ([page /Fit ...]), which is modeled as an implicit GoTo. Returns null if
    /// <paramref name="raw"/> is null or resolves to neither shape.
    /// </summary>
    public static PdfAction? Parse(PdfDocument doc, PdfObject? raw)
    {
        if (raw == null) return null;
        var resolved = doc.Resolve(raw);

        if (resolved is PdfArray destArray)
        {
            var pageNum = ResolveDestinationArrayPage(doc, destArray);
            return new PdfAction("GoTo", DestinationPage: pageNum);
        }

        if (resolved is not PdfDictionary dict) return null;
        return ParseDictionary(doc, dict, depth: 0);
    }

    /// <summary>
    /// Parse an additional-actions (/AA) dictionary into a map of trigger name
    /// (e.g. "O", "C", "WC", "WS", "DS", "WP", "DP") to the parsed action.
    /// Returns an empty dictionary if <paramref name="aaObj"/> is null or not a dictionary.
    /// </summary>
    public static Dictionary<string, PdfAction> ParseAdditionalActions(PdfDocument doc, PdfObject? aaObj)
    {
        var result = new Dictionary<string, PdfAction>();
        if (aaObj == null) return result;
        if (doc.Resolve(aaObj) is not PdfDictionary aaDict) return result;

        foreach (var kvp in aaDict)
        {
            var action = Parse(doc, kvp.Value);
            if (action != null)
                result[kvp.Key.Value] = action;
        }

        return result;
    }

    private static PdfAction ParseDictionary(PdfDocument doc, PdfDictionary dict, int depth)
    {
        var type = dict.GetNameOrNull("S") ?? "Unknown";

        string? uri = null;
        string? js = null;
        string? namedName = null;
        int? destPage = null;

        switch (type)
        {
            case "URI":
                var uriObj = doc.Resolve(dict.GetOptional("URI") ?? (PdfObject)PdfNull.Instance);
                uri = (uriObj as PdfString)?.Value;
                break;

            case "JavaScript":
                js = DecodeJavaScript(doc, dict.GetOptional("JS"));
                break;

            case "Named":
                namedName = dict.GetNameOrNull("N");
                break;

            case "GoTo":
                var dRaw = dict.GetOptional("D");
                if (dRaw != null)
                    destPage = ResolveDestination(doc, dRaw);
                break;

            default:
                // GoToR, GoToE, Launch, SubmitForm, ResetForm, ImportData, Hide,
                // SetOCGState, Rendition, Trans, GoTo3DView, and anything else —
                // modeled by Type alone; excise never executes any action, so
                // there is no behavioral gap in leaving these fields unset.
                break;
        }

        IReadOnlyList<PdfAction>? next = null;
        if (depth < MaxChainDepth)
        {
            var nextObj = dict.GetOptional("Next");
            if (nextObj != null)
                next = ParseNextChain(doc, doc.Resolve(nextObj), depth + 1);
        }

        return new PdfAction(type, uri, js, namedName, destPage, next);
    }

    private static IReadOnlyList<PdfAction> ParseNextChain(PdfDocument doc, PdfObject resolvedNext, int depth)
    {
        var list = new List<PdfAction>();

        if (resolvedNext is PdfDictionary singleDict)
        {
            list.Add(ParseDictionary(doc, singleDict, depth));
        }
        else if (resolvedNext is PdfArray arr)
        {
            foreach (var item in arr)
            {
                if (doc.Resolve(item) is PdfDictionary itemDict)
                    list.Add(ParseDictionary(doc, itemDict, depth));
            }
        }

        return list;
    }

    /// <summary>
    /// Resolve a GoTo action's /D entry (direct destination array, or a name/string
    /// referring into the document's named-destination map) to a 1-based page number.
    /// </summary>
    private static int? ResolveDestination(PdfDocument doc, PdfObject dRaw)
    {
        var resolved = doc.Resolve(dRaw);

        if (resolved is PdfArray arr)
            return ResolveDestinationArrayPage(doc, arr);

        string? name = resolved switch
        {
            PdfName n => n.Value,
            PdfString s => s.Value,
            _ => null,
        };

        if (name != null && doc.GetNamedDestinations().TryGetValue(name, out var namedDest))
            return namedDest.PageNumber;

        return null;
    }

    private static int? ResolveDestinationArrayPage(PdfDocument doc, PdfArray destArray)
    {
        if (destArray.Count == 0 || destArray[0] is not PdfReference pageRef)
            return null;

        var pageRefToNumber = PdfOutlineParser.BuildPageRefMap(doc);
        return pageRefToNumber.TryGetValue((pageRef.ObjectNum, pageRef.Generation), out var pageNum)
            ? pageNum
            : null;
    }

    /// <summary>
    /// Decode a /JS entry, which per spec may be either a text string or a stream
    /// containing the script text. Reuses <see cref="PdfString"/>'s text-string
    /// decoding (UTF-16BE-with-BOM, UTF-8-with-BOM, or PDFDocEncoding) for both shapes.
    /// </summary>
    private static string? DecodeJavaScript(PdfDocument doc, PdfObject? jsObj)
    {
        if (jsObj == null) return null;
        var resolved = doc.Resolve(jsObj);

        return resolved switch
        {
            PdfString s => s.Value,
            PdfStream stream => TryDecodeStreamText(stream),
            _ => null,
        };
    }

    private static string? TryDecodeStreamText(PdfStream stream)
    {
        try
        {
            return new PdfString(stream.DecodedData).Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
