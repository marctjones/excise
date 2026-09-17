using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// RC18 CERTAIN-channel recovery from unscrubbed CARRIERS. A redaction that
/// rewrote the visible page content but missed a carrier leaves the term
/// physically present and trivially readable. Unlike the width-residue channel
/// this asserts nothing probabilistic: the text is right there in the bytes.
/// </summary>
/// <remarks>
/// <para>
/// This is the READ mirror of the scrubbers (<see cref="StructureTreeRedactionScrubber"/>,
/// <c>PdfDocumentSanitizer</c>, <c>InteractiveRedactionScrubber</c>) and goes
/// further than they do, because an adversary does: what recovery reports here
/// is text a reader of the file can get without guessing. Carriers:
/// </para>
/// <list type="bullet">
///   <item>structure tree <c>/ActualText</c>, <c>/Alt</c>, <c>/E</c>, <c>/T</c> (#636);</item>
///   <item>annotation <c>/Contents</c>, <c>/RC</c>, <c>/Subj</c>, <c>/OverlayText</c>,
///     markup author <c>/T</c>, and every annotation's appearance-stream text;</item>
///   <item>AcroForm <c>/V</c>, <c>/DV</c>, <c>/RV</c>, <c>/TU</c>, <c>/Opt</c> (both halves
///     of an export/display pair) and widget <c>/MK</c> captions;</item>
///   <item>JavaScript and <c>/URI</c>/file targets of every action location;</item>
///   <item>XFA datasets values and template default values, captions, list items,
///     tooltips and scripts;</item>
///   <item>attachments (document name tree, <c>/AF</c> on the catalog, pages and
///     annotations, <c>/FileAttachment</c> annotations): name, <c>/F</c>, <c>/UF</c>,
///     <c>/Desc</c>, text-like payloads, and nested PDFs recursively;</item>
///   <item><c>/Info</c> (content keys only), XMP (content properties only), outline
///     titles, <c>/PieceInfo</c> private data, text in hidden optional content;</item>
///   <item>unreferenced (orphan) objects, and text an incremental update superseded
///     but left in an earlier revision.</item>
/// </list>
/// <para>
/// Noise control is by FILTERING, not by omitting carriers: tool-written
/// metadata (<c>/Producer</c>, <c>/Creator</c>, dates, XMP ids and history) is
/// not reported, and a prior revision reports only what the current revision no
/// longer contains. Content that is present but cannot be decoded — an opaque
/// attachment, a page thumbnail, an unreadable packet — is reported as
/// <see cref="CarrierFindingKind.Presence"/>, never dropped: a carrier the scan
/// could not read is a carrier it did not examine.
/// </para>
/// <para>
/// Bounded and cancellable. This is an audit pass for <c>excise unredact</c>; it
/// is deliberately not called on open, render, or redaction.
/// </para>
/// </remarks>
public static partial class CarrierTextRecovery
{
    /// <summary>What a finding asserts.</summary>
    public enum CarrierFindingKind
    {
        /// <summary>The text is present and was read back verbatim.</summary>
        Text = 0,

        /// <summary>
        /// Content is present that the scan did not decode into text (an opaque
        /// attachment, a thumbnail image, an unreadable packet). Not a recovery —
        /// a statement that something is there to look at.
        /// </summary>
        Presence = 1,
    }

    /// <summary>A recoverable string found in a carrier the visible page does not show.</summary>
    /// <param name="Carrier">Human-readable carrier name, e.g. "structure-tree /ActualText".</param>
    /// <param name="Text">The recovered text, verbatim (truncated past a bound, and marked so).
    /// For a <see cref="CarrierFindingKind.Presence"/> finding, a description of what is present.</param>
    /// <param name="PageNumber">1-based page when the carrier belongs to a page; 0 for document-level carriers.</param>
    /// <param name="ObjectNumber">Indirect object number holding the carrier, or 0 when it is direct or unknown.</param>
    public readonly record struct CarrierText(string Carrier, string Text, int PageNumber, int ObjectNumber = 0)
    {
        /// <summary>Whether this is recovered text or a presence note.</summary>
        public CarrierFindingKind Kind { get; init; }

        /// <summary>Where inside the carrier: a field name, attachment name, revision, element path.</summary>
        public string? Location { get; init; }

        /// <summary>
        /// True when the text is already visible to a reader of the document —
        /// drawn on a page, in a visible annotation or widget appearance, or in
        /// the title bar / bookmarks. Such a finding restates what the reader
        /// can see; it is not a leak. False for everything hidden, and always
        /// false for <see cref="CarrierFindingKind.Presence"/>.
        /// </summary>
        public bool VisibleElsewhere { get; init; }

        /// <summary>How close the carrier sits to a redaction mark.</summary>
        public CarrierRedactionProximity NearRedaction { get; init; }

        /// <summary>The page-space rectangle of the owning annotation or widget, when there is one.</summary>
        public PdfRectangle? Area { get; init; }

        /// <summary>
        /// For a carrier owned by a widget, the text that widget itself paints:
        /// the finding is visible only if it appears THERE, not merely somewhere
        /// in the document (a redacted field whose value also occurs in a page
        /// header is still a leak). Null = compare against the whole document.
        /// </summary>
        internal string? VisibleScope { get; init; }
    }

    /// <summary>
    /// Whether a finding sits near a redaction mark — a dark filled box, a
    /// <c>/Redact</c> annotation, or text a box covers. A hidden carrier next to
    /// a redaction is the classic leak: the page was blacked out, the carrier
    /// was not.
    /// </summary>
    public enum CarrierRedactionProximity
    {
        /// <summary>No redaction mark on the finding's page, or a document-level carrier.</summary>
        None = 0,

        /// <summary>The finding's page carries a redaction mark.</summary>
        SamePage = 1,

        /// <summary>The finding's annotation or widget rectangle overlaps a redaction mark.</summary>
        Overlapping = 2,
    }

    // ── Bounds ────────────────────────────────────────────────────────────
    internal const int MaxFindings = 5_000;
    internal const int MaxTextChars = 4_000;
    internal const int MaxAttachmentDepth = 3;
    internal const int MaxTextPayloadBytes = 4 * 1024 * 1024;
    internal const int MaxNestedPdfBytes = 64 * 1024 * 1024;
    internal const long MaxSourceBytes = 512L * 1024 * 1024;
    internal const int MaxRevisions = 16;
    internal const int MaxOrphanObjects = 100_000;
    internal const int WalkGuard = 200_000;

    /// <summary>Scan every carrier of <paramref name="doc"/>.</summary>
    public static IReadOnlyList<CarrierText> Scan(PdfDocument doc) => Scan(doc, CancellationToken.None);

    /// <summary>Scan every carrier of <paramref name="doc"/>, honouring cancellation.</summary>
    public static IReadOnlyList<CarrierText> Scan(PdfDocument doc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var found = new List<CarrierText>();
        var collector = new Collector(found, cancellationToken, prefix: "", depth: 0);
        ScanInto(doc, collector, includeHistory: true);
        return Classify(doc, found, cancellationToken);
    }

    /// <summary>
    /// Run every scanner against <paramref name="doc"/>. <paramref name="includeHistory"/>
    /// is false when scanning a prior revision: its own orphans and revisions are
    /// the same bytes the outer scan already covers.
    /// </summary>
    private static void ScanInto(PdfDocument doc, Collector c, bool includeHistory)
    {
        var interactive = new InteractiveIndex();
        Guarded(c, "structure tree", () => ScanStructureTree(doc, c));
        Guarded(c, "annotations", () => ScanAnnotations(doc, c, interactive));
        Guarded(c, "AcroForm", () => ScanAcroForm(doc, c, interactive));
        Guarded(c, "actions", () => ScanActions(doc, c, interactive));
        Guarded(c, "XFA", () => ScanXfa(doc, c));
        Guarded(c, "attachments", () => ScanAttachments(doc, c));
        Guarded(c, "/Info", () => ScanInfo(doc, c));
        Guarded(c, "XMP", () => ScanXmp(doc, c));
        Guarded(c, "outlines", () => ScanOutlines(doc, c));
        Guarded(c, "optional content", () => ScanHiddenOptionalContent(doc, c));
        Guarded(c, "page thumbnails", () => ScanThumbnails(doc, c));
        Guarded(c, "/PieceInfo", () => ScanPieceInfo(doc, c));
        if (!includeHistory) return;
        Guarded(c, "orphan objects", () => ScanOrphans(doc, c));
        Guarded(c, "prior revisions", () => ScanPriorRevisions(doc, c));
    }

    /// <summary>
    /// A scanner that throws has NOT examined its carrier. Say so, rather than
    /// letting one malformed structure read as a clean document.
    /// </summary>
    private static void Guarded(Collector c, string carrier, Action scan)
    {
        c.Token.ThrowIfCancellationRequested();
        if (c.Full) return;
        try
        {
            scan();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            c.Presence(carrier, $"not examined: {ex.GetType().Name}: {ex.Message}", 0);
        }
    }

    /// <summary>
    /// Accumulates findings under one budget, shared by nested scans (an
    /// attachment's PDF, a prior revision), which only add a label prefix.
    /// </summary>
    private sealed class Collector
    {
        private readonly List<CarrierText> _found;
        private readonly HashSet<(string, string, int, int, CarrierFindingKind)> _seen;
        private readonly SharedBudget _budget;

        public Collector(List<CarrierText> found, CancellationToken token, string prefix, int depth)
            : this(found, new HashSet<(string, string, int, int, CarrierFindingKind)>(), new SharedBudget(), token, prefix, depth, hostPage: 0)
        {
        }

        private Collector(
            List<CarrierText> found,
            HashSet<(string, string, int, int, CarrierFindingKind)> seen,
            SharedBudget budget,
            CancellationToken token,
            string prefix,
            int depth,
            int hostPage)
        {
            _found = found;
            _seen = seen;
            _budget = budget;
            Token = token;
            Prefix = prefix;
            Depth = depth;
            HostPage = hostPage;
        }

        public CancellationToken Token { get; }
        public string Prefix { get; }
        public int Depth { get; }

        /// <summary>Page of the outer document that owns a nested scan (0 = none).</summary>
        public int HostPage { get; }

        public bool Full => _budget.Exhausted;

        /// <summary>A child scan whose findings are labelled "<paramref name="label"/> &gt; …".</summary>
        public Collector Nested(string label, int hostPage, bool deeper) =>
            new(_found, _seen, _budget, Token, Prefix + label + " > ", deeper ? Depth + 1 : Depth, hostPage);

        public void Text(string carrier, string? text, int page, int obj = 0, string? location = null, PdfRectangle? area = null,
            string? visibleScope = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Add(carrier, Clip(text.Trim()), page, obj, location, CarrierFindingKind.Text, area, visibleScope);
        }

        public void Presence(string carrier, string description, int page, int obj = 0, string? location = null, PdfRectangle? area = null) =>
            Add(carrier, description, page, obj, location, CarrierFindingKind.Presence, area, null);

        private void Add(string carrier, string text, int page, int obj, string? location, CarrierFindingKind kind, PdfRectangle? area,
            string? visibleScope)
        {
            Token.ThrowIfCancellationRequested();
            if (_budget.Exhausted) return;
            var label = Prefix + carrier;
            // A nested scan's own page numbers are not pages of THIS document;
            // they move into the location and the host page stands in.
            var effectivePage = page;
            var effectiveLocation = location;
            if (Prefix.Length > 0)
            {
                if (page > 0)
                    effectiveLocation = location is null ? $"nested page {page}" : $"{location}, nested page {page}";
                effectivePage = HostPage;
            }
            if (!_seen.Add((label, text, effectivePage, obj, kind))) return;

            if (_found.Count >= MaxFindings)
            {
                _budget.Exhausted = true;
                _found.Add(new CarrierText("finding limit", $"stopped after {MaxFindings} findings; remaining carriers were not reported", 0)
                {
                    Kind = CarrierFindingKind.Presence,
                });
                return;
            }

            _found.Add(new CarrierText(label, text, effectivePage, Prefix.Length > 0 ? 0 : obj)
            {
                Kind = kind,
                Location = effectiveLocation,
                Area = Prefix.Length > 0 ? null : area,
                // A nested document's widgets are not visible in THIS one.
                VisibleScope = Prefix.Length > 0 ? null : visibleScope,
            });
        }

        private static string Clip(string text) =>
            text.Length <= MaxTextChars
                ? text
                : text[..MaxTextChars] + $" …[truncated {text.Length - MaxTextChars} chars]";

        private sealed class SharedBudget
        {
            public bool Exhausted;
        }
    }

    /// <summary>
    /// Widgets and fields seen by one scanner, so the next does not report or
    /// walk them twice.
    /// </summary>
    private sealed class InteractiveIndex
    {
        public HashSet<PdfDictionary> AnnotationsWithAppearanceScanned { get; } = new(ReferenceEqualityComparer.Instance);
        public List<(PdfDictionary Dict, int ObjectNumber, int Page, string Name)> Fields { get; } = new();
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    /// <summary>Resolve, remembering the object number when the value was a reference.</summary>
    private static PdfObject Deref(PdfDocument doc, PdfObject? obj, out int objectNumber)
    {
        objectNumber = obj is PdfReference r ? r.ObjectNum : 0;
        return obj == null ? PdfNull.Instance : doc.Resolve(obj);
    }

    /// <summary>
    /// A string-valued entry, resolving an indirect string first (#1155), or the
    /// decoded text of a stream-valued one (JavaScript and rich text may be either).
    /// </summary>
    private static string? ReadText(PdfDocument doc, PdfDictionary dict, string key) =>
        ObjectText(doc, dict.GetOptional(key));

    private static string? ObjectText(PdfDocument doc, PdfObject? value) =>
        Deref(doc, value, out _) switch
        {
            PdfString s => s.Value,
            PdfStream st => DecodeTextBytes(SafeDecoded(st)),
            _ => null,
        };

    private static byte[]? SafeDecoded(PdfStream stream)
    {
        try { return stream.DecodedData; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>BOM-aware text decode: UTF-8 when valid, otherwise Latin-1.</summary>
    internal static string? DecodeTextBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// True when the bytes read as text: few control characters outside
    /// whitespace. Used to decide whether an unlabelled stream is worth
    /// reporting as text rather than as opaque presence.
    /// </summary>
    internal static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        var sample = Math.Min(bytes.Length, 64 * 1024);
        var control = 0;
        for (var i = 0; i < sample; i++)
        {
            var b = bytes[i];
            if (b == 0) return false;
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D or 0x0C)) control++;
        }
        return control * 50 <= sample;
    }

    /// <summary>Words from letters, in extraction order, joined with spaces.</summary>
    private static string LettersToText(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0) return "";
        return string.Join(" ", TextExtractor.BuildWords(letters).Select(w => w.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    // ── Structure tree ────────────────────────────────────────────────────

    // The structure-tree carriers that spell real content (§14.9.4 / §14.7.2):
    // the actual text a span represents, a figure's alternate description, an
    // abbreviation expansion, and the element's title.
    private static readonly string[] StructCarriers = { "ActualText", "Alt", "E", "T" };

    private static void ScanStructureTree(PdfDocument doc, Collector c)
    {
        if (doc.Resolve(doc.Catalog?.GetOptional("StructTreeRoot") ?? PdfNull.Instance) is not PdfDictionary root)
            return;

        var stack = new Stack<PdfObject>();
        if (root.GetOptional("K") is { } k) stack.Push(k);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        // #1587: glyph boxes per (page, MCID), built lazily. A document with no
        // located carrier never pays for it, and one with many pays once per
        // page rather than once per carrier.
        var mcidBoxes = new Dictionary<int, IReadOnlyDictionary<int, PdfRectangle>>();
        var guard = 0;
        while (stack.Count > 0 && guard++ < WalkGuard)
        {
            var raw = stack.Pop();
            var node = Deref(doc, raw, out var objNum);
            if (node is PdfArray arr)
            {
                foreach (var e in arr) stack.Push(e);
                continue;
            }
            if (node is not PdfDictionary elem || !visited.Add(elem)) continue;

            // #1587: a structure carrier has no geometry of its own, so a report
            // built from it could say only "somewhere in the document". Locating
            // it costs a page walk, so only do it for an element that actually
            // carries one.
            var hasCarrier = StructCarriers.Any(carrier => elem.ContainsKey(carrier));
            var (pageNumber, area) = hasCarrier
                ? LocateStructureElement(doc, elem, mcidBoxes)
                : (0, (PdfRectangle?)null);

            foreach (var carrier in StructCarriers)
                c.Text($"structure-tree /{carrier}", ReadText(doc, elem, carrier), pageNumber, objNum,
                    elem.GetNameOrNull("S") is { } s ? $"/{s}" : null, area);
            if (elem.GetOptional("K") is { } kids) stack.Push(kids);
        }
    }

    /// <summary>
    /// #1587 — where a structure element's text sits on the page: the page its
    /// <c>/Pg</c> names, and the union of the glyph boxes its <c>/MCID</c>
    /// marked content painted (§14.7.4.3).
    ///
    /// <para>A carrier value has no geometry of its own — it is a property on a
    /// tree node, not a drawing operation — so the MCID bridge is the only
    /// evidence available for where the text it restates belonged. Returns page
    /// 0 and no rectangle rather than guessing when <c>/Pg</c> is absent,
    /// resolves to no page here, or names MCIDs this page never painted (the
    /// normal case AFTER a redaction removed those glyphs, which is exactly
    /// when this channel fires).</para>
    /// </summary>
    private static (int PageNumber, PdfRectangle? Area) LocateStructureElement(
        PdfDocument doc,
        PdfDictionary elem,
        Dictionary<int, IReadOnlyDictionary<int, PdfRectangle>> cache)
    {
        if (elem.GetOptional("Pg") is not PdfReference pageRef) return (0, null);

        var pageNumber = 0;
        for (var i = 1; i <= doc.PageCount; i++)
        {
            var candidate = doc.GetPageReference(i);
            if (candidate != null && candidate.ObjectNumber == pageRef.ObjectNumber)
            {
                pageNumber = i;
                break;
            }
        }
        if (pageNumber == 0) return (0, null);

        var mcids = CollectMcids(doc, elem.GetOptional("K")).ToList();
        if (mcids.Count == 0) return (pageNumber, null);

        if (!cache.TryGetValue(pageNumber, out var boxes))
        {
            boxes = McidBoxesOf(doc.GetPage(pageNumber));
            cache[pageNumber] = boxes;
        }

        PdfRectangle? union = null;
        foreach (var mcid in mcids)
        {
            if (!boxes.TryGetValue(mcid, out var box)) continue;
            union = union is { } u ? UnionOf(u, box) : box;
        }
        return (pageNumber, union);
    }

    /// <summary>Union of each MCID's glyph boxes on one page.</summary>
    private static IReadOnlyDictionary<int, PdfRectangle> McidBoxesOf(PdfPage page)
    {
        var boxes = new Dictionary<int, PdfRectangle>();
        IReadOnlyList<Text.Letter> letters;
        try { letters = page.Letters; }
        catch { return boxes; }

        foreach (var letter in letters)
        {
            if (letter.MarkedContentId is not { } id) continue;
            var box = letter.GlyphRectangle.Normalize();
            boxes[id] = boxes.TryGetValue(id, out var existing) ? UnionOf(existing, box) : box;
        }
        return boxes;
    }

    /// <summary>
    /// The MCIDs a structure element owns, including through nested
    /// <c>StructElem</c> children — a <c>/Span</c> carrying the
    /// <c>/ActualText</c> usually holds its glyphs one level down.
    /// </summary>
    private static IEnumerable<int> CollectMcids(PdfDocument doc, PdfObject? k, int depth = 0)
    {
        if (k == null || depth > 16) yield break;
        switch (doc.Resolve(k))
        {
            case PdfInteger direct:
                yield return (int)direct.Value;
                break;

            case PdfArray array:
                foreach (var item in array)
                    foreach (var id in CollectMcids(doc, item, depth + 1))
                        yield return id;
                break;

            case PdfDictionary dict:
                // A marked-content reference (§14.7.4.3), or a child StructElem
                // whose own /K holds the ids.
                if (dict.GetOptional("Type") is PdfName { Value: "MCR" } &&
                    dict.GetOptional("MCID") is PdfInteger mcr)
                {
                    yield return (int)mcr.Value;
                }
                else if (dict.GetOptional("K") is { } nested)
                {
                    foreach (var id in CollectMcids(doc, nested, depth + 1))
                        yield return id;
                }
                break;
        }
    }

    private static PdfRectangle UnionOf(PdfRectangle a, PdfRectangle b)
    {
        var x = a.Normalize();
        var y = b.Normalize();
        return new PdfRectangle(
            Math.Min(x.Left, y.Left), Math.Min(x.Bottom, y.Bottom),
            Math.Max(x.Right, y.Right), Math.Max(x.Top, y.Top));
    }

    // ── Annotations ───────────────────────────────────────────────────────

    // /Contents is the note/markup text (§12.5.6); /RC its rich-text restatement
    // (§12.5.6.2, #1185); /Subj the subject (#1427); /OverlayText what a /Redact
    // annotation shows before it is applied (#1427).
    private static readonly string[] AnnotationTextKeys = { "Contents", "RC", "Subj", "OverlayText" };

    private static void ScanAnnotations(PdfDocument doc, Collector c, InteractiveIndex index)
    {
        var dr = AcroFormDefaultResources(doc);
        for (var i = 1; i <= doc.PageCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var page = doc.GetPage(i);
            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;
            foreach (var annotObj in annots)
            {
                if (Deref(doc, annotObj, out var objNum) is not PdfDictionary annot) continue;
                var subtype = annot.GetNameOrNull("Subtype");
                var isWidget = subtype == "Widget";
                var area = RectOf(doc, annot);
                foreach (var key in AnnotationTextKeys)
                    c.Text($"annotation /{key}", ReadText(doc, annot, key), i, objNum, subtype is null ? null : $"/{subtype}", area);

                // /T is the FIELD NAME on a widget (noise), but the AUTHOR on a
                // markup annotation (§12.5.6.2) — a person's name.
                if (!isWidget && subtype is not ("Link" or "Popup"))
                    c.Text("annotation /T (author)", ReadText(doc, annot, "T"), i, objNum, subtype is null ? null : $"/{subtype}", area);

                if (index.AnnotationsWithAppearanceScanned.Add(annot))
                    ScanAppearance(doc, page, annot, dr, c, i, objNum, isWidget ? "widget" : "annotation", subtype);
            }
        }
    }

    /// <summary>An annotation's normalised /Rect, or null.</summary>
    private static PdfRectangle? RectOf(PdfDocument doc, PdfDictionary annot)
    {
        if (doc.Resolve(annot.GetOptional("Rect") ?? PdfNull.Instance) is not PdfArray r || r.Count < 4) return null;
        var v = new double[4];
        for (var k = 0; k < 4; k++)
            if (!doc.Resolve(r[k]).TryGetNumber(out v[k])) return null;
        return new PdfRectangle(v[0], v[1], v[2], v[3]).Normalize();
    }

    private static PdfDictionary? AcroFormDefaultResources(PdfDocument doc) =>
        doc.Resolve(doc.Catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acro
            ? doc.Resolve(acro.GetOptional("DR") ?? PdfNull.Instance) as PdfDictionary
            : null;

    /// <summary>
    /// The text an appearance stream paints (§12.5.5). Decoded through the one
    /// content walker (<see cref="TextExtractor.ExtractLettersFrom"/>) with the
    /// stream's own resources, falling back to the AcroForm <c>/DR</c> —
    /// the same path <c>AppearanceStreamRedactor</c> removes glyphs through.
    /// </summary>
    private static void ScanAppearance(
        PdfDocument doc, PdfPage page, PdfDictionary annot, PdfDictionary? defaultResources,
        Collector c, int pageNumber, int annotObj, string owner, string? subtype)
    {
        if (doc.Resolve(annot.GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary ap) return;
        var area = RectOf(doc, annot);
        foreach (var mode in new[] { "N", "R", "D" })
        {
            var entry = Deref(doc, ap.GetOptional(mode), out var streamObj);
            if (entry is PdfStream single)
            {
                ReportAppearanceStream(doc, page, single, defaultResources, c, pageNumber, streamObj != 0 ? streamObj : annotObj,
                    $"{owner} /AP /{mode}", subtype is null ? null : $"/{subtype}", area,
                    mode == "N" ? AppearanceScope(doc, annot) : null);
            }
            else if (entry is PdfDictionary states)
            {
                // Buttons: /N is a dictionary of state name → stream.
                foreach (var (stateName, stateValue) in states)
                {
                    if (Deref(doc, stateValue, out var stateObj) is PdfStream stateStream)
                        ReportAppearanceStream(doc, page, stateStream, defaultResources, c, pageNumber,
                            stateObj != 0 ? stateObj : annotObj, $"{owner} /AP /{mode}", $"state /{stateName.Value}", area,
                            mode == "N" && annot.GetNameOrNull("AS") == stateName.Value ? AppearanceScope(doc, annot) : null);
                }
            }
        }
    }

    private static void ReportAppearanceStream(
        PdfDocument doc, PdfPage page, PdfStream stream, PdfDictionary? defaultResources,
        Collector c, int pageNumber, int objNum, string carrier, string? location, PdfRectangle? area, string? scope)
    {
        var text = StreamPaintedText(doc, page, stream, defaultResources, c.Token);
        // A painted normal appearance is visible exactly when its annotation is.
        c.Text(carrier, text, pageNumber, objNum, location, area,
            scope is null ? null : scope.Length > 0 ? text : "");
    }

    /// <summary>"" when the annotation is not shown (hidden/NoView); a non-empty marker when it is.</summary>
    private static string AppearanceScope(PdfDocument doc, PdfDictionary annot) =>
        IsViewable(doc, annot) ? "shown" : "";

    private static string? StreamPaintedText(
        PdfDocument doc, PdfPage page, PdfStream stream, PdfDictionary? fallbackResources, CancellationToken ct)
    {
        var content = SafeDecoded(stream);
        if (content is null || content.Length == 0) return null;
        var resources = doc.Resolve(stream.GetOptional("Resources") ?? PdfNull.Instance) as PdfDictionary
                        ?? fallbackResources;
        var letters = new TextExtractor(page) { IncludeFormFieldValues = false }
            .ExtractLettersFrom(content, resources, ct);
        return LettersToText(letters);
    }
}
