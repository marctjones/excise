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
/// #2043: a text field's pressed (<c>/AP /D</c>) and hovered (<c>/AP /R</c>) appearances drawn in
/// a font whose <c>/ToUnicode</c> excise reads as something else (every code U+FFFD, or the next
/// letter). The stream holds the term's codes, so the byte scanner sees it and a viewer painting
/// the codes shows it, but excise's reading does not. <c>RedactText</c> (the CLI's path) used to
/// leave both states in place and report the term removed and the run clean. Now a state that
/// cannot be rewritten free of the term is dropped and reported; the rewritten <c>/N</c> stays.
/// The oracle is the inflating byte scanner over the SAVED file plus a structural read of the
/// widget's <c>/AP</c>; no excise text reading is consulted for absence.
/// </summary>
public class WidgetStateAppearanceRedactionTests
{
    private static RedactionOptions Options => RedactionOptions.Default with { DrawBox = false };

    [Theory]
    [InlineData(nameof(F.UnreadableFont.ReplacementToUnicode), true, true)]
    [InlineData(nameof(F.UnreadableFont.ShiftedToUnicode), true, true)]
    [InlineData(nameof(F.UnreadableFont.ShiftedToUnicode), true, false)]
    [InlineData(nameof(F.UnreadableFont.ShiftedToUnicode), false, true)]
    public void RedactText_UnreadableStateAppearance_IsDroppedAndReported_NormalAppearanceKept(
        string font, bool down, bool rollover)
    {
        var input = F.BuildWithUnreadableStateAppearances(Enum.Parse<F.UnreadableFont>(font), down, rollover);
        SavedPdfLeakScanner.FindTerm(input, F.StateText).Should().NotBeEmpty("planted: a state appearance holds the term");

        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            report = document.RedactText(F.Term, Options);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty("no appearance state keeps the term's codes");
        var states = (down ? 1 : 0) + (rollover ? 1 : 0);
        report.Removals.Should().ContainSingle(r => r.Feature.Contains("pressed or hovered appearance"))
            .Which.Count.Should().Be(states, "each dropped state is reported, not counted clean in silence");

        using var reopened = PdfDocument.Open(saved);
        var ap = Ap(reopened);
        ap.Keys.Select(k => k.Value).Should().BeEquivalentTo(["N"], "only the states that could not be rewritten go");
        var normal = (PdfStream)reopened.Resolve(ap.GetOptional("N")!)!;
        Encoding.Latin1.GetString(normal.DecodedData).Should().Contain(F.SharedSurvivor).And.NotContain(F.Term,
            "the normal appearance is rewritten, not dropped");
    }

    /// <summary>A readable <c>/D</c> holding the term is still rewritten (#2041); only the
    /// unreadable <c>/R</c> beside it is dropped.</summary>
    [Fact]
    public void RedactText_ReadableDownRewritten_UnreadableRolloverDropped()
    {
        var input = F.BuildWithUnreadableStateAppearances(F.UnreadableFont.ShiftedToUnicode, readableDown: true);
        byte[] saved;
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            report = document.RedactText(F.Term, Options);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty();
        report.Removals.Should().ContainSingle(r => r.Feature.Contains("pressed or hovered appearance"))
            .Which.Count.Should().Be(1);
        using var reopened = PdfDocument.Open(saved);
        var ap = Ap(reopened);
        ap.Keys.Select(k => k.Value).Should().BeEquivalentTo(["N", "D"]);
        var downStream = (PdfStream)reopened.Resolve(ap.GetOptional("D")!)!;
        Encoding.Latin1.GetString(downStream.DecodedData).Should().Contain("Down").And.NotContain(F.Term);
    }

    /// <summary>Area redaction over the widget already drops its whole <c>/AP</c>, states included:
    /// pinned so the term path's fix is not mistaken for the only safe one.</summary>
    [Fact]
    public void RedactAreaWithReport_OverTheWidget_LeavesNoStateAppearance()
    {
        var input = F.BuildWithUnreadableStateAppearances(F.UnreadableFont.ShiftedToUnicode);
        byte[] saved;
        using (var document = PdfDocument.Open(input))
        {
            document.GetPage(1).RedactAreaWithReport(new PdfRectangle(F.AlphaRect[0], F.AlphaRect[1], F.AlphaRect[2], F.AlphaRect[3]), Options);
            saved = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        Widget(reopened).GetOptional("AP").Should().BeNull();
    }

    private static PdfDictionary Widget(PdfDocument document)
        => document.GetPage(1).GetFormFields().Single(f => f.FullName == "Alpha").WidgetDictionaries.Single();

    private static PdfDictionary Ap(PdfDocument document)
        => (PdfDictionary)document.Resolve(Widget(document).GetOptional("AP")!)!;
}
