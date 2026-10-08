using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1985 at the engine layer: typewriter text flattened onto a rotated page reads upright
/// on the DISPLAYED page, inside the box drawn on screen. Before the fix it was laid out
/// in user space: on /Rotate 90 a 150 x 30 pt on-screen box became a 30 x 150 pt content
/// box, and the text ran down the screen out of it. MuPDF stext of the saved file is the
/// oracle for both the region and the reading direction.
/// </summary>
public class RotatedTypewriterPlacementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TypedText_ReadsUprightInsideTheOnScreenBox(int rotation)
    {
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable, "mutool is not installed; it is the glyph-region oracle.");

        const string typed = "UPRIGHT1985";
        var bytes = RotationFixtures.TryLoad(RotationFixtures.Get("probe-r0"), out _)!;
        var box = PdfPageRect.VisualPoints(1, 60, 70, 180, 30);
        byte[] saved;
        using (var doc = PdfDocument.Open(bytes))
        {
            var page = doc.GetPage(1);
            page.Rotation = rotation;
            var content = PdfCoordinateMapper.ToContentPoints(page, box).ToPdfRectangle();
            PdfTypewriterTextApplier.Apply(doc, PdfTypewriterTextOperation.Create(1, content, typed,
                new PdfTypewriterTextStyle(fontSize: 14)));
            saved = doc.SaveToBytes();
        }

        var stext = MutoolStextGeometry.Read(saved, 1);
        var hits = stext.Find(typed);
        hits.Should().NotBeEmpty("MuPDF must read the complete typed word");
        stext.DirectionOf(typed).Should().Be((1.0, 0.0), $"typed text reads left to right on the /Rotate {rotation} page");
        var region = new VisualRegion(box.X, box.Y, box.Right, box.Y2);
        hits[0].FractionCoveredBy(region.Inflate(2)).Should().BeGreaterThan(0.95,
            $"MuPDF places the word at {hits[0]}, inside the on-screen box {region}");
    }
}
