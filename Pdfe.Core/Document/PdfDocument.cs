using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Pdfe.Core.Writing;
using System.Linq;

namespace Pdfe.Core.Document;

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
    private readonly Pdfe.Core.Security.PdfStandardSecurityHandler? _securityHandler;
    private PageCollection? _pages;
    private IReadOnlyList<PdfOcg>? _ocgs;
    private PdfOcgConfig? _ocgConfig;
    private PdfStructElement? _structureTree;
    private bool? _isTaggedPdf;
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
        _xref[next] = new Pdfe.Core.Parsing.XRefEntry
        {
            Offset = 0, // filled in by the writer at serialize time
            Generation = 0,
            InUse = true,
        };
        _objectCache[next] = obj;
        return new PdfReference(next, 0);
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
    {
        var reachable = new HashSet<int>();
        var stack = new Stack<PdfObject>();
        foreach (var v in Trailer.Values) stack.Push(v);

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
        Pdfe.Core.Security.PdfStandardSecurityHandler? securityHandler = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _xref = xref;
        _objectCache = new Dictionary<int, PdfObject>();
        _parser = new PdfParser(new PdfLexer(stream, ownsStream: false));
        _decompressor = new StreamDecompressor();
        _securityHandler = securityHandler;

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
    /// PDF throws <see cref="Pdfe.Core.Parsing.PdfEncryptionNotSupportedException"/>.
    /// pdfe cannot yet decrypt encrypted streams (tracked: GitHub #324).
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

        while (currentTrailer.GetReferenceOrNull("Prev") != null || currentTrailer.ContainsKey("Prev"))
        {
            var prevObj = currentTrailer.GetOptional("Prev");
            if (prevObj == null) break;

            long prevXRef = prevObj.GetLong();
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

            currentTrailer = previous.prevTrailer;
        }

        // Encrypted PDFs: try to build a security handler that decrypts
        // streams + strings as they're read. If /Encrypt is present and
        // we can verify the empty user password (the common case), we
        // continue with full decryption. If we can't (unsupported V/R,
        // wrong password), we honour `allowEncrypted` — true keeps
        // returning ciphertext for inspection; false (default) throws.
        Pdfe.Core.Security.PdfStandardSecurityHandler? handler = null;
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
                        throw new Pdfe.Core.Parsing.PdfParseException("/Encrypt is not a dictionary");

                    // Some PDF 2.0 files encrypt only embedded-file streams
                    // (/EFF) while leaving normal document streams and strings
                    // on /Identity. Rendering/search/redaction of visible page
                    // content does not need a security handler in that case, and
                    // attempting password verification would wrongly reject an
                    // otherwise readable document.
                    if (!UsesIdentityCryptFiltersForDocumentContent(encryptDict))
                    {
                        // /ID is required; first element is what the security handler hashes.
                        var idArr = trailer.GetArray("ID");
                        if (idArr.Count == 0 || idArr[0] is not PdfString idStr)
                            throw new Pdfe.Core.Parsing.PdfParseException("/ID array missing or empty");
                        var firstId = idStr.Bytes;

                        // Try the supplied user password. A null password is the
                        // same as the empty user password, by far the most common
                        // case for encrypted PDFs.
                        handler = Pdfe.Core.Security.PdfStandardSecurityHandler.Build(
                            encryptDict, firstId, userPassword);
                    }
                }
            }
            catch (Pdfe.Core.Parsing.PdfEncryptionNotSupportedException)
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
        return new PdfDocument(stream, ownsStream, fullXRef, trailer, version, handler);
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
        var lexer = new Pdfe.Core.Parsing.PdfLexer(stream, ownsStream: false);
        lexer.Seek(offset);
        var parser = new Pdfe.Core.Parsing.PdfParser(lexer);
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
            throw new PdfParseException("Invalid PDF header");

        // Extract version (e.g., "1.4", "1.7", "2.0")
        int idx = headerStart + 5;
        while (idx < header.Length && (char.IsDigit(header[idx]) || header[idx] == '.'))
            idx++;

        return header.Substring(headerStart + 5, idx - (headerStart + 5));
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

        // Find in xref
        if (!_xref.TryGetValue(objectNumber, out var entry))
            throw new PdfParseException($"Object {objectNumber} not found in xref");

        if (!entry.InUse)
            return PdfNull.Instance;

        PdfObject obj;

        if (entry.IsCompressed)
        {
            // Object is in an object stream. The parent /ObjStm itself is
            // decrypted by this same code path when GetObjectFromStream
            // calls back into GetObject(streamNumber); the contained
            // objects are then plaintext and need no further decryption.
            obj = GetObjectFromStream(entry.ObjectStreamNumber!.Value, entry.IndexInStream!.Value);
        }
        else
        {
            // Regular object
            _parser.Seek(entry.Offset);
            var indirectObj = _parser.ParseIndirectObject();
            obj = indirectObj.Value;

            // Apply the security handler before any /Filter pipeline.
            // For RC4: ciphertext is stream's encoded bytes (post-compression
            // on encrypt, so we decrypt FIRST and then run FlateDecode etc.).
            // Strings inside the parsed object are also encrypted with the
            // same per-object key — walk the dict and decrypt them in place.
            // The /Encrypt dict itself is exempt (its strings are read with
            // a one-shot lexer in Open() before we have a handler).
            if (_securityHandler != null)
            {
                int objNum = indirectObj.ObjectNumber;
                int gen = indirectObj.Generation;

                if (obj is PdfStream stream)
                {
                    var encrypted = stream.EncodedData;
                    var decrypted = _securityHandler.DecryptStream(objNum, gen, encrypted);
                    stream.SetEncodedData(decrypted);
                }
                DecryptStringsInPlace(obj, objNum, gen);
            }

            // Decompress streams
            if (obj is PdfStream s && s.IsFiltered)
            {
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

    /// <summary>
    /// Get an object from an object stream.
    /// </summary>
    private PdfObject GetObjectFromStream(int streamNumber, int index)
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
        int n = streamObj.GetInt("N"); // Number of objects
        int first = streamObj.GetInt("First"); // Offset to first object

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

        // Find and parse the requested object
        if (index < 0 || index >= n)
            throw new PdfParseException($"Index {index} out of range in object stream {streamNumber}");

        int offset = first + offsets[index].Offset;
        parser.Seek(offset);
        return parser.ParseObject();
    }

    /// <summary>
    /// Parser callback for resolving indirect /Length refs on stream
    /// dicts. The parser saves and restores the lexer position around
    /// this call so we can safely re-enter <see cref="GetObject(int)"/>.
    /// </summary>
    private PdfObject? ResolveLengthReference(int objectNumber)
    {
        try { return GetObject(objectNumber); }
        catch (Exception __ex) when (__ex is not OutOfMemoryException) { return null; }
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
    /// Save the document to a stream.
    /// </summary>
    public void Save(Stream outputStream)
    {
        foreach (var action in _preSaveActions)
            action();
        var writer = new PdfDocumentWriter(this);
        writer.Write(outputStream);
    }

    /// <summary>
    /// Save the document to a byte array.
    /// </summary>
    public byte[] SaveToBytes()
    {
        using var ms = new MemoryStream();
        Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Save the document to a file.
    /// </summary>
    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Save(fs);
    }

    /// <summary>
    /// Walk the parsed object tree and decrypt every <see cref="PdfString"/>
    /// in place. Each indirect object has its own RC4 keystream derived
    /// from (objNum, gen) — strings inside the same indirect object share
    /// that keystream regardless of how deeply they're nested in dicts
    /// or arrays.
    /// </summary>
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

    /// <inheritdoc />
    public void Dispose()
    {
        _parser.Dispose();
        if (_ownsStream)
            _stream.Dispose();
    }
}
