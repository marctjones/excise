using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Parsing;

/// <summary>
/// Parser for PDF cross-reference tables and streams.
/// ISO 32000-2:2020 Section 7.5.
/// </summary>
public class XRefParser
{
    private readonly Stream _stream;
    private readonly PdfLexer _lexer;
    private const int XRefRepairTailSearchSize = 1024 * 1024;
    private const int XRefNearbyOffsetRepairWindow = 128;
    private const long XRefReconstructionSizeLimit = 64L * 1024 * 1024;
    private static readonly Regex IndirectObjectHeaderRegex = new(
        @"(?m)^[\t\n\f\r ]*(\d{1,10})[\t\n\f\r ]+(\d{1,5})[\t\n\f\r ]+obj\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Creates a new XRef parser for the specified stream.
    /// </summary>
    public XRefParser(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _lexer = new PdfLexer(stream, ownsStream: false);
    }

    /// <summary>
    /// Find the startxref position by scanning from the end of file.
    /// </summary>
    public long FindStartXRef()
    {
        // ISO 32000 expects startxref near EOF, but real files sometimes
        // append signatures, logs, or transport garbage after %%EOF. Search a
        // bounded tail window so those files can still open without turning
        // this into an unbounded full-file scan.
        const int searchSize = 64 * 1024;
        long fileSize = _stream.Length;
        long searchStart = Math.Max(0, fileSize - searchSize);

        _stream.Position = searchStart;
        var buffer = new byte[searchSize];
        int bytesRead = _stream.Read(buffer, 0, (int)(fileSize - searchStart));

        // Search for "startxref" backwards
        string content = Encoding.ASCII.GetString(buffer, 0, bytesRead);
        int idx = content.LastIndexOf("startxref", StringComparison.Ordinal);

        if (idx < 0)
            throw new PdfParseException("Could not find startxref");

        // Parse the position after "startxref"
        int pos = idx + 9; // Length of "startxref"

        // Skip whitespace
        while (pos < content.Length && char.IsWhiteSpace(content[pos]))
            pos++;

        // Read the number
        var sb = new StringBuilder();
        while (pos < content.Length && char.IsDigit(content[pos]))
        {
            sb.Append(content[pos]);
            pos++;
        }

        if (sb.Length == 0)
            throw new PdfParseException("Invalid startxref value");

        return long.Parse(sb.ToString(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parse the document's root xref section, falling back to conservative
    /// repair paths only after the standards-compliant startxref path fails.
    /// </summary>
    internal (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) ParseRootXRef()
    {
        PdfParseException? primaryFailure = null;

        try
        {
            var parsed = ParseXRef(FindStartXRef());
            RepairInvalidUncompressedXRefOffsets(parsed.XRef);
            return parsed;
        }
        catch (Exception ex) when (IsRecoverableXRefParseException(ex))
        {
            primaryFailure = WrapXRefParseException(ex);
        }

        if (TryFindLastTraditionalXRef(out var xrefPosition))
        {
            try
            {
                var repaired = ParseXRef(xrefPosition);
                RepairUncompressedXRefOffsets(repaired.XRef);
                return repaired;
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                primaryFailure = WrapXRefParseException(ex);
            }
        }

        try
        {
            return ReconstructXRefFromIndirectObjects();
        }
        catch (PdfParseException ex)
        {
            throw new PdfParseException(
                $"Could not recover PDF cross-reference data: {ex.Message}",
                primaryFailure ?? ex);
        }
    }

    /// <summary>
    /// Parse the cross-reference section at the specified position.
    /// Returns the trailer dictionary and populates the xref table.
    /// </summary>
    public (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) ParseXRef(long position)
    {
        _lexer.Seek(position);

        var token = _lexer.NextToken();

        if (token.IsKeyword("xref"))
        {
            // Traditional xref table
            return ParseTraditionalXRef();
        }
        else if (token.Type == PdfTokenType.Integer)
        {
            // XRef stream (PDF 1.5+)
            try
            {
                _lexer.Seek(position);
                return ParseXRefStream();
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                if (TryFindNearbyTraditionalXRef(position, out var repairedPosition))
                {
                    _lexer.Seek(repairedPosition);
                    token = _lexer.NextToken();
                    if (token.IsKeyword("xref"))
                        return ParseTraditionalXRef();
                }

                throw;
            }
        }
        else
        {
            if (TryFindNearbyTraditionalXRef(position, out var repairedPosition))
            {
                _lexer.Seek(repairedPosition);
                token = _lexer.NextToken();
                if (token.IsKeyword("xref"))
                    return ParseTraditionalXRef();
            }

            throw new PdfParseException($"Expected 'xref' or xref stream at position {position}, got {token.Value}");
        }
    }

    internal (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) ParseDocumentXRef(long position)
    {
        var parsed = ParseXRef(position);
        RepairInvalidUncompressedXRefOffsets(parsed.XRef);
        return parsed;
    }

    private bool TryFindLastTraditionalXRef(out long position)
    {
        position = 0;
        var fileSize = _stream.Length;
        var searchSize = (int)Math.Min(XRefRepairTailSearchSize, fileSize);
        var searchStart = fileSize - searchSize;
        var buffer = new byte[searchSize];

        _stream.Position = searchStart;
        var bytesRead = _stream.Read(buffer, 0, buffer.Length);

        for (var i = bytesRead - 4; i >= 0; i--)
        {
            if (!MatchesKeyword(buffer, i, "xref"u8))
                continue;

            if (!IsPdfTokenBoundary(buffer, i - 1) || !IsPdfTokenBoundary(buffer, i + 4))
                continue;

            position = searchStart + i;
            return true;
        }

        return false;
    }

    private bool TryFindNearbyTraditionalXRef(long position, out long repairedPosition)
    {
        repairedPosition = 0;
        if (position <= 0 || position > _stream.Length)
            return false;

        var searchStart = Math.Max(0, position - XRefNearbyOffsetRepairWindow);
        var searchSize = checked((int)(position - searchStart));
        if (searchSize < 4)
            return false;

        var buffer = new byte[searchSize];
        _stream.Position = searchStart;
        var bytesRead = _stream.Read(buffer, 0, buffer.Length);

        for (var i = bytesRead - 4; i >= 0; i--)
        {
            if (!MatchesKeyword(buffer, i, "xref"u8))
                continue;

            if (!IsPdfTokenBoundary(buffer, i - 1) || !IsPdfTokenBoundary(buffer, i + 4))
                continue;

            repairedPosition = searchStart + i;
            return true;
        }

        return false;
    }

    private (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) ReconstructXRefFromIndirectObjects()
    {
        var content = ReadRepairContent();
        var xref = ScanIndirectObjectHeaders(content);

        if (xref.Count == 0)
            throw new PdfParseException("No indirect object headers found");

        var trailer = FindRecoverableTrailer(content)
            ?? FindRecoverableXRefStreamTrailer(content)
            ?? SynthesizeTrailerFromCatalog(content, xref)
            ?? throw new PdfParseException("No recoverable trailer dictionary found");

        if (!trailer.ContainsKey("Size"))
            trailer["Size"] = new PdfInteger(xref.Keys.Max() + 1);

        return (trailer, xref);
    }

    private void RepairUncompressedXRefOffsets(Dictionary<int, XRefEntry> xref)
    {
        var repairedOffsets = ScanIndirectObjectHeaders(ReadRepairContent());

        foreach (var (objectNumber, repaired) in repairedOffsets)
        {
            if (!xref.TryGetValue(objectNumber, out var existing) || existing.IsCompressed)
                continue;

            xref[objectNumber] = new XRefEntry
            {
                Offset = repaired.Offset,
                Generation = repaired.Generation,
                InUse = existing.InUse
            };
        }
    }

    private void RepairInvalidUncompressedXRefOffsets(Dictionary<int, XRefEntry> xref)
    {
        foreach (var (objectNumber, entry) in xref)
        {
            if (entry.IsCompressed || !entry.InUse)
                continue;

            if (!OffsetLooksLikeIndirectObjectHeader(objectNumber, entry.Offset))
            {
                RepairUncompressedXRefOffsets(xref);
                return;
            }
        }
    }

    private bool OffsetLooksLikeIndirectObjectHeader(int objectNumber, long offset)
    {
        if (offset < 0 || offset >= _stream.Length)
            return false;

        Span<byte> buffer = stackalloc byte[64];
        _stream.Position = offset;
        var read = _stream.Read(buffer);
        var pos = 0;

        SkipPdfWhitespace(buffer[..read], ref pos);
        if (!TryReadUnsignedInteger(buffer[..read], ref pos, out var parsedObjectNumber)
            || parsedObjectNumber != objectNumber)
        {
            return false;
        }

        if (!TryRequirePdfWhitespace(buffer[..read], ref pos))
            return false;

        if (!TryReadUnsignedInteger(buffer[..read], ref pos, out _))
            return false;

        if (!TryRequirePdfWhitespace(buffer[..read], ref pos))
            return false;

        return pos + 3 <= read
               && buffer[pos] == (byte)'o'
               && buffer[pos + 1] == (byte)'b'
               && buffer[pos + 2] == (byte)'j'
               && (pos + 3 == read || IsPdfTokenBoundary((char)buffer[pos + 3]));
    }

    private static void SkipPdfWhitespace(ReadOnlySpan<byte> buffer, ref int pos)
    {
        while (pos < buffer.Length && IsPdfWhitespace(buffer[pos]))
            pos++;
    }

    private static bool TryRequirePdfWhitespace(ReadOnlySpan<byte> buffer, ref int pos)
    {
        if (pos >= buffer.Length || !IsPdfWhitespace(buffer[pos]))
            return false;

        SkipPdfWhitespace(buffer, ref pos);
        return true;
    }

    private static bool TryReadUnsignedInteger(ReadOnlySpan<byte> buffer, ref int pos, out int value)
    {
        value = 0;
        var start = pos;
        while (pos < buffer.Length && buffer[pos] is >= (byte)'0' and <= (byte)'9')
        {
            checked
            {
                value = value * 10 + (buffer[pos] - (byte)'0');
            }
            pos++;
        }

        return pos > start;
    }

    private static bool IsPdfWhitespace(byte b)
        => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static Dictionary<int, XRefEntry> ScanIndirectObjectHeaders(string content)
    {
        var xref = new Dictionary<int, XRefEntry>();

        foreach (Match match in IndirectObjectHeaderRegex.Matches(content))
        {
            var objectNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var generation = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var offset = match.Groups[1].Index;

            xref[objectNumber] = new XRefEntry
            {
                Offset = offset,
                Generation = generation,
                InUse = true
            };
        }

        return xref;
    }

    private string ReadRepairContent() => Encoding.Latin1.GetString(ReadAllBytesForRepair());

    private byte[] ReadAllBytesForRepair()
    {
        if (_stream.Length > XRefReconstructionSizeLimit)
        {
            throw new PdfParseException(
                $"PDF is too large for xref reconstruction ({_stream.Length} bytes)");
        }

        var data = new byte[checked((int)_stream.Length)];
        _stream.Position = 0;

        var total = 0;
        while (total < data.Length)
        {
            var read = _stream.Read(data, total, data.Length - total);
            if (read == 0)
                throw new PdfParseException("Unexpected end of file while reconstructing xref");
            total += read;
        }

        return data;
    }

    private PdfDictionary? FindRecoverableTrailer(string content)
    {
        var searchFrom = content.Length;
        while (searchFrom > 0)
        {
            var trailerIndex = content.LastIndexOf("trailer", searchFrom - 1, StringComparison.Ordinal);
            if (trailerIndex < 0)
                return null;

            if (!IsTrailerKeywordAt(content, trailerIndex))
            {
                searchFrom = trailerIndex;
                continue;
            }

            try
            {
                var trailerBodyPosition = trailerIndex + "trailer".Length;
                _lexer.Seek(trailerBodyPosition);
                if (new PdfParser(_lexer).ParseObject() is PdfDictionary trailer
                    && trailer.GetReferenceOrNull("Root") != null)
                {
                    return trailer;
                }
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                // Fall through to the partial parser below. Some real files
                // have a valid /Root and /Size followed by a malformed optional
                // entry such as /ID.
            }

            try
            {
                var trailerBodyPosition = trailerIndex + "trailer".Length;
                _lexer.Seek(trailerBodyPosition);
                var firstToken = _lexer.NextToken();
                if (firstToken.Type == PdfTokenType.DictionaryStart)
                {
                    var partialTrailer = ParsePartialTrailerDictionary(dictionaryDelimited: true);
                    if (partialTrailer != null)
                        return partialTrailer;
                }
                else if (firstToken.Type == PdfTokenType.Name)
                {
                    var bareTrailer = ParseBareTrailerDictionary(firstToken);
                    if (bareTrailer != null)
                        return bareTrailer;
                }
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                // Try the previous trailer marker. Malformed files sometimes
                // contain incidental marker text in stream data or junk.
            }

            searchFrom = trailerIndex;
        }

        return null;
    }

    private PdfDictionary? ParseBareTrailerDictionary(PdfToken firstKeyToken)
    {
        var dict = new PdfDictionary();
        var parser = new PdfParser(_lexer);
        var keyToken = firstKeyToken;

        while (true)
        {
            if (keyToken.Type != PdfTokenType.Name)
                return IsUsableTrailer(dict) ? dict : null;

            try
            {
                dict[keyToken.Value] = parser.ParseObject();
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                return IsUsableTrailer(dict) ? dict : null;
            }

            var next = _lexer.NextToken();
            if (next.Type == PdfTokenType.Eof || next.IsKeyword("startxref"))
                return IsUsableTrailer(dict) ? dict : null;

            keyToken = next;
        }
    }

    private PdfDictionary? ParsePartialTrailerDictionary(bool dictionaryDelimited)
    {
        var dict = new PdfDictionary();
        var parser = new PdfParser(_lexer);

        while (true)
        {
            var keyToken = _lexer.NextToken();
            if (keyToken.Type == PdfTokenType.Eof
                || keyToken.IsKeyword("startxref")
                || (dictionaryDelimited && keyToken.Type == PdfTokenType.DictionaryEnd))
            {
                return IsUsableTrailer(dict) ? dict : null;
            }

            if (keyToken.Type != PdfTokenType.Name)
                return IsUsableTrailer(dict) ? dict : null;

            try
            {
                dict[keyToken.Value] = parser.ParseObject();
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                return IsUsableTrailer(dict) ? dict : null;
            }
        }
    }

    private PdfDictionary? FindRecoverableXRefStreamTrailer(string content)
    {
        var candidates = new List<(int Position, int ObjectNumber)>();
        foreach (Match match in IndirectObjectHeaderRegex.Matches(content))
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var objectNumber))
                continue;

            var dictionaryStart = content.IndexOf("<<", match.Index + match.Length, StringComparison.Ordinal);
            if (dictionaryStart < 0)
                continue;

            var streamIndex = content.IndexOf("stream", dictionaryStart, StringComparison.Ordinal);
            if (streamIndex < 0)
                continue;

            if (streamIndex > match.Index && content.IndexOf("/Type", dictionaryStart, streamIndex - dictionaryStart, StringComparison.Ordinal) >= 0
                && content.IndexOf("/XRef", dictionaryStart, streamIndex - dictionaryStart, StringComparison.Ordinal) >= 0)
            {
                candidates.Add((match.Index, objectNumber));
            }
        }

        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var (position, _) = candidates[i];
            try
            {
                _lexer.Seek(position);
                var parsed = new PdfParser(_lexer).ParseIndirectObject();
                if (parsed.Value is PdfStream stream
                    && stream.GetNameOrNull("Type") == "XRef"
                    && IsUsableTrailer(stream))
                {
                    return stream;
                }
            }
            catch (Exception ex) when (IsRecoverableXRefParseException(ex))
            {
                var dictionaryStart = content.IndexOf("<<", position, StringComparison.Ordinal);
                if (dictionaryStart < 0)
                    continue;

                try
                {
                    _lexer.Seek(dictionaryStart);
                    if (new PdfParser(_lexer).ParseObject() is PdfDictionary trailer
                        && trailer.GetNameOrNull("Type") == "XRef"
                        && IsUsableTrailer(trailer))
                    {
                        return trailer;
                    }
                }
                catch (Exception parseEx) when (IsRecoverableXRefParseException(parseEx))
                {
                    // Try the previous xref stream candidate.
                }
            }
        }

        return null;
    }

    private static PdfDictionary? SynthesizeTrailerFromCatalog(
        string content,
        Dictionary<int, XRefEntry> xref)
    {
        var catalogRef = FindUniqueCatalogObject(content);
        if (catalogRef == null)
            return null;

        var trailer = new PdfDictionary
        {
            ["Root"] = catalogRef,
            ["Size"] = new PdfInteger(xref.Keys.Max() + 1)
        };
        return trailer;
    }

    private static PdfReference? FindUniqueCatalogObject(string content)
    {
        PdfReference? catalogRef = null;

        foreach (Match match in IndirectObjectHeaderRegex.Matches(content))
        {
            var objectNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var objectStart = match.Index + match.Length;
            var objectEnd = content.IndexOf("endobj", objectStart, StringComparison.Ordinal);
            if (objectEnd < 0)
                objectEnd = Math.Min(content.Length, objectStart + 4096);

            var objectText = content.Substring(objectStart, objectEnd - objectStart);
            if (!ContainsCatalogDictionary(objectText))
                continue;

            if (catalogRef != null)
                return null;

            var generation = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            catalogRef = new PdfReference(objectNumber, generation);
        }

        return catalogRef;
    }

    private static bool ContainsCatalogDictionary(string objectText)
        => Regex.IsMatch(
            objectText,
            @"(?s)<<?.*?/Type\s*/Catalog\b.*?/Pages\s+\d+\s+\d+\s+R\b.*?>>?",
            RegexOptions.CultureInvariant);

    private static bool IsUsableTrailer(PdfDictionary dict)
        => dict.GetReferenceOrNull("Root") != null;

    private static bool IsTrailerKeywordAt(string content, int index)
    {
        var before = index == 0 ? '\0' : content[index - 1];
        var afterIndex = index + "trailer".Length;
        var after = afterIndex >= content.Length ? '\0' : content[afterIndex];

        return IsPdfTokenBoundary(before) && IsPdfTokenBoundary(after);
    }

    private static bool MatchesKeyword(byte[] buffer, int offset, ReadOnlySpan<byte> keyword)
    {
        if (offset < 0 || offset + keyword.Length > buffer.Length)
            return false;

        for (var i = 0; i < keyword.Length; i++)
        {
            if (buffer[offset + i] != keyword[i])
                return false;
        }

        return true;
    }

    private static bool IsPdfTokenBoundary(byte[] buffer, int index)
    {
        if (index < 0 || index >= buffer.Length)
            return true;

        return IsPdfTokenBoundary((char)buffer[index]);
    }

    private static bool IsPdfTokenBoundary(char c)
        => c == '\0'
           || c == 0x00
           || c == 0x09
           || c == 0x0A
           || c == 0x0C
           || c == 0x0D
           || c == 0x20
           || c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private static bool IsRecoverableXRefParseException(Exception ex)
        => ex is PdfParseException or FormatException or OverflowException;

    private static PdfParseException WrapXRefParseException(Exception ex)
        => ex as PdfParseException
           ?? new PdfParseException($"Invalid cross-reference data: {ex.Message}", ex);

    /// <summary>
    /// Parse a traditional cross-reference table.
    /// </summary>
    private (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) ParseTraditionalXRef()
    {
        var xref = new Dictionary<int, XRefEntry>();

        // Parse subsections
        while (true)
        {
            var token = _lexer.NextToken();

            if (token.IsKeyword("trailer"))
                break;

            if (token.Type != PdfTokenType.Integer)
                throw new PdfParseException($"Expected subsection start number, got {token.Type}");

            int startObj = int.Parse(token.Value, CultureInfo.InvariantCulture);

            var countToken = _lexer.NextToken();
            if (countToken.Type != PdfTokenType.Integer)
                throw new PdfParseException($"Expected subsection count, got {countToken.Type}");

            int count = int.Parse(countToken.Value, CultureInfo.InvariantCulture);

            // Read entries
            for (int i = 0; i < count; i++)
            {
                var entry = ParseXRefEntry();
                xref[startObj + i] = entry;
            }
        }

        // Parse trailer dictionary
        var parser = new PdfParser(_lexer);
        var trailer = parser.ParseObject() as PdfDictionary
            ?? throw new PdfParseException("Expected trailer dictionary");

        return (trailer, xref);
    }

    /// <summary>
    /// Parse a single xref entry (20 bytes: offset gen status).
    /// </summary>
    private XRefEntry ParseXRefEntry()
    {
        // Each entry is exactly 20 bytes: 10 digits offset, space, 5 digits gen, space, f/n, EOL
        // But we'll use the lexer for robustness

        var offsetToken = _lexer.NextToken();
        if (offsetToken.Type != PdfTokenType.Integer)
            throw new PdfParseException($"Expected offset in xref entry, got {offsetToken.Type}");

        var genToken = _lexer.NextToken();
        if (genToken.Type != PdfTokenType.Integer)
            throw new PdfParseException($"Expected generation in xref entry, got {genToken.Type}");

        var statusToken = _lexer.NextToken();
        if (statusToken.Type != PdfTokenType.Keyword || (statusToken.Value != "n" && statusToken.Value != "f"))
            throw new PdfParseException($"Expected 'n' or 'f' in xref entry, got '{statusToken.Value}'");

        return new XRefEntry
        {
            Offset = long.Parse(offsetToken.Value, CultureInfo.InvariantCulture),
            Generation = int.Parse(genToken.Value, CultureInfo.InvariantCulture),
            InUse = statusToken.Value == "n"
        };
    }

    /// <summary>
    /// Parse a cross-reference stream (PDF 1.5+).
    /// </summary>
    private (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) ParseXRefStream()
    {
        var parser = new PdfParser(_lexer);
        var indirectObj = parser.ParseIndirectObject();

        if (indirectObj.Value is not PdfStream stream)
            throw new PdfParseException("Expected xref stream");

        if (stream.GetNameOrNull("Type") != "XRef")
            throw new PdfParseException("Stream is not an XRef stream");

        // Decode the stream
        var decompressor = new StreamDecompressor();
        decompressor.Decompress(stream);

        var data = stream.DecodedData;

        // Get W array (field widths)
        var wArray = stream.GetArray("W");
        if (wArray.Count != 3)
            throw new PdfParseException("XRef stream /W array must have 3 elements");

        int w1 = wArray.GetInt(0); // Type field width
        int w2 = wArray.GetInt(1); // Field 2 width
        int w3 = wArray.GetInt(2); // Field 3 width
        int entrySize = w1 + w2 + w3;

        // Get Index array (subsections)
        var indexArray = stream.GetArrayOrNull("Index");
        var subsections = new List<(int Start, int Count)>();

        if (indexArray != null)
        {
            for (int i = 0; i < indexArray.Count; i += 2)
            {
                subsections.Add((indexArray.GetInt(i), indexArray.GetInt(i + 1)));
            }
        }
        else
        {
            // Default: single section from 0 to Size
            int size = stream.GetInt("Size");
            subsections.Add((0, size));
        }

        // Parse entries
        var xref = new Dictionary<int, XRefEntry>();
        int dataPos = 0;

        foreach (var (start, count) in subsections)
        {
            for (int i = 0; i < count; i++)
            {
                if (dataPos + entrySize > data.Length)
                    throw new PdfParseException("XRef stream data too short");

                // Read fields
                int type = w1 > 0 ? ReadBigEndianInt(data, dataPos, w1) : 1; // Default type is 1
                dataPos += w1;

                long field2 = w2 > 0 ? ReadBigEndianLong(data, dataPos, w2) : 0;
                dataPos += w2;

                int field3 = w3 > 0 ? ReadBigEndianInt(data, dataPos, w3) : 0;
                dataPos += w3;

                var entry = type switch
                {
                    0 => new XRefEntry { InUse = false, Offset = 0, Generation = field3 }, // Free entry
                    1 => new XRefEntry { InUse = true, Offset = field2, Generation = field3 }, // In use
                    2 => new XRefEntry { InUse = true, ObjectStreamNumber = (int)field2, IndexInStream = field3 }, // Compressed
                    _ => throw new PdfParseException($"Unknown xref entry type: {type}")
                };

                xref[start + i] = entry;
            }
        }

        // The stream dictionary is also the trailer
        return (stream, xref);
    }

    private static int ReadBigEndianInt(byte[] data, int offset, int width)
    {
        int result = 0;
        for (int i = 0; i < width; i++)
        {
            result = (result << 8) | data[offset + i];
        }
        return result;
    }

    private static long ReadBigEndianLong(byte[] data, int offset, int width)
    {
        long result = 0;
        for (int i = 0; i < width; i++)
        {
            result = (result << 8) | data[offset + i];
        }
        return result;
    }
}

/// <summary>
/// Entry in the cross-reference table.
/// </summary>
public class XRefEntry
{
    /// <summary>
    /// Byte offset of the object in the file (for uncompressed objects).
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// Generation number of the object.
    /// </summary>
    public int Generation { get; init; }

    /// <summary>
    /// Whether this object is in use (true) or free (false).
    /// </summary>
    public bool InUse { get; init; }

    /// <summary>
    /// For compressed objects: the object number of the object stream containing this object.
    /// </summary>
    public int? ObjectStreamNumber { get; init; }

    /// <summary>
    /// For compressed objects: the index of this object within the object stream.
    /// </summary>
    public int? IndexInStream { get; init; }

    /// <summary>
    /// Whether this object is stored in an object stream.
    /// </summary>
    public bool IsCompressed => ObjectStreamNumber.HasValue;

    /// <inheritdoc />
    public override string ToString()
    {
        if (IsCompressed)
            return $"Compressed in obj {ObjectStreamNumber} index {IndexInStream}";
        return $"Offset {Offset} gen {Generation} {(InUse ? "n" : "f")}";
    }
}
