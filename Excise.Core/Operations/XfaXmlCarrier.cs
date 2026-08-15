using System.Text;
using System.Xml;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Operations;

/// <summary>
/// Reads and rewrites the XML Forms Architecture payload in /AcroForm /XFA.
/// </summary>
/// <remarks>
/// /XFA is either one stream containing a complete XDP document or an array of
/// packet-name/stream pairs. Packet arrays commonly split the opening and
/// closing XDP elements into separate streams, so the array must first be
/// considered as one XML document rather than parsing each stream in isolation.
/// </remarks>
internal static class XfaXmlCarrier
{
    private const long MaxXmlCharacters = 128L * 1024 * 1024;

    internal sealed record ScrubResult(bool Present, bool Changed, int UnexaminedPacketCount);

    internal static ScrubResult ScrubTerms(
        PdfDocument document,
        IReadOnlyList<string> terms,
        bool caseSensitive)
    {
        if (!TryGetXfa(document, out var acroForm, out var xfa))
            return new ScrubResult(false, false, 0);

        if (document.Resolve(xfa) is PdfStream singleStream)
        {
            if (!TryScrubXml(singleStream.DecodedData, terms, caseSensitive,
                    out var rewritten, out var changed, out var hasRemainingTerm))
            {
                return new ScrubResult(true, false,
                    ContainsAnyTerm(singleStream.DecodedData, terms, caseSensitive) ? 1 : 0);
            }

            if (changed)
                ReplaceStreamData(singleStream, rewritten);

            return new ScrubResult(true, changed, hasRemainingTerm ? 1 : 0);
        }

        if (document.Resolve(xfa) is not PdfArray packets)
            return new ScrubResult(true, false, 1);

        var streams = ResolvePacketStreams(document, packets);
        if (streams.Count == 0)
            return new ScrubResult(true, false, 1);

        // The standard packet-array shape is one XDP document split over the
        // streams (preamble opens xdp:xdp; postamble closes it). Parsing the
        // concatenation is the only way to validate that shape as XML.
        var combined = Concatenate(streams);
        if (TryScrubXml(combined, terms, caseSensitive,
                out var combinedRewrite, out var combinedChanged, out var combinedHasRemainingTerm))
        {
            if (combinedChanged)
            {
                // Both a single XDP stream and a packet array are legal /XFA
                // values. Replacing a changed array with one complete stream
                // avoids unsafe byte-offset splitting after XML serialization.
                acroForm["XFA"] = document.AddIndirectObject(new PdfStream(combinedRewrite));
            }

            return new ScrubResult(true, combinedChanged, combinedHasRemainingTerm ? 1 : 0);
        }

        // Some producers emit independently well-formed packet streams without
        // the preamble/postamble wrapper. Handle those in place. Any stream that
        // both fails XML parsing and contains a requested term remains explicitly
        // unexamined; callers surface that rather than falling back to byte splice.
        var anyChanged = false;
        var unexamined = 0;
        foreach (var stream in streams)
        {
            if (TryScrubXml(stream.DecodedData, terms, caseSensitive,
                    out var rewritten, out var changed, out var hasRemainingTerm))
            {
                if (changed)
                {
                    ReplaceStreamData(stream, rewritten);
                    anyChanged = true;
                }
                if (hasRemainingTerm) unexamined++;
            }
            else if (ContainsAnyTerm(stream.DecodedData, terms, caseSensitive))
            {
                unexamined++;
            }
        }

        return new ScrubResult(true, anyChanged, unexamined);
    }

    internal static int CountStreams(PdfDocument document)
    {
        if (!TryGetXfa(document, out _, out var xfa)) return 0;

        return document.Resolve(xfa) switch
        {
            PdfStream => 1,
            PdfArray packets => ResolvePacketStreams(document, packets).Count,
            _ => 1,
        };
    }

    internal static int CountUnexaminedPackets(
        PdfDocument document,
        IReadOnlyList<string>? requestedTerms,
        bool caseSensitive = false)
    {
        if (!TryGetXfa(document, out _, out var xfa)) return 0;

        var streams = document.Resolve(xfa) switch
        {
            PdfStream stream => new List<PdfStream> { stream },
            PdfArray packets => ResolvePacketStreams(document, packets),
            _ => new List<PdfStream>(),
        };

        if (requestedTerms == null || requestedTerms.Count == 0)
            return streams.Count == 0 ? 1 : streams.Count;

        if (streams.Count == 0) return 1;

        var combined = Concatenate(streams);
        if (TryLoadXml(combined, out var documentXml, out _))
            return XmlContainsAnyTerm(documentXml, requestedTerms, caseSensitive) ? 1 : 0;

        var count = 0;
        foreach (var stream in streams)
        {
            if (TryLoadXml(stream.DecodedData, out var packetXml, out _))
            {
                if (XmlContainsAnyTerm(packetXml, requestedTerms, caseSensitive)) count++;
            }
            else if (ContainsAnyTerm(stream.DecodedData, requestedTerms, caseSensitive))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryGetXfa(
        PdfDocument document,
        out PdfDictionary acroForm,
        out PdfObject xfa)
    {
        acroForm = null!;
        xfa = null!;

        if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance)
            is not PdfDictionary resolvedAcroForm)
        {
            return false;
        }

        var candidate = resolvedAcroForm.GetOptional("XFA");
        if (candidate == null) return false;

        acroForm = resolvedAcroForm;
        xfa = candidate;
        return true;
    }

    private static List<PdfStream> ResolvePacketStreams(PdfDocument document, PdfArray packets)
    {
        var streams = new List<PdfStream>();
        foreach (var item in packets)
        {
            if (document.Resolve(item) is PdfStream stream)
                streams.Add(stream);
        }
        return streams;
    }

    private static byte[] Concatenate(IReadOnlyList<PdfStream> streams)
    {
        var length = streams.Sum(s => (long)s.DecodedData.Length);
        if (length > int.MaxValue)
            throw new InvalidDataException("The combined XFA packet data is too large to process.");

        var combined = new byte[(int)length];
        var offset = 0;
        foreach (var stream in streams)
        {
            var data = stream.DecodedData;
            Buffer.BlockCopy(data, 0, combined, offset, data.Length);
            offset += data.Length;
        }
        return combined;
    }

    private static bool TryScrubXml(
        byte[] bytes,
        IReadOnlyList<string> terms,
        bool caseSensitive,
        out byte[] rewritten,
        out bool changed,
        out bool hasRemainingTerm)
    {
        rewritten = bytes;
        changed = false;
        hasRemainingTerm = false;
        if (!TryLoadXml(bytes, out var document, out var encoding)) return false;

        foreach (var attribute in document.Descendants().Attributes()
                     .Where(a => !a.IsNamespaceDeclaration))
        {
            var scrubbed = Excise(attribute.Value, terms, caseSensitive);
            if (scrubbed == attribute.Value) continue;
            attribute.Value = scrubbed;
            changed = true;
        }

        foreach (var text in document.DescendantNodes().OfType<XText>())
        {
            var scrubbed = Excise(text.Value, terms, caseSensitive);
            if (scrubbed == text.Value) continue;
            text.Value = scrubbed;
            changed = true;
        }

        foreach (var comment in document.DescendantNodes().OfType<XComment>())
        {
            var scrubbed = Excise(comment.Value, terms, caseSensitive);
            if (scrubbed == comment.Value) continue;
            comment.Value = scrubbed;
            changed = true;
        }

        foreach (var instruction in document.DescendantNodes().OfType<XProcessingInstruction>())
        {
            var scrubbed = Excise(instruction.Data, terms, caseSensitive);
            if (scrubbed == instruction.Data) continue;
            instruction.Data = scrubbed;
            changed = true;
        }

        if (changed)
            rewritten = Serialize(document, encoding);

        // A term can be split over XML nodes (for example, inline formatting).
        // We do not guess how to rewrite across those semantic boundaries, but
        // we do detect it so the caller can report the packet as unexamined.
        hasRemainingTerm = XmlContainsAnyTerm(document, terms, caseSensitive);

        return true;
    }

    private static bool TryLoadXml(byte[] bytes, out XDocument document, out Encoding encoding)
    {
        document = null!;
        encoding = DetectEncoding(bytes);

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
                MaxCharactersInDocument = MaxXmlCharacters,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return document.Root != null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        // XML without a BOM defaults to UTF-8. XmlReader still honors an
        // encoding declaration while parsing; XFA encountered in practice is
        // overwhelmingly UTF-8, and preserving BOM/no-BOM is what affects the
        // serialized packet contract.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static byte[] Serialize(XDocument document, Encoding encoding)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = encoding,
            OmitXmlDeclaration = document.Declaration == null,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            CloseOutput = false,
        }))
        {
            document.Save(writer);
        }
        return output.ToArray();
    }

    private static void ReplaceStreamData(PdfStream stream, byte[] bytes)
    {
        stream.DecodedData = bytes;
        stream["Length"] = new PdfInteger(bytes.Length);
    }

    private static bool XmlContainsAnyTerm(
        XDocument document,
        IReadOnlyList<string> terms,
        bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return document.Descendants().Attributes().Where(a => !a.IsNamespaceDeclaration)
                   .Any(a => terms.Any(t => a.Value.Contains(t, comparison)))
            || document.DescendantNodes().OfType<XText>()
                   .Any(n => terms.Any(t => n.Value.Contains(t, comparison)))
            || document.DescendantNodes().OfType<XComment>()
                   .Any(n => terms.Any(t => n.Value.Contains(t, comparison)))
            || document.DescendantNodes().OfType<XProcessingInstruction>()
                   .Any(n => terms.Any(t => n.Data.Contains(t, comparison)))
            || document.Descendants()
                   .Any(e => terms.Any(t => e.Value.Contains(t, comparison)));
    }

    private static bool ContainsAnyTerm(
        byte[] bytes,
        IReadOnlyList<string> terms,
        bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var encodings = new[]
        {
            Encoding.UTF8,
            Encoding.BigEndianUnicode,
            Encoding.Unicode,
            Encoding.Latin1,
        };

        return encodings.Any(encoding =>
        {
            var text = encoding.GetString(bytes);
            return terms.Any(term => text.Contains(term, comparison));
        });
    }

    private static string Excise(string value, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var result = value;
        foreach (var term in terms)
            result = result.Replace(term, string.Empty, comparison);
        return result;
    }
}
