using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Operations;

/// <summary>
/// Removes redacted terms from document-level text carriers (issue #608).
/// </summary>
/// <remarks>
/// Glyph removal is only as strong as the weakest carrier still holding the
/// string. A document routinely restates the text of its own pages in places
/// the content stream knows nothing about:
///
///   /Info          — Title, Author, Subject, Keywords
///   /Metadata      — the XMP packet, which is plain-text XML
///   /AcroForm /XFA — form-template and form-data XML packets
///   /Outlines      — bookmark titles, shown in the reader's sidebar
///   annotation /Contents — comment and markup text
///
/// A redacted name surviving in a bookmark title is visible in the navigation
/// pane without even opening the page. None of these carriers are reachable by
/// text extraction, so a content-stream assertion reports the document clean.
///
/// <para>
/// Scrubbing is <b>surgical</b>: only the offending substring is excised, and
/// unrelated values are left alone. The tempting alternative — deleting /Info,
/// /Metadata and /Outlines wholesale — would satisfy every leak assertion while
/// destroying the document's metadata and navigation. Callers that genuinely
/// want scorched earth should strip those dictionaries explicitly.
/// </para>
/// <para>
/// This is the DOCUMENT-level half of redaction. The PAGE-level half (content
/// stream, annotations, form fields, structure tree) is handled by
/// <c>PdfPageRedactionExtensions.RedactArea</c>.
/// </para>
/// </remarks>
public static class PdfDocumentSanitizer
{
    private static readonly string[] InfoKeys =
        { "Title", "Author", "Subject", "Keywords", "Creator", "Producer" };

    /// <summary>
    /// Shortest term we will act on. Excising one- and two-character fragments
    /// from every metadata string would corrupt unrelated values for no security
    /// benefit.
    /// </summary>
    private const int MinTermLength = 3;

    /// <summary>
    /// Remove every occurrence of <paramref name="terms"/> from the document's
    /// non-page text carriers.
    /// </summary>
    /// <returns>True if any carrier was modified.</returns>
    /// <param name="caseSensitive">
    /// Must match how the CALLER matched page content. #905: RedactText defaults
    /// to case-INsensitive glyph removal while this scrub was hard-coded to
    /// Ordinal, so redacting "smith" cleared the page and left "Smith" sitting in
    /// /Info /Title — the tool reporting success over a document that still names
    /// the person. An under-redaction is the failure that matters here, so the
    /// default is case-INsensitive: over-scrubbing metadata is recoverable, a
    /// surviving name is not.
    /// </param>
    public static bool ScrubTerms(
        PdfDocument document, IEnumerable<string> terms, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(terms);

        var actionable = terms
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length >= MinTermLength)
            .Distinct(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (actionable.Count == 0) return false;

        var changed = false;
        changed |= ScrubInfo(document, actionable, caseSensitive);
        changed |= ScrubXmpMetadata(document, actionable, caseSensitive);
        changed |= XfaXmlCarrier.ScrubTerms(document, actionable, caseSensitive).Changed;
        changed |= ScrubOutlines(document, actionable, caseSensitive);
        changed |= ScrubAnnotationContents(document, actionable, caseSensitive);
        changed |= ScrubFormFieldNames(document, actionable, caseSensitive);
        changed |= ScrubStructTree(document, actionable, caseSensitive);       // #1151
        changed |= ScrubJavaScript(document, actionable, caseSensitive);       // #1151
        changed |= ScrubEmbeddedFiles(document, actionable, caseSensitive);    // #1151
        changed |= ScrubActionUris(document, actionable, caseSensitive);       // #1168
        return changed;
    }

    private static bool ScrubInfo(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var info = document.Info;
        if (info == null) return false;

        var changed = false;
        foreach (var key in InfoKeys)
        {
            var value = ResolveStringOrNull(document, info, key);
            if (string.IsNullOrEmpty(value)) continue;

            var scrubbed = Excise(value, terms, caseSensitive);
            if (scrubbed == value) continue;

            if (scrubbed.Length == 0)
                info.Remove(key);
            else
                info[key] = new PdfString(scrubbed);

            changed = true;
        }
        return changed;
    }

    private static bool ScrubXmpMetadata(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        // #1129: EVERY reachable /Metadata packet, not just the catalog's.
        // §14.3.2 permits XMP on any object; a real CDC PDF kept the redacted
        // term in a page-level packet while the catalog packet scrubbed clean.
        var changed = false;
        foreach (var stream in document.EnumerateMetadataStreams())
        {
            // The XMP packet is plain-text XML. We treat it as text rather than
            // parsing it: a redacted name can appear in dc:title, dc:description,
            // pdf:Keywords, or a custom schema we have never heard of, and a
            // text-level excision catches all of them.
            var xmp = Encoding.UTF8.GetString(stream.DecodedData);
            var scrubbed = Excise(xmp, terms, caseSensitive);
            if (scrubbed == xmp) continue;

            // Write through the ENCODED bytes, not the decoded ones. The writer
            // serializes EncodedData; SetDecodedData only populates the decode
            // cache, so scrubbing that way would leave the secret in the saved
            // file while every in-memory read reported it gone.
            //
            // Storing the packet raw (dropping /Filter) is the conformant shape:
            // XMP must be readable without decompression anyway (§14.3.2).
            var bytes = Encoding.UTF8.GetBytes(scrubbed);
            stream.Remove("Filter");
            stream.Remove("DecodeParms");
            stream.SetEncodedData(bytes);
            stream["Length"] = new PdfInteger(bytes.Length);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// #1130 — AcroForm field NAMES (<c>/T</c>) and tooltips (<c>/TU</c>) carry
    /// human-readable text. A passport form named a field "Your name as printed
    /// on your most recent U..." and leaked the redacted term there, in a
    /// carrier no per-area scrub reaches (the field's widget need not overlap a
    /// redaction).
    ///
    /// <para>Cut the term out, keeping the rest, like #1038 does for <c>/V</c>.
    /// <c>/T</c> is referenced by name from <c>/Kids</c> parent chains and JS
    /// <c>getField()</c>, so excising it can break form logic — but a surviving
    /// secret is the failure that matters (the carrier policy: under-redaction
    /// over form fidelity). This is document-level and runs once, so it covers
    /// every field, not only those over a redaction box.</para>
    /// </summary>
    private static bool ScrubFormFieldNames(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        // Walk the raw /AcroForm/Fields tree, recursing through /Kids. The
        // leaking /T is often on a NON-TERMINAL parent field (< /Kids [...]
        // /T (Your name ...) >), which GetAcroForm().Fields does not enumerate.
        var acro = document.Resolve(document.Catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
        var rootFields = acro == null ? null
            : document.Resolve(acro.GetOptional("Fields") ?? PdfNull.Instance) as PdfArray;
        if (rootFields == null) return false;

        var changed = false;
        var visited = new HashSet<PdfDictionary>();
        var stack = new Stack<PdfObject>();
        foreach (var f in rootFields) stack.Push(f);

        while (stack.Count > 0)
        {
            if (document.Resolve(stack.Pop()) is not PdfDictionary node || !visited.Add(node))
                continue;

            // /T, /TU are labels — trailing residue after a cut is noise, so trim.
            foreach (var key in new[] { "T", "TU" })
            {
                if (document.Resolve(node.GetOptional(key) ?? PdfNull.Instance) is not PdfString str)
                    continue;
                var scrubbed = Excise(str.Value, terms, caseSensitive);
                if (scrubbed == str.Value) continue;
                node.SetString(key, scrubbed);
                changed = true;
            }

            // #1151: /V and /DV (the field VALUE and default value) are carriers
            // too — the #1115 canary survived there because only the AREA path
            // (#1038) scrubbed /V, so a value whose widget does not overlap a box
            // leaked. These are SEMANTIC values, so cut without trimming, exactly
            // as #1038 does — "Fallback SECRET" becomes "Fallback ", not
            // "Fallback" (and a second pass over an already-cut value is a no-op).
            foreach (var key in new[] { "V", "DV" })
            {
                if (document.Resolve(node.GetOptional(key) ?? PdfNull.Instance) is not PdfString str)
                    continue;
                var scrubbed = ExciseNoTrim(str.Value, terms, caseSensitive);
                if (scrubbed == str.Value) continue;
                node.SetString(key, scrubbed);
                changed = true;
            }

            if (document.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                foreach (var k in kids) stack.Push(k);
        }
        return changed;
    }

    /// <summary>
    /// #1151 — the structure tree restates text OUTSIDE the content stream in
    /// <c>/ActualText</c>, <c>/Alt</c> and <c>/E</c> (§14.9.4), and it survives
    /// glyph removal untouched (#636). #636's scrubber is AREA-based; this is the
    /// document-level, term-based one RedactText was missing — walk /StructTreeRoot
    /// through /K and cut the term from those three keys.
    /// </summary>
    private static bool ScrubStructTree(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        if (document.Resolve(document.Catalog?.GetOptional("StructTreeRoot") ?? PdfNull.Instance)
            is not PdfDictionary root)
            return false;

        var changed = false;
        var visited = new HashSet<PdfDictionary>();
        var stack = new Stack<PdfObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (document.Resolve(stack.Pop()) is not PdfDictionary node || !visited.Add(node))
                continue;
            foreach (var key in new[] { "ActualText", "Alt", "E" })
            {
                if (document.Resolve(node.GetOptional(key) ?? PdfNull.Instance) is not PdfString str)
                    continue;
                var scrubbed = Excise(str.Value, terms, caseSensitive);
                if (scrubbed == str.Value) continue;
                node.SetString(key, scrubbed);
                changed = true;
            }
            var kids = document.Resolve(node.GetOptional("K") ?? PdfNull.Instance);
            if (kids is PdfArray arr) foreach (var k in arr) stack.Push(k);
            else if (kids is PdfDictionary d) stack.Push(d);
        }
        return changed;
    }

    /// <summary>
    /// #1151 — JavaScript actions carry text in their <c>/JS</c> source. A
    /// document-level <c>/Names/JavaScript</c> name tree, plus <c>/OpenAction</c>
    /// and catalog <c>/AA</c>, can restate a redacted string; cut it out (the
    /// source is plain text, unlike an embedded binary).
    /// </summary>
    private static bool ScrubJavaScript(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var changed = false;
        var visited = new HashSet<PdfDictionary>();
        var stack = new Stack<PdfObject>();

        var names = document.Resolve(document.Catalog?.GetOptional("Names") ?? PdfNull.Instance) as PdfDictionary;
        if (names != null) stack.Push(names.GetOptional("JavaScript") ?? PdfNull.Instance);
        if (document.Catalog?.GetOptional("OpenAction") is { } oa) stack.Push(oa);
        if (document.Catalog?.GetOptional("AA") is { } aa) stack.Push(aa);

        var guard = 0;
        while (stack.Count > 0 && guard++ < 100_000)
        {
            var obj = document.Resolve(stack.Pop());
            if (obj is PdfDictionary node)
            {
                if (!visited.Add(node)) continue;
                if (document.Resolve(node.GetOptional("JS") ?? PdfNull.Instance) is PdfString js)
                {
                    var scrubbed = Excise(js.Value, terms, caseSensitive);
                    if (scrubbed != js.Value) { node.SetString("JS", scrubbed); changed = true; }
                }
                // Name-tree nodes (/Names, /Kids) and action chains (/Next).
                foreach (var key in new[] { "Names", "Kids", "Next" })
                    if (node.GetOptional(key) is { } sub) stack.Push(sub);
            }
            else if (obj is PdfArray a)
            {
                foreach (var e in a) stack.Push(e);
            }
        }
        return changed;
    }

    /// <summary>
    /// #1151 — an embedded file is a whole-binary carrier: you cannot surgically
    /// cut a term out of arbitrary bytes without risking corruption, so the safe
    /// action (the carrier policy: under-redaction over fidelity) is to REMOVE
    /// the attachment whose content contains the term — and ONLY that one, not
    /// the wholesale strip <c>RemoveAllMetadata</c> does. Matches on the decoded
    /// content, so an unrelated attachment survives.
    /// </summary>
    private static bool ScrubEmbeddedFiles(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var files = document.GetEmbeddedFiles();
        if (files.Count == 0) return false;

        var remove = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var f in files)
        {
            if (f.Bytes == null || f.Bytes.Length == 0) continue;
            var latin1 = Encoding.Latin1.GetString(f.Bytes);
            var utf8 = Encoding.UTF8.GetString(f.Bytes);
            if (terms.Any(t => Contains(latin1, t, caseSensitive) || Contains(utf8, t, caseSensitive)))
                remove.Add(f.RawDictionary);
        }
        if (remove.Count == 0) return false;

        var changed = false;
        if (document.Resolve(document.Catalog?.GetOptional("Names") ?? PdfNull.Instance) is PdfDictionary names
            && document.Resolve(names.GetOptional("EmbeddedFiles") ?? PdfNull.Instance) is PdfDictionary tree)
            changed |= FilterEmbeddedFileTree(document, tree, remove);

        changed |= FilterAssociatedFiles(document, document.Catalog, remove);
        for (var p = 1; p <= document.PageCount; p++)
            changed |= FilterAssociatedFiles(document, document.GetPage(p).Dictionary, remove);
        return changed;
    }

    // Remove (name, filespec) pairs whose resolved filespec is in `remove` from a
    // /Names/EmbeddedFiles name-tree node, recursing through /Kids.
    private static bool FilterEmbeddedFileTree(PdfDocument document, PdfDictionary node, HashSet<PdfDictionary> remove)
    {
        var changed = false;
        if (document.Resolve(node.GetOptional("Names") ?? PdfNull.Instance) is PdfArray pairs)
        {
            var kept = new PdfArray();
            for (var i = 0; i + 1 < pairs.Count; i += 2)
            {
                if (document.Resolve(pairs[i + 1]) is PdfDictionary fs && remove.Contains(fs))
                {
                    changed = true;   // drop this (name, filespec) pair
                    continue;
                }
                kept.Add(pairs[i]);
                kept.Add(pairs[i + 1]);
            }
            if (changed) node.Set("Names", kept);
        }
        if (document.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
            foreach (var k in kids)
                if (document.Resolve(k) is PdfDictionary kd)
                    changed |= FilterEmbeddedFileTree(document, kd, remove);
        return changed;
    }

    // Remove matching filespecs from an /AF (associated files) array (§7.7.4).
    private static bool FilterAssociatedFiles(PdfDocument document, PdfDictionary? owner, HashSet<PdfDictionary> remove)
    {
        if (owner == null || document.Resolve(owner.GetOptional("AF") ?? PdfNull.Instance) is not PdfArray af)
            return false;
        var kept = new PdfArray();
        var changed = false;
        foreach (var e in af)
        {
            if (document.Resolve(e) is PdfDictionary fs && remove.Contains(fs)) { changed = true; continue; }
            kept.Add(e);
        }
        if (changed) owner.Set("AF", kept);
        return changed;
    }

    private static bool Contains(string haystack, string term, bool caseSensitive) =>
        haystack.IndexOf(term, caseSensitive
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Read a string value, RESOLVING an indirect reference first — the load-
    /// bearing difference from <see cref="PdfDictionary.GetStringOrNull"/>, which
    /// returns null the moment the value is stored as <c>N 0 R</c> rather than a
    /// literal.
    ///
    /// <para>#1155: foss-primer stores every bookmark title as an indirect string
    /// object (<c>/Title 60 0 R</c> → <c>60 0 obj (Familiarize yourself…)</c>),
    /// and <see cref="ScrubOutlines"/> read those with <c>GetStringOrNull</c>, got
    /// null, and walked past a real carrier while reporting the scrub complete.
    /// The <c>CanaryInjectionLeakTests</c> suite missed it because its fixture
    /// writes the title as a DIRECT literal (<c>/Title (canary)</c>). /Info values
    /// and annotation /Contents can be indirect for the same reason, so every
    /// string-valued carrier in this file must resolve before it reads.</para>
    /// </summary>
    private static string? ResolveStringOrNull(PdfDocument document, PdfDictionary dict, string key) =>
        document.Resolve(dict.GetOptional(key) ?? PdfNull.Instance) is PdfString s ? s.Value : null;

    private static bool ScrubOutlines(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        if (document.Resolve(document.Catalog.GetOptional("Outlines") ?? PdfNull.Instance) is not PdfDictionary outlines)
            return false;

        var changed = false;
        var visited = new HashSet<PdfDictionary>();

        void Walk(PdfObject? node)
        {
            while (node != null)
            {
                if (document.Resolve(node) is not PdfDictionary item) return;
                if (!visited.Add(item)) return;   // guard against malformed cyclic /Next chains

                var title = ResolveStringOrNull(document, item, "Title");
                if (!string.IsNullOrEmpty(title))
                {
                    var scrubbed = Excise(title, terms, caseSensitive);
                    if (scrubbed != title)
                    {
                        // An emptied bookmark keeps its destination but loses its
                        // label; removing the node entirely would renumber the
                        // outline tree and orphan its children.
                        item["Title"] = new PdfString(scrubbed.Length == 0 ? "[redacted]" : scrubbed);
                        changed = true;
                    }
                }

                Walk(item.GetOptional("First"));   // descend into children
                node = item.GetOptional("Next");   // then continue along siblings
            }
        }

        Walk(outlines.GetOptional("First"));
        return changed;
    }

    private static bool ScrubAnnotationContents(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var changed = false;

        for (int i = 1; i <= document.PageCount; i++)
        {
            var page = document.GetPage(i);
            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;

            foreach (var annotObj in annots)
            {
                if (document.Resolve(annotObj) is not PdfDictionary annot) continue;

                // /Contents is the comment text; /T is the author-supplied title;
                // /RC is the RICH-TEXT variant of the comment (§12.7.3.4 / §12.5.6.2)
                // — an XHTML string that RESTATES /Contents and is a separate carrier.
                // #1185: a /Text sticky note kept "sticky note test1" in /RC after
                // /Contents was scrubbed, an intra-annotation asymmetry exactly like
                // the /A /URI one below. Excising the term from the raw string works
                // whether the value is plain or XHTML markup.
                foreach (var key in new[] { "Contents", "T", "RC" })
                {
                    var value = ResolveStringOrNull(document, annot, key);
                    if (string.IsNullOrEmpty(value)) continue;

                    var scrubbed = Excise(value, terms, caseSensitive);
                    if (scrubbed == value) continue;

                    if (scrubbed.Length == 0)
                        annot.Remove(key);
                    else
                        annot[key] = new PdfString(scrubbed);

                    changed = true;
                }

                // #1155: a link annotation's URI action carries the same string
                // as its /Contents (irs-1040-instructions restated
                // "https://www.irs.gov/your-account" in both). The loop above
                // excised /Contents and left /A /URI holding the term — an
                // intra-annotation asymmetry, and exactly the kind the carrier
                // policy calls a leak (the page and /Contents matched by
                // substring; the URI must too). Scrub the /URI with identical
                // semantics.
                changed |= ScrubUriAction(document, annot.GetOptional("A"), terms, caseSensitive);
            }
        }

        return changed;
    }

    /// <summary>
    /// #1155 — excise the term from a URI action's <c>/URI</c> string (§12.6.4.7),
    /// following the <c>/Next</c> chain (§12.6.3) so a term in a chained action is
    /// reached too. The action may be an indirect reference or an array of them.
    /// </summary>
    private static bool ScrubUriAction(
        PdfDocument document, PdfObject? actionObj, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var changed = false;
        var visited = new HashSet<PdfDictionary>();
        var stack = new Stack<PdfObject?>();
        stack.Push(actionObj);

        while (stack.Count > 0)
        {
            var resolved = document.Resolve(stack.Pop() ?? PdfNull.Instance);
            if (resolved is PdfArray arr)
            {
                foreach (var a in arr) stack.Push(a);
                continue;
            }
            if (resolved is not PdfDictionary action || !visited.Add(action)) continue;

            var uri = ResolveStringOrNull(document, action, "URI");
            if (!string.IsNullOrEmpty(uri))
            {
                var scrubbed = Excise(uri, terms, caseSensitive);
                if (scrubbed != uri)
                {
                    if (scrubbed.Length == 0)
                        action.Remove("URI");
                    else
                        action["URI"] = new PdfString(scrubbed);
                    changed = true;
                }
            }

            stack.Push(action.GetOptional("Next"));
        }

        return changed;
    }

    /// <summary>
    /// #1168 — a URI action (§12.6.4.7) can restate a redacted term in its
    /// <c>/URI</c> far from a page annotation's <c>/A</c>, which #1155 covered.
    /// This reaches every OTHER action-dictionary location the spec allows one
    /// in: catalog <c>/OpenAction</c> and <c>/AA</c> (§12.6), each page's
    /// <c>/AA</c>, each annotation's <c>/A</c> and <c>/AA</c> (§12.6.3), each
    /// AcroForm field's <c>/A</c> and <c>/AA</c> (walking <c>/Kids</c>), and each
    /// outline item's <c>/A</c> (§12.3.3). Every hit routes through
    /// <see cref="ScrubUriAction"/>, which already follows the <c>/Next</c> chain
    /// and dedups — re-scrubbing the annotation <c>/A</c> that
    /// <see cref="ScrubAnnotationContents"/> already handled is a harmless no-op
    /// (the term is gone), so this owns the complete set without the two methods
    /// having to agree on annotation enumeration.
    /// </summary>
    private static bool ScrubActionUris(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var changed = false;
        var catalog = document.Catalog;

        // Catalog-level: /OpenAction (may instead be a destination array, which
        // has no /URI — ScrubUriAction ignores it) and document /AA.
        changed |= ScrubUriAction(document, catalog?.GetOptional("OpenAction"), terms, caseSensitive);
        changed |= ScrubAdditionalActions(document, catalog?.GetOptional("AA"), terms, caseSensitive);

        // Every page and its annotations.
        for (int i = 1; i <= document.PageCount; i++)
        {
            var page = document.GetPage(i);
            changed |= ScrubAdditionalActions(document, page.Dictionary.GetOptional("AA"), terms, caseSensitive);

            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
                foreach (var annotObj in annots)
                    if (document.Resolve(annotObj) is PdfDictionary annot)
                    {
                        changed |= ScrubUriAction(document, annot.GetOptional("A"), terms, caseSensitive);
                        changed |= ScrubAdditionalActions(document, annot.GetOptional("AA"), terms, caseSensitive);
                    }
        }

        // AcroForm fields — a non-terminal field carries no widget on a page, so
        // the annotation walk above does not reach it. Walk /Fields and /Kids.
        if (document.Resolve(catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acro
            && document.Resolve(acro.GetOptional("Fields") ?? PdfNull.Instance) is PdfArray fields)
        {
            var fieldStack = new Stack<PdfObject>();
            foreach (var f in fields) fieldStack.Push(f);
            var visitedFields = new HashSet<PdfDictionary>();
            var guard = 0;
            while (fieldStack.Count > 0 && guard++ < 100_000)
            {
                if (document.Resolve(fieldStack.Pop()) is not PdfDictionary field || !visitedFields.Add(field)) continue;
                changed |= ScrubUriAction(document, field.GetOptional("A"), terms, caseSensitive);
                changed |= ScrubAdditionalActions(document, field.GetOptional("AA"), terms, caseSensitive);
                if (document.Resolve(field.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                    foreach (var k in kids) fieldStack.Push(k);
            }
        }

        // Outline items — /A can be a URI action; ScrubOutlines only touches
        // /Title. Walk the /First + /Next + /First(child) tree.
        if (document.Resolve(catalog?.GetOptional("Outlines") ?? PdfNull.Instance) is PdfDictionary outlines)
        {
            var stack = new Stack<PdfObject>();
            if (outlines.GetOptional("First") is { } first) stack.Push(first);
            var visited = new HashSet<PdfDictionary>();
            var guard = 0;
            while (stack.Count > 0 && guard++ < 100_000)
            {
                if (document.Resolve(stack.Pop()) is not PdfDictionary item || !visited.Add(item)) continue;
                changed |= ScrubUriAction(document, item.GetOptional("A"), terms, caseSensitive);
                if (item.GetOptional("Next") is { } next) stack.Push(next);
                if (item.GetOptional("First") is { } child) stack.Push(child);
            }
        }

        return changed;
    }

    /// <summary>
    /// #1168 — an additional-actions (<c>/AA</c>) dictionary (§12.6.3) maps
    /// trigger names (<c>/E</c>, <c>/X</c>, <c>/WC</c>, …) to action dictionaries.
    /// Scrub the URI of each entry's action (and its <c>/Next</c> chain).
    /// </summary>
    private static bool ScrubAdditionalActions(PdfDocument document, PdfObject? aaObj, IReadOnlyList<string> terms, bool caseSensitive)
    {
        if (document.Resolve(aaObj ?? PdfNull.Instance) is not PdfDictionary aa) return false;
        var changed = false;
        foreach (var key in aa.Keys)
            changed |= ScrubUriAction(document, aa.GetOptional(key.Value), terms, caseSensitive);
        return changed;
    }

    private static string Excise(string value, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var result = value;
        foreach (var term in terms)
            result = result.Replace(term, string.Empty, comparison);
        return result.Trim();
    }

    /// <summary>
    /// #1151 — cut the term but preserve surrounding whitespace, for SEMANTIC
    /// values (/V, /DV) where a trailing space is part of the value and #1038's
    /// area path keeps it. Idempotent: no match leaves the string identical, so a
    /// second scrub pass cannot mutate an already-cut value.
    /// </summary>
    private static string ExciseNoTrim(string value, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var result = value;
        foreach (var term in terms)
            result = result.Replace(term, string.Empty, comparison);
        return result;
    }
}
