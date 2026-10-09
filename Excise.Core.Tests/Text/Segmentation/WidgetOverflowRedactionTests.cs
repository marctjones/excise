using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.WidgetOverflowFixtures;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #2041: a term a widget's appearance draws OUTSIDE its <c>/Rect</c> (a scrolled multiline
/// field's lines below the box, clipped by the <c>/BBox</c>) is still in the file, and since
/// #2039 search finds it. The redaction must select the field by what its appearance draws, not
/// by where the match box lands: the box lies outside <c>/Rect</c>, on top of a different field.
/// The oracle is the inflating, string-decoding byte scanner over the SAVED file (no excise
/// reader is consulted for absence); the expected counts come from the fixture's construction.
/// </summary>
public class WidgetOverflowRedactionTests
{
    private static RedactionOptions Options => RedactionOptions.Default with { DrawBox = false };

    [Theory]
    [InlineData(nameof(F.TermEncoding.LiteralTj))]
    [InlineData(nameof(F.TermEncoding.KernedTJ))]
    [InlineData(nameof(F.TermEncoding.HexTj))]
    public void ClippedOverflowTerm_IsRemovedFromAppearanceAndValue_AndReportedTruthfully(string encoding)
    {
        var input = F.Build(Enum.Parse<F.TermEncoding>(encoding));
        SavedPdfLeakScanner.FindTerm(input, F.Term).Should().NotBeEmpty("planted: the scanner sees the term in the input");

        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            report = document.RedactText(F.Term, Options);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty("no appearance stream or value keeps the term");
        report.MatchesLocated.Should().Be(F.TermOccurrences, "the clipped lines draw the term twice");
        report.VerifiedRemovals.Should().Be(F.TermOccurrences);
        report.Survived.Should().Be(0);

        // Collateral, read from the saved bytes: the field's shown lines and the neighbour whose
        // /Rect the clipped lines overlap are not part of the delta.
        var after = SavedPdfLeakScanner.AllCarriersText(saved);
        after.Should().Contain(F.VisibleLine).And.Contain(F.SecondLine);
        after.Should().Contain(F.NeighbourValue);
        using var reopened = PdfDocument.Open(saved);
        var keep = Widget(reopened, "Keep");
        AppearanceStream(reopened, keep).Should().NotBeNull("the neighbour keeps its appearance");
        Encoding.Latin1.GetString(AppearanceStream(reopened, keep)!.DecodedData).Should().Contain(F.NeighbourValue);
        var notes = Widget(reopened, "Notes");
        AppearanceStream(reopened, notes).Should().NotBeNull("the field's appearance is rewritten, not dropped");
    }

    [Fact]
    public void SharedAppearance_TwoFieldsDrawingTheTerm_BothScrubbed_NeitherAppearanceDropped()
    {
        var input = F.BuildShared();
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            report = document.RedactText(F.Term, Options);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty();
        report.MatchesLocated.Should().Be(2, "both widgets draw the shared appearance");
        report.VerifiedRemovals.Should().Be(2);
        report.Survived.Should().Be(0);

        using var reopened = PdfDocument.Open(saved);
        foreach (var name in new[] { "Alpha", "Beta" })
        {
            var widget = Widget(reopened, name);
            var ap = AppearanceStream(reopened, widget);
            ap.Should().NotBeNull($"{name} keeps an appearance: a second rewrite of a clean stream must not drop it");
            Encoding.Latin1.GetString(ap!.DecodedData).Should().Contain(F.SharedSurvivor).And.NotContain(F.Term);
            (reopened.Resolve(widget.GetOptional("V") ?? PdfNull.Instance) as PdfString)!.Value
                .Should().NotContain(F.Term).And.Contain(F.SharedSurvivor);
        }
        SavedPdfLeakScanner.AllCarriersText(saved).Should().Contain(F.NeighbourValue);
    }

    /// <summary>
    /// Copy on write: scrubbing ONE of two widgets that share an appearance stream must not edit
    /// the stream the other still shows. The other widget keeps the original object, term and
    /// all (its own scrub, when its page is redacted, gets the same rewritten copy).
    /// </summary>
    [Fact]
    public void SharedAppearance_ScrubbingOneWidget_LeavesTheOtherWidgetsStreamUntouched()
    {
        using var document = PdfDocument.Open(F.BuildShared());
        var page = document.GetPage(1);
        var alpha = Widget(document, "Alpha");
        var beta = Widget(document, "Beta");
        var original = AppearanceStream(document, beta)!;
        AppearanceStream(document, alpha).Should().BeSameAs(original, "the fixture shares one stream");

        InteractiveRedactionScrubber.ScrubTerm(
            page, [new PdfRectangle(100, 700, 300, 720)], [alpha], Array.Empty<PdfRectangle>(),
            F.Term, caseSensitive: false, wholeWord: false, new InteractiveRedactionScrubber.TermScrubState())
            .Should().BeTrue();

        AppearanceStream(document, beta).Should().BeSameAs(original);
        Encoding.Latin1.GetString(original.DecodedData).Should().Contain(F.Term, "the shared stream was not edited in place");
        var rewritten = AppearanceStream(document, alpha)!;
        rewritten.Should().NotBeSameAs(original);
        Encoding.Latin1.GetString(rewritten.DecodedData).Should().NotContain(F.Term).And.Contain(F.SharedSurvivor);
    }

    /// <summary>
    /// The down appearance (<c>/AP /D</c>) is in the file and drawn while the widget is pressed.
    /// It used to be left untouched while the report said the term was removed.
    /// </summary>
    [Fact]
    public void DownAppearance_HoldingTheTerm_IsRewrittenToo()
    {
        var input = F.BuildWithDownAppearance();
        SavedPdfLeakScanner.FindTerm(input, "Down " + F.Term).Should().NotBeEmpty("planted: /D draws the term");
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            report = document.RedactText(F.Term, Options);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty("neither /N nor /D keeps the term");
        report.Survived.Should().Be(0);
        using var reopened = PdfDocument.Open(saved);
        var widget = Widget(reopened, "Alpha");
        var ap = (PdfDictionary)reopened.Resolve(widget.GetOptional("AP")!)!;
        var down = (PdfStream)reopened.Resolve(ap.GetOptional("D")!)!;
        Encoding.Latin1.GetString(down.DecodedData).Should().Contain("Down").And.NotContain(F.Term,
            "the down appearance is rewritten, not dropped");
    }

    private static PdfDictionary Widget(PdfDocument document, string name)
        => document.GetPage(1).GetFormFields().Single(f => f.FullName == name).WidgetDictionaries.Single();

    private static PdfStream? AppearanceStream(PdfDocument document, PdfDictionary widget)
        => document.Resolve(widget.GetOptional("AP") ?? PdfNull.Instance) is PdfDictionary ap
            ? document.Resolve(ap.GetOptional("N") ?? PdfNull.Instance) as PdfStream
            : null;
}
