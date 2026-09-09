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
        => ScrubTerms(document, terms, caseSensitive, RedactionCarriers.All);

    /// <summary>
    /// As <see cref="ScrubTerms(PdfDocument, IEnumerable{string}, bool)"/>, but
    /// scrubbing only the carriers in <paramref name="carriers"/> (#1188).
    /// <see cref="RedactionCarriers.All"/> is the default and the safe choice —
    /// disabling a carrier can leave the term in the document.
    /// </summary>
    public static bool ScrubTerms(
        PdfDocument document, IEnumerable<string> terms, bool caseSensitive,
        RedactionCarriers carriers)
        => ScrubTerms(document, terms, caseSensitive, carriers, CarrierScrubPolicy.Default).Changed;

    /// <summary>
    /// As the other overloads, but with a per-carrier <see cref="CarrierScrubMode"/>
    /// (#1188/#1169) and a per-carrier report of what actually ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CarrierScrubPolicy.Default"/> is <see cref="CarrierScrubMode.Strip"/>
    /// on every carrier, which is byte-for-byte the pre-#1188 behaviour — the
    /// bool-returning overloads above are this method with that policy.
    /// </para>
    /// <para>
    /// ⚠️ The returned rows are the ONLY way a caller can tell a scrubbed
    /// carrier from a <see cref="CarrierScrubMode.ReportOnly"/> one that still
    /// holds the term. Ignoring them turns an opt-in policy into a silent leak.
    /// </para>
    /// </remarks>
    public static CarrierScrubOutcome ScrubTerms(
        PdfDocument document, IEnumerable<string> terms, bool caseSensitive,
        RedactionCarriers carriers, CarrierScrubPolicy policy, bool wholeWord = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(policy);

        var actionable = terms
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length >= MinTermLength)
            .Distinct(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (actionable.Count == 0) return CarrierScrubOutcome.Empty;

        var changed = false;
        var invalidation = PdfDocumentDerivedStateScope.None;
        var rows = new List<CarrierScrubResult>();

        // One carrier stage: build its scrub context, run it, record the row.
        void Stage(
            RedactionCarriers carrier,
            Func<CarrierScrub, bool> run,
            PdfDocumentDerivedStateScope scope = PdfDocumentDerivedStateScope.None,
            string? unsupportedRemoveWholeReason = null,
            Func<bool>? carrierPresent = null)
        {
            if ((carriers & carrier) == 0) return;

            var mode = policy.ModeFor(carrier);
            if (mode == CarrierScrubMode.RemoveWhole && unsupportedRemoveWholeReason != null)
            {
                // Nothing to refuse when the document does not have this carrier
                // at all — a refusal row there is noise that trains people to
                // ignore refusal rows.
                if (carrierPresent != null && !carrierPresent()) return;

                // Never silently downgrade to a mode the caller did not ask for.
                rows.Add(new CarrierScrubResult(carrier, mode, TermFound: false,
                    Modified: false, RefusedReason: unsupportedRemoveWholeReason));
                return;
            }

            var scrub = new CarrierScrub(actionable, caseSensitive, mode, wholeWord);
            var stageChanged = run(scrub);
            changed |= stageChanged;
            if (stageChanged) invalidation |= scope;
            rows.Add(new CarrierScrubResult(carrier, mode, scrub.TermFound, stageChanged));
        }

        Stage(RedactionCarriers.Info, s => ScrubInfo(document, s), PdfDocumentDerivedStateScope.Metadata);
        Stage(RedactionCarriers.Xmp, s => ScrubXmpMetadata(document, s), PdfDocumentDerivedStateScope.Metadata);
        Stage(RedactionCarriers.Xfa, s => ScrubXfa(document, s), PdfDocumentDerivedStateScope.None,
            // An XFA packet is one XML form: dropping it wholesale destroys the
            // form rather than one value, and there is no "the value the term was
            // in" to remove without re-deciding the XML semantics. Refuse and say
            // so (the carrier policy: surface, don't guess).
            unsupportedRemoveWholeReason:
                "RemoveWhole is not defined for the XFA packet — dropping it destroys the whole form; use Strip or ReportOnly",
            carrierPresent: () => XfaXmlCarrier.CountUnexaminedPackets(document, null) > 0);
        Stage(RedactionCarriers.Outlines, s => ScrubOutlines(document, s));
        Stage(RedactionCarriers.Annotations, s => ScrubAnnotationContents(document, s));
        Stage(RedactionCarriers.FormFields, s => ScrubFormFieldNames(document, s));
        Stage(RedactionCarriers.StructTree, s => ScrubStructTree(document, s), PdfDocumentDerivedStateScope.StructureAndTagging); // #1151
        Stage(RedactionCarriers.JavaScript, s => ScrubJavaScript(document, s), PdfDocumentDerivedStateScope.CatalogActionsAndNames); // #1151
        Stage(RedactionCarriers.EmbeddedFiles, s => ScrubEmbeddedFiles(document, s), PdfDocumentDerivedStateScope.Attachments); // #1151
        Stage(RedactionCarriers.ActionUris, s => ScrubActionUris(document, s), PdfDocumentDerivedStateScope.CatalogActionsAndNames); // #1168

        if (invalidation != PdfDocumentDerivedStateScope.None)
            document.InvalidateDerivedState(invalidation);

        return new CarrierScrubOutcome(changed, rows);
    }

    /// <summary>
    /// The per-carrier scrub decision, in one place (#1188). Each carrier walker
    /// asks this what to write instead of calling <c>Excise</c> directly, so
    /// <see cref="CarrierScrubMode"/> cannot be honoured in one carrier and
    /// forgotten in another.
    /// </summary>
    private sealed class CarrierScrub
    {
        private readonly IReadOnlyList<string> _terms;
        private readonly bool _caseSensitive;
        private readonly bool _wholeWord;

        internal CarrierScrub(
            IReadOnlyList<string> terms, bool caseSensitive, CarrierScrubMode mode,
            bool wholeWord = false)
        {
            _terms = terms;
            _caseSensitive = caseSensitive;
            Mode = mode;
            _wholeWord = wholeWord;
        }

        internal CarrierScrubMode Mode { get; }

        /// <summary>True once any value in this carrier was seen to hold a term.</summary>
        internal bool TermFound { get; private set; }

        /// <summary>
        /// Record a hit a carrier detected its own way (XFA parses XML rather
        /// than reading string-keyed values).
        /// </summary>
        internal void MarkTermFound() => TermFound = true;

        internal IReadOnlyList<string> Terms => _terms;
        internal bool CaseSensitive => _caseSensitive;

        /// <summary>Does <paramref name="value"/> hold a term? Records the hit.</summary>
        internal bool Hits(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var term in _terms)
            {
                if (IndexOfTerm(value, term, _caseSensitive, _wholeWord, 0) < 0) continue;
                TermFound = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// What should replace <paramref name="value"/>? False means write
        /// nothing — either no match, or the mode forbids modifying.
        /// <paramref name="replacement"/> is empty when the whole value goes;
        /// each call site already knows what "empty" means for its key (remove
        /// it, or substitute a placeholder).
        /// </summary>
        internal bool TryApply(string? value, out string replacement, bool trim = true)
        {
            replacement = value ?? string.Empty;
            if (string.IsNullOrEmpty(value)) return false;

            // Strip keeps its exact pre-#1188 shape, including Excise's trim:
            // computing the replacement first is what decides "changed".
            if (Mode == CarrierScrubMode.Strip)
            {
                Hits(value);
                var scrubbed = ExciseCore(value, _terms, _caseSensitive, _wholeWord, trim);
                if (scrubbed == value) return false;
                replacement = scrubbed;
                return true;
            }

            if (!Hits(value)) return false;
            if (Mode == CarrierScrubMode.ReportOnly) return false;

            replacement = string.Empty;   // RemoveWhole
            return true;
        }
    }

    /// <summary>
    /// XFA is XML, not a string-keyed dictionary, so it cannot go through
    /// <see cref="CarrierScrub.TryApply"/> per value. Strip rewrites the packet;
    /// ReportOnly only counts packets holding the term. (RemoveWhole is refused
    /// upstream.)
    /// </summary>
    private static bool ScrubXfa(PdfDocument document, CarrierScrub scrub)
    {
        if (scrub.Mode == CarrierScrubMode.ReportOnly)
        {
            if (XfaXmlCarrier.CountUnexaminedPackets(document, scrub.Terms, scrub.CaseSensitive) > 0)
                scrub.MarkTermFound();   // record the hit without touching the packet
            return false;
        }

        var result = XfaXmlCarrier.ScrubTerms(document, scrub.Terms, scrub.CaseSensitive);
        if (result.Changed || result.UnexaminedPacketCount > 0)
            scrub.MarkTermFound();
        return result.Changed;
    }

    private static bool ScrubInfo(PdfDocument document, CarrierScrub scrub)
    {
        var info = document.Info;
        if (info == null) return false;

        var changed = false;
        foreach (var key in InfoKeys)
        {
            var value = ResolveStringOrNull(document, info, key);
            if (!scrub.TryApply(value, out var scrubbed)) continue;

            if (scrubbed.Length == 0)
                info.Remove(key);
            else
                info[key] = new PdfString(scrubbed);

            changed = true;
        }
        return changed;
    }

    private static bool ScrubXmpMetadata(PdfDocument document, CarrierScrub scrub)
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
            // RemoveWhole empties the packet rather than cutting the term out of
            // it: an XMP packet is one value, and a shredded packet's surviving
            // schema is exactly the structure #1169 says can reveal the term.
            if (!scrub.TryApply(xmp, out var scrubbed)) continue;

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
    private static bool ScrubFormFieldNames(PdfDocument document, CarrierScrub scrub)
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
                if (!scrub.TryApply(str.Value, out var scrubbed)) continue;
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
                if (!scrub.TryApply(str.Value, out var scrubbed, trim: false)) continue;
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
    private static bool ScrubStructTree(PdfDocument document, CarrierScrub scrub)
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
                if (!scrub.TryApply(str.Value, out var scrubbed)) continue;
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
    private static bool ScrubJavaScript(PdfDocument document, CarrierScrub scrub)
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
                if (document.Resolve(node.GetOptional("JS") ?? PdfNull.Instance) is PdfString js
                    && scrub.TryApply(js.Value, out var scrubbedJs))
                {
                    node.SetString("JS", scrubbedJs);
                    changed = true;
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
    /// <remarks>
    /// ⚠️ <see cref="CarrierScrubMode.Strip"/> and
    /// <see cref="CarrierScrubMode.RemoveWhole"/> are the SAME thing here, and
    /// that is a property of the carrier, not a silent downgrade: an attachment
    /// is opaque bytes plus its name and description — there is no substring to
    /// cut without risking corruption, so a hit always removes the whole file.
    /// <see cref="CarrierScrubMode.ReportOnly"/> detects and leaves it.
    /// </remarks>
    private static bool ScrubEmbeddedFiles(PdfDocument document, CarrierScrub scrub)
    {
        var files = document.GetEmbeddedFiles();
        if (files.Count == 0) return false;

        var remove = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var f in files)
        {
            var hit = false;
            if (f.Bytes is { Length: > 0 } bytes)
            {
                var latin1 = Encoding.Latin1.GetString(bytes);
                var utf8 = Encoding.UTF8.GetString(bytes);
                hit = scrub.Hits(latin1) || scrub.Hits(utf8);
            }
            // #1428: the payload wasn't the only carrier -- an attachment whose
            // /Desc or filename (/F, /UF, read via FileName) carries the term but
            // whose bytes don't was never flagged for removal at all, regardless
            // of whether it came from the catalog or an annotation's own /FS.
            if (!hit && !string.IsNullOrEmpty(f.Description))
                hit = scrub.Hits(f.Description);
            if (!hit && !string.IsNullOrEmpty(f.FileName))
                hit = scrub.Hits(f.FileName);
            if (hit)
                remove.Add(f.RawDictionary);
        }
        if (remove.Count == 0) return false;
        if (scrub.Mode == CarrierScrubMode.ReportOnly) return false;   // hit recorded, file kept

        var changed = false;
        if (document.Resolve(document.Catalog?.GetOptional("Names") ?? PdfNull.Instance) is PdfDictionary names
            && document.Resolve(names.GetOptional("EmbeddedFiles") ?? PdfNull.Instance) is PdfDictionary tree)
            changed |= FilterEmbeddedFileTree(document, tree, remove);

        changed |= FilterAssociatedFiles(document, document.Catalog, remove);
        for (var p = 1; p <= document.PageCount; p++)
        {
            var page = document.GetPage(p);
            changed |= FilterAssociatedFiles(document, page.Dictionary, remove);
            // #1428: a /FileAttachment annotation's /FS is not reachable from
            // /Names/EmbeddedFiles or /AF at all -- it can only be removed by
            // dropping the annotation that owns it. An attachment can't be
            // half-removed (its description/filename/bytes are one carrier), so
            // this removes the whole annotation, matching how
            // InteractiveRedactionScrubber.RemoveIntersectingAnnotations treats
            // a matched annotation elsewhere in this pipeline.
            changed |= FilterAnnotationFileAttachments(document, page.Dictionary, remove);
        }
        return changed;
    }

    // Remove /FileAttachment annotations whose /FS resolves into `remove` from a
    // page's /Annots array (§12.5.6.15) -- the whole annotation, since a file
    // attachment can't be partially scrubbed.
    private static bool FilterAnnotationFileAttachments(PdfDocument document, PdfDictionary pageDict, HashSet<PdfDictionary> remove)
    {
        if (document.Resolve(pageDict.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
            return false;

        var changed = false;
        for (var i = annots.Count - 1; i >= 0; i--)
        {
            if (document.Resolve(annots[i]) is not PdfDictionary annot)
                continue;
            if (annot.GetNameOrNull("Subtype") != "FileAttachment")
                continue;
            if (document.Resolve(annot.GetOptional("FS") ?? PdfNull.Instance) is not PdfDictionary fs || !remove.Contains(fs))
                continue;

            annots.RemoveAt(i);
            changed = true;
        }
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

    private static bool ScrubOutlines(PdfDocument document, CarrierScrub scrub)
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
                if (scrub.TryApply(title, out var scrubbed))
                {
                    // An emptied bookmark keeps its destination but loses its
                    // label; removing the node entirely would renumber the
                    // outline tree and orphan its children. RemoveWhole arrives
                    // here with an empty replacement, so it lands on the same
                    // "[redacted]" placeholder — the whole label is gone and no
                    // residue is left to infer the term from.
                    item["Title"] = new PdfString(scrubbed.Length == 0 ? "[redacted]" : scrubbed);
                    changed = true;
                }

                Walk(item.GetOptional("First"));   // descend into children
                node = item.GetOptional("Next");   // then continue along siblings
            }
        }

        Walk(outlines.GetOptional("First"));
        return changed;
    }

    private static bool ScrubAnnotationContents(PdfDocument document, CarrierScrub scrub)
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
                // /Subj is the "subject" text string on markup annotations (Table 170,
                // §12.5.6.2) — a separate free-text carrier from /Contents, not scrubbed
                // by anything before this. /OverlayText is Redact-specific (Table 192,
                // §12.5.6.23): the text a viewer draws over the redacted region when no
                // /RO overlay form is present — the annotation's OWN stated replacement
                // text, and until this fix nothing in the codebase read it at all.
                foreach (var key in new[] { "Contents", "T", "RC", "Subj", "OverlayText" })
                {
                    var value = ResolveStringOrNull(document, annot, key);
                    if (!scrub.TryApply(value, out var scrubbed)) continue;

                    if (scrubbed.Length == 0)
                        annot.Remove(key);
                    else
                        annot[key] = new PdfString(scrubbed);

                    changed = true;
                }

                // #1194: the widget's appearance-characteristics captions (/MK /CA
                // normal, /AC down, /RC rollover — §12.5.6.19 / §12.7.4.3) are the
                // button's visible LABEL, a separate string carrier. issue15053 kept
                // "This Button can be toggled" in /MK /CA after /V was scrubbed.
                if (document.Resolve(annot.GetOptional("MK") ?? PdfNull.Instance) is PdfDictionary mk)
                {
                    foreach (var capKey in new[] { "CA", "AC", "RC" })
                    {
                        var cap = (document.Resolve(mk.GetOptional(capKey) ?? PdfNull.Instance) as PdfString)?.Value;
                        if (!scrub.TryApply(cap, out var scrubbedCap)) continue;
                        mk[capKey] = new PdfString(scrubbedCap);
                        changed = true;
                    }
                }

                // #1194: a choice field's /Opt is the list of selectable options
                // (§12.7.4.4) — an array of strings OR [export, display] pairs.
                // listbox_form kept "Saskatchewan" in /Opt after redaction; the
                // find-step never saw it (not page content). Excise the term from
                // each option string, keeping the array structure so the form is
                // not corrupted. /Opt may sit on the widget (merged) or the parent
                // field.
                var optHolder = annot.ContainsKey("Opt") ? annot
                    : document.Resolve(annot.GetOptional("Parent") ?? PdfNull.Instance) as PdfDictionary;
                if (optHolder != null &&
                    document.Resolve(optHolder.GetOptional("Opt") ?? PdfNull.Instance) is PdfArray opt)
                {
                    for (var oi = 0; oi < opt.Count; oi++)
                    {
                        var item = document.Resolve(opt[oi]);
                        if (item is PdfString os)
                        {
                            if (scrub.TryApply(os.Value, out var so)) { opt[oi] = new PdfString(so); changed = true; }
                        }
                        else if (item is PdfArray pair)   // [export, display]
                        {
                            for (var pi = 0; pi < pair.Count; pi++)
                                if (document.Resolve(pair[pi]) is PdfString ps
                                    && scrub.TryApply(ps.Value, out var sp))
                                {
                                    pair[pi] = new PdfString(sp);
                                    changed = true;
                                }
                        }
                    }
                }

                // #1155 scrubbed the link annotation's /A /URI from HERE, because
                // a link restates its /Contents in its URI and the two must not
                // diverge. #1168 then gave URI actions their own complete walk
                // (ScrubActionUris), which reaches every annotation's /A as well
                // — so this call had become a duplicate.
                //
                // #1188/#1169 makes the duplicate actively WRONG rather than
                // merely redundant: scrubbing the URI under the ANNOTATIONS
                // carrier's scrub context applied the Annotations carrier's mode
                // and scope to a URI. Setting ActionUris to RemoveWhole or
                // ReportOnly (the whole point of #1169 — a known URL's residue
                // reveals the term) was silently overridden by Annotations=Strip,
                // and turning ActionUris OFF still stripped the URI while the
                // report said the carrier was disabled. The ActionUris stage now
                // owns every /URI, so its flag and its mode both mean what they
                // say.
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
        PdfDocument document, PdfObject? actionObj, CarrierScrub scrub)
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

            // #1169: this is the carrier where Strip can REVEAL the term --
            // https://www.irs.gov/your-account minus "your" reads back as
            // https://www.irs.gov/-account to anyone who knows the site. Under
            // RemoveWhole the whole /URI goes instead, leaving no surrounding
            // structure to reconstruct from.
            var uri = ResolveStringOrNull(document, action, "URI");
            if (scrub.TryApply(uri, out var scrubbed))
            {
                if (scrubbed.Length == 0)
                    action.Remove("URI");
                else
                    action["URI"] = new PdfString(scrubbed);
                changed = true;
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
    private static bool ScrubActionUris(PdfDocument document, CarrierScrub scrub)
    {
        var changed = false;
        var catalog = document.Catalog;

        // Catalog-level: /OpenAction (may instead be a destination array, which
        // has no /URI — ScrubUriAction ignores it) and document /AA.
        changed |= ScrubUriAction(document, catalog?.GetOptional("OpenAction"), scrub);
        changed |= ScrubAdditionalActions(document, catalog?.GetOptional("AA"), scrub);

        // Every page and its annotations.
        for (int i = 1; i <= document.PageCount; i++)
        {
            var page = document.GetPage(i);
            changed |= ScrubAdditionalActions(document, page.Dictionary.GetOptional("AA"), scrub);

            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
                foreach (var annotObj in annots)
                    if (document.Resolve(annotObj) is PdfDictionary annot)
                    {
                        changed |= ScrubUriAction(document, annot.GetOptional("A"), scrub);
                        changed |= ScrubAdditionalActions(document, annot.GetOptional("AA"), scrub);
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
                changed |= ScrubUriAction(document, field.GetOptional("A"), scrub);
                changed |= ScrubAdditionalActions(document, field.GetOptional("AA"), scrub);
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
                changed |= ScrubUriAction(document, item.GetOptional("A"), scrub);
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
    private static bool ScrubAdditionalActions(PdfDocument document, PdfObject? aaObj, CarrierScrub scrub)
    {
        if (document.Resolve(aaObj ?? PdfNull.Instance) is not PdfDictionary aa) return false;
        var changed = false;
        foreach (var key in aa.Keys)
            changed |= ScrubUriAction(document, aa.GetOptional(key.Value), scrub);
        return changed;
    }

    private static string Excise(string value, IReadOnlyList<string> terms, bool caseSensitive)
        => ExciseCore(value, terms, caseSensitive, wholeWord: false, trim: true);

    /// <summary>
    /// Cut every occurrence of <paramref name="terms"/> out of
    /// <paramref name="value"/>, honouring the #1052 whole-word rule.
    /// </summary>
    /// <remarks>
    /// ⚠️ The carrier path MUST use the same match rule as page content. #896's
    /// lesson is exactly this: a safe option that existed only in the GUI meant
    /// every other caller silently got the unsafe one. If page content matches
    /// whole-word-only and metadata still matched by substring, redacting "Lee"
    /// whole-word would leave the page intact and gut "Sleeman" in /Info.
    /// </remarks>
    private static string ExciseCore(
        string value, IReadOnlyList<string> terms, bool caseSensitive, bool wholeWord, bool trim)
    {
        var result = value;
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            var from = 0;
            while (from <= result.Length - term.Length)
            {
                var at = IndexOfTerm(result, term, caseSensitive, wholeWord, from);
                if (at < 0) break;
                result = result.Remove(at, term.Length);
                from = at;
            }
        }
        return trim ? result.Trim() : result;
    }

    /// <summary>
    /// Index of the next occurrence of <paramref name="term"/> at or after
    /// <paramref name="startIndex"/>, or -1. Under <paramref name="wholeWord"/>
    /// an occurrence only counts when a non-word character (or the string edge)
    /// bounds it on both sides — the same <c>\w</c> rule the page matcher uses.
    /// </summary>
    private static int IndexOfTerm(
        string value, string term, bool caseSensitive, bool wholeWord, int startIndex)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        var at = startIndex;
        while (at <= value.Length - term.Length)
        {
            var found = value.IndexOf(term, at, comparison);
            if (found < 0) return -1;
            if (!wholeWord) return found;

            var end = found + term.Length - 1;
            var boundedLeft = found == 0 || !IsWordChar(value[found - 1]);
            var boundedRight = end + 1 >= value.Length || !IsWordChar(value[end + 1]);
            if (boundedLeft && boundedRight) return found;

            at = found + 1;
        }
        return -1;
    }

    /// <summary>
    /// #1151 — cut the term but preserve surrounding whitespace, for SEMANTIC
    /// values (/V, /DV) where a trailing space is part of the value and #1038's
    /// area path keeps it. Idempotent: no match leaves the string identical, so a
    /// second scrub pass cannot mutate an already-cut value.
    /// </summary>
    private static string ExciseNoTrim(string value, IReadOnlyList<string> terms, bool caseSensitive)
        => ExciseCore(value, terms, caseSensitive, wholeWord: false, trim: false);
}
