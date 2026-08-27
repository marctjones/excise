using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1190 — Type3 fonts. The glyph cell's ascent was built from the raw Tf size
/// (a 299 Tf Type3 glyph in glyph-space units → a 299-unit-tall cell) without the
/// /FontMatrix vertical scale, so the cell centre landed ABOVE the page. RedactText
/// removed the term but the #1101 visible-window match tally rejected the off-page
/// centre and reported 0 — "Redacted 0 occurrence(s)" while the text was gone.
/// Type3HeightScale (FontMatrix[3]*1000, the mirror of #1103's width) fixes it.
/// </summary>
public sealed class Type3RedactionCountTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void Type3Text_RedactionReportsTheRemoval_NotZero()
    {
        var path = Path.Combine(RepoRoot(), "test-pdfs", "poppler", "tests", "type3.pdf");
        Assert.SkipUnless(File.Exists(path), "type3.pdf absent [requires: corpus:poppler]");

        using (var geo = PdfDocument.Open(path))
        {
            // Root-cause guard: the Type3 glyph cell must sit WITHIN the page, not
            // hundreds of units above it (the off-page centre #1190 was about).
            var page = geo.GetPage(1);
            var crop = page.CropBox.Normalize();
            foreach (var l in page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)))
            {
                var g = l.GlyphRectangle.Normalize();
                ((g.Bottom + g.Top) / 2).Should().BeLessThanOrEqualTo(crop.Top + 1,
                    "a Type3 glyph's cell centre must be on the page, not above it (#1190)");
            }
        }

        using var doc = PdfDocument.Open(path);
        doc.RedactText("ababab", drawBlackRect: false).VerifiedRemovals
            .Should().BeGreaterThan(0, "Type3 redaction must REPORT the removal, not 0 (#1190)");
    }
}
