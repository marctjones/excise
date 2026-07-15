using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Operations;

/// <summary>
/// Combines pages from multiple source documents into one new document,
/// preserving internal links, outline (bookmark) structure, and AcroForm
/// fields across the merge (#628).
/// </summary>
/// <remarks>
/// Reuses <see cref="PdfObjectCloner"/> — the same walker that already
/// safely deep-copies a page's own content — for outline and AcroForm
/// subtrees too, since both are just PDF dictionary/array graphs with
/// internal references. The only merge-specific mechanism is
/// <see cref="PdfObjectCloner.PageMap"/>: pages are reserved (given a
/// stable target <see cref="PdfReference"/>) before any content is cloned,
/// so a link or outline destination pointing at a page later in the merge
/// order still resolves correctly.
/// </remarks>
public static class PdfDocumentMerger
{
    /// <summary>
    /// Merge pages from <paramref name="sources"/> into a new document, in
    /// the given order. <c>PageIndices</c> are 0-based indices into each
    /// source document.
    /// </summary>
    public static PdfDocument Merge(IReadOnlyList<(PdfDocument Document, IReadOnlyList<int> PageIndices)> sources)
    {
        if (sources == null || sources.Count == 0)
            throw new ArgumentException("At least one source document is required.", nameof(sources));

        var target = PdfDocument.CreateNew();
        var (cloner, clonedRefsBySource) = ClonePagesInto(target, sources);

        // Phase 3: splice each source's outline (bookmark) subtree onto
        // the target's, in source order. A source with no outline of its
        // own contributes nothing.
        foreach (var (doc, _) in sources)
        {
            MergeOutline(target, doc, cloner, ClonedRefsFor(clonedRefsBySource, doc));
        }

        // Phase 4: merge AcroForm fields, renaming top-level name
        // collisions so fields from different sources never merge into
        // one under a shared name.
        var usedTopLevelFieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (doc, _) in sources)
        {
            MergeAcroForm(target, doc, cloner, ClonedRefsFor(clonedRefsBySource, doc), usedTopLevelFieldNames);
        }

        return target;
    }

    /// <summary>
    /// Phases 1+2 only: reserve a stable target reference for every page
    /// across every source, then clone each page's real content into
    /// <paramref name="target"/>, with intra-batch links/destinations
    /// resolving correctly via the shared <see cref="PdfObjectCloner.PageMap"/>.
    /// Deliberately does NOT touch outlines or AcroForm fields — shared by
    /// <see cref="Merge"/> (which adds those in phases 3-4) and
    /// <see cref="PdfDocumentSplitter"/> (which, per #628's split scope,
    /// intentionally does not splice either).
    /// </summary>
    internal static (PdfObjectCloner Cloner, Dictionary<PdfDocument, Dictionary<(int, int), PdfReference>> ClonedRefsBySource) ClonePagesInto(
        PdfDocument target,
        IReadOnlyList<(PdfDocument Document, IReadOnlyList<int> PageIndices)> sources)
    {
        var cloner = new PdfObjectCloner(target);
        var pageMap = new Dictionary<PdfDictionary, PdfReference>();
        cloner.PageMap = pageMap;

        // clonedRefs is memoized per SOURCE document (not per call) and
        // shared across that source's page cloning, outline cloning, and
        // AcroForm field cloning — an object reachable from more than one
        // of those (e.g. a widget annotation reached both as a page
        // annotation and via /AcroForm/Fields) is only cloned once.
        var clonedRefsBySource = new Dictionary<PdfDocument, Dictionary<(int, int), PdfReference>>();

        // Phase 1 (reserve): allocate a stable target PdfReference for
        // every page across every source up front, before any content is
        // resolved or cloned. This is what lets a link/outline destination
        // cloned later in this pass — from any source, pointing at any
        // page in the batch — resolve immediately via PageMap instead of
        // being dropped.
        var plan = new List<(PdfDocument Source, List<(PdfPage Page, PdfReference Reserved)> Pages)>();
        foreach (var (doc, indices) in sources)
        {
            var pages = new List<(PdfPage, PdfReference)>();
            foreach (var pageIndex in indices)
            {
                var page = doc.GetPage(pageIndex + 1);
                var reserved = target.AddIndirectObject(PdfNull.Instance);
                pageMap[page.Dictionary] = reserved;
                pages.Add((page, reserved));
            }
            plan.Add((doc, pages));
        }

        // Phase 2 (fill): clone each page's real content — now able to
        // resolve forward references to any page in the batch via the
        // completed PageMap — and overwrite its reservation.
        foreach (var (doc, pages) in plan)
        {
            var clonedRefs = ClonedRefsFor(clonedRefsBySource, doc);
            foreach (var (page, reserved) in pages)
            {
                var clonedDict = cloner.ClonePageDictionary(page, clonedRefs);
                clonedDict["Parent"] = target.Catalog.GetReference("Pages");
                target.ReplaceIndirectObject(reserved.ObjectNum, clonedDict);
                target.Pages.AppendPreRegisteredPage(reserved);
            }
        }

        return (cloner, clonedRefsBySource);
    }

    private static Dictionary<(int, int), PdfReference> ClonedRefsFor(
        Dictionary<PdfDocument, Dictionary<(int, int), PdfReference>> clonedRefsBySource,
        PdfDocument doc)
    {
        if (!clonedRefsBySource.TryGetValue(doc, out var refs))
        {
            refs = new Dictionary<(int, int), PdfReference>();
            clonedRefsBySource[doc] = refs;
        }
        return refs;
    }

    private static void MergeOutline(
        PdfDocument target,
        PdfDocument source,
        PdfObjectCloner cloner,
        Dictionary<(int, int), PdfReference> clonedRefs)
    {
        var sourceOutlinesObj = source.Catalog.GetOptional("Outlines");
        if (sourceOutlinesObj == null) return;
        if (source.Resolve(sourceOutlinesObj) is not PdfDictionary sourceRoot) return;

        var firstObj = sourceRoot.GetOptional("First");
        if (firstObj == null) return; // outline dict present but empty
        var lastObj = sourceRoot.GetOptional("Last") ?? firstObj;

        var (targetRoot, targetRootRef) = GetOrCreateOutlineRoot(target);

        // Pre-seed the memo so every top-level source child's /Parent
        // (which points at the SOURCE's outline root — never itself
        // cloned, since we only walk from /First) resolves directly to
        // the TARGET's outline root instead of being spuriously cloned as
        // an unrelated dictionary.
        if (sourceOutlinesObj is PdfReference sourceRootRef)
            clonedRefs[(sourceRootRef.ObjectNum, sourceRootRef.Generation)] = targetRootRef;

        var clonedFirst = cloner.CloneObject(source, firstObj, clonedRefs);
        var clonedLast = cloner.CloneObject(source, lastObj, clonedRefs);
        if (clonedFirst is not PdfReference clonedFirstRef) return;
        var clonedLastRef = clonedLast as PdfReference ?? clonedFirstRef;

        // Advisory only (viewer's initial open/closed hint) — top-level
        // added-sibling count, not a full recursive descendant count.
        int addedCount = CountSiblingChain(target, clonedFirstRef);

        var existingLastObj = targetRoot.GetOptional("Last");
        if (existingLastObj is PdfReference existingLastRef &&
            target.Resolve(existingLastRef) is PdfDictionary existingLastDict)
        {
            existingLastDict["Next"] = clonedFirstRef;
            if (target.Resolve(clonedFirstRef) is PdfDictionary clonedFirstDict)
                clonedFirstDict["Prev"] = existingLastRef;

            targetRoot["Last"] = clonedLastRef;
            int existingCount = targetRoot.GetOptional("Count") is PdfInteger ci ? (int)ci.Value : 0;
            targetRoot.SetInt("Count", existingCount + addedCount);
        }
        else
        {
            targetRoot["First"] = clonedFirstRef;
            targetRoot["Last"] = clonedLastRef;
            targetRoot.SetInt("Count", addedCount);
        }
    }

    private static (PdfDictionary Root, PdfReference RootRef) GetOrCreateOutlineRoot(PdfDocument target)
    {
        var existingObj = target.Catalog.GetOptional("Outlines");
        if (existingObj is PdfReference existingRef && target.Resolve(existingObj) is PdfDictionary existingDict)
            return (existingDict, existingRef);

        var root = new PdfDictionary();
        root.SetName("Type", "Outlines");
        var rootRef = target.AddIndirectObject(root);
        target.Catalog["Outlines"] = rootRef;
        return (root, rootRef);
    }

    private static int CountSiblingChain(PdfDocument doc, PdfReference first)
    {
        int count = 0;
        var visited = new HashSet<(int, int)>();
        PdfObject current = first;
        while (current is PdfReference r && visited.Add((r.ObjectNum, r.Generation)))
        {
            if (doc.Resolve(r) is not PdfDictionary d) break;
            count++;
            current = d.GetOptional("Next") ?? (PdfObject)PdfNull.Instance;
        }
        return count;
    }

    private static void MergeAcroForm(
        PdfDocument target,
        PdfDocument source,
        PdfObjectCloner cloner,
        Dictionary<(int, int), PdfReference> clonedRefs,
        HashSet<string> usedTopLevelFieldNames)
    {
        var sourceAcroFormObj = source.Catalog.GetOptional("AcroForm");
        if (sourceAcroFormObj == null) return;
        if (source.Resolve(sourceAcroFormObj) is not PdfDictionary sourceAcroForm) return;

        var fieldsObj = sourceAcroForm.GetOptional("Fields");
        if (fieldsObj == null || source.Resolve(fieldsObj) is not PdfArray sourceFields || sourceFields.Count == 0)
            return;

        var (_, targetFields) = GetOrCreateAcroForm(target);

        foreach (var fieldRefObj in sourceFields)
        {
            if (fieldRefObj is not PdfReference fieldRef) continue;
            if (source.Resolve(fieldRef) is not PdfDictionary) continue;

            var clonedObj = cloner.CloneReference(source, fieldRef, clonedRefs);
            if (clonedObj is not PdfReference clonedFieldRef) continue;
            if (target.Resolve(clonedFieldRef) is not PdfDictionary clonedFieldDict) continue;

            // Renaming just the TOP-LEVEL /T segment is sufficient to
            // disambiguate the whole cloned subtree: every descendant's
            // fully-qualified name is built by dot-joining ancestor /T
            // segments (PdfAcroFormParser.ParseFieldTree), so a unique
            // top-level segment makes every name under it unique too,
            // without touching a single nested field.
            var name = clonedFieldDict.GetStringOrNull("T") ?? $"Field{targetFields.Count + 1}";
            var uniqueName = MakeUnique(name, usedTopLevelFieldNames);
            usedTopLevelFieldNames.Add(uniqueName);
            if (uniqueName != clonedFieldDict.GetStringOrNull("T"))
                clonedFieldDict.SetString("T", uniqueName);

            targetFields.Add(clonedFieldRef);
        }
    }

    private static (PdfDictionary AcroForm, PdfArray Fields) GetOrCreateAcroForm(PdfDocument target)
    {
        var existingObj = target.Catalog.GetOptional("AcroForm");
        PdfDictionary acroForm;
        if (existingObj != null && target.Resolve(existingObj) is PdfDictionary existingDict)
        {
            acroForm = existingDict;
        }
        else
        {
            acroForm = new PdfDictionary();
            var acroFormRef = target.AddIndirectObject(acroForm);
            target.Catalog["AcroForm"] = acroFormRef;
        }

        var fieldsObj = acroForm.GetOptional("Fields");
        PdfArray fields;
        if (fieldsObj != null && target.Resolve(fieldsObj) is PdfArray existingFields)
        {
            fields = existingFields;
        }
        else
        {
            fields = new PdfArray();
            acroForm["Fields"] = fields;
        }
        return (acroForm, fields);
    }

    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (!used.Contains(name)) return name;
        int suffix = 2;
        string candidate;
        do
        {
            candidate = $"{name} ({suffix})";
            suffix++;
        } while (used.Contains(candidate));
        return candidate;
    }
}
