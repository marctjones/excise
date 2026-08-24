using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #1103 — a Type3 font's <c>/Widths</c> are in GLYPH SPACE and map to text
/// space through <c>/FontMatrix</c> (ISO 32000-1 §9.6.5). Every other font type
/// has the implicit <c>[0.001 …]</c> matrix, which is why the width cascade
/// treats <c>/Widths</c> as 1000ths of an em everywhere else. The walker — the
/// single text-state machine feeding both the letter model and the operator
/// geometry redaction removes on — read Type3 <c>/Widths</c> as if they were
/// already 1000ths, so any non-0.001 matrix scaled the advance wrong by the
/// matrix factor.
///
/// <para>The renderer already applied the matrix, so this was the exact defect
/// shape CLAUDE.md documents twice (§9.4.2 line stepping, #942/#899): one half
/// of the machine honours the matrix, the other does not, and the half that
/// does not is the one redaction trusts. On a real <c>[0.04]</c> Type3 font
/// (<c>test-pdfs/pdfjs/issue14953.pdf</c>) the letters stacked — advance 0.3pt
/// where mutool measured ~13pt — and stacked letters overlap a redaction match
/// box, which is how this class of bug destroys untargeted text.</para>
///
/// <para>Pinned against the §9.6.5 arithmetic itself, NOT against excise's
/// renderer — a differential between excise's parser and excise's renderer
/// cannot see a defect they share. The mutool corroboration on the real file is
/// the differential gate; this is the checked-in property gate with no corpus
/// dependency.</para>
/// </summary>
public class Type3FontMatrixWidthTests
{
    // FontMatrix a = 0.01 (10× the default 0.001). Glyph-space width 700.
    // §9.6.5 text-space advance = width · a · fontSize = 700 · 0.01 · 12 = 84pt.
    // The pre-#1103 bug read 700 as 1000ths, giving 700/1000 · 12 = 8.4pt — a
    // 10× error, and the letters would march by 8.4pt instead of 84pt.
    private const double FontMatrixA = 0.01;
    private const double GlyphSpaceWidth = 700;
    private const double FontSize = 12;
    private const double TextX = 100;

    private static readonly double ExpectedAdvancePt = GlyphSpaceWidth * FontMatrixA * FontSize; // 84
    private static readonly double BuggyAdvancePt = GlyphSpaceWidth / 1000.0 * FontSize;          // 8.4

    /// <summary>
    /// A one-page PDF whose /F1 is a Type3 font with <c>/FontMatrix
    /// [0.01 0 0 0.01 0 0]</c> and two box glyphs at glyph-space width 700,
    /// shown as "AB". No gitignored corpus — the whole font is inline.
    /// </summary>
    private static byte[] BuildType3Pdf() => ContentStreamFixture.Build(
        content: $"BT /F1 {FontSize} Tf {TextX} 700 Td (AB) Tj ET\n",
        fontObject:
            "5 0 obj\n<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] "
          + $"/FontMatrix [{FontMatrixA} 0 0 {FontMatrixA} 0 0] "
          + "/CharProcs 6 0 R /Encoding 7 0 R /FirstChar 65 /LastChar 66 "
          + $"/Widths [{GlyphSpaceWidth} {GlyphSpaceWidth}] /Resources << >> >>\nendobj\n",
        extraObjects:
            "6 0 obj\n<< /a 8 0 R /b 9 0 R >>\nendobj\n"
          + "7 0 obj\n<< /Type /Encoding /Differences [65 /a /b] >>\nendobj\n"
          + "8 0 obj\n<< /Length 25 >>\nstream\n700 0 d0\n0 0 700 700 re f\nendstream\nendobj\n"
          + "9 0 obj\n<< /Length 25 >>\nstream\n700 0 d0\n0 0 700 700 re f\nendstream\nendobj\n");

    [Fact]
    public void Type3GlyphWidth_IsScaledThroughFontMatrix_NotReadAsThousandths()
    {
        using var doc = PdfDocument.Open(BuildType3Pdf());
        var letters = ContentStreamFixture.ExtractLetters(doc.GetPage(1));

        letters.Should().HaveCountGreaterThanOrEqualTo(2,
            "the fixture shows two Type3 glyphs");

        letters[0].Width.Should().BeApproximately(ExpectedAdvancePt, 0.5,
            $"§9.6.5: a glyph-space width of {GlyphSpaceWidth} under /FontMatrix "
          + $"a={FontMatrixA} at {FontSize}pt advances {ExpectedAdvancePt}pt, not "
          + $"{BuggyAdvancePt}pt — reading /Widths as 1000ths ignores the matrix");

        letters[0].Width.Should().NotBeApproximately(BuggyAdvancePt, 1.0,
            "the pre-#1103 value (matrix ignored) must not survive");
    }

    [Fact]
    public void Type3Letters_DoNotStack_TheSecondStartsAFullAdvanceAfterTheFirst()
    {
        using var doc = PdfDocument.Open(BuildType3Pdf());
        var letters = ContentStreamFixture.ExtractLetters(doc.GetPage(1))
            .OrderBy(l => l.StartX).ToList();

        var gap = letters[1].StartX - letters[0].StartX;
        gap.Should().BeApproximately(ExpectedAdvancePt, 0.5,
            $"the second glyph must start {ExpectedAdvancePt}pt after the first; "
          + $"before #1103 the gap was {BuggyAdvancePt}pt and the glyphs stacked — "
          + "the CLAUDE.md 'letters stacked, CropBox drops them' collateral mechanism");

        // The specific pre-fix symptom: a near-zero gap that piles the line onto
        // one x-coordinate. A guard so a future regression that merely shifts the
        // constant still trips the assertion above rather than sliding under it.
        gap.Should().BeGreaterThan(BuggyAdvancePt * 2,
            "a stacked line is the observable failure this gate exists to catch");
    }
}
