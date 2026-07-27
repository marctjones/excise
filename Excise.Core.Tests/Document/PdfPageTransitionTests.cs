using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Parse + round-trip tests for page transitions (/Trans) and page display
/// duration (/Dur) — ISO 32000-2:2020 §12.4.4 (issue #331). Presentation-mode
/// playback is out of scope; these tests only cover parse/store/round-trip.
/// </summary>
public class PdfPageTransitionTests
{
    [Fact]
    public void Transition_NoTransDict_ReturnsNull()
    {
        var pdf = MakePdfWithPageExtras(null, null);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Transition.Should().BeNull();
    }

    [Theory]
    [InlineData("Wipe", PdfTransitionStyle.Wipe)]
    [InlineData("Fade", PdfTransitionStyle.Fade)]
    [InlineData("Dissolve", PdfTransitionStyle.Dissolve)]
    [InlineData("Box", PdfTransitionStyle.Box)]
    [InlineData("Blinds", PdfTransitionStyle.Blinds)]
    [InlineData("Glitter", PdfTransitionStyle.Glitter)]
    [InlineData("Split", PdfTransitionStyle.Split)]
    [InlineData("Cover", PdfTransitionStyle.Cover)]
    [InlineData("Uncover", PdfTransitionStyle.Uncover)]
    [InlineData("Push", PdfTransitionStyle.Push)]
    [InlineData("Fly", PdfTransitionStyle.Fly)]
    [InlineData("R", PdfTransitionStyle.Replace)]
    public void Transition_EachStyle_ParsesCorrectly(string pdfStyleName, PdfTransitionStyle expected)
    {
        var trans = $"/Trans << /S /{pdfStyleName} /D 2.5 >>";
        var pdf = MakePdfWithPageExtras(trans, null);
        using var doc = PdfDocument.Open(pdf);

        var transition = doc.GetPage(1).Transition;
        transition.Should().NotBeNull();
        transition!.Style.Should().Be(expected);
        transition.Duration.Should().Be(2.5);
    }

    [Fact]
    public void Transition_BlindsWithDimensionAndMotion_ParsesFields()
    {
        var pdf = MakePdfWithPageExtras(
            "/Trans << /S /Blinds /D 1.5 /Dm /V /Di 90 >>", null);
        using var doc = PdfDocument.Open(pdf);

        var transition = doc.GetPage(1).Transition!;
        transition.Style.Should().Be(PdfTransitionStyle.Blinds);
        transition.Dimension.Should().Be("V");
        transition.Direction.Should().Be(90);
    }

    [Fact]
    public void Transition_FlyWithScaleAndRectangle_ParsesFields()
    {
        var pdf = MakePdfWithPageExtras(
            "/Trans << /S /Fly /D 1 /M /O /SS 2.0 /B true >>", null);
        using var doc = PdfDocument.Open(pdf);

        var transition = doc.GetPage(1).Transition!;
        transition.Style.Should().Be(PdfTransitionStyle.Fly);
        transition.Motion.Should().Be("O");
        transition.FlyScale.Should().Be(2.0);
        transition.FlyOpaqueRectangle.Should().BeTrue();
    }

    [Fact]
    public void Transition_FlyWithNoneDirection_IsDistinctFromNumeric315()
    {
        // /Di /None (Fly moving directly inward/outward) must not be confused
        // with a literal /Di 315 (also spec-legal) — see PdfPageTransition.Direction.
        var none = MakePdfWithPageExtras("/Trans << /S /Fly /Di /None >>", null);
        using var noneDoc = PdfDocument.Open(none);
        noneDoc.GetPage(1).Transition!.Direction.Should().Be(-1);

        var numeric = MakePdfWithPageExtras("/Trans << /S /Fly /Di 315 >>", null);
        using var numericDoc = PdfDocument.Open(numeric);
        numericDoc.GetPage(1).Transition!.Direction.Should().Be(315);
    }

    [Fact]
    public void Transition_NoStyleEntry_DefaultsToReplace()
    {
        var pdf = MakePdfWithPageExtras("/Trans << /D 1 >>", null);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Transition!.Style.Should().Be(PdfTransitionStyle.Replace);
    }

    [Fact]
    public void Duration_NoDurEntry_ReturnsNull()
    {
        var pdf = MakePdfWithPageExtras(null, null);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Duration.Should().BeNull();
    }

    [Fact]
    public void Duration_DurEntryPresent_ReturnsValue()
    {
        var pdf = MakePdfWithPageExtras(null, "5");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Duration.Should().Be(5.0);
    }

    // ─── Round-trip: parse → save → reopen → still parses the same ─────────

    [Fact]
    public void Transition_RoundTrip_SurvivesSaveAndReopen()
    {
        var pdf = MakePdfWithPageExtras(
            "/Trans << /S /Dissolve /D 3.0 >>", "7.5");
        using var doc = PdfDocument.Open(pdf);

        // Sanity: parses before save.
        doc.GetPage(1).Transition!.Style.Should().Be(PdfTransitionStyle.Dissolve);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        var transition = reopened.GetPage(1).Transition;
        transition.Should().NotBeNull();
        transition!.Style.Should().Be(PdfTransitionStyle.Dissolve);
        transition.Duration.Should().Be(3.0);
        reopened.GetPage(1).Duration.Should().Be(7.5);
    }

    [Fact]
    public void Transition_RoundTrip_DoesNotAppearOnPlainDocument()
    {
        // Additive guarantee: a document with no /Trans or /Dur must not gain
        // one after a save/reopen cycle.
        var pdf = MakePdfWithPageExtras(null, null);
        using var doc = PdfDocument.Open(pdf);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        reopened.GetPage(1).Transition.Should().BeNull();
        reopened.GetPage(1).Duration.Should().BeNull();
    }

    // ─── Helper: PDF builder ───────────────────────────────────────────────

    /// <summary>
    /// Build a minimal 1-page PDF with optional /Trans and /Dur entries on the page.
    /// </summary>
    private static byte[] MakePdfWithPageExtras(string? transEntry, string? durValue)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        var extras = new StringBuilder();
        if (transEntry != null) extras.Append(' ').Append(transEntry);
        if (durValue != null) extras.Append(" /Dur ").Append(durValue);
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]{extras} >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
