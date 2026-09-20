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

    // The on-demand decode installed by the document object store (#1468).
    // The holder doubles as this stream's decode lock, so a stream that is
    // never deferred allocates nothing. Installed once and never cleared: it
    // is the lock a release and every later reader must agree on, and the
    // delegate a release re-arms. Null for a stream that was never deferred.
    private volatile DeferredDecode? _deferral;

    // Guarded by the _deferral lock; never read outside it. The decode is
    // pending (_deferral) or not (null).
    private DeferredDecode? _pendingDecode;

    // Guarded by the _deferral lock. True only while _decodedData is exactly
    // what the deferred decode produced from the current encoded bytes — the
    // one case where dropping the array and decoding again later is guaranteed
    // to give a reader the same bytes. Every writer of the byte fields clears
    // it, which is what keeps a redaction-written array from ever being
    // released and silently reverted to the original samples (#1468).
    private bool _decodedByDeferral;

    /// <summary>
    /// How many bytes the /Filter pipeline's Flate stage is expected to produce,
    /// or 0 when unknown (#1207, #1468 — F1). Set by the object store at resolve
    /// time for a deferred-decode image, from /Width, /Height, /BitsPerComponent
    /// and the component count of /ColorSpace (plus the PNG-predictor row byte
    /// when /DecodeParms says so). <see cref="Filters.FlateFilterDecoder"/> uses
    /// it to allocate the decoded array ONCE at the right size instead of
    /// growing a chunk list and copying it into an exact-size result — which
    /// held both live at the end and made a 92 MiB image cost ~184 MiB at its
    /// peak. A wrong hint costs one extra copy, never a wrong byte.
    /// </summary>
    internal long ExpectedDecodedLength { get; set; }

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
    /// Setting this replaces the stream's bytes and re-encodes them
    /// losslessly with <c>/FlateDecode</c> (#1549), dropping any previous
    /// <c>/Filter</c> and <c>/DecodeParms</c> and updating <c>/Length</c>.
    /// </summary>
    /// <remarks>
    /// Until #1549 the setter stored the bytes raw, so every page excise edited
    /// or redacted was saved with an uncompressed content stream: measured on
    /// irs-w4.pdf, redacting one term grew the file from 208,845 to 1,001,684
    /// bytes (4.8x). The bytes stay raw (no <c>/Filter</c>) when Flate would
    /// not make them smaller, and always for an XMP <c>/Type /Metadata</c>
    /// stream, which must stay readable without decompression (§14.3.2).
    /// </remarks>
    public byte[] DecodedData
    {
        get
        {
            // If no filters, encoded data IS decoded data
            if (!IsFiltered)
                return _encodedData;

            if (_decodedData is { } decoded)
                return decoded;

            // The array comes back from inside the decode lock, never from a
            // second read of the field: a release on another thread between
            // the two reads would turn a successful decode into this throw.
            return DecodeOnDemand() ?? throw new InvalidOperationException(
                "Stream has not been decoded. Call Decode() first or use a PdfDocumentReader.");
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Remove("DecodeParms");

            byte[]? compressed = null;
            if (value.Length > 0 && GetNameOrNull("Type") != "Metadata")
            {
                compressed = Excise.Core.Filters.BasicStreamFilters.EncodeFlate(value);
                if (compressed.Length >= value.Length)
                    compressed = null;
            }

            if (compressed == null)
            {
                ReplaceBytes(decoded: value, encoded: value);
                Remove("Filter");
                SetInt("Length", value.Length);
                return;
            }

            ReplaceBytes(decoded: value, encoded: compressed);
            SetName("Filter", "FlateDecode");
            SetInt("Length", compressed.Length);
        }
    }

    /// <summary>
    /// A new stream holding <paramref name="data"/> Flate-encoded on the way
    /// in (#1549) — the constructor for bytes excise itself generates (a
    /// rewritten content stream, a raster page, an XFA rewrite). The public
    /// <see cref="PdfStream(byte[])"/> constructor keeps its documented
    /// uncompressed shape.
    /// </summary>
    internal static PdfStream CreateCompressed(byte[] data)
    {
        var stream = new PdfStream();
        stream.DecodedData = data;
        return stream;
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
        ArgumentNullException.ThrowIfNull(data);
        // A pending decode was for the bytes being replaced; drop it so a
        // filtered stream reads back "not decoded" exactly as it did before
        // deferral existed.
        ReplaceBytes(decoded: null, encoded: data);
    }

    internal void SetDecodedData(byte[] data)
    {
        ReplaceBytes(decoded: data, encoded: null);
    }

    /// <summary>
    /// Replace both byte fields at once and set <c>/Filter</c> to
    /// <paramref name="filter"/> (or remove it when null), dropping
    /// <c>/DecodeParms</c> and updating <c>/Length</c> (#1550). For the file-size
    /// optimizer, which chooses its own encoding (a stronger Flate level, or a
    /// JPEG) where the <see cref="DecodedData"/> setter would pick Optimal Flate.
    /// </summary>
    /// <param name="encoded">The bytes the writer will emit.</param>
    /// <param name="decoded">What a reader of <see cref="DecodedData"/> sees:
    /// the samples for Flate, the codestream itself for a pass-through filter
    /// such as <c>DCTDecode</c>.</param>
    /// <param name="filter">The single filter that turns
    /// <paramref name="encoded"/> into <paramref name="decoded"/>.</param>
    internal void ReplaceEncoding(byte[] encoded, byte[] decoded, string? filter)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(decoded);
        Remove("DecodeParms");
        if (filter == null)
            Remove("Filter");
        else
            SetName("Filter", filter);
        ReplaceBytes(decoded: decoded, encoded: encoded);
        SetInt("Length", encoded.Length);
    }

    /// <summary>
    /// The one writer of the byte fields for every caller outside the deferred
    /// decode itself (#1468). <paramref name="encoded"/> null keeps the current
    /// encoded bytes.
    /// </summary>
    /// <remarks>
    /// On a deferred stream this takes the decode lock, so a write can never
    /// interleave with <see cref="TryReleaseDecoded()"/>: without it a release
    /// that had already checked the provenance flag could re-arm the decode and
    /// null a redaction's freshly written array a moment after it landed,
    /// handing the next reader the ORIGINAL samples again. Lock order stays
    /// the store's: a caller may hold <c>_parseLock</c> when it gets here, and
    /// nothing under this lock takes <c>_parseLock</c>. The deferred decode's
    /// own <c>SetDecodedData</c> call re-enters the lock it already holds.
    /// </remarks>
    private void ReplaceBytes(byte[]? decoded, byte[]? encoded)
    {
        var deferral = _deferral;
        if (deferral == null)
        {
            Write();
            return;
        }

        lock (deferral)
            Write();

        void Write()
        {
            _decodedByDeferral = false;
            _pendingDecode = null;
            if (encoded != null)
                _encodedData = encoded;
            _decodedData = decoded;
        }
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
    /// <see cref="DecodeFailureReason"/>) rather than throw. Called once, at
    /// resolve time, before the stream is reachable from any other thread.
    /// </remarks>
    /// <summary>
    /// F2: a forward-only stream of this stream's inflated bytes, or null when the
    /// filter pipeline is not exactly one Flate stage or carries a predictor. Never
    /// caches, never writes <c>_decodedData</c>: the point is that the inflated
    /// array never exists. The caller owns the returned stream.
    /// </summary>
    internal Stream? TryOpenFlateDecodeStream()
    {
        var filters = Filters;
        if (filters.Count != 1 || filters[0] is not ("FlateDecode" or "Fl"))
            return null;
        var parms = DecodeParams.Count > 0 ? DecodeParams[0] : null;
        if (parms != null && parms.GetInt("Predictor", 1) > 1)
            return null;
        return global::Excise.Core.Filters.FlateFilterDecoder.OpenDecodeStream(_encodedData);
    }

    internal void DeferDecode(Action<PdfStream> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        if (_decodedData != null)
            return;

        var deferral = new DeferredDecode(decode);
        _pendingDecode = deferral;
        _deferral = deferral;
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
        => _decodedData != null || DecodeOnDemand() != null;

    /// <summary>
    /// Runs the pending deferred decode if there is one and returns the decoded
    /// bytes as they stood INSIDE the decode lock — null when the stream is not
    /// decoded (refused, replaced, or never deferred and never decoded).
    /// </summary>
    /// <remarks>
    /// Callers must use the returned array and must not re-read
    /// <c>_decodedData</c> after this returns. Under the lock the state
    /// is always one of three consistent shapes — decoded; not decoded with the
    /// decode pending; not decoded and nothing pending (refused) — but outside
    /// it a concurrent <see cref="TryReleaseDecoded()"/> can move a decoded stream
    /// back to "pending" at any moment. A reader that decoded successfully and
    /// then looked at the field again would see null and report a refusal that
    /// never happened (#1468).
    /// </remarks>
    private byte[]? DecodeOnDemand()
    {
        var deferral = _deferral;
        if (deferral == null)
            return _decodedData;

        lock (deferral)
        {
            if (_decodedData == null && ReferenceEquals(_pendingDecode, deferral))
            {
                deferral.Decode(this);

                // Cleared only after a normal return, success or refusal — the
                // same single attempt the eager path made. An
                // OutOfMemoryException skips this and leaves the decode
                // pending, as the eager path left the object uncached so a
                // later resolve retried.
                _pendingDecode = null;

                // Set AFTER the decode: the decompressor publishes its result
                // through SetDecodedData, which clears the flag like any other
                // writer. Nothing else can write in between — every writer
                // takes this lock.
                _decodedByDeferral = _decodedData != null;
            }

            return _decodedData;
        }
    }

    /// <summary>
    /// Drops the decoded bytes of a deferred image stream (#1468) and re-arms
    /// its decode, so the next reader decodes the same encoded bytes again.
    /// Returns whether anything was released.
    /// </summary>
    /// <remarks>
    /// <para><b>What it will release.</b> Only bytes the deferred decode
    /// produced from the current encoded bytes. A stream whose bytes were
    /// written by anyone else — the <see cref="DecodedData"/> setter,
    /// <see cref="SetDecodedData"/> (a redacted image's zeroed samples, a
    /// clone), <see cref="SetEncodedData"/> — is refused, for as long as that
    /// write stands. So is a stream that was never deferred, never decoded, or
    /// whose decode refused: there is nothing that could be rebuilt exactly.</para>
    ///
    /// <para><b>What it guards.</b> The provenance flag watches the BYTES, not
    /// the dictionary. Every in-tree path that edits <c>/Filter</c> or
    /// <c>/DecodeParms</c> on an existing stream also replaces its bytes
    /// through one of the writers above (or runs at resolve time, before the
    /// decode is deferred), which is what makes a re-decode byte-identical.</para>
    ///
    /// <para><b>Concurrency.</b> Safe against concurrent readers. A reader
    /// already holding the array keeps it; a later reader finds the decode
    /// pending and runs it again under the lock. The decode is re-armed before
    /// the array is dropped, but correctness does not rest on that order —
    /// only the lock-free fast path reads <c>_decodedData</c> outside
    /// the lock, and it only ever returns a non-null array.</para>
    ///
    /// <para>The save path is unaffected: the writer serializes
    /// <see cref="EncodedData"/>, which a release never touches.</para>
    /// </remarks>
    internal bool TryReleaseDecoded() => TryReleaseDecoded(out _);

    /// <summary>
    /// <see cref="TryReleaseDecoded()"/>, reporting how many decoded bytes the
    /// release dropped (0 when nothing was released). For the viewer's release
    /// metrics (#1492).
    /// </summary>
    internal bool TryReleaseDecoded(out long releasedBytes)
    {
        releasedBytes = 0;
        var deferral = _deferral;
        if (deferral == null)
            return false;

        lock (deferral)
            return ReleaseHeld(deferral, out releasedBytes);
    }

    /// <summary>
    /// <see cref="TryReleaseDecoded(out long)"/> for a caller that must not wait
    /// (#1492): the interactive viewer releases on the UI thread, and a decode
    /// runs inside this stream's lock, which on a large image can take seconds.
    /// When the lock is held — a decode or a write is in progress, so the
    /// samples are in use right now — this gives up at once and reports
    /// <see cref="DecodedReleaseOutcome.Busy"/>, and the caller tries again
    /// later. Same release rules otherwise.
    /// </summary>
    internal DecodedReleaseOutcome TryReleaseDecodedWithoutWaiting(out long releasedBytes)
    {
        releasedBytes = 0;
        var deferral = _deferral;
        if (deferral == null)
            return DecodedReleaseOutcome.NotReleasable;

        if (!Monitor.TryEnter(deferral))
            return DecodedReleaseOutcome.Busy;
        try
        {
            return ReleaseHeld(deferral, out releasedBytes)
                ? DecodedReleaseOutcome.Released
                : DecodedReleaseOutcome.NotReleasable;
        }
        finally
        {
            Monitor.Exit(deferral);
        }
    }

    // Caller holds the deferral lock.
    private bool ReleaseHeld(DeferredDecode deferral, out long releasedBytes)
    {
        releasedBytes = 0;
        var decoded = _decodedData;
        if (!_decodedByDeferral || decoded == null)
            return false;

        _decodedByDeferral = false;
        _pendingDecode = deferral;
        _decodedData = null;
        releasedBytes = decoded.LongLength;
        return true;
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

/// <summary>
/// What <see cref="PdfStream.TryReleaseDecodedWithoutWaiting"/> did (#1492).
/// </summary>
internal enum DecodedReleaseOutcome
{
    /// <summary>The decoded bytes were dropped and the decode re-armed.</summary>
    Released,

    /// <summary>
    /// Nothing to release now: never deferred, not decoded, a refused decode,
    /// or bytes written by something other than the decoder.
    /// </summary>
    NotReleasable,

    /// <summary>The stream's lock was held (a decode or write in progress); try again later.</summary>
    Busy,
}
