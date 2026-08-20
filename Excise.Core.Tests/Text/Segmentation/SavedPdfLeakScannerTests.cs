using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1049 — proves the instrument, once, so the migration that converts ~20
/// files of leak assertions does not have to be argued file by file.
///
/// <para>The claim under test is exactly the one that failed in production:
/// <b>a term hidden inside a /FlateDecode stream is invisible to the raw
/// ASCII + UTF-16BE scan, and visible to this one.</b> CLAUDE.md prescribed
/// the raw form as the carrier-agnostic backstop — the thing that catches what
/// the extractor misses — and on #1040's leaking output it caught nothing:
/// 0 ASCII hits, 0 UTF-16BE hits, while mutool read the name straight out of
/// the file. excise compresses on save, so that blindness applies to every
/// assertion made over a saved document.</para>
/// </summary>
public class SavedPdfLeakScannerTests
{
    private const string Secret = "Farrar";

    /// <summary>
    /// A minimal PDF-shaped byte sequence with the term ONLY inside a
    /// compressed stream. Not a real document on purpose: the scanner is a byte
    /// tool and must not need a parseable file to work — on a corrupted or
    /// half-written output it is the last instrument still standing.
    /// </summary>
    private static byte[] BytesWithTermInsideACompressedStream()
    {
        var body = Encoding.Latin1.GetBytes(
            $"BT /F1 12 Tf 20 700 Td (Louise Anne {Secret}) Tj ET\n");

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(body, 0, body.Length);
        var deflated = compressed.ToArray();

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }

        Ascii("%PDF-1.7\n4 0 obj\n<< /Length " + deflated.Length + " /Filter /FlateDecode >>\nstream\n");
        file.Write(deflated, 0, deflated.Length);
        Ascii("\nendstream\nendobj\n%%EOF\n");
        return file.ToArray();
    }

    [Fact]
    public void TheRawScanCLAUDEmdPrescribed_IsBlindToACompressedStream()
    {
        var saved = BytesWithTermInsideACompressedStream();

        // The exact snippet CLAUDE.md gave as assertion option 1.
        var raw = Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved);

        raw.Should().NotContain(Secret,
            "this is the POINT: the prescribed scan reports clean on a file that " +
            "demonstrably contains the term. If this ever starts finding it, the " +
            "fixture stopped compressing and every assertion below proves nothing");
    }

    [Fact]
    public void TheDecompressingScanner_FindsIt()
    {
        var hits = SavedPdfLeakScanner.FindTerm(BytesWithTermInsideACompressedStream(), Secret);

        hits.Should().NotBeEmpty(
            "the term is in the file; an instrument that cannot see it is not a leak scan");
        hits.Should().Contain(h => h.Contains("inflated stream"),
            "and it must SAY WHERE — the location is what makes a red triageable " +
            "rather than a mystery");
    }

    [Fact]
    public void ACleanFile_ScansClean()
    {
        // Negative control. Without it a scanner that returned a hit for every
        // input would pass the test above.
        var clean = Encoding.Latin1.GetBytes("%PDF-1.7\n% nothing to see\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(clean, Secret).Should().BeEmpty(
            "a scanner that always finds something is not an instrument");
    }

    /// <summary>
    /// The case that makes this migration matter on REAL excise output rather
    /// than a hand-built fixture — and it is narrower than it first looks.
    ///
    /// <para>excise's writer already REFUSES to pack a dictionary carrying
    /// <c>/Title</c>, <c>/Author</c>, <c>/Subject</c>, <c>/Keywords</c>,
    /// <c>/Creator</c>, <c>/Producer</c> or <c>/Contents</c> into a compressed
    /// <c>/ObjStm</c> (<c>ContainsDocumentCarrierText</c>). That is a
    /// deliberate anti-leak measure: those carriers stay greppable in the raw
    /// bytes. Measured, an annotation's <c>/Contents</c> is written
    /// uncompressed, so the raw scan CAN see it.</para>
    ///
    /// <para><b>But that list is not the same list the scrubber handles.</b>
    /// <c>/ActualText</c> and <c>/Alt</c> — the structure-tree carriers #636
    /// was filed for — are absent from it, so a structure element holding one
    /// is packed and Flate-compressed like any other dictionary, and the raw
    /// scan cannot see it.</para>
    /// </summary>
    [Fact]
    public void AStructureTreeActualText_IsInvisibleToTheRawScan_AndVisibleHere()
    {
        var pdf = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n" +
            "5 0 obj\n<< /Type /StructTreeRoot /K [6 0 R] >>\nendobj\n" +
            $"6 0 obj\n<< /Type /StructElem /S /P /ActualText ({Secret}) >>\nendobj\n" +
            "trailer\n<< /Size 7 /Root 1 0 R >>\n%%EOF\n");

        using var doc = Excise.Core.Document.PdfDocument.Open(pdf);
        using var ms = new MemoryStream();
        doc.Save(ms);
        var saved = ms.ToArray();

        var rawScan = Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved);

        // If this flips, the writer's carrier list grew to cover /ActualText —
        // a GOOD change, and one that should delete this test rather than
        // weaken it. Do not relax the assertion to keep it passing.
        rawScan.Should().NotContain(Secret,
            "/ActualText is not in ContainsDocumentCarrierText, so its structure element " +
            "is packed into a Flate-compressed /ObjStm — invisible to the scan CLAUDE.md " +
            "prescribed, and #636 is exactly a leak through this carrier");

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().NotBeEmpty(
            "the term is in the file; an instrument that reports clean here is the one " +
            "that shipped #1040");
    }

    [Fact]
    public void ItFindsAUtf16BeTermInsideACompressedStream()
    {
        // /Info, /Contents and outline titles routinely carry UTF-16BE, and a
        // compressed object stream can hold any of them. Both dimensions —
        // compression AND encoding — have to be handled at once.
        var utf16 = Encoding.BigEndianUnicode.GetBytes(Secret);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(utf16, 0, utf16.Length);
        var deflated = compressed.ToArray();

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }
        Ascii("%PDF-1.7\n5 0 obj\n<< /Length " + deflated.Length + " /Filter /FlateDecode >>\nstream\n");
        file.Write(deflated, 0, deflated.Length);
        Ascii("\nendstream\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(file.ToArray(), Secret)
            .Should().Contain(h => h.Contains("UTF-16BE"),
                "a term that is both compressed and UTF-16BE encoded is still a leak");
    }
}
