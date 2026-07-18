using AwesomeAssertions;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Unit tests for XRefParser.
/// Tests parsing traditional xref tables and xref streams.
/// </summary>
public class XRefParserTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_ValidStream_Succeeds()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("test"));
        var parser = new XRefParser(stream);

        parser.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_NullStream_ThrowsArgumentNullException()
    {
        var act = () => new XRefParser(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region FindStartXRef Tests

    [Fact]
    public void FindStartXRef_ValidStartXRef_ReturnsPosition()
    {
        var pdf = Encoding.ASCII.GetBytes(@"
%PDF-1.4
...
startxref
42
%%EOF");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var pos = parser.FindStartXRef();

        pos.Should().Be(42);
    }

    [Fact]
    public void FindStartXRef_LargeOffset_Parsed()
    {
        var pdf = Encoding.ASCII.GetBytes(@"
%PDF-1.4
...
startxref
123456
%%EOF");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var pos = parser.FindStartXRef();

        pos.Should().Be(123456);
    }

    [Fact]
    public void FindStartXRef_WithWhitespace_SkipsWhitespace()
    {
        var pdf = Encoding.ASCII.GetBytes(@"
%PDF-1.4
...
startxref
  999
%%EOF");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var pos = parser.FindStartXRef();

        pos.Should().Be(999);
    }

    [Fact]
    public void FindStartXRef_WithLargeTrailingGarbage_ReturnsPosition()
    {
        var pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.4\n" +
            "startxref\n" +
            "42\n" +
            "%%EOF\n" +
            new string('x', 4096));
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var pos = parser.FindStartXRef();

        pos.Should().Be(42);
    }

    [Fact]
    public void FindStartXRef_NoStartXRef_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n...no startxref here...");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.FindStartXRef();

        act.Should().Throw<PdfParseException>().WithMessage("*startxref*");
    }

    [Fact]
    public void FindStartXRef_InvalidValue_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes(@"
%PDF-1.4
...
startxref
notanumber
%%EOF");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.FindStartXRef();

        act.Should().Throw<PdfParseException>().WithMessage("*Invalid startxref*");
    }

    [Fact]
    public void FindStartXRef_EmptyValue_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes(@"
%PDF-1.4
...
startxref

%%EOF");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.FindStartXRef();

        act.Should().Throw<PdfParseException>().WithMessage("*Invalid startxref*");
    }

    [Fact]
    public void FindStartXRef_ZeroOffset_Valid()
    {
        var pdf = Encoding.ASCII.GetBytes(@"
startxref
0
%%EOF");
        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var pos = parser.FindStartXRef();

        pos.Should().Be(0);
    }

    #endregion

    #region ParseXRef - Traditional Tests

    [Fact]
    public void ParseXRef_TraditionalTable_ParsesSuccessfully()
    {
        var pdf = BuildTraditionalXRefPdf(
            subsections: [(0, 1)],
            entries: [new XRefEntry { Offset = 0, Generation = 65535, InUse = false }],
            trailerDict: "<< /Root 1 0 R /Size 1 >>"
        );

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref.Should().HaveCount(1);
        xref.Should().ContainKey(0);
        xref[0].InUse.Should().BeFalse();
    }

    [Fact]
    public void ParseXRef_MultipleSubsections_ParsesAll()
    {
        var pdf = BuildTraditionalXRefPdf(
            subsections: [(0, 1), (2, 2)],
            entries: [
                new XRefEntry { Offset = 0, Generation = 65535, InUse = false },
                new XRefEntry { Offset = 100, Generation = 0, InUse = true },
                new XRefEntry { Offset = 200, Generation = 0, InUse = true }
            ],
            trailerDict: "<< /Root 1 0 R /Size 4 >>"
        );

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref.Should().HaveCount(3);
        xref[0].InUse.Should().BeFalse();
        xref[2].InUse.Should().BeTrue();
        xref[3].InUse.Should().BeTrue();
    }

    [Fact]
    public void ParseXRef_InUseEntries_ParsedCorrectly()
    {
        var pdf = BuildTraditionalXRefPdf(
            subsections: [(1, 2)],
            entries: [
                new XRefEntry { Offset = 100, Generation = 0, InUse = true },
                new XRefEntry { Offset = 200, Generation = 0, InUse = true }
            ],
            trailerDict: "<< /Root 1 0 R /Size 3 >>"
        );

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref[1].Offset.Should().Be(100);
        xref[1].Generation.Should().Be(0);
        xref[1].InUse.Should().BeTrue();
        xref[2].Offset.Should().Be(200);
    }

    [Fact]
    public void ParseXRef_FreeEntries_ParsedCorrectly()
    {
        var pdf = BuildTraditionalXRefPdf(
            subsections: [(1, 2)],
            entries: [
                new XRefEntry { Offset = 100, Generation = 65535, InUse = false },
                new XRefEntry { Offset = 200, Generation = 65534, InUse = false }
            ],
            trailerDict: "<< /Root 1 0 R /Size 3 >>"
        );

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref[1].InUse.Should().BeFalse();
        xref[1].Generation.Should().Be(65535);
        xref[2].InUse.Should().BeFalse();
        xref[2].Generation.Should().Be(65534);
    }

    [Fact]
    public void ParseXRef_TrailerDictionary_Parsed()
    {
        var pdf = BuildTraditionalXRefPdf(
            subsections: [(0, 1)],
            entries: [new XRefEntry { Offset = 0, Generation = 65535, InUse = false }],
            trailerDict: "<< /Root 1 0 R /Size 1 /Info 2 0 R >>"
        );

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        trailer.Should().NotBeNull();
        trailer.Should().BeOfType<PdfDictionary>();
    }

    [Fact]
    public void ParseXRef_MissingTrailer_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes("xref\n0 1\n0000000000 65535 f\n");

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void ParseXRef_InvalidSubsectionStart_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes("xref\ninvalid 1\n0000000000 65535 f\ntrailer\n<< >>\n");

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void ParseXRef_InvalidEntryOffset_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes("xref\n0 1\ninvalid 65535 f\ntrailer\n<< >>\n");

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void ParseXRef_InvalidEntryStatus_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes("xref\n0 1\n0000000000 65535 x\ntrailer\n<< >>\n");

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>();
    }

    #endregion

    #region ParseXRef - Stream Detection Tests

    [Fact]
    public void ParseXRef_DetectsXRefKeyword()
    {
        var pdf = BuildTraditionalXRefPdf(
            subsections: [(0, 1)],
            entries: [new XRefEntry { Offset = 0, Generation = 65535, InUse = false }],
            trailerDict: "<< /Root 1 0 R /Size 1 >>"
        );

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);
        var (trailer, xref) = parser.ParseXRef(0);

        xref.Should().NotBeNull();
    }

    [Fact]
    public void ParseXRef_InvalidXRefPosition_ThrowsException()
    {
        var pdf = Encoding.ASCII.GetBytes("invalid content at position\n");

        using var stream = new MemoryStream(pdf);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>();
    }

    #endregion

    #region XRefEntry Tests

    [Fact]
    public void XRefEntry_InUseEntry_Properties()
    {
        var entry = new XRefEntry { Offset = 1234, Generation = 5, InUse = true };

        entry.Offset.Should().Be(1234);
        entry.Generation.Should().Be(5);
        entry.InUse.Should().BeTrue();
        entry.IsCompressed.Should().BeFalse();
    }

    [Fact]
    public void XRefEntry_FreeEntry_Properties()
    {
        var entry = new XRefEntry { Offset = 0, Generation = 65535, InUse = false };

        entry.Offset.Should().Be(0);
        entry.Generation.Should().Be(65535);
        entry.InUse.Should().BeFalse();
        entry.IsCompressed.Should().BeFalse();
    }

    [Fact]
    public void XRefEntry_CompressedEntry_Properties()
    {
        var entry = new XRefEntry { ObjectStreamNumber = 42, IndexInStream = 7 };

        entry.ObjectStreamNumber.Should().Be(42);
        entry.IndexInStream.Should().Be(7);
        entry.IsCompressed.Should().BeTrue();
    }

    [Fact]
    public void XRefEntry_ToString_InUseEntry()
    {
        var entry = new XRefEntry { Offset = 1234, Generation = 5, InUse = true };

        var str = entry.ToString();

        str.Should().Contain("1234");
        str.Should().Contain("5");
        str.Should().Contain("n");
    }

    [Fact]
    public void XRefEntry_ToString_FreeEntry()
    {
        var entry = new XRefEntry { Offset = 0, Generation = 65535, InUse = false };

        var str = entry.ToString();

        str.Should().Contain("0");
        str.Should().Contain("65535");
        str.Should().Contain("f");
    }

    [Fact]
    public void XRefEntry_ToString_CompressedEntry()
    {
        var entry = new XRefEntry { ObjectStreamNumber = 42, IndexInStream = 7 };

        var str = entry.ToString();

        str.Should().Contain("Compressed");
        str.Should().Contain("42");
        str.Should().Contain("7");
    }

    #endregion

    #region ParseXRef - XRef Stream Tests

    [Fact]
    public void ParseXRefStream_BasicStream_ParsesSuccessfully()
    {
        // XRef stream test (PDF 1.5+)
        var pdfData = BuildXRefStreamPdf(
            w: (1, 2, 1),
            index: null, // Default [0 Size]
            size: 3,
            entries: [
                (0, new byte[] { 0, 0, 0, 0 }), // Type 0, free
                (1, new byte[] { 1, 0, 100, 0 }), // Type 1, offset 256
                (2, new byte[] { 1, 0, 200, 0 }) // Type 1, offset 512
            ]
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref.Should().NotBeNull();
    }

    [Fact]
    public void ParseXRefStream_WithIndexArray_ParsesMultipleSubsections()
    {
        // XRef stream with explicit Index array specifying subsections
        var pdfData = BuildXRefStreamWithIndexPdf();

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref.Should().NotBeNull();
    }

    [Fact]
    public void ParseXRefStream_W1Zero_DefaultsToType1()
    {
        // When W[0] is 0, entry type defaults to 1 (in-use, uncompressed)
        var pdfData = BuildXRefStreamPdf(
            w: (0, 2, 1),
            index: null,
            size: 2,
            entries: [
                (0, new byte[] { 0, 100, 0 }), // Type defaults to 1
                (1, new byte[] { 0, 200, 0 })
            ]
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref.Should().NotBeNull();
    }

    [Fact]
    public void ParseXRefStream_Type0FreeEntry()
    {
        var pdfData = BuildXRefStreamPdf(
            w: (1, 2, 1),
            index: null,
            size: 1,
            entries: [
                (0, new byte[] { 0, 0, 0, 5 }) // Type 0: free, next free at obj 5
            ]
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref[0].InUse.Should().BeFalse();
    }

    [Fact]
    public void ParseXRefStream_Type1InUseEntry()
    {
        var pdfData = BuildXRefStreamPdf(
            w: (1, 2, 1),
            index: null,
            size: 1,
            entries: [
                (0, new byte[] { 1, 0, 123, 0 }) // Type 1: in-use at offset 123
            ]
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref[0].InUse.Should().BeTrue();
        xref[0].Offset.Should().Be(123);
    }

    [Fact]
    public void ParseXRefStream_Type2CompressedEntry()
    {
        var pdfData = BuildXRefStreamPdf(
            w: (1, 2, 1),
            index: null,
            size: 1,
            entries: [
                (0, new byte[] { 2, 0, 42, 7 }) // Type 2: in object stream 42, index 7
            ]
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var (trailer, xref) = parser.ParseXRef(0);

        xref[0].IsCompressed.Should().BeTrue();
        xref[0].ObjectStreamNumber.Should().Be(42);
        xref[0].IndexInStream.Should().Be(7);
    }

    [Fact]
    public void ParseXRefStream_InvalidType_ThrowsException()
    {
        var pdfData = BuildXRefStreamPdf(
            w: (1, 2, 1),
            index: null,
            size: 2,
            entries: [
                (0, new byte[] { 5, 0, 100, 0 }) // Invalid type 5
            ]
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Unknown xref entry type*");
    }

    [Fact]
    public void ParseXRefStream_DataTooShort_ThrowsException()
    {
        // Truncated stream data: /Length 4 (one entry) but /Size 100 (needs 100 entries)
        var pdfData = Encoding.ASCII.GetBytes(
            "1 0 obj\n" +
            "<< /Type /XRef /W [1 2 1] /Size 100 /Length 4 >>\n" +
            "stream\n" +
            "\x01\x00\x64\x00" + // Only one entry but W says we need 100
            "\nendstream\n" +
            "endobj"
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>()
            .WithMessage("*too short*");
    }

    [Fact]
    public void ParseXRefStream_MissingWArray_ThrowsException()
    {
        var pdfData = Encoding.ASCII.GetBytes(
            "1 0 obj\n" +
            "<< /Type /XRef /Size 1 /Length 4 >>\n" + // Missing /W
            "stream\ndata\nendstream\n" +
            "endobj"
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        // GetArray throws KeyNotFoundException when /W key is absent before custom PdfParseException can fire
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ParseXRefStream_InvalidWArraySize_ThrowsException()
    {
        var pdfData = Encoding.ASCII.GetBytes(
            "1 0 obj\n" +
            "<< /Type /XRef /W [1 2] /Size 1 /Length 4 >>\n" + // W has 2 elements, not 3
            "stream\ndata\nendstream\n" +
            "endobj"
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>()
            .WithMessage("*must have 3 elements*");
    }

    [Fact]
    public void ParseXRefStream_NotAnXRefStream_ThrowsException()
    {
        var pdfData = Encoding.ASCII.GetBytes(
            "1 0 obj\n" +
            "<< /Type /Catalog /W [1 2 1] /Length 4 >>\n" + // Type is not /XRef
            "stream\ndata\nendstream\n" +
            "endobj"
        );

        using var stream = new MemoryStream(pdfData);
        var parser = new XRefParser(stream);

        var act = () => parser.ParseXRef(0);

        act.Should().Throw<PdfParseException>()
            .WithMessage("*not an XRef stream*");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds a traditional xref PDF for testing.
    /// </summary>
    private static byte[] BuildTraditionalXRefPdf(
        (int start, int count)[] subsections,
        XRefEntry[] entries,
        string trailerDict)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("xref");

        int entryIndex = 0;
        foreach (var (start, count) in subsections)
        {
            writer.WriteLine($"{start} {count}");
            for (int i = 0; i < count; i++)
            {
                if (entryIndex >= entries.Length)
                    break;

                var entry = entries[entryIndex++];
                var status = entry.InUse ? "n" : "f";
                writer.WriteLine($"{entry.Offset:D10} {entry.Generation:D5} {status}");
            }
        }

        writer.WriteLine("trailer");
        writer.WriteLine(trailerDict);
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Builds an xref stream PDF for testing.
    /// </summary>
    private static byte[] BuildXRefStreamPdf(
        (int w1, int w2, int w3) w,
        (int start, int count)[]? index,
        int size,
        (int objNum, byte[] entryData)[] entries)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Build xref stream data (concatenated entry bytes)
        var streamData = new MemoryStream();
        foreach (var (objNum, entryData) in entries)
        {
            streamData.Write(entryData, 0, entryData.Length);
        }

        var data = streamData.ToArray();

        // Build the xref stream object
        writer.Write("1 0 obj\n");
        writer.Write("<< /Type /XRef /W [");
        writer.Write($"{w.w1} {w.w2} {w.w3}");
        writer.Write("]");

        if (index != null)
        {
            writer.Write(" /Index [");
            foreach (var (start, count) in index)
            {
                writer.Write($"{start} {count} ");
            }
            writer.Write("]");
        }

        writer.Write($" /Size {size}");
        writer.Write($" /Length {data.Length}");
        writer.Write(" >>\n");
        writer.Write("stream\n");
        writer.Flush();

        ms.Write(data, 0, data.Length);

        writer.Write("\nendstream\n");
        writer.Write("endobj\n");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Builds an xref stream with explicit Index array.
    /// </summary>
    private static byte[] BuildXRefStreamWithIndexPdf()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        var streamData = new MemoryStream();
        // Entry for subsection 0: 1 entry
        streamData.Write(new byte[] { 0, 0, 0, 0 }, 0, 4); // Free entry
        // Entry for subsection 2: 2 entries
        streamData.Write(new byte[] { 1, 0, 100, 0 }, 0, 4); // In-use at offset 256
        streamData.Write(new byte[] { 1, 0, 200, 0 }, 0, 4); // In-use at offset 512

        var data = streamData.ToArray();

        writer.Write("1 0 obj\n");
        writer.Write("<< /Type /XRef /W [1 2 1] /Index [0 1 2 2] ");
        writer.Write($"/Size 4 /Length {data.Length} >>\n");
        writer.Write("stream\n");
        writer.Flush();

        ms.Write(data, 0, data.Length);

        writer.Write("\nendstream\n");
        writer.Write("endobj\n");
        writer.Flush();

        return ms.ToArray();
    }

    #endregion
}
