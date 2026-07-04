using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Text.Segmentation;

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

    private static bool ScrubFormFields(
        PdfPage page,
        PdfRectangle area,
        HashSet<int> pruneCandidates)
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

            if (field.FieldType is PdfFieldType.Button or PdfFieldType.Signature)
                continue;

            CaptureObjectGraph(page.Document, field.RawDictionary.GetOptional("AP"), pruneCandidates);
            changed |= field.RawDictionary.Remove("V");
            changed |= field.RawDictionary.Remove("DV");
            changed |= field.RawDictionary.Remove("AP");

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
