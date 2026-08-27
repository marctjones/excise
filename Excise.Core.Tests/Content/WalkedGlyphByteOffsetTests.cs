using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #1092 — WalkedGlyph must carry the code's byte offset WITHIN its operand and
/// which TJ element it came from, so operand-level rewrite (#1091) can edit the
/// right bytes. The offset is NOT the decoded character index: under a 2-byte
/// CID encoding the two diverge, and writing to the wrong one corrupts a
/// neighbouring glyph. Verified against a real CID font's raw bytes.
/// </summary>
public class WalkedGlyphByteOffsetTests
{
    private struct RunSink : IContentStreamSink
    {
        public List<List<WalkedGlyph>> Runs;
        public void OnOperator(string name, List<PdfObject> operands) { }
        public void OnInlineImage(PdfDictionary imageParams, byte[] imageData) { }
        public void OnTextShowBegin() { }
        public void OnStringBegin() => Runs.Add(new List<WalkedGlyph>());
        public void OnGlyph(in WalkedGlyph glyph) => Runs[^1].Add(glyph);
        public void OnStringEnd(int byteCount) { }
        public void OnTextShowEnd() { }
        public void OnTjAdjustment(double adjustment) { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    private static List<List<WalkedGlyph>> Walk(PdfPage page)
    {
        var sink = new RunSink { Runs = new List<List<WalkedGlyph>>() };
        var walker = new ContentStreamWalker(page.GetContentStreamBytes(), page);
        if (page.Resources != null) walker.PushResources(page.Resources);
        walker.Walk(ref sink, default);
        return sink.Runs;
    }

    [Fact]
    public void CidFont_ByteOffsetTracksRawBytes_NotTheCharacterIndex()
    {
        var path = Path.Combine(RepoRoot(), "test-pdfs", "pdfjs", "cid_cff.pdf");
        Assert.SkipUnless(File.Exists(path), "CID fixture absent [requires: corpus:pdfjs]");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var runs = Walk(doc.GetPage(1)).Where(r => r.Count > 0).ToList();
        runs.Should().NotBeEmpty();

        var twoByteRuns = runs.Where(r => r.Any(g => g.ByteLength == 2)).ToList();
        twoByteRuns.Should().NotBeEmpty("cid_cff.pdf uses a 2-byte Identity encoding");

        foreach (var run in twoByteRuns)
        {
            run[0].OperandByteOffset.Should().Be(0, "the first code sits at the start of the operand");
            var expected = 0;
            foreach (var g in run)
            {
                g.OperandByteOffset.Should().Be(expected,
                    "each code's offset is the sum of the preceding codes' byte lengths, " +
                    "which is NOT the character index once codes are 2 bytes");
                expected += g.ByteLength;
            }
            // The offset genuinely diverges from the char index: by the 2nd
            // glyph a 2-byte run is at byte 2, char index 1.
            if (run.Count > 1)
                run[1].OperandByteOffset.Should().Be(2).And.NotBe(1);
        }
    }

    [Fact]
    public void TjArray_ElementIndex_IdentifiesTheSourceElement()
    {
        // [(AB) -10 (CDE)] TJ — element 0 is "AB", element 1 is the adjustment,
        // element 2 is "CDE". A plain Tj must report element index -1.
        var content = "BT /F1 12 Tf 10 10 Td [(AB) -10 (CDE)] TJ (Z) Tj ET\n";
        var body = Encoding.Latin1.GetByteCount(content);
        var pdf = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
            $"4 0 obj\n<< /Length {body} >>\nstream\n{content}endstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");

        using var doc = PdfDocument.Open(pdf);
        var glyphs = Walk(doc.GetPage(1)).SelectMany(r => r).ToList();

        char C(WalkedGlyph g) => g.Unicode.Length == 1 ? g.Unicode[0] : '?';
        glyphs.Single(g => C(g) == 'A').Should().Match<WalkedGlyph>(g => g.TjElementIndex == 0 && g.OperandByteOffset == 0);
        glyphs.Single(g => C(g) == 'B').Should().Match<WalkedGlyph>(g => g.TjElementIndex == 0 && g.OperandByteOffset == 1);
        glyphs.Single(g => C(g) == 'C').Should().Match<WalkedGlyph>(g => g.TjElementIndex == 2 && g.OperandByteOffset == 0);
        glyphs.Single(g => C(g) == 'E').Should().Match<WalkedGlyph>(g => g.TjElementIndex == 2 && g.OperandByteOffset == 2);
        glyphs.Single(g => C(g) == 'Z').Should().Match<WalkedGlyph>(g => g.TjElementIndex == -1, "a plain Tj is not a TJ array element");
    }
}
