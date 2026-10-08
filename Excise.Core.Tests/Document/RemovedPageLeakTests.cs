using System;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Xfa;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2012: a removed page, and a field drawn only on it, must not survive in the
/// saved file through a bookmark, a destination, a link, the open action, a
/// structure element or the form's field tree. Oracle: the saved bytes, every
/// stream inflated by <see cref="SavedPdfLeakScanner"/> (not excise's reader).
/// The qpdf-backed twin, with an encrypted save, is
/// <c>Excise.Rendering.Tests.Differential.RemovedPageLeakOracleTests</c>.
/// </summary>
public sealed class RemovedPageLeakTests
{
    public static TheoryData<Back, string> Cases()
    {
        var data = new TheoryData<Back, string>();
        foreach (var back in Enum.GetValues<Back>())
        {
            data.Add(back, "1.4");  // classic xref table
            data.Add(back, "1.7");  // object streams
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RemovePage_ThenSave_RemovedPageIsNotInTheFile(Back back, string version)
    {
        var source = F.Build(back, version);
        foreach (var token in F.RemovedTokens(back))
            SavedPdfLeakScanner.FindTerm(source, token).Should().NotBeEmpty($"the fixture must carry {token}");

        byte[] saved;
        using (var doc = PdfDocument.Open(source))
        {
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        AssertAbsent(saved, $"{back}/{version}", F.RemovedTokens(back));

        // Save As again from the saved copy: nothing comes back.
        using (var reopened = PdfDocument.Open(saved))
            AssertAbsent(reopened.SaveToBytes(), $"{back}/{version} resaved", F.RemovedTokens(back));

        AssertExciseReadsTheResult(saved, $"{back}/{version}");
    }

    [Fact]
    public void RemovePage_ObjectSharedWithAKeptPage_StaysInTheFile()
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.Outline, "1.7", pageThreeSharesFont: true)))
        {
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.PageText).Should().BeEmpty("the removed page's content goes");
        SavedPdfLeakScanner.FindTerm(saved, F.FontName).Should().NotBeEmpty("page 3 still uses the shared font");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(2).Text.Should().Contain(F.KeptThree);
    }

    [Fact]
    public void RemovePage_FieldWithAWidgetOnAKeptPage_KeepsTheFieldAndThatWidget()
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.AcroFormFieldKids, "1.7", widgetOnPageOneToo: true)))
        {
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        AssertAbsent(saved, "field kept", F.PageTokens);
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetAnnotations().Should().ContainSingle("page 1's widget stays");
        reopened.GetPage(1).GetFormFields().Should().ContainSingle(f => f.Value == F.FieldValue);
    }

    [Fact]
    public void RemovePages_SeveralWithBackReferences_NoneSurvive()
    {
        // The App's Remove Pages removes in descending order, one RemoveAt each.
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.Outline, "1.7")))
        {
            doc.Pages.RemoveAt(2);
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        AssertAbsent(saved, "remove 2 and 3", [.. F.PageTokens, F.KeptThree]);
        SavedPdfLeakScanner.FindTerm(saved, F.KeptOne).Should().NotBeEmpty();
    }

    [Fact]
    public void RemovePage_ThenUndoByReinserting_PageComesBack()
    {
        // The App's undo re-inserts the captured page (Insert clones it): the
        // cut must not take the clone's content with it.
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.Outline, "1.7")))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, captured);
            saved = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.PageText).Should().NotBeEmpty("the re-inserted page is in the document");
        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(3);
        reopened.GetPage(2).Text.Should().Contain(F.PageText);
    }

    [Fact]
    public void RemovePage_SaveTwice_SecondSaveIsAlsoClean()
    {
        // The cut runs at every save, after every removal made since the last.
        using var doc = PdfDocument.Open(F.Build(Back.LinkOnKeptPage, "1.7"));
        doc.Pages.RemoveAt(1);
        AssertAbsent(doc.SaveToBytes(), "first save", F.PageTokens);
        AssertAbsent(doc.SaveToBytes(), "second save", F.PageTokens);
        doc.Pages.RemoveAt(1);
        AssertAbsent(doc.SaveToBytes(), "after a second removal", [.. F.PageTokens, F.KeptThree]);
    }

    [Fact]
    public void XfaLayout_ReplacedPlaceholderPage_IsNotInTheSavedFile()
    {
        // ApplyXfaLayout removes the placeholder pages with RemoveAt (#1547
        // fill-and-save depends on a re-layout leaving nothing behind). Here the
        // open action still points at the placeholder.
        using var doc = PdfDocument.Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate()));
        doc.Catalog["OpenAction"] = new PdfArray { (PdfObject)doc.Pages[0].Reference!, (PdfObject)new PdfName("Fit") };
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), XfaTestForms.Placeholder)
            .Should().NotBeEmpty("the fixture's placeholder page must carry its text");

        doc.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.LaidOut);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, XfaTestForms.Placeholder).Should().BeEmpty(
            "the replaced placeholder page must not ship in the saved file");
        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(1);
    }

    private static void AssertAbsent(byte[] saved, string label, string[] tokens)
    {
        foreach (var token in tokens)
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty(
                $"{label}: the removed page's {token} must not be in the saved bytes");
        SavedPdfLeakScanner.FindTerm(saved, F.KeptOne).Should().NotBeEmpty($"{label}: page 1 is kept");
    }

    private static void AssertExciseReadsTheResult(byte[] saved, string label)
    {
        // A cut page reference reads as null: the navigation readers must take it.
        using var doc = PdfDocument.Open(saved);
        doc.PageCount.Should().Be(2, label);
        doc.GetPage(1).Text.Should().Contain(F.KeptOne, label);
        doc.GetPage(2).Text.Should().Contain(F.KeptThree, label);
        foreach (var item in PdfOutlineParser.Parse(doc))
            item.PageNumber.Should().BeNull($"{label}: a bookmark to the removed page goes nowhere");
        doc.GetNamedDestinations();
        doc.GetPage(1).GetAnnotations();
        doc.GetPage(1).GetFormFields();
        doc.GetStructureTree();
    }
}
