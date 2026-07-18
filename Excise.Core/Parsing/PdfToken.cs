namespace Excise.Core.Parsing;

/// <summary>
/// Represents a token from the PDF lexer.
/// </summary>
public readonly struct PdfToken
{
    /// <summary>
    /// The type of token.
    /// </summary>
    public PdfTokenType Type { get; }

    /// <summary>
    /// The raw string value of the token.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Position in the stream where this token starts.
    /// </summary>
    public long Position { get; }

    /// <summary>
    /// Creates a new PDF token.
    /// </summary>
    public PdfToken(PdfTokenType type, string value, long position)
    {
        Type = type;
        Value = value;
        Position = position;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Type}: '{Value}' @ {Position}";

    /// <summary>
    /// Check if this is a specific keyword.
    /// </summary>
    public bool IsKeyword(string keyword) =>
        Type == PdfTokenType.Keyword && Value == keyword;

    /// <summary>
    /// Check if this is an end-of-file token.
    /// </summary>
    public bool IsEof => Type == PdfTokenType.Eof;

    /// <summary>
    /// Check if this is a number token (integer or real).
    /// </summary>
    public bool IsNumber => Type == PdfTokenType.Integer || Type == PdfTokenType.Real;
}

/// <summary>
/// Types of PDF tokens.
/// </summary>
public enum PdfTokenType
{
    /// <summary>End of file/stream.</summary>
    Eof,

    /// <summary>Integer number (e.g., 123, -45).</summary>
    Integer,

    /// <summary>Real number (e.g., 3.14, -0.5, .25).</summary>
    Real,

    /// <summary>Literal string in parentheses (Hello).</summary>
    LiteralString,

    /// <summary>Hexadecimal string in angle brackets &lt;48656C6C6F&gt;.</summary>
    HexString,

    /// <summary>Name starting with / (e.g., /Type).</summary>
    Name,

    /// <summary>Keyword (true, false, null, obj, endobj, stream, endstream, xref, trailer, startxref, R).</summary>
    Keyword,

    /// <summary>Array start [.</summary>
    ArrayStart,

    /// <summary>Array end ].</summary>
    ArrayEnd,

    /// <summary>Dictionary start &lt;&lt;.</summary>
    DictionaryStart,

    /// <summary>Dictionary end &gt;&gt;.</summary>
    DictionaryEnd,

    /// <summary>Comment starting with %.</summary>
    Comment
}
