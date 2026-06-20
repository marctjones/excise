using System;
using System.Text;
using System.Threading;
using AwesomeAssertions;
using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Parsing;

/// <summary>
/// #346: the parsers must survive hostile input — bound recursion (deeply
/// nested arrays/dicts) with a typed exception instead of a StackOverflow, and
/// honor a CancellationToken so a caller can bound a runaway parse.
/// </summary>
public class ParserHardeningTests
{
    // ---- recursion-depth guard ----

    [Fact]
    public void PdfParser_DeeplyNestedArray_ThrowsTypedException_NotStackOverflow()
    {
        var data = Encoding.ASCII.GetBytes(new string('[', 5000));
        var parser = new PdfParser(data);

        var act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>().WithMessage("*nesting depth*");
    }

    [Fact]
    public void PdfParser_DeeplyNestedDictionary_ThrowsTypedException()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("<< /a ", 5000)));
        var parser = new PdfParser(data);

        var act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>().WithMessage("*nesting depth*");
    }

    [Fact]
    public void ContentStreamParser_DeeplyNestedArray_ThrowsTypedException()
    {
        // A content-stream operand of 5000 nested arrays would recurse
        // ParseArray->ParseToken->ParseArray to a StackOverflow without a guard.
        var content = Encoding.ASCII.GetBytes(new string('[', 5000));
        var parser = new ContentStreamParser(content);

        var act = () => parser.Parse();

        act.Should().Throw<PdfParseException>().WithMessage("*nesting depth*");
    }

    [Fact]
    public void ContentStreamParser_LegitimateNesting_ParsesFine()
    {
        // A normal TJ array with a modest nested array still parses.
        var content = Encoding.ASCII.GetBytes("BT /F1 12 Tf [ (a) -10 (b) [1 2] ] TJ ET");
        var parser = new ContentStreamParser(content);

        var result = parser.Parse();

        result.Operators.Should().Contain(op => op.Name == "TJ");
    }

    // ---- cancellation ----

    [Fact]
    public void ContentStreamParser_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var parser = new ContentStreamParser(Encoding.ASCII.GetBytes("BT /F1 12 Tf (hello) Tj ET"));

        var act = () => parser.Parse(cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ContentStreamParser_DefaultToken_DoesNotCancel()
    {
        var parser = new ContentStreamParser(Encoding.ASCII.GetBytes("BT /F1 12 Tf (hello) Tj ET"));

        var act = () => parser.Parse(); // CancellationToken.None

        act.Should().NotThrow();
    }

    [Fact]
    public void PdfParser_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var parser = new PdfParser(Encoding.ASCII.GetBytes("[1 2 3]")) { CancellationToken = cts.Token };

        var act = () => parser.ParseObject();

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void PdfParser_StreamMissingLength_RecoversByEndstreamMarker()
    {
        var parser = new PdfParser(Encoding.ASCII.GetBytes("<< >>\nstream\nBT\nendstream\n"));

        var stream = parser.ParseObject().Should().BeOfType<PdfStream>().Subject;

        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("BT");
    }

    [Fact]
    public void PdfParser_StreamWithWrongLength_RecoversByEndstreamMarker()
    {
        var parser = new PdfParser(Encoding.ASCII.GetBytes(
            "<< /Length 4 >>\nstream\nBT /F1 12 Tf\nendstream\n"));

        var stream = parser.ParseObject().Should().BeOfType<PdfStream>().Subject;

        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("BT /F1 12 Tf");
    }

    [Fact]
    public void PdfParser_DictionaryMissingSlashOnKnownKey_RecoversKey()
    {
        var parser = new PdfParser(Encoding.ASCII.GetBytes(
            "<< /BaseFont /Arial ToUnicode 37 0 R >>"));

        var dict = parser.ParseObject().Should().BeOfType<PdfDictionary>().Subject;

        dict.GetOptional("ToUnicode").Should().BeOfType<PdfReference>()
            .Which.ObjectNum.Should().Be(37);
    }

    [Fact]
    public void PdfParser_DictionaryStrayKeywordBeforeNextKey_SkipsFragment()
    {
        var parser = new PdfParser(Encoding.ASCII.GetBytes(
            "<< /BaseFont /Arial,Unicode MS /ToUnicode 37 0 R >>"));

        var dict = parser.ParseObject().Should().BeOfType<PdfDictionary>().Subject;

        dict.GetOptional("ToUnicode").Should().BeOfType<PdfReference>()
            .Which.ObjectNum.Should().Be(37);
    }

    [Fact]
    public void PdfDocument_Open_HeaderWithinFirstKilobyte_Succeeds()
    {
        var pdf = BuildMinimalPdf(prefix: "% transport wrapper\n");

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_BrokenStartXrefFallsBackToLastXrefTable_Succeeds()
    {
        var pdf = BuildMinimalPdf(corruptStartXref: true);

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_RecoveredXrefTableRepairsBadObjectOffsets_Succeeds()
    {
        var pdf = BuildMinimalPdf(corruptStartXref: true, corruptObjectOffsets: true);

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_PrimaryXrefTableWithImpossibleObjectOffset_RepairsOffsets()
    {
        var pdf = BuildMinimalPdf(corruptObjectOffsets: true);

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_MissingXrefTableReconstructsObjectsFromTrailer_Succeeds()
    {
        var pdf = BuildMinimalPdf(omitXrefTable: true);

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_TrailerBeforeObjectsReconstructsObjects_Succeeds()
    {
        var pdf = BuildPdfWithTrailerBeforeObjects();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_BareDictionaryObjectsAndTrailer_ReconstructsObjects_Succeeds()
    {
        var pdf = BuildPdfWithBareDictionaryObjectsAndTrailer();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_TrailerWithMalformedOptionalId_ReconstructsObjects_Succeeds()
    {
        var pdf = BuildPdfWithMalformedOptionalTrailerEntry();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_InvalidPreviousXrefPointer_IgnoresBrokenIncrementalLink()
    {
        var pdf = BuildPdfWithInvalidPreviousXrefPointer();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_PreviousXrefPointerInsideSubsection_NormalizesToXrefKeyword()
    {
        var pdf = BuildPdfWithPreviousXrefPointerInsideSubsection();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_CorruptXrefStream_UsesStreamDictionaryAsTrailerAndRebuildsOffsets()
    {
        var pdf = BuildPdfWithCorruptXrefStream();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.5");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_MissingTrailerSynthesizesRootFromUniqueCatalog()
    {
        var pdf = BuildPdfWithObjectsButNoTrailer();

        using var doc = PdfDocument.Open(pdf);

        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_MalformedIndirectEncryptObject_IgnoresBrokenEncryptForReadableContent()
    {
        var pdf = BuildPdfWithMalformedIndirectEncryptObject();

        using var doc = PdfDocument.Open(pdf);

        doc.IsEncrypted.Should().BeTrue();
        doc.PageCount.Should().Be(0);
    }

    [Fact]
    public void PdfDocument_Open_ZeroPaddedOverlongAes256EncryptionStrings_TrimsPadding()
    {
        const string pdf = "../../../../test-pdfs/pdfjs/empty_protected.pdf";
        if (!File.Exists(pdf)) return;

        using var doc = PdfDocument.Open(pdf);

        doc.IsEncrypted.Should().BeTrue();
    }

    private static byte[] BuildMinimalPdf(
        string prefix = "",
        bool corruptStartXref = false,
        bool corruptObjectOffsets = false,
        bool omitXrefTable = false)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.Write(prefix);
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[3];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        if (!omitXrefTable)
        {
            writer.WriteLine("xref");
            writer.WriteLine("0 3");
            writer.WriteLine("0000000000 65535 f ");
            writer.WriteLine($"{(corruptObjectOffsets ? 123 : offsets[1]):D10} 00000 n ");
            writer.WriteLine($"{(corruptObjectOffsets ? 456 : offsets[2]):D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 3 >>");
        if (!omitXrefTable)
        {
            writer.WriteLine("startxref");
            writer.WriteLine((corruptStartXref ? 999999 : xrefPos)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteLine("%%EOF");
        }
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithTrailerBeforeObjects()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 3 >>");
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithBareDictionaryObjectsAndTrailer()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("1 0 obj");
        writer.WriteLine("/Type /Catalog");
        writer.WriteLine("/Pages 2 0 R");
        writer.WriteLine("endobj");
        writer.WriteLine("2 0 obj");
        writer.WriteLine("/Type /Pages");
        writer.WriteLine("/Kids []");
        writer.WriteLine("/Count 0");
        writer.WriteLine("endobj");
        writer.WriteLine("trailer");
        writer.WriteLine("/Root 1 0 R");
        writer.WriteLine("/Size 3");
        writer.WriteLine("startxref");
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithMalformedOptionalTrailerEntry()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 3 /ID [<904E5A162F03815B");
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithInvalidPreviousXrefPointer()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var object1 = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var object2 = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var brokenPrev = ms.Position;
        writer.WriteLine("not an xref section");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 3");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{object1:D10} 00000 n ");
        writer.WriteLine($"{object2:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size 3 /Prev {brokenPrev} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithPreviousXrefPointerInsideSubsection()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var object1 = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var object2 = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var previousXref = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 3");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{object1:D10} 00000 n ");
        writer.WriteLine($"{object2:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 3 >>");
        writer.Flush();

        var latestXref = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 0");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size 3 /Prev {previousXref + 8} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(latestXref.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithCorruptXrefStream()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.5");
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefStreamPos = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /XRef /Root 1 0 R /Size 4 /W [1 2 1] /Length 4 /Filter /ASCII85Decode >>");
        writer.WriteLine("stream");
        writer.WriteLine("bad!");
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefStreamPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithObjectsButNoTrailer()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithMalformedIndirectEncryptObject()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var object1 = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var object2 = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [] /Count 0 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var object3 = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("E< /Filter /Standard /V 5 /R 6 /Length 256 >");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 4");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{object1:D10} 00000 n ");
        writer.WriteLine($"{object2:D10} 00000 n ");
        writer.WriteLine($"{object3:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 4 /Encrypt 3 0 R /ID [(id) (id)] >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
