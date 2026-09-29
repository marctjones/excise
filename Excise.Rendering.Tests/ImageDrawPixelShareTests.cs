using AwesomeAssertions;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1804: the renderer draws image bitmaps through <c>SkiaRenderer.ShareAsImage</c>
/// instead of <see cref="SKCanvas.DrawBitmap(SKBitmap, SKRect, SKPaint)"/>, which
/// copies the whole bitmap on every draw. The saving rests on one SkiaSharp
/// behaviour, pinned here so a SkiaSharp upgrade that changes it fails loudly
/// instead of silently bringing the 17-24 MB per-draw copy back.
/// </summary>
public class ImageDrawPixelShareTests
{
    [Fact]
    public void FromBitmap_CopiesAMutableBitmap_AndSharesAnImmutableOne()
    {
        using var mutable = NewBitmap();
        using (var copied = SKImage.FromBitmap(mutable))
        using (var copiedPixels = copied.PeekPixels())
        {
            copiedPixels.GetPixels().Should().NotBe(mutable.GetPixels(),
                "a mutable bitmap is copied: this is the allocation #1804 removes");
        }

        using var shared = NewBitmap();
        shared.SetImmutable();
        using var image = SKImage.FromBitmap(shared);
        using var sharedPixels = image.PeekPixels();
        sharedPixels.GetPixels().Should().Be(shared.GetPixels(),
            "an immutable bitmap's pixel ref is shared, not copied");
    }

    [Fact]
    public void ASharedImage_KeepsItsPixelsAfterTheBitmapIsDisposed()
    {
        // A pattern cell records into an SKPicture, which can outlive the
        // bitmap handle; the share must hold the pixels by reference.
        SKImage image;
        using (var bitmap = NewBitmap())
        {
            bitmap.SetImmutable();
            image = SKImage.FromBitmap(bitmap);
        }

        using (image)
        using (var pixels = image.PeekPixels())
        {
            pixels.GetPixelColor(1, 1).Should().Be(SKColors.Red);
        }
    }

    private static SKBitmap NewBitmap()
    {
        var bitmap = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        return bitmap;
    }
}
