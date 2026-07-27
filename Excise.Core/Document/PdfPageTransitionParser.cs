using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Parser for a page's /Trans transition dictionary (ISO 32000-2:2020 §12.4.4).
/// /Trans is a direct (non-inheritable) entry on the page dictionary itself.
/// </summary>
internal static class PdfPageTransitionParser
{
    /// <summary>
    /// Parse the /Trans dictionary from a page. Returns null if absent or malformed.
    /// </summary>
    public static PdfPageTransition? Parse(PdfDocument doc, PdfDictionary pageDict)
    {
        var transObj = pageDict.GetOptional("Trans");
        if (transObj == null) return null;

        if (doc.Resolve(transObj) is not PdfDictionary trans) return null;

        var style = trans.GetNameOrNull("S") switch
        {
            "Split" => PdfTransitionStyle.Split,
            "Blinds" => PdfTransitionStyle.Blinds,
            "Box" => PdfTransitionStyle.Box,
            "Wipe" => PdfTransitionStyle.Wipe,
            "Dissolve" => PdfTransitionStyle.Dissolve,
            "Glitter" => PdfTransitionStyle.Glitter,
            "Fly" => PdfTransitionStyle.Fly,
            "Push" => PdfTransitionStyle.Push,
            "Cover" => PdfTransitionStyle.Cover,
            "Uncover" => PdfTransitionStyle.Uncover,
            "Fade" => PdfTransitionStyle.Fade,
            _ => PdfTransitionStyle.Replace, // /R, or /S absent
        };

        double duration = trans.ContainsKey("D") ? trans.GetNumber("D", 1.0) : 1.0;
        string? dimension = trans.GetNameOrNull("Dm");
        string? motion = trans.GetNameOrNull("M");

        int direction = 0;
        var diObj = trans.GetOptional("Di");
        if (diObj != null)
        {
            var resolvedDi = doc.Resolve(diObj);
            direction = resolvedDi switch
            {
                PdfInteger i => (int)i,
                PdfReal r => (int)r.Value,
                // Fly-only "None": moving directly inward/outward, no oblique angle.
                // Distinct from the numeric 315° direction (also legal per spec) —
                // -1 is an out-of-range sentinel so the two are never conflated.
                PdfName n when n.Value == "None" => -1,
                _ => 0,
            };
        }

        double? flyScale = trans.ContainsKey("SS") ? trans.GetNumber("SS", 1.0) : null;
        bool flyOpaque = trans.GetOptional("B") is PdfBoolean b && b.Value;

        return new PdfPageTransition(style, duration, dimension, motion, direction, flyScale, flyOpaque);
    }
}
