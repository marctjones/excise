using Excise.Core.ColorSpaces;
using Excise.Core.Filters.Jpx;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Context-free JPX codec selection and conversion to a Skia bitmap. PDF
/// resource resolution, filter-chain selection, placement, caching, and
/// diagnostics remain with the render execution layer.
/// </summary>
internal static class JpxImageDecoder
{
    public static SKBitmap? Decode(JpxImageDecodeRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Bytes.Length == 0 ||
            request.SourceWidth <= 0 ||
            request.SourceHeight <= 0 ||
            request.MaximumPixels <= 0)
        {
            return null;
        }

        try
        {
            var desiredComponents = Math.Max(1, request.ColorSpace.Components);
            if (request.ColorSpace.Components >= 3 && !request.HasExternalSoftMask)
                desiredComponents++;

            var reduceFactor = ChooseOpenJpegReduceFactor(
                request.SourceWidth,
                request.SourceHeight,
                request.TargetWidth,
                request.TargetHeight,
                request.MaximumPixels);
            var image = desiredComponents == 1 && request.HasExternalSoftMask
                ? JpxDecoder.TryDecodeOpenJpegGray(request.Bytes)
                : JpxDecoder.TryDecodeOpenJpeg(request.Bytes, reduceFactor);

            request.CancellationToken.ThrowIfCancellationRequested();
            if (image == null &&
                (long)request.SourceWidth * request.SourceHeight <= request.MaximumPixels)
            {
                image = JpxDecoder.TryDecodeManaged(request.Bytes, desiredComponents);
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            if (image == null || image.Components <= 0 || image.ComponentData.Length == 0)
                return null;

            return ConvertToBitmap(image, request);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap? ConvertToBitmap(JpxImage image, JpxImageDecodeRequest request)
    {
        var components = image.ComponentData;
        var decodedWidth = image.Width > 0 ? image.Width : request.SourceWidth;
        var decodedHeight = image.Height > 0 ? image.Height : request.SourceHeight;
        var target = image.BitsPerComponent > 8
            ? ClampTargetSize(
                request.SourceWidth,
                request.SourceHeight,
                request.SourceWidth,
                request.SourceHeight,
                request.MaximumPixels)
            : ClampTargetSize(
                decodedWidth,
                decodedHeight,
                request.TargetWidth,
                request.TargetHeight,
                request.MaximumPixels);

        var bitmap = new SKBitmap(
            target.Width,
            target.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        try
        {
            var destination = 0;
            var sourcePixelCount = (long)decodedWidth * decodedHeight;
            var hasEmbeddedAlpha = !request.HasExternalSoftMask &&
                                   components.Length > request.ColorSpace.Components &&
                                   request.ColorSpace.Components >= 1;
            var converter = ImageColorConverter.For(request.ColorSpace);
            for (var y = 0; y < target.Height; y++)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var sourceY = MapTargetToSource(y, target.Height, decodedHeight);
                var sourceRow = (long)sourceY * decodedWidth;
                for (var x = 0; x < target.Width; x++)
                {
                    var sourceX = MapTargetToSource(x, target.Width, decodedWidth);
                    var sourceIndex = sourceRow + sourceX;
                    if (sourceIndex >= sourcePixelCount)
                    {
                        destination += 4;
                        continue;
                    }

                    var (red, green, blue) = ConvertColor(
                        image,
                        request.ColorSpace,
                        converter,
                        components,
                        sourceIndex);
                    var alpha = 255;
                    if (hasEmbeddedAlpha)
                    {
                        var alphaComponentIndex = GetAlphaComponentIndex(
                            image,
                            request.ColorSpace.Components,
                            components.Length);
                        var alphaComponent = components[alphaComponentIndex];
                        if (sourceIndex < alphaComponent.LongLength)
                        {
                            alpha = NormalizeSampleToByte(
                                alphaComponent[(int)sourceIndex],
                                image.BitsPerComponent);
                        }
                    }

                    pixels[destination++] = red;
                    pixels[destination++] = green;
                    pixels[destination++] = blue;
                    pixels[destination++] = (byte)alpha;
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static (byte Red, byte Green, byte Blue) ConvertColor(
        JpxImage image,
        PdfColorSpace colorSpace,
        ImageColorConverter? converter,
        int[][] components,
        long sourceIndex)
    {
        if (image.ComponentsAreDisplayRgb && components.Length >= 3)
        {
            return (
                UnitToByte(NormalizeSampleToUnit(
                    sourceIndex < components[0].LongLength ? components[0][(int)sourceIndex] : 0,
                    image.BitsPerComponent)),
                UnitToByte(NormalizeSampleToUnit(
                    sourceIndex < components[1].LongLength ? components[1][(int)sourceIndex] : 0,
                    image.BitsPerComponent)),
                UnitToByte(NormalizeSampleToUnit(
                    sourceIndex < components[2].LongLength ? components[2][(int)sourceIndex] : 0,
                    image.BitsPerComponent)));
        }

        if (colorSpace.Type == PdfColorSpaceType.Indexed)
        {
            var index = sourceIndex < components[0].LongLength
                ? components[0][(int)sourceIndex]
                : 0;
            if (converter != null)
                return converter.ToRgb(index);

            var converted = colorSpace.ToRgb([index]);
            return (UnitToByte(converted.R), UnitToByte(converted.G), UnitToByte(converted.B));
        }

        var values = new double[Math.Max(1, colorSpace.Components)];
        for (var component = 0; component < values.Length; component++)
        {
            var decodedComponent = GetColorComponentIndex(
                image,
                colorSpace,
                component,
                components.Length);
            var sample = decodedComponent < components.Length &&
                         sourceIndex < components[decodedComponent].LongLength
                ? components[decodedComponent][(int)sourceIndex]
                : 0;
            values[component] = colorSpace.DecodeSampleByte(
                component,
                NormalizeSampleToByte(sample, image.BitsPerComponent));
        }

        if (converter != null)
            return converter.ToRgb(values);

        var rgb = colorSpace.ToRgb(values);
        return (UnitToByte(rgb.R), UnitToByte(rgb.G), UnitToByte(rgb.B));
    }

    private static int ChooseOpenJpegReduceFactor(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        long maximumPixels)
    {
        const int maxOpenJpegReduction = 5;
        var reduce = 0;
        while (reduce < maxOpenJpegReduction)
        {
            var next = reduce + 1;
            var nextWidth = Math.Max(1, sourceWidth >> next);
            var nextHeight = Math.Max(1, sourceHeight >> next);
            if (nextWidth < targetWidth || nextHeight < targetHeight)
            {
                var currentWidth = Math.Max(1, sourceWidth >> reduce);
                var currentHeight = Math.Max(1, sourceHeight >> reduce);
                if ((long)currentWidth * currentHeight > maximumPixels)
                {
                    reduce = next;
                    continue;
                }

                break;
            }

            reduce = next;
        }

        return reduce;
    }

    private static int GetColorComponentIndex(
        JpxImage image,
        PdfColorSpace colorSpace,
        int requestedComponent,
        int decodedComponentCount)
    {
        if (image.ComponentDefinitions.Count > 0)
        {
            var association = requestedComponent + 1;
            foreach (var component in image.ComponentDefinitions)
            {
                if (component.Type == 0 &&
                    component.Association == association &&
                    component.ComponentIndex >= 0 &&
                    component.ComponentIndex < decodedComponentCount)
                {
                    return component.ComponentIndex;
                }
            }
        }

        if (decodedComponentCount >= 3 &&
            requestedComponent < 3 &&
            !image.ComponentsAreLogicalColorOrder &&
            colorSpace.Components == 3 &&
            colorSpace.Type is PdfColorSpaceType.DeviceRGB or
                PdfColorSpaceType.CalRGB or
                PdfColorSpaceType.ICCBased)
        {
            return 2 - requestedComponent;
        }

        return requestedComponent;
    }

    private static int GetAlphaComponentIndex(
        JpxImage image,
        int fallbackIndex,
        int decodedComponentCount)
    {
        if (image.ComponentDefinitions.Count > 0)
        {
            foreach (var component in image.ComponentDefinitions)
            {
                if (component.Type is 1 or 2 &&
                    component.ComponentIndex >= 0 &&
                    component.ComponentIndex < decodedComponentCount)
                {
                    return component.ComponentIndex;
                }
            }
        }

        return Math.Clamp(fallbackIndex, 0, Math.Max(0, decodedComponentCount - 1));
    }

    private static (int Width, int Height) ClampTargetSize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        long maximumPixels)
    {
        var width = Math.Clamp(targetWidth, 1, Math.Max(1, sourceWidth));
        var height = Math.Clamp(targetHeight, 1, Math.Max(1, sourceHeight));
        var pixels = (long)width * height;
        if (pixels <= maximumPixels)
            return (width, height);

        var scale = Math.Sqrt(maximumPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private static int MapTargetToSource(int targetPosition, int targetSize, int sourceSize)
        => Math.Clamp(
            (int)(((targetPosition + 0.5) * sourceSize) / targetSize),
            0,
            sourceSize - 1);

    private static byte NormalizeSampleToByte(int sample, int bitsPerComponent)
    {
        if (bitsPerComponent <= 8)
            return (byte)Math.Clamp(sample, 0, 255);

        var maxSample = bitsPerComponent >= 31
            ? int.MaxValue
            : (1 << bitsPerComponent) - 1;
        if (maxSample <= 255)
            return (byte)Math.Clamp(sample, 0, 255);

        var normalized = (long)Math.Clamp(sample, 0, maxSample) * 255 + (maxSample / 2);
        return (byte)(normalized / maxSample);
    }

    private static double NormalizeSampleToUnit(int sample, int bitsPerComponent)
    {
        if (bitsPerComponent <= 8)
            return Math.Clamp(sample, 0, 255) / 255.0;

        var maxSample = bitsPerComponent >= 31
            ? int.MaxValue
            : (1 << bitsPerComponent) - 1;
        return maxSample > 0
            ? Math.Clamp(sample, 0, maxSample) / (double)maxSample
            : 0;
    }

    private static byte UnitToByte(double value)
        => (byte)Math.Clamp(value * 255, 0, 255);
}

internal readonly record struct JpxImageDecodeRequest(
    byte[] Bytes,
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight,
    PdfColorSpace ColorSpace,
    bool HasExternalSoftMask,
    long MaximumPixels,
    CancellationToken CancellationToken);
