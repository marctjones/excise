using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1566 — the safety-critical half of adding <see cref="PdfPage.UserUnit"/>:
/// a scaling factor that relabels what one POINT means in the real world must
/// not perturb glyph removal, which operates entirely in the page's own
/// (unscaled) user-space. See <see cref="PdfPage.UserUnit"/>'s remarks for why
/// this is expected to already hold — nothing in the coordinate/redaction path
/// reads UserUnit — this test is the proof, not a fix.
/// </summary>
public class UserUnitRedactionSafetyTests
{
    private const string Secret = "SECRETUSERUNITPROBE";

    [Theory]
    [InlineData(1.0)]
    [InlineData(10.0)]
    [InlineData(75000.0)] // Acrobat's own documented ceiling
    public void RedactText_OnAUserUnitPage_RemovesExactlyTheTargetedGlyphs(double userUnit)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        page.UserUnit = userUnit;
        using (var g = page.GetGraphics())
        {
            g.DrawString($"Before {Secret} After", PdfFont.Helvetica(12), PdfBrush.Black, 100, 700);
            g.Flush();
        }

        var report = doc.RedactText(Secret);
        var saved = doc.SaveToBytes();

        report.Pages.Sum(p => p.MatchesLocated).Should().Be(1,
            "exactly one occurrence of the target string was planted");
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            $"the secret must not survive redaction on a UserUnit={userUnit} page — an independent, " +
            "decompressing scanner, not excise's own reader");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).UserUnit.Should().Be(userUnit,
            "the redaction save must not disturb the page's own /UserUnit");
    }

    [Fact]
    public void RedactText_GlyphRectsAreIdentical_WithAndWithoutUserUnit()
    {
        // The stronger form of the property above: not just "redaction still
        // works", but the exact same glyphs at the exact same coordinates are
        // targeted and removed either way — proof that UserUnit is invisible
        // to this whole path, not merely that it happens not to break it.
        static PdfRectangle[] RemovedGlyphRects(double? userUnit)
        {
            using var doc = PdfDocument.CreateNew();
            var page = doc.Pages.AddBlank(612, 792);
            if (userUnit is { } u) page.UserUnit = u;
            using (var g = page.GetGraphics())
            {
                g.DrawString($"Before {Secret} After", PdfFont.Helvetica(12), PdfBrush.Black, 100, 700);
                g.Flush();
            }
            doc.RedactText(Secret);
            return doc.GetPage(1).GetAnnotations()
                .Where(a => a.Subtype == PdfAnnotationSubtype.Redact
                            || a.RawDictionary.GetNameOrNull("Subtype") == "Square")
                .Select(a => a.Rect.Normalize())
                .OrderBy(r => r.Left)
                .ToArray();
        }

        var without = RemovedGlyphRects(null);
        var with = RemovedGlyphRects(10.0);

        with.Should().BeEquivalentTo(without,
            "the covering box(es) the redaction draws must land at IDENTICAL page-space coordinates " +
            "with or without UserUnit set — UserUnit must never reach coordinate math");
    }
}
