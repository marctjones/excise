using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1982: every row of <c>tests/corpora/rotation-fixtures.tsv</c> is checked against
/// tools that are not excise — qpdf for structure, Poppler <c>pdfinfo</c> for the
/// effective /Rotate and boxes, MuPDF stext and Poppler <c>pdftotext -bbox</c> for
/// where the target text is displayed, and a MuPDF render for ink inside that region.
/// The interaction tests (#1983-#1986) take their expected geometry from the same
/// readers, so a wrong row here would mislead all of them.
/// </summary>
public class RotationFixtureManifestTests
{
    private readonly ITestOutputHelper _out;

    public RotationFixtureManifestTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> FixtureIds() =>
        RotationFixtures.All.Select(f => new object[] { f.Id });

    [Fact]
    public void Manifest_CoversEveryRotation_InheritanceAndCroppedOrigin()
    {
        var all = RotationFixtures.All;
        all.Select(f => f.EffectiveRotation).Distinct().Should().BeEquivalentTo(new[] { 0, 90, 180, 270 });
        all.Should().Contain(f => f.RotateAt == RotateLocation.Parent, "inherited rotation needs a probe");
        all.Should().Contain(f => f.RotateAt == RotateLocation.Absent, "the spec default 0 needs a probe that omits /Rotate");
        all.Should().Contain(f => f.CropBox[0] != 0 && f.CropBox[1] != 0 && f.EffectiveRotation == 90,
            "a nonzero CropBox origin must be combined with a quarter turn");
        all.Where(f => !f.IsSynthetic).Should().OnlyContain(f =>
            f.Sha256 != null && f.Sha256.Length == 64 && f.Source.StartsWith("https://", StringComparison.Ordinal),
            "corpus rows carry a full SHA-256 and a provenance URL");
        RotationScenarioTable.All.Select(s => s.FixtureId).Should().OnlyContain(id => all.Any(f => f.Id == id));
        RotationScenarioTable.All.Select(s => s.FinalRotation).Distinct().Should().BeEquivalentTo(new[] { 0, 90, 180, 270 });
        RotationScenarioTable.All.Where(s => s.View == RotationView.Continuous)
            .Select(s => s.FinalRotation).Distinct().Count().Should().BeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Fixture_GeometryAgreesWithQpdfPopplerAndMupdf(string id)
    {
        Assert.SkipWhen(!QpdfReferenceTool.IsAvailable, "qpdf is not installed.");
        Assert.SkipWhen(!PopplerGeometry.IsAvailable, "pdfinfo/pdftotext (Poppler) are not installed.");
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable, "mutool is not installed.");

        var fixture = RotationFixtures.Get(id);
        var bytes = RotationFixtures.TryLoad(fixture, out var absence);
        Assert.SkipWhen(bytes == null, absence);

        var path = Path.Combine(Path.GetTempPath(), $"excise-rotfix-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes!);
        try
        {
            // Structure: qpdf parses it and finds the page.
            var check = QpdfReferenceTool.Check(path);
            check.Should().NotBeNull();
            check!.Value.Success.Should().BeTrue($"qpdf --check must accept {id}:\n{check.Value.Output}");
            QpdfReferenceTool.PageCount(path).Should().BeGreaterThanOrEqualTo(fixture.Page);

            // Poppler resolves the same effective rotation and boxes the row records.
            var info = PopplerGeometry.Info(path, fixture.Page);
            info.Rotation.Should().Be(fixture.EffectiveRotation, $"pdfinfo rotation of {id}");
            info.MediaBox.Should().BeEquivalentTo(fixture.MediaBox, o => o.Using<double>(
                c => c.Subject.Should().BeApproximately(c.Expectation, 0.01)).WhenTypeIs<double>());
            info.CropBox.Should().BeEquivalentTo(fixture.CropBox, o => o.Using<double>(
                c => c.Subject.Should().BeApproximately(c.Expectation, 0.01)).WhenTypeIs<double>());

            // MuPDF displays the rotated crop box: its stext page is the recorded visual size.
            var stext = MutoolStextGeometry.Read(path, fixture.Page);
            stext.Width.Should().BeApproximately(fixture.VisualWidth, 0.5, $"MuPDF displayed width of {id}");
            stext.Height.Should().BeApproximately(fixture.VisualHeight, 0.5, $"MuPDF displayed height of {id}");
            _out.WriteLine($"{id}: rot={info.Rotation} visual={stext.Width}x{stext.Height}");

            if (fixture.Target == null)
            {
                stext.Lines.SelectMany(l => l).Should().NotBeEmpty("a geometry-only fixture still has glyphs");
                return;
            }

            var mupdf = stext.Find(fixture.Target);
            mupdf.Should().NotBeEmpty($"MuPDF must find '{fixture.Target}' on {id}");
            var target = mupdf[0];
            target.Left.Should().BeGreaterThanOrEqualTo(0);
            target.Top.Should().BeGreaterThanOrEqualTo(0);
            target.Right.Should().BeLessThanOrEqualTo(stext.Width);
            target.Bottom.Should().BeLessThanOrEqualTo(stext.Height);

            // Poppler, asked for the crop box, places the word in the same displayed region.
            var poppler = PopplerGeometry.Words(path, fixture.Page, cropBox: true);
            var word = poppler.Words.Where(w => w.Word == fixture.Target).Select(w => w.Box).ToList();
            word.Should().NotBeEmpty($"Poppler must find '{fixture.Target}' on {id}");
            var pBox = word[0];
            _out.WriteLine($"{id}: mupdf {target} poppler {pBox} poppler-page={poppler.Width}x{poppler.Height}");
            pBox.CenterX.Should().BeApproximately(target.CenterX, 3, "MuPDF and Poppler agree on the word's displayed x");
            pBox.CenterY.Should().BeApproximately(target.CenterY, 3, "MuPDF and Poppler agree on the word's displayed y");
            // Along the line the two agree tightly. Across it they differ by design and the
            // difference is classified, not tolerated away: MuPDF's quads use the glyph
            // ascent/descent (13.1 pt for 18 pt Helvetica), Poppler's the font bbox (16.65 pt),
            // so Poppler's box must CONTAIN MuPDF's in that axis.
            // The displayed line direction is read from the box, not the page /Rotate:
            // issue14497 counter-rotates its text matrix, so its words read horizontally.
            bool vertical = target.Height > target.Width;
            double Along(VisualRegion r) => vertical ? r.Height : r.Width;
            Along(pBox).Should().BeApproximately(Along(target), Math.Max(2, Along(target) * 0.1),
                "MuPDF and Poppler agree on the word's extent along the line");
            target.FractionCoveredBy(pBox.Inflate(1)).Should().BeGreaterThan(0.9,
                "Poppler's font-bbox word box contains MuPDF's glyph-quad box across the line");

            // Classified disagreement: Poppler's -bbox <page> size is the UNROTATED box on
            // a quarter-turned page, though its words are in displayed space. Anything else fails.
            var unrotated = (W: fixture.CropBox[2] - fixture.CropBox[0], H: fixture.CropBox[3] - fixture.CropBox[1]);
            var agrees = Math.Abs(poppler.Width - stext.Width) < 0.5 && Math.Abs(poppler.Height - stext.Height) < 0.5;
            var unrotatedQuirk = Math.Abs(poppler.Width - unrotated.W) < 0.5 && Math.Abs(poppler.Height - unrotated.H) < 0.5;
            (agrees || unrotatedQuirk).Should().BeTrue(
                $"Poppler page size {poppler.Width}x{poppler.Height} is neither the displayed nor the unrotated crop size");

            // Ink: MuPDF's own render has dark pixels inside the stext region.
            using var bitmap = MutoolReferenceRenderer.RenderPage(path, fixture.Page, 72);
            bitmap.Should().NotBeNull("mutool must render the fixture");
            InkFraction(bitmap!, target.Inflate(1), stext.Width).Should().BeGreaterThan(0.05,
                "the stext region of the target is where MuPDF draws it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static double InkFraction(SKBitmap bmp, VisualRegion r, double pageWidthPoints)
    {
        double scale = bmp.Width / pageWidthPoints;
        int x0 = Math.Max(0, (int)(r.Left * scale)), x1 = Math.Min(bmp.Width, (int)Math.Ceiling(r.Right * scale));
        int y0 = Math.Max(0, (int)(r.Top * scale)), y1 = Math.Min(bmp.Height, (int)Math.Ceiling(r.Bottom * scale));
        int ink = 0, total = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var c = bmp.GetPixel(x, y);
                total++;
                if (c.Red + c.Green + c.Blue < 600) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }
}
