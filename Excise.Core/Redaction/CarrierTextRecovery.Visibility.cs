using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

public static partial class CarrierTextRecovery
{
    // Annotation flags (§12.5.3, Table 167) that keep an appearance off screen.
    private const int AnnotationFlagHidden = 1 << 1;
    private const int AnnotationFlagNoView = 1 << 5;

    /// <summary>
    /// Mark each finding as visible-elsewhere or hidden, and record how close
    /// it sits to a redaction mark; hidden findings next to a mark come first.
    /// </summary>
    /// <remarks>
    /// A carrier that restates what the reader already sees — a field value
    /// its widget draws, the document title, a bookmark — is not a leak, and
    /// reporting it as one buried the real findings. "Visible" is: each page's
    /// drawn text (without hidden optional content and without text a dark box
    /// covers), the normal appearance of every annotation and widget that is
    /// not flagged hidden, FreeText contents, the /Info and XMP titles, and the
    /// outline titles. Comparison ignores case and all whitespace.
    /// A carrier a widget owns (<see cref="CarrierText.VisibleScope"/>) is
    /// compared against that widget's own appearance instead: a redacted field
    /// whose value is also printed elsewhere is still hidden.
    /// <para>Known gap: text drawn in render mode 3 (invisible) counts as
    /// visible here, because the walker does not tag a letter with its render
    /// mode. A carrier that restates invisible OCR text is therefore classed a
    /// duplicate.</para>
    /// </remarks>
    private static IReadOnlyList<CarrierText> Classify(PdfDocument doc, List<CarrierText> found, CancellationToken ct)
    {
        if (found.Count == 0) return found;

        string visible;
        Dictionary<int, List<PdfRectangle>> marks;
        try
        {
            (visible, marks) = BuildVisibility(doc, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Unknown visibility: report everything as hidden rather than hide a leak.
            visible = "";
            marks = new Dictionary<int, List<PdfRectangle>>();
        }

        var classified = new List<CarrierText>(found.Count);
        foreach (var f in found)
        {
            var isVisible = f.Kind == CarrierFindingKind.Text
                            && IsVisible(f.Text, f.VisibleScope is null ? visible : ScopeNormalized(f.VisibleScope));
            classified.Add(f with
            {
                VisibleElsewhere = isVisible,
                NearRedaction = Proximity(f, marks),
            });
        }

        // Stable: hidden before visible; within each, overlapping a mark first.
        return classified
            .Select((f, i) => (f, i))
            .OrderBy(x => x.f.VisibleElsewhere)
            .ThenByDescending(x => x.f.NearRedaction)
            .ThenBy(x => x.i)
            .Select(x => x.f)
            .ToList();
    }

    private static bool IsVisible(string text, string visibleNormalized)
    {
        var needle = NormalizeForVisibility(text);
        return needle.Length > 0 && visibleNormalized.Contains(needle, StringComparison.Ordinal);
    }

    private static string ScopeNormalized(string scope) =>
        string.Join("\u0000", scope.Split('\u0000').Select(NormalizeForVisibility));

    internal static string NormalizeForVisibility(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '\u200B' or '\uFEFF') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static CarrierRedactionProximity Proximity(CarrierText f, Dictionary<int, List<PdfRectangle>> marks)
    {
        if (f.PageNumber <= 0 || !marks.TryGetValue(f.PageNumber, out var pageMarks) || pageMarks.Count == 0)
            return CarrierRedactionProximity.None;
        if (f.Area is { } area && pageMarks.Any(m => m.IntersectsWith(area)))
            return CarrierRedactionProximity.Overlapping;
        return CarrierRedactionProximity.SamePage;
    }

    /// <summary>
    /// The document's visible text (normalised, joined with a separator a
    /// normalised needle cannot contain) and its redaction marks per page.
    /// </summary>
    private static (string Visible, Dictionary<int, List<PdfRectangle>> Marks) BuildVisibility(PdfDocument doc, CancellationToken ct)
    {
        var parts = new List<string>();
        var marks = new Dictionary<int, List<PdfRectangle>>();

        for (var i = 1; i <= doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc.GetPage(i);
            var pageMarks = new List<PdfRectangle>();

            IReadOnlyList<HiddenTextRecord> covered = Array.Empty<HiddenTextRecord>();
            try
            {
                pageMarks.AddRange(HiddenTextDetector.DarkFilledBoxes(page));
                covered = HiddenTextDetector.ScanPage(page, i, includeVisibleFailedRedactions: true);
                pageMarks.AddRange(covered.Select(h => h.BoundingBox));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // Marks are a ranking hint; their absence only lowers a rank.
            }

            // Drawn text: content letters only (no synthetic form/annotation
            // letters — their appearances are added below, from the streams a
            // viewer actually paints), minus hidden layers and covered text.
            var letters = new TextExtractor(page) { IncludeFormFieldValues = false }.ExtractLetters(ct)
                .Where(l => !l.IsInHiddenOptionalContent && !IsCovered(l, covered))
                .ToList();
            parts.Add(LettersToText(letters));

            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
            {
                var dr = AcroFormDefaultResources(doc);
                foreach (var a in annots)
                {
                    if (doc.Resolve(a) is not PdfDictionary annot) continue;
                    var subtype = annot.GetNameOrNull("Subtype");
                    if (subtype == "Redact" && RectOf(doc, annot) is { } redactRect)
                        pageMarks.Add(redactRect);
                    if (!IsViewable(doc, annot)) continue;
                    parts.Add(NormalAppearanceText(doc, page, annot, dr, ct) ?? "");
                    if (subtype == "FreeText" && annot.GetOptional("AP") == null)
                        parts.Add(ReadText(doc, annot, "Contents") ?? "");
                }
            }

            if (pageMarks.Count > 0) marks[i] = pageMarks;
        }

        // What the viewer shows outside the page: the title and the bookmarks.
        if (doc.Resolve(doc.Trailer.GetOptional("Info") ?? PdfNull.Instance) is PdfDictionary info)
            parts.Add(ReadText(doc, info, "Title") ?? "");
        foreach (var title in XmpTitles(doc)) parts.Add(title);
        try
        {
            var stack = new Stack<PdfOutlineItem>(PdfOutlineParser.Parse(doc));
            var guard = 0;
            while (stack.Count > 0 && guard++ < WalkGuard)
            {
                var item = stack.Pop();
                parts.Add(item.Title);
                foreach (var child in item.Children) stack.Push(child);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        // NUL separates the parts: normalisation strips control characters
        // from every needle, so no match can span two parts.
        return (string.Join("\u0000", parts.Select(NormalizeForVisibility)), marks);
    }

    private static bool IsCovered(Letter letter, IReadOnlyList<HiddenTextRecord> covered)
    {
        if (covered.Count == 0) return false;
        var r = letter.GlyphRectangle;
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Bottom + r.Top) / 2;
        foreach (var h in covered)
        {
            var b = h.BoundingBox.Normalize();
            if (cx >= b.Left && cx <= b.Right && cy >= b.Bottom && cy <= b.Top) return true;
        }
        return false;
    }

    private static bool IsViewable(PdfDocument doc, PdfDictionary annot)
    {
        var flags = doc.Resolve(annot.GetOptional("F") ?? PdfNull.Instance).TryGetNumber(out var f) ? (int)f : 0;
        return (flags & (AnnotationFlagHidden | AnnotationFlagNoView)) == 0;
    }

    /// <summary>The text of the appearance a viewer paints: /N, or /N's /AS state.</summary>
    private static string? NormalAppearanceText(PdfDocument doc, PdfPage page, PdfDictionary annot, PdfDictionary? dr, CancellationToken ct)
    {
        if (doc.Resolve(annot.GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary ap) return null;
        var normal = doc.Resolve(ap.GetOptional("N") ?? PdfNull.Instance);
        if (normal is PdfDictionary states and not PdfStream)
        {
            var state = annot.GetNameOrNull("AS");
            normal = state is null ? PdfNull.Instance : doc.Resolve(states.GetOptional(state) ?? PdfNull.Instance);
        }
        return normal is PdfStream stream ? StreamPaintedText(doc, page, stream, dr, ct) : null;
    }

    private static IEnumerable<string> XmpTitles(PdfDocument doc)
    {
        var titles = new List<string>();
        if (doc.Resolve(doc.Catalog?.GetOptional("Metadata") ?? PdfNull.Instance) is not PdfStream md) return titles;
        var bytes = SafeDecoded(md);
        if (bytes is null || !Excise.Core.Operations.XfaXmlCarrier.TryLoadXml(bytes, out var xmp, out _) || xmp.Root is null)
            return titles;
        foreach (var e in xmp.Root.Descendants())
            if (e.Name.LocalName == "title" && e.Name.NamespaceName == "http://purl.org/dc/elements/1.1/")
                titles.Add(e.Value);
        return titles;
    }
}
