using System.Globalization;
using System.Text;

namespace Pdfe.Core.Parsing;

/// <summary>
/// Tokenizer for PDF syntax.
/// Reads a stream and produces tokens according to ISO 32000-2:2020 Section 7.2.
/// </summary>
public class PdfLexer : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _buffer;
    private int _bufferPos;
    private int _bufferLen;
    private const int BufferSize = 8192;
    private const int DefaultMaxRecoveredStreamBytes = 128 * 1024 * 1024;

    // PDF whitespace characters (ISO 32000-2:2020 Table 1)
    private static readonly HashSet<byte> Whitespace = new() { 0, 9, 10, 12, 13, 32 };

    // PDF delimiter characters (ISO 32000-2:2020 Table 2)
    private static readonly HashSet<byte> Delimiters = new()
    {
        (byte)'(', (byte)')', (byte)'<', (byte)'>', (byte)'[', (byte)']',
        (byte)'{', (byte)'}', (byte)'/', (byte)'%'
    };

    /// <summary>
    /// Creates a new lexer for the specified stream.
    /// </summary>
    public PdfLexer(Stream stream, bool ownsStream = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
        _buffer = new byte[BufferSize];
        _bufferPos = 0;
        _bufferLen = 0;
    }

    /// <summary>
    /// Creates a new lexer for the specified byte array.
    /// </summary>
    public PdfLexer(byte[] data) : this(new MemoryStream(data, writable: false), ownsStream: true)
    {
    }

    /// <summary>
    /// Current position in the stream.
    /// </summary>
    public long Position => _stream.Position - _bufferLen + _bufferPos;

    /// <summary>
    /// Seek to a specific position in the stream.
    /// </summary>
    public void Seek(long position)
    {
        // A malformed xref table or hostile offset can yield a negative or
        // past-EOF position. Reject it with a typed PdfParseException instead
        // of letting MemoryStream throw a raw ArgumentOutOfRangeException. (#352)
        if (position < 0 || position > _stream.Length)
            throw new PdfParseException($"Invalid seek offset {position} (stream length {_stream.Length})");
        _stream.Position = position;
        _bufferPos = 0;
        _bufferLen = 0;
    }

    /// <summary>
    /// Read the next token from the stream.
    /// </summary>
    public PdfToken NextToken()
    {
        SkipWhitespaceAndComments();

        long startPos = Position;
        int c = ReadByte();

        if (c == -1)
            return new PdfToken(PdfTokenType.Eof, string.Empty, startPos);

        // Array delimiters
        if (c == '[')
            return new PdfToken(PdfTokenType.ArrayStart, "[", startPos);
        if (c == ']')
            return new PdfToken(PdfTokenType.ArrayEnd, "]", startPos);

        // Dictionary or hex string
        if (c == '<')
        {
            int next = PeekByte();
            if (next == '<')
            {
                ReadByte(); // consume second <
                return new PdfToken(PdfTokenType.DictionaryStart, "<<", startPos);
            }
            return ReadHexString(startPos);
        }

        if (c == '>')
        {
            int next = PeekByte();
            if (next == '>')
            {
                ReadByte(); // consume second >
                return new PdfToken(PdfTokenType.DictionaryEnd, ">>", startPos);
            }
            throw new PdfParseException($"Unexpected '>' at position {startPos}");
        }

        // Literal string
        if (c == '(')
            return ReadLiteralString(startPos);

        // Name
        if (c == '/')
            return ReadName(startPos);

        // Comment
        if (c == '%')
            return ReadComment(startPos);

        // Number or keyword
        if (IsNumberStart(c))
        {
            UnreadByte();
            return ReadNumberOrKeyword(startPos);
        }

        // Keyword (true, false, null, obj, endobj, etc.)
        // Keywords always start with an ASCII letter (or '*' for hex-string-like)
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
        {
            UnreadByte();
            return ReadKeyword(startPos);
        }

        throw new PdfParseException($"Unexpected character '{(char)c}' (0x{c:X2}) at position {startPos}");
    }

    /// <summary>
    /// Peek at the next token without consuming it.
    /// </summary>
    public PdfToken PeekToken()
    {
        long savedPos = Position;
        var token = NextToken();
        Seek(savedPos);
        return token;
    }

    /// <summary>
    /// Read stream data after the 'stream' keyword.
    /// </summary>
    /// <param name="length">Number of bytes to read.</param>
    public byte[] ReadStreamData(int length)
    {
        if (length < 0)
            throw new PdfParseException($"Invalid negative stream length: {length}");

        SkipStreamDataLineEnding();

        var data = new byte[length];
        int totalRead = 0;

        // First, consume buffered data
        if (_bufferLen > _bufferPos)
        {
            int bufferedAvailable = _bufferLen - _bufferPos;
            int toCopy = Math.Min(bufferedAvailable, length);
            Array.Copy(_buffer, _bufferPos, data, 0, toCopy);
            _bufferPos += toCopy;
            totalRead = toCopy;
        }

        // Read remaining directly from stream
        while (totalRead < length)
        {
            int read = _stream.Read(data, totalRead, length - totalRead);
            if (read == 0)
                throw new PdfParseException($"Unexpected end of stream data, expected {length} bytes but got {totalRead}");
            totalRead += read;
        }

        // If we consumed all buffered data, clear buffer for fresh reading
        if (_bufferPos >= _bufferLen)
        {
            _bufferPos = 0;
            _bufferLen = 0;
        }

        return data;
    }

    /// <summary>
    /// Recover malformed stream data by scanning to the next <c>endstream</c>
    /// marker. This is used only when /Length is absent, cannot be resolved, or
    /// is proved wrong by the following token. Valid streams still use
    /// <see cref="ReadStreamData(int)"/> so binary data containing the marker is
    /// governed by the declared length, as the PDF spec intends.
    /// </summary>
    internal byte[] ReadStreamDataUntilEndstream(int maxBytes = DefaultMaxRecoveredStreamBytes)
    {
        SkipStreamDataLineEnding();

        var output = new MemoryStream();
        ReadOnlySpan<byte> marker = "endstream"u8;
        int matched = 0;

        while (true)
        {
            int c = ReadByte();
            if (c == -1)
                throw new PdfParseException("Unexpected end of file while scanning for endstream");

            output.WriteByte((byte)c);
            if (output.Length > maxBytes)
                throw new PdfParseException(
                    $"Recovered stream exceeded {maxBytes} bytes without an endstream marker");

            if ((byte)c == marker[matched])
            {
                matched++;
                if (matched == marker.Length)
                {
                    var bytes = output.ToArray();
                    int dataLength = bytes.Length - marker.Length;

                    // The EOL before endstream is PDF syntax, not stream data,
                    // when we recover by marker scan.
                    while (dataLength > 0 && (bytes[dataLength - 1] == '\n' || bytes[dataLength - 1] == '\r'))
                        dataLength--;

                    var data = new byte[dataLength];
                    Array.Copy(bytes, data, dataLength);
                    return data;
                }
            }
            else
            {
                matched = (byte)c == marker[0] ? 1 : 0;
            }
        }
    }

    private void SkipStreamDataLineEnding()
    {
        // The spec requires EOL after the stream keyword. Some real-world files
        // insert horizontal whitespace before that EOL; skip it only when the
        // EOL is actually present so malformed no-EOL streams keep their data.
        var start = Position;
        var c = ReadByte();
        if (c == '\r')
        {
            c = ReadByte();
            if (c != '\n')
                UnreadByte();
            return;
        }

        if (c == '\n' || c == -1)
            return;

        if (IsHorizontalWhitespace(c))
        {
            while (IsHorizontalWhitespace(PeekByte()))
                ReadByte();

            c = ReadByte();
            if (c == '\r')
            {
                c = ReadByte();
                if (c != '\n')
                    UnreadByte();
                return;
            }

            if (c == '\n')
                return;

            Seek(start);
            return;
        }

        UnreadByte();
    }

    /// <summary>
    /// Skip whitespace and comments.
    /// </summary>
    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            int c = PeekByte();
            if (c == -1)
                return;

            if (Whitespace.Contains((byte)c))
            {
                ReadByte();
                continue;
            }

            if (c == '%')
            {
                // Skip comment to end of line
                while ((c = ReadByte()) != -1 && c != '\r' && c != '\n') { }
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// Read a literal string (parentheses-delimited).
    /// </summary>
    private PdfToken ReadLiteralString(long startPos)
    {
        var bytes = new List<byte>();
        int depth = 1;

        while (depth > 0)
        {
            int c = ReadByte();
            if (c == -1)
                throw new PdfParseException($"Unterminated literal string starting at {startPos}");

            if (c == '(')
            {
                depth++;
                bytes.Add((byte)c);
            }
            else if (c == ')')
            {
                depth--;
                if (depth > 0)
                    bytes.Add((byte)c);
            }
            else if (c == '\\')
            {
                // Escape sequence
                c = ReadByte();
                if (c == -1)
                    throw new PdfParseException($"Unterminated escape in literal string at {startPos}");

                switch (c)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '(': bytes.Add((byte)'('); break;
                    case ')': bytes.Add((byte)')'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    case '\r':
                        // Line continuation - skip \r and optional \n
                        if (PeekByte() == '\n')
                            ReadByte();
                        break;
                    case '\n':
                        // Line continuation - skip
                        break;
                    default:
                        // Check for octal escape (0-7)
                        if (c >= '0' && c <= '7')
                        {
                            int octal = c - '0';
                            for (int i = 0; i < 2; i++)
                            {
                                int next = PeekByte();
                                if (next >= '0' && next <= '7')
                                {
                                    ReadByte();
                                    octal = octal * 8 + (next - '0');
                                }
                                else
                                    break;
                            }
                            bytes.Add((byte)(octal & 0xFF));
                        }
                        else
                        {
                            // Unknown escape, just include the character
                            bytes.Add((byte)c);
                        }
                        break;
                }
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        // Return as string for the token, but could also be interpreted as bytes
        string value = Encoding.GetEncoding("ISO-8859-1").GetString(bytes.ToArray());
        return new PdfToken(PdfTokenType.LiteralString, value, startPos);
    }

    /// <summary>
    /// Read a hex string (angle bracket delimited).
    /// </summary>
    private PdfToken ReadHexString(long startPos)
    {
        var sb = new StringBuilder();

        while (true)
        {
            int c = ReadByte();
            if (c == -1)
                throw new PdfParseException($"Unterminated hex string starting at {startPos}");

            if (c == '>')
                break;

            // Skip whitespace in hex strings
            if (Whitespace.Contains((byte)c))
                continue;

            if (!IsHexDigit(c))
                throw new PdfParseException($"Invalid hex digit '{(char)c}' in hex string at {Position}");

            sb.Append((char)c);
        }

        return new PdfToken(PdfTokenType.HexString, sb.ToString(), startPos);
    }

    /// <summary>
    /// Read a name token.
    /// </summary>
    private PdfToken ReadName(long startPos)
    {
        var sb = new StringBuilder();

        while (true)
        {
            int c = PeekByte();
            if (c == -1 || Whitespace.Contains((byte)c) || Delimiters.Contains((byte)c))
                break;

            ReadByte();

            // Handle #XX escape sequences
            if (c == '#')
            {
                int h1 = ReadByte();
                int h2 = ReadByte();
                if (h1 == -1 || h2 == -1 || !IsHexDigit(h1) || !IsHexDigit(h2))
                    throw new PdfParseException($"Invalid hex escape in name at {Position}");

                int value = (HexValue(h1) << 4) | HexValue(h2);
                sb.Append((char)value);
            }
            else
            {
                sb.Append((char)c);
            }
        }

        return new PdfToken(PdfTokenType.Name, sb.ToString(), startPos);
    }

    /// <summary>
    /// Read a number (integer or real).
    /// </summary>
    private PdfToken ReadNumberOrKeyword(long startPos)
    {
        var sb = new StringBuilder();
        bool hasDecimal = false;
        bool hasExponent = false;

        // First character might be sign or digit or decimal
        int first = PeekByte();
        if (first == '+' || first == '-')
        {
            sb.Append((char)ReadByte());
        }

        // Check if it starts with a decimal point
        if (PeekByte() == '.')
        {
            hasDecimal = true;
            sb.Append((char)ReadByte());
        }

        // Read digits and possible decimal point
        while (true)
        {
            int c = PeekByte();
            if (c >= '0' && c <= '9')
            {
                sb.Append((char)ReadByte());
            }
            else if (c == '.' && !hasDecimal)
            {
                hasDecimal = true;
                sb.Append((char)ReadByte());
            }
            else if ((c == 'e' || c == 'E') && !hasExponent)
            {
                // Scientific notation (rare in PDFs but valid)
                hasExponent = true;
                hasDecimal = true; // Treat as real
                sb.Append((char)ReadByte());
                // Optional sign after exponent
                int next = PeekByte();
                if (next == '+' || next == '-')
                    sb.Append((char)ReadByte());
            }
            else
            {
                break;
            }
        }

        string value = sb.ToString();

        // Check if it's actually a number or might be a keyword
        if (value.Length == 0 || value == "+" || value == "-" || value == ".")
        {
            // Not a valid number, try to read as keyword
            Seek(startPos);
            return ReadKeyword(startPos);
        }

        var type = hasDecimal ? PdfTokenType.Real : PdfTokenType.Integer;
        return new PdfToken(type, value, startPos);
    }

    /// <summary>
    /// Read a keyword.
    /// </summary>
    private PdfToken ReadKeyword(long startPos)
    {
        var sb = new StringBuilder();

        while (true)
        {
            int c = PeekByte();
            if (c == -1 || Whitespace.Contains((byte)c) || Delimiters.Contains((byte)c))
                break;

            sb.Append((char)ReadByte());
        }

        return new PdfToken(PdfTokenType.Keyword, sb.ToString(), startPos);
    }

    /// <summary>
    /// Read a comment.
    /// </summary>
    private PdfToken ReadComment(long startPos)
    {
        var sb = new StringBuilder();

        while (true)
        {
            int c = ReadByte();
            if (c == -1 || c == '\r' || c == '\n')
                break;
            sb.Append((char)c);
        }

        return new PdfToken(PdfTokenType.Comment, sb.ToString(), startPos);
    }

    #region Byte Reading

    private int ReadByte()
    {
        if (_bufferPos >= _bufferLen)
        {
            _bufferLen = _stream.Read(_buffer, 0, BufferSize);
            _bufferPos = 0;
            if (_bufferLen == 0)
                return -1;
        }
        return _buffer[_bufferPos++];
    }

    private int PeekByte()
    {
        if (_bufferPos >= _bufferLen)
        {
            _bufferLen = _stream.Read(_buffer, 0, BufferSize);
            _bufferPos = 0;
            if (_bufferLen == 0)
                return -1;
        }
        return _buffer[_bufferPos];
    }

    private void UnreadByte()
    {
        if (_bufferPos > 0)
            _bufferPos--;
        else
            throw new InvalidOperationException("Cannot unread at start of buffer");
    }

    #endregion

    #region Character Classification

    private static bool IsNumberStart(int c) =>
        (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.';

    private static bool IsHorizontalWhitespace(int c) =>
        c is 0 or 9 or 12 or 32;

    private static bool IsRegularChar(int c) =>
        c > 32 && c < 127 && !Delimiters.Contains((byte)c);

    private static bool IsHexDigit(int c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static int HexValue(int c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        throw new ArgumentException($"Invalid hex digit: {(char)c}");
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }
}

/// <summary>
/// Exception thrown when PDF parsing fails.
/// </summary>
public class PdfParseException : Exception
{
    public PdfParseException(string message) : base(message) { }
    public PdfParseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when an encrypted PDF is opened but decryption is not yet
/// implemented. Encrypted streams in such files contain ciphertext that
/// pdfe cannot read; silently parsing the unencrypted catalog and
/// returning garbage stream bytes was the prior behaviour and produced
/// confusing failures (e.g. redactions appearing to succeed but doing
/// nothing). Callers can pass <c>allowEncrypted: true</c> to
/// <c>PdfDocument.Open</c> to suppress this and accept best-effort
/// behaviour at their own risk.
/// </summary>
public class PdfEncryptionNotSupportedException : Exception
{
    public PdfEncryptionNotSupportedException()
        : base("This PDF is encrypted and pdfe does not yet support decryption. " +
               "See https://github.com/marctjones/pdfe/issues/324. " +
               "Pass allowEncrypted: true to Open() to bypass this check (returns garbage for encrypted streams).")
    {
    }

    public PdfEncryptionNotSupportedException(string message) : base(message) { }
}
