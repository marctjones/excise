using System.Globalization;
using System.Text;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Content;

/// <summary>
/// Serializes a ContentStream to PDF content stream bytes.
/// ISO 32000-2:2020 Section 7.8.2.
/// </summary>
public class ContentStreamWriter
{
    private readonly StringBuilder _sb = new();

    /// <summary>
    /// Write a ContentStream to bytes.
    /// </summary>
    public byte[] Write(ContentStream content)
    {
        _sb.Clear();

        foreach (var op in content.Operators)
        {
            WriteOperator(op);
        }

        return Encoding.Latin1.GetBytes(_sb.ToString());
    }

    /// <summary>
    /// Write a single operator.
    /// </summary>
    private void WriteOperator(ContentOperator op)
    {
        // Inline images have bespoke syntax (BI <params> ID <bytes> EI) that
        // does not follow the generic "operands then name" form — the
        // parameters are bare key/value pairs (no << >> wrapper) and the
        // binary data must be emitted verbatim. (#354)
        if (op.Name == "BI")
        {
            WriteInlineImage(op);
            return;
        }

        // Write operands
        foreach (var operand in op.Operands)
        {
            WriteOperand(operand);
            _sb.Append(' ');
        }

        // Write operator name
        _sb.Append(op.Name);
        _sb.Append('\n');
    }

    /// <summary>
    /// Write an inline image operator: <c>BI</c>, the parameter key/value
    /// pairs, <c>ID</c>, the raw image bytes, then <c>EI</c> (ISO 32000-2
    /// §8.9.7). The parser stores the parameter dictionary as the single
    /// operand and the pixel bytes on <see cref="ContentOperator.InlineImageData"/>.
    /// </summary>
    private void WriteInlineImage(ContentOperator op)
    {
        _sb.Append("BI\n");

        if (op.Operands.Count > 0 && op.Operands[0] is PdfDictionary dict)
        {
            foreach (var kvp in dict)
            {
                _sb.Append('/');
                WriteName(kvp.Key.Value);
                _sb.Append(' ');
                WriteOperand(kvp.Value);
                _sb.Append('\n');
            }
        }

        // Exactly one whitespace separates ID from the data (per spec).
        _sb.Append("ID ");
        if (op.InlineImageData is { Length: > 0 } data)
        {
            // Latin1 is a 1:1 byte↔char[0..255] mapping, so appending the
            // bytes as a Latin1 string and encoding the whole buffer back to
            // Latin1 in Write() round-trips every byte losslessly.
            _sb.Append(Encoding.Latin1.GetString(data));
        }
        // Newline delimiter before EI keeps any declared /L length valid:
        // readers skip /L bytes, then whitespace, then read EI.
        _sb.Append("\nEI\n");
    }

    /// <summary>
    /// Write an operand value.
    /// </summary>
    private void WriteOperand(PdfObject obj)
    {
        switch (obj)
        {
            case PdfNull:
                _sb.Append("null");
                break;

            case PdfBoolean b:
                _sb.Append(b.Value ? "true" : "false");
                break;

            case PdfInteger i:
                _sb.Append(i.Value);
                break;

            case PdfReal r:
                var value = r.Value;
                if (Math.Abs(value - Math.Round(value)) < 0.00001)
                {
                    _sb.Append(((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    _sb.Append(value.ToString("G", CultureInfo.InvariantCulture));
                }
                break;

            case PdfString s:
                WriteStringBytes(s.Bytes);
                break;

            case PdfName n:
                _sb.Append('/');
                WriteName(n.Value);
                break;

            case PdfArray a:
                _sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) _sb.Append(' ');
                    WriteOperand(a[i]);
                }
                _sb.Append(']');
                break;

            case PdfDictionary d:
                _sb.Append("<<");
                foreach (var kvp in d)
                {
                    _sb.Append('/');
                    WriteName(kvp.Key.Value);
                    _sb.Append(' ');
                    WriteOperand(kvp.Value);
                    _sb.Append(' ');
                }
                _sb.Append(">>");
                break;

            default:
                // Unknown type - try ToString
                _sb.Append(obj.ToString());
                break;
        }
    }

    /// <summary>
    /// Write a string literal with proper escaping, operating on the
    /// PdfString's RAW BYTES directly — never <see cref="PdfString.Value"/>.
    /// <c>Value</c> decodes through PDFDocEncoding, which remaps bytes
    /// 0x18-0x1F/0x80-0x9E/0xA0 to Unicode code points above 255 (e.g. 0x99
    /// → 'Ž', U+017D = 381 decimal). Octal-escaping *that* decoded value
    /// instead of the original byte writes `\575` (381 in octal) into the
    /// content stream — a value no PDF octal escape can represent losslessly
    /// (max is `\377` = 255), so a reader re-parses it as 381 mod 256 = 125
    /// ('}'), silently corrupting the byte. This bit a Type0/CID font whose
    /// original code (preserved verbatim by <c>OperationReconstructor</c> for
    /// redaction, #353/#659) happened to fall in the remapped range — the
    /// bytes must round-trip exactly, not through a text-decoding table meant
    /// for actual PDFDocEncoded text strings.
    /// </summary>
    private void WriteStringBytes(byte[] bytes)
    {
        _sb.Append('(');

        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\\':
                    _sb.Append("\\\\");
                    break;
                case (byte)'(':
                    _sb.Append("\\(");
                    break;
                case (byte)')':
                    _sb.Append("\\)");
                    break;
                case (byte)'\n':
                    _sb.Append("\\n");
                    break;
                case (byte)'\r':
                    _sb.Append("\\r");
                    break;
                case (byte)'\t':
                    _sb.Append("\\t");
                    break;
                case (byte)'\b':
                    _sb.Append("\\b");
                    break;
                case (byte)'\f':
                    _sb.Append("\\f");
                    break;
                default:
                    if (b < 32 || b > 126)
                    {
                        // Write as octal escape — b is 0-255, so this always
                        // fits in 3 octal digits (max \377).
                        _sb.Append('\\');
                        _sb.Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        _sb.Append((char)b);
                    }
                    break;
            }
        }

        _sb.Append(')');
    }

    /// <summary>
    /// Write a name with proper encoding.
    /// </summary>
    private void WriteName(string name)
    {
        foreach (var c in name)
        {
            if (c < 33 || c > 126 || c == '#' || c == '/' || c == '[' || c == ']' ||
                c == '<' || c == '>' || c == '(' || c == ')' || c == '{' || c == '}' ||
                c == '%')
            {
                // Write as hex escape
                _sb.Append('#');
                _sb.Append(((int)c).ToString("X2"));
            }
            else
            {
                _sb.Append(c);
            }
        }
    }
}
