using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Deep-clones a PDF object graph from a source document into a target
/// document, registering new indirect objects in the target as needed.
///
/// Extracted from <see cref="PageCollection"/>'s original page-copy logic
/// (#628) so document-merge/split operations can reuse the exact same,
/// already-hardened cloning behavior for outline and AcroForm-field
/// subtrees too, not just page content — an outline entry and a field
/// object are just PDF dictionary/array graphs with internal references,
/// same as a page. One cloner, one set of edge cases to get right.
/// </summary>
internal sealed class PdfObjectCloner
{
    private readonly PdfDocument _target;

    public PdfObjectCloner(PdfDocument target)
    {
        _target = target;
    }

    /// <summary>
    /// Maps a source page dictionary (by reference identity — page dict
    /// instances never collide across independently-parsed documents, so
    /// no need to key by source document too) to that page's already-
    /// reserved reference in the target document, for pages that are
    /// <em>also</em> being cloned as part of the same merge operation.
    ///
    /// When a <c>/Type Page</c> reference is encountered during cloning
    /// (e.g. a link annotation's <c>/Dest</c>, or an outline item's
    /// destination) and it resolves to a page found in this map, the
    /// clone gets a working forward reference to that page's new home
    /// instead of being dropped. When null, or when the specific page
    /// isn't in the map (destination outside the merge, or this is a
    /// same-document single-page <see cref="PageCollection.Insert"/> with
    /// no merge context at all), the reference resolves to
    /// <see cref="PdfNull.Instance"/> — the original guard against
    /// re-cloning the page tree itself (parent-page back-references,
    /// external destinations) stays exactly as conservative as before.
    /// </summary>
    public Dictionary<PdfDictionary, PdfReference>? PageMap { get; set; }

    /// <summary>
    /// Clone <paramref name="obj"/> (from <paramref name="sourceDocument"/>)
    /// into a form valid for <see cref="_target"/>. <paramref name="clonedRefs"/>
    /// memoizes already-cloned indirect objects <em>for this source
    /// document</em> so cyclic/shared structure round-trips correctly and
    /// isn't duplicated — callers cloning multiple related subtrees from
    /// the same source (e.g. a page's content, then that page's AcroForm
    /// field which a widget on it also references) should share one
    /// <paramref name="clonedRefs"/> instance across those calls.
    /// </summary>
    public PdfObject CloneObject(
        PdfDocument sourceDocument,
        PdfObject obj,
        Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference> clonedRefs)
    {
        if (obj is PdfReference reference)
            return CloneReference(sourceDocument, reference, clonedRefs);

        if (obj is PdfStream stream)
            return CloneStream(sourceDocument, stream, clonedRefs);

        if (obj is PdfDictionary dictionary)
            return CloneDictionary(sourceDocument, dictionary, clonedRefs);

        if (obj is PdfArray array)
            return CloneArray(sourceDocument, array, clonedRefs);

        if (obj is PdfName name)
            return new PdfName(name.Value);

        if (obj is PdfString str)
            return new PdfString(str.Bytes.ToArray());

        if (obj is PdfInteger integer)
            return new PdfInteger(integer.Value);

        if (obj is PdfReal real)
            return new PdfReal(real.Value);

        if (obj is PdfBoolean boolean)
            return PdfBoolean.Get(boolean.Value);

        return obj;
    }

    /// <summary>
    /// Clone an indirect reference, registering a brand-new target object
    /// the first time a given source object number is seen (memoized via
    /// <paramref name="clonedRefs"/> for the rest of this call chain).
    /// </summary>
    public PdfObject CloneReference(
        PdfDocument sourceDocument,
        PdfReference reference,
        Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference> clonedRefs)
    {
        var key = (reference.ObjectNum, reference.Generation);
        if (clonedRefs.TryGetValue(key, out var existing))
            return existing;

        var resolved = sourceDocument.Resolve(reference);
        if (resolved is PdfDictionary dict)
        {
            var type = dict.GetNameOrNull("Type");
            if (type == "Page")
            {
                if (PageMap != null && PageMap.TryGetValue(dict, out var mappedPageRef))
                    return mappedPageRef;
                return PdfNull.Instance;
            }
            if (type == "Pages")
            {
                // A /Pages tree node is never a legitimate destination —
                // only leaf /Type Page dicts are. Always drop it (guards
                // against page-tree parent-cycle references).
                return PdfNull.Instance;
            }
        }

        // Reserve and memoize the target object BEFORE recursing into its
        // contents. Some source graphs are genuinely cyclic outside the
        // page tree too — e.g. an outline item's /Prev pointing back to a
        // sibling whose /Next is still being cloned, or a field's /Kids
        // widget pointing back to its /Parent field. Memoizing only after
        // CloneObject returns means that cycle re-enters CloneReference
        // for the same key before it's in the map, recursing forever
        // (confirmed via a real stack-overflow crash with a /Prev-linked
        // 2-item outline during #628 development). Reserving first makes
        // the self-reference resolve to the in-flight placeholder instead.
        var placeholderRef = _target.AddIndirectObject(PdfNull.Instance);
        clonedRefs[key] = placeholderRef;

        var clonedObject = CloneObject(sourceDocument, resolved, clonedRefs);
        _target.ReplaceIndirectObject(placeholderRef.ObjectNum, clonedObject);
        return placeholderRef;
    }

    public PdfDictionary CloneDictionary(
        PdfDocument sourceDocument,
        PdfDictionary source,
        Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference> clonedRefs)
    {
        var clone = new PdfDictionary();
        var isAnnotation = source.GetNameOrNull("Type") == "Annot";

        // No blanket "Parent" exclusion here: CloneReference already nulls
        // any reference that resolves to a /Type Page or /Type Pages dict
        // (the actual guard against page-tree back-reference cycles), so a
        // page's own /Parent drops out on its own. A dict-key-level
        // exclusion would ALSO strip legitimate, non-cyclic /Parent links —
        // AcroForm field hierarchies (child field -> parent field, needed to
        // build fully-qualified names) and outline items (child bookmark ->
        // parent bookmark) both use /Parent and both need it preserved for
        // #628's merge to produce a working field/outline tree. Memoization
        // in clonedRefs already makes parent<->child back-references safe to
        // walk (a repeat reference to an already-cloned object number
        // returns the memoized clone instead of recursing again).
        foreach (var kvp in source)
        {
            var key = kvp.Key.Value;

            if (isAnnotation && key == "P")
                continue;

            var cloned = CloneObject(sourceDocument, kvp.Value, clonedRefs);
            if (cloned is not PdfNull)
                clone[key] = cloned;
        }

        return clone;
    }

    public PdfArray CloneArray(
        PdfDocument sourceDocument,
        PdfArray source,
        Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference> clonedRefs)
    {
        var clone = new PdfArray();
        foreach (var item in source)
        {
            var cloned = CloneObject(sourceDocument, item, clonedRefs);
            clone.Add(cloned);
        }

        return clone;
    }

    public PdfStream CloneStream(
        PdfDocument sourceDocument,
        PdfStream source,
        Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference> clonedRefs)
    {
        var dict = CloneDictionary(sourceDocument, source, clonedRefs);
        var clone = new PdfStream(dict, source.EncodedData.ToArray());
        if (source.IsDecoded)
            clone.SetDecodedData(source.DecodedData.ToArray());

        return clone;
    }

    /// <summary>
    /// Clone a top-level page dictionary (the page itself, not a reference
    /// to it — mirrors the old <c>PageCollection.ClonePageDictionary</c>).
    /// Does not set <c>/Parent</c>; the caller wires that to whatever
    /// Pages node the clone is inserted under.
    /// </summary>
    public PdfDictionary ClonePageDictionary(
        PdfPage sourcePage,
        Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference> clonedRefs)
    {
        var sourceDict = sourcePage.Dictionary;
        var newDict = new PdfDictionary();

        foreach (var kvp in sourceDict)
        {
            if (kvp.Key.Value == "Parent")
                continue;

            newDict[kvp.Key.Value] = CloneObject(sourcePage.Document, kvp.Value, clonedRefs);
        }

        newDict.SetName("Type", "Page");
        return newDict;
    }
}
