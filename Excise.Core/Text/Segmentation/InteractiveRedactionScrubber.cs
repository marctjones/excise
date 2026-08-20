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
    /// <para><b>/AP is still dropped, deliberately.</b> The appearance stream
    /// holds the term as drawn GLYPHS, and rewriting it is a separate piece of
    /// work; deleting it plus <c>/NeedAppearances</c> is the leak-safe move
    /// available today, and it is what already happened. So this change is
    /// strictly an improvement on every axis — same carriers dropped, minus the
    /// data destruction.</para>
    /// </summary>
    public static bool ScrubTerm(PdfPage page, PdfRectangle area, string term, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(term)) return ScrubArea(page, area);

        area = area.Normalize();
        var changed = false;
        var pruneCandidates = new HashSet<int>();

        changed |= ScrubFormFields(page, area, pruneCandidates, term, caseSensitive);
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
    private static string? WithoutTerm(string value, string term, bool caseSensitive)
    {
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var at = value.IndexOf(term, comparison);
        if (at < 0) return null;

        var sb = new System.Text.StringBuilder(value.Length);
        var from = 0;
        while (at >= 0)
        {
            sb.Append(value, from, at - from);
            from = at + term.Length;
            at = value.IndexOf(term, from, comparison);
        }
        sb.Append(value, from, value.Length - from);
        return sb.ToString();
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
        HashSet<int> pruneCandidates)
    {
        var raw = dictionary.GetOptional(key);
        if (raw == null) return false;
        if (document.Resolve(raw) is not PdfString str) return false;

        var redacted = WithoutTerm(str.Value, term, caseSensitive);
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
        HashSet<int> pruneCandidates)
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
                    if (WithoutTerm(entry.Value, term, caseSensitive) is { } cut)
                    {
                        options[i] = new PdfString(cut);
                        changed = true;
                    }
                    break;

                case PdfArray pair:
                    for (var j = 0; j < pair.Count; j++)
                    {
                        if (document.Resolve(pair[j]) is PdfString s2 &&
                            WithoutTerm(s2.Value, term, caseSensitive) is { } cut2)
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
        bool caseSensitive = false)
    {
        IReadOnlyList<PdfField> fields;
        try { fields = page.GetFormFields(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }

        var changed = false;
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

            if (term != null)
            {
                // #1038: cut the term out, keep the rest of the value. See
                // ScrubTerm for what deleting it instead cost on a real file.
                changed |= RedactStringEntry(
                    page.Document, field.RawDictionary, "V", term, caseSensitive, pruneCandidates);
                changed |= RedactStringEntry(
                    page.Document, field.RawDictionary, "DV", term, caseSensitive, pruneCandidates);
            }
            else
            {
                changed |= field.RawDictionary.Remove("V");
                changed |= field.RawDictionary.Remove("DV");
            }

            // Dropped in both modes. The appearance draws the term as glyphs
            // and nothing here rewrites those; /NeedAppearances below is what
            // gets the redacted value back on screen.
            changed |= field.RawDictionary.Remove("AP");

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
                        page.Document, field.RawDictionary, term, caseSensitive, pruneCandidates)
                    : field.RawDictionary.Remove("Opt");

            foreach (var widget in field.WidgetDictionaries)
            {
                CaptureObjectGraph(page.Document, widget.GetOptional("AP"), pruneCandidates);
                changed |= widget.Remove("AP");
            }
        }

        if (changed)
            page.Document.SetAcroFormNeedAppearances();

        return changed;
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
