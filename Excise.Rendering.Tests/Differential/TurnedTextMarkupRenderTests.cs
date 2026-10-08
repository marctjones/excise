using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1984 at the authoring layer: Underline / StrikeOut / Squiggly on text whose baseline
/// runs along user-space y (a /Rotate 90 page whose text matrix turns back, so it reads
/// upright on screen). Before the fix the appearance was drawn along user-space x: a
/// line across the end of the word, not along it. MuPDF renders the saved bytes and its
/// own stext says where the word is and which way it reads.
/// </summary>
public class TurnedTextMarkupRenderTests
{
    private const int Dpi = 144;

    [Theory]
    [InlineData("Underline")]
    [InlineData("StrikeOut")]
    [InlineData("Squiggly")]
    public void Markup_OnTextTurnedInUserSpace_RunsAlongTheWord(string subtype)
    {
        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable || !MutoolStextGeometry.IsAvailable,
            "mutool is not installed; it is the render and glyph-region oracle.");

        var fixture = RotationFixtures.Get("probe-r90-textccw");
        var bytes = RotationFixtures.TryLoad(fixture, out _)!;
        byte[] saved;
        using (var doc = PdfDocument.Open(bytes))
        {
            // The marked rect: the first five letters' cells, as a text selection would give.
            var cells = doc.GetPage(1).Letters.Take(5).Select(l => l.GlyphRectangle).ToList();
            var rect = new PdfRectangle(cells.Min(c => c.Left), cells.Min(c => c.Bottom),
                cells.Max(c => c.Right), cells.Max(c => c.Top));
            _ = subtype switch
            {
                "Underline" => doc.AddUnderlineAnnotation(1, rect),
                "StrikeOut" => doc.AddStrikeOutAnnotation(1, rect),
                _ => doc.AddSquigglyAnnotation(1, rect),
            };
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-1984-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        try
        {
            var stext = MutoolStextGeometry.Read(path, 1);
            var word = stext.Find("ALPHA")[0];
            var dir = stext.DirectionOf("ALPHA");
            dir.Should().Be((1.0, 0.0), "the probe's text reads left to right on screen");
            using var bmp = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            bmp.Should().NotBeNull();

            // Red stroke pixels near the word, located along and across the line.
            double scale = bmp!.Width / stext.Width;
            double cross = word.Height;
            int colored = 0;
            double xMin = double.MaxValue, xMax = double.MinValue, ySum = 0;
            for (int py = (int)((word.Top - cross) * scale); py < (int)((word.Bottom + cross) * scale); py++)
                for (int px = (int)((word.Left - 2) * scale); px < (int)((word.Right + 2) * scale); px++)
                {
                    var c = bmp.GetPixel(px, py);
                    if (c.Red - Math.Max(c.Green, c.Blue) < 80) continue;
                    colored++;
                    double x = (px + 0.5) / scale, y = (py + 0.5) / scale;
                    xMin = Math.Min(xMin, x);
                    xMax = Math.Max(xMax, x);
                    ySum += y;
                }

            colored.Should().BeGreaterThan(10, $"the {subtype} is drawn at the word {word}");
            ((xMax - xMin) / word.Width).Should().BeInRange(0.75, 1.35, $"the {subtype} runs the length of the word");
            double meanY = ySum / colored;
            if (subtype == "StrikeOut")
                meanY.Should().BeInRange(word.Top, word.Bottom, "a strikeout runs through the word");
            else
                meanY.Should().BeGreaterThan(word.Bottom - cross * 0.2, $"a {subtype} runs along the baseline, below the text");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
