using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Parsing;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

public static partial class CarrierTextRecovery
{
    // ── /Info (§14.3.3) ───────────────────────────────────────────────────

    // Written by software, not by the author: reporting them would put a
    // finding on every PDF ever saved and bury the real ones.
    private static readonly HashSet<string> ToolInfoKeys = new(StringComparer.Ordinal)
    {
        "Producer", "Creator", "CreationDate", "ModDate", "Trapped",
    };

    private static void ScanInfo(PdfDocument doc, Collector c)
    {
        var infoObj = Deref(doc, doc.Trailer.GetOptional("Info"), out var objNum);
        if (infoObj is not PdfDictionary info) return;
        foreach (var (key, value) in info)
        {
            if (ToolInfoKeys.Contains(key.Value)) continue;
            c.Text($"/Info /{key.Value}", ObjectText(doc, value), 0, objNum);
        }
    }

    // ── XMP (§14.3.2) ─────────────────────────────────────────────────────

    // Machine-written XMP: ids, history, conformance claims, and the PDF/A
    // extension-schema boilerplate that describes other properties.
    private static readonly HashSet<string> ToolXmpNamespaces = new(StringComparer.Ordinal)
    {
        "http://ns.adobe.com/xap/1.0/mm/",
        "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#",
        "http://ns.adobe.com/xap/1.0/sType/ResourceRef#",
        "http://ns.adobe.com/xap/1.0/sType/Version#",
        "http://www.aiim.org/pdfa/ns/id/",
        "http://www.aiim.org/pdfua/ns/id/",
        "http://www.aiim.org/pdfa/ns/extension/",
        "http://www.aiim.org/pdfa/ns/schema#",
        "http://www.aiim.org/pdfa/ns/property#",
        "http://www.aiim.org/pdfa/ns/type#",
        "http://www.aiim.org/pdfa/ns/field#",
        "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        "http://www.w3.org/XML/1998/namespace",
        "http://www.w3.org/2000/xmlns/",
        "adobe:ns:meta/",
    };

    private static readonly HashSet<string> ToolXmpProperties = new(StringComparer.Ordinal)
    {
        "CreatorTool", "Producer", "CreateDate", "ModifyDate", "MetadataDate", "PDFVersion",
        "Trapped", "format", "DocumentID", "InstanceID", "OriginalDocumentID", "VersionID",
    };

    private static void ScanXmp(PdfDocument doc, Collector c)
    {
        foreach (var stream in doc.EnumerateMetadataStreams())
        {
            c.Token.ThrowIfCancellationRequested();
            var obj = doc.GetReferenceTo(stream)?.ObjectNum ?? 0;
            var bytes = SafeDecoded(stream);
            if (bytes is null || bytes.Length == 0) continue;
            if (!XfaXmlCarrier.TryLoadXml(bytes, out var xmp, out _) || xmp.Root is null)
            {
                c.Presence("XMP", $"XMP packet ({bytes.Length} bytes) is not readable XML", 0, obj);
                continue;
            }
            ReportXmpProperties(xmp.Root, c, obj, "XMP");
        }
    }

    private static void ReportXmpProperties(XElement root, Collector c, int obj, string carrier)
    {
        var guard = 0;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (guard++ > WalkGuard || c.Full) return;

            // Attribute-form properties: <rdf:Description pdf:Keywords="...">.
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var ns = attribute.Name.NamespaceName;
                if (ns.Length == 0 || ToolXmpNamespaces.Contains(ns) || ToolXmpProperties.Contains(attribute.Name.LocalName))
                    continue;
                c.Text($"{carrier} {QualifiedName(root, attribute.Name)}", attribute.Value, 0, obj);
            }

            if (element.HasElements) continue;
            // rdf:li / rdf:Alt / rdf:Bag values take the name of the property
            // that holds the container.
            var property = element;
            while (property.Parent != null && property.Name.NamespaceName == RdfNamespace)
                property = property.Parent;
            if (ToolXmpNamespaces.Contains(property.Name.NamespaceName)
                || ToolXmpProperties.Contains(property.Name.LocalName)
                || property.Ancestors().Any(IsToolStructure))
                continue;
            c.Text($"{carrier} {QualifiedName(root, property.Name)}", element.Value, 0, obj);
        }
    }

    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>
    /// An enclosing element that makes everything under it machine-written
    /// (xmpMM:History, the PDF/A extension schemas) — but not the RDF and
    /// x:xmpmeta wrappers every property sits in.
    /// </summary>
    private static bool IsToolStructure(XElement ancestor)
    {
        var ns = ancestor.Name.NamespaceName;
        return ns != RdfNamespace && ns != "adobe:ns:meta/" && ToolXmpNamespaces.Contains(ns);
    }

    private static string QualifiedName(XElement scope, XName name)
    {
        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        if (prefix is null)
        {
            foreach (var d in scope.DescendantsAndSelf())
            {
                prefix = d.GetPrefixOfNamespace(name.Namespace);
                if (prefix != null) break;
            }
        }
        return prefix is null ? name.LocalName : $"{prefix}:{name.LocalName}";
    }

    // ── Outlines (§12.3.3) ────────────────────────────────────────────────

    private static void ScanOutlines(PdfDocument doc, Collector c)
    {
        var stack = new Stack<PdfOutlineItem>();
        foreach (var item in PdfOutlineParser.Parse(doc).Reverse()) stack.Push(item);
        var guard = 0;
        while (stack.Count > 0 && guard++ < WalkGuard)
        {
            var item = stack.Pop();
            c.Text("outline /Title", item.Title, item.PageNumber ?? 0);
            for (var i = item.Children.Count - 1; i >= 0; i--) stack.Push(item.Children[i]);
        }
    }

    // ── Hidden optional content (§8.11) ───────────────────────────────────

    /// <summary>
    /// Text inside an optional-content group that is OFF in the default
    /// configuration: not drawn, fully present. The walker already tags every
    /// letter (<see cref="Letter.IsInHiddenOptionalContent"/>).
    /// </summary>
    private static void ScanHiddenOptionalContent(PdfDocument doc, Collector c)
    {
        if (doc.Catalog?.GetOptional("OCProperties") == null) return;
        for (var i = 1; i <= doc.PageCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var hidden = doc.GetPage(i).GetLetters(c.Token).Where(l => l.IsInHiddenOptionalContent).ToList();
            if (hidden.Count > 0)
                c.Text("optional content (hidden by default)", LettersToText(hidden), i);
        }
    }

    // ── Page thumbnails (§12.3.4) ─────────────────────────────────────────

    private static void ScanThumbnails(PdfDocument doc, Collector c)
    {
        for (var i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            var thumbObj = Deref(doc, page.Dictionary.GetOptional("Thumb"), out var objNum);
            if (thumbObj is not PdfStream thumb) continue;
            var w = thumb.GetOptional("Width").TryGetNumber(out var wn) ? (int)wn : 0;
            var h = thumb.GetOptional("Height").TryGetNumber(out var hn) ? (int)hn : 0;
            c.Presence("page /Thumb", $"{w}x{h} thumbnail image — may show content as it was before redaction", i, objNum);
        }
    }

    // ── Page-piece dictionaries (§14.5) ───────────────────────────────────

    private static void ScanPieceInfo(PdfDocument doc, Collector c)
    {
        ReportPieceInfo(doc, c, doc.Catalog, 0, "catalog");
        for (var i = 1; i <= doc.PageCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var page = doc.GetPage(i);
            ReportPieceInfo(doc, c, page.Dictionary, i, "page");
            if (page.Resources is { } resources
                && doc.Resolve(resources.GetOptional("XObject") ?? PdfNull.Instance) is PdfDictionary xobjects)
            {
                foreach (var (name, value) in xobjects)
                    if (doc.Resolve(value) is PdfDictionary form)
                        ReportPieceInfo(doc, c, form, i, $"XObject /{name.Value}");
            }
        }
    }

    private static void ReportPieceInfo(PdfDocument doc, Collector c, PdfDictionary? owner, int page, string ownerLabel)
    {
        if (owner == null) return;
        if (Deref(doc, owner.GetOptional("PieceInfo"), out var objNum) is not PdfDictionary pieces) return;
        foreach (var (app, data) in pieces)
        {
            if (doc.Resolve(data) is not PdfDictionary appData) continue;
            var carrier = $"/PieceInfo /{app.Value} /Private";
            var privateObj = appData.GetOptional("Private");
            if (privateObj == null) continue;
            ReportLooseStrings(doc, c, privateObj, carrier, page, objNum, ownerLabel, followReferences: true);
        }
    }

    /// <summary>
    /// Every string (and text-like stream) inside an arbitrary object, which is
    /// what private application data and orphan objects are. Bounded.
    /// </summary>
    private static void ReportLooseStrings(
        PdfDocument doc, Collector c, PdfObject start, string carrier, int page, int objNum, string? location,
        bool followReferences)
    {
        var stack = new Stack<PdfObject>();
        stack.Push(start);
        var visitedRefs = new HashSet<int>();
        var guard = 0;
        while (stack.Count > 0 && guard++ < 10_000)
        {
            var node = stack.Pop();
            if (node is PdfReference r)
            {
                if (!followReferences || !visitedRefs.Add(r.ObjectNum)) continue;
                node = doc.Resolve(r);
            }
            switch (node)
            {
                case PdfString s:
                    c.Text(carrier, s.Value, page, objNum, location);
                    break;
                case PdfStream st:
                    ReportLooseStream(c, st, carrier, page, objNum, location);
                    foreach (var v in st.Values) stack.Push(v);
                    break;
                case PdfDictionary d:
                    foreach (var v in d.Values) stack.Push(v);
                    break;
                case PdfArray a:
                    foreach (var v in a) stack.Push(v);
                    break;
            }
        }
    }

    private static void ReportLooseStream(Collector c, PdfStream st, string carrier, int page, int objNum, string? location)
    {
        var bytes = SafeDecoded(st);
        if (bytes is null)
        {
            c.Presence(carrier, "stream present but not decodable", page, objNum, location);
            return;
        }
        if (bytes.Length == 0) return;
        if (LooksLikeText(bytes))
            c.Text(carrier + " (stream)", DecodeTextBytes(bytes.Length > MaxTextPayloadBytes ? bytes[..MaxTextPayloadBytes] : bytes), page, objNum, location);
        else
            c.Presence(carrier + " (stream)", $"{bytes.Length} bytes of binary data", page, objNum, location);
    }

    // ── Orphan objects ────────────────────────────────────────────────────

    /// <summary>
    /// Objects the cross-reference table lists as in use that nothing reachable
    /// from the trailer points at — the leftovers of an editor that dropped a
    /// reference and kept the object (a replaced content stream, a deleted
    /// annotation). No viewer shows them; every dump tool does.
    /// </summary>
    private static void ScanOrphans(PdfDocument doc, Collector c)
    {
        var inUse = doc.SnapshotInUseObjectNumbers();
        if (inUse.Length == 0) return;
        var reachable = doc.ComputeReachableObjects();
        var scanned = 0;
        PdfPage? contextPage = doc.PageCount > 0 ? doc.GetPage(1) : null;

        foreach (var n in inUse)
        {
            c.Token.ThrowIfCancellationRequested();
            if (reachable.Contains(n)) continue;
            if (scanned++ >= MaxOrphanObjects)
            {
                c.Presence("orphan object", $"more than {MaxOrphanObjects} unreferenced objects; the rest were not examined", 0);
                return;
            }

            PdfObject obj;
            try { obj = doc.GetObject(n); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }

            switch (obj)
            {
                case PdfNull:
                    continue;
                case PdfStream stream:
                    ReportOrphanStream(doc, c, stream, n, contextPage);
                    break;
                case PdfDictionary dict when IsStructuralOnly(dict):
                    continue;
                default:
                    ReportLooseStrings(doc, c, obj, "orphan object", 0, n, null, followReferences: false);
                    break;
            }
        }
    }

    /// <summary>Unreachable by design: cross-reference and object streams, linearization.</summary>
    private static bool IsStructuralOnly(PdfDictionary dict) =>
        dict.GetNameOrNull("Type") is "XRef" or "ObjStm" || dict.ContainsKey("Linearized");

    private static void ReportOrphanStream(PdfDocument doc, Collector c, PdfStream stream, int n, PdfPage? contextPage)
    {
        if (IsStructuralOnly(stream)) return;
        var type = stream.GetNameOrNull("Type");
        var subtype = stream.GetNameOrNull("Subtype");

        // Dictionary entries of the stream first (a /Metadata name, a title).
        foreach (var v in stream.Values)
            ReportLooseStrings(doc, c, v, "orphan object", 0, n, null, followReferences: false);

        if (subtype == "Image")
        {
            c.Presence("orphan object (image)", $"{SafeDecodedLength(stream)} byte image no page draws", 0, n);
            return;
        }

        var bytes = SafeDecoded(stream);
        if (bytes is null)
        {
            c.Presence("orphan object (stream)", "stream present but not decodable", 0, n);
            return;
        }
        if (bytes.Length == 0) return;

        if (type == "EmbeddedFile")
        {
            ReportPayload(c, bytes, subtype, $"object {n}", 0, n, "orphan object (embedded file)");
            return;
        }

        if (type == "Metadata" || subtype == "XML")
        {
            if (XfaXmlCarrier.TryLoadXml(bytes, out var xml, out _) && xml.Root is { } root)
                ReportXmpProperties(root, c, n, "orphan object (XML)");
            else
                c.Presence("orphan object (XML)", $"{bytes.Length} bytes, not readable XML", 0, n);
            return;
        }

        // A content stream (a page's old /Contents, a Form XObject): decode its
        // text through the one walker, with its own resources or page 1's.
        if (contextPage != null && (subtype == "Form" || LooksLikeContentStream(bytes)))
        {
            var text = StreamPaintedText(doc, contextPage, stream, contextPage.Resources, c.Token);
            if (!string.IsNullOrWhiteSpace(text))
            {
                c.Text("orphan object (content stream)", text, 0, n);
                return;
            }
        }

        if (LooksLikeText(bytes))
            c.Text("orphan object (stream)", DecodeTextBytes(bytes.Length > MaxTextPayloadBytes ? bytes[..MaxTextPayloadBytes] : bytes), 0, n);
        else
            c.Presence("orphan object (stream)", $"{bytes.Length} bytes{(type is null ? "" : $", /Type /{type}")} — not decoded", 0, n);
    }

    private static long SafeDecodedLength(PdfStream stream) => SafeDecoded(stream)?.LongLength ?? stream.EncodedData.LongLength;

    private static bool LooksLikeContentStream(byte[] bytes)
    {
        var span = bytes.AsSpan(0, Math.Min(bytes.Length, 64 * 1024));
        return span.IndexOf("BT"u8) >= 0 && (span.IndexOf("Tj"u8) >= 0 || span.IndexOf("TJ"u8) >= 0);
    }

    // ── Prior revisions (§7.5.6) ──────────────────────────────────────────

    /// <summary>
    /// An incremental update appends; it never erases. The file prefix that ends
    /// at the <c>%%EOF</c> after an earlier cross-reference section IS that
    /// earlier revision, a complete PDF. Open it, and report what it contains
    /// that the current revision no longer does — the classic "redacted by
    /// saving over" leak.
    /// </summary>
    private static void ScanPriorRevisions(PdfDocument doc, Collector c)
    {
        var bytes = doc.TryReadSourceBytes(MaxSourceBytes);
        if (bytes is null)
        {
            if (doc.PageCount > 0 && doc.Trailer.ContainsKey("Prev"))
                c.Presence("prior revisions", "the file has earlier revisions but its bytes were not available to the scan", 0);
            return;
        }

        var boundaries = PriorRevisionEnds(bytes, c);
        if (boundaries.Count == 0) return;
        if (doc.IsEncrypted)
        {
            c.Presence("prior revisions", $"{boundaries.Count} earlier revision(s) present; not examined because the file is encrypted", 0);
            return;
        }

        // What the current revision says, to compare against.
        var currentWords = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i <= doc.PageCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            foreach (var w in TextExtractor.BuildWords(doc.GetPage(i).GetLetters(c.Token)))
                currentWords.Add(w.Text);
        }
        var currentCarriers = new List<CarrierText>();
        ScanInto(doc, new Collector(currentCarriers, c.Token, prefix: "", depth: c.Depth), includeHistory: false);
        var currentCarrierSet = currentCarriers.Select(f => (f.Carrier, f.Text)).ToHashSet();

        var total = boundaries.Count + 1;
        for (var r = 0; r < boundaries.Count; r++)
        {
            c.Token.ThrowIfCancellationRequested();
            var (end, xrefOffset) = boundaries[r];
            var label = $"prior revision {r + 1} of {total}";
            PdfDocument old;
            try
            {
                old = PdfDocument.Open(bytes[..end]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                c.Presence(label, $"revision ending at byte {end} could not be opened ({ex.GetType().Name})", 0);
                continue;
            }

            using (old)
            {
                for (var p = 1; p <= old.PageCount; p++)
                {
                    c.Token.ThrowIfCancellationRequested();
                    var missing = MissingRuns(TextExtractor.BuildWords(old.GetPage(p).GetLetters(c.Token)), currentWords);
                    c.Text($"{label} page text", missing, p, 0, $"xref at byte {xrefOffset}");
                }

                var oldCarriers = new List<CarrierText>();
                ScanInto(old, new Collector(oldCarriers, c.Token, prefix: "", depth: c.Depth), includeHistory: false);
                foreach (var f in oldCarriers)
                {
                    if (f.Kind != CarrierFindingKind.Text || currentCarrierSet.Contains((f.Carrier, f.Text))) continue;
                    c.Text($"{label} > {f.Carrier}", f.Text, f.PageNumber, 0, f.Location);
                }
            }
        }
    }

    /// <summary>
    /// Words of an old page that the current document does not contain, as
    /// runs in their original order, joined by " … " between runs.
    /// </summary>
    private static string MissingRuns(IReadOnlyList<Word> words, HashSet<string> current)
    {
        var runs = new List<string>();
        var run = new List<string>();
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            if (current.Contains(w.Text))
            {
                if (run.Count > 0) { runs.Add(string.Join(' ', run)); run.Clear(); }
            }
            else
            {
                run.Add(w.Text);
            }
        }
        if (run.Count > 0) runs.Add(string.Join(' ', run));
        return string.Join(" … ", runs);
    }

    /// <summary>
    /// End offsets (exclusive) of each earlier revision, oldest first, found by
    /// following the <c>/Prev</c> chain from the current cross-reference
    /// section. A section whose revision would end at or past the current one
    /// (linearization's first-page section points FORWARD) is not history.
    /// </summary>
    private static List<(int End, long XrefOffset)> PriorRevisionEnds(byte[] bytes, Collector c)
    {
        var result = new List<(int End, long XrefOffset)>();
        using var ms = new MemoryStream(bytes, writable: false);
        var parser = new XRefParser(ms);
        long start;
        PdfDictionary trailer;
        try
        {
            start = parser.FindStartXRef();
            trailer = parser.ParseXRef(start).Trailer;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return result;
        }

        var currentEnd = EndOfRevision(bytes, start) ?? bytes.Length;
        var seen = new HashSet<long> { start };
        for (var hop = 0; hop < MaxRevisions * 2; hop++)
        {
            if (trailer.GetOptional("Prev") is not { } prevObj || !prevObj.TryGetNumber(out var prevNumber)) break;
            var prev = (long)prevNumber;
            if (prev <= 0 || prev >= bytes.Length || !seen.Add(prev)) break;
            try
            {
                trailer = parser.ParseXRef(prev).Trailer;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                c.Presence("prior revisions", $"earlier cross-reference section at byte {prev} is unreadable ({ex.GetType().Name})", 0);
                break;
            }

            if (EndOfRevision(bytes, prev) is { } end && end < currentEnd)
                result.Add((end, prev));
            if (result.Count >= MaxRevisions)
            {
                c.Presence("prior revisions", $"more than {MaxRevisions} earlier revisions; older ones were not examined", 0);
                break;
            }
        }

        // Deduplicate (a hybrid file can reach one revision twice) and order oldest first.
        return result.GroupBy(r => r.End).Select(g => g.First()).OrderBy(r => r.End).ToList();
    }

    /// <summary>The offset just past the first <c>%%EOF</c> at or after <paramref name="from"/>.</summary>
    private static int? EndOfRevision(byte[] bytes, long from)
    {
        if (from < 0 || from >= bytes.Length) return null;
        var idx = bytes.AsSpan((int)from).IndexOf("%%EOF"u8);
        if (idx < 0) return null;
        var end = (int)from + idx + 5;
        // Keep the line ending that belongs to the marker.
        while (end < bytes.Length && (bytes[end] == (byte)'\r' || bytes[end] == (byte)'\n')) end++;
        return end;
    }
}
