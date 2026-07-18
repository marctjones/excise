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
    private byte[]? _decodedData;

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

            return _decodedData ?? throw new InvalidOperationException(
                "Stream has not been decoded. Call Decode() first or use a PdfDocumentReader.");
        }
        set
        {
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
        _decodedData = null;
    }

    internal void SetDecodedData(byte[] data)
    {
        _decodedData = data;
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
