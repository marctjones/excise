using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Removes page-adjacent interactive structures that can visibly overlap a
/// redaction rectangle but live outside the page content stream.
/// </summary>
internal static class InteractiveRedactionScrubber
{
    public static bool ScrubArea(PdfPage page, PdfRectangle area)
    {
        area = area.Normalize();
        var changed = false;
        var pruneCandidates = new HashSet<int>();

        changed |= ScrubFormFields(page, IntersectsAny([area]), pruneCandidates);
        changed |= RemoveIntersectingAnnotations(page, area, pruneCandidates);

        if (pruneCandidates.Count > 0)
            PruneUnreachableCandidates(page.Document, pruneCandidates);

        if (changed)
            page.InvalidateTextExtractionCache();

        return changed;
    }

    /// <summary>
    /// The area selection: a field is scrubbed when one of its widgets on this
    /// page has a <c>/Rect</c> that intersects one of <paramref name="areas"/>.
    /// </summary>
    private static Func<PdfPage, PdfField, IReadOnlyList<PdfFieldWidget>, bool> IntersectsAny(
        IReadOnlyList<PdfRectangle> areas)
        => (page, _, widgets) => areas.Count > 0 && widgets.Any(w =>
            w.PageNumber == page.PageNumber && areas.Any(a => w.Rect.IntersectsWith(a)));

    /// <summary>
    /// #2041 — state one <c>RedactText</c> call carries across its page passes:
    /// which appearance streams it has already rewritten. An appearance can be
    /// reached more than once (a field matched on two passes, a stream shared by
    /// two widgets, a field's widgets on two pages), and a second rewrite of a
    /// stream that no longer holds the term finds nothing and would drop the
    /// appearance the first one just fixed.
    /// </summary>
    internal sealed class TermScrubState
    {
        /// <summary>Original appearance stream to its rewritten copy; null when it could not be
        /// rewritten (every holder then drops its appearance).</summary>
        public Dictionary<PdfStream, PdfReference?> Rewritten { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The copies this redaction wrote: already free of the term.</summary>
        public HashSet<PdfStream> Clean { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// #1038 — the TERM-AWARE scrub. Removes the matched term from a form
    /// field's value carriers instead of deleting the carriers wholesale.
    ///
    /// <para><b>Why this exists.</b> <see cref="ScrubArea"/> knows only a
    /// rectangle, so its only safe move is to drop <c>/V</c>, <c>/DV</c>,
    /// <c>/Opt</c> and <c>/AP</c> entirely. On a field whose widget is large,
    /// that deletes everything the field holds in order to remove one word. On
    /// <c>issue18036.pdf</c> — a certificate of insurance whose body is a
    /// read-only multiline <c>/Tx</c> field — redacting <c>certificate</c>
    /// destroyed <b>545 of 568 characters</b>, measured, and reported success.
    /// The whole-operator fallback everyone assumed was responsible for that
    /// class of damage never ran (0 firings in 235 redactions).</para>
    ///
    /// <para><c>RedactText</c> knows the term, so it can do the surgery the
    /// rectangle cannot: cut the matched substring out of each value string and
    /// leave the rest of the field intact.</para>
    ///
    /// <para><b>/AP is now rewritten, not dropped (#1098).</b> The appearance
    /// stream holds the term as drawn GLYPHS; the glyph-removal engine cuts them
    /// out of the appearance's own content (via <see cref="AppearanceStreamRedactor"/>)
    /// so the field still renders its remaining text in readers that ignore
    /// <c>/NeedAppearances</c>. It falls back to dropping <c>/AP</c> (the old
    /// leak-safe move) only when the appearance is not text-extractable — a
    /// subsetted font with no ToUnicode, the #637 limitation.</para>
    ///
    /// <para><b>/NeedAppearances is no longer set just because something changed
    /// (#1499).</b> It used to be, at the end of every scrub — which contradicted
    /// the paragraph above (the flag tells the viewer to discard the appearance
    /// #1098 had just rewritten) and, because PDF/A forbids the flag
    /// (ISO 19005-2 6.4.1#3, ISO 19005-1 6.9#1), silently cost a redacted PDF/A
    /// form its conformance.
    /// <see cref="SettleWidgetAppearances"/> now decides per widget.</para>
    /// </summary>
    public static bool ScrubTerm(
        PdfPage page, PdfRectangle area, string term, bool caseSensitive,
        bool wholeWord = false)   // #1052
    {
        if (string.IsNullOrEmpty(term)) return ScrubArea(page, area);
        area = area.Normalize();
        return ScrubTerm(page, [area], Array.Empty<PdfDictionary>(), [area], term, caseSensitive, wholeWord,
            new TermScrubState());
    }

    /// <summary>
    /// #2041 — the term scrub for one page pass of <c>RedactText</c>, every
    /// interactive match at once.
    ///
    /// <para><b>Fields are selected by what their appearance DRAWS.</b> A
    /// letter read from a widget's appearance names that widget
    /// (<see cref="Letter.SourceWidget"/>), and the field owning it is scrubbed
    /// wherever the glyph landed. Selecting by rectangle (the match box against
    /// the widget <c>/Rect</c>) missed a scrolled multiline field: its
    /// appearance draws the lines below <c>/Rect</c> that its <c>/BBox</c>
    /// clips, the file holds them and search finds them, and no field was
    /// scrubbed (pdfjs annotation-text-widget.pdf: "Etiam" located, 0 removed,
    /// reported survived). The match box can also fall on ANOTHER widget, whose
    /// appearance the scrub would then drop for not holding the term.</para>
    ///
    /// <para><paramref name="unattributedAreas"/> are the boxes of match letters
    /// no widget drew (a field's <c>/V</c> laid out in its <c>/Rect</c>, a
    /// hidden widget's text): those still select by rectangle, as before.
    /// <paramref name="matchAreas"/> (every interactive match) drive the
    /// non-widget annotation removal, unchanged.</para>
    /// </summary>
    internal static bool ScrubTerm(
        PdfPage page,
        IReadOnlyList<PdfRectangle> matchAreas,
        IReadOnlyCollection<PdfDictionary> drawingWidgets,
        IReadOnlyList<PdfRectangle> unattributedAreas,
        string term,
        bool caseSensitive,
        bool wholeWord,
        TermScrubState state)
    {
        var changed = false;
        var pruneCandidates = new HashSet<int>();

        var drawn = new HashSet<PdfDictionary>(drawingWidgets, ReferenceEqualityComparer.Instance);
        var byRect = IntersectsAny(unattributedAreas.Select(a => a.Normalize()).ToList());
        changed |= ScrubFormFields(
            page,
            (p, field, widgets) => drawn.Contains(field.RawDictionary)
                                   || field.WidgetDictionaries.Any(drawn.Contains)
                                   || byRect(p, field, widgets),
            pruneCandidates, term, caseSensitive, wholeWord, state);
        foreach (var area in matchAreas)
            changed |= RemoveIntersectingAnnotations(page, area.Normalize(), pruneCandidates);

        if (pruneCandidates.Count > 0)
            PruneUnreachableCandidates(page.Document, pruneCandidates);

        if (changed)
            page.InvalidateTextExtractionCache();

        return changed;
    }

    /// <summary>
    /// Cut the term out of a value carrier in place. Returns false when the
    /// carrier does not hold the term — in which case it is LEFT ALONE, since
    /// deleting a value that never contained the match is pure destruction.
    /// </summary>
    private static bool RedactStringEntry(
        PdfDocument document,
        PdfDictionary dictionary,
        string key,
        string term,
        bool caseSensitive,
        HashSet<int> pruneCandidates,
        bool wholeWord = false)
    {
        var raw = dictionary.GetOptional(key);
        if (raw == null) return false;
        if (document.Resolve(raw) is not PdfString str) return false;

        var redacted = TermMatch.Mask(str.Value, [term], caseSensitive, wholeWord);
        if (redacted == null) return false;

        // The old value may be its own indirect object still holding the term.
        // Capturing it here lets the unreachability prune drop it once the
        // field points at the direct replacement instead.
        CaptureObjectGraph(document, raw, pruneCandidates);
        dictionary.SetString(key, redacted);
        return true;
    }

    /// <summary>
    /// Cut the term out of each <c>/Opt</c> entry rather than dropping the
    /// option list. Entries are either a string or a two-element
    /// [export, display] array (§12.7.4.4); both forms are handled.
    /// </summary>
    private static bool RedactOptionList(
        PdfDocument document,
        PdfDictionary field,
        string term,
        bool caseSensitive,
        HashSet<int> pruneCandidates,
        bool wholeWord = false)
    {
        var raw = field.GetOptional("Opt");
        if (raw == null) return false;
        if (document.Resolve(raw) is not PdfArray options) return false;

        var changed = false;
        for (var i = 0; i < options.Count; i++)
        {
            switch (document.Resolve(options[i]))
            {
                case PdfString entry:
                    if (TermMatch.Mask(entry.Value, [term], caseSensitive, wholeWord) is { } cut)
                    {
                        options[i] = new PdfString(cut);
                        changed = true;
                    }
                    break;

                case PdfArray pair:
                    for (var j = 0; j < pair.Count; j++)
                    {
                        if (document.Resolve(pair[j]) is PdfString s2 &&
                            TermMatch.Mask(s2.Value, [term], caseSensitive, wholeWord) is { } cut2)
                        {
                            pair[j] = new PdfString(cut2);
                            changed = true;
                        }
                    }
                    break;
            }
        }

        if (changed)
            CaptureObjectGraph(document, raw, pruneCandidates);
        return changed;
    }

    /// <summary>
    /// #2039 — a choice field names its selection by the SAVE value of an
    /// <c>[save display]</c> option pair (§12.7.4.4; XFA 3.3 p758-760): <c>/V</c>
    /// "CAN" while the widget shows "Canada". Cutting the term out of the value
    /// strings cannot reach that: "CAN" does not contain "Canada", yet it says
    /// which option was chosen as plainly as the display text did. So for every
    /// pair whose display text holds the term, its save value is blanked and
    /// any <c>/V</c> or <c>/DV</c> selecting it is removed, with <c>/I</c> (the
    /// selected indices, which point at the same option) when the selection
    /// changed. Pairs whose display does not hold the term are left alone.
    /// </summary>
    private static bool RedactChoiceSelection(
        PdfDocument document,
        PdfDictionary field,
        string term,
        bool caseSensitive,
        HashSet<int> pruneCandidates,
        bool wholeWord)
    {
        if (document.Resolve(field.GetOptional("Opt") ?? PdfNull.Instance) is not PdfArray options)
            return false;

        var saves = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in options)
        {
            if (document.Resolve(item) is not PdfArray { Count: >= 2 } pair
                || document.Resolve(pair[0]) is not PdfString save
                || document.Resolve(pair[1]) is not PdfString display
                || !TermMatch.Holds(display.Value, [term], caseSensitive, wholeWord))
                continue;
            saves.Add(save.Value);
            pair[0] = new PdfString(string.Empty);
        }
        if (saves.Count == 0)
            return false;
        CaptureObjectGraph(document, field.GetOptional("Opt"), pruneCandidates);

        var selectionChanged = false;
        foreach (var key in new[] { "V", "DV" })
        {
            var raw = field.GetOptional(key);
            switch (raw == null ? null : document.Resolve(raw))
            {
                case PdfString s when saves.Contains(s.Value):
                    CaptureObjectGraph(document, raw, pruneCandidates);
                    field.Remove(key);
                    selectionChanged = true;
                    break;

                case PdfArray values:
                    var kept = new PdfArray();
                    foreach (var v in values)
                    {
                        if (document.Resolve(v) is PdfString s2 && saves.Contains(s2.Value))
                            continue;
                        kept.Add(v);
                    }
                    if (kept.Count != values.Count)
                    {
                        CaptureObjectGraph(document, raw, pruneCandidates);
                        field[key] = kept;
                        selectionChanged = true;
                    }
                    break;
            }
        }
        if (selectionChanged)
            field.Remove("I");
        return true;
    }

    private static bool ScrubFormFields(
        PdfPage page,
        Func<PdfPage, PdfField, IReadOnlyList<PdfFieldWidget>, bool> selected,
        HashSet<int> pruneCandidates,
        string? term = null,
        bool caseSensitive = false,
        bool wholeWord = false,
        TermScrubState? state = null)
    {
        state ??= new TermScrubState();
        IReadOnlyList<PdfField> fields;
        // #2040: every field with a widget on this page, not only those whose
        // FIRST widget is here: a match or an area on page 2 of a field
        // repeated on pages 1 and 2 otherwise scrubbed nothing.
        try { fields = page.GetFormFieldsWithWidgetsOnPage(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }

        var changed = false;
        // #1098: a single-widget field often merges the field and widget into
        // ONE dictionary, so its /AP is reached twice (as field, as widget).
        // Rewriting removes the term the first time; the second pass would find
        // no match and drop the appearance we just fixed. Process each /AP once.
        var processedAp = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        // #1499: every widget this scrub ran over, so its appearance can be
        // accounted for ONCE at the end (see SettleWidgetAppearances). A
        // HashSet<PdfDictionary> is reference-keyed — PdfDictionary does not
        // override Equals — which is what dedupes the merged field/widget dict.
        var touchedWidgets = new HashSet<PdfDictionary>();
        foreach (var field in fields)
        {
            var widgets = field.Widgets.Count > 0
                ? field.Widgets
                : field.Rect is { } rect
                    ? new[] { new PdfFieldWidget(rect, field.PageNumber, exportValue: null) }
                    : Array.Empty<PdfFieldWidget>();

            if (!selected(page, field, widgets))
                continue;

            // #1760: /V on a Button holds an on/off STATE NAME
            // (checkbox/radio), never human-readable text — but a PUSHBUTTON's
            // /AP/N is very commonly a direct content stream that draws a
            // CUSTOM CAPTION as real glyphs (issue15053: "This Button can be
            // toggled", painted text, no /V at all). The value carriers
            // (V/DV/RV/Opt) genuinely do not apply to a button and stay
            // skipped, but the #1098 appearance-stream rewrite must NOT be —
            // this used to skip buttons entirely, including that rewrite,
            // which is exactly how a pushbutton's caption survived a redaction
            // that reported success. Signature fields USED to be excluded on
            // the same "no text" assumption — but #669 fixed TextExtractor to
            // read a signature widget's /AP/N appearance text (a real
            // "Digitally signed by…" block), so a match can now legitimately
            // reach here for a Signature field too, and skipping it would be
            // exactly the "found but not removable" gap #660 already had to
            // fix for FreeText. Removing /V on a Signature field drops the
            // reference to the signature dictionary (whose /Reason, /Name,
            // /Location strings can restate the same text as the appearance)
            // — since Save() only serializes objects reachable from the
            // trailer, letting that reference go is enough for the dictionary
            // to fall out of the saved bytes with no separate prune step
            // needed for it specifically.
            var isButton = field.FieldType == PdfFieldType.Button;

            CaptureObjectGraph(page.Document, field.RawDictionary.GetOptional("AP"), pruneCandidates);

            var defaultResources = GetAcroFormDefaultResources(page.Document);
            if (term != null)
            {
                if (!isButton)
                {
                    // #1038: cut the term out, keep the rest of the value. See
                    // ScrubTerm for what deleting it instead cost on a real file.
                    changed |= RedactStringEntry(
                        page.Document, field.RawDictionary, "V", term, caseSensitive, pruneCandidates, wholeWord);
                    changed |= RedactStringEntry(
                        page.Document, field.RawDictionary, "DV", term, caseSensitive, pruneCandidates, wholeWord);
                    // #1581: /RV, the rich-text value (§12.7.4.3). It is an
                    // independent carrier — a field can have /RV and NO /V at
                    // all — and mutool DRAWS it, so the term stayed both in
                    // the file and on the page while every /V assertion read
                    // clean.
                    changed |= RedactStringEntry(
                        page.Document, field.RawDictionary, "RV", term, caseSensitive, pruneCandidates, wholeWord);
                }
                // #1098/#1760: rewrite the appearance to remove the term's
                // GLYPHS so the field (button caption included) still renders
                // its remaining text in readers that ignore /NeedAppearances.
                // Drops /AP (leak-safe) only if the rewrite can't be done —
                // for a checkbox/radio whose /AP/N is a STATE DICTIONARY
                // (§12.5.6.19), AppearanceStreamRedactor correctly refuses to
                // rewrite it (no readable stream), so this falls back to
                // dropping /AP: the same fail-closed policy every other field
                // type already gets when its widget rect intersects the match.
                changed |= RewriteOrDropAppearance(
                    page, field.RawDictionary, defaultResources, term, caseSensitive, processedAp, state, wholeWord);
            }
            else
            {
                if (!isButton)
                {
                    changed |= field.RawDictionary.Remove("V");
                    changed |= field.RawDictionary.Remove("DV");
                    changed |= field.RawDictionary.Remove("RV");   // #1581
                }
                // Area mode knows only a rectangle, so the whole appearance goes.
                changed |= field.RawDictionary.Remove("AP");
            }

            // Choice fields (combo/list boxes) restate every option string in
            // /Opt independent of /V — a list box's full option list is what
            // TextExtractor now surfaces for search/redaction (#661). Leaving
            // /Opt behind after wiping V/DV/AP would be a redaction leak: the
            // matched text would still sit in the saved file bytes, and a
            // reader that regenerates the appearance from NeedAppearances
            // would draw the option list right back. Stripped for every
            // Choice field here (not just list boxes) since a combo box's
            // /Opt carries the same risk even though it isn't rendered as
            // extractable text today.
            if (field.FieldType == PdfFieldType.Choice)
            {
                // #2039: before the display strings are cut, so the options
                // whose display holds the term can still be told apart.
                if (term != null)
                    changed |= RedactChoiceSelection(
                        page.Document, field.RawDictionary, term, caseSensitive, pruneCandidates, wholeWord);
                changed |= term != null
                    ? RedactOptionList(
                        page.Document, field.RawDictionary, term, caseSensitive, pruneCandidates, wholeWord)
                    : field.RawDictionary.Remove("Opt");
            }

            foreach (var widget in field.WidgetDictionaries)
            {
                CaptureObjectGraph(page.Document, widget.GetOptional("AP"), pruneCandidates);
                changed |= term != null
                    ? RewriteOrDropAppearance(page, widget, defaultResources, term, caseSensitive, processedAp, state, wholeWord)
                    : widget.Remove("AP");
            }

            // #1499: the widgets whose appearance this field's scrub just
            // settled. A field that is its own widget has it in
            // WidgetDictionaries already (PdfAcroFormParser adds the field dict
            // for /Subtype /Widget); a field with neither is the odd
            // rect-derived case handled above, where RawDictionary is the only
            // holder there is.
            if (field.WidgetDictionaries.Count > 0)
            {
                foreach (var widget in field.WidgetDictionaries)
                    touchedWidgets.Add(widget);
            }
            else
            {
                touchedWidgets.Add(field.RawDictionary);
            }
        }

        if (changed)
            changed |= SettleWidgetAppearances(page.Document, touchedWidgets);

        return changed;
    }

    /// <summary>
    /// #1499 — decide, PER WIDGET, what the scrubbed form still needs.
    ///
    /// <para><b>What this replaces.</b> The scrub used to end with
    /// <c>SetAcroFormNeedAppearances()</c> whenever ANYTHING changed. That is
    /// wrong twice over. It contradicts #1098 — the appearance stream was just
    /// rewritten to remove the term's glyphs, and the flag asks the viewer to
    /// throw that away and re-typeset from <c>/V</c>. And
    /// <c>/NeedAppearances</c> is forbidden by PDF/A (ISO 19005-2 6.4.1#3,
    /// ISO 19005-1 6.9#1), so redacting one field silently cost the whole
    /// output file its conformance.</para>
    ///
    /// <para>The rule now:</para>
    /// <list type="bullet">
    ///   <item>Widget still has an <c>/AP /N</c> — #1098 rewrote it, or it was
    ///     never dropped. It is accurate for the scrubbed value. Nothing to
    ///     do, and no flag: this is the common path and it is now PDF/A-clean
    ///     for every document, not only archival ones.</item>
    ///   <item>Widget has no appearance and the document targets PDF/A — write
    ///     an EMPTY one. An appearance is required (19005-2 6.3.3#1, 19005-1
    ///     6.9#2) and the flag that used to stand in for it is forbidden
    ///     (19005-2 6.4.1#3, 19005-1 6.9#1). ⚠️ 19005-1 has no zero-size
    ///     exemption where 19005-2 does, so a degenerate widget that
    ///     <see cref="AcroFormAuthoring.TryWriteEmptyAppearance"/> refuses stays
    ///     non-conformant under PDF/A-1b — the #623 invisible-signature shape,
    ///     unchanged by this fix.</item>
    ///   <item>Widget has no appearance and the document is not PDF/A — set the
    ///     flag exactly as before, so a viewer that honours it still draws the
    ///     remaining value. #1499's acceptance: non-PDF/A behaves as
    ///     before.</item>
    /// </list>
    ///
    /// <para>⚠️ An already-set <c>/NeedAppearances</c> on the INPUT is left
    /// alone. Clearing it would change how fields this redaction never touched
    /// are rendered, which is outside the requested delta.</para>
    /// </summary>
    private static bool SettleWidgetAppearances(PdfDocument document, IEnumerable<PdfDictionary> widgets)
    {
        var changed = false;
        var needAppearances = false;
        // Hoisted: the getter inflates and decodes the XMP packet, and this runs
        // once per widget, per page, per term.
        var targetsPdfA = document.TargetsPdfA;

        foreach (var widget in widgets)
        {
            if (HasNormalAppearance(document, widget))
                continue;

            if (targetsPdfA)
            {
                // No fallback to the flag here — PDF/A forbids it. When even an
                // empty appearance cannot be written (no readable /Rect, or the
                // zero-size invisible-signature shape #623 deliberately keeps
                // appearance-less), leaving the widget as it is is the only move
                // that does not break conformance by itself.
                changed |= AcroFormAuthoring.TryWriteEmptyAppearance(document, widget);
                continue;
            }

            needAppearances = true;
        }

        if (needAppearances)
        {
            document.SetAcroFormNeedAppearances();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Does <paramref name="widget"/> still carry a resolvable <c>/AP /N</c>?
    /// <c>/N</c> is a stream for text and choice widgets and a state dictionary
    /// for buttons (§12.5.5) — <see cref="PdfStream"/> derives from
    /// <see cref="PdfDictionary"/>, so one check covers both. An <c>/AP</c>
    /// whose <c>/N</c> resolves to neither is as good as absent.
    /// </summary>
    private static bool HasNormalAppearance(PdfDocument document, PdfDictionary widget)
    {
        if (document.Resolve(widget.GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary ap)
            return false;
        return document.Resolve(ap.GetOptional("N") ?? PdfNull.Instance) is PdfDictionary;
    }

    /// <summary>The AcroForm default resources (/DR) — the fonts a producer may
    /// share across fields rather than duplicate in each appearance stream.</summary>
    private static PdfDictionary? GetAcroFormDefaultResources(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acro)
            return doc.Resolve(acro.GetOptional("DR") ?? PdfNull.Instance) as PdfDictionary;
        return null;
    }

    /// <summary>
    /// #1098 — rewrite the holder's <c>/AP</c> to remove the term's glyphs, or
    /// drop it (leak-safe) if the rewrite can't be done. Each holder is
    /// touched at most once (<paramref name="processedHolders"/>): a field that
    /// is its own widget is reached as field and as widget.
    ///
    /// <para><b>#2041: copy on write.</b> The rewritten content goes into a NEW
    /// stream, and the holder gets its own <c>/AP</c> dictionary pointing at
    /// it. The original stream and dictionary are left to whoever else holds
    /// them: an appearance shared with a widget this scrub did not select is
    /// never edited for one field (a sharer that draws the term is selected
    /// on its own page and gets the same copy, through
    /// <paramref name="state"/>). Whatever is left unreachable is pruned. A
    /// stream this redaction already wrote is clean and kept, not rewritten
    /// again (finding nothing, a second rewrite used to drop the appearance the
    /// first had just fixed).</para>
    /// </summary>
    private static bool RewriteOrDropAppearance(
        PdfPage page, PdfDictionary holder, PdfDictionary? defaultResources,
        string term, bool caseSensitive, HashSet<PdfDictionary> processedHolders, TermScrubState state,
        bool wholeWord = false)
    {
        var document = page.Document;
        if (document.Resolve(holder.GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary ap)
            return false;   // nothing to touch
        if (!processedHolders.Add(holder))
            return false;   // already handled via the merged field/widget dict

        // A /N that is a dictionary of states (§12.5.5) has no single readable
        // stream: dropped, as any appearance that cannot be rewritten is.
        if (document.Resolve(ap.GetOptional("N") ?? PdfNull.Instance) is not PdfStream normal)
            return holder.Remove("AP");

        var replaced = new Dictionary<string, PdfReference>();
        if (!state.Clean.Contains(normal))   // else written by this redaction: already free of the term
        {
            if (!state.Rewritten.TryGetValue(normal, out var replacement))
            {
                replacement = RewriteIntoCopy(page, normal, defaultResources, term, caseSensitive, wholeWord, state);
                state.Rewritten[normal] = replacement;
            }
            if (replacement == null)
                return holder.Remove("AP");   // couldn't rewrite -> drop
            replaced["N"] = replacement;
        }

        // #2041: the down (/D) and rollover (/R) appearances are drawn while the
        // widget is pressed or hovered, and are in the file regardless. They
        // used to be left as they were: a redaction reported the term removed
        // while /D still drew it. One that reads the term is rewritten the same
        // way, or the whole /AP goes; one that does not is left alone.
        foreach (var key in new[] { "D", "R" })
        {
            if (document.Resolve(ap.GetOptional(key) ?? PdfNull.Instance) is not PdfStream other
                || state.Clean.Contains(other))
                continue;
            if (!state.Rewritten.TryGetValue(other, out var otherReplacement))
            {
                if (!AppearanceStreamRedactor.Holds(page, other, defaultResources, term, caseSensitive, wholeWord))
                    continue;
                otherReplacement = RewriteIntoCopy(page, other, defaultResources, term, caseSensitive, wholeWord, state);
                state.Rewritten[other] = otherReplacement;
            }
            if (otherReplacement == null)
                return holder.Remove("AP");
            replaced[key] = otherReplacement;
        }

        if (replaced.Count == 0)
            return false;
        var own = new PdfDictionary();
        foreach (var entry in ap)
            own[entry.Key] = entry.Value;
        foreach (var (key, value) in replaced)
            own[key] = value;
        holder["AP"] = own;
        return true;
    }

    private static PdfReference? RewriteIntoCopy(
        PdfPage page, PdfStream original, PdfDictionary? defaultResources, string term, bool caseSensitive,
        bool wholeWord, TermScrubState state)
        => AppearanceStreamRedactor.RewrittenContent(page, original, defaultResources, term, caseSensitive, wholeWord)
            is { } content
            ? AddRewrittenCopy(page.Document, original, content, state)
            : null;

    /// <summary>
    /// A new indirect stream with <paramref name="original"/>'s dictionary
    /// (<c>/BBox</c>, <c>/Matrix</c>, <c>/Resources</c>, ...) and
    /// <paramref name="content"/> as its data, encoded afresh.
    /// </summary>
    private static PdfReference AddRewrittenCopy(
        PdfDocument document, PdfStream original, byte[] content, TermScrubState state)
    {
        var copy = new PdfStream();
        foreach (var entry in original)
        {
            if (entry.Key.Value is "Length" or "Filter" or "DecodeParms" or "DL" or "F" or "FFilter" or "FDecodeParms")
                continue;
            copy[entry.Key] = entry.Value;
        }
        copy.DecodedData = content;
        state.Clean.Add(copy);
        return document.AddIndirectObject(copy);
    }

    /// <summary>
    /// #1753 — remove every non-widget annotation whose <c>/Rect</c> is sized
    /// to one of <paramref name="words"/> (<see cref="WordDecorationRemover.IsSizedTo"/>):
    /// an Underline or StrikeOut whose Rect is the stroke alone misses the
    /// glyph centreline the area scrub tests, and at the word's width it states
    /// the removed word's width once the gap closes. Returns how many went.
    /// </summary>
    public static int RemoveWordSizedAnnotations(PdfPage page, IReadOnlyList<PdfRectangle> words)
    {
        var pruneCandidates = new HashSet<int>();
        var removed = RemoveAnnotations(page, rect => words.Any(word => WordDecorationRemover.IsSizedTo(rect, word)),
            pruneCandidates);
        if (pruneCandidates.Count > 0)
            PruneUnreachableCandidates(page.Document, pruneCandidates);
        if (removed > 0)
            page.InvalidateTextExtractionCache();
        return removed;
    }

    private static bool RemoveIntersectingAnnotations(
        PdfPage page,
        PdfRectangle area,
        HashSet<int> pruneCandidates)
        => RemoveAnnotations(page, rect => rect.IntersectsWith(area), pruneCandidates) > 0;

    private static int RemoveAnnotations(
        PdfPage page,
        Func<PdfRectangle, bool> matches,
        HashSet<int> pruneCandidates)
    {
        var annotsObj = page.Dictionary.GetOptional("Annots");
        if (annotsObj == null)
            return 0;

        if (page.Document.Resolve(annotsObj) is not PdfArray annots)
            return 0;

        var removed = 0;
        for (var i = annots.Count - 1; i >= 0; i--)
        {
            var annotObj = annots[i];
            if (page.Document.Resolve(annotObj) is not PdfDictionary annot)
                continue;

            if (!TryGetRect(page.Document, annot.GetOptional("Rect"), out var rect) ||
                !matches(rect))
            {
                continue;
            }

            var subtype = annot.GetNameOrNull("Subtype");
            if (subtype == "Widget")
            {
                // AcroForm field values/appearances are scrubbed above while
                // preserving empty widgets. Removing widgets here would make
                // ordinary field redaction more destructive than necessary.
                continue;
            }

            CaptureObjectGraph(page.Document, annotObj, pruneCandidates);
            annots.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private static bool TryGetRect(PdfDocument document, PdfObject? rectObj, out PdfRectangle rect)
    {
        rect = default;
        if (rectObj == null)
            return false;

        if (document.Resolve(rectObj) is not PdfArray array || array.Count < 4)
            return false;

        if (!array[0].TryGetNumber(out var left) ||
            !array[1].TryGetNumber(out var bottom) ||
            !array[2].TryGetNumber(out var right) ||
            !array[3].TryGetNumber(out var top))
        {
            return false;
        }

        rect = new PdfRectangle(left, bottom, right, top).Normalize();
        return true;
    }

    private static void CaptureObjectGraph(PdfDocument document, PdfObject? obj, HashSet<int> objectNumbers)
    {
        if (obj == null)
            return;

        CaptureObjectGraph(document, obj, objectNumbers, new HashSet<int>());
    }

    private static void CaptureObjectGraph(
        PdfDocument document,
        PdfObject obj,
        HashSet<int> objectNumbers,
        HashSet<int> visited)
    {
        switch (obj)
        {
            case PdfReference reference:
                if (!visited.Add(reference.ObjectNum))
                    return;

                objectNumbers.Add(reference.ObjectNum);
                try
                {
                    CaptureObjectGraph(document, document.GetObject(reference), objectNumbers, visited);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
                break;

            case PdfStream stream:
                foreach (var value in stream.Values)
                    CaptureObjectGraph(document, value, objectNumbers, visited);
                break;

            case PdfDictionary dictionary:
                foreach (var value in dictionary.Values)
                    CaptureObjectGraph(document, value, objectNumbers, visited);
                break;

            case PdfArray array:
                foreach (var value in array)
                    CaptureObjectGraph(document, value, objectNumbers, visited);
                break;
        }
    }

    private static void PruneUnreachableCandidates(PdfDocument document, HashSet<int> candidates)
    {
        HashSet<int> reachable;
        try { reachable = document.ComputeReachableObjects(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }

        foreach (var objectNumber in candidates)
        {
            if (!reachable.Contains(objectNumber))
                document.RemoveObject(objectNumber);
        }
    }
}
