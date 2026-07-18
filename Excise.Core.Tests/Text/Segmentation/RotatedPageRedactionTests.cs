using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Redaction on rotated pages (issue #616).
///
/// A redaction rectangle is captured in *visual* space — where the user sees
/// the text on a rotated page — and must be mapped into *content-stream* space
/// before glyphs can be matched and removed. If that mapping ignores /Rotate,
/// the wrong glyphs are removed and the black box lands somewhere else: the
/// sensitive text survives while the UI confirms a redaction that never
/// happened. That is the worst failure this engine can have, because it fails
/// silently and reassuringly.
///
/// These tests drive the exact path the GUI uses
/// (PdfPageRect.ToContentPoints -> PdfPage.ToContentStreamCoordinates ->
/// PdfPage.RedactArea) at every legal rotation, and assert both directions:
/// the targeted word is gone, and the neighbouring word survives. The second
/// half matters as much as the first — a mapping that redacts *everything*
/// would pass a removal-only assertion.
/// </summary>
public class RotatedPageRedactionTests
{
    private const string Secret = "SECRET";
    private const string Keep = "KEEPME";

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RedactArea_FromVisualRect_RemovesTargetedWordAtEveryRotation(int rotation)
    {
        var pdf = CreateTwoWordPdf();
        var page = pdf.GetPage(1);
        page.Rotation = rotation;

        page.Text.Should().Contain(Secret).And.Contain(Keep, "fixture sanity");

        // Where the user actually drags: the on-screen (visual) box over SECRET.
        var visualRect = VisualRectOf(page, Secret);

        // The GUI path: visual -> content, then redact.
        var contentRect = PdfCoordinateMapper
            .ToContentPoints(page, visualRect)
            .ToPdfRectangle()
            .Normalize();

        page.RedactArea(contentRect, GlyphRemovalStrategy.AnyOverlap);

        var after = page.Text;
        after.Should().NotContain(Secret,
            $"the glyphs under the user's box must be removed at /Rotate {rotation} — " +
            "if the rect is not mapped through the page rotation, the wrong region is " +
            "redacted and the secret survives behind a black box drawn elsewhere");
        after.Should().Contain(Keep,
            $"only the targeted word may be removed at /Rotate {rotation}; redacting the " +
            "whole page would also satisfy a removal-only assertion");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void VisualToContent_RoundTripsToTheOriginalContentRect(int rotation)
    {
        var pdf = CreateTwoWordPdf();
        var page = pdf.GetPage(1);
        page.Rotation = rotation;

        var original = ContentRectOf(page, Secret);

        var visual = PdfCoordinateMapper.ToVisualPoints(
            page, PdfPageRect.FromContentPoints(page.PageNumber, original));

        var roundTripped = PdfCoordinateMapper
            .ToContentPoints(page, visual)
            .ToPdfRectangle()
            .Normalize();

        // The invariant, not the numbers: content -> visual -> content is identity.
        // This is what makes the redaction mapping trustworthy at any rotation.
        roundTripped.Left.Should().BeApproximately(original.Left, 0.001);
        roundTripped.Bottom.Should().BeApproximately(original.Bottom, 0.001);
        roundTripped.Right.Should().BeApproximately(original.Right, 0.001);
        roundTripped.Top.Should().BeApproximately(original.Top, 0.001);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void VisualDimensions_SwapOnQuarterTurns(int rotation)
    {
        var pdf = CreateTwoWordPdf();
        var page = pdf.GetPage(1);
        page.Rotation = rotation;

        page.VisualWidth.Should().BeApproximately(page.Height, 0.001);
        page.VisualHeight.Should().BeApproximately(page.Width, 0.001);
    }

    [Fact]
    public void Rotation_InheritedFromPagesNode_IsHonouredByRedaction()
    {
        // /Rotate is an inheritable attribute: it may live on an ancestor Pages
        // node rather than the page itself. PdfPage.Rotation reads it via
        // GetInheritedInt, so the redaction mapping must see it too.
        var pdf = CreateTwoWordPdf();
        var page = pdf.GetPage(1);

        var parent = page.Dictionary.GetOptional("Parent");
        parent.Should().NotBeNull("fixture page must have a Pages parent to inherit from");

        var pagesDict = pdf.Resolve(parent!) as PdfDictionary;
        pagesDict.Should().NotBeNull();
        pagesDict!.SetInt("Rotate", 90);

        page.Rotation.Should().Be(90, "rotation must be inherited from the Pages node");

        var visualRect = VisualRectOf(page, Secret);
        var contentRect = PdfCoordinateMapper.ToContentPoints(page, visualRect).ToPdfRectangle().Normalize();
        page.RedactArea(contentRect, GlyphRemovalStrategy.AnyOverlap);

        page.Text.Should().NotContain(Secret, "inherited /Rotate must be applied to the redaction mapping");
        page.Text.Should().Contain(Keep);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(-270, 90)]
    public void Rotation_NormalizesOutOfRangeValues(int raw, int expected)
    {
        var pdf = CreateTwoWordPdf();
        var page = pdf.GetPage(1);

        // Written through the raw dictionary: the Rotation setter rejects
        // non-canonical values, but a real-world PDF can carry any integer.
        page.Dictionary.SetInt("Rotate", raw);

        page.Rotation.Should().Be(expected,
            "a PDF in the wild may carry a negative or >360 /Rotate; the mapping must " +
            "fold it into {0,90,180,270} rather than fall through to the unrotated case");
    }

    /// <summary>The on-screen box the user would drag over <paramref name="word"/>.</summary>
    private static PdfPageRect VisualRectOf(PdfPage page, string word) =>
        PdfCoordinateMapper.ToVisualPoints(
            page,
            PdfPageRect.FromContentPoints(page.PageNumber, ContentRectOf(page, word)));

    /// <summary>Content-space bounding box of <paramref name="word"/>, from the page's own letters.</summary>
    private static PdfRectangle ContentRectOf(PdfPage page, string word)
    {
        var letters = page.Letters
            .Where(l => word.Contains(l.Value, System.StringComparison.Ordinal))
            .ToList();

        letters.Should().NotBeEmpty($"fixture must contain the letters of '{word}'");

        // Restrict to the run that actually spells the word: both fixture words
        // sit on their own line, so grouping by Y is sufficient and keeps the
        // helper honest about which glyphs it is targeting.
        var targetY = page.Letters.First(l => l.Value == word[0].ToString()).GlyphRectangle.Bottom;
        var run = page.Letters
            .Where(l => System.Math.Abs(l.GlyphRectangle.Bottom - targetY) < 2.0)
            .ToList();

        double left = run.Min(l => l.GlyphRectangle.Left);
        double right = run.Max(l => l.GlyphRectangle.Right);
        double bottom = run.Min(l => l.GlyphRectangle.Bottom);
        double top = run.Max(l => l.GlyphRectangle.Top);

        // Pad slightly, as a real drag would.
        return new PdfRectangle(left - 1, bottom - 1, right + 1, top + 1).Normalize();
    }

    private static PdfDocument CreateTwoWordPdf()
    {
        // Two words, well separated, on their own lines.
        var content =
            $"BT /F1 24 Tf 100 700 Td ({Secret}) Tj ET " +
            $"BT /F1 24 Tf 100 500 Td ({Keep}) Tj ET";
        return PdfDocument.Open(BuildPdfWithContent(content));
    }

    private static byte[] BuildPdfWithContent(string contentStream)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(body);
        }

        sb.Append("%PDF-1.7\n");

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {contentStream.Length} >>\nstream\n{contentStream}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xrefPos = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
