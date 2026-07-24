using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Canonical-equivalence (NFC/NFD) folding in redaction matching (#724).
///
/// Unicode has two canonically EQUIVALENT spellings of every accented Latin
/// letter: precomposed ("café", é = U+00E9) and decomposed ("cafe" + combining
/// acute U+0301). Keyboards type the precomposed form; PDFs produced from
/// TeX/OpenType pipelines routinely store the decomposed form (base glyph +
/// combining-mark glyph), and vice versa. Before the fix these tests cover,
/// <c>RedactText("café")</c> returned 0 on a decomposed fixture and reported
/// success while the word stayed in the file — CLAUDE.md limitation #1
/// (silent redaction failure) in everyday European-language text.
///
/// The ground truth for the equivalence is Unicode canonical equivalence
/// (deterministic; not a tool's opinion): NFD("café") = "cafe" + U+0301 and
/// NFC("cafe" + U+0301) = "café". Matching folds BOTH sides to the canonical
/// DECOMPOSITION (NFD) — same match set as NFC-on-both-sides, but safe for
/// the per-letter index arithmetic redaction uses (composition can merge
/// characters ACROSS letter boundaries; decomposition never does). This is
/// canonical-only: compatibility characters are untouched (no NFKC creep),
/// and accent-INSENSITIVE matching ("cafe" finding "café") stays out of
/// scope — the accent is canonically meaningful.
///
/// Saved-bytes assertions follow the redaction test rules: ASCII + UTF-16BE
/// + UTF-8 views of the written file, checked for the word in both canonical
/// spellings and for the raw character-code carrier the fixture is known to
/// use — with anti-vacuity checks that the carrier existed before redaction.
/// </summary>
public class CanonicalAccentRedactionTests
{
    /// <summary>What a user types: precomposed é (U+00E9).</summary>
    private const string PrecomposedWord = "caf\u00E9";

    /// <summary>What the fixture stores: e + combining acute (U+0301).</summary>
    private const string DecomposedWord = "cafe\u0301";

    /// <summary>Per-glyph Unicode scalars of the decomposed word.</summary>
    private static readonly int[] DecomposedScalars = { 'c', 'a', 'f', 'e', 0x0301 };

    /// <summary>Per-glyph Unicode scalars of the precomposed word.</summary>
    private static readonly int[] PrecomposedScalars = { 'c', 'a', 'f', 0x00E9 };

    [Fact]
    public void RedactText_PrecomposedNeedle_RemovesDecomposedWord()
    {
        // Codes ABCDE in logical order, /ToUnicode maps to c,a,f,e,U+0301.
        var pdf = RtlPdfFixtures.SingleTj(DecomposedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        // Anti-vacuity: before redaction the word must be present — extracted
        // in its raw decomposed spelling, and carried in the saved bytes as
        // its raw character codes '(ABCDE)'.
        doc.GetPage(1).Text.Should().Contain(DecomposedWord,
            "sanity: the fixture must extract decomposed for this test to prove anything");
        doc.GetPage(1).Text.Should().NotContain(PrecomposedWord,
            "sanity: the precomposed spelling must NOT be extractable, or matching would succeed without canonical folding");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(ABCDE)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(PrecomposedWord);

        removed.Should().BeGreaterThan(0,
            "a precomposed needle (U+00E9) must match canonically equivalent " +
            "decomposed text (e + U+0301); 0 matches is the silent-failure mode " +
            "this test exists to prevent (#724)");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain(PrecomposedWord, "the word must not survive precomposed");
        searchable.Should().NotContain(DecomposedWord, "nor decomposed");
        searchable.Should().NotContain("(ABCDE)", "nor as its raw character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny(PrecomposedWord, DecomposedWord);
    }

    [Fact]
    public void RedactText_DecomposedNeedle_RemovesPrecomposedWord()
    {
        // The mirror image: text stored precomposed, needle pasted decomposed
        // (e.g. copied out of a decomposed-form source).
        var pdf = RtlPdfFixtures.SingleTj(PrecomposedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Contain(PrecomposedWord,
            "sanity: the fixture must extract precomposed");
        doc.GetPage(1).Text.Should().NotContain(DecomposedWord);
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(ABCD)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(DecomposedWord);

        removed.Should().BeGreaterThan(0,
            "a decomposed needle must match canonically equivalent precomposed text (#724)");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(PrecomposedWord);
        searchable.Should().NotContain(DecomposedWord);
        searchable.Should().NotContain("(ABCD)");
    }

    [Theory]
    [InlineData("Mu\u00F1oz", new[] { 'M', 'u', 'n', 0x0303, 'o', 'z' })]        // ñ = n + combining tilde
    [InlineData("F\u00FChrer", new[] { 'F', 'u', 0x0308, 'h', 'r', 'e', 'r' })]  // ü = u + combining diaeresis
    [InlineData("Ha\u010Dek", new[] { 'H', 'a', 'c', 0x030C, 'e', 'k' })]        // č = c + combining caron
    public void RedactText_PrecomposedNeedle_RemovesOtherCombiningMarks(
        string precomposedNeedle, int[] decomposedScalars)
    {
        var pdf = RtlPdfFixtures.SingleTj(decomposedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var storedWord = string.Concat(decomposedScalars.Select(char.ConvertFromUtf32));
        doc.GetPage(1).Text.Should().Contain(storedWord, "sanity: raw decomposed text must extract");

        var removed = doc.RedactText(precomposedNeedle);

        removed.Should().BeGreaterThan(0,
            $"needle '{precomposedNeedle}' must match its canonical decomposition");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(storedWord);
        searchable.Should().NotContain(precomposedNeedle);
    }

    [Fact]
    public void RedactText_PrecomposedNeedle_DoesNotTouchUnrelatedText()
    {
        // Scope guard: redacting the decomposed word must leave other text on
        // the same line intact — "cafe<U+0301> xyz", redact "café", keep "xyz".
        var scalars = DecomposedScalars.Append(' ').Concat("xyz".Select(c => (int)c)).ToArray();
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(PrecomposedWord);

        removed.Should().BeGreaterThan(0);

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("xyz", "unrelated text on the same line must survive");
        text.Should().NotContainAny(PrecomposedWord, DecomposedWord);
    }

    [Fact]
    public void RedactText_UnaccentedNeedle_DoesNotMatchAccentedWord()
    {
        // Scope guard: canonical folding must NOT become accent-insensitive
        // matching. "cafe" (no accent) is NOT canonically equivalent to
        // "café" and must not redact it. The stored form here is
        // PRECOMPOSED: with decomposed storage the raw bytes physically
        // contain the letters c-a-f-e and the raw-substring operator
        // backstop legitimately (and fail-safely) removes them — that
        // pre-existing behavior is not what this guard is about.
        var pdf = RtlPdfFixtures.SingleTj(PrecomposedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("cafe");

        removed.Should().Be(0,
            "the accent is canonically meaningful; accent-INSENSITIVE matching is out of scope");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.GetPage(1).Text.Should().Contain(PrecomposedWord,
            "the accented word must be untouched by a non-equivalent needle");
    }

    /// <summary>
    /// Carrier-agnostic view of the saved file, per the redaction test rules:
    /// ASCII (raw codes, hex carriers) + UTF-16BE (PDF Unicode text strings)
    /// + UTF-8 (XMP metadata).
    /// </summary>
    private static string SearchableTextOf(byte[] saved) =>
        Encoding.ASCII.GetString(saved) +
        Encoding.BigEndianUnicode.GetString(saved) +
        Encoding.UTF8.GetString(saved);
}
