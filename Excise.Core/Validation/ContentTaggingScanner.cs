using System.Collections.Generic;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Validation;

/// <summary>
/// Scans a page content stream for text-showing operators that are neither
/// inside the structure tree (a marked-content span whose /MCID the tree
/// references) nor marked as an <c>/Artifact</c>. Such text is "untagged real
/// content", the PDF/UA §7.1 violation.
///
/// <para>Reads the raw operator stream rather than going through
/// <see cref="Text.TextExtractor"/>, so it can distinguish an artifact from
/// untagged content (which <see cref="Text.Letter"/> cannot). The nesting is
/// the parser's <see cref="ContentOperator.EnclosingSpans"/> stamp (#1848).</para>
/// </summary>
internal static class ContentTaggingScanner
{
    /// <summary>
    /// Count text-showing operators on <paramref name="page"/> that draw visible
    /// text yet fall outside both the structure tree and any /Artifact span.
    /// </summary>
    public static int CountUntaggedTextRuns(
        PdfPage page,
        int pageNumber,
        HashSet<(int, int)> taggedQualified,
        HashSet<int> taggedPageAgnostic)
    {
        IReadOnlyList<ContentOperator> ops;
        try { ops = page.GetContentStream().Operators; }
        catch { return 0; }

        int untagged = 0;
        foreach (var op in ops)
        {
            if (op.Name is not ("Tj" or "TJ" or "'" or "\"") || !HasVisibleText(op.Operands)) continue;

            // Outermost first: an /Artifact anywhere around the text covers it,
            // and the innermost /MCID is the one it belongs to.
            var artifact = false;
            int? mcid = null;
            foreach (var span in op.EnclosingSpans)
            {
                artifact |= span.Operands.Count > 0 && span.Operands[0] is PdfName { Value: "Artifact" };
                if (span.Name == "BDC" && span.Operands.Count > 1 && span.Operands[1] is PdfDictionary props
                    && props.GetOptional("MCID") is PdfInteger m)
                    mcid = (int)m.Value;
            }
            if (artifact) continue;                                              // artifact — fine
            if (mcid is int id &&
                (taggedQualified.Contains((pageNumber, id)) || taggedPageAgnostic.Contains(id)))
                continue;                                                        // inside struct tree — fine
            untagged++;
        }

        return untagged;
    }

    private static bool HasVisibleText(IReadOnlyList<PdfObject> operands)
    {
        foreach (var o in operands)
        {
            if (o is PdfString s && s.Bytes.Length > 0) return true;
            if (o is PdfArray arr)
                foreach (var e in arr)
                    if (e is PdfString es && es.Bytes.Length > 0) return true;
        }
        return false;
    }
}
