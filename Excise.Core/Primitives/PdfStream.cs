namespace Excise.Core.Primitives;

/// <summary>
/// PDF stream object. A stream consists of a dictionary followed by byte data.
/// ISO 32000-2:2020 Section 7.3.8.
/// </summary>
/// <remarks>
/// The dictionary must contain a /Length entry specifying the number of bytes
/// in the encoded stream data. Streams may be filtered (compressed/encoded).
/// </remarks>
public class PdfStream : PdfDictionary
{
    private byte[] _encodedData;

    // volatile: a deferred decode (#1468) publishes this from whichever thread
    // reads DecodedData first, and the fast path reads it without the lock.
    private volatile byte[]? _decodedData;

    // Pending on-demand decode installed by the document object store (#1468).
    // The holder doubles as this stream's decode lock, so a stream that is
    // never deferred allocates nothing. Null once the decode has run.
    private volatile DeferredDecode? _deferredDecode;

    /// <summary>
    /// Creates a new PDF stream with the specified dictionary and data.
    /// </summary>
    /// <param name="dictionary">The stream dictionary (will be copied).</param>
    /// <param name="encodedData">The raw (possibly compressed) stream data.</param>
    public PdfStream(PdfDictionary dictionary, byte[] encodedData)
    {
        // Copy dictionary entries
        foreach (var kvp in dictionary)
        {
            this[kvp.Key] = kvp.Value;
        }
        _encodedData = encodedData ?? throw new ArgumentNullException(nameof(encodedData));

        // Copy object numbers if set
        ObjectNumber = dictionary.ObjectNumber;
        GenerationNumber = dictionary.GenerationNumber;
    }

    /// <summary>
    /// Creates a new PDF stream with empty dictionary and data.
    /// </summary>
    public PdfStream() : this(new PdfDictionary(), Array.Empty<byte>())
    {
    }

    /// <summary>
    /// Creates a new PDF stream with data (uncompressed).
    /// </summary>
    public PdfStream(byte[] data)
    {
        _encodedData = data ?? throw new ArgumentNullException(nameof(data));
        _decodedData = data; // Uncompressed, so decoded = encoded
        SetInt("Length", data.Length);
    }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Stream;

    /// <summary>
    /// Gets the raw (possibly compressed) stream data.
    /// </summary>
    public byte[] EncodedData => _encodedData;

    /// <summary>
    /// Gets the length of the encoded data as declared in the dictionary.
    /// </summary>
    public int Length => GetInt("Length", _encodedData.Length);

    /// <summary>
    /// Gets the filter(s) applied to this stream.
    /// </summary>
    public IReadOnlyList<string> Filters
    {
        get
        {
            var filter = GetOptional("Filter");
            return filter switch
            {
                PdfName n => new[] { n.Value },
                PdfArray a => a.OfType<PdfName>().Select(n => n.Value).ToList(),
                _ => Array.Empty<string>()
            };
        }
    }

    /// <summary>
    /// Gets the decode parameters for each filter.
    /// </summary>
    public IReadOnlyList<PdfDictionary?> DecodeParams
    {
        get
        {
            var parms = GetOptional("DecodeParms");
            return parms switch
            {
                PdfDictionary d => new[] { d },
                PdfArray a => a.Select(o => o as PdfDictionary).ToList(),
                _ => Array.Empty<PdfDictionary?>()
            };
        }
    }

    /// <summary>
    /// Whether this stream has filters applied.
    /// </summary>
    public bool IsFiltered => ContainsKey("Filter");

    /// <summary>
    /// Gets or sets the decoded (uncompressed) stream data.
    /// For unfiltered streams, returns the encoded data directly.
    /// Setting this will also update the encoded data (without compression).
    /// </summary>
    public byte[] DecodedData
    {
        get
        {
            // If no filters, encoded data IS decoded data
            if (!IsFiltered)
                return _encodedData;

            if (_decodedData is { } decoded)
                return decoded;

            if (_deferredDecode != null)
                RunDeferredDecode();

            return _decodedData ?? throw new InvalidOperationException(
                "Stream has not been decoded. Call Decode() first or use a PdfDocumentReader.");
        }
        set
        {
            _deferredDecode = null;
            _decodedData = value;
            _encodedData = value; // No compression for now
            Remove("Filter");
            Remove("DecodeParms");
            SetInt("Length", value.Length);
        }
    }

    /// <summary>
    /// Whether the stream data has been decoded.
    /// </summary>
    public bool IsDecoded => _decodedData != null;

    /// <summary>
    /// Set the decoded data directly (used by StreamDecompressor).
    /// </summary>
    /// <summary>
    /// Replaces the encoded (possibly compressed/encrypted) stream bytes.
    /// Used by the security handler to swap ciphertext for plaintext
    /// before the /Filter pipeline runs.
    /// </summary>
    internal void SetEncodedData(byte[] data)
    {
        _encodedData = data ?? throw new ArgumentNullException(nameof(data));
        // A pending decode was for the bytes being replaced; drop it so a
        // filtered stream reads back "not decoded" exactly as it did before
        // deferral existed.
        _deferredDecode = null;
        _decodedData = null;
    }

    internal void SetDecodedData(byte[] data)
    {
        _decodedData = data;
        _deferredDecode = null;
    }

    /// <summary>
    /// Defers this stream's <c>/Filter</c> pipeline until the first read of
    /// <see cref="DecodedData"/> (#1468).
    /// </summary>
    /// <remarks>
    /// Installed by the document object store for image XObjects only, which
    /// owns the decompressor; a stream never builds its own. <paramref name="decode"/>
    /// runs at most once to completion, under this stream's own lock, on
    /// whichever thread reads first; concurrent readers wait and then see the
    /// same array. It is expected to behave exactly as the eager decode at
    /// resolve time did: set the decoded bytes on success, and on a refused
    /// decode leave the stream undecoded (recording
    /// <see cref="DecodeFailureReason"/>) rather than throw.
    /// </remarks>
    internal void DeferDecode(Action<PdfStream> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        if (_decodedData != null)
            return;

        _deferredDecode = new DeferredDecode(decode);
    }

    /// <summary>
    /// Runs a pending deferred decode (#1468), if there is one, and reports
    /// whether the stream now holds decoded bytes. Never throws for a decode
    /// that refused; the reason, if any, is in <see cref="DecodeFailureReason"/>.
    /// For a stream with no pending decode this is just <see cref="IsDecoded"/>.
    /// </summary>
    /// <remarks>
    /// For callers that branch on <see cref="IsDecoded"/> or on
    /// <see cref="DecodeFailureReason"/> and must see the same answer they
    /// saw when every filtered stream was decoded at resolve time.
    /// </remarks>
    internal bool TryEnsureDecoded()
    {
        if (_decodedData == null && _deferredDecode != null)
            RunDeferredDecode();

        return _decodedData != null;
    }

    private void RunDeferredDecode()
    {
        var pending = _deferredDecode;
        if (pending == null)
            return;

        lock (pending)
        {
            // Another reader finished (or the bytes were replaced) while this
            // one waited for the lock.
            if (_decodedData != null || !ReferenceEquals(_deferredDecode, pending))
                return;

            pending.Decode(this);

            // Cleared only after a normal return, success or refusal — the same
            // single attempt the eager path made. An OutOfMemoryException skips
            // this and leaves the decode pending, as the eager path left the
            // object uncached so a later resolve retried.
            if (ReferenceEquals(_deferredDecode, pending))
                _deferredDecode = null;
        }
    }

    private sealed class DeferredDecode
    {
        public DeferredDecode(Action<PdfStream> decode) => Decode = decode;

        public Action<PdfStream> Decode { get; }
    }

    /// <summary>
    /// Why this stream's <c>/Filter</c> pipeline could not produce decoded
    /// bytes, when a decoder attempted the decode and refused (#1396). Null
    /// when the stream decoded, or when nothing tried.
    /// </summary>
    /// <remarks>
    /// The reason has to be recorded HERE because of where it is needed. The
    /// object store swallows decode failures on purpose — one unreadable image
    /// must not fail the whole document open — so by the time a renderer asks
    /// for the samples, the exception is long gone and all that remains is
    /// "not decoded", which is indistinguishable from "nobody decoded it yet".
    /// A refusal nobody can see is the same defect as the fabrication it
    /// replaced, one step further along.
    /// </remarks>
    internal string? DecodeFailureReason { get; private set; }

    internal void SetDecodeFailureReason(string reason)
    {
        DecodeFailureReason = reason;
    }

    /// <summary>
    /// Get the decoded data as a string (UTF-8).
    /// </summary>
    public string GetDecodedString() =>
        System.Text.Encoding.UTF8.GetString(DecodedData);

    /// <summary>
    /// Get the decoded data as a string with specific encoding.
    /// </summary>
    public string GetDecodedString(System.Text.Encoding encoding) =>
        encoding.GetString(DecodedData);

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{base.ToString()}\nstream\n[{_encodedData.Length} bytes]\nendstream";
    }
}
