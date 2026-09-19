using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1052 — the explicit whole-word alternative #1000's substring default
/// assumes.
///
/// <para>#1000 decided the default: <c>the</c> matches inside <c>Theodore</c>,
/// substring matching stays. What makes that decision safe is that the
/// alternative is an EXPLICIT USER CHOICE rather than a silent global rule —
/// substring is correct for a case number inside a longer citation and wrong
/// for <c>Lee</c> inside <c>Sleeman</c>, and the tool must not guess.</para>
///
/// <para>Two properties beyond "it matches fewer things":</para>
/// <list type="number">
///   <item>The rule reaches page content AND the document-level carriers
///   together (#896: a safe option that existed only in the GUI meant every
///   other caller silently got the unsafe one).</item>
///   <item>Whichever way it is set is VISIBLE IN THE RESULT
///   (<see cref="RedactionReport.WholeWord"/>) — a user who does not know which
///   rule ran cannot reason about what was left behind.</item>
/// </list>
/// </summary>
public sealed class WholeWordRedactionTests
{
    // "Sleeman met Lee" — the #1052 example, on the page and restated in
    // /Info /Title so both matchers can be checked against one fixture.
    private static byte[] SleemanPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << /Font << /F1 5 0 R >> >> >>",
        Stream("", "BT /F1 12 Tf 72 700 Td (Sleeman met Lee) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Title (Sleeman met Lee) >>");

    private static string TitleOf(PdfDocument doc) =>
        (doc.Resolve(doc.Info!.GetOptional("Title")!) as Excise.Core.Primitives.PdfString)?.Value
        ?? "";

    [Fact]
    public void Substring_IsStillTheDefault_AndGutsTheLongerWord()
    {
        // #1000's decision, pinned as a fact. Redacting "Lee" by substring also
        // takes the "lee" out of "Sleeman".
        using var doc = PdfDocument.Open(SleemanPdf());
        // #1586: StripDocumentMetadata: false. The Standard profile deletes
        // /Info wholesale, which is the right default and would make this test
        // pass for the wrong reason — an empty title matches no rule at all.
        // What is pinned here is the carrier scrub's MATCH RULE, so the carrier
        // has to survive to be read.
        var report = doc.RedactText(
            "Lee", RedactionOptions.Default with { StripDocumentMetadata = false });

        report.WholeWord.Should().BeFalse();
        report.MatchesLocated.Should().Be(2,
            "substring matching finds 'Lee' inside 'Sleeman' as well as standing alone");
        TitleOf(doc).Should().Be("Sman met",
            "the carrier scrub uses the same substring rule");
    }

    [Fact]
    public void WholeWord_MatchesOnlyTheStandaloneOccurrence()
    {
        using var doc = PdfDocument.Open(SleemanPdf());
        var report = doc.RedactText("Lee", RedactionOptions.Default with { WholeWord = true });

        report.MatchesLocated.Should().Be(1,
            "'Lee' inside 'Sleeman' is bounded by word characters on both sides");
        report.Survived.Should().Be(0);

        var text = doc.GetPage(1).Text;
        text.Should().Contain("Sleeman", "the longer word is left intact");
        text.Should().NotContain("Lee", "the standalone occurrence is gone");
    }

    [Fact]
    public void WholeWord_AppliesToTheCarrierScrubToo_NotJustPageContent()
    {
        // #896's lesson made concrete: if the carriers kept matching by
        // substring, whole-word "Lee" would leave the page correct and still
        // turn "Sleeman" into "Sman" in /Info — one redaction, two rules.
        using var doc = PdfDocument.Open(SleemanPdf());
        // StripDocumentMetadata: false — see Substring_IsStillTheDefault.
        doc.RedactText("Lee", RedactionOptions.Default with
        {
            WholeWord = true,
            StripDocumentMetadata = false,
        });

        TitleOf(doc).Should().Be("Sleeman met",
            "the carrier scrub honours the same word-boundary rule");
    }

    [Theory]
    // bounded by whitespace or punctuation — matches
    [InlineData("No. 2024-1234 (Cir.)", "2024-1234", true, "No.  (Cir.)")]
    [InlineData("Lee, J.", "Lee", true, ", J.")]
    // bounded by a word character on either side — does not match
    [InlineData("Sleeman", "Lee", true, "Sleeman")]
    [InlineData("Leeward", "Lee", true, "Leeward")]
    [InlineData("SECRET_KEY", "SECRET", true, "SECRET_KEY")]
    // the same inputs under the substring default — all match
    [InlineData("Sleeman", "Lee", false, "Sman")]
    [InlineData("SECRET_KEY", "SECRET", false, "_KEY")]
    public void BoundaryRule_IsTheOrdinaryWordCharacterRule(
        string title, string term, bool wholeWord, string expected)
    {
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("", "BT ET"),
            $"<< /Title ({title}) >>");

        using var doc = PdfDocument.Open(pdf);
        PdfDocumentSanitizer.ScrubTerms(
            doc, new[] { term }, caseSensitive: false, RedactionCarriers.All,
            CarrierScrubPolicy.Default, wholeWord);

        TitleOf(doc).Should().Be(expected);
    }

    [Fact]
    public void TheRuleThatRan_IsVisibleInTheResult()
    {
        // ⚠️ #1052's explicit requirement: not just at the moment of clicking.
        using var doc = PdfDocument.Open(SleemanPdf());
        var report = doc.RedactText("Lee", RedactionOptions.Default with { WholeWord = true });

        report.WholeWord.Should().BeTrue();
        report.ToString().Should().Contain("whole-word");
    }

    [Fact]
    public void WholeWord_StillRemovesTheTermFromEveryCarrier_NoLeak()
    {
        // The option narrows WHICH occurrences match; it must not weaken the
        // removal of the ones that do. Carrier-agnostic check over the saved
        // bytes, compressed streams included (CLAUDE.md assertion 1).
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT /F1 12 Tf 72 700 Td (ZORBLAX report) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Outlines /First 7 0 R /Count 1 >>",
            "<< /Title (ZORBLAX chapter) /Parent 6 0 R >>");

        using var doc = PdfDocument.Open(pdf);
        var report = doc.RedactText("ZORBLAX", RedactionOptions.Default with { WholeWord = true });

        report.Survived.Should().Be(0);
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "ZORBLAX").Should().BeEmpty(
            "a narrower match rule must not become a weaker removal");
    }

    private static string Stream(string dictExtra, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dictExtra} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    // The last object is the /Info dictionary when its body starts with /Title.
    private static byte[] Build(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        var infoIndex = 0;
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            if (bodies[i].StartsWith("<< /Title", System.StringComparison.Ordinal))
                infoIndex = i + 1;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        var info = infoIndex > 0 ? $" /Info {infoIndex} 0 R" : "";
        Write($"trailer\n<< /Root 1 0 R{info} /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
