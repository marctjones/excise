using Excise.Core.ColorSpaces;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Context-free conversion of decoded PDF image samples. The renderer resolves
/// indirect objects and image-mask paint state before constructing the request;
/// this type owns sample unpacking, Decode arrays, colour-key masking, and
/// colour-space conversion.
/// </summary>
internal static class RawSampleImageDecoder
{
    public static SKBitmap? Decode(RawSampleImageDecodeRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.ColorSpace == null || request.Width <= 0 || request.Height <= 0)
        {
            return null;
        }

        try
        {
            if (request.DecodeArray == null && request.ColorKeyMask == null)
            {
                var fastBitmap = TryDecodeFast(request);
                if (fastBitmap != null)
                    return fastBitmap;
            }

            return DecodeGeneral(request);
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

    private static SKBitmap? TryDecodeFast(RawSampleImageDecodeRequest request)
    {
        if (request.BitsPerComponent != 8)
            return null;

        var expectedPixels = checked((long)request.Width * request.Height);
        var requiredBytes = checked(expectedPixels * request.ComponentsPerPixel);
        if (requiredBytes > request.Samples.LongLength)
            return null;

        return request.ColorSpace!.Type switch
        {
            PdfColorSpaceType.DeviceGray when request.ComponentsPerPixel == 1 =>
                CreateGrayBitmap(request.Samples, request.Width, request.Height),
            PdfColorSpaceType.DeviceRGB when request.ComponentsPerPixel == 3 =>
                CreateRgbBitmap(request.Samples, request.Width, request.Height),
            PdfColorSpaceType.DeviceCMYK when request.ComponentsPerPixel == 4 =>
                CreateCmykBitmap(request.Samples, request.Width, request.Height, request.ColorSpace),
            _ => null
        };
    }

    private static SKBitmap? DecodeGeneral(RawSampleImageDecodeRequest request)
    {
        var colorSpace = request.ColorSpace!;
        var componentsPerPixel = request.ComponentsPerPixel;
        if (componentsPerPixel <= 0)
            return null;

        var bitmap = new SKBitmap(
            request.Width,
            request.Height,
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
            var sourceIndex = 0;
            var destinationIndex = 0;
            var pixelValues = new double[componentsPerPixel];
            var maxSample = Math.Pow(2, request.BitsPerComponent) - 1;
            var imageColorConverter = request.DecodeArray == null
                ? ImageColorConverter.For(colorSpace)
                : null;
            var rawSamples = request.ColorKeyMask != null
                ? new int[componentsPerPixel]
                : null;

            for (var y = 0; y < request.Height; y++)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < request.Width; x++)
                {
                    byte red = 0, green = 0, blue = 0, alpha = 255;
                    var samplesRead = false;

                    if (request.BitsPerComponent > 1)
                    {
                        if (request.BitsPerComponent == 8 &&
                            sourceIndex + componentsPerPixel <= request.Samples.Length)
                        {
                            for (var component = 0; component < componentsPerPixel; component++)
                            {
                                var sample = request.Samples[sourceIndex + component];
                                if (rawSamples != null)
                                    rawSamples[component] = sample;
                                pixelValues[component] = DecodeImageSample(
                                    request.DecodeArray,
                                    colorSpace,
                                    component,
                                    sample,
                                    maxSample);
                            }
                            sourceIndex += componentsPerPixel;
                            samplesRead = true;
                            ConvertPixel(
                                colorSpace,
                                imageColorConverter,
                                pixelValues,
                                out red,
                                out green,
                                out blue);
                        }
                        else if (request.BitsPerComponent != 8)
                        {
                            var rowBits = checked(request.Width * componentsPerPixel * request.BitsPerComponent);
                            var rowStrideBits = AlignBitsToByte(rowBits);
                            var bitOffset = checked(
                                (y * rowStrideBits) +
                                (x * componentsPerPixel * request.BitsPerComponent));
                            if (bitOffset + (componentsPerPixel * request.BitsPerComponent) <=
                                request.Samples.Length * 8)
                            {
                                for (var component = 0; component < componentsPerPixel; component++)
                                {
                                    var sample = ReadPackedImageSample(
                                        request.Samples,
                                        bitOffset + (component * request.BitsPerComponent),
                                        request.BitsPerComponent);
                                    if (rawSamples != null)
                                        rawSamples[component] = sample;
                                    pixelValues[component] = DecodeImageSample(
                                        request.DecodeArray,
                                        colorSpace,
                                        component,
                                        sample,
                                        maxSample);
                                }

                                samplesRead = true;
                                ConvertPixel(
                                    colorSpace,
                                    imageColorConverter,
                                    pixelValues,
                                    out red,
                                    out green,
                                    out blue);
                            }
                        }
                    }
                    else if (request.BitsPerComponent == 1)
                    {
                        var byteIndex = sourceIndex / 8;
                        var bitIndex = 7 - (sourceIndex % 8);
                        var sample = byteIndex < request.Samples.Length
                            ? (request.Samples[byteIndex] >> bitIndex) & 1
                            : 0;
                        pixelValues[0] = DecodeImageSample(
                            request.DecodeArray,
                            colorSpace,
                            0,
                            sample,
                            maxSample);
                        ConvertPixel(
                            colorSpace,
                            imageColorConverter,
                            pixelValues,
                            out red,
                            out green,
                            out blue);
                        if (rawSamples != null)
                            rawSamples[0] = sample;
                        samplesRead = true;
                        sourceIndex++;
                    }

                    if (samplesRead &&
                        rawSamples != null &&
                        IsColorKeyMasked(rawSamples, request.ColorKeyMask!))
                    {
                        alpha = 0;
                    }

                    pixels[destinationIndex++] = red;
                    pixels[destinationIndex++] = green;
                    pixels[destinationIndex++] = blue;
                    pixels[destinationIndex++] = alpha;
                }

                if (request.BitsPerComponent == 1)
                    sourceIndex = AlignBitsToByte(sourceIndex);
            }
        }
        catch (OperationCanceledException)
        {
            bitmap.Dispose();
            throw;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    private static void ConvertPixel(
        PdfColorSpace colorSpace,
        ImageColorConverter? converter,
        double[] values,
        out byte red,
        out byte green,
        out byte blue)
    {
        if (converter != null)
        {
            var rgb = converter.ToRgb(values);
            red = rgb.R;
            green = rgb.G;
            blue = rgb.B;
            return;
        }

        var converted = colorSpace.ToRgb(values);
        red = (byte)Math.Clamp(converted.R * 255, 0, 255);
        green = (byte)Math.Clamp(converted.G * 255, 0, 255);
        blue = (byte)Math.Clamp(converted.B * 255, 0, 255);
    }

    private static int AlignBitsToByte(int bitCount)
        => ((bitCount + 7) / 8) * 8;

    private static int ReadPackedImageSample(byte[] data, int bitOffset, int bitsPerComponent)
    {
        var sample = 0;
        for (var i = 0; i < bitsPerComponent; i++)
        {
            var absoluteBit = bitOffset + i;
            var byteIndex = absoluteBit / 8;
            if (byteIndex >= data.Length)
                break;

            var bitIndex = 7 - (absoluteBit % 8);
            sample = (sample << 1) | ((data[byteIndex] >> bitIndex) & 1);
        }

        return sample;
    }

    private static double DecodeImageSample(
        double[]? decode,
        PdfColorSpace colorSpace,
        int componentIndex,
        int sample,
        double maxSample)
    {
        var offset = componentIndex * 2;
        if (decode != null && decode.Length >= offset + 2)
        {
            var decodeMinimum = decode[offset];
            var decodeMaximum = decode[offset + 1];
            return maxSample > 0
                ? decodeMinimum + sample * ((decodeMaximum - decodeMinimum) / maxSample)
                : decodeMinimum;
        }

        if (colorSpace.Type == PdfColorSpaceType.Indexed)
            return sample;

        var normalizedByte = maxSample > 0
            ? (byte)Math.Clamp((int)Math.Round(sample * (255.0 / maxSample)), 0, 255)
            : (byte)0;
        return colorSpace.DecodeSampleByte(componentIndex, normalizedByte);
    }

    /// <summary>
    /// Colour-key ranges are tested against raw samples, before colour-space
    /// conversion. A pixel is transparent only when every component is inside
    /// its inclusive range (PDF 32000-1 section 8.9.6.4).
    /// </summary>
    private static bool IsColorKeyMasked(int[] rawSamples, int[] ranges)
    {
        for (var component = 0; component < rawSamples.Length; component++)
        {
            var minimum = ranges[component * 2];
            var maximum = ranges[(component * 2) + 1];
            if (minimum > maximum)
                (minimum, maximum) = (maximum, minimum);
            if (rawSamples[component] < minimum || rawSamples[component] > maximum)
                return false;
        }

        return true;
    }

    private static SKBitmap? CreateGrayBitmap(byte[] data, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var gray = data[src++];
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = 255;
        }

        return bitmap;
    }

    private static SKBitmap? CreateRgbBitmap(byte[] data, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = 255;
        }

        return bitmap;
    }

    private static SKBitmap? CreateCmykBitmap(
        byte[] data,
        int width,
        int height,
        PdfColorSpace colorSpace)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        var converter = ImageColorConverter.For(colorSpace);
        if (converter == null)
        {
            bitmap.Dispose();
            return null;
        }

        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var (r, g, b) = converter.ToRgb(
                data[src],
                data[src + 1],
                data[src + 2],
                data[src + 3]);
            src += 4;
            pixels[dst++] = r;
            pixels[dst++] = g;
            pixels[dst++] = b;
            pixels[dst++] = 255;
        }

        return bitmap;
    }
}

internal readonly record struct RawSampleImageDecodeRequest(
    byte[] Samples,
    int Width,
    int Height,
    int BitsPerComponent,
    PdfColorSpace? ColorSpace,
    int ComponentsPerPixel,
    double[]? DecodeArray,
    int[]? ColorKeyMask,
    CancellationToken CancellationToken = default);

internal static class SkiaBitmapPixelBuffer
{
    public static unsafe Span<byte> GetWritableSpan(SKBitmap bitmap)
    {
        var pointer = bitmap.GetPixels();
        return pointer == IntPtr.Zero
            ? Span<byte>.Empty
            : new Span<byte>((void*)pointer, bitmap.RowBytes * bitmap.Height);
    }
}
