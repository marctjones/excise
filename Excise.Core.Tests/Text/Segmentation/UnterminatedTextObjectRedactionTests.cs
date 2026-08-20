using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1039 — a <c>BT</c> that is never closed with <c>ET</c>.
///
/// <para>Every real viewer treats end-of-content as an implicit <c>ET</c>
/// (§9.4), and excise's EXTRACTION always did — the text was found. Removal
/// did not: <c>GlyphRemover.IdentifyTextBlocks</c> only recorded a block when
/// it saw a closing <c>ET</c>, so an unterminated block contributed no
/// reconstruction candidates at all. The glyphs stayed, the letter stream came
/// back identical, <c>RedactText</c> read that as a stall, and the page went to
/// the whole-operator fallback — which deleted the entire line (#1038) while
/// reporting a match count inflated by the retries (#1043).</para>
///
/// <para>So one malformed-but-tolerated construct produced BOTH failure modes
/// this project cares about at once: text destroyed that was never targeted,
/// and a count that claimed more work than was done.</para>
/// </summary>
public class UnterminatedTextObjectRedactionTests
{
    /// <summary>
    /// Two text objects. The first closes properly; the second runs to
    /// end-of-content with no <c>ET</c> — the shape in
    /// <c>test-pdfs/pdfium/hello_world_split_streams.pdf</c>, reproduced here
    /// so the gate does not depend on a gitignored corpus.
    /// </summary>
    private static byte[] BuildPdfWithUnterminatedBlock() => ContentStreamFixture.Build(
        "BT /F1 12 Tf 20 700 Td (Hello, world!) Tj ET\n" +
        "BT /F1 12 Tf 20 650 Td (Greetings, world!) Tj\n");

    private static byte[] RedactAndSave(byte[] pdf, string term, out int reported)
    {
        using var doc = PdfDocument.Open(pdf);
        reported = doc.RedactText(term).VerifiedRemovals;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void TextInAnUnterminatedBlock_IsRemovedFromTheSavedBytes()
    {
        var saved = RedactAndSave(BuildPdfWithUnterminatedBlock(), "world", out _);

        // CARRIER-AGNOSTIC, and decompressing. Not excise's extractor refereeing
        // excise's removal — if the term is anywhere in the file, in any
        // carrier, this fails.
        //
        // The raw-byte form of this scan (#1040) would pass here only by luck:
        // it sees nothing inside a /FlateDecode stream, and excise compresses on
        // save. Which streams happen to stay uncompressed is a writer detail,
        // not a property this gate may depend on.
        SavedPdfLeakScanner.FindTerm(saved, "world").Should().BeEmpty(
            "the second occurrence lives in a BT block with no ET; before #1039 it " +
            "survived redaction entirely while RedactText reported success");
    }

    [Fact]
    public void TheUnterminatedBlocksOtherText_SurvivesRedaction()
    {
        var pdf = BuildPdfWithUnterminatedBlock();
        using (var before = PdfDocument.Open(pdf))
            before.GetPage(1).Text.Should().Contain("Greetings",
                "guard: the fixture must actually contain the text this test claims to protect");

        var saved = RedactAndSave(pdf, "world", out _);

        using var after = PdfDocument.Open(saved);
        after.GetPage(1).Text.Should().Contain("Greetin",
            "redacting 'world' must not take the rest of the line with it — before #1039 " +
            "this block reached the whole-operator fallback and the line vanished entirely");
    }

    [Fact]
    public void TheReportedCount_DoesNotExceedTheOccurrencesPresent()
    {
        RedactAndSave(BuildPdfWithUnterminatedBlock(), "world", out var reported);

        // 'world' appears exactly twice. Before #1039 the second occurrence
        // could not be removed, so the loop retried and re-counted it — the
        // number grew precisely because less work succeeded (#1043).
        reported.Should().BeLessThanOrEqualTo(2,
            "the count must never exceed the occurrences in the document; a larger " +
            "number means retries were counted as separate removals");
    }

    [Fact]
    public void TheRewrittenStream_DoesNotNestTextObjects()
    {
        var saved = RedactAndSave(BuildPdfWithUnterminatedBlock(), "world", out _);

        using var doc = PdfDocument.Open(saved);
        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());

        // §9.4: text objects cannot nest. The fix appends a reconstructed
        // BT…ET block after the original one, so an implicitly-closed block
        // must be given an explicit ET first — otherwise the output is LESS
        // valid than the malformed input we were handed.
        var depth = 0;
        var maxDepth = 0;
        foreach (var token in content.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "BT") { depth++; maxDepth = System.Math.Max(maxDepth, depth); }
            else if (token == "ET") depth--;
        }

        maxDepth.Should().BeLessThanOrEqualTo(1, "a BT inside an open BT is invalid per §9.4");
        depth.Should().BeLessThanOrEqualTo(0, "every BT the rewrite emits must be closed");
    }
}
