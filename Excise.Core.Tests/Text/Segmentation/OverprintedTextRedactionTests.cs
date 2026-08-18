using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1047 — OVERPRINTED text: the same run stamped several times at sub-point
/// offsets to fake bold.
///
/// <para>excise's letter model records every stamp, correctly. The matcher then
/// reads the letters in order, so a 4x-stamped <c>Test test</c> reads as
/// <c>TTTTeeeesssstttt</c> and a search for <c>test</c> matches <b>nothing</b>.
/// The term survives, and <c>RedactText</c> reports success.</para>
///
/// <para>Found on <c>test-pdfs/pdfjs/bug900822.pdf</c> by the first full-corpus
/// run of <c>RedactionCollateralHarness</c> (#1046): mutool counted 40
/// occurrences of <c>test</c>, excise removed 34 and left 6 — one entire
/// quadruple-stamped line — while MuPDF's own redactor removed all 40.</para>
///
/// <para>This is CLAUDE.md's Limitations #1 in its purest form: not a removal
/// bug, a READING bug that presents as a silent leak.</para>
/// </summary>
public class OverprintedTextRedactionTests
{
    private const string Secret = "Secret";

    /// <summary>
    /// The same word stamped four times — two X offsets by two Y offsets — the
    /// faux-bold idiom that produced #1047. Offsets are sub-point, as observed
    /// on the real document (0.2 horizontal, 0.4 vertical on a ~10pt glyph).
    ///
    /// <para>⚠️ Each glyph gets its OWN text object with an absolute <c>Tm</c>,
    /// because that is what makes the stamps interleave. A first draft of this
    /// fixture stamped whole words instead and extracted as
    /// <c>"Secret Secret Secret Secret"</c> — four runs kept in order, perfectly
    /// searchable, so the redaction succeeded and the tests below passed
    /// without exercising the defect at all. The interleave comes from
    /// per-glyph runs whose X positions collide.</para>
    /// </summary>
    private static byte[] OverprintedPdf()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Secret.Length; i++)
        {
            var x = 100 + i * 7.0;
            foreach (var (dx, dy) in new[] { (0.0, 0.0), (0.2, 0.0), (0.0, 0.4), (0.2, 0.4) })
                sb.Append(System.FormattableString.Invariant(
                    $"BT /F1 12 Tf 1 0 0 1 {x + dx} {700 + dy} Tm ({Secret[i]}) Tj ET\n"));
        }
        return ContentStreamFixture.Build(sb.ToString());
    }

    /// <summary>A single, ordinary run containing a genuine double letter.</summary>
    private static byte[] DoubleLetterPdf() => ContentStreamFixture.Build(
        "BT /F1 12 Tf 100 700 Td (a letter follows) Tj ET\n");

    private static byte[] RedactAndSave(byte[] pdf, string term, out int reported)
    {
        using var doc = PdfDocument.Open(pdf);
        reported = doc.RedactText(term);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Guard_TheStampsAreWhatMakeTheTextUnsearchable()
    {
        // Documents the defect rather than the fix: excise's extracted text for
        // an overprinted run really does interleave the stamps. If this ever
        // stops being true the collapse below is solving a problem that moved.
        using var doc = PdfDocument.Open(OverprintedPdf());
        doc.GetPage(1).Text.Should().Contain("SSSS",
            "four stamps of the same run interleave character-by-character in " +
            "the letter sequence — which is precisely why a plain search fails");
    }

    [Fact]
    public void AnOverprintedTerm_IsRemovedFromEveryStamp()
    {
        var pdf = OverprintedPdf();

        // ⚠️ A term scan is USELESS on this shape and it took a failed
        // fail-without-fix run to notice: each glyph is its own Tj, so
        // "Secret" is never contiguous in the bytes and FindTerm reports clean
        // on the UNREDACTED file. The structural count is the only instrument
        // that can see this — every stamp is a text-showing operator, and after
        // redaction there must be none left.
        var before = CountTextShowingOperators(pdf);
        before.Should().Be(Secret.Length * 4,
            "guard: the fixture must really stamp every glyph four times");

        var saved = RedactAndSave(pdf, Secret, out _);

        CountTextShowingOperators(saved).Should().Be(0,
            "before #1047 the interleaved letter sequence matched nothing, so all " +
            "four stamps survived and RedactText still reported success");
    }

    /// <summary>
    /// Text-showing operators across every (inflated) stream in the file.
    /// Structural, so it does not depend on excise's extractor to referee
    /// excise's removal.
    /// </summary>
    private static int CountTextShowingOperators(byte[] pdf)
    {
        var n = 0;
        foreach (var body in SavedPdfLeakScanner.StreamBodies(pdf))
            n += System.Text.RegularExpressions.Regex.Matches(body, @"\)\s*Tj").Count;
        return n;
    }

    [Fact]
    public void AnOverprintedTerm_CountsAsOneOccurrence_WhileEveryStampIsRemoved()
    {
        RedactAndSave(OverprintedPdf(), Secret, out var reported);

        // ONE is the right answer, and it is the answer a user can check: the
        // word appears once on the page, however many times it was stamped to
        // fake bold. It also agrees with what mutool counts, which is what made
        // the real document's post-fix "Redacted 40 occurrence(s)" line up with
        // the oracle's 40 exactly.
        reported.Should().Be(1,
            "four stamps of one word are one occurrence to the reader, and the " +
            "reported count is what a user judges the redaction by (#1043)");

        // Collapsing is a MATCHING view only — removal must still reach every
        // stamp. AnOverprintedTerm_IsRemovedFromEveryStamp is what proves it;
        // asserted together here so the count can never be "fixed" by matching
        // one stamp and removing one stamp.
    }

    [Fact]
    public void AGenuineDoubleLetter_IsNotCollapsed()
    {
        // The guard against over-collapsing. If same-valued neighbours merged
        // on text alone, "letter" would become "leter" in the matching view and
        // this redaction would find nothing. Real double letters sit a full
        // glyph-width apart; stamps sit on top of each other.
        var saved = RedactAndSave(DoubleLetterPdf(), "letter", out var reported);

        reported.Should().Be(1, "'letter' occurs once and its double 't' must not collapse");
        SavedPdfLeakScanner.FindTerm(saved, "letter").Should().BeEmpty();
    }

    [Fact]
    public void AGenuineDoubleLetter_KeepsItsNeighbours()
    {
        var saved = RedactAndSave(DoubleLetterPdf(), "letter", out _);

        using var doc = PdfDocument.Open(saved);
        var text = doc.GetPage(1).Text;
        text.Should().Contain("follows", "redacting one word must not take the next");
    }
}
