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
/// Invisible/optional separator folding in redaction matching (#726).
///
/// Justified and shaped text routinely carries characters a user cannot
/// see and will never type: soft hyphens (U+00AD) at line-break
/// opportunities, zero-width space/non-joiner/joiner (U+200B–U+200D)
/// between letters, the zero-width no-break space / BOM (U+FEFF), and
/// non-breaking spaces (U+00A0) where the keyboard produces a plain space.
/// Extraction faithfully reports these code points, so before the fix
/// these tests cover, <c>RedactText("secret")</c> returned 0 against
/// stored "se&#173;cret" and reported success while the word stayed in
/// the file — CLAUDE.md limitation #1 (silent redaction failure) in
/// ordinary justified body text.
///
/// The ground truth is the Unicode code points themselves (deterministic;
/// not a tool's opinion). Matching removes exactly {U+00AD, U+200B,
/// U+200C, U+200D, U+FEFF} and maps U+00A0 to a plain space, on BOTH
/// sides. Scope is tight: real hyphens (U+002D), spaces, and every other
/// format/space character keep their identity.
///
/// Saved-bytes assertions follow the redaction test rules: ASCII +
/// UTF-16BE + UTF-8 views of the written file, checked for the word with
/// and without the separator and for the raw character-code carrier, with
/// anti-vacuity checks that the carrier existed before redaction.
/// </summary>
public class InvisibleSeparatorRedactionTests
{
    [Theory]
    [InlineData(0x00AD, "soft hyphen")]
    [InlineData(0x200B, "zero-width space")]
    [InlineData(0x200C, "zero-width non-joiner")]
    [InlineData(0x200D, "zero-width joiner")]
    [InlineData(0xFEFF, "zero-width no-break space")]
    public void RedactText_PlainNeedle_RemovesWordSplitByInvisibleChar(
        int separator, string name)
    {
        // Stored: "se" + separator + "cret"; typed: "secret".
        var scalars = new[] { (int)'s', (int)'e', separator, (int)'c', (int)'r', (int)'e', (int)'t' };
        var storedWord = string.Concat(scalars.Select(char.ConvertFromUtf32));
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        // Anti-vacuity: the split word must be present — extracted with the
        // raw invisible code point, carried as raw character codes.
        doc.GetPage(1).Text.Should().Contain(storedWord,
            $"sanity: the fixture must extract with the raw {name} for this test to prove anything");
        doc.GetPage(1).Text.Should().NotContain("secret",
            "sanity: the unbroken spelling must NOT be extractable, or matching would succeed without separator folding");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(ABCDEFG)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText("secret");

        removed.Should().BeGreaterThan(0,
            $"a plain needle must match a word split by a {name} " +
            "(U+" + separator.ToString("X4") + "); 0 matches is the " +
            "silent-failure mode this test exists to prevent (#726)");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain("secret", "the word must not survive unbroken");
        searchable.Should().NotContain(storedWord, "nor with the invisible separator");
        searchable.Should().NotContain("(ABCDEFG)", "nor as its raw character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny("secret", storedWord);
    }

    [Fact]
    public void RedactText_NeedleWithPlainSpace_RemovesNonBreakingSpaceText()
    {
        // Stored: "top" + NBSP + "secret"; typed: "top secret" with a plain space.
        var scalars = "top".Select(c => (int)c)
            .Append(0x00A0)
            .Concat("secret".Select(c => (int)c)).ToArray();
        var storedText = "top\u00A0secret";
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Contain(storedText,
            "sanity: the fixture must extract with the raw NBSP");

        var removed = doc.RedactText("top secret");

        removed.Should().BeGreaterThan(0,
            "a needle typed with a plain space must match text stored with U+00A0 (#726)");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(storedText);
        searchable.Should().NotContain("top secret");
        searchable.Should().NotContain("(ABCDEFGHIJ)");
    }

    [Fact]
    public void RedactText_SoftHyphenNeedle_AlsoRemovesSplitWord()
    {
        // A user may paste text copied from a viewer, soft hyphen included.
        // Folding applies to BOTH sides.
        var scalars = new[] { (int)'s', (int)'e', 0x00AD, (int)'c', (int)'r', (int)'e', (int)'t' };
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("se\u00ADcret");

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain("secret");
        searchable.Should().NotContain("se\u00ADcret");
        searchable.Should().NotContain("(ABCDEFG)");
    }

    [Fact]
    public void RedactText_PlainNeedle_DoesNotTouchUnrelatedText()
    {
        // Scope guard: redacting the split word must leave other text on the
        // same line intact.
        var scalars = new[] { (int)'s', (int)'e', 0x00AD, (int)'c', (int)'r', (int)'e', (int)'t' }
            .Append(' ').Concat("xyz".Select(c => (int)c)).ToArray();
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("secret");

        removed.Should().BeGreaterThan(0);

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("xyz", "unrelated text on the same line must survive");
        text.Should().NotContain("secret");
    }

    [Fact]
    public void RedactText_RealHyphen_IsNotFolded()
    {
        // Scope guard: a REAL hyphen (U+002D) is visible content, not an
        // optional separator — "secret" must not match "se-cret".
        var scalars = new[] { (int)'s', (int)'e', (int)'-', (int)'c', (int)'r', (int)'e', (int)'t' };
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("secret");

        removed.Should().Be(0,
            "a real hyphen is visible content; only the soft hyphen is an optional separator");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.GetPage(1).Text.Should().Contain("se-cret",
            "the hyphenated word must be untouched");
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
