using Excise.Core.Primitives;

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
        var removedObjects = RemovedAnnotations(document, removedPages, deletedAnnotations, livePages);
        var edited = new HashSet<int>();
        var shrunk = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        var cut = 0;

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
