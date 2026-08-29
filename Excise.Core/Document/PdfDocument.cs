using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Writing;
using System.Linq;

namespace Excise.Core.Document;

/// <summary>
/// Represents a PDF document.
/// Main entry point for reading and manipulating PDFs.
/// </summary>
public class PdfDocument : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Dictionary<int, XRefEntry> _xref;
    private readonly Dictionary<int, PdfObject> _objectCache;
    private readonly PdfParser _parser;
    // Object resolution seeks and reads the single shared _parser/lexer stream and
    // mutates _objectCache, so it is NOT safe to call from multiple threads at once
    // (concurrent Seeks corrupt each other -> "Unexpected keyword 'obj'"). The GUI
    // hits this when a background search-indexer parses pages while the UI thread
    // reads links/renders. Serialize the whole resolve path; the lock is reentrant
    // so recursive resolution (ObjStm containers, /Length references) is fine. (#376)
    private readonly object _parseLock = new();
    private readonly StreamDecompressor _decompressor;
    private readonly Excise.Core.Security.PdfStandardSecurityHandler? _securityHandler;
    private PageCollection? _pages;
    private IReadOnlyList<PdfOcg>? _ocgs;
    private PdfOcgConfig? _ocgConfig;
    private PdfStructElement? _structureTree;
    private bool? _isTaggedPdf;
    private Dictionary<PdfDictionary, int>? _pagesByDict;
    private IReadOnlyList<PdfEmbeddedFile>? _embeddedFiles;

    /// <summary>
    /// The trailer dictionary.
    /// </summary>
    public PdfDictionary Trailer { get; }

    /// <summary>
    /// The document catalog.
    /// </summary>
    public PdfDictionary Catalog { get; }

    /// <summary>
    /// Number of pages in the document.
    /// </summary>
    public int PageCount => Pages.Count;

    /// <summary>
    /// PDF version (e.g., "1.4", "1.7", "2.0").
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Register <paramref name="obj"/> as a new indirect object in this
    /// document. Allocates the next free object number, wires it into
    /// the xref and object cache, and returns a reference callers can
    /// drop into other dictionaries or arrays.
    /// </summary>
    /// <remarks>
    /// Used by mutation paths (AddBlank, SetContentStreamBytes, …) that
    /// need to produce objects the writer can serialize at the top level
    /// with a real <c>N 0 obj … endobj</c> frame — critical for stream
    /// objects which are not valid inline in PDF syntax.
    /// </remarks>
    internal PdfReference AddIndirectObject(PdfObject obj)
    {
        int next = _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1;
        _xref[next] = new Excise.Core.Parsing.XRefEntry
        {
            Offset = 0, // filled in by the writer at serialize time
            Generation = 0,
            InUse = true,
        };
        _objectCache[next] = obj;
        return new PdfReference(next, 0);
    }

    /// <summary>
    /// The object number <see cref="AddIndirectObject"/> would allocate if
    /// called right now — computed without mutating the xref/object cache.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="Excise.Core.Writing.PdfDocumentWriter"/> to reserve
    /// a number for a write-time-only <c>/Encrypt</c> dictionary that must
    /// never become part of the persistent document graph (it is not
    /// reachable from the catalog, so re-adding it via
    /// <see cref="AddIndirectObject"/> on every save would leak an
    /// ever-growing number of orphaned encrypt-dict objects into
    /// <c>_xref</c>/<c>_objectCache</c> across repeated Save() calls on the
    /// same <see cref="PdfDocument"/> instance).
    /// </remarks>
    internal int NextFreeObjectNumber => _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1;

    /// <summary>
    /// Overwrite the content of an already-registered indirect object.
    /// </summary>
    /// <remarks>
    /// Used by the merge/split page-cloning path (#628) to <em>reserve</em>
    /// a stable <see cref="PdfReference"/> for every page across every
    /// source up front (via <see cref="AddIndirectObject"/> with a
    /// placeholder), so links/outlines being cloned in the same pass can
    /// resolve a forward reference to a page that hasn't been fully cloned
    /// yet — then fill in each page's real content once cloning completes.
    /// The xref entry from the original <see cref="AddIndirectObject"/>
    /// call is left untouched; only the cached object changes.
    /// </remarks>
    internal void ReplaceIndirectObject(int objectNumber, PdfObject obj)
    {
        _objectCache[objectNumber] = obj;
    }

    /// <summary>
    /// Mark an indirect object as free so it is no longer serialized.
    /// Used by flatten-then-redact (#355) to drop a Form XObject that has
    /// been inlined into a page and is no longer reachable from the trailer —
    /// otherwise the writer (which serializes every in-use object, with no
    /// garbage collection) would re-emit the orphan's content and leak the
    /// very text the redaction removed. Callers must confirm the object is
    /// unreachable before calling this.
    /// </summary>
    internal void RemoveObject(int objectNumber)
    {
        _xref.Remove(objectNumber);
        _objectCache.Remove(objectNumber);
    }

    /// <summary>
    /// Object numbers reachable from the trailer by walking the object graph
    /// (mark phase of a mark-and-sweep). Used to confirm an inlined Form
    /// XObject is truly orphaned before <see cref="RemoveObject"/> frees it.
    /// </summary>
    internal HashSet<int> ComputeReachableObjects()
        => ComputeReachableObjectsFrom(Trailer.Values);

    /// <summary>
    /// Every XMP <c>/Metadata</c> stream reachable in the document, not just
    /// the catalog's. §14.3.2 permits a <c>/Metadata</c> stream on ANY object
    /// (pages, Form XObjects, images), and a redacted term can survive in a
    /// page-level packet while the catalog packet is clean (#1129). Distinct
    /// by object number so a shared packet is scrubbed once.
    /// </summary>
    internal IEnumerable<PdfStream> EnumerateMetadataStreams()
    {
        var seen = new HashSet<int>();
        foreach (var objNum in ComputeReachableObjects())
        {
            PdfObject obj;
            try { obj = GetObject(objNum); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (obj is not PdfDictionary dict) continue;

            var mdRef = dict.GetOptional("Metadata");
            if (mdRef == null) continue;
            if (mdRef is PdfReference r && !seen.Add(r.ObjectNum)) continue;
            if (Resolve(mdRef) is PdfStream md) yield return md;
        }
    }

    /// <summary>
    /// Object numbers reachable from the trailer shape emitted by
    /// <see cref="Excise.Core.Writing.PdfDocumentWriter"/>. This intentionally
    /// excludes original-trailer entries that are not preserved on save, such
    /// as /Prev and /Encrypt, so full-save output also garbage-collects
    /// stale incremental-update objects.
    /// </summary>
    internal HashSet<int> ComputeSaveReachableObjects()
    {
        var roots = new List<PdfObject> { GetCatalogReference() };
        var infoRef = Trailer.GetReferenceOrNull("Info");
        if (infoRef != null)
            roots.Add(infoRef);
        return ComputeReachableObjectsFrom(roots);
    }

    private HashSet<int> ComputeReachableObjectsFrom(IEnumerable<PdfObject> roots)
    {
        var reachable = new HashSet<int>();
        var stack = new Stack<PdfObject>();
        foreach (var v in roots) stack.Push(v);

        while (stack.Count > 0)
        {
            var o = stack.Pop();
            switch (o)
            {
                case PdfReference r:
                    if (reachable.Add(r.ObjectNum))
                    {
                        PdfObject target;
                        try { target = GetObject(r.ObjectNum); }
                        catch (Exception __ex) when (__ex is not OutOfMemoryException) { break; }
                        stack.Push(target);
                    }
                    break;
                case PdfStream s:           // PdfStream is a PdfDictionary
                    foreach (var v in s.Values) stack.Push(v);
                    break;
                case PdfDictionary d:
                    foreach (var v in d.Values) stack.Push(v);
                    break;
                case PdfArray a:
                    foreach (var v in a) stack.Push(v);
                    break;
            }
        }
        return reachable;
    }

    /// <summary>
    /// Whether this document is encrypted.
    /// </summary>
    public bool IsEncrypted => Trailer.ContainsKey("Encrypt");

    /// <summary>
    /// The document's decoded <c>/P</c> permission flags (ISO 32000-2
    /// Table 22) — issue #642. Always non-null:
    /// <see cref="Excise.Core.Security.PdfPermissions.AllAllowed"/> for
    /// unencrypted documents (an unencrypted document genuinely has no
    /// restrictions, and callers at the action layer shouldn't have to
    /// null-check), and the decoded <c>/P</c> value for encrypted ones.
    /// A malformed encrypted document with an unreadable /Encrypt or /P
    /// also decodes as all-allowed — /P is advisory metadata, and failing
    /// open on a broken mask matches every mainstream reader.
    ///
    /// This is the raw document policy; enforcement points should consult
    /// <see cref="EffectivePermissions"/>, which additionally accounts for
    /// owner-password authority. See <see cref="Excise.Core.Security.PdfPermissions"/>
    /// for the full enforcement policy (action-layer only, accessibility
    /// carve-out, explicit overrides).
    /// </summary>
    public Excise.Core.Security.PdfPermissions Permissions { get; }

    /// <summary>
    /// Whether the document was opened with the OWNER password. Per spec
    /// the owner password confers full permissions, so enforcement only
    /// applies to user-password (including empty-password) opens. excise's
    /// decrypt path currently verifies only the USER password —
    /// owner-password-only opening is #324 and unsupported — so this is
    /// always <c>false</c> today; #324's owner-open path is expected to
    /// set it, which flips <see cref="EffectivePermissions"/> to
    /// all-allowed without any enforcement-point changes.
    /// </summary>
    public bool OpenedWithOwnerPassword { get; }

    /// <summary>
    /// The permissions that apply to THIS open of the document: the
    /// decoded <see cref="Permissions"/> for a user-password open, or
    /// <see cref="Excise.Core.Security.PdfPermissions.AllAllowed"/> when the
    /// document is unencrypted or was opened with the owner password.
    /// GUI/CLI/scripting enforcement points consult this, not
    /// <see cref="Permissions"/>.
    /// </summary>
    public Excise.Core.Security.PdfPermissions EffectivePermissions =>
        OpenedWithOwnerPassword ? Excise.Core.Security.PdfPermissions.AllAllowed : Permissions;

    /// <summary>
    /// Information dictionary (metadata). Publicly read-only; created on demand
    /// by the metadata setters (<see cref="SetTitle"/> etc.) when absent.
    /// </summary>
    public PdfDictionary? Info { get; private set; }

    /// <summary>
    /// Collection of pages in the document.
    /// Provides methods for adding, removing, and reordering pages.
    /// </summary>
    public PageCollection Pages
    {
        get
        {
            _pages ??= new PageCollection(this);
            return _pages;
        }
    }

    /// <summary>
    /// Get the list of Optional Content Groups (OCGs/layers) in this document.
    /// Returns an empty list if the document has no optional content.
    /// Cached after first call.
    /// </summary>
    public IReadOnlyList<PdfOcg> GetOptionalContentGroups()
    {
        _ocgs ??= PdfOcgParser.ParseOptionalContentGroups(this).ocgs;
        return _ocgs;
    }

    /// <summary>
    /// Get the Optional Content Groups configuration (visibility defaults, intent, etc).
    /// Returns a config with empty OCG list if the document has no optional content.
    /// Cached after first call.
    /// </summary>
    public PdfOcgConfig GetOptionalContentGroupConfig()
    {
        if (_ocgConfig == null)
        {
            (_ocgs, _ocgConfig) = PdfOcgParser.ParseOptionalContentGroups(this);
        }
        return _ocgConfig!;
    }

    /// <summary>
    /// Get the root element of the document's structure tree (tagged PDF).
    /// Returns null if the document has no /StructTreeRoot.
    /// Cached after first call.
    /// </summary>
    public PdfStructElement? GetStructureTree()
    {
        _structureTree ??= PdfStructTreeParser.ParseStructureTree(this) ?? null;
        return _structureTree;
    }

    /// <summary>
    /// Resolve the real body text of a tagged-PDF structure element from its
    /// marked-content references (#776 — the accessibility MCID→letter bridge).
    /// Gathers the extracted <see cref="Text.Letter"/>s whose /MCID (and page)
    /// match the element's references — both /MCID integers directly in the
    /// element's /K (which belong to the element's own /Pg, or the supplied
    /// <paramref name="inheritedPageNumber"/> when the element has none) and
    /// marked-content-reference (/MCR) child dictionaries (which carry their own
    /// /Pg) — and concatenates them in reference (reading) order.
    ///
    /// <para>
    /// This is how a heading or paragraph with no /ActualText carrier can still
    /// have its real glyphs read in structure order: /ActualText is the author's
    /// explicit replacement text, but most tagged elements have none and their
    /// text lives only in MCID-tagged content. Returns an empty string when the
    /// element references no resolvable marked content (e.g. a /Figure, or an
    /// element whose page cannot be determined).
    /// </para>
    /// </summary>
    public string ResolveStructElementText(PdfStructElement element, int? inheritedPageNumber = null)
    {
        if (element == null)
            return string.Empty;

        int? elementPage = PageNumberFromPg(element.RawDictionary) ?? inheritedPageNumber;

        // Ordered (page, mcid) references this element points at directly. Child
        // struct elements (/K dicts with their own /S) are NOT descended into —
        // each resolves its own text.
        var refs = new List<(int Page, int Mcid)>();
        CollectMarkedContentRefs(element.RawDictionary.GetOptional("K"), elementPage, refs, depth: 0);
        if (refs.Count == 0)
            return string.Empty;

        // Cache each referenced page's letters once.
        var lettersByPage = new Dictionary<int, IReadOnlyList<Text.Letter>>();
        var sb = new System.Text.StringBuilder();
        foreach (var (page, mcid) in refs)
        {
            if (page < 1 || page > PageCount)
                continue;
            if (!lettersByPage.TryGetValue(page, out var letters))
                lettersByPage[page] = letters = GetPage(page).Letters;
            foreach (var letter in letters)
            {
                if (letter.MarkedContentId == mcid)
                    sb.Append(letter.Value);
            }
        }
        return sb.ToString();
    }

    private void CollectMarkedContentRefs(
        PdfObject? kObj, int? elementPage, List<(int Page, int Mcid)> refs, int depth)
    {
        if (kObj == null || depth > 64)
            return;

        var resolved = Resolve(kObj);
        switch (resolved)
        {
            case PdfInteger mcidInt when elementPage.HasValue:
                refs.Add((elementPage.Value, (int)mcidInt.Value));
                break;

            case PdfArray arr:
                foreach (var item in arr)
                    CollectMarkedContentRefs(item, elementPage, refs, depth + 1);
                break;

            case PdfDictionary dict:
                // A child struct element (has /S) is a separate element; skip it.
                // A marked-content-reference dict (/MCR, or any /S-less dict with
                // an /MCID) carries the mcid and optionally its own /Pg.
                if (dict.GetOptional("S") != null)
                    break;
                var mcidObj = dict.GetOptional("MCID");
                if (mcidObj != null && Resolve(mcidObj) is PdfInteger mcrMcid)
                {
                    int? refPage = PageNumberFromPg(dict) ?? elementPage;
                    if (refPage.HasValue)
                        refs.Add((refPage.Value, (int)mcrMcid.Value));
                }
                break;
        }
    }

    // Map a dictionary's /Pg entry (a page reference) to its 1-based page number.
    private int? PageNumberFromPg(PdfDictionary dict)
    {
        var pgObj = dict.GetOptional("Pg");
        if (pgObj == null)
            return null;
        if (Resolve(pgObj) is not PdfDictionary pageDict)
            return null;

        if (_pagesByDict == null)
        {
            _pagesByDict = new Dictionary<PdfDictionary, int>();
            for (int i = 1; i <= PageCount; i++)
                _pagesByDict[GetPage(i).Dictionary] = i;
        }
        return _pagesByDict.TryGetValue(pageDict, out int n) ? n : (int?)null;
    }

    /// <summary>
    /// Check if this is a tagged PDF (has /MarkInfo/Marked = true).
    /// Tagged PDFs have a structure tree that associates content with semantic roles.
    /// </summary>
    public bool IsTaggedPdf
    {
        get
        {
            if (!_isTaggedPdf.HasValue)
            {
                var markInfo = Catalog.GetOptional("MarkInfo");
                var markInfoDict = markInfo != null ? (Resolve(markInfo) as PdfDictionary) : null;
                var markedObj = markInfoDict?.GetOptional("Marked");
                _isTaggedPdf = (markedObj is PdfName name && name.Value == "true") ||
                               (markedObj is PdfBoolean bool_ && bool_.Value);
            }
            return _isTaggedPdf.Value;
        }
    }

    private PdfDocument(
        Stream stream,
        bool ownsStream,
        Dictionary<int, XRefEntry> xref,
        PdfDictionary trailer,
        string version,
        Excise.Core.Security.PdfStandardSecurityHandler? securityHandler = null,
        Excise.Core.Security.PdfPermissions? permissions = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _xref = xref;
        _objectCache = new Dictionary<int, PdfObject>();
        _parser = new PdfParser(new PdfLexer(stream, ownsStream: false));
        _decompressor = new StreamDecompressor();
        _securityHandler = securityHandler;
        Permissions = permissions ?? Excise.Core.Security.PdfPermissions.AllAllowed;
        // Owner-password opening is #324 (unsupported): every successful
        // open today verifies the USER password, so permissions apply.
        OpenedWithOwnerPassword = false;

        // Let the parser resolve indirect /Length refs on stream dicts by
        // calling back into our object cache — needed for PDFs (notably
        // LibreOffice output) that write the length as an indirect ref.
        _parser.IndirectObjectResolver = ResolveLengthReference;

        Trailer = trailer;
        Version = version;

        // Load catalog. A hostile/truncated trailer may lack a valid /Root —
        // fail with a typed PdfParseException, not a raw KeyNotFound/cast. (#352)
        var catalogRef = trailer.GetReferenceOrNull("Root")
            ?? throw new PdfParseException("Trailer has no valid /Root reference");
        Catalog = GetObject(catalogRef) as PdfDictionary
            ?? throw new PdfParseException("Could not load document catalog");

        // Get info dictionary
        var infoRef = trailer.GetReferenceOrNull("Info");
        if (infoRef != null)
        {
            try
            {
                Info = GetObject(infoRef) as PdfDictionary;
            }
            catch (Exception __ex) when (__ex is not OutOfMemoryException)
            {
                Info = null;
            }
        }
    }

    /// <summary>
    /// Open a PDF document from a file.
    /// </summary>
    public static PdfDocument Open(string path, bool allowEncrypted = false)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return OpenCore(stream, ownsStream: true, allowEncrypted: allowEncrypted, userPassword: null);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open a password-protected PDF document from a file.
    /// </summary>
    /// <param name="path">Path to the PDF file.</param>
    /// <param name="userPassword">User password. <c>null</c> is treated as the empty password.</param>
    /// <param name="allowEncrypted">When true, unsupported encrypted PDFs are opened for inspection with ciphertext streams.</param>
    public static PdfDocument Open(string path, string? userPassword, bool allowEncrypted = false)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return OpenCore(stream, ownsStream: true, allowEncrypted: allowEncrypted, userPassword: userPassword);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open a PDF document from a stream.
    /// </summary>
    /// <param name="stream">Stream to read.</param>
    /// <param name="ownsStream">Whether the document should dispose the stream on close.</param>
    /// <param name="allowEncrypted">When false (default), opening an encrypted
    /// PDF throws <see cref="Excise.Core.Parsing.PdfEncryptionNotSupportedException"/>.
    /// excise cannot yet decrypt encrypted streams (tracked: GitHub #324).
    /// Without this guard, encrypted streams return ciphertext bytes — features
    /// like text extraction and redaction would silently produce wrong output.
    /// Pass true to bypass the guard for unencrypted-dict / encrypted-stream
    /// inspection at the caller's own risk.</param>
    public static PdfDocument Open(Stream stream, bool ownsStream = false, bool allowEncrypted = false)
        => OpenCore(stream, ownsStream, allowEncrypted, userPassword: null);

    /// <summary>
    /// Open a password-protected PDF document from a stream.
    /// </summary>
    /// <param name="stream">Stream to read.</param>
    /// <param name="userPassword">User password. <c>null</c> is treated as the empty password.</param>
    /// <param name="ownsStream">Whether the document should dispose the stream on close.</param>
    /// <param name="allowEncrypted">When true, unsupported encrypted PDFs are opened for inspection with ciphertext streams.</param>
    public static PdfDocument Open(Stream stream, string? userPassword, bool ownsStream = false, bool allowEncrypted = false)
        => OpenCore(stream, ownsStream, allowEncrypted, userPassword);

    /// <summary>
    /// Merge the cross-reference stream named by a classic trailer's /XRefStm
    /// (a "hybrid-reference file", PDF 32000-1 §7.5.8.4).
    /// </summary>
    /// <remarks>
    /// A hybrid-reference file carries BOTH a classic xref table and a
    /// cross-reference stream for the same revision. The classic table lists
    /// only what a pre-PDF-1.5 reader can use; objects living inside object
    /// streams can only be expressed in the stream, so the table simply omits
    /// them and the trailer points at the stream via /XRefStm.
    ///
    /// Ignoring the key does not produce a parse error — it produces a
    /// SUCCESSFUL parse of the wrong revision. The reader falls through to
    /// /Prev and resolves superseded definitions as current, silently (#872).
    /// For a redaction tool that is a document-identity bug: the reviewer sees
    /// content the author replaced, and redaction decisions are made against
    /// it.
    ///
    /// Precedence follows the same rule as the /Prev walk — an entry already
    /// established by a newer section is never overwritten. Within one hybrid
    /// section the classic table is merged first and the stream fills the gaps
    /// it structurally cannot express, which is what the spec's compatibility
    /// scheme intends and what other readers do.
    ///
    /// The stream's own /Prev is deliberately NOT followed: the containing
    /// classic trailer owns the chaining, and following both would walk the
    /// history twice.
    /// </remarks>
    private static void MergeHybridXRefStream(
        XRefParser xrefParser,
        Stream stream,
        PdfDictionary sectionTrailer,
        Dictionary<int, XRefEntry> fullXRef,
        HashSet<long> parsedHybridXRefStreams)
    {
        var xrefStmObj = sectionTrailer.GetOptional("XRefStm");
        if (xrefStmObj == null)
            return;

        long offset;
        try
        {
            offset = xrefStmObj.GetLong();
        }
        catch
        {
            return;   // a malformed /XRefStm is not worth failing the open over
        }

        if (offset <= 0 || offset >= stream.Length || !parsedHybridXRefStreams.Add(offset))
            return;

        Dictionary<int, XRefEntry> entries;
        try
        {
            entries = xrefParser.ParseDocumentXRef(offset).XRef;
        }
        catch (Exception ex) when (IsRecoverableIncrementalXRefException(ex))
        {
            // Best-effort: a broken hybrid stream leaves us exactly where we
            // were before this method existed, rather than failing the open.
            return;
        }

        foreach (var kvp in entries)
        {
            if (!fullXRef.ContainsKey(kvp.Key))
                fullXRef[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Whether the trailer's /Root names an object the assembled xref can
    /// actually produce. A direct (non-indirect) /Root dictionary needs no xref
    /// entry and counts as reachable; a missing or free entry does not (#884).
    /// </summary>
    private static bool RootIsReachable(PdfDictionary trailer, Dictionary<int, XRefEntry> xref)
    {
        var rootRef = trailer.GetReferenceOrNull("Root");
        if (rootRef == null)
            return trailer.GetOptional("Root") is PdfDictionary;

        return xref.TryGetValue(rootRef.ObjectNum, out var entry) && entry.InUse;
    }

    private static PdfDocument OpenCore(Stream stream, bool ownsStream, bool allowEncrypted, string? userPassword)
    {
        // Read PDF version from header
        string version = ReadVersion(stream);

        // Find and parse xref
        var xrefParser = new XRefParser(stream);
        var (trailer, xref) = xrefParser.ParseRootXRef();

        // Handle incremental updates (Prev pointer)
        var fullXRef = new Dictionary<int, XRefEntry>(xref);
        var currentTrailer = trailer;
        var parsedPreviousXRefs = new HashSet<long>();
        var parsedHybridXRefStreams = new HashSet<long>();

        // Hybrid-reference files (#872): the ROOT section may itself carry
        // /XRefStm, so this has to run before the /Prev walk, not just inside it.
        MergeHybridXRefStream(xrefParser, stream, trailer, fullXRef, parsedHybridXRefStreams);

        while (currentTrailer.GetReferenceOrNull("Prev") != null || currentTrailer.ContainsKey("Prev"))
        {
            var prevObj = currentTrailer.GetOptional("Prev");
            if (prevObj == null) break;

            // /Prev must be a direct integer byte offset (§7.5.5). An indirect
            // reference there made GetLong throw a raw InvalidCastException
            // out of Open (#960 deep sweep, seed 9603). Treat a non-number as
            // "no usable previous section" and stop walking, exactly as an
            // out-of-range offset already does below: a damaged trailer chain
            // costs the older revisions, not the document.
            if (!prevObj.TryGetNumber(out var prevNumber))
                break;

            long prevXRef = (long)prevNumber;
            if (prevXRef < 0 || prevXRef >= stream.Length || !parsedPreviousXRefs.Add(prevXRef))
                break;

            (PdfDictionary prevTrailer, Dictionary<int, XRefEntry> prevXRefEntries) previous;
            try
            {
                previous = xrefParser.ParseDocumentXRef(prevXRef);
            }
            catch (Exception ex) when (IsRecoverableIncrementalXRefException(ex))
            {
                break;
            }

            // Merge with previous xref (older entries don't override newer)
            foreach (var kvp in previous.prevXRefEntries)
            {
                if (!fullXRef.ContainsKey(kvp.Key))
                    fullXRef[kvp.Key] = kvp.Value;
            }

            // Each section in the chain may be hybrid-reference too.
            MergeHybridXRefStream(
                xrefParser, stream, previous.prevTrailer, fullXRef, parsedHybridXRefStreams);

            currentTrailer = previous.prevTrailer;
        }

        // The xref we just assembled is only usable if the catalog is reachable
        // through it. When it is not, rebuild by scanning the file for indirect
        // object headers (#884).
        //
        // pdfium/embedded_images.pdf is the case that needs this: 34 KB of a
        // file whose tail was cut off, so `startxref` (124724), the trailer's
        // /Prev (123786) and its /XRefStm (123449) all point past EOF. ParseXRef
        // throws, the repair path finds the file's terminal "xref 0 0" section
        // and SUCCEEDS with zero entries and a healthy-looking /Root 1 0 R, and
        // RepairUncompressedXRefOffsets can only rewrite entries that already
        // exist — it can never add one. Reconstruction was never reached, and it
        // would have worked: the catalog is at offset 17 and objects 1-15 are
        // intact. mutool and pdftocairo both render the page.
        //
        // This runs AFTER the /Prev walk, deliberately, and NOT inside
        // XRefParser.ParseRootXRef. A single xref SECTION of a healthy
        // incrementally-updated file legitimately omits the catalog — it lives
        // in an earlier section reached through /Prev — so only the assembled
        // table can answer "is the catalog reachable". Asking one section would
        // condemn a large class of perfectly good files.
        //
        // Merge, don't replace: reconstructed entries fill gaps only. Anything
        // the real xref defined (including entries it marks FREE) stays
        // authoritative, so a document is never silently rewound to a superseded
        // revision of an object the xref deliberately replaced.
        if (!RootIsReachable(trailer, fullXRef)
            && xrefParser.TryReconstructXRef(out var rebuiltTrailer, out var rebuiltXRef))
        {
            foreach (var kvp in rebuiltXRef)
            {
                if (!fullXRef.ContainsKey(kvp.Key))
                    fullXRef[kvp.Key] = kvp.Value;
            }

            // Only if the catalog is STILL unreachable is the trailer's own
            // /Root the thing that is wrong; then prefer the one recovered
            // alongside the rebuilt table.
            if (!RootIsReachable(trailer, fullXRef)
                && rebuiltTrailer.GetOptional("Root") is { } rebuiltRoot
                && RootIsReachable(rebuiltTrailer, fullXRef))
            {
                trailer["Root"] = rebuiltRoot;
            }
        }

        // Encrypted PDFs: try to build a security handler that decrypts
        // streams + strings as they're read. If /Encrypt is present and
        // we can verify the empty user password (the common case), we
        // continue with full decryption. If we can't (unsupported V/R,
        // wrong password), we honour `allowEncrypted` — true keeps
        // returning ciphertext for inspection; false (default) throws.
        Excise.Core.Security.PdfStandardSecurityHandler? handler = null;
        Excise.Core.Security.PdfPermissions? permissions = null;
        if (trailer.ContainsKey("Encrypt"))
        {
            try
            {
                // Resolve the /Encrypt dict (it can be an indirect ref).
                var encryptObj = trailer.GetOptional("Encrypt");
                if (encryptObj is PdfReference encryptRef)
                {
                    // Need to read the object directly from xref since
                    // we don't have a document yet.
                    try
                    {
                        encryptObj = ReadIndirectObjectAt(stream, fullXRef[encryptRef.ObjectNum].Offset);
                    }
                    catch (Exception ex) when (IsRecoverableMalformedEncryptObjectException(ex))
                    {
                        encryptObj = null;
                    }
                }
                if (encryptObj == null)
                    handler = null;
                else
                {
                    if (encryptObj is not PdfDictionary encryptDict)
                        throw new Excise.Core.Parsing.PdfParseException("/Encrypt is not a dictionary");

                    // Surface /P (#642) regardless of whether the security
                    // handler can be built — it's plain-integer metadata.
                    permissions = ReadPermissions(encryptDict);

                    // Some PDF 2.0 files encrypt only embedded-file streams
                    // (/EFF) while leaving normal document streams and strings
                    // on /Identity. Rendering/search/redaction of visible page
                    // content does not need a security handler in that case, and
                    // attempting password verification would wrongly reject an
                    // otherwise readable document.
                    if (!UsesIdentityCryptFiltersForDocumentContent(encryptDict))
                    {
                        // /ID is required; first element is what the security handler hashes.
                        // GetArrayOrNull, not GetArray: an encrypted file with no
                        // /ID at all is malformed and must refuse as the typed
                        // PdfParseException below, not as a raw
                        // KeyNotFoundException from the dictionary accessor. The
                        // "missing or empty" check under it was already written
                        // for this case but could never be reached (bug_644.pdf).
                        // GetDirect deliberately: this runs during trailer parsing, in a static
                        // context, before a document exists to resolve through. An indirect
                        // /ID would also be a chicken-and-egg problem -- /ID is needed to
                        // derive the encryption key that would decrypt the object holding it.
                        var idArr = trailer.GetDirectArrayOrNull("ID");
                        if (idArr is null || idArr.Count == 0 || idArr[0] is not PdfString idStr)
                            throw new Excise.Core.Parsing.PdfParseException("/ID array missing or empty");
                        var firstId = idStr.Bytes;

                        // Try the supplied user password. A null password is the
                        // same as the empty user password, by far the most common
                        // case for encrypted PDFs.
                        handler = Excise.Core.Security.PdfStandardSecurityHandler.Build(
                            encryptDict, firstId, userPassword);
                    }
                }
            }
            catch (Excise.Core.Parsing.PdfEncryptionNotSupportedException)
            {
                if (!allowEncrypted)
                {
                    if (ownsStream) stream.Dispose();
                    throw;
                }
                // allowEncrypted=true: caller wants the doc anyway, accept
                // that streams will be ciphertext.
                handler = null;
            }
        }

        // Create document (loads catalog internally)
        return new PdfDocument(stream, ownsStream, fullXRef, trailer, version, handler, permissions);
    }

    /// <summary>
    /// Decode /P from the /Encrypt dictionary (#642). Missing or
    /// non-numeric /P (malformed — /P is required for the Standard
    /// handler) fails open to all-allowed: /P is advisory policy metadata
    /// and mainstream readers treat a broken mask the same way.
    /// </summary>
    private static Excise.Core.Security.PdfPermissions ReadPermissions(PdfDictionary encryptDict)
    {
        try
        {
            var p = encryptDict.GetOptional("P");
            if (p is PdfInteger or PdfReal)
                return new Excise.Core.Security.PdfPermissions(p.GetLong());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // fall through to all-allowed
        }
        return Excise.Core.Security.PdfPermissions.AllAllowed;
    }

    private static bool UsesIdentityCryptFiltersForDocumentContent(PdfDictionary encryptDict)
        => string.Equals(encryptDict.GetNameOrNull("StmF"), "Identity", StringComparison.Ordinal)
           && string.Equals(encryptDict.GetNameOrNull("StrF"), "Identity", StringComparison.Ordinal);

    private static bool IsRecoverableIncrementalXRefException(Exception ex)
        => ex is PdfParseException or FormatException or OverflowException or KeyNotFoundException;

    private static bool IsRecoverableMalformedEncryptObjectException(Exception ex)
        => ex is PdfParseException { Message: var message }
           && (message.Contains("Unexpected keyword", StringComparison.Ordinal)
               || message.Contains("Expected object number", StringComparison.Ordinal)
               || message.Contains("Expected generation number", StringComparison.Ordinal)
               || message.Contains("Expected 'obj'", StringComparison.Ordinal)
               || message.Contains("Unterminated dictionary", StringComparison.Ordinal));

    /// <summary>
    /// One-shot reader used by <see cref="Open(Stream, bool, bool)"/> to
    /// resolve an indirect /Encrypt reference *before* the PdfDocument
    /// (and therefore the parser's resolver) exists. Seeks to the given
    /// offset, parses an indirect object, returns its value.
    /// </summary>
    private static PdfObject ReadIndirectObjectAt(Stream stream, long offset)
    {
        var lexer = new Excise.Core.Parsing.PdfLexer(stream, ownsStream: false);
        lexer.Seek(offset);
        var parser = new Excise.Core.Parsing.PdfParser(lexer);
        return parser.ParseIndirectObject().Value;
    }

    /// <summary>
    /// Open a PDF document from a byte array.
    /// </summary>
    public static PdfDocument Open(byte[] data, bool allowEncrypted = false)
    {
        return OpenCore(new MemoryStream(data, writable: false), ownsStream: true, allowEncrypted: allowEncrypted, userPassword: null);
    }

    /// <summary>
    /// Open a password-protected PDF document from a byte array.
    /// </summary>
    /// <param name="data">PDF bytes.</param>
    /// <param name="userPassword">User password. <c>null</c> is treated as the empty password.</param>
    /// <param name="allowEncrypted">When true, unsupported encrypted PDFs are opened for inspection with ciphertext streams.</param>
    public static PdfDocument Open(byte[] data, string? userPassword, bool allowEncrypted = false)
    {
        return OpenCore(new MemoryStream(data, writable: false), ownsStream: true, allowEncrypted: allowEncrypted, userPassword: userPassword);
    }

    /// <summary>
    /// Create a new empty in-memory PDF document. The returned document
    /// has a <c>/Catalog</c>, an empty <c>/Pages</c> tree, and no pages.
    /// Use <see cref="Pages"/>.<see cref="PageCollection.AddBlank"/> to
    /// append pages.
    /// </summary>
    /// <remarks>
    /// Implementation goes through a <c>Open(bytes)</c> round-trip so the
    /// new document is fully initialized with parser / xref / object
    /// cache in the same shape as a document loaded from disk — mutation
    /// paths then work identically on freshly-created and loaded docs.
    /// </remarks>
    public static PdfDocument CreateNew(string version = "1.7")
    {
        return Open(BuildMinimalEmptyPdfBytes(version));
    }

    /// <summary>
    /// Raw-bytes writer that produces a minimal valid empty PDF: header,
    /// catalog object, empty pages object, xref, trailer. Just enough
    /// for the parser to accept and for AddBlank to latch onto.
    /// </summary>
    private static byte[] BuildMinimalEmptyPdfBytes(string version)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new System.Text.UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine($"%PDF-{version}");
        w.Flush();

        var offsets = new long[3];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj");
        w.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj");
        w.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        w.WriteLine("endobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 3");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 2; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine("trailer");
        w.WriteLine("<< /Root 1 0 R /Size 3 >>");
        w.WriteLine("startxref");
        w.WriteLine(xrefPos.ToString());
        w.WriteLine("%%EOF");
        w.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Read the PDF version from the header.
    /// </summary>
    private static string ReadVersion(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[Math.Min(1024, Math.Max(20, (int)Math.Min(stream.Length, 1024)))];
        int read = stream.Read(buffer, 0, buffer.Length);

        var header = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
        var headerStart = header.IndexOf("%PDF-", StringComparison.Ordinal);
        if (headerStart < 0)
            return "0.0";

        // Extract version (e.g., "1.4", "1.7", "2.0")
        int idx = headerStart + 5;
        while (idx < header.Length && (char.IsDigit(header[idx]) || header[idx] == '.'))
            idx++;

        var version = header.Substring(headerStart + 5, idx - (headerStart + 5));
        return IsValidPdfVersion(version) ? version : "0.0";
    }

    private static bool IsValidPdfVersion(string version)
    {
        if (version.Length != 3 || version[1] != '.')
            return false;

        return char.IsDigit(version[0]) && char.IsDigit(version[2]);
    }

    /// <summary>
    /// Get a page by number (1-based).
    /// </summary>
    public PdfPage GetPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page number must be between 1 and {PageCount}");

        // Delegate to the PageCollection, which already handles both
        // indirect-reference and inline-dictionary kids uniformly.
        return Pages[pageNumber - 1];
    }

    /// <summary>
    /// Find the indirect reference of a page (1-based) by walking the /Pages
    /// tree. Returns null if pages are inline rather than indirect (rare).
    /// Used by tagged-PDF authoring (/Pg) and form authoring.
    /// </summary>
    internal PdfReference? GetPageReference(int pageNumber)
    {
        var pagesObj = Catalog.GetOptional("Pages");
        if (pagesObj == null || Resolve(pagesObj) is not PdfDictionary pages) return null;
        int target = pageNumber - 1, counter = 0;
        return WalkPageKids(pages, ref counter, target);
    }

    private PdfReference? WalkPageKids(PdfDictionary node, ref int counter, int target)
    {
        var kidsObj = node.GetOptional("Kids");
        if (kidsObj == null || Resolve(kidsObj) is not PdfArray kids) return null;
        foreach (var kidObj in kids)
        {
            var kid = Resolve(kidObj) as PdfDictionary;
            if (kid == null) continue;
            if (kid.GetNameOrNull("Type") == "Pages")
            {
                var found = WalkPageKids(kid, ref counter, target);
                if (found != null) return found;
            }
            else
            {
                if (counter == target) return kidObj as PdfReference;
                counter++;
            }
        }
        return null;
    }

    /// <summary>
    /// Get all pages.
    /// </summary>
    public IEnumerable<PdfPage> GetPages()
    {
        for (int i = 1; i <= PageCount; i++)
        {
            yield return GetPage(i);
        }
    }

    /// <summary>
    /// Find a page in the page tree by index.
    /// </summary>
    private PdfDictionary FindPage(PdfDictionary node, int targetIndex, int currentIndex)
    {
        var type = node.GetNameOrNull("Type");

        if (type == "Page")
        {
            if (currentIndex == targetIndex)
                return node;
            throw new PdfParseException($"Page index mismatch: expected {targetIndex}, at {currentIndex}");
        }

        // It's a Pages node
        var kids = node.GetArray("Kids");
        int index = currentIndex;

        foreach (var kidRef in kids)
        {
            if (kidRef is not PdfReference kr)
                throw new PdfParseException("Invalid page tree: kid is not a reference");

            var kid = GetObject(kr) as PdfDictionary
                ?? throw new PdfParseException("Invalid page tree: kid is not a dictionary");

            var kidType = kid.GetNameOrNull("Type");

            if (kidType == "Page")
            {
                if (index == targetIndex)
                    return kid;
                index++;
            }
            else
            {
                // Pages node
                int count = kid.GetInt("Count");
                if (targetIndex >= index && targetIndex < index + count)
                {
                    return FindPage(kid, targetIndex, index);
                }
                index += count;
            }
        }

        throw new PdfParseException($"Could not find page {targetIndex}");
    }

    /// <summary>
    /// Get an object by reference.
    /// </summary>
    public PdfObject GetObject(PdfReference reference)
    {
        return GetObject(reference.ObjectNum);
    }

    /// <summary>
    /// Get an object by object number.
    /// </summary>
    public PdfObject GetObject(int objectNumber)
    {
      // Serialize the shared-stream seek/parse + cache mutation against concurrent
      // readers (reentrant for recursive resolution). (#376)
      lock (_parseLock)
      {
        // Check cache
        if (_objectCache.TryGetValue(objectNumber, out var cached))
            return cached;

        // Find in xref.
        //
        // An object number with no xref entry is NOT an error. PDF 32000-1
        // §7.3.10: "An indirect reference to an undefined object shall not be
        // considered an error by a conforming reader; it shall be treated as a
        // reference to the null object."
        //
        // So throwing here was spec-incorrect, and the inconsistency was
        // visible two lines down — a FREE entry (the same condition, just
        // recorded rather than omitted) already returned null. Files whose xref
        // omits an object were condemned at open while mutool and pdftocairo
        // read them (#884).
        //
        // Returning null can turn a hard failure into a page that renders
        // without some content. That is the better failure: the
        // missing-content gate (#883) catches a blank region, whereas a refused
        // document produces nothing to inspect at all.
        if (!_xref.TryGetValue(objectNumber, out var entry))
            return PdfNull.Instance;

        if (!entry.InUse)
            return PdfNull.Instance;

        PdfObject obj;

        if (entry.IsCompressed)
        {
            // Object is in an object stream. The parent /ObjStm itself is
            // decrypted by this same code path when GetObjectFromStream
            // calls back into GetObject(streamNumber); the contained
            // objects are then plaintext and need no further decryption.
            obj = GetObjectFromStream(entry.ObjectStreamNumber!.Value, objectNumber);
        }
        else
        {
            // Regular object.
            //
            // A malformed object costs THAT OBJECT, not the document (#973).
            // pdfium's bug_481363.pdf writes `6 0 obj [ /Lab 4< /WhitePoint ...`
            // — the stray `4` turns the `<<` into a hex string, and the lexer
            // hits '/' where a hex digit must be. That object is the page's
            // /ColorSpace /CS1, so the throw propagated out of a resource
            // lookup and refused the whole page, while mutool and pdftocairo
            // both render one.
            //
            // Degrading to null is the same posture — and the same reasoning —
            // as the missing-xref-entry branch above: §7.3.10 already makes an
            // unresolvable reference the null object, and a page that renders
            // without one resource is inspectable where a refused document is
            // not. Only PdfParseException is caught: an OutOfMemoryException or
            // a bug in our own parser must still surface.
            //
            // IsResourceGuard is excluded on purpose. A recursion-depth trip
            // (#969/#971) is excise defending itself against hostile input, not
            // evidence that the rest of the file is readable; silently nulling
            // those objects would let a crafted file delete content at scale
            // and would replace "maximum nesting depth exceeded" with a
            // downstream "could not load document catalog".
            _parser.Seek(entry.Offset);
            PdfIndirectObject indirectObj;
            try
            {
                indirectObj = _parser.ParseIndirectObject();
            }
            catch (PdfParseException ex) when (!ex.IsResourceGuard)
            {
                _objectCache[objectNumber] = PdfNull.Instance;
                return PdfNull.Instance;
            }

            obj = indirectObj.Value;

            // Apply the security handler before any /Filter pipeline.
            // For RC4: ciphertext is stream's encoded bytes (post-compression
            // on encrypt, so we decrypt FIRST and then run FlateDecode etc.).
            // Strings inside the parsed object are also encrypted with the
            // same per-object key — walk the dict and decrypt them in place.
            // The /Encrypt dict itself is exempt (its strings are read with
            // a one-shot lexer in Open() before we have a handler).
            if (_securityHandler != null && !IsExemptFromEncryption(obj))
            {
                int objNum = indirectObj.ObjectNumber;
                int gen = indirectObj.Generation;

                if (obj is PdfStream stream)
                {
                    // §7.4.10: a stream may override the document's default
                    // crypt filter with /Crypt /Name /Identity. Its bytes are
                    // deliberately plaintext, so passing them to AES-CBC
                    // would turn a valid stream into the #1048-style "input
                    // data is not a complete block" failure (#1167).
                    //
                    // The /Crypt filter is an encryption stage, not a content
                    // filter. Remove this no-op stage before the normal
                    // decompressor runs; it has no /Crypt decoder and must
                    // only receive actual content filters such as FlateDecode.
                    if (!RemoveIdentityCryptFilter(stream))
                    {
                        var encrypted = stream.EncodedData;
                        var decrypted = _securityHandler.DecryptStream(objNum, gen, encrypted);
                        stream.SetEncodedData(decrypted);
                    }
                }
                DecryptStringsInPlace(obj, objNum, gen);
            }

            // Decompress streams
            if (obj is PdfStream s && s.IsFiltered)
            {
                // §7.3.8 makes every PDF stream an indirect object, so a
                // conforming /DecodeParms << /JBIG2Globals n 0 R >> is ALWAYS
                // a reference — resolve it before the filter pipeline runs, or
                // the JBIG2 decoder never sees its shared symbol dictionary
                // (#874).
                ResolveJbig2GlobalsReferences(s);

                try
                {
                    _decompressor.Decompress(s);
                }
                catch (Exception __ex) when (__ex is not OutOfMemoryException)
                {
                    // Some streams can't be decompressed (images, etc.) - that's OK
                }
            }
        }

        _objectCache[objectNumber] = obj;
        return obj;
      }
    }

    // ── Object-stream materialization cache (#743, epic #596) ───────────────
    // GetObjectFromStream used to re-parse the full N-pair /ObjStm index and
    // construct a fresh PdfParser + PdfLexer + MemoryStream for EVERY
    // contained-object fetch — 76,233 parser instantiations + index re-parses
    // on one save of irs-1040-instructions.pdf (785 streams), 67% of the save
    // workflow and ~3.2 GB of managed allocation (#597 baseline, see
    // docs/performance-baselines/2026-07-25-hotpath-baseline/). Instead, the
    // first touch of an object stream parses the index once and batch-parses
    // all N contained objects in a single pass; subsequent fetches are O(1)
    // array lookups. Results are keyed by (stream, index) exactly like the
    // old per-fetch path — the same byte offsets are parsed with the same
    // parser configuration, just once instead of once per fetch — so object
    // resolution is value-identical. This is caching only.
    //
    // Thread-safety: read and mutated only inside GetObjectFromStream, which
    // is reachable only from GetObject under _parseLock (same discipline as
    // _objectCache). Invalidation: an entry is keyed to the container
    // PdfStream instance and its decoded buffer; if the container object or
    // its decoded bytes are ever replaced (e.g. RemoveObject then re-parse),
    // the identity check misses and the index is rebuilt from the new bytes.
    private sealed class ObjectStreamCacheEntry
    {
        public required PdfStream Source { get; init; }
        public required byte[] Data { get; init; }
        public required int First { get; init; }
        public required (int ObjNum, int Offset)[] Offsets { get; init; }
        public required PdfObject?[] Objects { get; init; }

        /// <summary>
        /// Slot of each contained object number, built from the /ObjStm's own
        /// N-pair index. This — not the xref's index-in-stream — is what
        /// <see cref="GetObjectFromStream"/> resolves against (#869).
        /// </summary>
        public required Dictionary<int, int> SlotByObjectNumber { get; init; }
    }

    private readonly Dictionary<int, ObjectStreamCacheEntry> _objectStreamCache = new();

    /// <summary>
    /// Get an object from an object stream.
    /// </summary>
    private PdfObject GetObjectFromStream(int streamNumber, int objectNumber)
    {
        // Get the object stream
        var streamObj = GetObject(streamNumber) as PdfStream
            ?? throw new PdfParseException($"Object stream {streamNumber} not found");

        // Ensure it's decoded
        if (!streamObj.IsDecoded)
        {
            _decompressor.Decompress(streamObj);
        }

        var data = streamObj.DecodedData;

        // Parse the index and batch-materialize on first touch (#743).
        if (!_objectStreamCache.TryGetValue(streamNumber, out var cached)
            || !ReferenceEquals(cached.Source, streamObj)
            || !ReferenceEquals(cached.Data, data))
        {
            cached = MaterializeObjectStream(streamObj, data);
            _objectStreamCache[streamNumber] = cached;
        }

        // Locate the slot BY OBJECT NUMBER, not by the xref's index-in-stream
        // (#869).
        //
        // A type-2 xref entry's third field is the object's index within the
        // /ObjStm, and in an xref STREAM the width of that field comes from /W.
        // pdfjs/bug1978317.pdf declares /W [1 3 2] over 65,564 objects: field 3
        // holds two bytes, so every index >= 65536 wraps. Its catalog really
        // sits at slot 65541 of /ObjStm 65547, which the xref records as 5 —
        // and slot 5 is a /Type /Annot /Subtype /Link. Positional lookup
        // therefore returned a link annotation AS THE CATALOG, with no error,
        // and the document died two steps later on "no Pages dictionary" while
        // qpdf, mutool and pdftocairo all read it.
        //
        // The /ObjStm's own N-pair index names the object numbers it carries
        // (ISO 32000-2 7.5.7) and is the authority here; the xref's index is a
        // shortcut that a narrow /W, a bad producer, or a hostile file can make
        // wrong. Resolving by number costs one dictionary lookup and cannot
        // return a DIFFERENT object than the one asked for — which is the real
        // defect: silently substituting one object for another is worse than
        // any parse error, because nothing downstream can detect it.
        if (!cached.SlotByObjectNumber.TryGetValue(objectNumber, out var slot))
        {
            // This stream does not contain the requested object, whatever the
            // xref claims. Return null rather than whatever happens to occupy
            // slot `index` — the same choice, for the same reason, as an xref
            // entry that is missing altogether (see GetObject above): a page
            // that renders without some content can be inspected, a confidently
            // wrong object cannot.
            return PdfNull.Instance;
        }

        // The batch pass already parsed this slot; a null slot means that one
        // object failed to parse — retry it here so the caller sees the same
        // exception the old per-fetch path would have thrown.
        var obj = cached.Objects[slot];
        if (obj != null)
            return obj;

        using var retryParser = new PdfParser(data);
        retryParser.Seek(cached.First + cached.Offsets[slot].Offset);
        obj = retryParser.ParseObject();
        cached.Objects[slot] = obj;
        return obj;
    }

    /// <summary>
    /// Parse an /ObjStm's N-pair index once and batch-parse all N contained
    /// objects with a single parser pass (#743). Per-object parse failures
    /// leave the slot null; the failure is surfaced only if that specific
    /// object is requested, matching the old lazy per-fetch behavior on
    /// malformed streams.
    /// </summary>
    private static ObjectStreamCacheEntry MaterializeObjectStream(PdfStream streamObj, byte[] data)
    {
        // /N and /First are REQUIRED by §7.5.7, and GetInt throws a raw
        // KeyNotFoundException when they are absent — which escaped
        // PdfDocument.Open on a mutated object stream (#974, found by the
        // #960 token fuzzer). An object stream missing its index is
        // unparseable, so this is a genuine refusal; it just has to be typed.
        if (!streamObj.ContainsKey("N") || !streamObj.ContainsKey("First"))
            throw new PdfParseException(
                "Object stream is missing the required /N or /First entry");

        int n = streamObj.GetInt("N"); // Number of objects
        int first = streamObj.GetInt("First"); // Offset to first object

        // /N is attacker-controlled and sizes an allocation. An index entry is
        // two integer tokens, so it cannot occupy fewer than 2 bytes of the
        // decoded stream even at its most compact ("0 0"); anything claiming
        // more entries than that is lying and would otherwise reach
        // `new[n]` with n up to int.MaxValue — a raw OverflowException
        // ("Array dimensions exceeded supported range") out of
        // PdfDocument.Open, or an OOM on a slightly smaller lie
        // (#974, found by the #960 token fuzzer).
        if (n < 0 || (long)n * 2 > data.Length)
            throw new PdfParseException(
                $"Object stream declares /N {n}, which does not fit its {data.Length}-byte index");

        // Parse the index (pairs of object number and byte offset)
        using var parser = new PdfParser(data);
        var offsets = new (int ObjNum, int Offset)[n];

        for (int i = 0; i < n; i++)
        {
            var objNumToken = parser.Lexer.NextToken();
            var offsetToken = parser.Lexer.NextToken();

            if (objNumToken.Type != PdfTokenType.Integer || offsetToken.Type != PdfTokenType.Integer)
                throw new PdfParseException("Invalid object stream index");

            offsets[i] = (
                int.Parse(objNumToken.Value),
                int.Parse(offsetToken.Value)
            );
        }

        var objects = new PdfObject?[n];
        for (int i = 0; i < n; i++)
        {
            try
            {
                parser.Seek(first + offsets[i].Offset);
                objects[i] = parser.ParseObject();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Leave the slot null; surfaced on direct request.
            }
        }

        // Object number -> slot, first occurrence wins. A well-formed /ObjStm
        // never repeats an object number; if a damaged one does, the earlier
        // definition is the one the index's own ordering names.
        var slotByObjectNumber = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            slotByObjectNumber.TryAdd(offsets[i].ObjNum, i);

        return new ObjectStreamCacheEntry
        {
            Source = streamObj,
            Data = data,
            First = first,
            Offsets = offsets,
            Objects = objects,
            SlotByObjectNumber = slotByObjectNumber,
        };
    }

    // Guard against a hostile /JBIG2Globals reference cycle (an image whose
    // globals point back at an object currently being materialized would
    // otherwise recurse without limit). Keys are the TARGET object numbers of
    // resolutions currently in flight. Touched only under _parseLock.
    private readonly HashSet<int> _jbig2GlobalsResolutionsInFlight = new();

    /// <summary>
    /// Resolves an indirect <c>/DecodeParms /JBIG2Globals</c> reference on a
    /// stream into the referenced <see cref="PdfStream"/>, in place, so the
    /// /JBIG2Decode filter can hand the shared segments to the decoder (#874).
    /// ISO 32000-2 §7.3.8 requires streams to be indirect objects, so on any
    /// conforming file this entry is a <see cref="PdfReference"/>, never an
    /// inline stream. Runs after the owning object has fully parsed, so the
    /// re-entrant <see cref="GetObject(int)"/> seek cannot desync the shared
    /// lexer for the object being read.
    /// </summary>
    private void ResolveJbig2GlobalsReferences(PdfStream stream)
    {
        foreach (var parms in stream.DecodeParams)
        {
            if (parms?.GetOptional("JBIG2Globals") is not PdfReference reference)
                continue;

            if (!_jbig2GlobalsResolutionsInFlight.Add(reference.ObjectNum))
                continue;

            try
            {
                if (GetObject(reference.ObjectNum) is PdfStream globals)
                    parms["JBIG2Globals"] = globals;
            }
            catch (Exception __ex) when (__ex is not OutOfMemoryException)
            {
                // Leave the reference unresolved; the JBIG2 decode then fails
                // the same way it would on a file with no usable globals.
            }
            finally
            {
                _jbig2GlobalsResolutionsInFlight.Remove(reference.ObjectNum);
            }
        }
    }

    // Guard against a hostile indirect /Length cycle — the same shape as the
    // /JBIG2Globals guard above, on a far more reachable path (#969). A stream
    // whose /Length points at its own object (or at an object whose own
    // /Length points back) re-enters the parse of the object being parsed;
    // _objectCache cannot break it because an object is only cached AFTER its
    // parse completes. Keys are the TARGET object numbers of resolutions
    // currently in flight.
    private readonly HashSet<int> _lengthResolutionsInFlight = new();

    /// <summary>
    /// Parser callback for resolving indirect /Length refs on stream
    /// dicts. The parser saves and restores the lexer position around
    /// this call so we can safely re-enter <see cref="GetObject(int)"/>.
    /// </summary>
    /// <remarks>
    /// Re-entrant resolution of an object number already in flight returns
    /// null, which drops <c>PdfParser.ParseStream</c> onto its existing
    /// scan-to-<c>endstream</c> fallback — exactly the path an unresolvable
    /// /Length already takes. Before this guard the recursion was unbounded
    /// and killed the PROCESS with a StackOverflowException, which .NET
    /// cannot catch: no typed exception, no timeout, no corpus
    /// classification, on a 422-byte document (#969).
    /// </remarks>
    private PdfObject? ResolveLengthReference(int objectNumber)
    {
        // Locked explicitly rather than relying on the caller: unlike the
        // JBIG2 guard this runs from inside PdfParser.ParseStream, which is
        // also reachable from the constructor's trailer/xref-stream parse.
        // Monitor is reentrant, so nesting inside GetObject's lock is free.
        lock (_parseLock)
        {
            if (!_lengthResolutionsInFlight.Add(objectNumber)) return null;
            try { return GetObject(objectNumber); }
            catch (Exception __ex) when (__ex is not OutOfMemoryException) { return null; }
            finally { _lengthResolutionsInFlight.Remove(objectNumber); }
        }
    }

    /// <summary>
    /// Resolve a reference to its actual object.
    /// If the object is a reference, follows it. Otherwise returns the object itself.
    /// </summary>
    public PdfObject Resolve(PdfObject obj)
    {
        while (obj is PdfReference reference)
        {
            obj = GetObject(reference);
        }
        return obj;
    }

    /// <summary>
    /// Get document metadata title.
    /// </summary>
    public string? Title => Info?.GetStringOrNull("Title");

    /// <summary>
    /// Get document metadata author.
    /// </summary>
    public string? Author => Info?.GetStringOrNull("Author");

    /// <summary>
    /// Get document metadata subject.
    /// </summary>
    public string? Subject => Info?.GetStringOrNull("Subject");

    /// <summary>
    /// Get document metadata keywords.
    /// </summary>
    public string? Keywords => Info?.GetStringOrNull("Keywords");

    /// <summary>
    /// Get document metadata creator.
    /// </summary>
    public string? Creator => Info?.GetStringOrNull("Creator");

    /// <summary>
    /// Get document metadata producer.
    /// </summary>
    public string? Producer => Info?.GetStringOrNull("Producer");

    // ── Metadata / catalog authoring (#381) ──────────────────────────────────

    /// <summary>
    /// The document's natural language as a BCP 47 tag (catalog <c>/Lang</c>,
    /// e.g. <c>"en-US"</c>). Required by PDF/UA for accessible documents so
    /// screen readers pronounce content correctly. Setting <c>null</c> removes
    /// the entry. PDF spec §14.9.2.
    /// </summary>
    public string? Language
    {
        get => Catalog.GetStringOrNull("Lang");
        set
        {
            if (value == null) Catalog.Remove("Lang");
            else Catalog.SetString("Lang", value);
        }
    }

    /// <summary>Set the document title (Info <c>/Title</c>).</summary>
    public void SetTitle(string title) => EnsureInfo().SetString("Title", title ?? string.Empty);

    /// <summary>Set the document author (Info <c>/Author</c>).</summary>
    public void SetAuthor(string author) => EnsureInfo().SetString("Author", author ?? string.Empty);

    /// <summary>Set the document subject (Info <c>/Subject</c>).</summary>
    public void SetSubject(string subject) => EnsureInfo().SetString("Subject", subject ?? string.Empty);

    /// <summary>Set the document keywords (Info <c>/Keywords</c>).</summary>
    public void SetKeywords(string keywords) => EnsureInfo().SetString("Keywords", keywords ?? string.Empty);

    /// <summary>Set the creating application (Info <c>/Creator</c>).</summary>
    public void SetCreator(string creator) => EnsureInfo().SetString("Creator", creator ?? string.Empty);

    /// <summary>Set the producer (Info <c>/Producer</c>).</summary>
    public void SetProducer(string producer) => EnsureInfo().SetString("Producer", producer ?? string.Empty);

    /// <summary>
    /// Return the Info dictionary, creating and wiring it into the trailer
    /// (<c>/Info</c>) on first use. Newly created documents have no Info dict.
    /// </summary>
    private PdfDictionary EnsureInfo()
    {
        if (Info != null) return Info;
        var info = new PdfDictionary();
        var reference = AddIndirectObject(info);
        Trailer["Info"] = reference;
        Info = info;   // keep the read-side properties (Title/Author/…) in sync
        return info;
    }

    /// <summary>
    /// Get the document's interactive form (AcroForm), if present.
    /// Returns null if the document has no AcroForm.
    /// PDF spec §12.7.
    /// </summary>
    public PdfAcroForm? GetAcroForm()
    {
        var acroFormObj = Catalog.GetOptional("AcroForm");
        if (acroFormObj == null)
            return null;

        if (Resolve(acroFormObj) is not PdfDictionary acroFormDict)
            return null;

        return PdfAcroFormParser.Parse(this, acroFormDict);
    }

    /// <summary>
    /// Mark the AcroForm dictionary as requiring appearance stream regeneration
    /// (sets /NeedAppearances true). Called automatically by
    /// <see cref="PdfField.SetValue(string?)"/>; expose for callers that mutate
    /// field dictionaries directly.
    /// No-op if the document has no AcroForm.
    /// </summary>
    public void SetAcroFormNeedAppearances()
    {
        var acroFormObj = Catalog.GetOptional("AcroForm");
        if (acroFormObj == null) return;
        if (Resolve(acroFormObj) is not PdfDictionary acroFormDict) return;
        acroFormDict.SetBool("NeedAppearances", true);
    }

    /// <summary>
    /// Bake all current AcroForm field values into static page content and
    /// remove the interactive form. After flattening:
    ///   • Each text/choice field's /V is rendered as page content at the
    ///     widget's /Rect (using /DA appearance string when available, else
    ///     a default Helvetica 10 pt black);
    ///   • Each widget annotation is removed from its host page's /Annots
    ///     array;
    ///   • The /AcroForm catalog entry is removed;
    ///   • Any cached page state (letters, text) is invalidated so subsequent
    ///     reads see the baked content.
    ///
    /// Call <see cref="Save(Stream)"/> afterwards to persist.
    ///
    /// Signature fields are skipped (their visual representation comes from
    /// the signature appearance, not /V) and their widget annotations are
    /// preserved.
    /// </summary>
    public void FlattenAcroForm()
    {
        var form = GetAcroForm();
        if (form == null) return;

        AcroFormFlattener.Flatten(this, form);

        Catalog.Remove("AcroForm");
    }

    /// <summary>
    /// Check whether this document has embedded files (PDF 2.0 portfolios / associated files).
    /// Returns true if /Catalog/Names/EmbeddedFiles or legacy /Catalog/AF are present.
    /// </summary>
    public bool HasEmbeddedFiles
    {
        get
        {
            var namesObj = Catalog.GetOptional("Names");
            if (namesObj != null && Resolve(namesObj) is PdfDictionary namesDict)
                if (namesDict.GetOptional("EmbeddedFiles") != null)
                    return true;

            if (Catalog.GetOptional("AF") != null)
                return true;

            return false;
        }
    }

    /// <summary>
    /// Get the list of embedded files in this document.
    /// Returns an empty list if the document has no embedded files.
    /// Walks /Catalog/Names/EmbeddedFiles (PDF 2.0 name tree) and falls back to
    /// legacy /Catalog/Names/AF and /Catalog/AF arrays per PDF 2.0 §7.7.4.
    /// Cached after first call.
    /// </summary>
    public IReadOnlyList<PdfEmbeddedFile> GetEmbeddedFiles()
    {
        _embeddedFiles ??= PdfEmbeddedFileParser.ParseEmbeddedFiles(this);
        return _embeddedFiles;
    }

    /// <summary>
    /// Remove all embedded files from this document.
    /// Removes the /Catalog/Names/EmbeddedFiles entry and /Catalog/AF arrays if present.
    /// The embedded-file stream objects remain in the file until the next save rewrites
    /// the xref; they become unreferenced and the writer drops them.
    /// This operation is idempotent (safe to call multiple times).
    ///
    /// This is critical for redaction security when dealing with hybrid documents like
    /// ZUGFeRD e-invoices (bundled XML) or legal exhibit packages (source documents).
    /// After content-level redaction removes glyphs from the visible pages, ScrubEmbeddedFiles
    /// ensures the data is not also present in the attachment tree.
    ///
    /// The change is applied to the in-memory document; call Save afterwards to persist.
    /// </summary>
    public void ScrubEmbeddedFiles()
    {
        // Clear the cache so subsequent calls will see the updated state
        _embeddedFiles = null;

        // Remove modern PDF 2.0: /Catalog/Names/EmbeddedFiles
        var namesObj = Catalog.GetOptional("Names");
        if (namesObj != null && Resolve(namesObj) is PdfDictionary namesDict)
            namesDict.Remove("EmbeddedFiles");

        // Remove legacy: /Catalog/AF
        Catalog.Remove("AF");
    }

    /// <summary>
    /// Get the raw XMP metadata stream bytes, or null if the document has no
    /// /Metadata entry on the catalog. The bytes are the decoded XMP RDF/XML
    /// body. PDF spec §14.3.2 / XMP spec part 1 §7.6.
    /// </summary>
    public byte[]? GetXmpMetadata()
    {
        var metaObj = Catalog.GetOptional("Metadata");
        if (metaObj == null) return null;
        if (Resolve(metaObj) is not PdfStream stream) return null;
        try { return stream.DecodedData; }
        catch (Exception __ex) when (__ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// Remove all document-level metadata and optionally embedded files.
    /// Clears the Info dictionary keys (/Title /Author /Subject /Keywords /Creator
    /// /Producer /CreationDate /ModDate), removes the Catalog's /Metadata stream,
    /// and optionally scrubs embedded files (portfolios, associated files).
    ///
    /// This is critical for redaction: even after content-level redaction
    /// removes glyphs from the page body, the title, author, and attachments
    /// of the document still surface the redacted data to anyone viewing the
    /// file's properties, running pdfinfo, or extracting attachments.
    ///
    /// The change is applied to the in-memory document; call Save afterwards
    /// to persist.
    ///
    /// <param name="scrubAttachments">If true (default), also calls ScrubEmbeddedFiles
    /// to remove embedded files. For backwards compatibility, defaults to true.</param>
    /// </summary>
    public void ScrubMetadata(bool scrubAttachments = true)
    {
        // Wipe the legacy Info dictionary in place — keep the dict so xref
        // structure is preserved, just empty it.
        if (Info != null)
        {
            foreach (var key in InfoKeysToScrub)
                Info.Remove(key);
        }

        // Drop the XMP metadata stream from the catalog. The stream object
        // remains in the file until the next save rewrites the xref; the
        // catalog no longer points at it.
        Catalog.Remove("Metadata");

        // Optionally scrub embedded files (portfolios, associated files).
        if (scrubAttachments)
            ScrubEmbeddedFiles();
    }

    /// <summary>
    /// Selectively scrub Info-dict keys without touching XMP. Useful when
    /// the caller wants finer control (e.g. preserve /CreationDate but
    /// drop /Title).
    /// </summary>
    public void ScrubInfoKeys(params string[] keys)
    {
        if (Info == null || keys == null) return;
        foreach (var k in keys) Info.Remove(k);
    }

    private static readonly string[] InfoKeysToScrub = new[]
    {
        "Title", "Author", "Subject", "Keywords",
        "Creator", "Producer", "CreationDate", "ModDate",
        "Trapped"
    };

    #region Save Methods

    // Actions run just before serialization — used by embedded fonts to finalize
    // their FontFile2 subset once every glyph that will be drawn is known (#393).
    private readonly List<Action> _preSaveActions = new();

    /// <summary>
    /// Register an action to run immediately before the document is serialized.
    /// Idempotent actions only — it may run on each Save.
    /// </summary>
    internal void RegisterPreSaveAction(Action action) => _preSaveActions.Add(action);

    /// <summary>
    /// Find the indirect reference of a cached object instance (by identity), or
    /// null if it isn't a top-level indirect object. Used by tagged-PDF authoring
    /// to reference a widget annotation from the structure tree (/OBJR).
    /// </summary>
    internal PdfReference? GetReferenceTo(PdfObject obj)
    {
        foreach (var (num, cached) in _objectCache)
            if (ReferenceEquals(cached, obj))
                return new PdfReference(num, 0);
        return null;
    }

    /// <summary>
    /// Save the document to a stream. Writes an unencrypted file — even when
    /// the source was opened encrypted (see <see cref="IsEncrypted"/>). To
    /// keep an encrypted source encrypted, pass
    /// <see cref="GetReEncryptionOptions"/>'s result to
    /// <see cref="Save(Stream, Excise.Core.Security.PdfEncryptionOptions?)"/> (#643).
    /// The plaintext default is deliberate: dozens of flows (rendering,
    /// splitting, extraction) rely on "save = decrypt" being explicit, so
    /// nothing re-encrypts by surprise.
    /// </summary>
    public void Save(Stream outputStream) => Save(outputStream, encryptionOptions: null);

    /// <summary>
    /// Save the document to a stream, optionally encrypting the output with
    /// the PDF Standard Security Handler. <paramref name="encryptionOptions"/>
    /// of <c>null</c> writes plaintext (identical to <see cref="Save(Stream)"/>).
    /// Combine with <see cref="GetReEncryptionOptions"/> to preserve an
    /// encrypted source's protection across a redact/edit round-trip (#643).
    /// </summary>
    public void Save(Stream outputStream, Excise.Core.Security.PdfEncryptionOptions? encryptionOptions)
    {
        foreach (var action in _preSaveActions)
            action();
        var writer = new PdfDocumentWriter(this, encryptionOptions);
        writer.Write(outputStream);
    }

    /// <summary>
    /// Save the document to a byte array. Plaintext output — see
    /// <see cref="Save(Stream)"/>'s remarks.
    /// </summary>
    public byte[] SaveToBytes() => SaveToBytes(encryptionOptions: null);

    /// <summary>
    /// Save the document to a byte array, optionally encrypted — see
    /// <see cref="Save(Stream, Excise.Core.Security.PdfEncryptionOptions?)"/>.
    /// </summary>
    public byte[] SaveToBytes(Excise.Core.Security.PdfEncryptionOptions? encryptionOptions)
    {
        using var ms = new MemoryStream();
        Save(ms, encryptionOptions);
        return ms.ToArray();
    }

    /// <summary>
    /// Save the document to a file. Plaintext output — see
    /// <see cref="Save(Stream)"/>'s remarks.
    /// </summary>
    public void Save(string path) => Save(path, encryptionOptions: null);

    /// <summary>
    /// Save the document to a file, optionally encrypted — see
    /// <see cref="Save(Stream, Excise.Core.Security.PdfEncryptionOptions?)"/>.
    /// </summary>
    public void Save(string path, Excise.Core.Security.PdfEncryptionOptions? encryptionOptions)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Save(fs, encryptionOptions);
    }

    /// <summary>
    /// Encryption options that re-encrypt a save of this document with the
    /// same protection its source was opened with (#643): same algorithm
    /// where the writer supports it, same <c>/P</c> permission mask, same
    /// <c>/EncryptMetadata</c> choice. Returns <c>null</c> when the source
    /// was not encrypted — so <c>doc.Save(path, doc.GetReEncryptionOptions(pw))</c>
    /// is always safe: unencrypted sources stay unencrypted.
    /// </summary>
    /// <remarks>
    /// Algorithm mapping: V=5 R=6 sources round-trip as
    /// <see cref="Excise.Core.Security.PdfEncryptionAlgorithm.Aes256"/>; V=4 R=4
    /// AESV2 sources as <see cref="Excise.Core.Security.PdfEncryptionAlgorithm.Aes128"/>.
    /// Sources excise can decrypt but whose algorithm the writer does not emit
    /// (RC4: V=1 R=2, V=2 R=3, and V=4 R=4 with CFM=V2) are re-encrypted as
    /// AES-256 — always an upgrade, never a downgrade. The same upgrade
    /// applies when the source's /Encrypt could not be fully parsed but the
    /// trailer says the file is encrypted.
    ///
    /// The owner password of the source cannot be recovered from a
    /// user-password open (#324 — excise verifies user passwords only), so the
    /// returned options reuse <paramref name="userPassword"/> as the owner
    /// password: nobody gains authority they did not already have, and the
    /// legitimate holder of the user password is not locked out of their own
    /// re-saved file. A source opened with the empty password re-encrypts
    /// with the empty password.
    /// </remarks>
    /// <param name="userPassword">
    /// The password this document was opened with (<c>null</c>/empty for the
    /// empty user password — the common case). The caller supplies it because
    /// the document does not retain the password text after open.
    /// </param>
    public Excise.Core.Security.PdfEncryptionOptions? GetReEncryptionOptions(string? userPassword)
    {
        if (!IsEncrypted) return null;

        var algorithm = _securityHandler switch
        {
            { V: 5, R: 6 } => Excise.Core.Security.PdfEncryptionAlgorithm.Aes256,
            { V: 4, R: 4, UsesAes: true } => Excise.Core.Security.PdfEncryptionAlgorithm.Aes128,
            // RC4 variants (V=1/2, V=4 CFM=V2) and unparseable /Encrypt:
            // upgrade to the PDF 2.0 native algorithm.
            _ => Excise.Core.Security.PdfEncryptionAlgorithm.Aes256,
        };

        return new Excise.Core.Security.PdfEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = userPassword,
            Permissions = Permissions.RawValue,
            EncryptMetadata = _securityHandler?.EncryptMetadata ?? true,
            Algorithm = algorithm,
        };
    }

    /// <summary>
    /// Walk the parsed object tree and decrypt every <see cref="PdfString"/>
    /// in place. Each indirect object has its own RC4 keystream derived
    /// from (objNum, gen) — strings inside the same indirect object share
    /// that keystream regardless of how deeply they're nested in dicts
    /// or arrays.
    /// </summary>
    /// <summary>
    /// §7.5.8.2 / §7.6.2: streams that are NEVER encrypted, so the security
    /// handler must skip them entirely — body AND dictionary strings. Applying
    /// AES-CBC to their unencrypted bytes throws "input data is not a complete
    /// block" (#1048, hit on Save's <see cref="GetAllObjects"/> which is the
    /// first path to touch the xref-stream object). Skipping the string pass
    /// too is load-bearing: an xref stream's dictionary IS the trailer and
    /// carries /ID, and that ID feeds R=4 key derivation — "decrypting" it
    /// would silently write an undecryptable file, worse than the crash.
    ///   • cross-reference streams (/Type /XRef) — always exempt.
    ///   • the /Metadata stream when /EncryptMetadata is explicitly false
    ///     (absent means true, i.e. still encrypted — do NOT exempt then).
    ///
    /// Per-stream <c>/Crypt /Name /Identity</c> is intentionally NOT listed
    /// here. It exempts the stream bytes, not ordinary strings in that
    /// indirect object's dictionary: those remain governed by the document's
    /// <c>/StrF</c> filter. See <see cref="RemoveIdentityCryptFilter"/>.
    /// </summary>
    private bool IsExemptFromEncryption(PdfObject obj)
    {
        if (obj is not PdfStream stream) return false;
        var type = stream.GetNameOrNull("Type");
        if (type == "XRef") return true;
        if (type == "Metadata" && _securityHandler is { EncryptMetadata: false }) return true;
        return false;
    }

    /// <summary>
    /// Finds a per-stream <c>/Filter /Crypt</c> stage with
    /// <c>/DecodeParms &lt;&lt; /Name /Identity &gt;&gt;</c>, removes that no-op
    /// stage from the filter pipeline, and returns whether it was present.
    ///
    /// <para>PDF's array-valued <c>/Filter</c> and <c>/DecodeParms</c> entries
    /// are positional (§7.4): remove the matching decode-parameter element
    /// with the matching filter. Direct parameter dictionaries are the only
    /// representation that reaches this point; <see cref="PdfParser"/>
    /// resolves indirect <c>/DecodeParms</c> while parsing the stream.</para>
    /// </summary>
    private static bool RemoveIdentityCryptFilter(PdfStream stream)
    {
        var filters = stream.Filters;
        if (filters.Count == 0)
            return false;

        var parameters = stream.DecodeParams;
        var keptFilters = new List<PdfObject>(filters.Count);
        var keptParameters = new List<PdfObject>(filters.Count);
        var removedIdentityCrypt = false;

        for (var i = 0; i < filters.Count; i++)
        {
            var parameter = i < parameters.Count ? parameters[i] : null;
            var isIdentityCrypt = filters[i] == "Crypt"
                && parameter?.GetNameOrNull("Name") == "Identity";

            if (isIdentityCrypt)
            {
                removedIdentityCrypt = true;
                continue;
            }

            keptFilters.Add(new PdfName(filters[i]));
            keptParameters.Add((PdfObject?)parameter ?? PdfNull.Instance);
        }

        if (!removedIdentityCrypt)
            return false;

        if (keptFilters.Count == 0)
        {
            stream.Remove("Filter");
            stream.Remove("DecodeParms");
            return true;
        }

        stream["Filter"] = keptFilters.Count == 1 ? keptFilters[0] : new PdfArray(keptFilters);

        // A missing DecodeParms entry is semantically different from an array
        // containing only nulls, so retain it only when one existed originally.
        if (stream.ContainsKey("DecodeParms"))
            stream["DecodeParms"] = keptParameters.Count == 1
                ? keptParameters[0]
                : new PdfArray(keptParameters);

        return true;
    }

    private void DecryptStringsInPlace(PdfObject root, int objNum, int gen)
    {
        if (_securityHandler == null) return;

        // BFS via stack to avoid recursion depth on pathological PDFs.
        var stack = new Stack<PdfObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case PdfString str:
                    str.ReplaceBytes(_securityHandler.DecryptString(objNum, gen, str.Bytes));
                    break;
                case PdfDictionary dict:
                    foreach (var kv in dict)
                        stack.Push(kv.Value);
                    break;
                case PdfArray arr:
                    foreach (var item in arr) stack.Push(item);
                    break;
                // Streams: their dict's strings still need decryption,
                // so recurse into the dict portion. The encoded data is
                // handled separately by the caller.
                // (PdfStream inherits from PdfDictionary so the dict
                // case above already covers it — guard just in case.)
            }
        }
    }

    /// <summary>
    /// True if this document was opened with a working security handler
    /// (i.e. encryption is being decrypted transparently).
    /// </summary>
    public bool IsDecrypting => _securityHandler != null;

    /// <summary>
    /// Get all objects in the document (for writing).
    /// </summary>
    internal IEnumerable<(int ObjectNumber, int Generation, PdfObject Object)> GetAllObjects()
    {
        foreach (var kvp in _xref)
        {
            if (kvp.Value.InUse)
            {
                var obj = GetObject(kvp.Key);
                yield return (kvp.Key, kvp.Value.Generation, obj);
            }
        }
    }

    /// <summary>
    /// Get the catalog reference for writing.
    /// </summary>
    internal PdfReference GetCatalogReference()
    {
        return Trailer.Get<PdfReference>("Root");
    }

    #endregion

    /// <summary>
    /// Get the page label for a given page number (1-based).
    /// Returns the formatted label string (e.g., "i", "1", "A-1"), or null if no labels defined.
    /// </summary>
    public string? GetPageLabel(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            return null;

        _pageLabelCache ??= PdfPageLabelParser.ParsePageLabels(this);

        if (_pageLabelCache.Count == 0)
            return null;

        // Find the label definition that applies to this page (0-based index)
        int pageIndex = pageNumber - 1;
        int applicableIndex = -1;

        // Find the highest index <= pageIndex that has a label definition
        foreach (var key in _pageLabelCache.Keys.OrderBy(k => k))
        {
            if (key <= pageIndex)
                applicableIndex = key;
            else
                break;
        }

        if (applicableIndex < 0)
            return null;

        var label = _pageLabelCache[applicableIndex];
        int offset = pageIndex - applicableIndex;
        return label.Format(offset);
    }

    /// <summary>
    /// Get all named destinations in the document.
    /// Returns an empty dictionary if no named destinations defined.
    /// </summary>
    public IReadOnlyDictionary<string, NamedDestination> GetNamedDestinations()
    {
        _namedDestinationsCache ??= BuildNamedDestinations();
        return _namedDestinationsCache;
    }

    /// <summary>
    /// Build the named destinations from the catalog.
    /// </summary>
    private Dictionary<string, NamedDestination> BuildNamedDestinations()
    {
        var result = new Dictionary<string, NamedDestination>();

        // Build page ref → page number map
        var pageRefToNumber = PdfOutlineParser.BuildPageRefMap(this);

        // Get the raw named destination objects (name → destination array or dict)
        var rawDests = PdfOutlineParser.BuildNamedDestinations(this);
        if (rawDests == null)
            return result;

        foreach (var kvp in rawDests)
        {
            var name = kvp.Key;
            var destObj = Resolve(kvp.Value) as PdfArray;
            if (destObj == null || destObj.Count == 0)
                continue;

            // First element is the page reference
            int? pageNumber = null;
            if (destObj[0] is PdfReference pageRef &&
                pageRefToNumber.TryGetValue((pageRef.ObjectNum, pageRef.Generation), out var pageNum))
            {
                pageNumber = pageNum;
            }

            // Parse the destination array: [page /Fit|/FitH|etc params...]
            var (fitMode, x, y, zoom) = ParseDestinationArray(destObj);

            var dest = new NamedDestination(
                Name: name,
                PageNumber: pageNumber,
                X: x,
                Y: y,
                Zoom: zoom,
                FitMode: fitMode);

            result[name] = dest;
        }

        return result;
    }

    /// <summary>
    /// Parse a destination array to extract fit mode and coordinates.
    /// Format: [page /FitMode param1 param2 ...]
    /// </summary>
    private static (string FitMode, double? X, double? Y, double? Zoom) ParseDestinationArray(PdfArray arr)
    {
        if (arr.Count < 2)
            return ("XYZ", null, null, null);

        var fitModeObj = arr[1];
        string fitMode = fitModeObj is PdfName name ? name.Value : "XYZ";

        // Parse parameters based on fit mode (ISO 32000-2 §12.3.2.2)
        // Note: PdfName.Value does not include the "/" prefix
        return fitMode switch
        {
            "Fit" => ("Fit", null, null, null),
            "FitH" => ("FitH", null, arr.Count > 2 ? GetNumber(arr[2]) : null, null),
            "FitV" => ("FitV", arr.Count > 2 ? GetNumber(arr[2]) : null, null, null),
            "FitB" => ("FitB", null, null, null),
            "FitBH" => ("FitBH", null, arr.Count > 2 ? GetNumber(arr[2]) : null, null),
            "FitBV" => ("FitBV", arr.Count > 2 ? GetNumber(arr[2]) : null, null, null),
            "FitR" => ("FitR",
                arr.Count > 2 ? GetNumber(arr[2]) : null,
                arr.Count > 3 ? GetNumber(arr[3]) : null,
                null),  // FitR has left, bottom, right, top but we simplify
            "XYZ" => ("XYZ",
                arr.Count > 2 ? GetNumber(arr[2]) : null,
                arr.Count > 3 ? GetNumber(arr[3]) : null,
                arr.Count > 4 ? GetNumber(arr[4]) : null),
            _ => ("XYZ", null, null, null)
        };
    }

    /// <summary>
    /// Extract a numeric value from a PDF object.
    /// </summary>
    private static double? GetNumber(PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            PdfNull => null,
            _ => null
        };
    }

    /// <summary>
    /// Cache for page labels (parsed on first access).
    /// </summary>
    private Dictionary<int, PdfPageLabel>? _pageLabelCache;

    /// <summary>
    /// Cache for named destinations (parsed on first access).
    /// </summary>
    private Dictionary<string, NamedDestination>? _namedDestinationsCache;

    /// <summary>
    /// The document-open action (/Catalog/OpenAction, ISO 32000-2:2020 §12.3.2 / §12.6).
    /// May be an action dictionary (e.g. a GoTo, or a JavaScript action run on open —
    /// never executed by excise) or, in legacy documents, a bare destination array.
    /// Null if the catalog has no /OpenAction.
    /// </summary>
    public PdfAction? OpenAction
    {
        get
        {
            if (!_openActionParsed)
            {
                _openActionCache = PdfActionParser.Parse(this, Catalog.GetOptional("OpenAction"));
                _openActionParsed = true;
            }
            return _openActionCache;
        }
    }
    private PdfAction? _openActionCache;
    private bool _openActionParsed;

    /// <summary>
    /// Document-level additional actions (/Catalog/AA, ISO 32000-2:2020 §12.6.3, Table 204).
    /// Keys are trigger names: "WC" (before close), "WS" (before save), "DS" (after save),
    /// "WP" (before print), "DP" (after print). Empty if the catalog has no /AA.
    /// Never executed by excise — parsed for round-trip and inspection only.
    /// </summary>
    public IReadOnlyDictionary<string, PdfAction> AdditionalActions
    {
        get
        {
            _additionalActionsCache ??= PdfActionParser.ParseAdditionalActions(this, Catalog.GetOptional("AA"));
            return _additionalActionsCache;
        }
    }
    private Dictionary<string, PdfAction>? _additionalActionsCache;

    /// <summary>
    /// Document-level JavaScript actions from the /Catalog/Names/JavaScript name tree
    /// (ISO 32000-2:2020 §7.7.4.3) — scripts a conforming viewer would run once, when
    /// the document is opened. Keyed by script name. Never executed by excise;
    /// <see cref="PdfAction.JavaScriptSource"/> exposes the decoded source for
    /// inspection/auditing only. Empty if the catalog has no such name tree.
    /// </summary>
    public IReadOnlyDictionary<string, PdfAction> DocumentJavaScriptActions
    {
        get
        {
            _documentJavaScriptCache ??= BuildDocumentJavaScriptActions();
            return _documentJavaScriptCache;
        }
    }
    private Dictionary<string, PdfAction>? _documentJavaScriptCache;

    private Dictionary<string, PdfAction> BuildDocumentJavaScriptActions()
    {
        var result = new Dictionary<string, PdfAction>();

        var namesObj = Catalog.GetOptional("Names");
        if (namesObj == null || Resolve(namesObj) is not PdfDictionary namesDict)
            return result;

        var jsObj = namesDict.GetOptional("JavaScript");
        if (jsObj == null || Resolve(jsObj) is not PdfDictionary jsRoot)
            return result;

        WalkJavaScriptNameTree(jsRoot, result);
        return result;
    }

    /// <summary>
    /// Walk a PDF name tree (§7.9.6) of JavaScript actions. Leaves have a /Names
    /// array of [name action name action ...] pairs; branches have /Kids subtrees.
    /// </summary>
    private void WalkJavaScriptNameTree(PdfDictionary node, Dictionary<string, PdfAction> result)
    {
        var namesArrObj = node.GetOptional("Names");
        if (namesArrObj != null && Resolve(namesArrObj) is PdfArray namesArr)
        {
            for (int i = 0; i + 1 < namesArr.Count; i += 2)
            {
                if (namesArr[i] is not PdfString nameStr) continue;
                var action = PdfActionParser.Parse(this, namesArr[i + 1]);
                if (action != null)
                    result[nameStr.Value] = action;
            }
        }

        var kidsObj = node.GetOptional("Kids");
        if (kidsObj != null && Resolve(kidsObj) is PdfArray kidsArr)
        {
            foreach (var kidObj in kidsArr)
            {
                if (Resolve(kidObj) is PdfDictionary kidDict)
                    WalkJavaScriptNameTree(kidDict, result);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _parser.Dispose();
        if (_ownsStream)
            _stream.Dispose();
    }
}
