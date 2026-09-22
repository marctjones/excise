using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// The NAMED form of a marked-content property list (#1599).
///
/// <para>§14.6.2 lets <c>BDC</c> carry its property dictionary two ways: inline
/// (<c>/Span &lt;&lt; /ActualText (…) &gt;&gt; BDC</c>) or by NAME
/// (<c>/Span /P1 BDC</c>), where <c>/P1</c> resolves through the page's
/// <c>/Resources /Properties</c>. The scrubber handled only the inline form, so a
/// producer that emits the named one — which is the more common output of a real
/// tagging pipeline, because the dictionary can then be shared — kept its
/// <c>/ActualText</c> through a redaction that reported success.</para>
///
/// <para>The named form brings a hazard the inline one does not have: the
/// dictionary is SHARED. Scrubbing it because one span was redacted would silently
/// damage every other span pointing at it, so it is only safe when no surviving
/// span still references that name. Both directions are pinned here — a test that
/// only checked the removal would pass just as well on a scrubber that wrecked
/// every shared dictionary it touched.</para>
///
/// <para>Assertions read the SAVED BYTES through <see cref="SavedPdfLeakScanner"/>,
/// which sees inside Flate-compressed object streams. A content-stream text
/// extraction cannot see this carrier at all and passes on a leaking file.</para>
/// </summary>
public class NamedMarkedContentCarrierTests
{
    private const string Secret = "SECRETNAME";
    private const string First = "TARGETONE";
    private const string Second = "TARGETTWO";

    [Fact]
    public void RedactArea_ScrubsActualTextReachedThroughAPropertyNAME()
    {
        using var pdf = PdfDocument.Open(BuildPdf(twoSpans: false));
        var page = pdf.GetPage(1);

        RedactWord(page, First);

        SavedPdfLeakScanner.FindTerm(Save(pdf), Secret).Should().BeEmpty(
            "/ActualText reached through /Resources /Properties is the same carrier as the "
            + "inline form — a redaction that leaves it behind has leaked the name it was "
            + "asked to remove");
    }

    [Fact]
    public void RedactArea_LeavesASharedPropertyDictionaryAloneWhileASpanStillNeedsIt()
    {
        using var pdf = PdfDocument.Open(BuildPdf(twoSpans: true));
        var page = pdf.GetPage(1);

        RedactWord(page, First);

        var saved = Save(pdf);
        using (var reopened = PdfDocument.Open(saved))
        {
            reopened.GetPage(1).Text.Should().NotContain(First,
                "the redaction itself must still have happened");
        }
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().NotBeEmpty(
            "a second span that was NOT redacted still points at this dictionary — scrubbing "
            + "it would corrupt the text that span represents, which is destruction the "
            + "caller never asked for");
    }

    /// <summary>
    /// #1599 / CLAUDE.md rule 6 — a carrier the engine refuses to scrub because
    /// scrubbing it would corrupt a surviving span must be REPORTED, not
    /// silently left behind with a report that still claims success. This is
    /// the document-level <c>RedactText</c> entry point the CLI and GUI
    /// actually call, not the lower-level area primitive the sibling test
    /// above uses.
    /// </summary>
    [Fact]
    public void RedactText_ReportsSharedPropertyListItCouldNotScrub()
    {
        using var pdf = PdfDocument.Open(BuildPdf(twoSpans: true));

        var report = pdf.RedactText(First);

        report.Carriers.Should().Contain(
            c => c.RefusedReason != null && c.RefusedReason.Contains("#1599"),
            "a shared named property list that could not be scrubbed must show up as a refused " +
            "carrier — silently leaving /ActualText behind while reporting success is exactly " +
            "the failure CLAUDE.md rule 6 forbids");
        report.IsCleanSuccess.Should().BeFalse(
            "a carrier still holds the redacted term (SECRETNAME, via the second span's " +
            "/ActualText), so this run must not be reported clean");

        SavedPdfLeakScanner.FindTerm(Save(pdf), Secret).Should().NotBeEmpty(
            "sanity: the value really is still there — the report above must match reality");
    }

    private static void RedactWord(PdfPage page, string word)
    {
        var letters = page.Letters.Where(l => word.Contains(l.Value)).ToList();
        letters.Should().NotBeEmpty($"the fixture must actually draw '{word}'");

        var targetY = letters[0].GlyphRectangle.Bottom;
        var run = page.Letters
            .Where(l => Math.Abs(l.GlyphRectangle.Bottom - targetY) < 2.0)
            .ToList();

        var area = new PdfRectangle(
            run.Min(l => l.GlyphRectangle.Left) - 1,
            run.Min(l => l.GlyphRectangle.Bottom) - 1,
            run.Max(l => l.GlyphRectangle.Right) + 1,
            run.Max(l => l.GlyphRectangle.Top) + 1).Normalize();

        page.RedactArea(area, GlyphRemovalStrategy.AnyOverlap);
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using var ms = new MemoryStream();
        pdf.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// One page whose spans name their property list instead of inlining it.
    /// With <paramref name="twoSpans"/> the SAME name is used twice, only one of
    /// which is redacted.
    /// </summary>
    private static byte[] BuildPdf(bool twoSpans)
    {
        var content = $"/Span /P1 BDC BT /F1 24 Tf 100 700 Td ({First}) Tj ET EMC\n";
        if (twoSpans)
            content += $"/Span /P1 BDC BT /F1 24 Tf 100 500 Td ({Second}) Tj ET EMC\n";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> /Properties << /P1 6 0 R >> >> "
                + "/Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /ActualText ({Secret}) >>",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
