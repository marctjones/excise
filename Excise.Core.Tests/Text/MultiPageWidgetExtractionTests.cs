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
        // #2059: the widgets that never drew the term keep their appearances and their text.
        SavedPdfLeakScanner.FindTerm(saved, Page2Only).Should().NotBeEmpty("page 2's widget is not part of the delta");
        Encoding.Latin1.GetString(saved).Should().NotContain("/NeedAppearances", "no widget lost its appearance");
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 1).Should().Contain(Shared);
        NormalAppearanceText(reopened, 2).Should().Contain(Page2Only);
        NormalAppearanceText(reopened, 3).Should().NotBeNull("rewritten, not dropped").And.NotContain(Page3Only);
        reopened.GetPage(2).Text.Should().Contain(Page2Only);
        reopened.GetPage(1).Text.Should().Contain(Shared);
        Encoding.Latin1.GetString(saved).Should().Contain($"({Shared})", "/V does not hold the term");
    }

    /// <summary>#2059: a widget that draws no text keeps its (empty) appearance: dropping it set
    /// /NeedAppearances and a viewer drew /V there instead.</summary>
    [Fact]
    public void RedactingAPage3OnlyTerm_KeepsAnEmptyAppearance_OnAnotherPage()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, null, Page3Only], Shared)))
        {
            document.RedactText(Page3Only, RedactionOptions.Default with { DrawBox = false })
                .VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().BeEmpty();
        Encoding.Latin1.GetString(saved).Should().NotContain("/NeedAppearances");
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 2).Should().NotBeNull("the empty appearance is kept");
    }

    /// <summary>#2059 (c): the term is /V and page 1 draws it; pages 2 and 3 draw their own text.
    /// /V and page 1 are cut; nothing that drew the old value survives, and the other pages keep
    /// theirs.</summary>
    [Fact]
    public void RedactingTheValue_CutsEveryCopyOfIt_AndKeepsTheUnrelatedWidgets()
    {
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only, Page3Only], Shared)))
        {
            report = document.RedactText(Shared, RedactionOptions.Default with { DrawBox = false });
            report.Survived.Should().Be(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Shared).Should().BeEmpty("neither /V nor page 1's appearance keeps it");
        SavedPdfLeakScanner.FindTerm(saved, Page2Only).Should().NotBeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().NotBeEmpty();
        report.Removals.Should().NotContain(r => r.Feature.Contains("redacted form field's widget"));
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 1).Should().NotBeNull().And.NotContain(Shared);
        NormalAppearanceText(reopened, 2).Should().Contain(Page2Only);
        NormalAppearanceText(reopened, 3).Should().Contain(Page3Only);
        for (var page = 1; page <= 3; page++)
            reopened.GetPage(page).Text.Should().NotContain(Shared, $"page {page}");
    }

    /// <summary>#2059 (d): pages 1 and 3 share ONE appearance stream drawing the term; page 2 draws
    /// its own. Both sharers get the rewritten copy; page 2 keeps its stream.</summary>
    [Fact]
    public void RedactingATermInASharedAppearance_RewritesBothSharers_KeepsTheOther()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Page3Only, Page2Only, null], Shared,
                   shareAppearance: new Dictionary<int, int> { [3] = 1 })))
        {
            document.RedactText(Page3Only, RedactionOptions.Default with { DrawBox = false })
                .VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().BeEmpty();
        Encoding.Latin1.GetString(saved).Should().NotContain("/NeedAppearances");
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 1).Should().NotBeNull().And.NotContain(Page3Only);
        NormalAppearanceText(reopened, 3).Should().NotBeNull().And.NotContain(Page3Only);
        NormalAppearanceText(reopened, 2).Should().Contain(Page2Only);
    }

    /// <summary>#2059: page 2's appearance draws the term in a font whose /ToUnicode reads other
    /// letters (#2043). It does not read the term and cannot be shown free of it: dropped and
    /// reported, never kept.</summary>
    [Fact]
    public void ASiblingAppearanceThatHidesTheTermBehindItsFont_IsDroppedAndReported()
    {
        var input = F.Build([Shared, Page3Only, Page3Only], Shared, shiftedFontPages: [2]);
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            report = document.RedactText(Page3Only, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().BeEmpty("the hidden copy goes too");
        report.Removals.Should().ContainSingle(r => r.Feature.Contains("redacted form field's widget"))
            .Which.Count.Should().Be(1);
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 2).Should().BeNull();
        NormalAppearanceText(reopened, 1).Should().Contain(Shared);
    }

    /// <summary>#2059: the same font drawing text that is not the term: its codes read twice
    /// (encoding and /ToUnicode) and neither spells the term, so it is kept.</summary>
    [Fact]
    public void ASiblingAppearanceInAShiftedFont_NotDrawingTheTerm_IsKept()
    {
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only, Page3Only], Shared, shiftedFontPages: [2])))
        {
            document.RedactText(Page3Only, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 2).Should().Contain(Page2Only);
    }

    /// <summary>#2059: the byte read of a /ToUnicode font is Latin-1, which a MacRoman or Standard
    /// encoding agrees with only in ASCII. For a term with any other letter that font cannot be
    /// checked, so its appearance is dropped and reported even when it draws something else.</summary>
    [Fact]
    public void ANonAsciiTerm_ASiblingInAToUnicodeFont_IsDroppedAndReported()
    {
        const string term = "MÖTHGLASS";
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(F.Build([Shared, Page2Only, term], Shared, shiftedFontPages: [2])))
        {
            report = document.RedactText(term, RedactionOptions.Default with { DrawBox = false });
            report.VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        report.Removals.Should().ContainSingle(r => r.Feature.Contains("redacted form field's widget"))
            .Which.Count.Should().Be(1);
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 2).Should().BeNull();
        NormalAppearanceText(reopened, 1).Should().Contain(Shared, "Helvetica without /ToUnicode reads once, honestly");
    }

    /// <summary>#2059: with a format action, an appearance can show the cut value in a form the
    /// term search does not match ("$1,234.50" for 1234.5). No appearance of that field is kept
    /// for not holding the term. The redaction profile removes the script before the scrub runs,
    /// and an earlier redaction of another term removes it before this one starts: neither may
    /// make the field look unformatted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactingTheValue_OfAFormattedField_KeepsNoAppearance(bool earlierRedaction)
    {
        const string value = "1234.5";
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(F.Build(["$1,234.50", Page2Only], value, formatAction: true)))
        {
            if (earlierRedaction)
                document.RedactText("Page 2 body", RedactionOptions.Default with { DrawBox = false });
            report = document.RedactText(value, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, value).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "1,234").Should().BeEmpty("the formatted copy goes with the value");
        report.Removals.Should().ContainSingle(r => r.Feature.Contains("redacted form field's widget"));
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 1).Should().BeNull();
        NormalAppearanceText(reopened, 2).Should().BeNull("the field's value may be restated anywhere it is shown");
    }

    /// <summary>#2059: without a format action, a formatted copy of the term still counts as
    /// holding it (compared on letters and digits), so it is not kept.</summary>
    [Fact]
    public void RedactingTheValue_AFormattedCopyWithoutAnAction_IsNotKept()
    {
        const string value = "1234.5";
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build(["$1,234.50", Page2Only], value)))
        {
            document.RedactText(value, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, "1,234").Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        NormalAppearanceText(reopened, 2).Should().Contain(Page2Only);
    }

    /// <summary>The decoded <c>/AP /N</c> of the field's widget on <paramref name="page"/>, or null
    /// when the widget has no normal appearance.</summary>
    private static string? NormalAppearanceText(PdfDocument document, int page)
    {
        var widget = document.GetPage(1).GetFormFields().Single(f => f.FullName == F.FieldName)
            .WidgetDictionaries[page - 1];
        if (document.Resolve(widget.GetOptional("AP") ?? Excise.Core.Primitives.PdfNull.Instance)
                is not Excise.Core.Primitives.PdfDictionary ap
            || document.Resolve(ap.GetOptional("N") ?? Excise.Core.Primitives.PdfNull.Instance)
                is not Excise.Core.Primitives.PdfStream normal)
            return null;
        return Encoding.Latin1.GetString(normal.DecodedData);
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
