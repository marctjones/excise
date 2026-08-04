using System.Globalization;
using Excise.Core.Primitives;

namespace Excise.Core.Parsing;

/// <summary>
/// Parser for PDF objects.
/// Uses PdfLexer to tokenize and builds PdfObject instances.
/// </summary>
public class PdfParser : IDisposable
{
    private readonly PdfLexer _lexer;
    private readonly bool _ownsLexer;
    private static readonly HashSet<string> KnownDictionaryKeysWithoutSlash = new(StringComparer.Ordinal)
    {
        "A", "AA", "AcroForm", "Annots", "AP", "AS",
        "BaseFont", "BBox", "BitsPerComponent", "BM",
        "C", "CA", "CIDSet", "CIDSystemInfo", "CIDToGIDMap", "ColorSpace", "Contents",
        "Count", "CropBox", "CS",
        "DA", "Decode", "DecodeParms", "DescendantFonts", "Dest", "Domain", "DR", "DW",
        "Encoding", "Encrypt", "ExtGState",
        "F", "Fields", "Filter", "First", "FirstChar", "Font", "FontBBox", "FontDescriptor",
        "Function", "FunctionType",
        "Group",
        "Height",
        "ID", "ImageMask", "Index", "Info", "Interpolate",
        "Kids",
        "LastChar", "Length", "Length1", "Length2", "Length3", "Limits",
        "Matrix", "MediaBox", "Metadata",
        "N", "Names",
        "OC", "OpenAction", "Outlines",
        "P", "Pages", "Parent", "Pattern", "ProcSet",
        "Range", "Rect", "Resources", "Root",
        "Shading", "Size", "SMask", "StemV", "StructTreeRoot", "Subtype",
        "T", "ToUnicode", "TR", "Trapped", "Type",
        "V", "ViewerPreferences",
        "W", "Width", "Widths",
        "XObject", "XRef",
    };

    /// <summary>
    /// Current array/dictionary nesting depth, used to bound recursion on
    /// hostile or malformed input (deeply nested <c>[[[…]]]</c> / <c>&lt;&lt;…&gt;&gt;</c>)
    /// that would otherwise drive an uncatchable StackOverflow. See issue #346.
    /// </summary>
    private int _depth;

    /// <summary>
    /// Maximum array/dictionary nesting depth before parsing aborts with a
    /// <see cref="PdfParseException"/>. Generous enough for any legitimate PDF
    /// (the deepest real-world structures are well under 100 levels) while
    /// preventing a stack overflow from adversarial input.
    /// </summary>
    public int MaxNestingDepth { get; set; } = 512;

    /// <summary>
    /// Cooperative cancellation for runaway parses of hostile/huge input.
    /// Checked at object-parse entry and inside the array/dictionary element
    /// loops, so a caller's timeout can bound a single pathological object
    /// rather than only the whole document. See issue #346.
    /// </summary>
    public System.Threading.CancellationToken CancellationToken { get; set; } = default;

    /// <summary>
    /// Creates a new parser with the specified lexer.
    /// </summary>
    public PdfParser(PdfLexer lexer, bool ownsLexer = false)
    {
        _lexer = lexer ?? throw new ArgumentNullException(nameof(lexer));
        _ownsLexer = ownsLexer;
    }

    /// <summary>
    /// Creates a new parser for the specified stream.
    /// </summary>
    public PdfParser(Stream stream) : this(new PdfLexer(stream, ownsStream: false), ownsLexer: true)
    {
    }

    /// <summary>
    /// Creates a new parser for the specified byte array.
    /// </summary>
    public PdfParser(byte[] data) : this(new PdfLexer(data), ownsLexer: true)
    {
    }

    /// <summary>
    /// The underlying lexer.
    /// </summary>
    public PdfLexer Lexer => _lexer;

    /// <summary>
    /// Callback used to resolve indirect object references encountered
    /// while parsing. Set by <see cref="Document.PdfDocument"/> after
    /// construction. Required for PDFs that use an indirect /Length on
    /// stream dictionaries (common in PDFs written by LibreOffice, etc.).
    /// </summary>
    public Func<int, PdfObject?>? IndirectObjectResolver { get; set; }

    /// <summary>
    /// Current position in the stream.
    /// </summary>
    public long Position => _lexer.Position;

    /// <summary>
    /// Seek to a specific position.
    /// </summary>
    public void Seek(long position) => _lexer.Seek(position);

    /// <summary>
    /// Parse a single PDF object from the current position.
    /// </summary>
    public PdfObject ParseObject()
    {
        CancellationToken.ThrowIfCancellationRequested();
        var token = _lexer.NextToken();
        return ParseObjectFromToken(token);
    }

    /// <summary>
    /// Parse a PDF object from a token.
    /// </summary>
    private PdfObject ParseObjectFromToken(PdfToken token)
    {
        switch (token.Type)
        {
            case PdfTokenType.Eof:
                throw new PdfParseException("Unexpected end of file");

            case PdfTokenType.Integer:
                return ParsePossibleReference(token);

            case PdfTokenType.Real:
                return new PdfReal(double.Parse(token.Value, CultureInfo.InvariantCulture));

            case PdfTokenType.LiteralString:
                return new PdfString(System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(token.Value), isHex: false);

            case PdfTokenType.HexString:
                return PdfString.FromHex(token.Value);

            case PdfTokenType.Name:
                return new PdfName(token.Value);

            case PdfTokenType.ArrayStart:
                return ParseArray();

            case PdfTokenType.DictionaryStart:
                return ParseDictionaryOrStream();

            case PdfTokenType.Keyword:
                return token.Value switch
                {
                    "true" => PdfBoolean.True,
                    "false" => PdfBoolean.False,
                    "null" => PdfNull.Instance,
                    _ => throw new PdfParseException($"Unexpected keyword '{token.Value}' at position {token.Position}")
                };

            default:
                throw new PdfParseException($"Unexpected token {token.Type} at position {token.Position}");
        }
    }

    /// <summary>
    /// After reading an integer, check if it's part of a reference (n g R).
    /// </summary>
    private PdfObject ParsePossibleReference(PdfToken intToken)
    {
        long savedPos = _lexer.Position;
        var token2 = _lexer.NextToken();

        if (token2.Type == PdfTokenType.Integer)
        {
            var token3 = _lexer.NextToken();
            if (token3.IsKeyword("R"))
            {
                // It's a reference
                int objNum = ParseInt32Token(intToken, "object number");
                int genNum = ParseInt32Token(token2, "generation number");
                return new PdfReference(objNum, genNum);
            }
        }

        // Not a reference, restore position and return integer
        _lexer.Seek(savedPos);
        return new PdfInteger(ParseInt64Token(intToken, "integer"));
    }

    /// <summary>
    /// Parse a PDF array.
    /// </summary>
    private PdfArray ParseArray()
    {
        if (++_depth > MaxNestingDepth)
        {
            _depth--;
            throw new PdfParseException($"Maximum nesting depth ({MaxNestingDepth}) exceeded while parsing array");
        }
        try
        {
            var array = new PdfArray();

            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var token = _lexer.NextToken();

                if (token.Type == PdfTokenType.ArrayEnd)
                    break;

                if (token.Type == PdfTokenType.Eof)
                    throw new PdfParseException("Unterminated array");

                array.Add(ParseObjectFromToken(token));
            }

            return array;
        }
        finally { _depth--; }
    }

    /// <summary>
    /// Parse a dictionary, and if followed by 'stream', parse as stream.
    /// </summary>
    private PdfObject ParseDictionaryOrStream()
    {
        var dict = ParseDictionaryContents();

        // Check for stream
        long savedPos = _lexer.Position;
        var token = _lexer.NextToken();

        if (token.IsKeyword("stream"))
        {
            return ParseStream(dict);
        }

        // Not a stream, restore position
        _lexer.Seek(savedPos);
        return dict;
    }

    /// <summary>
    /// Parse dictionary contents (after &lt;&lt; and before &gt;&gt;).
    /// </summary>
    private PdfDictionary ParseDictionaryContents()
    {
        if (++_depth > MaxNestingDepth)
        {
            _depth--;
            throw new PdfParseException($"Maximum nesting depth ({MaxNestingDepth}) exceeded while parsing dictionary");
        }
        try
        {
            var dict = new PdfDictionary();

            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                long tokenStart = _lexer.Position;
                var token = _lexer.NextToken();

                if (token.Type == PdfTokenType.DictionaryEnd)
                    break;

                if (token.Type == PdfTokenType.Eof)
                    throw new PdfParseException("Unterminated dictionary");

                // RECOVERY (#884): the dictionary is never closed, and an
                // object-boundary keyword shows up where `>>` or a key belongs.
                // pdfium bug_1893.pdf ends an object `/BaseFont /Times-Roman`
                // then goes straight to `endobj`. Rewind so the CALLER still
                // sees the keyword — ParseDictionaryOrStream needs to spot
                // `stream`, and the indirect-object parser needs `endobj`.
                if (token.Type == PdfTokenType.Keyword && IsObjectBoundaryKeyword(token.Value))
                {
                    _lexer.Seek(tokenStart);
                    break;
                }

                // RECOVERY (#884): a doubled `<<`, i.e. a dictionary opening
                // where a key belongs. pdfium bug_481363.pdf and
                // bug_488948351.pdf both write `N 0 obj << << /Type /Page …`.
                // Parse the inner dictionary and fold it into this one rather
                // than discarding it — it holds the object's real content, and
                // in bug_488948351 only ONE `>>` follows, so treating the inner
                // as the object's own body is what lets the trailing `stream`
                // attach to a dictionary that still has its /Length.
                if (token.Type == PdfTokenType.DictionaryStart)
                {
                    var inner = ParseDictionaryContents();
                    foreach (var kvp in inner)
                        if (!dict.ContainsKey(kvp.Key))
                            dict[kvp.Key] = kvp.Value;
                    continue;
                }

                // Key must be a name. Real-world PDFs lose the leading slash:
                // known structural keys ("ToUnicode 37 0 R"), and also
                // arbitrary RESOURCE names — pdfium bug_900552.pdf writes
                // `/Font <<F1 7 0 R>>` while its content stream selects `/F1`,
                // so the bare keyword IS the intended key and dropping the
                // entry loses the font. Resource names cannot be allow-listed,
                // so a name-shaped keyword is accepted as a key; reserved words
                // are excluded so content tokens still cannot pose as dictionary
                // structure, which is what the allow-list was guarding.
                if (token.Type != PdfTokenType.Name)
                {
                    // Known structural keys keep their original precedence.
                    bool knownKey = token.Type == PdfTokenType.Keyword
                                    && KnownDictionaryKeysWithoutSlash.Contains(token.Value);
                    if (!knownKey)
                    {
                        // ORDER MATTERS. A bare keyword is ambiguous, and what
                        // disambiguates it is what comes NEXT:
                        //
                        //   "/BaseFont /Arial,Unicode MS /ToUnicode 37 0 R"
                        //        `MS` is debris from a split font name, and the
                        //        next token is a NAME — the real next key.
                        //   "/Font <<F1 7 0 R>>"
                        //        `F1` is a genuine key, and the next token
                        //        begins its VALUE.
                        //
                        // So the stray-fragment test has to run BEFORE accepting
                        // a name-shaped keyword as a key. Checking them the other
                        // way round swallows the following key as the debris's
                        // value and silently drops it — which is exactly what
                        // ParserHardeningTests caught when this landed reversed.
                        if (IsRecoverableStrayDictionaryKeyword(token))
                            continue;

                        if (token.Type != PdfTokenType.Keyword || !IsNameShapedKeyword(token.Value))
                            throw new PdfParseException($"Expected name in dictionary, got {token.Type} at position {token.Position}");
                    }
                }

                var key = new PdfName(token.Value);
                var value = ParseObject();

                dict[key] = value;
            }

            return dict;
        }
        finally { _depth--; }
    }

    /// <summary>
    /// Keywords that end an indirect object or begin the next syntactic region.
    /// Seeing one inside a dictionary means the dictionary was never closed.
    /// </summary>
    private static bool IsObjectBoundaryKeyword(string value) => value is
        "endobj" or "stream" or "endstream" or "xref" or "trailer" or "startxref" or "obj";

    /// <summary>
    /// Could this bare keyword be a dictionary key that lost its leading slash?
    ///
    /// Reserved words are excluded: `true`/`false`/`null` are VALUES, and `R`
    /// is the reference marker — accepting any of those as a key would let a
    /// malformed value cascade into inventing dictionary structure, which is
    /// exactly what the original known-keys allow-list existed to prevent.
    /// Object-boundary keywords are handled earlier and never reach here.
    /// </summary>
    private static bool IsNameShapedKeyword(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 127) return false;
        if (value is "true" or "false" or "null" or "R") return false;
        if (IsObjectBoundaryKeyword(value)) return false;

        foreach (var c in value)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-' || c == '+';
            if (!ok) return false;
        }
        // A key that is only digits is far more likely to be a stray number
        // than a name; require at least one letter or underscore.
        foreach (var c in value)
            if (char.IsLetter(c) || c == '_') return true;
        return false;
    }

    private bool IsRecoverableStrayDictionaryKeyword(PdfToken token)
    {
        if (token.Type != PdfTokenType.Keyword)
            return false;

        var next = _lexer.PeekToken();
        return next.Type == PdfTokenType.Name
               || next.Type == PdfTokenType.DictionaryEnd
               || (next.Type == PdfTokenType.Keyword && KnownDictionaryKeysWithoutSlash.Contains(next.Value));
    }

    /// <summary>
    /// Parse a stream (dictionary already parsed).
    /// </summary>
    private PdfStream ParseStream(PdfDictionary dict)
    {
        // Get length from dictionary
        int? length = null;
        var lengthObj = dict.GetOptional("Length");
        if (lengthObj is PdfInteger li)
        {
            length = (int)li.Value;
        }
        else if (lengthObj is PdfReference lenRef)
        {
            // Indirect /Length. Resolver is wired up by PdfDocument; save
            // the lexer position, resolve out-of-band, restore, then carry
            // on reading stream bytes at the original position.
            if (IndirectObjectResolver == null)
                throw new PdfParseException(
                    "Stream /Length is an indirect reference but no resolver is configured.");

            long savedPos = _lexer.Position;
            var resolved = IndirectObjectResolver(lenRef.ObjectNum);
            _lexer.Seek(savedPos);

            if (resolved is PdfInteger ri)
                length = (int)ri.Value;
        }

        long streamDataStart = _lexer.Position;
        if (length is not { } declaredLength)
            return new PdfStream(dict, _lexer.ReadStreamDataUntilEndstream());

        // Read stream data
        var data = _lexer.ReadStreamData(declaredLength);

        // Expect 'endstream' keyword. A wrong /Length can land the lexer on a
        // byte that cannot BEGIN any token at all, in which case NextToken
        // throws instead of returning a wrong token. That is the same producer
        // bug as landing on a wrong-but-tokenizable byte, so it must reach the
        // same marker-scan recovery below rather than fail the object.
        // Worked example (#874): pdfium's pixel/bug_1087.pdf declares
        // /Length 89 (indirect, via 10 0 R) on a 94-byte CCITT stream; byte 89
        // of the payload is '^' (0x5E), so the old code threw
        // "Unexpected character '^'" and the recovery was unreachable.
        PdfToken token;
        try
        {
            token = _lexer.NextToken();
        }
        catch (PdfParseException)
        {
            token = default; // Eof-typed placeholder; never a keyword.
        }

        if (!token.IsKeyword("endstream"))
        {
            // Some PDFs have off-by-one length, try to recover
            if (token.Type == PdfTokenType.Keyword && token.Value.StartsWith("endstream"))
            {
                // Close enough
            }
            else
            {
                // Some producer bugs write a too-short or too-long /Length. If
                // the declared-length path lands anywhere other than
                // endstream, re-read from the actual stream-data start and
                // recover by marker scan.
                _lexer.Seek(streamDataStart);
                var recovered = _lexer.ReadStreamDataUntilEndstream(out var foundEndstream);

                // When a real 'endstream' was found it is a delimiter and wins
                // outright. When it was NOT — no marker anywhere before EOF —
                // both extents are guesses, so take the shorter one (#884).
                //
                // Both directions of that occur in the wild. A stream declaring
                // /Length 4 over "test" whose 'endstream' is simply missing is
                // best served by its length; pdfium/bug_452455.pdf, declaring
                // /Length 536870911 in a 1 KB file, is best served by the
                // resynchronised scan. Shorter is also the safer bias for a
                // redaction tool: an over-long extent absorbs the bytes of the
                // objects that follow, which is how a stream ends up carrying a
                // second copy of text that redaction removed from its owner.
                if (foundEndstream || recovered.Length < data.Length)
                    data = recovered;
            }
        }

        ResolveStreamDecodeParms(dict);
        return new PdfStream(dict, data);
    }

    private void ResolveStreamDecodeParms(PdfDictionary dict)
    {
        if (IndirectObjectResolver == null)
            return;

        var parms = dict.GetOptional("DecodeParms");
        switch (parms)
        {
            case PdfReference reference:
                if (IndirectObjectResolver(reference.ObjectNum) is PdfDictionary resolved)
                    dict["DecodeParms"] = resolved;
                break;

            case PdfArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not PdfReference itemReference)
                        continue;

                    if (IndirectObjectResolver(itemReference.ObjectNum) is PdfDictionary itemDictionary)
                        array[i] = itemDictionary;
                }
                break;
        }
    }

    /// <summary>
    /// Parse an indirect object at the current position.
    /// Expects format: "n g obj ... endobj"
    /// </summary>
    public PdfIndirectObject ParseIndirectObject()
    {
        var objNumToken = _lexer.NextToken();
        if (objNumToken.Type != PdfTokenType.Integer)
            throw new PdfParseException($"Expected object number, got {objNumToken.Type} at position {objNumToken.Position}");

        var genNumToken = _lexer.NextToken();
        if (genNumToken.Type != PdfTokenType.Integer)
            throw new PdfParseException($"Expected generation number, got {genNumToken.Type} at position {genNumToken.Position}");

        var objToken = _lexer.NextToken();
        if (!objToken.IsKeyword("obj"))
            throw new PdfParseException($"Expected 'obj', got '{objToken.Value}' at position {objToken.Position}");

        int objNum = ParseInt32Token(objNumToken, "object number");
        int genNum = ParseInt32Token(genNumToken, "generation number");

        var value = ParseObject();

        var endObjToken = _lexer.NextToken();
        if (!endObjToken.IsKeyword("endobj"))
        {
            if (value is PdfName firstBareKey)
            {
                value = ParseBareDictionaryObject(firstBareKey, endObjToken);
            }
            // Otherwise: KEEP THE VALUE. A missing or malformed 'endobj' is a
            // trailing-delimiter problem, not a value problem — ParseObject has
            // already succeeded by the time we get here (#884).
            //
            // Throwing here discarded a successfully-parsed object and, because
            // this runs during document open, took the whole file with it: 13
            // corpus pages reported MALFORMED_PDF while mutool and pdftocairo
            // rendered them, making it the single largest EXCISE_SIDE_GAP
            // cluster.
            //
            // Tolerating it is safe because this reader is OFFSET-DRIVEN, not
            // sequential. Both callers Seek() to the object's own xref offset
            // before parsing (PdfDocument.ReadIndirectObjectAt and the
            // GetObject path), so a stray token after a value cannot desync the
            // next object — the next read starts from its own offset. 'endobj'
            // is a delimiter for a scan this parser does not perform.
            //
            // The risk this accepts is the opposite one: if ParseObject went
            // wrong and swallowed too much, we now keep a wrong value instead of
            // failing loudly. That trade is worth taking because the failure was
            // not local — it condemned the entire document, and every other
            // reader accepts these files.
        }

        return new PdfIndirectObject(objNum, genNum, value);
    }

    private PdfDictionary ParseBareDictionaryObject(PdfName firstKey, PdfToken firstValueToken)
    {
        var dict = new PdfDictionary
        {
            [firstKey] = ParseObjectFromToken(firstValueToken)
        };

        while (true)
        {
            var keyToken = _lexer.NextToken();
            if (keyToken.IsKeyword("endobj"))
                return dict;

            if (keyToken.Type == PdfTokenType.Eof)
                throw new PdfParseException("Unterminated bare dictionary object");

            if (keyToken.Type != PdfTokenType.Name)
                throw new PdfParseException(
                    $"Expected name in bare dictionary object, got {keyToken.Type} at position {keyToken.Position}");

            dict[new PdfName(keyToken.Value)] = ParseObject();
        }
    }

    private static int ParseInt32Token(PdfToken token, string label)
    {
        try
        {
            return int.Parse(token.Value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new PdfParseException($"Invalid {label} '{token.Value}' at position {token.Position}", ex);
        }
    }

    private static long ParseInt64Token(PdfToken token, string label)
    {
        try
        {
            return long.Parse(token.Value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new PdfParseException($"Invalid {label} '{token.Value}' at position {token.Position}", ex);
        }
    }

    /// <summary>
    /// Try to parse an indirect object, returning null if not at an object.
    /// </summary>
    public PdfIndirectObject? TryParseIndirectObject()
    {
        long savedPos = _lexer.Position;

        try
        {
            var token1 = _lexer.NextToken();
            if (token1.Type != PdfTokenType.Integer)
            {
                _lexer.Seek(savedPos);
                return null;
            }

            var token2 = _lexer.NextToken();
            if (token2.Type != PdfTokenType.Integer)
            {
                _lexer.Seek(savedPos);
                return null;
            }

            var token3 = _lexer.NextToken();
            if (!token3.IsKeyword("obj"))
            {
                _lexer.Seek(savedPos);
                return null;
            }

            _lexer.Seek(savedPos);
            return ParseIndirectObject();
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            _lexer.Seek(savedPos);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsLexer)
            _lexer.Dispose();
    }
}
