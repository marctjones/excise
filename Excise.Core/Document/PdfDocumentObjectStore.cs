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
                    try
                    {
                        _decompressor.Decompress(filteredStream);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Image and unsupported content filters may remain encoded.
                    }
                }
            }

            _objectCache[objectNumber] = obj;
            return obj;
        }
    }

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
                    parameters["JBIG2Globals"] = globals;
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
