using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.MultiPageWidgetFixtures;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #2040: a form field with widgets on several pages. Each widget's appearance is page text on the
/// page whose <c>/Annots</c> lists it (ISO 32000-2 12.5.2), not only the first widget's page.
/// Extraction keyed fields by their first widget, so the text another page's widget draws was
/// neither found nor redacted (redaction cannot remove what extraction cannot read). Independent
/// oracles (mutool per page, pdftotext) are in Excise.Rendering.Tests
/// (MultiPageWidgetOracleTests).
/// </summary>
public class MultiPageWidgetExtractionTests
{
    private const string Shared = "CASE4471";
    private const string Page2Only = "ZEBRAQUILL";
    private const string Page3Only = "MOTHGLASS";

    private static List<Letter> Matches(PdfPage page, string term)
        => PdfDocumentRedactionExtensions.FindTextMatches(page.Letters, term, caseSensitive: true)
            .SelectMany(m => m).ToList();

    [Fact]
    public void SharedValue_IsPageText_OnEveryWidgetsPage()
    {
        using var document = PdfDocument.Open(F.Build([Shared, Shared], Shared));

        for (var page = 1; page <= 2; page++)
        {
            var letters = Matches(document.GetPage(page), Shared);
            letters.Should().HaveCount(Shared.Length, $"page {page}'s widget draws the value once");
            letters[0].StartX.Should().BeApproximately(F.GlyphX, 0.01, "read at the drawn glyph");
            letters[0].StartY.Should().BeApproximately(F.GlyphY, 0.01);
        }
    }

    [Fact]
    public void PerWidgetAppearance_IsReadOnItsOwnPage_AndNowhereElse()
    {
        using var document = PdfDocument.Open(F.Build([Shared, Page2Only], Shared));

        document.GetPage(2).Text.Should().Contain(Page2Only, "page 2's widget draws it");
        Matches(document.GetPage(2), Page2Only)[0].StartX.Should().BeApproximately(F.GlyphX, 0.01);
        document.GetPage(1).Text.Should().NotContain(Page2Only, "page 1 does not draw it");
        // /V is read on each page whose widgets do not draw it, as for any appearance that draws
        // something other than the value (#2039): a reader honouring /NeedAppearances draws it there.
        Matches(document.GetPage(2), Shared).Should().HaveCount(Shared.Length);
        Matches(document.GetPage(1), Shared).Should().HaveCount(Shared.Length, "page 1 draws /V: read once");
    }

    [Fact]
    public void ThreeWidgets_EachPageReadsItsOwnWidget()
    {
        using var document = PdfDocument.Open(F.Build([Shared, Page2Only, Page3Only], Shared));

        document.GetPage(1).Text.Should().Contain(Shared).And.NotContain(Page2Only).And.NotContain(Page3Only);
        document.GetPage(2).Text.Should().Contain(Page2Only).And.NotContain(Page3Only);
        document.GetPage(3).Text.Should().Contain(Page3Only).And.NotContain(Page2Only);
    }

    [Fact]
    public void WidgetWithoutP_IsReadOnThePageWhoseAnnotsListsIt()
    {
        using var document = PdfDocument.Open(F.Build([Shared, Page2Only], Shared, omitP: [2]));

        document.GetPage(2).Text.Should().Contain(Page2Only);
        document.GetPage(1).Text.Should().NotContain(Page2Only);
    }

    [Fact]
    public void FirstWidgetWithoutP_StillReadsBothPages()
    {
        using var document = PdfDocument.Open(F.Build([Shared, Page2Only], Shared, omitP: [1, 2]));

        document.GetPage(1).Text.Should().Contain(Shared);
        document.GetPage(2).Text.Should().Contain(Page2Only);
    }

    [Fact]
    public void HiddenFirstWidget_TheShownWidgetOnPage2_IsPageText()
    {
        using var document = PdfDocument.Open(F.Build([Page2Only, Page2Only], value: null, hiddenPage: 1));

        Matches(document.GetPage(2), Page2Only).Should().HaveCount(Page2Only.Length);
    }

    [Fact]
    public void SingleWidgetField_IsUnchanged()
    {
        using var document = PdfDocument.Open(F.Build([Shared], Shared, emptyPages: 1));

        Matches(document.GetPage(1), Shared).Should().HaveCount(Shared.Length);
        document.GetPage(2).Text.Should().NotContain(Shared);
        document.GetPage(1).GetFormFields().Should().ContainSingle(f => f.FullName == F.FieldName);
        document.GetPage(2).GetFormFields().Should().BeEmpty();
    }

    [Fact]
    public void ValueNoWidgetDraws_IsReadOnEachPage_AtThatPagesWidget()
    {
        // No appearance draws text: the /V fallback (letters laid out in /Rect) stands in on each
        // page a widget of the field is on.
        using var document = PdfDocument.Open(F.Build([null, null], Shared));

        for (var page = 1; page <= 2; page++)
            Matches(document.GetPage(page), Shared).Should().HaveCount(Shared.Length, $"page {page}");
    }

    [Fact]
    public void RedactingAPage2OnlyTerm_RemovesIt_FromTheSavedFile()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only], Shared)))
        {
            var report = document.RedactText(Page2Only, RedactionOptions.Default with { DrawBox = false });
            report.VerifiedRemovals.Should().BeGreaterThan(0, "the term is drawn on page 2");
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page2Only).Should().BeEmpty("no appearance keeps it");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(2).Text.Should().NotContain(Page2Only);
        reopened.GetPage(1).Text.Should().Contain(Shared, "page 1 is not part of the delta");
        Encoding.Latin1.GetString(saved).Should().Contain($"({Shared})", "nor is /V");
    }

    [Fact]
    public void RedactingAPage3OnlyTerm_OfAThreeWidgetField_RemovesIt()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only, Page3Only], Shared, omitP: [3])))
        {
            document.RedactText(Page3Only, RedactionOptions.Default with { DrawBox = false })
                .VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().BeEmpty();
        // Page 2's own appearance, which never drew the term, is dropped with it today: See issue #2059.
    }

    [Fact]
    public void RedactingTheSharedValue_RemovesEveryPagesCopy()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, Shared, Shared], Shared)))
        {
            document.RedactText(Shared, RedactionOptions.Default with { DrawBox = false })
                .VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Shared).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        for (var page = 1; page <= 3; page++)
            reopened.GetPage(page).Text.Should().NotContain(Shared, $"page {page}");
    }

    [Fact]
    public void RedactingTheValue_ReadOnPage2From_V_RemovesIt_WithNoSurvivor()
    {
        // Page 2 reads /V by estimate (its widget draws something else); page 1's pass masks /V
        // first, and page 2's pass must neither miss nor report its copy as surviving.
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only], Shared)))
        {
            var report = document.RedactText(Shared, RedactionOptions.Default with { DrawBox = false });
            report.VerifiedRemovals.Should().BeGreaterThan(0);
            report.Survived.Should().Be(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Shared).Should().BeEmpty();
    }

    [Fact]
    public void AreaRedaction_OverPage2sWidget_RemovesItsAppearance()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only], Shared)))
        {
            var r = F.WidgetRect;
            document.GetPage(2).RedactArea(new PdfRectangle(r[0], r[1], r[2], r[3]),
                RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page2Only).Should().BeEmpty("the area covers the widget that draws it");
    }
}
