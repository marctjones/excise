using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1038 — redacting one word out of a form field must not delete the field.
///
/// <para><b>What this cost.</b> <c>test-pdfs/pdfjs/issue18036.pdf</c> is a
/// certificate of insurance whose body text is a read-only multiline
/// <c>/Tx</c> field. Redacting the single word <c>certificate</c> left 23 of
/// 568 characters — <b>545 destroyed</b> — and reported success:</para>
///
/// <code>
/// $ excise text issue18036.pdf     # 568 chars
/// $ excise redact ... certificate  # "Redacted 4 occurrence(s)"
/// $ excise text after.pdf          # 23 chars: "SAV CERT -   4/12/2024"
/// </code>
///
/// <para><b>Why nobody found it.</b> The damage was blamed on the
/// whole-operator removal fallback for months. That fallback fires
/// <b>zero</b> times across all 235 documents in the collateral corpus
/// (<c>DestructivePathInstrumentationTests</c>) — it was never involved.
/// <c>InteractiveRedactionScrubber.ScrubFormFields</c> matched on GEOMETRY
/// alone ("does a widget rect overlap the redaction box") and then removed
/// <c>/V</c>, <c>/DV</c>, <c>/Opt</c> and <c>/AP</c> outright. A redaction box
/// around one word overlaps a 567x116pt widget, so the field's entire value
/// went with it.</para>
///
/// <para>The fixture is synthetic so this runs without the gitignored corpus,
/// and the term is asserted absent from the SAVED BYTES rather than from
/// excise's own extractor — removing less must not be allowed to leak more.</para>
/// </summary>
public class FormFieldValueRedactionTests
{
    private const string Secret = "certificate";

    /// <summary>The value either side of the term, which must survive.</summary>
    private const string Head = "This ";
    private const string Tail =
        " or verification of insurance is not an insurance policy and does not " +
        "amend, extend or alter the coverage afforded by the policy listed herein.";

    private static string FieldValue => Head + Secret + Tail;

    /// <summary>
    /// One page, one merged widget/field: a read-only multiline <c>/Tx</c>
    /// whose <c>/Rect</c> spans most of the page — the shape that makes a
    /// geometry-only scrub catastrophic.
    /// </summary>
    private static byte[] BuildPdfWithLongTextFieldValue()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        var pos = new long[6];

        pos[1] = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>");
        sb.AppendLine("endobj");

        pos[2] = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        pos[3] = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                      "/Contents 4 0 R /Annots [5 0 R] >>");
        sb.AppendLine("endobj");

        pos[4] = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // /Ff 4096 = bit 13, Multiline (§12.7.4.3 Table 228).
        pos[5] = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /Ff 4096 " +
                      "/T (body) /P 3 0 R /Rect [23 456 590 572] " +
                      $"/V ({FieldValue}) >>");
        sb.AppendLine("endobj");

        var xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        for (var i = 1; i <= 5; i++) sb.AppendLine($"{pos[i]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] RedactAndSave(string term, out RedactionReport report)
    {
        using var doc = PdfDocument.Open(BuildPdfWithLongTextFieldValue());
        report = doc.RedactText(term);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Guard_TheFieldValueIsReachableBeforeRedaction()
    {
        // Without this the collateral assertion below could pass on a fixture
        // whose text excise never saw at all.
        using var doc = PdfDocument.Open(BuildPdfWithLongTextFieldValue());
        doc.GetPage(1).Text.Should().Contain(Secret,
            "the fixture must put the term where excise can find it, or this class proves nothing");
    }

    [Fact]
    public void TheTermIsGoneFromEveryCarrierInTheSavedFile()
    {
        var saved = RedactAndSave(Secret, out _);

        // Carrier-agnostic and decompressing. The whole point of keeping the
        // value is that it must not keep the term with it.
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "surgical removal must be as complete as deleting the field was");
    }

    [Fact]
    public void TheRestOfTheFieldValueSurvives()
    {
        var saved = RedactAndSave(Secret, out _);

        using var doc = PdfDocument.Open(saved);
        var text = doc.GetPage(1).Text;

        // THE PIN. Before the fix this was empty: /V, /DV and /AP were all
        // removed because the widget rect overlapped the redaction box.
        text.Should().Contain("verification of insurance",
            "removing one word must not take the field's other 500 characters with it");
        text.Should().Contain("coverage afforded",
            "text at the far end of the value is just as much collateral as text beside the term");
    }

    [Fact]
    public void TheCollateralIsExactlyTheTermAndNothingElse()
    {
        var before = FieldValue.Count(char.IsLetterOrDigit);
        var saved = RedactAndSave(Secret, out var report);

        using var doc = PdfDocument.Open(saved);
        var after = doc.GetPage(1).Text.Count(char.IsLetterOrDigit);

        // A "survives" assertion can pass while a chunk in the middle is gone.
        // This is the exact-cost form: one occurrence removed, one occurrence
        // worth of characters lost.
        (before - after).Should().Be(Secret.Length,
            "the only characters lost may be the term's own");
        report.VerifiedRemovals.Should().Be(1);
    }

    [Fact]
    public void ARedactionThatMatchesNothingLeavesTheValueAlone()
    {
        var saved = RedactAndSave("Farrar", out _);

        using var doc = PdfDocument.Open(saved);
        // Negative control. A geometry-only scrub cannot express "no match
        // here"; it deletes whatever its rectangle touches. Nothing overlaps
        // and nothing matches, so nothing may change.
        doc.GetPage(1).Text.Should().Contain(Secret,
            "a term that is not in the document must not cost the document anything");
    }
}
