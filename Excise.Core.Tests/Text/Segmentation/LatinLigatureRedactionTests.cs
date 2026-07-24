using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Latin ligature folding in redaction matching (#722).
///
/// Many PDFs store Latin ligatures as single Unicode code points —
/// U+FB00–U+FB06: ﬀ(ff) ﬁ(fi) ﬂ(fl) ﬃ(ffi) ﬄ(ffl) ﬅ(long s-t) ﬆ(st) — so a
/// page's extracted text for "office" is "oﬃce". A user's redaction needle is
/// typed in plain letters. Before the fix these tests cover,
/// <c>RedactText("office")</c> returned 0 on the fixtures below and reported
/// success while the word stayed in the file — CLAUDE.md limitation #1
/// (silent redaction failure) in ordinary English body text.
///
/// The ground truth for the equivalence is the Unicode compatibility
/// decomposition of U+FB00–U+FB06 (deterministic; not a tool's opinion):
/// U+FB03 → "ffi" (a 1→3 expansion), U+FB01 → "fi", and NFKC also folds
/// ſ → s so both U+FB05 (ﬅ) and U+FB06 (ﬆ) fold to "st". Folding is applied
/// to MATCHING only — the letters, operator text, and removed bytes keep the
/// raw ligature code points, so glyph-level removal still maps char-to-glyph
/// exactly. This mirrors the Arabic presentation-form fix (#632).
///
/// Saved-bytes assertions follow the redaction test rules: ASCII + UTF-16BE
/// (+ UTF-8) views of the written file, checked for the word in plain and
/// ligated form and for the raw character-code carrier the fixture is known
/// to use — with an anti-vacuity check that the carrier existed before
/// redaction.
/// </summary>
public class LatinLigatureRedactionTests
{
    /// <summary>What a user types: plain letters.</summary>
    private const string PlainWord = "office";

    /// <summary>The same word as PDFs commonly store it: o + ﬃ + c + e.</summary>
    private static readonly string LigatedWord =
        "o" + (char)0xFB03 + "ce";

    /// <summary>Per-glyph Unicode scalars of the ligated word.</summary>
    private static readonly int[] LigatedScalars = { 'o', 0xFB03, 'c', 'e' };

    [Fact]
    public void RedactText_PlainLetterNeedle_RemovesLigatedWord()
    {
        // Codes ABCD in logical order, /ToUnicode maps to o, ﬃ, c, e.
        var pdf = RtlPdfFixtures.SingleTj(LigatedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        // Anti-vacuity: before redaction the word must be present — extracted
        // with its raw ligature code point, and carried in the saved bytes as
        // its raw character codes '(ABCD)'.
        doc.GetPage(1).Text.Should().Contain(LigatedWord,
            "sanity: the fixture must extract with the raw ligature code point for this test to prove anything");
        doc.GetPage(1).Text.Should().NotContain(PlainWord,
            "sanity: the plain spelling must NOT be extractable, or matching would succeed without folding");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(ABCD)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(PlainWord);

        removed.Should().BeGreaterThan(0,
            "a plain-letter needle must match a ligature code point via " +
            "compatibility-decomposition folding; 0 matches is the silent-failure " +
            "mode this test exists to prevent (#722)");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain(PlainWord, "the word must not survive in plain letters");
        searchable.Should().NotContain(LigatedWord, "nor with its ligature code point");
        searchable.Should().NotContain("(ABCD)", "nor as its raw character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny(PlainWord, LigatedWord);
    }

    [Fact]
    public void RedactText_LigatedNeedle_AlsoRemovesLigatedWord()
    {
        // A user may also paste ligated text copied from a viewer. Folding is
        // applied to BOTH sides, so a ligated needle matches too.
        var pdf = RtlPdfFixtures.SingleTj(LigatedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(LigatedWord);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(PlainWord);
        searchable.Should().NotContain(LigatedWord);
        searchable.Should().NotContain("(ABCD)");
    }

    [Theory]
    [InlineData(0xFB00, "offer", "o", "er")]   // oﬀer  (ﬀ → ff)
    [InlineData(0xFB01, "final", "", "nal")]   // ﬁnal  (ﬁ → fi)
    [InlineData(0xFB02, "flat", "", "at")]     // ﬂat   (ﬂ → fl)
    [InlineData(0xFB04, "waffle", "wa", "e")]  // waﬄe  (ﬄ → ffl)
    [InlineData(0xFB05, "stop", "", "op")]     // ﬅop   (ﬅ → st: NFKC folds ſ → s)
    [InlineData(0xFB06, "stop", "", "op")]     // ﬆop   (ﬆ → st)
    public void RedactText_PlainLetterNeedle_RemovesEveryLigature(
        int ligature, string plainWord, string prefix, string suffix)
    {
        // Each ligature code point, embedded in a real word built as
        // prefix + ligature + suffix: the plain-letter needle must match.
        var scalars = prefix.Select(c => (int)c)
            .Append(ligature)
            .Concat(suffix.Select(c => (int)c))
            .ToArray();
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var storedWord = prefix + (char)ligature + suffix;
        doc.GetPage(1).Text.Should().Contain(storedWord, "sanity: raw ligature must extract");

        var removed = doc.RedactText(plainWord);

        removed.Should().BeGreaterThan(0,
            $"needle '{plainWord}' must match stored '{storedWord}' (U+{ligature:X4})");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(storedWord);
        searchable.Should().NotContain(plainWord);
    }

    [Fact]
    public void RedactText_PlainLetterNeedle_DoesNotTouchUnrelatedText()
    {
        // Scope guard: redacting the ligated word must leave other text on
        // the same line intact — "oﬃce xyz", redact "office", keep "xyz".
        var scalars = LigatedScalars.Append(' ').Concat("xyz".Select(c => (int)c)).ToArray();
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(PlainWord);

        removed.Should().BeGreaterThan(0);

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("xyz", "unrelated text on the same line must survive");
        text.Should().NotContainAny(PlainWord, LigatedWord);
    }

    [Fact]
    public void RedactText_PlainLetterNeedle_RemovesLigatureRun_InPdfJsCorpusFixture()
    {
        // Real-world fixture: pdf.js copy_paste_ligatures.pdf extracts
        // "abcdefﬀﬁﬂﬃﬄﬅﬆghijklmno". A plain-letter needle spelled over the
        // ligature run ("flffiffl" = ﬂ+ﬃ+ﬄ folded) matched nothing before
        // the fix.
        var path = FindRepoFile("test-pdfs", "pdfjs", "copy_paste_ligatures.pdf");
        Assert.SkipWhen(path == null,
            "gitignored pdf.js corpus fixture copy_paste_ligatures.pdf not present (scripts/download-pdfjs-corpus.sh).");

        using var doc = PdfDocument.Open(path!);

        var ligatureRun = new string(new[]
        {
            (char)0xFB00, (char)0xFB01, (char)0xFB02, (char)0xFB03,
            (char)0xFB04, (char)0xFB05, (char)0xFB06
        });
        doc.GetPage(1).Text.Should().Contain(ligatureRun,
            "sanity: the fixture must extract the raw ligature code points");

        var removed = doc.RedactText("flffiffl"); // ﬂ + ﬃ + ﬄ in plain letters

        removed.Should().BeGreaterThan(0,
            "a plain-letter needle must match the ligated run in a real-world PDF");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().NotContain(new string(new[] { (char)0xFB02, (char)0xFB03, (char)0xFB04 }),
            "the matched ligatures must be structurally removed");
        text.Should().Contain("abcdef", "unmatched text before the run must survive");
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

    private static string? FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
