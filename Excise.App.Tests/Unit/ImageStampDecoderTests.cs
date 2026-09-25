using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.Unit;

public class ImageStampDecoderTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var f in _temp) { try { File.Delete(f); } catch (IOException) { } }
    }

    private string WritePng(int w, int h, Func<int, int, SKColor> pixel)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, pixel(x, y));
        var path = Path.Combine(Path.GetTempPath(), $"excise-decoder-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path))
            bmp.Encode(fs, SKEncodedImageFormat.Png, 100);
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void ATransparentPng_KeepsItsAlphaAndItsColour()
    {
        var path = WritePng(4, 2, (x, _) => x == 0 ? new SKColor(0, 0, 0, 0) : new SKColor(10, 20, 30, 255));

        ImageStampDecoder.TryDecode(path, out var image).Should().BeTrue();

        image.Width.Should().Be(4);
        image.Height.Should().Be(2);
        image.HasTransparency.Should().BeTrue();
        image.Alpha!.Take(4).Should().Equal(0, 255, 255, 255);
        image.Rgb[3].Should().Be(10);
        image.Rgb[4].Should().Be(20);
        image.Rgb[5].Should().Be(30);
    }

    [Fact]
    public void AnOpaquePng_HasNoAlpha()
    {
        var path = WritePng(3, 3, (_, _) => new SKColor(1, 2, 3, 255));

        ImageStampDecoder.TryDecode(path, out var image).Should().BeTrue();

        image.HasTransparency.Should().BeFalse();
        image.Alpha.Should().BeNull();
    }

    [Fact]
    public void ANonImage_IsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-decoder-{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "not an image");
        _temp.Add(path);

        ImageStampDecoder.TryDecode(path, out _).Should().BeFalse();
    }

    [Fact]
    public void InkOnWhite_IsRecognisedAndTheWhiteBecomesTransparent()
    {
        // A dark blue "stroke" down the middle of a white 9x9 scan.
        var path = WritePng(9, 9, (x, _) => x == 4 ? new SKColor(20, 30, 120, 255) : new SKColor(255, 255, 255, 255));
        ImageStampDecoder.TryDecode(path, out var scan).Should().BeTrue();

        ImageStampDecoder.LooksLikeInkOnWhite(scan).Should().BeTrue();
        var cleaned = ImageStampDecoder.RemoveLightBackground(scan);

        cleaned.Alpha![0].Should().Be(0, "white paper is dropped");
        cleaned.Alpha[4].Should().BeGreaterThan(200, "the ink stays");
        // Ink colour survives the un-blend: still a dark blue, not grey.
        cleaned.Rgb[4 * 3].Should().BeLessThan(60);
        cleaned.Rgb[4 * 3 + 2].Should().BeGreaterThan(100);
    }

    [Fact]
    public void ASoftPenEdge_KeepsPartialOpacity_AndAFullStrokeStaysOpaque()
    {
        // Mid-grey is ink at about 44% over white; pure black is fully opaque.
        var path = WritePng(3, 1, (x, _) => x switch
        {
            0 => new SKColor(128, 128, 128, 255),
            1 => new SKColor(0, 0, 0, 255),
            _ => new SKColor(255, 255, 255, 255),
        });
        ImageStampDecoder.TryDecode(path, out var scan).Should().BeTrue();

        var cleaned = ImageStampDecoder.RemoveLightBackground(scan);

        cleaned.Alpha![0].Should().BeInRange(80, 140, "a soft edge is partly transparent, not all or nothing");
        cleaned.Alpha[1].Should().Be(255);
        cleaned.Alpha[2].Should().Be(0);
        cleaned.Alpha[0].Should().BeLessThan(cleaned.Alpha[1]);
    }

    [Fact]
    public void ScannerNoiseNearWhite_IsDropped()
    {
        var path = WritePng(9, 9, (x, y) => (x + y) % 2 == 0 ? new SKColor(245, 245, 245, 255) : new SKColor(255, 255, 255, 255));
        ImageStampDecoder.TryDecode(path, out var scan).Should().BeTrue();

        ImageStampDecoder.RemoveLightBackground(scan).Alpha!.Should().OnlyContain(a => a == 0);
    }

    [Fact]
    public void APhotoOrColouredImage_IsNotTreatedAsInkOnWhite()
    {
        var path = WritePng(9, 9, (_, _) => new SKColor(200, 60, 60, 255));
        ImageStampDecoder.TryDecode(path, out var image).Should().BeTrue();
        ImageStampDecoder.LooksLikeInkOnWhite(image).Should().BeFalse();

        var already = WritePng(9, 9, (_, _) => new SKColor(255, 255, 255, 0));
        ImageStampDecoder.TryDecode(already, out var transparent).Should().BeTrue();
        ImageStampDecoder.LooksLikeInkOnWhite(transparent).Should().BeFalse("it already has transparency");
    }

    [Theory]
    [InlineData(400, 100, 200, 100)]   // wide picture, square-ish box: fills the width
    [InlineData(100, 400, 200, 100)]   // tall picture, wide box: fills the height
    [InlineData(200, 100, 200, 100)]   // same proportions: fills the box
    public void FitInside_KeepsTheAspectRatio_AndCentres(int imageW, int imageH, double boxW, double boxH)
    {
        var box = new PdfRectangle(100, 300, 100 + boxW, 300 + boxH);

        var fit = ImageStampDecoder.FitInside(box, imageW, imageH);

        (fit.Width / fit.Height).Should().BeApproximately((double)imageW / imageH, 1e-9);
        fit.Width.Should().BeLessThanOrEqualTo(boxW + 1e-9);
        fit.Height.Should().BeLessThanOrEqualTo(boxH + 1e-9);
        ((fit.Left + fit.Right) / 2).Should().BeApproximately((box.Left + box.Right) / 2, 1e-9);
        ((fit.Bottom + fit.Top) / 2).Should().BeApproximately((box.Bottom + box.Top) / 2, 1e-9);
        (Math.Abs(fit.Width - boxW) < 1e-9 || Math.Abs(fit.Height - boxH) < 1e-9).Should().BeTrue(
            "it is the largest fit: it touches the box on at least one axis");
    }

    [Fact]
    public void FitInside_AReversedDragRectangle_IsNormalised()
    {
        var fit = ImageStampDecoder.FitInside(new PdfRectangle(300, 400, 100, 300), 100, 100);
        fit.Left.Should().BeLessThan(fit.Right);
        fit.Bottom.Should().BeLessThan(fit.Top);
    }
}
