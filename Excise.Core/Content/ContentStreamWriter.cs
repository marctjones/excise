using System.Text;
using Excise.Core.Primitives;

namespace Excise.Core.Content;

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
    /// Write <paramref name="content"/> back out, keeping the ORIGINAL bytes of
    /// every operator that was not touched (#1093).
    ///
    /// <para>Re-serializing a whole stream to change one operator puts every
    /// other operator through this class's escaping, number formatting and
    /// inline-image reconstruction — and each of those has silently corrupted
    /// content an edit never intended to touch: inline-image syntax (#354),
    /// float formatting (#762), PDFDocEncoding octal escapes. Each was found by
    /// a leak, not by a gate. Bytes that are copied cannot be reformatted.</para>
    ///
    /// <para>An operator is copied verbatim only when it carries a source span
    /// (<see cref="ContentStreamParser.TrackSourceSpans"/>) AND its serialized
    /// form still hashes to what it hashed to at parse time. That second
    /// condition is the fail-closed one: redaction mutates some parsed operands
    /// IN PLACE — <c>MarkedContentCarrierScrubber</c> removes an
    /// <c>/ActualText</c> from a <c>BDC</c> operand dictionary, the #636 leak
    /// carrier — and copying such an operator's original bytes would put the
    /// scrubbed text back into the file. A mismatch re-serializes, which is
    /// exactly today's behaviour, so the failure mode of this check is "no
    /// worse than before".</para>
    ///
    /// <para>A stream whose operators are all present, all unmodified and still
    /// in source order round-trips BYTE-IDENTICALLY.</para>
    /// </summary>
    /// <param name="content">The (possibly edited) operator list.</param>
    /// <param name="source">The bytes <paramref name="content"/> was parsed
    /// from — <see cref="ContentStream.SourceBytes"/>.</param>
    internal byte[] Write(ContentStream content, byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var ops = content.Operators;

        // The whole-stream case: every operator verbatim, in order, tiling the
        // source from offset 0. Then the answer IS the source — including the
        // tail after the last operator, which no per-operator copy can know
        // about.
        if (IsUnmodifiedWholeStream(ops, source))
            return (byte[])source.Clone();

        _sb.Clear();
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (!CanCopyVerbatim(op, source))
            {
                WriteOperator(op);
                continue;
            }

            _sb.Append(Encoding.Latin1.GetString(source, op.SourceStart, op.SourceEnd - op.SourceStart));

            // A span ends on its operator token with no trailing separator —
            // the whitespace that followed belongs to the NEXT operator's span.
            // So a separator is needed unless the next operator is the one that
            // physically followed this one in the source; without it "Tj" and a
            // re-serialized "1 0 0 1 5 5 cm" would fuse into "Tj1 0 0 …".
            var next = i + 1 < ops.Count ? ops[i + 1] : null;
            bool followsContiguously = next != null
                && CanCopyVerbatim(next, source)
                && next.SourceStart == op.SourceEnd;
            if (!followsContiguously)
                _sb.Append('\n');
        }

        return Encoding.Latin1.GetBytes(_sb.ToString());
    }

    /// <summary>
    /// Whether every operator can be copied verbatim, in source order, with no
    /// gaps, starting at offset 0 — i.e. nothing was removed, added, reordered
    /// or modified.
    /// </summary>
    private static bool IsUnmodifiedWholeStream(IReadOnlyList<ContentOperator> ops, byte[] source)
    {
        if (ops.Count == 0) return false;

        int expected = 0;
        foreach (var op in ops)
        {
            if (!CanCopyVerbatim(op, source) || op.SourceStart != expected) return false;
            expected = op.SourceEnd;
        }

        // Anything after the last operator is whitespace/comments; copying the
        // source wholesale keeps it, which is what makes this byte-identical.
        return true;
    }

    private static bool CanCopyVerbatim(ContentOperator op, byte[] source) =>
        op.HasSourceSpan
        // Reference identity, not just bounds: an operator parsed from some
        // OTHER stream — a form XObject inlined into a page — would otherwise
        // have its offsets applied to the wrong array (#1093).
        && ReferenceEquals(op.SourceArray, source)
        && op.SourceEnd <= source.Length
        && Fingerprint(op) == op.SourceFingerprint;

    // One reusable writer per thread for fingerprinting, so the check costs no
    // allocation beyond the serialized text itself.
    [ThreadStatic] private static ContentStreamWriter? _fingerprintWriter;

    /// <summary>
    /// A hash of the operator's serialized form, used to detect that a parsed
    /// operator's operands were mutated after its source span was recorded.
    ///
    /// <para>SHA-256 truncated to 128 bits rather than a fast non-cryptographic
    /// hash on purpose: the content this hashes is written by the document's
    /// author, and in a redaction tool the document's author is the adversary.
    /// A forgeable hash would let a crafted <c>BDC</c> dictionary collide with
    /// its own scrubbed form and so survive redaction — the exact leak this
    /// check exists to prevent (#1093).</para>
    /// </summary>
    internal static UInt128 Fingerprint(ContentOperator op)
    {
        var writer = _fingerprintWriter ??= new ContentStreamWriter();
        writer._sb.Clear();
        writer.WriteOperator(op);

        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(
            Encoding.Latin1.GetBytes(writer._sb.ToString()), digest);

        return new UInt128(BitConverter.ToUInt64(digest[8..]), BitConverter.ToUInt64(digest));
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
                _sb.Append(PdfNumberFormatter.Format(r.Value));
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
