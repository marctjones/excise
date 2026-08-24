using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1116 — measure the width gap a redaction leaves, and compare it to what was
/// removed. This is subtraction, not cryptanalysis: no dictionary, no entropy,
/// no recovery of any text. Just — does the hole excise leaves equal the width
/// of the glyphs it took out?
///
/// <para>If it does, the removed text's rendered width survives in the layout,
/// which is exactly what the PETS 2023 attack exploits and what #1045 ("preserve
/// the layout width, decided: yes") chose to leave open. This gate puts a price
/// on that decision: measured on excise's own redaction, the gap matches the
/// removed width to within a point — so #1045 can be re-decided on evidence, not
/// re-argued on preference.</para>
///
/// <para>All positions come from mutool <c>stext</c>, never excise's own
/// extractor — a tool must not referee the property it exists to guarantee. A
/// contiguous <c>AAA&lt;term&gt;ZZZ</c> fixture makes every number a difference
/// of glyph origins, so even the one anchor advance is measured, not looked up.</para>
/// </summary>
public class WidthGapLeakMeasurementTests
{
    private readonly ITestOutputHelper _out;
    public WidthGapLeakMeasurementTests(ITestOutputHelper o) => _out = o;

    private const string Before = "AAA";   // three identical anchors → advance is measurable
    private const string Term = "SECRETWORD";
    private const string After = "ZZZ";

    /// <summary>Contiguous "AAASECRETWORDZZZ" on one line, Helvetica 18.</summary>
    private static byte[] BuildFixture()
    {
        var text = Before + Term + After;
        var content = Encoding.Latin1.GetBytes($"BT /F1 18 Tf 72 700 Td ({text}) Tj ET\n");
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          + "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); ms.Write(content); W("\nendstream\nendobj\n");
        W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void ExciseRedaction_LeavesAGapThatMatchesTheRemovedWidth()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var beforePath = Path.Combine(Path.GetTempPath(), $"gap-before-{System.Guid.NewGuid():N}.pdf");
        var afterPath = Path.Combine(Path.GetTempPath(), $"gap-after-{System.Guid.NewGuid():N}.pdf");
        try
        {
            var pdf = BuildFixture();
            File.WriteAllBytes(beforePath, pdf);

            // Redact the middle word with excise's real engine, box suppressed so
            // the black rectangle does not perturb the surviving glyph positions.
            using (var doc = PdfDocument.Open(pdf))
            {
                var report = doc.RedactText(Term, drawBlackRect: false);
                report.VerifiedRemovals.Should().BeGreaterThan(0, "the term must actually be removed");
                using var fs = File.Create(afterPath);
                doc.Save(fs);
            }

            var before = MutoolGlyphPositions.ExtractPage(beforePath, 1);
            var after = MutoolGlyphPositions.ExtractPage(afterPath, 1);
            before.Should().NotBeNull();
            after.Should().NotBeNull();

            var b = before!.OrderBy(g => g.X).ToList();
            var a = after!.OrderBy(g => g.X).ToList();

            // BEFORE: A A A [10 term glyphs] Z Z Z. removed_width is the origin
            // span of the term = origin of the first char AFTER the term minus
            // origin of the first term char (glyphs are contiguous, so origin
            // spacing IS advance). The anchor advance is two A origins apart.
            b.Should().HaveCount(Before.Length + Term.Length + After.Length,
                "mutool must see every glyph before redaction");
            var advanceAnchor = b[1].X - b[0].X;                          // width of one 'A'
            var firstTerm = Before.Length;                               // index of the term's first glyph
            var firstAfter = Before.Length + Term.Length;                // index of the first 'Z'
            var removedWidth = b[firstAfter].X - b[firstTerm].X;         // Σ term advances

            // AFTER: A A A Z Z Z. The origin gap between the last surviving 'A'
            // and the first surviving 'Z', minus the 'A' advance, is the empty
            // span the removal left — edge of A to origin of Z.
            a.Should().HaveCount(Before.Length + After.Length,
                "only the anchors survive; the term's glyphs are gone");
            var lastBefore = Before.Length - 1;                          // last 'A'
            var gapOriginToOrigin = a[Before.Length].X - a[lastBefore].X; // first 'Z' − last 'A'
            var leakedWidth = gapOriginToOrigin - advanceAnchor;

            var deltaPt = System.Math.Abs(leakedWidth - removedWidth);
            var ratio = removedWidth > 0 ? leakedWidth / removedWidth : 0;

            _out.WriteLine($"removed_width = {removedWidth:F2} pt   (width of \"{Term}\")");
            _out.WriteLine($"gap_width     = {leakedWidth:F2} pt   (empty span left in the layout)");
            _out.WriteLine($"delta         = {deltaPt:F2} pt   ratio = {ratio:P0}");
            _out.WriteLine(deltaPt <= 1.0
                ? "VERDICT: the removed width is PRESERVED in the gap — the width channel leaks (#1045)."
                : "VERDICT: the gap does NOT match the removed width — the width channel is closed.");

            // The measurement's own correctness: on excise's width-preserving
            // redaction the gap equals the removed width to within a point. This
            // is the price #1045 pays — asserted so a future reflow-on-redact
            // change flips it and forces the decision to be revisited.
            leakedWidth.Should().BeApproximately(removedWidth, 1.0,
                "excise leaves the removed text's width as an empty gap (#1045), so the " +
                "gap and the removed width are the same number — that equality IS the leak, " +
                "and this measurement is what gives #1045 a price instead of a preference");
        }
        finally
        {
            File.Delete(beforePath);
            File.Delete(afterPath);
        }
    }

    [Fact]
    public void WidthClosingMode_DestroysTheResidueChannel()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var beforePath = Path.Combine(Path.GetTempPath(), $"wc-before-{System.Guid.NewGuid():N}.pdf");
        var afterPath = Path.Combine(Path.GetTempPath(), $"wc-after-{System.Guid.NewGuid():N}.pdf");
        try
        {
            var pdf = BuildFixture();
            File.WriteAllBytes(beforePath, pdf);

            // The #1145 option: close the width channel. Box suppressed so the
            // measurement sees the glyph gap, not the box (#1140).
            using (var doc = PdfDocument.Open(pdf))
            {
                doc.RedactText(Term, drawBlackRect: false, closeWidth: true);
                using var fs = File.Create(afterPath);
                doc.Save(fs);
            }

            var b = MutoolGlyphPositions.ExtractPage(beforePath, 1)!.OrderBy(g => g.X).ToList();
            var a = MutoolGlyphPositions.ExtractPage(afterPath, 1)!.OrderBy(g => g.X).ToList();

            var advanceAnchor = b[1].X - b[0].X;
            var removedWidth = b[Before.Length + Term.Length].X - b[Before.Length].X;

            // After width-closing the surviving "ZZZ" is pulled left, so the gap
            // between the last "A" and the first "Z" is ~one space, NOT the
            // removed word's width.
            var gapOriginToOrigin = a[Before.Length].X - a[Before.Length - 1].X;
            var leakedWidth = gapOriginToOrigin - advanceAnchor;

            _out.WriteLine($"removed_width = {removedWidth:F2} pt");
            _out.WriteLine($"gap after width-closing = {leakedWidth:F2} pt");

            leakedWidth.Should().BeLessThan(removedWidth * 0.5,
                "width-closing must collapse the gap so it no longer reveals the removed " +
                $"width ({removedWidth:F1}pt) — the residue channel #1116 measures is destroyed");
            // Surviving text must remain, just repositioned — not a collateral loss.
            (MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "").Should().Contain("ZZZ",
                "width-closing repositions surviving text; it must not destroy it");
        }
        finally
        {
            File.Delete(beforePath);
            File.Delete(afterPath);
        }
    }
}
