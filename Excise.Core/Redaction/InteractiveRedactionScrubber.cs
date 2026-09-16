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

        changed |= ScrubFormFields(page, area, pruneCandidates);
        changed |= RemoveIntersectingAnnotations(page, area, pruneCandidates);

        if (pruneCandidates.Count > 0)
            PruneUnreachableCandidates(page.Document, pruneCandidates);

        if (changed)
            page.InvalidateTextExtractionCache();

        return changed;
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
        var changed = false;
        var pruneCandidates = new HashSet<int>();

        changed |= ScrubFormFields(page, area, pruneCandidates, term, caseSensitive, wholeWord);
        changed |= RemoveIntersectingAnnotations(page, area, pruneCandidates);

        if (pruneCandidates.Count > 0)
            PruneUnreachableCandidates(page.Document, pruneCandidates);

        if (changed)
            page.InvalidateTextExtractionCache();

        return changed;
    }

    /// <summary>
    /// <paramref name="value"/> with every occurrence of <paramref name="term"/>
    /// cut out, or null when it contains none.
    /// </summary>
    private static string? WithoutTerm(
        string value, string term, bool caseSensitive, bool wholeWord = false)
    {
        // #1052: the same \w boundary rule the page matcher uses. A field value
        // must not be cut by a looser rule than the one that found the match —
        // whole-word "Lee" would otherwise still turn "Sleeman" into "Sman"
        // here, which is the #896 failure (a safe option honoured in one path
        // and silently not in another).
        var at = IndexOfTerm(value, term, caseSensitive, wholeWord, 0);
        if (at < 0) return null;

        var sb = new System.Text.StringBuilder(value.Length);
        var from = 0;
        while (at >= 0)
        {
            sb.Append(value, from, at - from);
            from = at + term.Length;
            at = IndexOfTerm(value, term, caseSensitive, wholeWord, from);
        }
        sb.Append(value, from, value.Length - from);
        return sb.ToString();
    }

    /// <summary>
    /// Next occurrence of <paramref name="term"/> at or after
    /// <paramref name="startIndex"/>, or -1. Under <paramref name="wholeWord"/>
    /// it must be bounded by a non-word character (or the string edge) on both
    /// sides (#1052).
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
            if ((found == 0 || !IsWordChar(value[found - 1])) &&
                (end + 1 >= value.Length || !IsWordChar(value[end + 1])))
                return found;

            at = found + 1;
        }
        return -1;
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

        var redacted = WithoutTerm(str.Value, term, caseSensitive, wholeWord);
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
                    if (WithoutTerm(entry.Value, term, caseSensitive, wholeWord) is { } cut)
                    {
                        options[i] = new PdfString(cut);
                        changed = true;
                    }
                    break;

                case PdfArray pair:
                    for (var j = 0; j < pair.Count; j++)
                    {
                        if (document.Resolve(pair[j]) is PdfString s2 &&
                            WithoutTerm(s2.Value, term, caseSensitive, wholeWord) is { } cut2)
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

    private static bool ScrubFormFields(
        PdfPage page,
        PdfRectangle area,
        HashSet<int> pruneCandidates,
        string? term = null,
        bool caseSensitive = false,
        bool wholeWord = false)
    {
        IReadOnlyList<PdfField> fields;
        try { fields = page.GetFormFields(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }

        var changed = false;
        // #1098: a single-widget field often merges the field and widget into
        // ONE dictionary, so its /AP is reached twice (as field, as widget).
        // Rewriting removes the term the first time; the second pass would find
        // no match and drop the appearance we just fixed. Process each /AP once.
        var processedAp = new HashSet<PdfDictionary>();
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

            if (!widgets.Any(w => w.PageNumber == page.PageNumber && w.Rect.IntersectsWith(area)))
                continue;

            // Buttons are off/on/checked/unchecked names, never human-readable
            // text — TextExtractor never emits letters for them, so a match
            // can't reach this method for a Button field. Signature fields
            // USED to be excluded here too, on the same "no text" assumption
            // — but #669 fixed TextExtractor to read a signature widget's
            // /AP/N appearance text (a real "Digitally signed by…" block),
            // so a match can now legitimately reach here for a Signature
            // field, and skipping it would be exactly the "found but not
            // removable" gap #660 already had to fix for FreeText. Removing
            // /V here drops the reference to the signature dictionary (whose
            // /Reason, /Name, /Location strings can restate the same text as
            // the appearance) — since Save() only serializes objects
            // reachable from the trailer, letting that reference go is
            // enough for the dictionary to fall out of the saved bytes with
            // no separate prune step needed for it specifically.
            if (field.FieldType == PdfFieldType.Button)
                continue;

            CaptureObjectGraph(page.Document, field.RawDictionary.GetOptional("AP"), pruneCandidates);

            var defaultResources = GetAcroFormDefaultResources(page.Document);
            if (term != null)
            {
                // #1038: cut the term out, keep the rest of the value. See
                // ScrubTerm for what deleting it instead cost on a real file.
                changed |= RedactStringEntry(
                    page.Document, field.RawDictionary, "V", term, caseSensitive, pruneCandidates, wholeWord);
                changed |= RedactStringEntry(
                    page.Document, field.RawDictionary, "DV", term, caseSensitive, pruneCandidates, wholeWord);
                // #1098: rewrite the appearance to remove the term's GLYPHS so
                // the field still renders its remaining text in readers that
                // ignore /NeedAppearances. Drops /AP (leak-safe) only if the
                // rewrite can't be done.
                changed |= RewriteOrDropAppearance(
                    page, field.RawDictionary, defaultResources, term, caseSensitive, processedAp, wholeWord);
            }
            else
            {
                changed |= field.RawDictionary.Remove("V");
                changed |= field.RawDictionary.Remove("DV");
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
                changed |= term != null
                    ? RedactOptionList(
                        page.Document, field.RawDictionary, term, caseSensitive, pruneCandidates, wholeWord)
                    : field.RawDictionary.Remove("Opt");

            foreach (var widget in field.WidgetDictionaries)
            {
                CaptureObjectGraph(page.Document, widget.GetOptional("AP"), pruneCandidates);
                changed |= term != null
                    ? RewriteOrDropAppearance(page, widget, defaultResources, term, caseSensitive, processedAp, wholeWord)
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
    /// drop it (leak-safe) if the rewrite can't be done. Each appearance is
    /// touched at most once (<paramref name="processedAp"/>): a rewrite removes
    /// the term, so a second pass over the same shared dict would find no match
    /// and wrongly drop the appearance it just fixed.
    /// </summary>
    private static bool RewriteOrDropAppearance(
        PdfPage page, PdfDictionary holder, PdfDictionary? defaultResources,
        string term, bool caseSensitive, HashSet<PdfDictionary> processedAp, bool wholeWord = false)
    {
        if (page.Document.Resolve(holder.GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary ap)
            return false;   // nothing to touch
        if (!processedAp.Add(ap))
            return false;   // already handled via the merged field/widget dict
        if (AppearanceStreamRedactor.RedactTerm(page, ap, defaultResources, term, caseSensitive, wholeWord))
            return true;    // rewritten in place, kept
        return holder.Remove("AP");   // couldn't rewrite -> drop
    }

    private static bool RemoveIntersectingAnnotations(
        PdfPage page,
        PdfRectangle area,
        HashSet<int> pruneCandidates)
    {
        var annotsObj = page.Dictionary.GetOptional("Annots");
        if (annotsObj == null)
            return false;

        if (page.Document.Resolve(annotsObj) is not PdfArray annots)
            return false;

        var changed = false;
        for (var i = annots.Count - 1; i >= 0; i--)
        {
            var annotObj = annots[i];
            if (page.Document.Resolve(annotObj) is not PdfDictionary annot)
                continue;

            if (!TryGetRect(page.Document, annot.GetOptional("Rect"), out var rect) ||
                !rect.IntersectsWith(area))
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
            changed = true;
        }

        return changed;
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
