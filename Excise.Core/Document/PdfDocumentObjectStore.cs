using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Security;

namespace Excise.Core.Document;

/// <summary>
/// Owns the single parsed object graph and the stream/parser resources that
/// give every indirect object in a <see cref="PdfDocument"/> its identity.
/// </summary>
/// <remarks>
/// This is an ownership boundary, not another document model or parser. The
/// public <see cref="PdfDocument"/> facade delegates all object access and
/// mutation here, while <see cref="PdfParser"/> remains the sole parser.
/// </remarks>
internal sealed class PdfDocumentObjectStore : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Dictionary<int, XRefEntry> _xref;
    private readonly Dictionary<int, PdfObject> _objectCache = new();
    private readonly PdfParser _parser;

    // Resolution seeks and reads one shared parser/lexer stream and mutates
    // one identity cache. Serialize the complete path; Monitor is reentrant
    // for indirect lengths, object streams, and JBIG2 globals. See #376.
    private readonly object _parseLock = new();
    private readonly StreamDecompressor _decompressor = new();
    private readonly PdfStandardSecurityHandler? _securityHandler;
    private readonly Dictionary<int, ObjectStreamCacheEntry> _objectStreamCache = new();
    private readonly HashSet<int> _jbig2GlobalsResolutionsInFlight = new();
    private readonly HashSet<int> _lengthResolutionsInFlight = new();

    internal PdfDocumentObjectStore(
        Stream stream,
        bool ownsStream,
        Dictionary<int, XRefEntry> xref,
        PdfStandardSecurityHandler? securityHandler)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _xref = xref;
        _securityHandler = securityHandler;
        _parser = new PdfParser(new PdfLexer(stream, ownsStream: false));

        // LibreOffice and other producers may write an indirect /Length.
        // PdfParser preserves/restores its lexer position around this callback.
        _parser.IndirectObjectResolver = ResolveLengthReference;
    }

    internal PdfStandardSecurityHandler? SecurityHandler => _securityHandler;

    internal bool IsDecrypting => _securityHandler != null;

    internal int NextFreeObjectNumber => _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1;

    internal PdfReference AddIndirectObject(PdfObject obj)
    {
        var next = NextFreeObjectNumber;
        _xref[next] = new XRefEntry
        {
            Offset = 0,
            Generation = 0,
            InUse = true,
        };
        _objectCache[next] = obj;
        return new PdfReference(next, 0);
    }

    internal void ReplaceIndirectObject(int objectNumber, PdfObject obj)
        => _objectCache[objectNumber] = obj;

    internal void RemoveObject(int objectNumber)
    {
        _xref.Remove(objectNumber);
        _objectCache.Remove(objectNumber);
    }

    /// <summary>
    /// F3 (#1207): forget a resolved object so its bytes can be collected, WITHOUT
    /// touching the xref — the next resolve re-parses it from the file exactly as
    /// the first one did (decryption, JBIG2 globals, deferred decode and the F1 hint
    /// all run again). Unlike <see cref="RemoveObject"/> this is not document
    /// mutation: nothing observable changes except memory. Used by a one-shot
    /// render for an image it has finished with — its encoded bytes were the whole
    /// of what remained on the managed heap at the x4 peak after F2 (dotnet-dump:
    /// 110 MB of System.Byte[], every array rooted through _objectCache).
    /// </summary>
    internal void EvictFromCache(int objectNumber)
    {
        lock (_parseLock)
            _objectCache.Remove(objectNumber);
    }

    internal PdfReference? GetReferenceTo(PdfObject obj)
    {
        foreach (var (number, cached) in _objectCache)
        {
            if (ReferenceEquals(cached, obj))
                return new PdfReference(number, 0);
        }

        return null;
    }

    internal HashSet<int> ComputeReachableObjects(IEnumerable<PdfObject> roots)
    {
        var reachable = new HashSet<int>();
        var stack = new Stack<PdfObject>();
        foreach (var root in roots)
            stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            switch (current)
            {
                case PdfReference reference:
                    if (reachable.Add(reference.ObjectNum))
                    {
                        PdfObject target;
                        try
                        {
                            target = GetObject(reference.ObjectNum);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            break;
                        }

                        stack.Push(target);
                    }
                    break;
                case PdfStream stream:
                    foreach (var value in stream.Values)
                        stack.Push(value);
                    break;
                case PdfDictionary dictionary:
                    foreach (var value in dictionary.Values)
                        stack.Push(value);
                    break;
                case PdfArray array:
                    foreach (var value in array)
                        stack.Push(value);
                    break;
            }
        }

        return reachable;
    }

    /// <summary>
    /// Object numbers the assembled cross-reference table marks in use. A
    /// snapshot, so callers may resolve objects while iterating it.
    /// </summary>
    internal int[] SnapshotInUseObjectNumbers()
    {
        lock (_parseLock)
            return _xref.Where(e => e.Value.InUse).Select(e => e.Key).OrderBy(n => n).ToArray();
    }

    /// <summary>
    /// Copy of the bytes the document was opened from, or null when the source
    /// is not seekable or is larger than <paramref name="maxBytes"/>. Reads under
    /// the parse lock and restores the stream position, so concurrent object
    /// resolution is unaffected. Used only by the unredact carrier scan, which
    /// needs the raw file to reach revisions an incremental update superseded.
    /// </summary>
    internal byte[]? TryReadSourceBytes(long maxBytes)
    {
        lock (_parseLock)
        {
            if (!_stream.CanSeek || !_stream.CanRead || _stream.Length > maxBytes)
                return null;
            var saved = _stream.Position;
            try
            {
                var buffer = new byte[_stream.Length];
                _stream.Position = 0;
                _stream.ReadExactly(buffer);
                return buffer;
            }
            finally
            {
                _stream.Position = saved;
            }
        }
    }

    internal PdfObject GetObject(PdfReference reference)
        => GetObject(reference.ObjectNum);

    internal PdfObject GetObject(int objectNumber)
    {
        lock (_parseLock)
        {
            if (_objectCache.TryGetValue(objectNumber, out var cached))
                return cached;

            // ISO 32000-1 7.3.10: an undefined or free indirect reference is
            // the null object, not a document-level parse failure (#884).
            if (!_xref.TryGetValue(objectNumber, out var entry) || !entry.InUse)
                return PdfNull.Instance;

            PdfObject obj;
            if (entry.IsCompressed)
            {
                obj = GetObjectFromStream(entry.ObjectStreamNumber!.Value, objectNumber);
            }
            else
            {
                _parser.Seek(entry.Offset);
                PdfIndirectObject indirectObject;
                try
                {
                    indirectObject = _parser.ParseIndirectObject();
                }
                catch (PdfParseException ex) when (!ex.IsResourceGuard)
                {
                    _objectCache[objectNumber] = PdfNull.Instance;
                    return PdfNull.Instance;
                }

                obj = indirectObject.Value;
                if (_securityHandler != null && !IsExemptFromEncryption(obj))
                {
                    var parsedObjectNumber = indirectObject.ObjectNumber;
                    var generation = indirectObject.Generation;

                    if (obj is PdfStream stream && !RemoveIdentityCryptFilter(stream))
                    {
                        var decrypted = _securityHandler.DecryptStream(
                            parsedObjectNumber,
                            generation,
                            stream.EncodedData);
                        stream.SetEncodedData(decrypted);
                    }

                    DecryptStringsInPlace(obj, parsedObjectNumber, generation);
                }

                if (obj is PdfStream filteredStream && filteredStream.IsFiltered)
                {
                    ResolveJbig2GlobalsReferences(filteredStream);

                    // #1468: an image XObject's samples are decoded when someone
                    // reads them, not when the object is resolved. Resolving is
                    // what every /Do does just to read /Subtype — text extraction
                    // rejects images that way — so eager decoding here inflated
                    // every Flate image in a document on open and pinned the bytes
                    // in _objectCache until close. Everything else (content
                    // streams, fonts, ICC, object streams, JBIG2 globals) stays
                    // eager: decryption and globals resolution above have already
                    // run, so the deferred decode sees exactly the bytes and
                    // DecodeParms the eager one would have.
                    if (IsDeferredDecodeImage(filteredStream))
                    {
                        // F1 (#1207): computed HERE, under _parseLock at resolve time,
                        // because resolving /ColorSpace (an ICC stream's /N, a DeviceN
                        // names array) is allowed here and forbidden inside the deferred
                        // decode, whose lock rule is stream lock only.
                        filteredStream.ExpectedDecodedLength = EstimateDecodedImageLength(filteredStream);
                        filteredStream.DeferDecode(DecodeDeferredStream);
                    }
                    else
                    {
                        DecodeStream(filteredStream);
                    }
                }
            }

            _objectCache[objectNumber] = obj;
            return obj;
        }
    }

    /// <summary>
    /// Which filtered streams resolve with their decode deferred (#1468): image
    /// XObjects (§8.9.5 — <c>/Subtype /Image</c>, <c>/Type</c> absent or
    /// <c>/XObject</c>). Deliberately narrow. Object streams, content streams,
    /// fonts, ICC profiles and JBIG2 globals have callers that branch on
    /// <see cref="PdfStream.IsDecoded"/> and stay decoded at resolve time.
    /// </summary>
    /// <summary>
    /// The byte count a single-stage /FlateDecode image is expected to inflate
    /// to, from its dictionary alone — or 0 when anything needed is missing,
    /// unknown, or does not fit an array. Only Flate: it is the one filter whose
    /// output size the dictionary predicts and whose decoder grows-and-copies.
    /// With a PNG predictor the inflated rows carry one filter-type byte each and
    /// are sized by the /DecodeParms the predictor itself will read.
    /// </summary>
    private long EstimateDecodedImageLength(PdfStream stream)
    {
        var filters = stream.Filters;
        if (filters.Count != 1 || filters[0] is not ("FlateDecode" or "Fl"))
            return 0;

        int width = stream.GetInt("Width", 0), height = stream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return 0;

        int bitsPerComponent, components;
        if (stream.GetBool("ImageMask"))
        {
            bitsPerComponent = 1;
            components = 1;
        }
        else
        {
            bitsPerComponent = stream.GetInt("BitsPerComponent", 0);
            components = ImageComponentCount(stream.GetOptional("ColorSpace"));
            if (bitsPerComponent <= 0 || components <= 0)
                return 0;
        }

        var parms = stream.DecodeParams.Count > 0 ? stream.DecodeParams[0] : null;
        long rowBytes;
        if (parms != null && parms.GetInt("Predictor", 1) >= 10)
        {
            long colors = parms.GetInt("Colors", 1), columns = parms.GetInt("Columns", 1), bpc = parms.GetInt("BitsPerComponent", 8);
            rowBytes = 1 + (colors * columns * bpc + 7) / 8;
        }
        else
        {
            rowBytes = ((long)components * bitsPerComponent * width + 7) / 8;
        }

        var expected = rowBytes * height;
        return expected > int.MaxValue ? 0 : expected;
    }

    /// <summary>Component count of an image /ColorSpace, or 0 when it cannot be read from the dictionaries.</summary>
    private int ImageComponentCount(PdfObject? colorSpace)
    {
        if (colorSpace == null)
            return 0;
        var resolved = Resolve(colorSpace);
        switch (resolved)
        {
            case PdfName name:
            {
                var cs = ColorSpaces.PdfColorSpace.FromName(name.Value);
                return cs.Type is ColorSpaces.PdfColorSpaceType.Unknown or ColorSpaces.PdfColorSpaceType.Pattern ? 0 : cs.Components;
            }
            case PdfArray array when array.Count >= 1 && array[0] is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased":
                        return array.Count >= 2 && Resolve(array[1]) is PdfStream icc ? Math.Max(0, icc.GetInt("N", 0)) : 0;
                    case "Indexed" or "I" or "Separation" or "CalGray" or "DeviceGray" or "G":
                        return 1;
                    case "CalRGB" or "Lab" or "DeviceRGB" or "RGB":
                        return 3;
                    case "DeviceCMYK" or "CMYK":
                        return 4;
                    case "DeviceN":
                        return array.Count >= 2 && Resolve(array[1]) is PdfArray names ? names.Count : 0;
                    default:
                        return 0;
                }
            default:
                return 0;
        }
    }

    private static bool IsDeferredDecodeImage(PdfStream stream)
    {
        if (stream.GetNameOrNull("Subtype") != "Image")
            return false;

        var type = stream.GetNameOrNull("Type");
        return type is null or "XObject";
    }

    /// <summary>
    /// Runs a stream's /Filter pipeline once, swallowing a refusal exactly as
    /// resolve-time decoding always has: one unreadable image must not fail the
    /// document open, and a decoder that ATTEMPTED the decode and refused has
    /// something worth saying, which is recorded on the stream (#1396).
    /// </summary>
    private void DecodeStream(PdfStream stream)
    {
        try
        {
            _decompressor.Decompress(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Image and unsupported content filters may remain encoded.
            if (ex is Filters.PdfFilterDecodeException)
                stream.SetDecodeFailureReason(ex.Message);
        }
    }

    /// <summary>
    /// The deferred decode installed on image XObjects (#1468). Runs on
    /// whichever thread first reads <see cref="PdfStream.DecodedData"/>, under
    /// that stream's own lock.
    /// </summary>
    /// <remarks>
    /// ⚠️ LOCK RULE — what keeps this deadlock-free: this method must never take
    /// <c>_parseLock</c> (no <see cref="GetObject(int)"/>, no <see cref="Resolve"/>)
    /// and must never read another stream's <see cref="PdfStream.DecodedData"/>
    /// or call its <see cref="PdfStream.TryEnsureDecoded"/>. Everything the
    /// decode needs was settled at resolve time: decryption has already
    /// replaced the encoded bytes, and <see cref="ResolveJbig2GlobalsReferences"/>
    /// has already swapped in (and decoded) the globals stream. The only lock
    /// order in the store is therefore <c>_parseLock</c> → stream lock, never
    /// the reverse. <see cref="_decompressor"/> is shared without a lock: the
    /// filter registry and its decoders hold no per-decode mutable state.
    /// </remarks>
    private void DecodeDeferredStream(PdfStream stream) => DecodeStream(stream);

    internal PdfObject Resolve(PdfObject obj)
    {
        while (obj is PdfReference reference)
            obj = GetObject(reference);

        return obj;
    }

    internal IEnumerable<(int ObjectNumber, int Generation, PdfObject Object)> GetAllObjects()
    {
        foreach (var (objectNumber, entry) in _xref)
        {
            if (entry.InUse)
                yield return (objectNumber, entry.Generation, GetObject(objectNumber));
        }
    }

    private PdfObject GetObjectFromStream(int streamNumber, int objectNumber)
    {
        var stream = GetObject(streamNumber) as PdfStream
            ?? throw new PdfParseException($"Object stream {streamNumber} not found");

        if (!stream.IsDecoded)
            _decompressor.Decompress(stream);

        var data = stream.DecodedData;
        if (!_objectStreamCache.TryGetValue(streamNumber, out var cached)
            || !ReferenceEquals(cached.Source, stream)
            || !ReferenceEquals(cached.Data, data))
        {
            cached = MaterializeObjectStream(stream, data);
            _objectStreamCache[streamNumber] = cached;
        }

        // Resolve from the /ObjStm index's object numbers, not the type-2
        // xref entry's possibly wrapped position (#869).
        if (!cached.SlotByObjectNumber.TryGetValue(objectNumber, out var slot))
            return PdfNull.Instance;

        var obj = cached.Objects[slot];
        if (obj != null)
            return obj;

        using var retryParser = new PdfParser(data);
        retryParser.Seek(cached.First + cached.Offsets[slot].Offset);
        obj = retryParser.ParseObject();
        cached.Objects[slot] = obj;
        return obj;
    }

    private static ObjectStreamCacheEntry MaterializeObjectStream(PdfStream stream, byte[] data)
    {
        if (!stream.ContainsKey("N") || !stream.ContainsKey("First"))
        {
            throw new PdfParseException(
                "Object stream is missing the required /N or /First entry");
        }

        var count = stream.GetInt("N");
        var first = stream.GetInt("First");
        if (count < 0 || (long)count * 2 > data.Length)
        {
            throw new PdfParseException(
                $"Object stream declares /N {count}, which does not fit its {data.Length}-byte index");
        }

        using var parser = new PdfParser(data);
        var offsets = new (int ObjNum, int Offset)[count];
        for (var index = 0; index < count; index++)
        {
            var objectNumberToken = parser.Lexer.NextToken();
            var offsetToken = parser.Lexer.NextToken();
            if (objectNumberToken.Type != PdfTokenType.Integer
                || offsetToken.Type != PdfTokenType.Integer)
            {
                throw new PdfParseException("Invalid object stream index");
            }

            offsets[index] = (
                int.Parse(objectNumberToken.Value),
                int.Parse(offsetToken.Value));
        }

        var objects = new PdfObject?[count];
        for (var index = 0; index < count; index++)
        {
            try
            {
                parser.Seek(first + offsets[index].Offset);
                objects[index] = parser.ParseObject();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Preserve lazy failure: retry only when this slot is requested.
            }
        }

        var slotByObjectNumber = new Dictionary<int, int>(count);
        for (var index = 0; index < count; index++)
            slotByObjectNumber.TryAdd(offsets[index].ObjNum, index);

        return new ObjectStreamCacheEntry
        {
            Source = stream,
            Data = data,
            First = first,
            Offsets = offsets,
            Objects = objects,
            SlotByObjectNumber = slotByObjectNumber,
        };
    }

    private void ResolveJbig2GlobalsReferences(PdfStream stream)
    {
        foreach (var parameters in stream.DecodeParams)
        {
            if (parameters?.GetOptional("JBIG2Globals") is not PdfReference reference)
                continue;
            if (!_jbig2GlobalsResolutionsInFlight.Add(reference.ObjectNum))
                continue;

            try
            {
                if (GetObject(reference.ObjectNum) is PdfStream globals)
                {
                    // Globals are read DURING another stream's decode, and a
                    // decode must never wait on a second stream's lock (see
                    // DecodeDeferredStream). So a globals stream that happens to
                    // be shaped like an image is decoded now, at resolve time,
                    // under _parseLock — the only lock order that exists is
                    // _parseLock → stream lock (#1468).
                    globals.TryEnsureDecoded();
                    parameters["JBIG2Globals"] = globals;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Leave unusable globals unresolved and let decoding classify it.
            }
            finally
            {
                _jbig2GlobalsResolutionsInFlight.Remove(reference.ObjectNum);
            }
        }
    }

    private PdfObject? ResolveLengthReference(int objectNumber)
    {
        lock (_parseLock)
        {
            if (!_lengthResolutionsInFlight.Add(objectNumber))
                return null;

            try
            {
                return GetObject(objectNumber);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
            finally
            {
                _lengthResolutionsInFlight.Remove(objectNumber);
            }
        }
    }

    private bool IsExemptFromEncryption(PdfObject obj)
    {
        if (obj is not PdfStream stream)
            return false;

        var type = stream.GetNameOrNull("Type");
        return type == "XRef"
            || type == "Metadata" && _securityHandler is { EncryptMetadata: false };
    }

    private static bool RemoveIdentityCryptFilter(PdfStream stream)
    {
        var filters = stream.Filters;
        if (filters.Count == 0)
            return false;

        var parameters = stream.DecodeParams;
        var keptFilters = new List<PdfObject>(filters.Count);
        var keptParameters = new List<PdfObject>(filters.Count);
        var removedIdentityCrypt = false;

        for (var index = 0; index < filters.Count; index++)
        {
            var parameter = index < parameters.Count ? parameters[index] : null;
            var isIdentityCrypt = filters[index] == "Crypt"
                && parameter?.GetNameOrNull("Name") == "Identity";
            if (isIdentityCrypt)
            {
                removedIdentityCrypt = true;
                continue;
            }

            keptFilters.Add(new PdfName(filters[index]));
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

        stream["Filter"] = keptFilters.Count == 1
            ? keptFilters[0]
            : new PdfArray(keptFilters);

        if (stream.ContainsKey("DecodeParms"))
        {
            stream["DecodeParms"] = keptParameters.Count == 1
                ? keptParameters[0]
                : new PdfArray(keptParameters);
        }

        return true;
    }

    private void DecryptStringsInPlace(PdfObject root, int objectNumber, int generation)
    {
        if (_securityHandler == null)
            return;

        var stack = new Stack<PdfObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case PdfString text:
                    text.ReplaceBytes(_securityHandler.DecryptString(
                        objectNumber,
                        generation,
                        text.Bytes));
                    break;
                case PdfDictionary dictionary:
                    foreach (var (_, value) in dictionary)
                        stack.Push(value);
                    break;
                case PdfArray array:
                    foreach (var item in array)
                        stack.Push(item);
                    break;
            }
        }
    }

    public void Dispose()
    {
        _parser.Dispose();
        if (_ownsStream)
            _stream.Dispose();
    }

    private sealed class ObjectStreamCacheEntry
    {
        public required PdfStream Source { get; init; }
        public required byte[] Data { get; init; }
        public required int First { get; init; }
        public required (int ObjNum, int Offset)[] Offsets { get; init; }
        public required PdfObject?[] Objects { get; init; }
        public required Dictionary<int, int> SlotByObjectNumber { get; init; }
    }
}
