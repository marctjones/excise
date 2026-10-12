using System;
using System.Globalization;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #1047 in extraction: a word whose every letter is drawn several times in a row,
/// the copies ~0.3 pt apart (faux bold by stacking), reads once in page text, the
/// word list (search, the search index) and copied text, as MuPDF reads it. The
/// letter model keeps every stamp, and redaction removes every one of them. A genuine
/// double letter is never collapsed.
/// </summary>
public class OverprintedTextExtractionTests
{
    private const double PageHeight = 792;

    // Helvetica widths (1/1000 em) of the stacked letters.
    private static readonly (char Glyph, int Width)[] Stacked = [('T', 611), ('e', 556), ('s', 500), ('t', 278)];

    /// <summary>
    /// "A Test word" at 24 pt, each letter of "Test" stamped three times: after each
    /// stamp the TJ adjustment moves back all but 12.5 units (0.3 pt at 24 pt).
    /// </summary>
    private static byte[] StackedPdf(int copies = 3)
    {
        var tj = new StringBuilder("[(A )");
        foreach (var (glyph, width) in Stacked)
        {
            for (var copy = 0; copy < copies; copy++)
            {
                tj.Append(" (").Append(glyph).Append(')');
                if (copy < copies - 1)
                    tj.Append(' ').Append((width - 12.5).ToString(CultureInfo.InvariantCulture));
            }
        }
        tj.Append(" ( word)]");
        return ContentStreamFixture.Build($"BT /F1 24 Tf 72 700 Td {tj} TJ ET\n");
    }

    [Fact]
    public void TheLetterModel_KeepsEveryStamp()
    {
        using var doc = PdfDocument.Open(StackedPdf());
        string.Concat(doc.GetPage(1).Letters.Select(l => l.Value)).Should().Be("A TTTeeesssttt word",
            "the letters are what redaction removes: every stamp stays in them");
    }

    [Fact]
    public void PageText_ReadsTheStackOnce_AsMuPdfDoes()
    {
        var pdf = StackedPdf();
        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).Text.Should().Be("A Test word");
        if (MutoolTextOracle.IsAvailable)
            MutoolTextOracle.ExtractAllPages(pdf).Should().Contain("A Test word", "the independent reference reads it so");
    }

    [Fact]
    public void Words_ReadTheStackOnce()
    {
        using var doc = PdfDocument.Open(StackedPdf());
        doc.GetPage(1).GetWords().Select(w => w.Text).Should().Equal("A", "Test", "word");
        doc.GetPage(1).ExtractTextAndWordsWithoutRetainingLetters().Words.Select(w => w.Text)
            .Should().Equal("A", "Test", "word");
    }

    [Fact]
    public void CopiedText_ReadsTheStackOnce()
    {
        using var doc = PdfDocument.Open(StackedPdf());
        var letters = doc.GetPage(1).Letters;
        var reading = TextSelectionEngine.SortReadingOrder(letters);
        var selection = TextSelectionEngine.BuildSelection(reading, letters,
            letters.First(l => l.Value == "T"), letters.Last(l => l.Value == "t"), columnGapThreshold: 50);
        selection.Text.Should().Be("Test");
    }

    [Fact]
    public void AWordDrawnWholeSeveralTimes_StillReadsAsItsRepeats_AsMuPdfReadsIt()
    {
        // Not a stack of letters: the copies are not neighbours in the letter order.
        var content = new StringBuilder();
        foreach (var dx in new[] { 0.0, 0.3, 0.6 })
            content.Append(FormattableString.Invariant($"BT /F1 24 Tf {72 + dx} 700 Td (Test) Tj ET\n"));
        var pdf = ContentStreamFixture.Build(content.ToString());
        using var doc = PdfDocument.Open(pdf);
        System.Text.RegularExpressions.Regex.Matches(doc.GetPage(1).Text, "Test").Count.Should().Be(3);
        if (MutoolTextOracle.IsAvailable)
            System.Text.RegularExpressions.Regex.Matches(MutoolTextOracle.ExtractAllPages(pdf), "Test").Count
                .Should().Be(3, "the reference reads the repeats too");
    }

    public static TheoryData<string, double, int> GenuineDoubles()
    {
        var data = new TheoryData<string, double, int>();
        foreach (var font in new[] { "Helvetica", "Courier", "Times-Roman" })
            foreach (var size in new[] { 4.0, 9.0, 24.0, 72.0 })
                foreach (var kern in new[] { 0, 60, 120 })
                    data.Add(font, size, kern);
        return data;
    }

    [Theory]
    [MemberData(nameof(GenuineDoubles))]
    public void AGenuineDoubleLetter_IsNeverCollapsed_EvenTightlyKerned(string font, double size, int kern)
    {
        // Each double letter is drawn with the kern pulling the second copy towards
        // the first, as a tight-kerned font would: still most of a glyph apart.
        // Each word split between its two equal letters: (let) kern (ter).
        var words = new[] { ("let", "ter"), ("Mis", "sissippi"), ("se", "en"), ("bo", "ok") };
        var tj = new StringBuilder("[");
        foreach (var (left, right) in words)
            tj.Append(FormattableString.Invariant($"({left}) {kern} ({right} ) "));
        tj.Append(']');
        var pdf = ContentStreamFixture.Build(
            FormattableString.Invariant($"BT /F1 {size} Tf 20 600 Td {tj} TJ ET\n"),
            fontObject: $"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /{font} >>\nendobj\n");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        page.Text.Should().Contain("letter").And.Contain("Mississippi").And.Contain("seen").And.Contain("book");
        page.GetWords().Select(w => w.Text).Should().Contain(["letter", "Mississippi", "seen", "book"]);
    }

    [Fact]
    public void Redaction_RemovesEveryStamp_AndTheSameRuleReadsBoth()
    {
        var pdf = StackedPdf();
        // Counterfactual for the byte oracle: every stamp is a literal in the input.
        foreach (var (glyph, _) in Stacked)
            SavedPdfLeakScanner.FindTerm(pdf, $"({glyph})").Should().NotBeEmpty("guard: each stamp is in the input");

        using var doc = PdfDocument.Open(pdf);
        var report = doc.RedactText("test", RedactionOptions.Default);
        var saved = doc.SaveToBytes();

        report.VerifiedRemovals.Should().Be(1, "three stamps of one word are one occurrence");
        foreach (var (glyph, _) in Stacked)
            SavedPdfLeakScanner.FindTerm(saved, $"({glyph})").Should().BeEmpty($"no stamp of '{glyph}' may remain");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContainAny("T", "e", "s", "t");

        if (!(MutoolTextOracle.IsAvailable && MutoolStextOracle.IsAvailable)) return;
        var mutool = MutoolTextOracle.ExtractAllPages(saved);
        mutool.Should().NotContain("Test").And.NotContain("T").And.Contain("word");
        var before = MutoolStextOracle.Words(pdf, 1, PageHeight);
        var after = MutoolStextOracle.Words(saved, 1, PageHeight);
        foreach (var word in before.Where(w => w.Text is "A" or "word"))
            after.Should().Contain(w => w.Text == word.Text
                && System.Math.Abs(w.Left - word.Left) < 0.01 && System.Math.Abs(w.Bottom - word.Bottom) < 0.01,
                $"'{word.Text}' must survive in place");
    }
}
