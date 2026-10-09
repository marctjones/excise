using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #2034 — a value the file DRAWS but never holds in one string. Every positive
/// case here is a planted leak: FindTerm returned empty on each before shown
/// text was reconstructed. Every negative case guards the other direction, a
/// term that is NOT drawn must not be assembled from text on different lines,
/// words or streams.
/// </summary>
public class SavedPdfLeakScannerShownTextTests
{
    private const string Term = "ALPHAOLD";

    private static byte[] Pdf(string content, bool compressed = false) => Pdf([content], compressed);

    private static byte[] Pdf(string[] contents, bool compressed = false)
    {
        using var file = new MemoryStream();
        void Write(byte[] b) => file.Write(b, 0, b.Length);
        void Ascii(string s) => Write(Encoding.Latin1.GetBytes(s));
        Ascii("%PDF-1.7\n");
        var n = 4;
        foreach (var content in contents)
        {
            var body = Encoding.Latin1.GetBytes(content);
            if (compressed)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(body, 0, body.Length);
                body = ms.ToArray();
            }
            Ascii($"{n++} 0 obj\n<< /Length {body.Length}{(compressed ? " /Filter /FlateDecode" : "")} >>\nstream\n");
            Write(body);
            Ascii("\nendstream\nendobj\n");
        }
        Ascii("%%EOF\n");
        return file.ToArray();
    }

    private static string Text(string showing) => $"BT /F1 12 Tf 20 700 Td {showing} ET\n";

    // ── planted leaks: all invisible to the old per-string search ───────────

    [Fact]
    public void AKernedTjArray_IsFound()
    {
        var hits = SavedPdfLeakScanner.FindTerm(Pdf(Text("[(ALP) -20 (HA) 10 (OLD)] TJ")), Term);

        hits.Should().ContainSingle(h => h.Contains("shown text split across 3 string operands"));
    }

    [Fact]
    public void OneGlyphPerTj_IsFound()
    {
        var showing = string.Join(" ", Term.Select(c => $"({c}) Tj"));

        SavedPdfLeakScanner.FindTerm(Pdf(Text(showing)), Term).Should().NotBeEmpty();
    }

    [Fact]
    public void OneGlyphPerTj_WithHorizontalOnlyTd_IsFound()
    {
        var showing = string.Join(" 7 0 Td ", Term.Select(c => $"({c}) Tj"));

        SavedPdfLeakScanner.FindTerm(Pdf(Text(showing)), Term).Should().NotBeEmpty(
            "a Td with no y offset starts no new line; per-glyph positioning is the case to catch");
    }

    [Fact]
    public void AHexTjArray_IsFound()
    {
        SavedPdfLeakScanner.FindTerm(Pdf(Text("[<414C50> -20 <4841> 10 <4F4C44>] TJ")), Term).Should().NotBeEmpty();
    }

    [Fact]
    public void TwoByteHexGlyphsInSeparateTj_AreFound_AsUtf16()
    {
        var showing = string.Join(" ", Term.Select(c => $"<00{(int)c:X2}> Tj"));

        SavedPdfLeakScanner.FindTerm(Pdf(Text(showing)), Term).Should().NotBeEmpty();
    }

    [Fact]
    public void EscapedAndOctalStrings_AreDecodedBeforeJoining()
    {
        // \101 = A, \114 = L, \120 = P, \110 = H; an escaped paren and a line
        // continuation are part of the value, not of its spelling.
        var showing = "[(\\101L\\120) 5 (H\\\n\\101) -10 (\\117\\114D)] TJ";

        SavedPdfLeakScanner.FindTerm(Pdf(Text(showing)), Term).Should().NotBeEmpty();
        SavedPdfLeakScanner.FindTerm(Pdf(Text("[(AL\\() 3 (P\\)HA)] TJ")), "AL(P)HA").Should().NotBeEmpty();
    }

    [Fact]
    public void AKernedTjArray_InsideACompressedStream_IsFound()
    {
        var hits = SavedPdfLeakScanner.FindTerm(Pdf(Text("[(ALP) -20 (HA) 10 (OLD)] TJ"), compressed: true), Term);

        hits.Should().Contain(h => h.StartsWith("inflated stream #0:") && h.Contains("shown text"));
    }

    [Fact]
    public void AFormXObjectOrAppearanceStream_IsReadLikeAPage()
    {
        // The scanner reads every stream body, so a non-page stream needs no special case.
        var saved = Pdf([
            "q 1 0 0 1 0 0 cm Q\n",
            "/Tx BMC q BT /Helv 12 Tf 2 2 Td [(ALP) -20 (HA) 10 (OLD)] TJ ET Q EMC\n"]);

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().Contain(h => h.Contains("stream #1"));
    }

    [Fact]
    public void ARunStartingWithAQuoteOperator_IsFound()
    {
        SavedPdfLeakScanner.FindTerm(Pdf(Text("(first line) Tj (ALP) ' (HAOLD) Tj")), Term).Should().NotBeEmpty(
            "' starts a new line and its string is the first of the new run");
    }

    [Fact]
    public void ATermWithASpace_IsFound_WhenDrawnWithOne()
    {
        SavedPdfLeakScanner.FindTerm(Pdf(Text("[(ALPHA ) -20 (OLD)] TJ")), "ALPHA OLD").Should().NotBeEmpty();
    }

    // ── false-positive boundary ─────────────────────────────────────────────

    [Theory]
    [InlineData("(ALPHA) Tj 0 -14 Td (OLD) Tj", "Td with a y offset")]
    [InlineData("(ALPHA) Tj T* (OLD) Tj", "T*")]
    [InlineData("(ALPHA) Tj (OLD) '", "'")]
    [InlineData("(ALPHA) Tj 0 0 (OLD) \"", "\"")]
    [InlineData("(ALPHA) Tj 0 -14 TD (OLD) Tj", "TD with a y offset")]
    [InlineData("1 0 0 1 20 700 Tm (ALPHA) Tj 1 0 0 1 20 686 Tm (OLD) Tj", "Tm to another baseline")]
    [InlineData("1 0 0 1 20 700 Tm (ALPHA) Tj 0 1 -1 0 20 700 Tm (OLD) Tj", "Tm with another orientation")]
    [InlineData("[(ALPHA) -400 (OLD)] TJ", "a TJ word gap")]
    [InlineData("(ALPHA) Tj ET BT (OLD) Tj", "ET and BT")]
    public void TextOnDifferentLines_IsNotJoined(string showing, string because)
    {
        SavedPdfLeakScanner.FindTerm(Pdf(Text(showing)), Term).Should().BeEmpty(because);
    }

    [Theory]
    [InlineData("q (ALPHA) Tj Q (OLD) Tj")]
    [InlineData("(ALPHA) Tj 1 0 0 1 5 5 cm (OLD) Tj")]
    [InlineData("(ALPHA) Tj /Fm1 Do (OLD) Tj")]
    public void AGraphicsStateOrXObjectBoundary_EndsTheRun(string showing)
    {
        SavedPdfLeakScanner.FindTerm(Pdf(Text(showing)), Term).Should().BeEmpty();
    }

    [Fact]
    public void TextInTwoStreams_IsNotJoined()
    {
        var saved = Pdf([Text("(ALPHA) Tj"), Text("(OLD) Tj")]);

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty();
    }

    [Fact]
    public void TheOrderIsTheDrawnOrder()
    {
        SavedPdfLeakScanner.FindTerm(Pdf(Text("[(OLD) -20 (ALPHA)] TJ")), Term).Should().BeEmpty();
    }

    [Fact]
    public void StringsThatAreNotShown_AreNotJoined()
    {
        // Strings in a dictionary or as operands of other operators are not drawn.
        var saved = Pdf("<< /A (ALPHA) /B (OLD) >> BDC (ALPHA) (OLD) gs\n[(ALPHA) (OLD)] 0 d\n");

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty();
    }

    [Fact]
    public void AKerningNumberIsNotTextAndInlineImageDataIsNotSyntax()
    {
        var saved = Pdf("BT [(ALPHA) 65 79 (OLD)] TJ ET\nBI /W 1 /H 1 ID (X) Tj (ALPHA) Tj EI BT (OLD) Tj ET\n");

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().ContainSingle(h => h.Contains("shown text"),
            "the TJ joins ALPHA and OLD, the inline image bytes join nothing");
    }

    [Fact]
    public void SameLineTmRepeat_IsJoined_ThatIsTheDocumentedBoundary()
    {
        // Two runs on one baseline that a horizontal move separates are joined:
        // the scanner cannot see the gap's width without the font.
        SavedPdfLeakScanner.FindTerm(
            Pdf(Text("1 0 0 1 20 700 Tm (ALPHA) Tj 1 0 0 1 90 700 Tm (OLD) Tj")), Term).Should().NotBeEmpty();
    }

    [Fact]
    public void ATermInOneString_IsReportedOnce_ByTheStringScan()
    {
        var hits = SavedPdfLeakScanner.FindTerm(Pdf(Text("(ALPHAOLD) Tj (x) Tj")), Term);

        hits.Should().NotBeEmpty().And.NotContain(h => h.Contains("shown text"),
            "the run adds nothing the string scan did not already say");
    }
}
