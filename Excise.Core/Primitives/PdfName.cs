using System.Text;

namespace Excise.Core.Primitives;

/// <summary>
/// PDF name object. Names are atomic symbols uniquely defined by a sequence of characters.
/// ISO 32000-2:2020 Section 7.3.5.
/// </summary>
/// <remarks>
/// Names begin with a solidus (/) followed by the name characters.
/// Special characters in names are encoded as #XX where XX is the hex code.
/// </remarks>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    /// <summary>
    /// The name value without the leading solidus (/).
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new PDF name.
    /// </summary>
    /// <param name="value">The name value (without leading /).</param>
    public PdfName(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Name;

    /// <summary>
    /// Gets the name with the leading solidus (/).
    /// </summary>
    public override string ToString() => "/" + Value;

    /// <summary>
    /// Encode the name for writing to PDF (handles special characters).
    /// </summary>
    public string ToEncodedString()
    {
        var sb = new StringBuilder("/");
        foreach (char c in Value)
        {
            // Characters that must be encoded: null, tab, newline, form feed, carriage return, space,
            // (, ), <, >, [, ], {, }, /, %, #
            if (c < 33 || c > 126 || c == '#' || c == '/' || c == '%' ||
                c == '(' || c == ')' || c == '<' || c == '>' ||
                c == '[' || c == ']' || c == '{' || c == '}')
            {
                sb.Append('#');
                sb.Append(((int)c).ToString("X2"));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decode a name string (handles #XX escape sequences).
    /// </summary>
    /// <param name="encoded">The encoded name (without leading /).</param>
    public static string Decode(string encoded)
    {
        if (!encoded.Contains('#'))
            return encoded;

        var sb = new StringBuilder();
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '#' && i + 2 < encoded.Length)
            {
                string hex = encoded.Substring(i + 1, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                {
                    sb.Append((char)code);
                    i += 2;
                }
                else
                {
                    sb.Append(encoded[i]);
                }
            }
            else
            {
                sb.Append(encoded[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Implicit conversion from string.
    /// </summary>
    public static implicit operator PdfName(string s) => new(s);

    /// <summary>
    /// Implicit conversion to string.
    /// </summary>
    public static implicit operator string(PdfName n) => n.Value;

    /// <inheritdoc />
    public bool Equals(PdfName? other) => other is not null && Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdfName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(PdfName? left, PdfName? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(PdfName? left, PdfName? right) => !(left == right);

    #region Common PDF Names

    // Document structure
    public static readonly PdfName Type = new("Type");
    public static readonly PdfName Subtype = new("Subtype");
    public static readonly PdfName Catalog = new("Catalog");
    public static readonly PdfName Pages = new("Pages");
    public static readonly PdfName Page = new("Page");
    public static readonly PdfName Count = new("Count");
    public static readonly PdfName Kids = new("Kids");
    public static readonly PdfName Parent = new("Parent");

    // Page attributes
    public static readonly PdfName MediaBox = new("MediaBox");
    public static readonly PdfName CropBox = new("CropBox");
    public static readonly PdfName BleedBox = new("BleedBox");
    public static readonly PdfName TrimBox = new("TrimBox");
    public static readonly PdfName ArtBox = new("ArtBox");
    public static readonly PdfName Contents = new("Contents");
    public static readonly PdfName Resources = new("Resources");
    public static readonly PdfName Rotate = new("Rotate");

    // Resources
    public static readonly PdfName Font = new("Font");
    public static readonly PdfName XObject = new("XObject");
    public static readonly PdfName ExtGState = new("ExtGState");
    public static readonly PdfName ColorSpace = new("ColorSpace");
    public static readonly PdfName Pattern = new("Pattern");
    public static readonly PdfName Shading = new("Shading");
    public static readonly PdfName ProcSet = new("ProcSet");
    public static readonly PdfName Properties = new("Properties");

    // Fonts
    public static readonly PdfName BaseFont = new("BaseFont");
    public static readonly PdfName Encoding = new("Encoding");
    public static readonly PdfName ToUnicode = new("ToUnicode");
    public static readonly PdfName FontDescriptor = new("FontDescriptor");
    public static readonly PdfName Widths = new("Widths");
    public static readonly PdfName FirstChar = new("FirstChar");
    public static readonly PdfName LastChar = new("LastChar");
    public static readonly PdfName DescendantFonts = new("DescendantFonts");
    public static readonly PdfName CIDSystemInfo = new("CIDSystemInfo");
    public static readonly PdfName DW = new("DW");
    public static readonly PdfName W = new("W");

    // Font types
    public static readonly PdfName Type0 = new("Type0");
    public static readonly PdfName Type1 = new("Type1");
    public static readonly PdfName TrueType = new("TrueType");
    public static readonly PdfName CIDFontType0 = new("CIDFontType0");
    public static readonly PdfName CIDFontType2 = new("CIDFontType2");

    // Stream
    public static readonly PdfName Length = new("Length");
    public static readonly PdfName Filter = new("Filter");
    public static readonly PdfName DecodeParms = new("DecodeParms");
    public static readonly PdfName FlateDecode = new("FlateDecode");
    public static readonly PdfName ASCIIHexDecode = new("ASCIIHexDecode");
    public static readonly PdfName ASCII85Decode = new("ASCII85Decode");
    public static readonly PdfName LZWDecode = new("LZWDecode");
    public static readonly PdfName DCTDecode = new("DCTDecode");
    public static readonly PdfName CCITTFaxDecode = new("CCITTFaxDecode");
    public static readonly PdfName JBIG2Decode = new("JBIG2Decode");
    public static readonly PdfName JPXDecode = new("JPXDecode");
    public static readonly PdfName RunLengthDecode = new("RunLengthDecode");
    public static readonly PdfName Crypt = new("Crypt");

    // XObjects
    public static readonly PdfName Image = new("Image");
    public static readonly PdfName Form = new("Form");

    // Image attributes
    public static readonly PdfName Width = new("Width");
    public static readonly PdfName Height = new("Height");
    public static readonly PdfName BitsPerComponent = new("BitsPerComponent");
    public static readonly PdfName ImageMask = new("ImageMask");
    public static readonly PdfName Mask = new("Mask");
    public static readonly PdfName SMask = new("SMask");
    public static readonly PdfName DecodeArray = new("Decode");
    public static readonly PdfName Interpolate = new("Interpolate");

    // Color spaces
    public static readonly PdfName DeviceGray = new("DeviceGray");
    public static readonly PdfName DeviceRGB = new("DeviceRGB");
    public static readonly PdfName DeviceCMYK = new("DeviceCMYK");
    public static readonly PdfName CalGray = new("CalGray");
    public static readonly PdfName CalRGB = new("CalRGB");
    public static readonly PdfName Lab = new("Lab");
    public static readonly PdfName ICCBased = new("ICCBased");
    public static readonly PdfName Indexed = new("Indexed");
    public static readonly PdfName Separation = new("Separation");
    public static readonly PdfName DeviceN = new("DeviceN");

    // Graphics state
    public static readonly PdfName CA = new("CA");
    public static readonly PdfName ca = new("ca");
    public static readonly PdfName BM = new("BM");
    public static readonly PdfName AIS = new("AIS");
    public static readonly PdfName TK = new("TK");

    // Metadata
    public static readonly PdfName Info = new("Info");
    public static readonly PdfName Metadata = new("Metadata");
    public static readonly PdfName Title = new("Title");
    public static readonly PdfName Author = new("Author");
    public static readonly PdfName Subject = new("Subject");
    public static readonly PdfName Keywords = new("Keywords");
    public static readonly PdfName Creator = new("Creator");
    public static readonly PdfName Producer = new("Producer");
    public static readonly PdfName CreationDate = new("CreationDate");
    public static readonly PdfName ModDate = new("ModDate");

    // Encryption
    public static readonly PdfName Encrypt = new("Encrypt");
    public static readonly PdfName V = new("V");
    public static readonly PdfName R = new("R");
    public static readonly PdfName O = new("O");
    public static readonly PdfName U = new("U");
    public static readonly PdfName P = new("P");
    public static readonly PdfName CF = new("CF");
    public static readonly PdfName StmF = new("StmF");
    public static readonly PdfName StrF = new("StrF");

    // Cross-reference streams
    public static readonly PdfName XRef = new("XRef");
    public static readonly PdfName Size = new("Size");
    public static readonly PdfName Index = new("Index");
    public static readonly PdfName Prev = new("Prev");
    public static readonly PdfName Root = new("Root");
    public static readonly PdfName ID = new("ID");

    #endregion
}
