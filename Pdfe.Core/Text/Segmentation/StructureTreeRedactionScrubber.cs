using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Text.Segmentation;

/// <summary>
/// Removes sensitive text from the structure tree of a tagged PDF (issue #636).
/// </summary>
/// <remarks>
/// Glyph removal rewrites the content stream, but a tagged PDF holds the same
/// text in carriers the content stream knows nothing about:
///
///   /ActualText — the real text a marked-content span represents
///   /Alt        — the alternate description of a figure
///   /E          — the expansion of an abbreviation
///
/// Acrobat, screen readers, and every tag-aware extractor read all three. Left
/// alone, they spell out the redacted name in a document whose glyphs are
/// perfectly gone — and text extraction, which reads only the content stream,
/// reports the file clean. That combination (a real leak plus a green test) is
/// why this runs as part of redaction rather than as an optional sanitize step.
///
/// Two passes, deliberately overlapping:
///
///  1. <b>Structural.</b> Map the redacted region to the /MCID marked-content
///     ids whose content it covers, then scrub the carriers on struct elements
///     that own those ids. This is the precise, correct removal.
///
///  2. <b>Content-matching.</b> Scrub any carrier on this page that still spells
///     out text we just removed. This catches what pass 1 structurally cannot:
///     a Figure whose /Alt describes the redacted content but which carries no
///     MCID of its own, and any element whose /Pg we can resolve but whose
///     marked-content mapping is absent or malformed.
///
/// Pass 2 exists because redaction must fail closed. A carrier we cannot prove
/// is unrelated is a carrier we remove.
///
/// What this deliberately does NOT do is strip the structure tree. Removing the
/// tags would satisfy every leak assertion while destroying the document's
/// accessibility for whoever receives it (#631). Only the offending entries go.
/// </remarks>
internal static class StructureTreeRedactionScrubber
{
    /// <summary>Text carriers on a structure element, in scrub order.</summary>
    private static readonly string[] TextCarriers = { "ActualText", "Alt", "E" };

    /// <summary>
    /// Shortest removed run we will content-match on. One- and two-character
    /// fragments ("a", "of") match almost any alternate description and would
    /// turn pass 2 into "delete the structure tree".
    /// </summary>
    private const int MinMatchLength = 3;

    /// <summary>
    /// Scrub structure-tree text carriers covering <paramref name="area"/>.
    /// Must be called BEFORE the content stream is rewritten, while the page's
    /// original operators and letters still describe what is being removed.
    /// </summary>
    /// <returns>True if any carrier was removed.</returns>
    public static bool ScrubArea(PdfPage page, PdfRectangle area)
    {
        var doc = page.Document;

        var root = doc.Resolve(doc.Catalog.GetOptional("StructTreeRoot") ?? PdfNull.Instance) as PdfDictionary;
        if (root == null) return false;   // untagged document: nothing to scrub

        var affectedMcids = CollectAffectedMcids(page, area);
        var removedText = CollectRemovedText(page, area);

        if (affectedMcids.Count == 0 && removedText.Count == 0)
            return false;

        var pageRef = FindPageReference(doc, page);

        var changed = false;
        var visited = new HashSet<PdfDictionary>();
        Walk(doc, root.GetOptional("K"), pageRef, affectedMcids, removedText, visited, ref changed);
        return changed;
    }

    private static void Walk(
        PdfDocument doc,
        PdfObject? node,
        PdfReference? pageRef,
        HashSet<int> affectedMcids,
        IReadOnlyCollection<string> removedText,
        HashSet<PdfDictionary> visited,
        ref bool changed)
    {
        if (node == null) return;

        var resolved = doc.Resolve(node);

        if (resolved is PdfArray array)
        {
            foreach (var item in array)
                Walk(doc, item, pageRef, affectedMcids, removedText, visited, ref changed);
            return;
        }

        if (resolved is not PdfDictionary elem) return;
        if (!visited.Add(elem)) return;   // guard against cyclic /K graphs

        if (ScrubElement(doc, elem, pageRef, affectedMcids, removedText))
            changed = true;

        Walk(doc, elem.GetOptional("K"), pageRef, affectedMcids, removedText, visited, ref changed);
    }

    private static bool ScrubElement(
        PdfDocument doc,
        PdfDictionary elem,
        PdfReference? pageRef,
        HashSet<int> affectedMcids,
        IReadOnlyCollection<string> removedText)
    {
        var carriers = TextCarriers.Where(elem.ContainsKey).ToList();
        if (carriers.Count == 0) return false;

        // Only touch elements belonging to the page we are redacting. An element
        // with no /Pg is ambiguous; we fall through to content-matching rather
        // than assume it is safe.
        var elemPage = elem.GetOptional("Pg") as PdfReference;
        if (pageRef != null && elemPage != null && !elemPage.Equals(pageRef))
            return false;

        // Pass 1 — structural: does this element own a redacted MCID?
        var structural = affectedMcids.Count > 0
                         && CollectMcids(doc, elem.GetOptional("K")).Any(affectedMcids.Contains);

        var changed = false;
        foreach (var carrier in carriers)
        {
            var value = elem.GetStringOrNull(carrier);
            if (value == null) continue;

            // Pass 2 — content-matching: does the carrier still spell out
            // something we just removed from the glyphs?
            var restatesRemovedText = removedText.Any(t =>
                t.Length >= MinMatchLength &&
                value.Contains(t, StringComparison.Ordinal));

            if (structural || restatesRemovedText)
            {
                elem.Remove(carrier);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The /MCID values whose marked content intersects the redaction area.
    /// Walks the page's operators keeping a marked-content stack, exactly as a
    /// renderer would, so nesting is handled.
    /// </summary>
    private static HashSet<int> CollectAffectedMcids(PdfPage page, PdfRectangle area)
    {
        var affected = new HashSet<int>();
        var stack = new Stack<int?>();

        foreach (var op in page.GetContentStream().Operators)
        {
            switch (op.Name)
            {
                case "BDC":
                    stack.Push(ExtractMcid(op));
                    break;

                case "BMC":
                    stack.Push(null);   // marked content with no id
                    break;

                case "EMC":
                    if (stack.Count > 0) stack.Pop();
                    break;

                default:
                    if (stack.Count == 0) continue;
                    if (op.BoundingBox is not { } box) continue;
                    if (!Intersects(box, area)) continue;

                    // Every enclosing marked-content id is implicated: nested
                    // spans all describe the content we are about to delete.
                    foreach (var mcid in stack)
                        if (mcid is { } id) affected.Add(id);
                    break;
            }
        }

        return affected;
    }

    private static int? ExtractMcid(ContentOperator op)
    {
        // BDC operands: /Tag <</MCID n>>  — the property list may also be a
        // named resource, which carries no inline MCID for us to read.
        foreach (var operand in op.Operands)
        {
            if (operand is PdfDictionary props &&
                props.GetOptional("MCID") is PdfInteger mcid)
            {
                return (int)mcid.Value;
            }
        }
        return null;
    }

    private static IEnumerable<int> CollectMcids(PdfDocument doc, PdfObject? k)
    {
        if (k == null) yield break;

        var resolved = doc.Resolve(k);

        switch (resolved)
        {
            case PdfInteger direct:
                // /K 0 — the element's content is marked-content id 0.
                yield return (int)direct.Value;
                break;

            case PdfArray array:
                foreach (var item in array)
                {
                    if (doc.Resolve(item) is PdfInteger n)
                        yield return (int)n.Value;
                    else if (doc.Resolve(item) is PdfDictionary mcr &&
                             mcr.GetOptional("Type") is PdfName { Value: "MCR" } &&
                             mcr.GetOptional("MCID") is PdfInteger id)
                    {
                        // Marked-content reference (§14.7.4.3).
                        yield return (int)id.Value;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// The words whose glyphs the redaction is about to delete. Read from the
    /// page's own letters, so it reflects what will actually be removed rather
    /// than what the caller intended.
    /// </summary>
    private static IReadOnlyCollection<string> CollectRemovedText(PdfPage page, PdfRectangle area)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in page.GetWords())
        {
            if (!Intersects(word.BoundingBox, area)) continue;
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            removed.Add(word.Text);
        }

        return removed;
    }

    private static bool Intersects(PdfRectangle a, PdfRectangle b)
    {
        var x = a.Normalize();
        var y = b.Normalize();
        return x.Left < y.Right && x.Right > y.Left &&
               x.Bottom < y.Top && x.Top > y.Bottom;
    }

    private static PdfReference? FindPageReference(PdfDocument doc, PdfPage page)
    {
        // Struct elements point at their page by reference (/Pg). We need the
        // same reference to compare against.
        for (int i = 1; i <= doc.PageCount; i++)
        {
            if (i != page.PageNumber) continue;
            return doc.GetPageReference(i);
        }
        return null;
    }
}
