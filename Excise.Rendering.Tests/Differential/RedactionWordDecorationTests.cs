using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1753 — an underline, box, strike-through or highlight drawn to one
/// redacted word's extent goes with the word, under every width policy.
/// Left at the old width beside a closed gap it states the removed word's
/// width: the issue recovered 8 of 8 words that way. Under the keep-layout
/// default an underline below the covering box, or an annotation drawn on top
/// of it, states the width the box was widened to blur. A decoration spanning
/// more than the word (a sentence highlight, a rule across the line), under a
/// neighbouring word, or above or below the line says nothing about the word's
/// width and is kept.
/// </summary>
/// <remarks>
/// The secret ends its line, so closing the gap moves nothing into the
/// word's old extent; with no box drawn, whatever ink mutool renders there
/// afterwards is the decoration.
/// </remarks>
public class RedactionWordDecorationTests : IDisposable
{
    private const string Secret = "ALBERTINA";
    private const int Dpi = 150;
    private const double PageHeight = 120;
    private const string CutItemsRow = "structure-tree reference(s) to a removed object";

    // 20 pt Helvetica from x=20 at baseline 50. "Name: " advances 3.223 em
    // (64.46 pt) and ALBERTINA 5.557 em (111.14 pt), so the word spans
    // 84.46..195.60. "Name" spans 20..73.34; " Smith" (space 0.278 em, Smith
    // 2.556 em) puts Smith at 201.16..252.28.
    private const string Line = "BT /F1 20 Tf 20 50 Td (Name: ALBERTINA) Tj ET";
    private const string LineWithNext = "BT /F1 20 Tf 20 50 Td (Name: ALBERTINA Smith) Tj ET";
    private static readonly PdfRectangle WordExtent = new(84.46, 43, 195.6, 68);

    private readonly List<string> _temp = new();

    private static readonly (string Kind, string Decoration)[] WordSizedDecorations =
    {
        ("underline", "84.46 46.5 111.14 1.2 re f"),
        ("box", "2 w 82.46 44 115.14 21 re S"),
        ("highlight", "1 1 0 rg 84.46 45 111.14 20 re f 0 g"),
        ("strike-through", "1.5 w 84.46 56 m 195.6 56 l S"),
    };

    public static TheoryData<string, string, WidthPolicy> WordSizedDecorationsUnderEveryPolicy()
    {
        var data = new TheoryData<string, string, WidthPolicy>();
        foreach (var (kind, decoration) in WordSizedDecorations)
            foreach (var policy in Enum.GetValues<WidthPolicy>())
                data.Add(kind, decoration, policy);
        return data;
    }

    public static TheoryData<WidthPolicy> EveryPolicy()
    {
        var data = new TheoryData<WidthPolicy>();
        foreach (var policy in Enum.GetValues<WidthPolicy>()) data.Add(policy);
        return data;
    }

    [Theory]
    [MemberData(nameof(WordSizedDecorationsUnderEveryPolicy))]
    public void EveryPolicy_RemovesTheDecorationSizedToTheWord(string kind, string decoration, WidthPolicy policy)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Fixture($"q {decoration} Q\n{Line}");
        using (var before = MutoolReferenceRenderer.RenderPage(WriteTemp(source), 1, Dpi))
            InkFractionIn(before!, WordExtent).Should().BeGreaterThan(0.01,
                $"fixture sanity: the {kind} is drawn before redaction");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false, Width = policy }, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret).And.Contain("Name");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, WordExtent).Should().BeLessThan(0.001,
            $"the {kind} was sized to the removed word, so it states its width under {policy} (#1753)");
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1,
            "every removal is reported");
    }

    [Theory]
    [MemberData(nameof(EveryPolicy))]
    public void EveryPolicy_KeepsADecorationSpanningMoreThanTheWord(WidthPolicy policy)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // A highlight over the whole "Name: ALBERTINA" sentence and a rule
        // across the line: neither is sized to the word, so neither measures it.
        var source = Fixture($"q 0 1 1 rg 18 45 180 20 re f Q q 10 40 380 0.8 re f Q\n{Line}");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false, Width = policy }, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret);
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, new PdfRectangle(150, 47, 190, 63)).Should().BeGreaterThan(0.9,
            "the sentence highlight is kept, under where the word was");
        InkFractionIn(after!, new PdfRectangle(250, 40, 380, 40.8)).Should().BeGreaterThan(0.5,
            "the rule across the line is kept");
        report.Removals.Should().NotContain(r => r.Feature.Contains("sized to a redacted word"));
    }

    [Fact]
    public void CollapsePreserveLayout_RemovesTheDecoration_TheBoxIsNotTheOnlyWidthSignal()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Before the owner's 2026-10 decision this test pinned the opposite:
        // the decoration stayed because the exact-width box already states the
        // width. With no box drawn the decoration is the only width signal,
        // and an annotation draws on top of any box, so it goes here too.
        var source = Fixture($"q 84.46 46.5 111.14 1.2 re f Q\n{Line}");

        var saved = Redact(source,
            RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.CollapsePreserveLayout },
            out var report);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        using var after = MutoolReferenceRenderer.RenderPage(WriteTemp(saved), 1, Dpi);
        InkFractionIn(after!, new PdfRectangle(84.46, 46.5, 195.6, 47.7)).Should().BeLessThan(0.001);
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1);
    }

    [Fact]
    public void Default_RemovesTheUnderlineBelowTheBox_AndStillDrawsTheBox()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The owner's case: under the keep-layout default the box is widened
        // to blur the word's width, and an underline sized to the word below
        // it would state that width exactly. Width is NOT set: the default
        // itself is pinned.
        RedactionOptions.Default.Width.Should().Be(WidthPolicy.OvershootPreserveLayout, "this test pins the default");
        var underline = new PdfRectangle(84.46, 40, 195.6, 41.2);
        var source = Fixture($"q 84.46 40 111.14 1.2 re f Q\n{Line}");
        using (var before = MutoolReferenceRenderer.RenderPage(WriteTemp(source), 1, Dpi))
            InkFractionIn(before!, underline).Should().BeGreaterThan(0.5, "fixture sanity");

        var saved = Redact(source, RedactionOptions.Default, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret).And.Contain("Name");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, underline).Should().BeLessThan(0.001,
            "the underline below the widened box stated the word's exact width");
        InkFractionIn(after!, new PdfRectangle(100, 50, 180, 60)).Should().BeGreaterThan(0.95,
            "the covering box still draws over the word: removing the decoration left no hole in it");
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1);
    }

    [Fact]
    public void Default_KeepsDecorationsUnderTheNextWord_AcrossBothWords_AndAboveOrBelowTheLine()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Under Smith only; under ALBERTINA and Smith together; a word-sized
        // rule on the line above and one on the line below; a paragraph-wide
        // rule. None belongs to the removed run.
        var smithUnderline = new PdfRectangle(201.16, 46.5, 252.28, 47.7);
        var bothUnderline = new PdfRectangle(84.46, 38, 252.28, 39);
        var above = new PdfRectangle(84.46, 85, 195.6, 86.2);
        var below = new PdfRectangle(84.46, 20, 195.6, 21.2);
        var paragraph = new PdfRectangle(10, 10, 390, 11.2);
        var source = Fixture(string.Join(" ", new[] { smithUnderline, bothUnderline, above, below, paragraph }
                                 .Select(Rule))
                             + "\n" + LineWithNext);

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false }, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret).And.Contain("Smith");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, Inset(smithUnderline)).Should().BeGreaterThan(0.5, "the next word's underline is not the removed word's");
        InkFractionIn(after!, Inset(bothUnderline)).Should().BeGreaterThan(0.5, "an underline across both words spans more than the removed one");
        InkFractionIn(after!, Inset(above)).Should().BeGreaterThan(0.5, "a rule on the line above is not on the word's line");
        InkFractionIn(after!, Inset(below)).Should().BeGreaterThan(0.5, "a rule on the line below is not on the word's line");
        InkFractionIn(after!, Inset(paragraph)).Should().BeGreaterThan(0.5, "a paragraph-wide rule is kept");
        report.Removals.Should().NotContain(r => r.Feature.Contains("sized to a redacted word"));
    }

    [Fact]
    public void Default_MovesNoSurvivingGlyph()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Fixture($"q 84.46 40 111.14 1.2 re f Q q 201.16 46.5 51.12 1.2 re f Q\n{LineWithNext}");
        var before = MutoolGlyphPositions.ExtractPage(WriteTemp(source), 1);
        var saved = Redact(source, RedactionOptions.Default, out _);
        var after = MutoolGlyphPositions.ExtractPage(WriteTemp(saved), 1);
        before.Should().NotBeNull();
        after.Should().NotBeNull();

        static bool Survives(MutoolGlyphPositions.Glyph g) =>
            !string.IsNullOrWhiteSpace(g.Char) && (g.X < WordExtent.Left - 1 || g.X >= WordExtent.Right - 1);
        var expected = before!.Where(Survives).ToList();
        expected.Select(g => g.Char).Should().Equal("N", "a", "m", "e", ":", "S", "m", "i", "t", "h");
        var kept = after!.Where(g => !string.IsNullOrWhiteSpace(g.Char)).ToList();
        kept.Select(g => g.Char).Should().Equal(expected.Select(g => g.Char));
        for (var i = 0; i < expected.Count; i++)
        {
            kept[i].X.Should().BeApproximately(expected[i].X, 0.01, $"glyph {expected[i].Char} must not move");
            kept[i].Y.Should().BeApproximately(expected[i].Y, 0.01, $"glyph {expected[i].Char} must not move");
        }
    }

    [Fact]
    public void Default_SequentialRedactions_EachTakeOnlyTheirOwnDecoration()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var nameUnderline = new PdfRectangle(20, 40, 73.34, 41.2);
        var secretUnderline = new PdfRectangle(84.46, 40, 195.6, 41.2);
        var smithUnderline = new PdfRectangle(201.16, 40, 252.28, 41.2);
        var source = Fixture(string.Join(" ", new[] { nameUnderline, secretUnderline, smithUnderline }
                                 .Select(Rule))
                             + "\n" + LineWithNext);

        byte[] saved;
        RedactionReport first, second;
        using (var doc = PdfDocument.Open(source))
        {
            first = doc.RedactText(Secret, RedactionOptions.Default);
            second = doc.RedactText("Smith", RedactionOptions.Default);
            saved = doc.SaveToBytes();
        }
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "Smith").Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("Name").And.NotContain("Smith");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, secretUnderline).Should().BeLessThan(0.001);
        InkFractionIn(after!, smithUnderline).Should().BeLessThan(0.001);
        InkFractionIn(after!, Inset(nameUnderline)).Should().BeGreaterThan(0.5, "Name was never redacted");
        InkFractionIn(after!, new PdfRectangle(100, 50, 180, 60)).Should().BeGreaterThan(0.95,
            "the first redaction's box is not taken by the second: it is not sized to Smith");
        InkFractionIn(after!, new PdfRectangle(210, 50, 245, 60)).Should().BeGreaterThan(0.95);
        first.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1);
        second.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1);
    }

    public static TheoryData<string, string, WidthPolicy> WordSizedAnnotations()
    {
        // The Rect of an Underline or StrikeOut can be the stroke alone, below
        // or through the glyphs rather than around them.
        var data = new TheoryData<string, string, WidthPolicy>();
        foreach (var policy in Enum.GetValues<WidthPolicy>())
        {
            data.Add("Highlight", "84.46 43 195.6 68", policy);
            data.Add("Underline", "84.46 45.5 195.6 47.5", policy);
            data.Add("StrikeOut", "84.46 55 195.6 57", policy);
            data.Add("Squiggly", "84.46 44 195.6 47", policy);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WordSizedAnnotations))]
    public void EveryPolicy_RemovesTheMarkupAnnotationSizedToTheWord(string subtype, string rect, WidthPolicy policy)
    {
        var quads = "84.46 68 195.6 68 84.46 43 195.6 43";
        var source = Fixture(Line,
            $"/Annots [<< /Type /Annot /Subtype /{subtype} /Rect [{rect}] /QuadPoints [{quads}] /C [1 0 0] >>]");
        SavedPdfLeakScanner.FindTerm(source, $"/{subtype}").Should().NotBeEmpty("fixture sanity");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false, Width = policy }, out _);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, $"/{subtype}").Should().BeEmpty(
            $"a {subtype} annotation at the word's width states it under {policy} (#1753)");
    }

    [Theory]
    [InlineData("Highlight", "84.46 43 195.6 68")]
    [InlineData("Underline", "84.46 45.5 195.6 47.5")]
    public void Default_TaggedAnnotation_LeavesTheFile_StructureTreeItemCut(string subtype, string rect)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // #2045: the structure tree names the annotation (/OBJR) and its Popup
        // names it as /Parent. Off the page is not out of the file while either
        // reaches it; DECORATION-NOTE is not the secret, so only the annotation
        // object leaving the file removes it.
        const string Note = "DECORATION-NOTE";
        var quads = "84.46 68 195.6 68 84.46 43 195.6 43";
        var source = Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 {PageHeight}] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R 9 0 R] >>",
            Stream(Line),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Annot /Subtype /{subtype} /Rect [{rect}] /QuadPoints [{quads}] /C [1 1 0] " +
            $"/Contents ({Note}) /P 3 0 R /Popup 9 0 R >>",
            "<< /Type /StructTreeRoot /K 8 0 R >>",
            "<< /Type /StructElem /S /Annot /P 7 0 R /Pg 3 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>",
            "<< /Type /Annot /Subtype /Popup /Rect [300 80 390 110] /Parent 6 0 R >>");
        SavedPdfLeakScanner.FindTerm(source, Note).Should().NotBeEmpty("fixture sanity");
        using (var before = MutoolReferenceRenderer.RenderPage(WriteTemp(source), 1, Dpi))
            InkFractionIn(before!, new PdfRectangle(84.46, 43, 195.6, 68)).Should().BeGreaterThan(0.01,
                $"fixture sanity: mutool draws the {subtype}");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false }, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, Note).Should().BeEmpty(
            "the annotation left the file, not just its page (#2045)");
        SavedPdfLeakScanner.FindTerm(saved, "/Popup").Should().BeEmpty("its Popup went with it");
        SavedPdfLeakScanner.FindTerm(saved, "/StructTreeRoot").Should().NotBeEmpty("the tags are kept");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, new PdfRectangle(84.46, 43, 195.6, 68)).Should().BeLessThan(0.001);
        report.Removals.Should().Contain(r => r.Feature == CutItemsRow && r.Count == 1);
        report.Carriers.Should().NotContain(c => !c.Scrubbed,
            "nothing reaches the removed annotation after the cut");
    }

    // ---- #1905: a mark made from a text selection (the GUI's right-click Redact) ----

    public static TheoryData<string, string> WordSizedDecorationKinds()
    {
        var data = new TheoryData<string, string>();
        foreach (var (kind, decoration) in WordSizedDecorations) data.Add(kind, decoration);
        return data;
    }

    [Theory]
    [MemberData(nameof(WordSizedDecorationKinds))]
    public void TextSelection_RemovesTheDecorationSizedToTheSelectedWord(string kind, string decoration)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Fixture($"q {decoration} Q\n{Line}");
        using (var before = MutoolReferenceRenderer.RenderPage(WriteTemp(source), 1, Dpi))
            InkFractionIn(before!, WordExtent).Should().BeGreaterThan(0.01, $"fixture sanity: the {kind} is drawn");

        var saved = RedactSelection(source, RedactionOptions.Default with { DrawBox = false }, textSelection: true, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret).And.Contain("Name");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, WordExtent).Should().BeLessThan(0.001,
            $"the {kind} was sized to the selected word, so it states its width (#1905)");
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1,
            "every removal is reported");
    }

    [Fact]
    public void DraggedArea_OverTheSameWord_KeepsTheDecoration()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The anti-vacuity half: the identical rectangle as a dragged box is not
        // a word, so its underline stays (today's behaviour, kept on purpose).
        var underline = new PdfRectangle(84.46, 40, 195.6, 41.2);
        var source = Fixture($"q {Rule(underline)} Q\n{Line}");

        var saved = RedactSelection(source, RedactionOptions.Default with { DrawBox = false }, textSelection: false, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret);
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, Inset(underline)).Should().BeGreaterThan(0.5, "a dragged area is not a word");
        report.Removals.Should().NotContain(r => r.Feature.Contains("sized to a redacted word"));
    }

    [Fact]
    public void TextSelection_KeepsTheNextWordsDecoration_TheParagraphRule_AndAnotherOccurrencesDecoration()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // A second ALBERTINA on the line above, underlined: a selection is that
        // one run, not the text, so the other occurrence and its underline stay.
        // "Also " advances 2.223 em (44.46 pt), so it spans 64.46..175.6.
        const string secondLine = "BT /F1 20 Tf 20 90 Td (Also ALBERTINA) Tj ET";
        var secretUnderline = new PdfRectangle(84.46, 40, 195.6, 41.2);
        var smithUnderline = new PdfRectangle(201.16, 40, 252.28, 41.2);
        var otherUnderline = new PdfRectangle(64.46, 80, 175.6, 81.2);
        var bothUnderline = new PdfRectangle(84.46, 36, 252.28, 37.2);
        var paragraph = new PdfRectangle(10, 10, 390, 11.2);
        var source = Fixture(string.Join(" ", new[] { secretUnderline, smithUnderline, otherUnderline, bothUnderline, paragraph }
                                 .Select(Rule))
                             + "\n" + LineWithNext + "\n" + secondLine);

        var saved = RedactSelection(source, RedactionOptions.Default with { DrawBox = false }, textSelection: true, out var report);
        var path = WriteTemp(saved);

        var text = MutoolTextExtractor.ExtractPage(path, 1)!;
        text.Split(Secret).Length.Should().Be(2, "exactly one ALBERTINA, the other occurrence, is left");
        text.Should().Contain("Also").And.Contain("Smith");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, secretUnderline).Should().BeLessThan(0.001, "the selected word's underline goes");
        InkFractionIn(after!, Inset(smithUnderline)).Should().BeGreaterThan(0.5, "the next word's underline is not the selected word's");
        InkFractionIn(after!, Inset(otherUnderline)).Should().BeGreaterThan(0.5, "another occurrence was not selected");
        InkFractionIn(after!, Inset(bothUnderline)).Should().BeGreaterThan(0.5, "an underline across both words spans more than the selection");
        InkFractionIn(after!, Inset(paragraph)).Should().BeGreaterThan(0.5, "a paragraph-wide rule is kept");
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1);
    }

    [Fact]
    public void TextSelection_Default_MovesNoSurvivingGlyph()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Fixture($"q 84.46 40 111.14 1.2 re f Q q 201.16 46.5 51.12 1.2 re f Q\n{LineWithNext}");
        var before = MutoolGlyphPositions.ExtractPage(WriteTemp(source), 1);
        var saved = RedactSelection(source, RedactionOptions.Default, textSelection: true, out var report);
        var after = MutoolGlyphPositions.ExtractPage(WriteTemp(saved), 1);
        before.Should().NotBeNull();
        after.Should().NotBeNull();
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word"), "anti-vacuity: the decoration pass ran");

        static bool Survives(MutoolGlyphPositions.Glyph g) =>
            !string.IsNullOrWhiteSpace(g.Char) && (g.X < WordExtent.Left - 1 || g.X >= WordExtent.Right - 1);
        var expected = before!.Where(Survives).ToList();
        expected.Select(g => g.Char).Should().Equal("N", "a", "m", "e", ":", "S", "m", "i", "t", "h");
        var kept = after!.Where(g => !string.IsNullOrWhiteSpace(g.Char)).ToList();
        kept.Select(g => g.Char).Should().Equal(expected.Select(g => g.Char));
        for (var i = 0; i < expected.Count; i++)
        {
            kept[i].X.Should().BeApproximately(expected[i].X, 0.01, $"glyph {expected[i].Char} must not move");
            kept[i].Y.Should().BeApproximately(expected[i].Y, 0.01, $"glyph {expected[i].Char} must not move");
        }
    }

    /// <summary>
    /// An Underline annotation whose /Rect is the stroke below the glyphs does
    /// not meet the selection's area, so the area scrub never reached it. As a
    /// word-sized annotation it goes with a selection, its Popup with it, and
    /// the structure tree's /OBJR is cut (#2045); a dragged area keeps it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaggedUnderlineBelowTheWord_GoesWithASelection_StaysWithADraggedArea(bool textSelection)
    {
        const string Note = "DECORATION-NOTE";
        var quads = "84.46 42 195.6 42 84.46 40 195.6 40";
        var source = Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 {PageHeight}] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R 9 0 R] >>",
            Stream(Line),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Annot /Subtype /Underline /Rect [84.46 40 195.6 42] /QuadPoints [{quads}] /C [1 0 0] " +
            $"/Contents ({Note}) /P 3 0 R /Popup 9 0 R >>",
            "<< /Type /StructTreeRoot /K 8 0 R >>",
            "<< /Type /StructElem /S /Annot /P 7 0 R /Pg 3 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>",
            "<< /Type /Annot /Subtype /Popup /Rect [300 80 390 110] /Parent 6 0 R >>");
        SavedPdfLeakScanner.FindTerm(source, Note).Should().NotBeEmpty("fixture sanity");

        var saved = RedactSelection(source, RedactionOptions.Default with { DrawBox = false }, textSelection, out var report);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        if (textSelection)
        {
            SavedPdfLeakScanner.FindTerm(saved, Note).Should().BeEmpty("the annotation left the file, not just its page");
            SavedPdfLeakScanner.FindTerm(saved, "/Popup").Should().BeEmpty("its Popup went with it");
            report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1);
            report.Removals.Should().Contain(r => r.Feature == CutItemsRow && r.Count == 1);
            report.Carriers.Should().NotContain(c => !c.Scrubbed, "nothing reaches the removed annotation after the cut");
        }
        else
        {
            SavedPdfLeakScanner.FindTerm(saved, Note).Should().NotBeEmpty("a dragged area is not a word");
            report.Removals.Should().NotContain(r => r.Feature.Contains("sized to a redacted word"));
        }
    }

    /// <summary>
    /// Redact the bottom-most ALBERTINA's glyph box on page 1 as the GUI does: a
    /// text selection (<paramref name="textSelection"/>) or a dragged area.
    /// </summary>
    private static byte[] RedactSelection(byte[] source, RedactionOptions options, bool textSelection, out RedactionReport report)
    {
        using var doc = PdfDocument.Open(source);
        var page = doc.GetPage(1);
        var area = PdfDocumentRedactionExtensions.FindTextMatches(page.Letters, Secret, caseSensitive: true)
            .Select(PdfDocumentRedactionExtensions.BoundingBoxOf)
            .OrderBy(box => box.Bottom)
            .First();
        report = textSelection
            ? page.RedactTextRunsWithReport(new[] { area }, options)
            : page.RedactAreaWithReport(area, options);
        return doc.SaveToBytes();
    }

    private static PdfRectangle Inset(PdfRectangle r) => new(r.Left + 3, r.Bottom, r.Right - 3, r.Top);

    private static string Rule(PdfRectangle r) =>
        FormattableString.Invariant($"q {r.Left} {r.Bottom} {r.Right - r.Left} {r.Top - r.Bottom} re f Q");

    private static byte[] Redact(byte[] source, RedactionOptions options, out RedactionReport report)
    {
        using var doc = PdfDocument.Open(source);
        report = doc.RedactText(Secret, options);
        return doc.SaveToBytes();
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
        int x0 = Math.Max(0, (int)Math.Floor(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right * scale));
        int y0 = Math.Max(0, (int)Math.Floor((PageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling((PageHeight - box.Bottom) * scale));
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                total++;
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static byte[] Fixture(string content, string pageExtra = "") => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 {PageHeight}] /Contents 4 0 R " +
        $"/Resources << /Font << /F1 5 0 R >> >> {pageExtra} >>",
        Stream(content),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private static string Stream(string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

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
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-decoration-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
