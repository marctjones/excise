using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.Core.Xfa;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.WidgetDisplayTextFixtures;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #2039: a widget's text is what its appearance DRAWS, read through the one content-stream walk at
/// the glyph positions ISO 32000-2 12.5.5 Algorithm 8.1 puts it, not letters made from <c>/V</c> and
/// placed by estimate. A choice field's <c>/V</c> is its save value (§12.7.4.4; XFA 3.3 p758-760),
/// so search and redaction that read only <c>/V</c> could not find the text the page shows.
/// Independent oracles (mutool text and positions, pdftotext, qpdf, pixels) are in
/// Excise.Rendering.Tests (WidgetAppearanceTextOracleTests).
/// </summary>
public class WidgetAppearanceTextTests
{
    private static PdfDocument Open() => PdfDocument.Open(F.Build());

    private static List<Letter> Run(IReadOnlyList<Letter> letters, string term)
    {
        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, term, caseSensitive: true);
        matches.Should().HaveCount(1, $"'{term}' is on the page once");
        return matches[0];
    }

    [Fact]
    public void ChoiceField_ItsDisplayedText_IsFound_NotOnlyItsSaveValue()
    {
        using var document = Open();
        var page = document.GetPage(1);

        page.Text.Should().Contain(F.ChoiceDisplay, "the widget's appearance draws the display text");
        PdfDocumentRedactionExtensions.FindTextMatches(page.Letters, F.ChoiceDisplay, caseSensitive: true)
            .Should().NotBeEmpty("search must find what the page shows");
    }

    [Fact]
    public void AppearanceLetters_SitAtTheDrawnGlyphs_ByAlgorithm81()
    {
        using var document = Open();
        var canada = Run(document.GetPage(1).Letters, F.ChoiceDisplay);

        canada[0].StartX.Should().BeApproximately(F.ChoiceGlyphX, 0.01, "/Matrix first, then BBox onto Rect");
        canada[0].StartY.Should().BeApproximately(F.ChoiceGlyphY, 0.01);
        canada[0].FontSize.Should().BeApproximately(F.ChoiceFontSize, 0.01);
        canada.Should().AllSatisfy(l => l.FontName.Should().StartWith("AcroForm:",
            "redaction routes a widget match to the interactive scrubber by this prefix"));
        canada.Should().AllSatisfy(l => l.OperandByteOffset.Should().Be(-1,
            "no page content operand backs an appearance glyph"));

        var ada = Run(document.GetPage(1).Letters, F.TextValue);
        ada[0].StartX.Should().BeApproximately(F.TextGlyphX, 0.01, "/Matrix omitted is the identity (Table 93)");
        ada[0].StartY.Should().BeApproximately(F.TextGlyphY, 0.01);
    }

    [Fact]
    public void AppearanceText_StartsFromTheDefaultTextState_NotWhatThePageLeft()
    {
        using var document = Open();
        var canada = Run(document.GetPage(1).Letters, F.ChoiceDisplay);

        canada.Should().AllSatisfy(l => l.TextRenderMode.Should().Be(0, "Tr defaults to 0; the page's trailing 3 Tr is not the appearance's"));
        // Helvetica 'C' is 722/1000 em; at 12 pt that is 8.66, and 4.33 under the page's leftover 50 Tz.
        canada[0].Width.Should().BeApproximately(8.664, 0.05, "Tz defaults to 100");
    }

    [Fact]
    public void AValueTheAppearanceDraws_IsReadOnce_NotAlsoAsSyntheticLetters()
    {
        using var document = Open();
        var text = document.GetPage(1).Text;

        CountOf(text, F.TextValue).Should().Be(1, "the drawn value and /V are the same text");
        CountOf(text, F.ChoiceSave).Should().Be(0,
            "a save value whose option's display text the appearance draws is not page text");
    }

    [Fact]
    public void CombField_StaysFindableAsOneWord()
    {
        using var document = Open();
        PdfDocumentRedactionExtensions.FindTextMatches(document.GetPage(1).Letters, F.CombValue, caseSensitive: true)
            .Should().NotBeEmpty("a comb draws one glyph per cell; the value must still be found as typed");
    }

    [Fact]
    public void AnAppearanceThatDrawsNoText_FallsBackToTheValue()
    {
        using var document = Open();
        document.GetPage(1).Text.Should().Contain(F.FallbackValue);
    }

    [Fact]
    public void RedactingTheDisplayedText_RemovesTheAppearanceText_TheSaveValue_AndTheOption()
    {
        byte[] saved;
        using (var document = Open())
        {
            var report = document.RedactText(F.ChoiceDisplay, RedactionOptions.Default);
            report.VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.ChoiceDisplay).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        var country = reopened.GetAcroForm()!.FindField("Country")!.RawDictionary;
        (reopened.Resolve(country.GetOptional("V") ?? PdfNull.Instance) as PdfString)?.Value
            .Should().NotBe(F.ChoiceSave, "the save value encodes the redacted selection");
        Encoding.Latin1.GetString(saved).Should().NotContain($"({F.ChoiceSave})",
            "the export value paired with the redacted display is gone too");
        reopened.GetPage(1).Text.Should().Contain(F.TextValue, "the rest of the page is untouched");
        Encoding.Latin1.GetString(saved).Should().Contain($"({F.OtherDisplay})", "the other option is not part of the delta");
    }

    // ------------------------------------------------------------ XFA S1 generated widgets (#2028)

    private static readonly string CountryTemplate = XfaTestForms.Template(
        "<subform name=\"S\" x=\"0in\" y=\"0in\" w=\"8in\" h=\"10in\" layout=\"position\">"
        + "<field name=\"Name\" x=\"1in\" y=\"0.5in\" w=\"3in\" h=\"0.3in\"><ui><textEdit/></ui></field>"
        + "<field name=\"Country\" x=\"1in\" y=\"3in\" w=\"3in\" h=\"0.3in\"><ui><choiceList/></ui>"
        + "<items><text>Canada</text><text>France</text></items><items save=\"1\"><text>CA</text><text>FR</text></items></field>"
        + "</subform>",
        layout: "position");

    private static PdfDocument LaidOut()
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(CountryTemplate,
            XfaTestForms.Data("<S><Name>Grace Hopper</Name><Country>FR</Country></S>")));
        var result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = true }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        return document;
    }

    [Fact]
    public void GeneratedChoiceWidget_ItsDisplayedText_IsFound()
    {
        using var document = LaidOut();
        var country = document.GetAcroForm()!.FindField("form1[0].S[0].Country[0]")!;
        country.Value.Should().Be("FR", "fixture sanity: /V is the save value");

        document.GetPage(1).Text.Should().Contain("France");
        PdfDocumentRedactionExtensions.FindTextMatches(document.GetPage(1).Letters, "France", caseSensitive: true)
            .Should().NotBeEmpty();
    }

    [Fact]
    public void GeneratedWidgets_ReadTheSameCharacters_BeforeAndAfterTheFlatten()
    {
        using var document = LaidOut();
        var before = Characters(new TextExtractor(document.GetPage(1)).ExtractText(TestContext.Current.CancellationToken));

        PdfXfaLayout.FlattenGeneratedXfaFields(document).Should().NotBeNull();
        document.GetPage(1).InvalidateTextExtractionCache();
        var after = Characters(new TextExtractor(document.GetPage(1)).ExtractText(TestContext.Current.CancellationToken));

        after.Should().BeEquivalentTo(before, "decision 17 stamps the same appearance into the page: the same text, read the same way");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RedactingAGeneratedChoiceFieldsDisplayedText_LeavesNoCopy(bool flattenFirst)
    {
        byte[] saved;
        using (var document = LaidOut())
        {
            if (flattenFirst)
                PdfXfaLayout.FlattenGeneratedXfaFields(document).Should().NotBeNull();
            var report = document.RedactText("France", RedactionOptions.Default);
            report.VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, "France").Should().BeEmpty("no appearance, option, value or datasets copy");
        using var reopened = PdfDocument.Open(saved);
        (reopened.Resolve(reopened.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary)?
            .ContainsKey("XFA").Should().NotBe(true, "decision 5: the datasets go with /XFA");
        if (reopened.GetAcroForm()?.FindField("form1[0].S[0].Country[0]") is { } field)
            field.Value.Should().NotBe("FR", "the save value encodes the redacted selection");
        reopened.GetPage(1).Text.Should().Contain("Grace Hopper", "the other field is not part of the delta");
    }

    private static int CountOf(string text, string term)
    {
        int count = 0;
        for (int at = text.IndexOf(term, StringComparison.Ordinal); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static Dictionary<char, int> Characters(string text)
        => text.Where(c => !char.IsWhiteSpace(c)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
}
