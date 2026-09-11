using System.Runtime.ExceptionServices;
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
    // #1208: below either bound the per-row conversion stays serial, where the
    // cost of scheduling bands would dominate the work they split.
    private const int MinimumParallelRows = 256;
    private const long MinimumParallelPixels = 1_000_000;
    private const int BandsPerProcessor = 4;

    public static SKBitmap? Decode(RawSampleImageDecodeRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.ColorSpace == null || request.Width <= 0 || request.Height <= 0)
        {
            return null;
        }

        // #1470: an Indexed /Decode of exactly [0, 2^bpc - 1] is the spec
        // default and a provable no-op here: DecodeImageSample maps it to
        // 0 + sample * ((2^bpc - 1) / (2^bpc - 1)), and that quotient is
        // exactly 1.0 in IEEE-754, so the decoded value IS the sample. Its mere
        // presence used to disable the 256-entry palette table below (the
        // converter is only built when DecodeArray == null), sending every
        // pixel through PdfColorSpace.ToRgb instead. Both routes resolve
        // LookupIndexed((int)Math.Round(sample)) and truncate x*255 the same
        // way, so dropping the array cannot change a pixel.
        //
        // Indexed ONLY. For continuous spaces the default path normalizes via
        // sample * (255.0 / maxSample) and DecodeSampleByte, while the Decode
        // path computes sample * (1.0 / maxSample) — those can differ in the
        // last bit, so an identity array there is NOT provably a no-op.
        if (IsIdentityIndexedDecodeArray(request.ColorSpace, request.BitsPerComponent, request.DecodeArray))
        {
            request = request with { DecodeArray = null };
        }

        // #1403: decode cost is otherwise proportional to the SOURCE pixel
        // grid, not to what the target device space can actually show. A
        // caller (SkiaRenderer.Images.cs) that knows the device-space draw
        // size passes it as TargetWidth/TargetHeight; when that is smaller
        // than the source grid, DecodeGeneral samples the source on a
        // nearest-neighbor grid instead of decoding every source pixel. The
        // request-side clamp (EstimateImageDecodeSize) never asks for a
        // target larger than the source, so upscaling always sees full
        // source detail via the unclamped `subsample = false` path below.
        var targetWidth = request.TargetWidth is > 0 ? Math.Min(request.TargetWidth.Value, request.Width) : request.Width;
        var targetHeight = request.TargetHeight is > 0 ? Math.Min(request.TargetHeight.Value, request.Height) : request.Height;
        var subsample = targetWidth < request.Width || targetHeight < request.Height;

        try
        {
            if (!subsample && request.DecodeArray == null && request.ColorKeyMask == null)
            {
                var fastBitmap = TryDecodeFast(request);
                if (fastBitmap != null)
                    return fastBitmap;
            }

            return DecodeGeneral(request, targetWidth, targetHeight, subsample);
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

    /// <summary>
    /// True when <paramref name="decodeArray"/> is exactly the §8.9.5.2 default
    /// for an Indexed image, <c>[0, 2^bpc - 1]</c>, with bpc ≤ 8 (#1470). Exact
    /// comparison and exact length: any other array keeps the general path.
    /// </summary>
    internal static bool IsIdentityIndexedDecodeArray(
        PdfColorSpace? colorSpace,
        int bitsPerComponent,
        double[]? decodeArray)
    {
        if (decodeArray is not { Length: 2 } ||
            colorSpace?.Type != PdfColorSpaceType.Indexed ||
            bitsPerComponent is < 1 or > 8)
        {
            return false;
        }

        return decodeArray[0] == 0.0 && decodeArray[1] == (1 << bitsPerComponent) - 1;
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
                CreateCmykBitmap(request.Samples, request.Width, request.Height, request.ColorSpace, request.CancellationToken),
            // #1208: ICCBased N=4 reaches exactly the same converter call here
            // as in DecodeGeneral. There, at 8 bpc with no /Decode, a sample
            // is normalised as (byte)Round(sample * (255.0 / 255.0)) — the
            // quotient is exactly 1.0, so that is the sample itself — and
            // DecodeSampleByte returns sample / 255.0 for every non-Lab space.
            // CreateCmykBitmap computes byte / 255.0 directly. Both then call
            // ImageColorConverter.ToRgb(ReadOnlySpan<double>) on the same
            // four doubles, which for a 4-component non-Lab space is always the
            // Continuous4DLattice (never null), so the bytes cannot differ.
            PdfColorSpaceType.ICCBased when request.ColorSpace.Components == 4 && request.ComponentsPerPixel == 4 =>
                CreateCmykBitmap(request.Samples, request.Width, request.Height, request.ColorSpace, request.CancellationToken),
            _ => null
        };
    }

    /// <summary>
    /// Decodes into a <paramref name="targetWidth"/> x <paramref name="targetHeight"/>
    /// bitmap. When <paramref name="subsample"/> is true, that target is smaller
    /// than the source sample grid (<see cref="RawSampleImageDecodeRequest.Width"/> x
    /// <see cref="RawSampleImageDecodeRequest.Height"/>): each destination pixel reads
    /// exactly one nearest-neighbor SOURCE pixel rather than every source pixel
    /// (#1403). Nearest-neighbor is deliberate, not just cheap: for an Indexed
    /// color space a sample is a PALETTE INDEX, and indices are not perceptually
    /// ordered, so averaging them (a box filter) would blend unrelated palette
    /// entries into a bogus index / an out-of-range value. Picking one real
    /// source sample and decoding it through the normal Decode-array /
    /// color-space / color-key path keeps every downsampled pixel a value that
    /// genuinely occurred in the source, for every color space uniformly.
    /// </summary>
    private static unsafe SKBitmap? DecodeGeneral(
        RawSampleImageDecodeRequest request,
        int targetWidth,
        int targetHeight,
        bool subsample)
    {
        var colorSpace = request.ColorSpace!;
        var componentsPerPixel = request.ComponentsPerPixel;
        if (componentsPerPixel <= 0)
            return null;

        var bitmap = new SKBitmap(
            targetWidth,
            targetHeight,
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
            var maxSample = Math.Pow(2, request.BitsPerComponent) - 1;
            var imageColorConverter = request.DecodeArray == null
                ? ImageColorConverter.For(colorSpace)
                : null;

            // Row stride of the SOURCE sample grid (not the target). Every
            // branch below addresses a source pixel by formula (row stride *
            // source row + source column), so a downsampled pass can jump
            // straight to the one source pixel it needs instead of walking
            // every sample in between.
            var sourceRowStrideBits = AlignBitsToByte(
                checked(request.Width * componentsPerPixel * request.BitsPerComponent));

            // The dedicated 1 bpc branch below has always read exactly ONE
            // sample per pixel regardless of ComponentsPerPixel (pre-#1403
            // behaviour, preserved as-is here) — so its row stride is per
            // PIXEL, not per component, and must not reuse the stride above.
            var singleBitRowStrideBits = AlignBitsToByte(request.Width);

            // #1208: rows only in parallel when every pixel converts through the
            // cached ImageColorConverter (a byte table or lattice, read-only
            // after construction). Without it ConvertPixel falls back to
            // PdfColorSpace.ToRgb, whose ICC profile and tint caches take a
            // lock per call, so parallel rows would serialise on it at best.
            var pixelAddress = bitmap.GetPixels();
            var pixelLength = pixels.Length;
            ForEachRowBand(
                targetHeight,
                targetWidth,
                allowParallel: imageColorConverter != null,
                request.CancellationToken,
                (startRow, endRow) => DecodeGeneralRows(
                    request,
                    colorSpace,
                    imageColorConverter,
                    maxSample,
                    sourceRowStrideBits,
                    singleBitRowStrideBits,
                    targetWidth,
                    targetHeight,
                    subsample,
                    new Span<byte>((void*)pixelAddress, pixelLength),
                    startRow,
                    endRow));
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

    /// <summary>
    /// Target rows <paramref name="startRow"/> (inclusive) to
    /// <paramref name="endRow"/> (exclusive) of <see cref="DecodeGeneral"/>.
    /// Each call owns its scratch arrays and writes only its own rows, so
    /// disjoint row ranges may run concurrently (#1208). Fresh scratch per call
    /// is equivalent to the old single pass: every component a pixel reads is
    /// written before it is used, and the components a branch never writes
    /// (the 1 bpc branch's [1..]) stay 0 in both.
    /// </summary>
    private static void DecodeGeneralRows(
        RawSampleImageDecodeRequest request,
        PdfColorSpace colorSpace,
        ImageColorConverter? imageColorConverter,
        double maxSample,
        int sourceRowStrideBits,
        int singleBitRowStrideBits,
        int targetWidth,
        int targetHeight,
        bool subsample,
        Span<byte> pixels,
        int startRow,
        int endRow)
    {
        var componentsPerPixel = request.ComponentsPerPixel;
        var destinationIndex = startRow * targetWidth * 4;
        var pixelValues = new double[componentsPerPixel];
        var rawSamples = request.ColorKeyMask != null
            ? new int[componentsPerPixel]
            : null;

        for (var ty = startRow; ty < endRow; ty++)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var sy = subsample ? MapTargetToSource(ty, targetHeight, request.Height) : ty;

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var sx = subsample ? MapTargetToSource(tx, targetWidth, request.Width) : tx;

                byte red = 0, green = 0, blue = 0, alpha = 255;
                var samplesRead = false;

                if (request.BitsPerComponent > 1)
                {
                    if (request.BitsPerComponent == 8)
                    {
                        // Rows are always byte-aligned at 8 bpc, so this is a
                        // direct formula, not an accumulated scan position.
                        var byteOffset = checked(
                            ((long)sy * request.Width * componentsPerPixel) +
                            ((long)sx * componentsPerPixel));
                        if (byteOffset + componentsPerPixel <= request.Samples.LongLength)
                        {
                            var baseOffset = (int)byteOffset;
                            for (var component = 0; component < componentsPerPixel; component++)
                            {
                                var sample = request.Samples[baseOffset + component];
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
                    else
                    {
                        var bitOffset = checked(
                            ((long)sy * sourceRowStrideBits) +
                            ((long)sx * componentsPerPixel * request.BitsPerComponent));
                        if (bitOffset + (componentsPerPixel * request.BitsPerComponent) <=
                            (long)request.Samples.Length * 8)
                        {
                            for (var component = 0; component < componentsPerPixel; component++)
                            {
                                var sample = ReadPackedImageSample(
                                    request.Samples,
                                    checked((int)(bitOffset + (component * request.BitsPerComponent))),
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
                    // Same row-stride formula as the packed-bit branch above,
                    // specialized to the single-component case (matches the
                    // pre-#1403 behaviour: a 1 bpc pixel here is always read
                    // as one sample, regardless of ComponentsPerPixel).
                    var bitOffset = checked(((long)sy * singleBitRowStrideBits) + sx);
                    var byteIndex = bitOffset / 8;
                    var bitIndex = 7 - (int)(bitOffset % 8);
                    var sample = byteIndex < request.Samples.LongLength
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
        }
    }

    private delegate void RowRangeAction(int startRow, int endRow);

    /// <summary>
    /// Runs <paramref name="rows"/> as [start, end) row bands (#1208): a single
    /// serial call below <see cref="MinimumParallelRows"/> rows or
    /// <see cref="MinimumParallelPixels"/> pixels, or when
    /// <paramref name="allowParallel"/> is false; otherwise bands of equal
    /// height via Parallel.For, capped at <see cref="Environment.ProcessorCount"/>
    /// workers.
    /// <para>
    /// The caller's thread executes bands itself, so a saturated thread pool —
    /// for instance several GUI page bands decoding at once, each asking for
    /// its own workers — slows this down but cannot deadlock it; the pool's
    /// slow thread injection queues the surplus rather than oversubscribing.
    /// </para>
    /// <para>
    /// Parallel.For wraps a band's exception in an AggregateException. It is
    /// unwrapped here so cancellation still surfaces as
    /// <see cref="OperationCanceledException"/> (the decoders' catch-all would
    /// otherwise report a cancelled decode as a malformed image) and any other
    /// failure reaches that catch-all as it did from the serial loop.
    /// </para>
    /// </summary>
    private static void ForEachRowBand(
        int rows,
        int columns,
        bool allowParallel,
        CancellationToken cancellationToken,
        RowRangeAction action)
    {
        var processors = Environment.ProcessorCount;
        if (!allowParallel ||
            processors < 2 ||
            rows < MinimumParallelRows ||
            (long)rows * columns < MinimumParallelPixels)
        {
            action(0, rows);
            return;
        }

        var rowsPerBand = (rows + (processors * BandsPerProcessor) - 1) / (processors * BandsPerProcessor);
        var bandCount = (rows + rowsPerBand - 1) / rowsPerBand;
        try
        {
            Parallel.For(
                0,
                bandCount,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = processors,
                    CancellationToken = cancellationToken,
                },
                band =>
                {
                    var startRow = band * rowsPerBand;
                    action(startRow, Math.Min(rows, startRow + rowsPerBand));
                });
        }
        catch (AggregateException aggregate)
        {
            var failures = aggregate.Flatten().InnerExceptions;
            var failure = failures.FirstOrDefault(static e => e is OperationCanceledException) ?? failures[0];
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
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

    /// <summary>
    /// Nearest-neighbor source coordinate for a target coordinate, same
    /// formula as the sibling copies in SkiaRenderer.Images.cs and
    /// JpxImageDecoder.cs (there is no shared home for it across the three
    /// decode paths; keep this in sync if the mapping ever changes there).
    /// </summary>
    private static int MapTargetToSource(int targetPosition, int targetSize, int sourceSize)
        => Math.Clamp((int)(((targetPosition + 0.5) * sourceSize) / targetSize), 0, sourceSize - 1);

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

    private static unsafe SKBitmap? CreateCmykBitmap(
        byte[] data,
        int width,
        int height,
        PdfColorSpace colorSpace,
        CancellationToken cancellationToken)
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

        // #1208: the converter is a lattice read-only after construction, so
        // disjoint row bands may convert concurrently (see ForEachRowBand).
        var pixelAddress = bitmap.GetPixels();
        var pixelLength = pixels.Length;
        try
        {
            ForEachRowBand(
                height,
                width,
                allowParallel: true,
                cancellationToken,
                (startRow, endRow) => ConvertCmykRows(
                    converter,
                    data,
                    width,
                    new Span<byte>((void*)pixelAddress, pixelLength),
                    startRow,
                    endRow,
                    cancellationToken));
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        return bitmap;
    }

    /// <summary>
    /// Rows <paramref name="startRow"/> (inclusive) to <paramref name="endRow"/>
    /// (exclusive) of <see cref="CreateCmykBitmap"/>: the same per-pixel
    /// arithmetic as the single pass it replaced, starting at the offsets that
    /// pass had reached by <paramref name="startRow"/>.
    /// </summary>
    private static void ConvertCmykRows(
        ImageColorConverter converter,
        byte[] data,
        int width,
        Span<byte> pixels,
        int startRow,
        int endRow,
        CancellationToken cancellationToken)
    {
        var src = startRow * width * 4;
        var dst = startRow * width * 4;
        for (var row = startRow; row < endRow; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < width; column++)
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
        }
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
    CancellationToken CancellationToken = default,
    // #1403: device-space draw size, when the caller knows it. Null (or >=
    // Width/Height) means "decode at full source resolution" — the default,
    // and always what an unclamped/unknown target gets. A caller passing a
    // smaller value is asking for a downsampled decode; RawSampleImageDecoder
    // still clamps it to the source size itself, so passing a too-large value
    // by mistake can never upscale past source detail.
    int? TargetWidth = null,
    int? TargetHeight = null);

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
