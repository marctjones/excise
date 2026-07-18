using Excise.Core.Parsing;
using Excise.Core.Primitives;
using System.Collections;

namespace Excise.Core.Document;

/// <summary>
/// Collection of pages in a PDF document.
/// Provides methods for adding, removing, inserting, and reordering pages.
/// </summary>
public class PageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument _document;
    private readonly List<PdfPage> _pages;
    private readonly PdfDictionary _pagesDict;
    private PdfArray _kidsArray;
    private int _declaredCount;
    private bool _malformedPageTree;
    private const int MaxRecoverableMalformedPageCountOverage = 128;

    /// <summary>
    /// Creates a new page collection for the document.
    /// </summary>
    internal PageCollection(PdfDocument document)
    {
        _document = document;
        _pages = new List<PdfPage>();

        // Get the Pages dictionary from catalog. A hostile/malformed catalog
        // may lack a valid /Pages — fail with a typed PdfParseException so
        // callers see graceful failure, not a raw InvalidOperationException. (#352)
        var pagesRef = document.Catalog.GetReferenceOrNull("Pages");
        if (pagesRef == null)
            throw new PdfParseException("Document has no Pages dictionary");

        _pagesDict = document.GetObject(pagesRef) as PdfDictionary
            ?? throw new PdfParseException("Pages is not a dictionary");

        // Get or create Kids array
        var kidsObj = _pagesDict.GetOptional("Kids");
        _kidsArray = kidsObj != null
            ? document.Resolve(kidsObj) as PdfArray ?? new PdfArray()
            : new PdfArray();

        // Load all pages (flatten page tree if necessary)
        LoadPages();
    }

    /// <summary>
    /// Load all pages from the page tree.
    /// </summary>
    private void LoadPages()
    {
        _pages.Clear();
        _malformedPageTree = false;
        _declaredCount = GetDeclaredRootPageCount();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        LoadPagesRecursive(_pagesDict, 0, visited, depth: 0);
    }

    private int GetDeclaredRootPageCount()
    {
        try
        {
            var countObj = _pagesDict.GetOptional("Count");
            if (countObj == null || !countObj.TryGetNumber(out var count))
                return 0;

            if (count <= 0 || count > int.MaxValue)
                return 0;

            return (int)count;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Maximum legal /Pages tree depth. PDF 2.0 doesn't impose a hard
    /// limit but real documents rarely exceed depth 4–6; anything above
    /// 32 is almost certainly a malformed or malicious file. We bail
    /// rather than let a pathologically-deep tree consume stack.
    /// </summary>
    private const int MaxPageTreeDepth = 32;

    /// <summary>
    /// Recursively load pages from a Pages node. Defended against
    /// circular references (a Pages dict whose /Kids points back to
    /// itself or an ancestor) and depth-exhaustion attacks via a
    /// reference-equality visited-set + a hard depth limit.
    ///
    /// Pre-this-fix, a circular page tree caused a stack overflow that
    /// crashed the test host — caught by the differential corpus run
    /// against pdf.js's regression PDFs, which include exactly this
    /// shape of malformed input.
    /// </summary>
    private int LoadPagesRecursive(
        PdfDictionary node, int pageNumber,
        HashSet<PdfDictionary> visited, int depth)
    {
        if (depth > MaxPageTreeDepth)
        {
            _malformedPageTree = true;
            return 0;
        }

        if (!visited.Add(node))
        {
            _malformedPageTree = true;
            return 0; // cycle — already on this path
        }

        try
        {
            var type = node.GetNameOrNull("Type");

            if (type == "Page")
            {
                // This is a leaf page
                _pages.Add(new PdfPage(_document, node, pageNumber + 1));
                return 1;
            }

            // This is a Pages node
            var kids = node.GetArrayOrNull("Kids");
            if (kids == null)
                return 0;

            int count = 0;
            foreach (var kidObj in kids)
            {
                // Kids may be either indirect references (standard) or inline
                // dictionaries (our mutation path — AddBlank/Insert — writes
                // inline dicts because a full indirect-object registry isn't
                // wired up yet). Handle both.
                var kid = ResolvePageTreeKid(kidObj);
                if (kid == null) continue;

                count += LoadPagesRecursive(kid, pageNumber + count, visited, depth + 1);
            }

            return count;
        }
        finally
        {
            visited.Remove(node);
        }
    }

    /// <summary>
    /// Number of pages in the collection.
    /// </summary>
    public int Count
    {
        get
        {
            if (!_malformedPageTree)
                return _declaredCount > 0 ? _declaredCount : _pages.Count;

            if (_declaredCount > _pages.Count &&
                _declaredCount - _pages.Count <= MaxRecoverableMalformedPageCountOverage)
            {
                return _declaredCount;
            }

            return _pages.Count;
        }
    }

    /// <summary>
    /// Get a page by index (0-based).
    /// </summary>
    public PdfPage this[int index]
    {
        get
        {
            if (index < 0 || index >= _pages.Count)
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index), $"Page index must be between 0 and {Count - 1}");

                return FindPageByIndex(index);
            }
            return _pages[index];
        }
    }

    private PdfPage FindPageByIndex(int targetIndex)
    {
        int currentIndex = 0;
        var page = FindPageByIndexRecursive(
            _pagesDict,
            targetIndex,
            ref currentIndex,
            new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance),
            depth: 0);

        if (page != null)
            return page;

        throw new PdfParseException($"Page index {targetIndex} could not be resolved from the page tree");
    }

    private PdfPage? FindPageByIndexRecursive(
        PdfDictionary node,
        int targetIndex,
        ref int currentIndex,
        HashSet<PdfDictionary> path,
        int depth)
    {
        if (depth > MaxPageTreeDepth)
            throw new PdfParseException("Page tree exceeds maximum depth");
        if (!path.Add(node))
            throw new PdfParseException("Page tree contains a circular reference");

        try
        {
            var type = node.GetNameOrNull("Type");
            if (type == "Page")
            {
                if (currentIndex == targetIndex)
                    return new PdfPage(_document, node, targetIndex + 1);

                currentIndex++;
                return null;
            }

            var kids = node.GetArrayOrNull("Kids");
            if (kids == null)
                return null;

            foreach (var kidObj in kids)
            {
                var kid = ResolvePageTreeKid(kidObj);
                if (kid == null) continue;

                var page = FindPageByIndexRecursive(kid, targetIndex, ref currentIndex, path, depth + 1);
                if (page != null)
                    return page;
            }

            return null;
        }
        finally
        {
            path.Remove(node);
        }
    }

    private PdfDictionary? ResolvePageTreeKid(PdfObject kidObj)
        => kidObj switch
        {
            PdfReference kr => _document.GetObject(kr) as PdfDictionary,
            PdfDictionary kd => kd,
            _ => null,
        };

    /// <summary>
    /// Add a page from another document to the end of this document.
    /// Creates a copy of the page.
    /// </summary>
    /// <param name="page">The page to add (can be from another document).</param>
    public void Add(PdfPage page)
    {
        Insert(_pages.Count, page);
    }

    /// <summary>
    /// Append a blank page of the given size (in points) to the document
    /// and return it. Default size is US Letter (612 × 792 points).
    /// </summary>
    public PdfPage AddBlank(double widthPoints = 612, double heightPoints = 792)
    {
        // Build a minimal page dictionary: Type / Parent / MediaBox /
        // Resources. Contents is omitted — callers get one on demand via
        // page.SetContentStreamBytes (or GetGraphics + Flush).
        var pageDict = new PdfDictionary();
        pageDict["Type"] = new PdfName("Page");
        pageDict["Parent"] = _document.Catalog.GetReference("Pages");
        var mediaBox = new PdfArray();
        mediaBox.Add(0.0);
        mediaBox.Add(0.0);
        mediaBox.Add(widthPoints);
        mediaBox.Add(heightPoints);
        pageDict["MediaBox"] = mediaBox;
        pageDict["Resources"] = new PdfDictionary();

        // Register as an indirect object so the writer produces a proper
        // `N 0 obj … endobj` frame, and reference it from /Kids.
        var pageRef = _document.AddIndirectObject(pageDict);
        _kidsArray.Add(pageRef);
        _pagesDict["Kids"] = _kidsArray;
        _pagesDict.SetInt("Count", _pages.Count + 1);

        LoadPages();
        return _pages[^1];
    }

    /// <summary>
    /// Insert a page at the specified index.
    /// Creates a copy of the page.
    /// </summary>
    /// <param name="index">Index at which to insert the page.</param>
    /// <param name="page">The page to insert.</param>
    public void Insert(int index, PdfPage page)
    {
        if (index < 0 || index > _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Insert index must be between 0 and {_pages.Count}");

        // Clone the page dictionary and any indirect objects the page owns
        // (content streams, resources, annotations) via the shared cloner
        // (#628 — also used by document-merge for outline/AcroForm subtrees).
        // Without this, copying a page between documents can leave dangling
        // references to source-doc object numbers that do not exist in the
        // target. No PageMap is set here, so any /Type Page reference this
        // page's own annotations happen to carry (e.g. a same-document
        // internal link) resolves to null, exactly as before — resolving it
        // correctly requires the multi-page merge context in
        // PdfDocumentMerger, which knows about every page being copied, not
        // just this one in isolation.
        var cloner = new PdfObjectCloner(_document);
        var clonedRefs = new Dictionary<(int, int), PdfReference>();
        var newPageDict = cloner.ClonePageDictionary(page, clonedRefs);
        newPageDict["Parent"] = _document.Catalog.GetReference("Pages");

        // Register as an indirect object (matching AddBlank's convention)
        // so the page has a stable identity other objects (links, outline
        // entries added later) could reference — previously this stored
        // the dict inline in /Kids, which the loader already tolerated
        // (ResolvePageTreeKid handles both) but gave the page no reference
        // of its own.
        var pageRef = _document.AddIndirectObject(newPageDict);
        _kidsArray.Insert(index, pageRef);
        _pagesDict["Kids"] = _kidsArray;

        // Update Count
        _pagesDict.SetInt("Count", _pages.Count + 1);

        // Reload pages to get correct page numbers
        LoadPages();
    }

    /// <summary>
    /// Append a page reference that has already been registered as an
    /// indirect object in this document (#628 — used by
    /// <c>PdfDocumentMerger</c>, which reserves and fills page objects
    /// itself via <see cref="PdfDocument.AddIndirectObject"/>/
    /// <see cref="PdfDocument.ReplaceIndirectObject"/> before wiring them
    /// into any target's page tree, so cross-page destinations across the
    /// whole merge can resolve regardless of which page gets appended
    /// first). Does no cloning — the caller is responsible for the
    /// object already existing and being a valid <c>/Type Page</c> dict
    /// with the correct <c>/Parent</c>.
    /// </summary>
    internal void AppendPreRegisteredPage(PdfReference pageRef)
    {
        _kidsArray.Add(pageRef);
        _pagesDict["Kids"] = _kidsArray;
        _pagesDict.SetInt("Count", _pages.Count + 1);
        LoadPages();
    }

    /// <summary>
    /// Remove the page at the specified index.
    /// </summary>
    /// <param name="index">Index of the page to remove.</param>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Page index must be between 0 and {_pages.Count - 1}");

        if (_pages.Count <= 1)
            throw new InvalidOperationException("Cannot remove the last page from a document");

        // Remove from Kids array
        _kidsArray.RemoveAt(index);
        _pagesDict["Kids"] = _kidsArray;

        // Update Count
        _pagesDict.SetInt("Count", _pages.Count - 1);

        // Reload pages
        LoadPages();
    }

    /// <summary>
    /// Move a page from one position to another.
    /// </summary>
    /// <param name="fromIndex">Current index of the page.</param>
    /// <param name="toIndex">New index for the page.</param>
    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(fromIndex), $"From index must be between 0 and {_pages.Count - 1}");
        if (toIndex < 0 || toIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(toIndex), $"To index must be between 0 and {_pages.Count - 1}");

        if (fromIndex == toIndex)
            return;

        // Get the page reference from Kids array
        var pageRef = _kidsArray[fromIndex];

        // Remove from current position
        _kidsArray.RemoveAt(fromIndex);

        // Insert at new position (adjust if moving forward)
        _kidsArray.Insert(toIndex, pageRef);

        _pagesDict["Kids"] = _kidsArray;

        // Reload pages
        LoadPages();
    }

    /// <summary>
    /// Get the pages dictionary (for internal use).
    /// </summary>
    internal PdfDictionary PagesDictionary => _pagesDict;

    /// <inheritdoc />
    public IEnumerator<PdfPage> GetEnumerator() => _pages.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
