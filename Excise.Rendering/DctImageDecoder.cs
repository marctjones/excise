using BitMiracle.LibJpeg.Classic;
using Excise.Core.ColorSpaces;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Context-free DCT codec policy for PDF color transforms that Skia cannot
/// infer reliably. PDF object resolution and filter selection remain with the
/// render execution layer.
/// </summary>
internal static class DctImageDecoder
{
    internal static int? ResolveColorTransform(
        byte[] data,
        string colorSpace,
        int? decodeParametersColorTransform)
    {
        var normalizedColorSpace = NormalizeColorSpaceName(colorSpace);
        if (TryGetAdobeColorTransform(data, out var markerColorTransform))
        {
            if (normalizedColorSpace == "DeviceCMYK" || markerColorTransform == 0)
                return markerColorTransform;

            if (normalizedColorSpace == "DeviceRGB")
                return null;
        }

        if (decodeParametersColorTransform is { } declaredColorTransform)
            return declaredColorTransform;

        return normalizedColorSpace == "DeviceCMYK" ? 0 : null;
    }

    internal static SKBitmap? Decode(DctImageDecodeRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Bytes.Length == 0 ||
            request.SourceWidth <= 0 ||
            request.SourceHeight <= 0 ||
            !TryGetColorSpaces(
                request.ColorSpaceName,
                request.ColorTransform,
                out var inputColorSpace,
                out var outputColorSpace))
        {
            return null;
        }

        var scaleDenominator = outputColorSpace == J_COLOR_SPACE.JCS_CMYK
            ? 1
            : ChooseScaleDenominator(
                request.SourceWidth,
                request.SourceHeight,
                request.TargetWidth,
                request.TargetHeight);
        var decompressor = new jpeg_decompress_struct();
        try
        {
            using var input = new MemoryStream(request.Bytes, writable: false);
            decompressor.jpeg_stdio_src(input);
            decompressor.jpeg_read_header(true);
            decompressor.Jpeg_color_space = inputColorSpace;
            decompressor.Out_color_space = outputColorSpace;
            decompressor.Scale_num = 1;
            decompressor.Scale_denom = scaleDenominator;

            request.CancellationToken.ThrowIfCancellationRequested();
            decompressor.jpeg_start_decompress();
            var width = decompressor.Output_width;
            var height = decompressor.Output_height;
            if (width <= 0 || height <= 0)
                return null;

            return outputColorSpace == J_COLOR_SPACE.JCS_CMYK
                ? DecodeCmyk(decompressor, width, height, request)
                : DecodeRgb(decompressor, width, height, request);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                decompressor.jpeg_destroy();
            }
            catch
            {
                // Malformed JPEG data can also make native cleanup fail.
            }
        }
    }

    private static SKBitmap? DecodeRgb(
        jpeg_decompress_struct decompressor,
        int width,
        int height,
        DctImageDecodeRequest request)
    {
        if (decompressor.Output_components != 3)
            return null;

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        try
        {
            var scanline = new[] { new byte[checked(width * decompressor.Output_components)] };
            var destination = 0;
            while (decompressor.Output_scanline < decompressor.Output_height)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                decompressor.jpeg_read_scanlines(scanline, 1);
                var row = scanline[0];
                for (var source = 0; source < width * 3;)
                {
                    pixels[destination++] = row[source++];
                    pixels[destination++] = row[source++];
                    pixels[destination++] = row[source++];
                    pixels[destination++] = 255;
                }
            }

            decompressor.jpeg_finish_decompress();
            return Resize(
                bitmap,
                Math.Clamp(request.TargetWidth, 1, request.SourceWidth),
                Math.Clamp(request.TargetHeight, 1, request.SourceHeight));
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static SKBitmap? DecodeCmyk(
        jpeg_decompress_struct decompressor,
        int width,
        int height,
        DctImageDecodeRequest request)
    {
        if (decompressor.Output_components != 4 || request.ResolvedColorSpace == null)
            return null;

        var samples = new byte[checked(width * height * 4)];
        var scanline = new[] { new byte[checked(width * decompressor.Output_components)] };
        var destination = 0;
        while (decompressor.Output_scanline < decompressor.Output_height)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            decompressor.jpeg_read_scanlines(scanline, 1);
            var row = scanline[0];
            Array.Copy(row, 0, samples, destination, width * 4);
            destination += width * 4;
        }

        decompressor.jpeg_finish_decompress();
        var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            samples,
            width,
            height,
            8,
            request.ResolvedColorSpace,
            request.ResolvedColorSpace.Components,
            request.DecodeArray,
            request.ColorKeyMask,
            request.CancellationToken));
        if (bitmap == null)
            return null;

        return Resize(
            bitmap,
            Math.Clamp(request.TargetWidth, 1, request.SourceWidth),
            Math.Clamp(request.TargetHeight, 1, request.SourceHeight));
    }

    private static bool TryGetColorSpaces(
        string colorSpace,
        int colorTransform,
        out J_COLOR_SPACE inputColorSpace,
        out J_COLOR_SPACE outputColorSpace)
    {
        inputColorSpace = J_COLOR_SPACE.JCS_UNKNOWN;
        outputColorSpace = J_COLOR_SPACE.JCS_UNKNOWN;
        switch (NormalizeColorSpaceName(colorSpace))
        {
            case "DeviceRGB":
                inputColorSpace = colorTransform == 0
                    ? J_COLOR_SPACE.JCS_RGB
                    : J_COLOR_SPACE.JCS_YCbCr;
                outputColorSpace = J_COLOR_SPACE.JCS_RGB;
                return true;
            case "DeviceCMYK":
                inputColorSpace = colorTransform == 0
                    ? J_COLOR_SPACE.JCS_CMYK
                    : J_COLOR_SPACE.JCS_YCCK;
                outputColorSpace = J_COLOR_SPACE.JCS_CMYK;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetAdobeColorTransform(byte[] data, out int colorTransform)
    {
        colorTransform = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return false;

        var offset = 2;
        while (offset + 3 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < data.Length && data[offset] == 0xFF)
                offset++;
            if (offset >= data.Length)
                return false;

            var marker = data[offset++];
            if (marker == 0xDA || marker == 0xD9)
                return false;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (offset + 1 >= data.Length)
                return false;

            var segmentLength = (data[offset] << 8) | data[offset + 1];
            if (segmentLength < 2)
                return false;
            var payloadOffset = offset + 2;
            var nextOffset = offset + segmentLength;
            if (nextOffset > data.Length)
                return false;

            if (marker == 0xEE &&
                segmentLength >= 14 &&
                data[payloadOffset] == (byte)'A' &&
                data[payloadOffset + 1] == (byte)'d' &&
                data[payloadOffset + 2] == (byte)'o' &&
                data[payloadOffset + 3] == (byte)'b' &&
                data[payloadOffset + 4] == (byte)'e')
            {
                colorTransform = data[payloadOffset + 11] switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 1,
                    _ => -1
                };
                return colorTransform >= 0;
            }

            offset = nextOffset;
        }

        return false;
    }

    private static string NormalizeColorSpaceName(string colorSpace)
        => colorSpace switch
        {
            "RGB" => "DeviceRGB",
            "CMYK" => "DeviceCMYK",
            _ => colorSpace
        };

    private static int ChooseScaleDenominator(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        foreach (var denominator in new[] { 8, 4, 2 })
        {
            if ((sourceWidth + denominator - 1) / denominator >= targetWidth &&
                (sourceHeight + denominator - 1) / denominator >= targetHeight)
            {
                return denominator;
            }
        }

        return 1;
    }

    private static SKBitmap? Resize(SKBitmap bitmap, int targetWidth, int targetHeight)
    {
        if (bitmap.Width == targetWidth && bitmap.Height == targetHeight)
            return bitmap;

        try
        {
            return bitmap.Resize(
                new SKImageInfo(
                    targetWidth,
                    targetHeight,
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        finally
        {
            bitmap.Dispose();
        }
    }
}

internal readonly record struct DctImageDecodeRequest(
    byte[] Bytes,
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight,
    string ColorSpaceName,
    int ColorTransform,
    PdfColorSpace? ResolvedColorSpace,
    double[]? DecodeArray,
    int[]? ColorKeyMask,
    CancellationToken CancellationToken);
