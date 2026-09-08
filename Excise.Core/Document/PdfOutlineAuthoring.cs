using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Authoring API for the document's /Outlines (bookmark) tree. §12.3.3.
/// <see cref="PdfOutlineParser"/> in <c>PdfOutline.cs</c> is the read side;
/// this is the write side, added for #1412. Mirrors
/// <see cref="AcroFormAuthoring"/>'s get-or-create-root, append-as-last-child
/// shape, and reuses the linked-list wiring already proven in
/// <c>PdfDocumentMerger.MergeOutline</c>/<c>GetOrCreateOutlineRoot</c>.
/// </summary>
public static class PdfOutlineAuthoring
{
    /// <summary>
    /// Append a new top-level outline item pointing at <paramref name="pageNumber"/>
    /// (1-based, fit-whole-page destination). Returns the new item's object
    /// reference so callers can add children under it via
    /// <see cref="AddChildOutlineItem"/>.
    /// </summary>
    public static PdfReference AddOutlineItem(this PdfDocument document, string title, int pageNumber)
    {
        var (root, rootRef) = GetOrCreateOutlineRoot(document);
        var pageRef = document.GetPageReference(pageNumber)
            ?? throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} not found");

        return AppendChild(document, root, rootRef, title, pageRef);
    }

    /// <summary>
    /// Append a new outline item as the last child of an existing item
    /// (identified by the reference <see cref="AddOutlineItem"/> or a prior
    /// call to this method returned), pointing at <paramref name="pageNumber"/>.
    /// </summary>
    public static PdfReference AddChildOutlineItem(this PdfDocument document, PdfReference parentRef, string title, int pageNumber)
    {
        if (document.Resolve(parentRef) is not PdfDictionary parent)
            throw new ArgumentException("Parent outline item reference does not resolve to a dictionary", nameof(parentRef));

        var pageRef = document.GetPageReference(pageNumber)
            ?? throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} not found");

        return AppendChild(document, parent, parentRef, title, pageRef);
    }

    private static PdfReference AppendChild(PdfDocument document, PdfDictionary parent, PdfReference parentRef, string title, PdfReference pageRef)
    {
        var item = new PdfDictionary();
        item.SetString("Title", title);
        item["Parent"] = parentRef;
        item["Dest"] = new PdfArray(pageRef, new PdfName("Fit"));
        var itemRef = document.AddIndirectObject(item);

        var existingLastObj = parent.GetOptional("Last");
        if (existingLastObj is PdfReference existingLastRef &&
            document.Resolve(existingLastRef) is PdfDictionary existingLastDict)
        {
            existingLastDict["Next"] = itemRef;
            item["Prev"] = existingLastRef;
            parent["Last"] = itemRef;
        }
        else
        {
            parent["First"] = itemRef;
            parent["Last"] = itemRef;
        }

        // Advisory viewer hint only (§12.3.3's initial open/closed count),
        // not a full recursive descendant count -- matches the simplified
        // convention PdfDocumentMerger.MergeOutline already established.
        int existingCount = parent.GetOptional("Count") is PdfInteger ci ? (int)ci.Value : 0;
        parent.SetInt("Count", existingCount + 1);

        return itemRef;
    }

    private static (PdfDictionary Root, PdfReference RootRef) GetOrCreateOutlineRoot(PdfDocument document)
    {
        var existingObj = document.Catalog.GetOptional("Outlines");
        if (existingObj is PdfReference existingRef && document.Resolve(existingObj) is PdfDictionary existingDict)
            return (existingDict, existingRef);

        var root = new PdfDictionary();
        root.SetName("Type", "Outlines");
        var rootRef = document.AddIndirectObject(root);
        document.Catalog["Outlines"] = rootRef;
        return (root, rootRef);
    }
}
