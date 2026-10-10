using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;

namespace Excise.Core.Document;

/// <summary>
/// Cuts every reference to a page that has left the page tree, to the
/// annotations that sat only on it, and to annotations deleted from a page, so
/// the save does not ship them (#2012).
/// </summary>
/// <remarks>
/// <para>The writer saves only objects reachable from the trailer, so a page
/// dropped from <c>/Kids</c> is dropped from the file — unless something else
/// still points at it. A bookmark, a named destination, a link on a kept page,
/// the open action, a structure element's <c>/Pg</c>: each keeps the removed
/// page dictionary reachable, and the page dictionary reaches its content
/// stream, its fonts, its images and its annotations. A form field whose widget
/// sat on the page keeps that widget, and the field's value, reachable through
/// <c>/AcroForm/Fields</c>. Measured before this existed: every one of those
/// shipped the removed page's text or the field's value, on every save path.</para>
/// <para>The walk is generic (every value reachable from the trailer), not a
/// list of known destination locations, so a carrier nobody listed is cut too.
/// A reference to a removed page becomes <c>null</c>, which is what an
/// undefined reference already means (ISO 32000-2 §7.3.10): a bookmark to it
/// goes nowhere. A reference to one of its annotations is removed from the
/// array that held it (a field's <c>/Kids</c>, <c>/Fields</c>, <c>/CO</c>) or
/// nulled in a dictionary (a structure element's <c>/OBJR</c>), and a field
/// left with no widgets by that cut goes from the field tree too: a field is
/// the widgets it is drawn by, and one with none would carry its value
/// nowhere a reader shows. An annotation a kept page also lists stays.</para>
/// <para>The walk does not descend into what it cuts, so an object the removed
/// page shares with a kept page is untouched and stays in the file through the
/// kept page.</para>
/// <para>The structure tree is cut first, while every <c>/Pg</c> still names its
/// page (#2014); see <see cref="ScrubStructureTree"/>.</para>
/// <para>Each indirect object an edit lands in is re-registered with
/// <see cref="PdfDocument.ReplaceIndirectObject"/>: an edit inside an array
/// nested in a parsed dictionary does not clear that dictionary's pristine
/// flag, and the object store may evict a pristine object and re-parse the
/// original from the file, which would silently restore the reference.</para>
/// </remarks>
internal static class RemovedPageReferenceScrubber
{
    /// <summary>
    /// Cut references to <paramref name="removedPages"/> (object numbers no
    /// longer in the page tree), and to their annotations and the
    /// <paramref name="deletedAnnotations"/> that no page in
    /// <paramref name="livePages"/> lists. Returns the number of references cut.
    /// </summary>
    internal static int Scrub(
        PdfDocument document,
        IReadOnlySet<int> removedPages,
        IReadOnlySet<int> deletedAnnotations,
        IEnumerable<PdfDictionary> livePages)
    {
        if (removedPages.Count == 0 && deletedAnnotations.Count == 0)
            return 0;

        // Objects that leave every list holding them: the removed pages'
        // annotations and the deleted ones, then each field the cut leaves
        // without a widget.
        var live = livePages as IReadOnlyCollection<PdfDictionary> ?? livePages.ToList();
        var removedObjects = RemovedAnnotations(document, removedPages, deletedAnnotations, live);
        var edited = new HashSet<int>();
        var shrunk = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        var cut = ScrubStructureTree(document, removedPages, removedObjects, live, edited);

        // A pruned field can itself be listed elsewhere (/CO, a parent's /Kids),
        // and pruning it can empty its parent: walk again until nothing new.
        while (true)
        {
            cut += CutReferences(document, removedPages, removedObjects, shrunk, edited);
            var pruned = FieldsLeftWithoutWidgets(document, shrunk, edited);
            pruned.ExceptWith(removedObjects);
            if (pruned.Count == 0)
                break;
            removedObjects.UnionWith(pruned);
        }

        foreach (var number in edited)
            document.ReplaceIndirectObject(number, document.GetObject(number));

        return cut;
    }

    private static int CutReferences(
        PdfDocument document,
        IReadOnlySet<int> removedPages,
        HashSet<int> removedObjects,
        HashSet<PdfArray> shrunk,
        HashSet<int> edited)
    {
        var visited = new HashSet<int>();
        // (container, the indirect object it lives in, or null for the trailer)
        var stack = new Stack<(PdfObject Container, int? Owner)>();
        stack.Push((document.Trailer, null));
        var cut = 0;

        void Visit(PdfObject value, int? owner)
        {
            if (value is PdfReference reference)
            {
                if (!visited.Add(reference.ObjectNum))
                    return;
                if (TryGet(document, reference.ObjectNum) is { } target and (PdfDictionary or PdfArray))
                    stack.Push((target, reference.ObjectNum));
            }
            else if (value is PdfDictionary or PdfArray)
            {
                stack.Push((value, owner));
            }
        }

        bool IsCut(PdfObject value, out bool isPage)
        {
            isPage = value is PdfReference p && removedPages.Contains(p.ObjectNum);
            return isPage || (value is PdfReference o && removedObjects.Contains(o.ObjectNum));
        }

        while (stack.Count > 0)
        {
            var (container, owner) = stack.Pop();
            var changed = false;
            switch (container)
            {
                case PdfDictionary dictionary:
                    foreach (var key in dictionary.Keys)
                    {
                        var value = dictionary[key];
                        if (IsCut(value, out _))
                        {
                            dictionary[key] = PdfNull.Instance; // removes the entry (§7.3.7)
                            changed = true;
                            cut++;
                        }
                        else
                        {
                            Visit(value, owner);
                        }
                    }
                    break;
                case PdfArray array:
                    for (var i = array.Count - 1; i >= 0; i--)
                    {
                        if (!IsCut(array[i], out var isPage))
                        {
                            Visit(array[i], owner);
                            continue;
                        }
                        // A page keeps its slot (a destination is positional);
                        // an annotation or field leaves the list that held it.
                        if (isPage)
                        {
                            array[i] = PdfNull.Instance;
                        }
                        else
                        {
                            array.RemoveAt(i);
                            shrunk.Add(array);
                        }
                        changed = true;
                        cut++;
                    }
                    break;
            }

            if (changed && owner is { } number)
                edited.Add(number);
        }

        return cut;
    }

    /// <summary>
    /// Cut the removed pages out of the logical structure tree (#2014), before
    /// the generic walk nulls the <c>/Pg</c> entries this reads. Returns the
    /// number of elements, content items, text fields and tree entries cut;
    /// each indirect element dropped joins <paramref name="removedObjects"/> so
    /// the generic walk cuts any other reference to it (a <c>/Ref</c>, ...).
    /// </summary>
    /// <remarks>
    /// <para><b>Where content is.</b> A content item of an element is an integer
    /// MCID or an <c>/MCR</c> on a page, or an <c>/OBJR</c> naming an object
    /// (ISO 32000-2 §14.7.2 Table 355, §14.7.5). Its page is the item's own
    /// <c>/Pg</c> (an MCR's or OBJR's), else the element's. Table 355 requires
    /// the element's <c>/Pg</c> whenever <c>/K</c> holds an integer and defines
    /// no inheritance from an ancestor; readers take the nearest ancestor's,
    /// and so does this. An item no <c>/Pg</c> places is placed by the
    /// <c>/ParentTree</c> (§14.7.5.4): on a removed page when only a removed
    /// page's entry names its element. An OBJR is removed content when its
    /// <c>/Obj</c> is a removed annotation or page, or its page is removed.</para>
    /// <para><b>Dropped.</b> An element whose subtree has no content on a kept
    /// page, and which is tied to a removed page (removed content, a dropped
    /// child, or a removed <c>/Pg</c> of its own or inherited), goes with
    /// everything under it. An element with no content and no page is not
    /// touched: nothing ties it to the removed page.</para>
    /// <para><b>Kept but spanning.</b> An element with content on a kept page
    /// stays; its removed-page items and dropped children leave its <c>/K</c>.
    /// Left in place, a removed page's integer MCID would bind to MCID n of
    /// whatever page the element is read against once its <c>/Pg</c> is cut.
    /// Its <c>/ActualText</c>, <c>/Alt</c>, <c>/E</c> and <c>/T</c> go: the
    /// first three describe the element and ALL its children (§14.9.3,
    /// §14.9.4, Table 355), so they spell out the removed page's part and no
    /// longer describe what is left; <c>/T</c> is a carrier for the reason
    /// redaction scrubs it (#1583). Which part of such a string came from the
    /// removed page is unknowable, so none of it stays.</para>
    /// <para><b>Indexes.</b> A page's <c>/ParentTree</c> value is an array
    /// indexed by MCID (§14.7.5.4): a dropped element's slot becomes
    /// <c>null</c>, never removed, or every later MCID would map to the wrong
    /// element. The removed pages' and removed annotations' entries go whole,
    /// as do <c>/IDTree</c> entries naming a dropped element. The generic walk
    /// would remove an array element instead and break the key/value pairing
    /// of <c>/Nums</c> and <c>/Names</c>, which is why these are cut here.</para>
    /// <para>No removal report reaches a caller from this path: the page
    /// collection's pre-save action has no report channel.</para>
    /// </remarks>
    private static int ScrubStructureTree(
        PdfDocument document,
        IReadOnlySet<int> removedPages,
        HashSet<int> removedObjects,
        IReadOnlyCollection<PdfDictionary> livePages,
        HashSet<int> edited)
    {
        if (removedPages.Count == 0 && removedObjects.Count == 0)
            return 0;
        var catalogNumber = document.Trailer.GetReferenceOrNull("Root")?.ObjectNum;
        if (document.Catalog.GetOptional("StructTreeRoot") is not { } rootRaw
            || Resolve(document, rootRaw) is not PdfDictionary root)
        {
            return 0;
        }
        var rootOwner = rootRaw is PdfReference rr ? rr.ObjectNum : catalogNumber;

        // /ParentTree keys of the removed pages and annotations, and of the kept pages.
        var removedKeys = new HashSet<long>();
        var keptKeys = new HashSet<long>();
        foreach (var number in removedPages)
            AddKey(TryGet(document, number) as PdfDictionary, "StructParents", removedKeys);
        foreach (var number in removedObjects)
            AddKey(TryGet(document, number) as PdfDictionary, "StructParent", removedKeys);
        foreach (var page in livePages)
            AddKey(page, "StructParents", keptKeys);
        removedKeys.ExceptWith(keptKeys);

        // The elements each side of the parent tree names: places an unplaced item.
        var onRemovedPage = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var onKeptPage = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var parentTreeRaw = root.GetOptional("ParentTree");
        if (parentTreeRaw != null)
        {
            WalkTree(parentTreeRaw, "Nums", pairs =>
            {
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                {
                    if (Resolve(document, pairs[i]) is not PdfInteger key)
                        continue;
                    var into = removedKeys.Contains(key.Value) ? onRemovedPage : onKeptPage;
                    switch (Resolve(document, pairs[i + 1]))
                    {
                        case PdfDictionary element:
                            into.Add(element);
                            break;
                        case PdfArray slots:
                            foreach (var slot in slots)
                            {
                                if (Resolve(document, slot) is PdfDictionary e)
                                    into.Add(e);
                            }
                            break;
                    }
                }
                return null;
            });
        }
        onRemovedPage.ExceptWith(onKeptPage);

        // Every element under the root, with the page it inherits.
        var nodes = new Dictionary<PdfDictionary, StructNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<StructNode>();
        StructNode Discover(PdfDictionary element, PdfObject raw, int? parentOwner, int? inheritedPage)
        {
            if (nodes.TryGetValue(element, out var known))
                return known;
            var number = raw is PdfReference r ? r.ObjectNum : (int?)null;
            var node = new StructNode(element, number, number ?? parentOwner,
                element.GetOptional("Pg") is PdfReference pg ? pg.ObjectNum : inheritedPage);
            nodes[element] = node;
            pending.Push(node);
            return node;
        }

        bool RemovedAt(int? page, PdfDictionary element) =>
            page is { } p ? removedPages.Contains(p) : onRemovedPage.Contains(element);

        StructKid Classify(PdfObject raw, int? itemOwner, StructNode parent)
        {
            var resolved = Resolve(document, raw);
            if (resolved is PdfInteger)
                return new StructKid(raw, null, RemovedAt(parent.Page, parent.Element));
            if (resolved is not PdfDictionary dict)
                return new StructKid(raw, null, false);
            var itemPage = dict.GetOptional("Pg") is PdfReference ipg ? ipg.ObjectNum : parent.Page;
            switch (dict.GetNameOrNull("Type"))
            {
                case "MCR":
                    return new StructKid(raw, null, RemovedAt(itemPage, parent.Element));
                case "OBJR":
                    var obj = dict.GetOptional("Obj") as PdfReference;
                    var removed = (obj != null && (removedObjects.Contains(obj.ObjectNum) || removedPages.Contains(obj.ObjectNum)))
                        || RemovedAt(itemPage, parent.Element);
                    return new StructKid(raw, null, removed);
            }
            return dict.ContainsKey("S")
                ? new StructKid(raw, Discover(dict, raw, itemOwner, parent.Page), false)
                : new StructKid(raw, null, false);
        }

        var rootKids = new List<StructKid>();
        foreach (var (raw, itemOwner) in KidItems(document, root, rootOwner))
        {
            rootKids.Add(Resolve(document, raw) is PdfDictionary element && element.ContainsKey("S")
                ? new StructKid(raw, Discover(element, raw, itemOwner, null), false)
                : new StructKid(raw, null, false));
        }
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var (raw, itemOwner) in KidItems(document, node.Element, node.Owner))
            {
                var kid = Classify(raw, itemOwner, node);
                node.Kids.Add(kid);
                if (kid.Child != null)
                    continue;
                if (kid.Removed)
                    node.HasRemoved = true;
                else
                    node.HasKept = true;
            }
        }

        // Decide children before parents: iterative post-order. The back edge
        // of a cycle (malformed) is ignored.
        var order = new Stack<StructNode>();
        foreach (var kid in rootKids)
        {
            if (kid.Child is { } child)
                order.Push(child);
        }
        while (order.Count > 0)
        {
            var node = order.Peek();
            if (node.State == 0)
            {
                node.State = 1;
                foreach (var kid in node.Kids)
                {
                    if (kid.Child is { State: 0 } child)
                        order.Push(child);
                }
                continue;
            }
            order.Pop();
            if (node.State != 1)
                continue;
            node.State = 2;
            var aliveChild = node.Kids.Any(k => k.Child is { State: 2, Dead: false });
            var deadChild = node.Kids.Any(k => k.Child is { Dead: true });
            node.Touched = node.HasRemoved || deadChild || (node.Page is { } p && removedPages.Contains(p));
            node.Dead = node.Touched && !node.HasKept && !aliveChild;
        }

        var cut = 0;
        // The root and each surviving element lose their dropped children and
        // removed-page items; a surviving element its text fields.
        var droppedFromRoot = rootKids.Count(k => k.Child is { Dead: true });
        if (droppedFromRoot > 0)
        {
            root["K"] = new PdfArray(rootKids.Where(k => k.Child is not { Dead: true }).Select(k => k.Raw));
            cut += droppedFromRoot;
            if (rootOwner is { } n)
                edited.Add(n);
        }
        foreach (var node in nodes.Values)
        {
            if (node.Dead || !node.Touched)
                continue;
            var keep = node.Kids.Where(k => !k.Removed && k.Child is not { Dead: true }).Select(k => k.Raw).ToList();
            if (keep.Count != node.Kids.Count)
            {
                node.Element["K"] = new PdfArray(keep);
                cut += node.Kids.Count - keep.Count;
            }
            if (node.Element.GetOptional("Pg") is PdfReference own && removedPages.Contains(own.ObjectNum))
                node.Element.Remove("Pg");
            foreach (var carrier in StructureTreeRedactionScrubber.StructureElementTextCarriers)
            {
                if (node.Element.Remove(carrier))
                    cut++;
            }
            if (node.Owner is { } n)
                edited.Add(n);
        }

        var dead = new HashSet<PdfDictionary>(
            nodes.Values.Where(n => n.Dead).Select(n => n.Element), ReferenceEqualityComparer.Instance);
        bool IsDead(PdfObject raw) => Resolve(document, raw) is PdfDictionary d && dead.Contains(d);

        if (parentTreeRaw != null)
        {
            WalkTree(parentTreeRaw, "Nums", pairs =>
            {
                var rebuilt = new PdfArray();
                var changed = false;
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                {
                    var value = pairs[i + 1];
                    if ((Resolve(document, pairs[i]) is PdfInteger key && removedKeys.Contains(key.Value)) || IsDead(value))
                    {
                        changed = true;
                        cut++;
                        continue;
                    }
                    if (Resolve(document, value) is PdfArray slots && slots.Any(IsDead))
                    {
                        changed = true;
                        cut += slots.Count(IsDead);
                        var nulled = new PdfArray(slots.Select(s => IsDead(s) ? PdfNull.Instance : s));
                        if (nulled.All(s => s is PdfNull))
                            continue;
                        value = nulled;
                    }
                    rebuilt.Add(pairs[i]);
                    rebuilt.Add(value);
                }
                return changed ? rebuilt : null;
            });
        }
        if (dead.Count > 0 && root.GetOptional("IDTree") is { } idTreeRaw)
        {
            WalkTree(idTreeRaw, "Names", pairs =>
            {
                var rebuilt = new PdfArray();
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                {
                    if (IsDead(pairs[i + 1]))
                    {
                        cut++;
                        continue;
                    }
                    rebuilt.Add(pairs[i]);
                    rebuilt.Add(pairs[i + 1]);
                }
                return rebuilt.Count != pairs.Count ? rebuilt : null;
            });
        }

        foreach (var node in nodes.Values)
        {
            if (!node.Dead)
                continue;
            cut++;
            if (node.Number is { } number)
                removedObjects.Add(number);
        }
        return cut;

        // Walk a number or name tree from the structure tree root; a leaf whose
        // pairs the callback rewrites gets the new array.
        void WalkTree(PdfObject treeRaw, string leafKey, Func<PdfArray, PdfArray?> rewriteLeaf)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfObject Raw, int? Owner)>();
            stack.Push((treeRaw, rootOwner));
            while (stack.Count > 0)
            {
                var (raw, parentOwner) = stack.Pop();
                if (Resolve(document, raw) is not PdfDictionary node || !seen.Add(node))
                    continue;
                var owner = raw is PdfReference r ? r.ObjectNum : parentOwner;
                if (node.GetOptional(leafKey) is { } leafRaw && Resolve(document, leafRaw) is PdfArray pairs
                    && rewriteLeaf(pairs) is { } rewritten)
                {
                    node[leafKey] = rewritten;
                    if (owner is { } n)
                        edited.Add(n);
                }
                if (node.GetOptional("Kids") is { } kidsRaw && Resolve(document, kidsRaw) is PdfArray kids)
                {
                    var kidsOwner = kidsRaw is PdfReference kr ? kr.ObjectNum : owner;
                    foreach (var kid in kids)
                        stack.Push((kid, kidsOwner));
                }
            }
        }

        void AddKey(PdfDictionary? dict, string key, HashSet<long> into)
        {
            if (dict?.GetOptional(key) is { } raw && Resolve(document, raw) is PdfInteger value)
                into.Add(value.Value);
        }
    }

    /// <summary>
    /// The items of a structure node's <c>/K</c> (an array's entries, or the
    /// single value), each with the indirect object an inline item lives in:
    /// an indirect <c>/K</c> array's own, else the node's.
    /// </summary>
    private static IReadOnlyList<(PdfObject Raw, int? Owner)> KidItems(PdfDocument document, PdfDictionary node, int? nodeOwner)
    {
        if (node.GetOptional("K") is not { } raw)
            return [];
        if (Resolve(document, raw) is not PdfArray array)
            return [(raw, nodeOwner)];
        var owner = raw is PdfReference r ? r.ObjectNum : nodeOwner;
        return [.. array.Select(item => (item, owner))];
    }

    private sealed class StructNode(PdfDictionary element, int? number, int? owner, int? page)
    {
        public PdfDictionary Element { get; } = element;
        /// <summary>The element's object number when it is indirect.</summary>
        public int? Number { get; } = number;
        /// <summary>The indirect object the element lives in.</summary>
        public int? Owner { get; } = owner;
        /// <summary>Its own <c>/Pg</c>, else the nearest ancestor's.</summary>
        public int? Page { get; } = page;
        public List<StructKid> Kids { get; } = [];
        public bool HasKept { get; set; }
        public bool HasRemoved { get; set; }
        public bool Touched { get; set; }
        public bool Dead { get; set; }
        /// <summary>Post-order state: 0 unseen, 1 children pending, 2 decided.</summary>
        public int State { get; set; }
    }

    /// <summary>One <c>/K</c> item: a child element, or a content item and whether it is on a removed page.</summary>
    private readonly record struct StructKid(PdfObject Raw, StructNode? Child, bool Removed);

    /// <summary>Annotations listed by a removed page, or deleted, and listed by no live page.</summary>
    private static HashSet<int> RemovedAnnotations(
        PdfDocument document,
        IReadOnlySet<int> removedPages,
        IReadOnlySet<int> deletedAnnotations,
        IEnumerable<PdfDictionary> livePages)
    {
        var onLivePages = new HashSet<int>();
        foreach (var page in livePages)
            AddAnnotationNumbers(document, page, onLivePages);

        var removed = new HashSet<int>(deletedAnnotations);
        foreach (var number in removedPages)
        {
            if (TryGet(document, number) is PdfDictionary page)
                AddAnnotationNumbers(document, page, removed);
        }
        removed.ExceptWith(onLivePages);
        removed.ExceptWith(removedPages);
        return removed;
    }

    private static void AddAnnotationNumbers(PdfDocument document, PdfDictionary page, HashSet<int> into)
    {
        PdfObject? annots;
        try
        {
            annots = page.GetOptional("Annots") is { } raw ? document.Resolve(raw) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }
        if (annots is not PdfArray array)
            return;
        foreach (var item in array)
        {
            if (item is PdfReference reference)
                into.Add(reference.ObjectNum);
        }
    }

    /// <summary>
    /// Fields whose <c>/Kids</c> the cut emptied: each has no widget left
    /// (§12.7.4), so it goes too. An indirect field is returned for the next
    /// walk to cut wherever it is listed; an inline one is removed from its
    /// parent here.
    /// </summary>
    private static HashSet<int> FieldsLeftWithoutWidgets(PdfDocument document, HashSet<PdfArray> shrunk, HashSet<int> edited)
    {
        var pruned = new HashSet<int>();
        if (shrunk.Count == 0
            || document.Catalog.GetOptional("AcroForm") is not { } acroFormRaw
            || Resolve(document, acroFormRaw) is not PdfDictionary acroForm
            || acroForm.GetOptional("Fields") is not { } fieldsRaw)
        {
            return pruned;
        }

        var visited = new HashSet<int>();
        int? Owner(PdfObject raw, int? fallback) => raw is PdfReference r ? r.ObjectNum : fallback;
        // An inline /AcroForm lives in the catalog.
        var acroFormOwner = Owner(acroFormRaw, document.Trailer.GetReferenceOrNull("Root")?.ObjectNum);

        void Prune(PdfArray kids, int? kidsOwner)
        {
            for (var i = kids.Count - 1; i >= 0; i--)
            {
                var kidRaw = kids[i];
                if (kidRaw is PdfReference r && !visited.Add(r.ObjectNum))
                    continue;
                if (Resolve(document, kidRaw) is not PdfDictionary field
                    || field.GetOptional("Kids") is not { } childRaw
                    || Resolve(document, childRaw) is not PdfArray children)
                {
                    continue;
                }

                Prune(children, Owner(childRaw, Owner(kidRaw, kidsOwner)));
                if (children.Count != 0 || !shrunk.Contains(children))
                    continue;
                if (kidRaw is PdfReference fieldRef)
                {
                    pruned.Add(fieldRef.ObjectNum);
                }
                else
                {
                    kids.RemoveAt(i);
                    shrunk.Add(kids);
                    if (kidsOwner is { } number)
                        edited.Add(number);
                }
            }
        }

        if (Resolve(document, fieldsRaw) is PdfArray fields)
            Prune(fields, Owner(fieldsRaw, acroFormOwner));
        return pruned;
    }

    private static PdfObject? TryGet(PdfDocument document, int objectNumber)
    {
        try
        {
            return document.GetObject(objectNumber);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static PdfObject? Resolve(PdfDocument document, PdfObject obj)
    {
        try
        {
            return document.Resolve(obj);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
