using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Authoring;

/// <summary>The PDF/A conformance level to target.</summary>
public enum PdfAConformance
{
    /// <summary>PDF/A-2b — visual reproducibility (recommended for archival).</summary>
    PdfA2B,

    /// <summary>PDF/A-1b — the original visual-reproducibility level.</summary>
    PdfA1B,
}

/// <summary>
/// Adds the document-level structures a PDF/A file requires: an XMP metadata
/// packet with the <c>pdfaid</c> identifier, and an OutputIntent referencing an
/// embedded sRGB ICC profile. The caller must also embed all fonts (e.g. via
/// <see cref="PdfDocumentBuilder.DefaultFont"/>) — PDF/A forbids the non-embedded
/// base-14 fonts.
/// </summary>
internal static class PdfAWriter
{
    public static void Apply(PdfDocument document, PdfAConformance conformance)
    {
        WriteXmp(document, conformance);
        WriteOutputIntent(document);
        RemoveForbiddenActions(document);
        document.InvalidateDerivedState(
            PdfDocumentDerivedStateScope.Metadata | PdfDocumentDerivedStateScope.CatalogActionsAndNames);
    }

    /// <summary>
    /// #1498 — remove the actions PDF/A forbids. The rules, read from veraPDF's
    /// own validation profiles (<c>PDFA-1B.xml</c>, <c>PDFA-2B.xml</c>) rather
    /// than from memory, because an earlier draft of this method got the shape
    /// of them wrong:
    ///
    /// <list type="bullet">
    ///   <item><b>The action-type rule is an ALLOW-list, not a JavaScript
    ///     ban.</b> 19005-1 6.6.1#1 permits only
    ///     <c>GoTo|GoToR|Thread|URI|Named|SubmitForm</c> (19005-2 6.5.1#1 adds
    ///     <c>GoToE</c>), and a <c>Named</c> action only for the four page
    ///     navigations (6.6.1#2 / 6.5.1#2). Launch, Sound, Movie, Hide and
    ///     friends fail it just as JavaScript does.</item>
    ///   <item><b>A widget annotation may not carry <c>/A</c> or <c>/AA</c> AT
    ///     ALL</b> — 19005-1 6.6.1#3 ("interactive form fields shall not perform
    ///     actions of any type", <c>containsA == false</c>) and 19005-2 6.4.1#1
    ///     (<c>containsA == false &amp;&amp; containsAA == false</c>). Filtering
    ///     only the JavaScript entries out of a widget's <c>/AA</c> and keeping
    ///     the rest still fails.</item>
    ///   <item><b>A field dictionary may not carry <c>/AA</c></b> — 19005-1
    ///     6.6.2#2, 19005-2 6.4.1#2. Same for the document catalog (19005-1
    ///     6.6.2#3).</item>
    /// </list>
    ///
    /// <para>So <c>/AA</c> goes wholesale from the catalog and from every field
    /// and widget dictionary, and <c>/A</c> goes wholesale from every widget —
    /// there the KEY is what is banned. Elsewhere — a Link annotation's
    /// <c>/A</c>, a page's <c>/AA</c>, <c>/OpenAction</c> — the key is fine and
    /// only a forbidden TYPE is removed, so a legal <c>/GoTo</c> or <c>/URI</c>
    /// survives; no PDF/A rule asks for more, and deleting working navigation
    /// nobody objected to would be overreach.</para>
    ///
    /// <para><b>Why here and not at authoring time.</b>
    /// <see cref="PdfDocumentBuilder.PdfA"/> may be called before or after the
    /// fields, so a check inside <c>AddDateField</c> would depend on call order.
    /// This runs as part of the PDF/A pre-save pass, so the rule is simply
    /// "PDF/A output carries none of these" regardless of how the document was
    /// assembled, and it also catches actions a caller wrote by hand.</para>
    ///
    /// <para>⚠️ <b>User-visible.</b> <see cref="AcroFormAuthoring.AddDateField"/>
    /// writes <c>/AA /F</c> and <c>/AA /K</c> format and keystroke actions; under
    /// <c>PdfA()</c> they are stripped, so the field no longer validates or
    /// reformats typed input. It stays a working text field with its name, rect,
    /// tooltip, flags and appearance intact. There is no conforming way to keep
    /// the enforcement — PDF/A's whole point is a file that renders without a
    /// scripting engine.</para>
    ///
    /// <para>⚠️ That loss is currently SILENT, and not for want of trying:
    /// <c>Excise.Core</c> has no save-path diagnostic channel to report it
    /// through — no logger, no warnings collection, and
    /// <c>RegisterPreSaveAction</c> returns <c>void</c>, so this method cannot
    /// hand anything back. Tracked by #1509, which also records why
    /// <c>PdfAStructuralValidator</c> is not the answer (after the strip it
    /// reports "no JavaScript present" — true, and useless). Do not bolt a
    /// one-off reporting hook onto this method; #1509 owns the shape.</para>
    /// </summary>
    private static void RemoveForbiddenActions(PdfDocument document)
    {
        var visited = new HashSet<PdfDictionary>();

        // Catalog: /AA wholesale (19005-1 6.6.2#3), an /OpenAction of a
        // forbidden type, and the document-level /Names/JavaScript name tree.
        document.Catalog.Remove("AA");
        if (IsForbiddenAction(document, document.Catalog.GetOptional("OpenAction")))
            document.Catalog.Remove("OpenAction");
        if (document.Resolve(document.Catalog.GetOptional("Names") ?? PdfNull.Instance) is PdfDictionary names)
            names.Remove("JavaScript");

        // The AcroForm field tree FIRST: a field node that is not itself a widget
        // still carries /AA, and its widgets hang off /Kids. Walking it before
        // the page annotations matters because `visited` gates the /Kids descent
        // — a merged field/widget dictionary reached first as a page annotation
        // would stop the walk from descending into its children.
        if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acroForm
            && document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) is PdfArray fields)
        {
            StripFieldTree(document, fields, visited, depth: 0);
        }

        // Pages and their annotations.
        for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
        {
            PdfDictionary pageDict;
            try { pageDict = document.GetPage(pageNumber).Dictionary; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }

            StripForbiddenActionTypes(document, pageDict);
            if (document.Resolve(pageDict.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
            {
                foreach (var annotObj in annots)
                {
                    if (document.Resolve(annotObj) is not PdfDictionary annot) continue;
                    if (IsWidget(annot))
                        StripAllActions(annot, visited);
                    else
                        StripForbiddenActionTypes(document, annot);
                }
            }
        }
    }

    private static bool IsWidget(PdfDictionary dict) => dict.GetNameOrNull("Subtype") == "Widget";

    private static void StripFieldTree(PdfDocument document, PdfArray nodes, HashSet<PdfDictionary> visited, int depth)
    {
        // A /Kids cycle in a malformed file must not become a stack overflow;
        // `visited` already stops re-entry, and the bound stops a pathological
        // but acyclic tree.
        if (depth > 64) return;

        foreach (var nodeObj in nodes)
        {
            if (document.Resolve(nodeObj) is not PdfDictionary node) continue;
            if (!StripAllActions(node, visited)) continue;   // already walked
            if (document.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                StripFieldTree(document, kids, visited, depth + 1);
        }
    }

    /// <summary>
    /// A field or widget dictionary: <c>/AA</c> and <c>/A</c> both go, whatever
    /// they hold. Returns false when this dictionary has already been handled.
    /// </summary>
    private static bool StripAllActions(PdfDictionary holder, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(holder)) return false;
        holder.Remove("AA");
        holder.Remove("A");
        return true;
    }

    /// <summary>
    /// Anything that is neither a field nor a widget — a page, a Link
    /// annotation: the KEY is allowed, only forbidden action TYPES are not, so
    /// a legal <c>/GoTo</c> or <c>/URI</c> survives. An <c>/AA</c> emptied of
    /// its forbidden entries is dropped, since an empty additional-actions
    /// dictionary carries no meaning.
    /// </summary>
    private static void StripForbiddenActionTypes(PdfDocument document, PdfDictionary holder)
    {
        if (IsForbiddenAction(document, holder.GetOptional("A")))
            holder.Remove("A");

        if (document.Resolve(holder.GetOptional("AA") ?? PdfNull.Instance) is not PdfDictionary additional)
            return;

        foreach (var key in additional.Keys.ToList())
        {
            if (IsForbiddenAction(document, additional.GetOptional(key.Value)))
                additional.Remove(key.Value);
        }
        if (additional.Count == 0)
            holder.Remove("AA");
    }

    /// <summary>
    /// The action types both profiles allow — 19005-1 6.6.1#1 and 19005-2
    /// 6.5.1#1, which are allow-lists, not JavaScript-specific bans. The
    /// 19005-1 set is used (it omits <c>GoToE</c>) so one pass satisfies both
    /// levels.
    /// </summary>
    private static readonly HashSet<string> AllowedActionTypes = new()
    {
        "GoTo", "GoToR", "Thread", "URI", "Named", "SubmitForm",
    };

    /// <summary>Named actions PDF/A permits (19005-1 6.6.1#2, 19005-2 6.5.1#2).</summary>
    private static readonly HashSet<string> AllowedNamedActions = new()
    {
        "NextPage", "PrevPage", "FirstPage", "LastPage",
    };

    /// <summary>
    /// Does <paramref name="action"/> name a type PDF/A does not permit —
    /// directly, or anywhere down its <c>/Next</c> chain (§12.6.1)? A chain
    /// counts as forbidden if any link is: keeping it would keep the forbidden
    /// action.
    /// </summary>
    private static bool IsForbiddenAction(PdfDocument document, PdfObject? action, int depth = 0)
    {
        if (action == null) return false;
        // A chain deeper than this is malformed; refusing to vouch for what we
        // did not walk is the safe answer for a conformance strip.
        if (depth > 32) return true;
        if (document.Resolve(action) is not PdfDictionary dict) return false;

        var type = dict.GetNameOrNull("S");
        if (type == null || !AllowedActionTypes.Contains(type)) return true;
        if (type == "Named")
        {
            var named = dict.GetNameOrNull("N");
            if (named == null || !AllowedNamedActions.Contains(named)) return true;
        }

        var next = dict.GetOptional("Next");
        if (next == null) return false;
        if (document.Resolve(next) is PdfArray chain)
            return chain.Any(link => IsForbiddenAction(document, link, depth + 1));
        return IsForbiddenAction(document, next, depth + 1);
    }

    private static void WriteXmp(PdfDocument document, PdfAConformance conformance)
    {
        var part = conformance == PdfAConformance.PdfA1B ? 1 : 2;
        var title = XmlEscape(document.Info?.GetStringOrNull("Title") ?? document.Title ?? string.Empty);
        var producer = XmlEscape(document.Info?.GetStringOrNull("Producer") ?? "excise");
        var creator = XmlEscape(document.Info?.GetStringOrNull("Creator") ?? string.Empty);

        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        sb.Append($"   <pdfaid:part>{part}</pdfaid:part>\n");
        sb.Append("   <pdfaid:conformance>B</pdfaid:conformance>\n");
        sb.Append("  </rdf:Description>\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append("   <dc:format>application/pdf</dc:format>\n");
        if (title.Length > 0)
        {
            sb.Append($"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>\n");
        }
        sb.Append("  </rdf:Description>\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        sb.Append($"   <pdf:Producer>{producer}</pdf:Producer>\n");
        sb.Append("  </rdf:Description>\n");
        if (creator.Length > 0)
        {
            sb.Append("  <rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
            sb.Append($"   <xmp:CreatorTool>{creator}</xmp:CreatorTool>\n");
            sb.Append("  </rdf:Description>\n");
        }
        sb.Append(" </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var dict = new PdfDictionary();
        dict.SetName("Type", "Metadata");
        dict.SetName("Subtype", "XML");
        dict.SetInt("Length", bytes.Length);
        document.Catalog["Metadata"] = document.AddIndirectObject(new PdfStream(dict, bytes));
    }

    private static void WriteOutputIntent(PdfDocument document)
    {
        if (document.Catalog.ContainsKey("OutputIntents")) return;

        var icc = Convert.FromBase64String(SrgbIccProfileBase64);
        var iccDict = new PdfDictionary();
        iccDict.SetInt("N", 3);
        iccDict.SetInt("Length", icc.Length);
        var iccRef = document.AddIndirectObject(new PdfStream(iccDict, icc));

        var outputIntent = new PdfDictionary();
        outputIntent.SetName("Type", "OutputIntent");
        outputIntent.SetName("S", "GTS_PDFA1");
        outputIntent.SetString("OutputConditionIdentifier", "sRGB IEC61966-2.1");
        outputIntent.SetString("Info", "sRGB IEC61966-2.1");
        outputIntent.Set("DestOutputProfile", iccRef);

        document.Catalog["OutputIntents"] = new PdfArray(outputIntent);
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Minimal valid sRGB v2 ICC profile (736 bytes), suitable for PDF/A OutputIntent embedding.
    private const string SrgbIccProfileBase64 =
        "AAAC4GxjbXMCEAAAbW50clJHQiBYWVogB+IAAwAUAAkADgAdYWNzcE1TRlQAAAAAc2F3c2N0cmwAAAAAAAAAAAAAAAAAAPbWAAEAAAAA0y1oYW5kk7I0qQ6wIoqY/Zqvo2eJmwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAABfY3BydAAAAQwAAAAMd3RwdAAAARgAAAAUclhZWgAAASwAAAAUZ1hZWgAAAUAAAAAUYlhZWgAAAVQAAAAUclRSQwAAAWgAAAF4Z1RSQwAAAWgAAAF4YlRSQwAAAWgAAAF4ZGVzYwAAAAAAAAAFc1JHQgAAAAAAAAAAAAAAAHRleHQAAAAAQ0MwAFhZWiAAAAAAAADzVAABAAAAARbJWFlaIAAAAAAAAG+gAAA48gAAA49YWVogAAAAAAAAYpYAALeJAAAY2lhZWiAAAAAAAAAkoAAAD4UAALbEY3VydgAAAAAAAAC2AAAAHAA4AFQAcACMAKgAxADhAQABIgFGAW0BlQHBAfACIAJVAosCxAMBAz8DggPGBA4EWQSnBPkFTAWkBf4GXAa+ByEHigf0CGMI1QlJCcMKPwq/C0ILyQxUDOENdA4JDqIPQA/gEIURLRHaEooTPhP2FLIVcRY2Fv0XyhiZGW4aRhsiHAMc5x3QHr0friCkIZ4inCOfJKUlsSbAJ9Uo7SoKKyssUS18Lqov3jEWMlIzlDTZNiQ3czjGOiA7fDzfPkU/sEEhQpZEEEWPRxJIm0ooS7tNUU7uUI9SNVPgVZBXRVkAWr5chF5MYBth72PHZaZniWlxa19tUW9KcUZzSnVRd155cXuIfaZ/yIHwhB6GUIiJisWNCY9RkZ+T85ZLmKubDp14n+eiW6TWp1ap26xnrvexj7Qqtsy5dLwhvtXBjcRMxxDJ2syrz3/SXNU92CTbEt4E4P7j/OcB6gztHPA081D2c/mb/Mr//w==";
}
