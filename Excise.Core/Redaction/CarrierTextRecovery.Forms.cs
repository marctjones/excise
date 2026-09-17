using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

public static partial class CarrierTextRecovery
{
    // ── AcroForm (§12.7) ──────────────────────────────────────────────────

    // Field value, default value, rich-text value, and the user-facing
    // alternate name (the tooltip / screen-reader label). /T is the partial
    // field name and deliberately not reported: it is structure, not content.
    private static readonly string[] FieldValueKeys = { "V", "DV", "RV" };

    // Widget appearance characteristics (§12.5.6.19): normal, rollover and
    // down captions.
    private static readonly string[] MkCaptionKeys = { "CA", "RC", "AC" };

    // Checkbox/radio state names that carry no information.
    private static readonly HashSet<string> TrivialStateNames = new(StringComparer.Ordinal) { "Off", "Yes", "On" };

    private static void ScanAcroForm(PdfDocument doc, Collector c, InteractiveIndex index)
    {
        if (doc.Resolve(doc.Catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) is not PdfDictionary acro)
            return;
        if (doc.Resolve(acro.GetOptional("Fields") ?? PdfNull.Instance) is not PdfArray fields)
            return;

        var pageOf = PageIndexByDictionary(doc);
        var dr = doc.Resolve(acro.GetOptional("DR") ?? PdfNull.Instance) as PdfDictionary;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Node, string ParentName)>();
        for (var i = fields.Count - 1; i >= 0; i--) stack.Push((fields[i], ""));

        var guard = 0;
        while (stack.Count > 0 && guard++ < WalkGuard)
        {
            c.Token.ThrowIfCancellationRequested();
            var (raw, parentName) = stack.Pop();
            if (Deref(doc, raw, out var objNum) is not PdfDictionary node || !visited.Add(node)) continue;

            var partial = ReadText(doc, node, "T");
            var name = string.IsNullOrEmpty(partial) ? parentName
                : parentName.Length == 0 ? partial! : parentName + "." + partial;
            var page = PageOf(doc, node, pageOf);
            var location = name.Length == 0 ? null : $"field '{name}'";
            index.Fields.Add((node, objNum, page, name));

            foreach (var key in FieldValueKeys)
                ReportFieldValue(doc, c, node.GetOptional(key), $"acroform /{key}", page, objNum, location);
            c.Text("acroform /TU", ReadText(doc, node, "TU"), page, objNum, location);
            ReportOptions(doc, c, node, page, objNum, location);

            if (doc.Resolve(node.GetOptional("MK") ?? PdfNull.Instance) is PdfDictionary mk)
                foreach (var key in MkCaptionKeys)
                    c.Text($"widget /MK /{key}", ReadText(doc, mk, key), page, objNum, location);

            // A widget that no page lists in /Annots still carries an appearance.
            if (node.GetNameOrNull("Subtype") == "Widget" && index.AnnotationsWithAppearanceScanned.Add(node)
                && doc.PageCount > 0)
            {
                var host = doc.GetPage(page > 0 ? page : 1);
                ScanAppearance(doc, host, node, dr, c, page, objNum, "widget", "unlisted widget");
            }

            if (doc.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                for (var i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], name));
        }
    }

    private static void ReportFieldValue(
        PdfDocument doc, Collector c, PdfObject? value, string carrier, int page, int objNum, string? location)
    {
        switch (Deref(doc, value, out _))
        {
            case PdfString s:
                c.Text(carrier, s.Value, page, objNum, location);
                break;
            case PdfStream st:
                c.Text(carrier, DecodeTextBytes(SafeDecoded(st)), page, objNum, location);
                break;
            case PdfName n when !TrivialStateNames.Contains(n.Value):
                c.Text(carrier + " (state name)", n.Value, page, objNum, location);
                break;
            case PdfArray arr:
                foreach (var item in arr)
                    ReportFieldValue(doc, c, item, carrier, page, objNum, location);
                break;
        }
    }

    /// <summary>
    /// Choice-field options (§12.7.5.4): each entry is a display string, or an
    /// <c>[export display]</c> pair — both halves are text, and the export value
    /// never appears on the page.
    /// </summary>
    private static void ReportOptions(PdfDocument doc, Collector c, PdfDictionary node, int page, int objNum, string? location)
    {
        if (Deref(doc, node.GetOptional("Opt"), out _) is not PdfArray opts) return;
        foreach (var entry in opts)
        {
            switch (Deref(doc, entry, out _))
            {
                case PdfString s:
                    c.Text("acroform /Opt", s.Value, page, objNum, location);
                    break;
                case PdfArray pair:
                    if (pair.Count > 0)
                        c.Text("acroform /Opt export value", ObjectText(doc, pair[0]), page, objNum, location);
                    if (pair.Count > 1)
                        c.Text("acroform /Opt display value", ObjectText(doc, pair[1]), page, objNum, location);
                    break;
            }
        }
    }

    private static Dictionary<PdfDictionary, int> PageIndexByDictionary(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 1; i <= doc.PageCount; i++)
            map.TryAdd(doc.GetPage(i).Dictionary, i);
        return map;
    }

    /// <summary>The page a widget lives on via <c>/P</c>, or 0 when unknown.</summary>
    private static int PageOf(PdfDocument doc, PdfDictionary node, Dictionary<PdfDictionary, int> pageOf) =>
        doc.Resolve(node.GetOptional("P") ?? PdfNull.Instance) is PdfDictionary p && pageOf.TryGetValue(p, out var n)
            ? n
            : 0;

    // ── Actions (§12.6): JavaScript, URI and file targets ─────────────────

    private static void ScanActions(PdfDocument doc, Collector c, InteractiveIndex index)
    {
        var walker = new ActionWalker(doc, c);
        var catalog = doc.Catalog;

        // Document-level JavaScript name tree (§12.6.4.17 / §7.7.4).
        if (doc.Resolve(catalog?.GetOptional("Names") ?? PdfNull.Instance) is PdfDictionary names)
            WalkNameTree(doc, names.GetOptional("JavaScript"), c,
                (key, value) => walker.Walk(value, "document JavaScript", 0, key, 0));

        walker.Walk(catalog?.GetOptional("OpenAction"), "/OpenAction", 0, null, 0);
        walker.WalkAdditional(catalog?.GetOptional("AA"), "document /AA", 0, null, 0);

        for (var i = 1; i <= doc.PageCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var page = doc.GetPage(i);
            walker.WalkAdditional(page.Dictionary.GetOptional("AA"), "page /AA", i, null, 0);
            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;
            foreach (var annotObj in annots)
            {
                if (Deref(doc, annotObj, out var annotNum) is not PdfDictionary annot) continue;
                var owner = annot.GetNameOrNull("Subtype") == "Widget" ? "widget" : "annotation";
                walker.Walk(annot.GetOptional("A"), $"{owner} /A", i, null, annotNum);
                walker.WalkAdditional(annot.GetOptional("AA"), $"{owner} /AA", i, null, annotNum);
            }
        }

        // Non-terminal fields carry actions no page annotation reaches.
        foreach (var (dict, fieldNum, page, name) in index.Fields)
        {
            var location = name.Length == 0 ? null : $"field '{name}'";
            walker.Walk(dict.GetOptional("A"), "field /A", page, location, fieldNum);
            walker.WalkAdditional(dict.GetOptional("AA"), "field /AA", page, location, fieldNum);
        }

        // Outline items: /A can be a URI or JavaScript action.
        if (doc.Resolve(catalog?.GetOptional("Outlines") ?? PdfNull.Instance) is PdfDictionary outlines)
        {
            var stack = new Stack<PdfObject>();
            if (outlines.GetOptional("First") is { } first) stack.Push(first);
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var guard = 0;
            while (stack.Count > 0 && guard++ < WalkGuard)
            {
                if (Deref(doc, stack.Pop(), out var itemNum) is not PdfDictionary item || !visited.Add(item)) continue;
                walker.Walk(item.GetOptional("A"), "outline /A", 0, null, itemNum);
                if (item.GetOptional("Next") is { } next) stack.Push(next);
                if (item.GetOptional("First") is { } child) stack.Push(child);
            }
        }
    }

    /// <summary>
    /// Reads one action and its <c>/Next</c> chain. Shared visited set: an
    /// action object referenced from two places is reported once.
    /// </summary>
    private sealed class ActionWalker
    {
        private readonly PdfDocument _doc;
        private readonly Collector _c;
        private readonly HashSet<PdfDictionary> _visited = new(ReferenceEqualityComparer.Instance);

        public ActionWalker(PdfDocument doc, Collector c)
        {
            _doc = doc;
            _c = c;
        }

        public void WalkAdditional(PdfObject? aa, string source, int page, string? location, int ownerObj)
        {
            if (Deref(_doc, aa, out var aaNum) is not PdfDictionary triggers) return;
            foreach (var (trigger, action) in triggers)
                Walk(action, $"{source} /{trigger.Value}", page, location, aaNum != 0 ? aaNum : ownerObj);
        }

        /// <param name="ownerObj">Object number to report for a DIRECT action — the dictionary it is written in.</param>
        public void Walk(PdfObject? start, string source, int page, string? location, int ownerObj)
        {
            if (start == null) return;
            var stack = new Stack<PdfObject>();
            stack.Push(start);
            var guard = 0;
            while (stack.Count > 0 && guard++ < WalkGuard)
            {
                var node = Deref(_doc, stack.Pop(), out var objNum);
                if (objNum == 0) objNum = ownerObj;
                if (node is PdfArray arr)
                {
                    // /Next may be an array of actions; a destination array
                    // (an /OpenAction [page /Fit]) holds no action dictionaries.
                    foreach (var e in arr) stack.Push(e);
                    continue;
                }
                if (node is not PdfDictionary action || !_visited.Add(action)) continue;
                if (action.GetNameOrNull("Type") is { } type && type != "Action") continue;

                var kind = action.GetNameOrNull("S");
                if (kind == "JavaScript" || action.ContainsKey("JS"))
                    _c.Text($"JavaScript ({source})", ReadText(_doc, action, "JS"), page, objNum, location);
                if (action.ContainsKey("URI"))
                    _c.Text($"action /URI ({source})", ReadText(_doc, action, "URI"), page, objNum, location);
                if (kind is "Launch" or "GoToR" or "GoToE" or "SubmitForm" or "ImportData" or "Thread")
                    _c.Text($"action /{kind} file target ({source})", FileSpecText(_doc, action.GetOptional("F")), page, objNum, location);

                if (action.GetOptional("Next") is { } next) stack.Push(next);
            }
        }
    }

    /// <summary>A file specification's name: a string, or a dictionary's /UF or /F.</summary>
    private static string? FileSpecText(PdfDocument doc, PdfObject? spec) =>
        Deref(doc, spec, out _) switch
        {
            PdfString s => s.Value,
            PdfDictionary d => ReadText(doc, d, "UF") ?? ReadText(doc, d, "F"),
            _ => null,
        };

    /// <summary>
    /// Walk a name tree (§7.9.6), calling <paramref name="visit"/> for each
    /// (key, unresolved value) pair.
    /// </summary>
    private static void WalkNameTree(PdfDocument doc, PdfObject? root, Collector c, Action<string, PdfObject> visit)
    {
        if (root == null) return;
        var stack = new Stack<PdfObject>();
        stack.Push(root);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var guard = 0;
        while (stack.Count > 0 && guard++ < WalkGuard)
        {
            c.Token.ThrowIfCancellationRequested();
            if (doc.Resolve(stack.Pop()) is not PdfDictionary node || !visited.Add(node)) continue;
            if (doc.Resolve(node.GetOptional("Names") ?? PdfNull.Instance) is PdfArray pairs)
            {
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                    visit(ObjectText(doc, pairs[i]) ?? "", pairs[i + 1]);
            }
            if (doc.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                foreach (var k in kids) stack.Push(k);
        }
    }
}
