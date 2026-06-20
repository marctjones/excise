using System.Text;

namespace Pdfe.Core.Primitives;

/// <summary>
/// PDF string object. Strings can be literal (parentheses) or hexadecimal (angle brackets).
/// ISO 32000-2:2020 Section 7.3.4.
/// </summary>
public sealed class PdfString : PdfObject, IEquatable<PdfString>
{
    /// <summary>
    /// The raw bytes of the string.
    /// </summary>
    public byte[] Bytes { get; private set; }

    /// <summary>
    /// Replaces the underlying bytes. Intended for in-place decryption
    /// of strings encountered during indirect-object parsing — the
    /// security handler XORs each PdfString's bytes with its per-object
    /// RC4 keystream. Treat as internal — public callers should not
    /// mutate strings.
    /// </summary>
    internal void ReplaceBytes(byte[] newBytes)
    {
        Bytes = newBytes ?? throw new ArgumentNullException(nameof(newBytes));
    }

    /// <summary>
    /// Whether this string was originally a hex string.
    /// </summary>
    public bool IsHex { get; }

    /// <summary>
    /// Creates a new PDF string from bytes.
    /// </summary>
    public PdfString(byte[] bytes, bool isHex = false)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        IsHex = isHex;
    }

    /// <summary>
    /// Creates a new PDF string from a .NET string using PDFDocEncoding.
    /// </summary>
    public PdfString(string text, bool isHex = false)
    {
        // Use PDFDocEncoding for simple strings, or UTF-16BE with BOM for Unicode
        bool hasNonLatin = text.Any(c => c > 255);
        if (hasNonLatin)
        {
            // UTF-16BE with BOM
            var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
            Bytes = new byte[2 + utf16.Length];
            Bytes[0] = 0xFE; // BOM
            Bytes[1] = 0xFF;
            Array.Copy(utf16, 0, Bytes, 2, utf16.Length);
        }
        else
        {
            // PDFDocEncoding (ISO-8859-1 compatible for 0-255)
            Bytes = text.Select(c => (byte)c).ToArray();
        }
        IsHex = isHex;
    }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.String;

    /// <summary>
    /// Gets the string value as decoded text.
    /// </summary>
    public string Value => DecodeToString();

    /// <summary>
    /// Decode the bytes to a .NET string.
    /// </summary>
    private string DecodeToString()
    {
        if (Bytes.Length == 0)
            return string.Empty;

        // Check for UTF-16BE BOM
        if (Bytes.Length >= 2 && Bytes[0] == 0xFE && Bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);
        }

        // Check for UTF-8 BOM
        if (Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB && Bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(Bytes, 3, Bytes.Length - 3);
        }

        // PDFDocEncoding (compatible with ISO-8859-1 for printable range)
        return DecodePdfDocEncoding(Bytes);
    }

    /// <summary>
    /// Decode bytes using PDFDocEncoding (ISO 32000-1 Annex D.3).
    /// </summary>
    /// <remarks>
    /// PDFDocEncoding agrees with Latin-1 across the printable Latin-1 range,
    /// but assigns typographic characters to code points that Latin-1 leaves
    /// as control characters — notably 0x80–0x9F (bullet, dagger, em/en dash,
    /// curly quotes, ligatures, …), 0x18–0x1F (spacing diacritics) and 0xA0
    /// (€). Decoding those with a plain <c>(char)b</c> cast yields C1 control
    /// characters that render as tofu boxes (e.g. a bookmark titled
    /// "Part I—Fundamentals" came out as "Part I□Fundamentals"). See
    /// <see cref="PdfDocEncodingTable"/>.
    /// </remarks>
    private static string DecodePdfDocEncoding(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            sb.Append(PdfDocEncodingTable[b]);
        return sb.ToString();
    }

    /// <summary>
    /// Encode text as PDFDocEncoding when every character is representable.
    /// Returns <c>false</c> instead of falling back to Unicode; security-handler
    /// password algorithms need the exact byte string attempted by the user.
    /// </summary>
    internal static bool TryEncodePdfDocEncoding(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text.Length == 0)
            return true;

        var encoded = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!PdfDocEncodingReverseTable.TryGetValue(text[i], out var b))
                return false;
            encoded[i] = b;
        }

        bytes = encoded;
        return true;
    }

    /// <summary>
    /// PDFDocEncoding → Unicode for all 256 byte values (ISO 32000-1 Table
    /// D.2). Identity for everything except the typographic code points; the
    /// two positions the spec leaves undefined (0x9F, 0xAD) fall through to
    /// their Latin-1 value so no data is lost on round-trip.
    /// </summary>
    private static readonly char[] PdfDocEncodingTable = BuildPdfDocEncodingTable();
    private static readonly IReadOnlyDictionary<char, byte> PdfDocEncodingReverseTable =
        BuildPdfDocEncodingReverseTable();

    private static char[] BuildPdfDocEncodingTable()
    {
        var table = new char[256];
        for (int i = 0; i < 256; i++)
            table[i] = (char)i; // Latin-1 default

        // 0x18–0x1F: spacing diacritics.
        table[0x18] = '˘'; // breve
        table[0x19] = 'ˇ'; // caron
        table[0x1A] = 'ˆ'; // modifier circumflex
        table[0x1B] = '˙'; // dot above
        table[0x1C] = '˝'; // double acute
        table[0x1D] = '˛'; // ogonek
        table[0x1E] = '˚'; // ring above
        table[0x1F] = '˜'; // small tilde

        // 0x80–0x9E: punctuation, symbols, ligatures, Latin letters.
        table[0x80] = '•'; // bullet
        table[0x81] = '†'; // dagger
        table[0x82] = '‡'; // double dagger
        table[0x83] = '…'; // horizontal ellipsis
        table[0x84] = '—'; // em dash
        table[0x85] = '–'; // en dash
        table[0x86] = 'ƒ'; // florin
        table[0x87] = '⁄'; // fraction slash
        table[0x88] = '‹'; // single left-pointing angle quote
        table[0x89] = '›'; // single right-pointing angle quote
        table[0x8A] = '−'; // minus sign
        table[0x8B] = '‰'; // per mille
        table[0x8C] = '„'; // double low-9 quote
        table[0x8D] = '“'; // left double quote
        table[0x8E] = '”'; // right double quote
        table[0x8F] = '‘'; // left single quote
        table[0x90] = '’'; // right single quote
        table[0x91] = '‚'; // single low-9 quote
        table[0x92] = '™'; // trademark
        table[0x93] = 'ﬁ'; // fi ligature
        table[0x94] = 'ﬂ'; // fl ligature
        table[0x95] = 'Ł'; // L with stroke
        table[0x96] = 'Œ'; // OE
        table[0x97] = 'Š'; // S with caron
        table[0x98] = 'Ÿ'; // Y with diaeresis
        table[0x99] = 'Ž'; // Z with caron
        table[0x9A] = 'ı'; // dotless i
        table[0x9B] = 'ł'; // l with stroke
        table[0x9C] = 'œ'; // oe
        table[0x9D] = 'š'; // s with caron
        table[0x9E] = 'ž'; // z with caron
        // 0x9F is undefined in PDFDocEncoding → leave as Latin-1.

        table[0xA0] = '€'; // euro
        // 0xAD is undefined in PDFDocEncoding → leave as Latin-1.

        return table;
    }

    private static IReadOnlyDictionary<char, byte> BuildPdfDocEncodingReverseTable()
    {
        var table = new Dictionary<char, byte>();
        for (int i = 0; i < PdfDocEncodingTable.Length; i++)
        {
            var c = PdfDocEncodingTable[i];
            if (!table.ContainsKey(c))
                table[c] = (byte)i;
        }

        return table;
    }

    /// <summary>
    /// Encode for writing as a literal string (parentheses).
    /// </summary>
    public string ToLiteralString()
    {
        var sb = new StringBuilder("(");
        foreach (byte b in Bytes)
        {
            switch ((char)b)
            {
                case '(':
                    sb.Append("\\(");
                    break;
                case ')':
                    sb.Append("\\)");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (b < 32 || b > 126)
                    {
                        // Octal escape for non-printable
                        sb.Append('\\');
                        sb.Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        sb.Append((char)b);
                    }
                    break;
            }
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Encode for writing as a hex string (angle brackets).
    /// </summary>
    public string ToHexString()
    {
        var sb = new StringBuilder("<");
        foreach (byte b in Bytes)
        {
            sb.Append(b.ToString("X2"));
        }
        sb.Append('>');
        return sb.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => IsHex ? ToHexString() : ToLiteralString();

    /// <summary>
    /// Create a PdfString from text (literal string).
    /// </summary>
    public static PdfString FromText(string text) => new(text, isHex: false);

    /// <summary>
    /// Create a PdfString from a hex-encoded string (without angle brackets).
    /// </summary>
    public static PdfString FromHex(string hex)
    {
        // Remove whitespace
        hex = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());

        // Odd-length hex strings are padded with 0
        if (hex.Length % 2 != 0)
            hex += "0";

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return new PdfString(bytes, isHex: true);
    }

    /// <summary>
    /// Implicit conversion from string.
    /// </summary>
    public static implicit operator PdfString(string s) => new(s);

    /// <summary>
    /// Implicit conversion to string.
    /// </summary>
    public static implicit operator string(PdfString s) => s.Value;

    /// <inheritdoc />
    public bool Equals(PdfString? other) =>
        other is not null && Bytes.SequenceEqual(other.Bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdfString other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            foreach (byte b in Bytes)
                hash = hash * 31 + b;
            return hash;
        }
    }
}
