using BitMiracle.LibJpeg.Classic;
using Excise.Core.Writing;

namespace Excise.Rendering;

/// <summary>
/// Image codecs Excise.Core cannot carry itself because it has no JPEG
/// implementation (#1550).
/// </summary>
public static class PdfImageCodecs
{
    /// <summary>
    /// The JPEG codec Reduce File Size uses to decode and re-encode
    /// <c>/DCTDecode</c> images. Managed (LibJpeg.NET), so it does not touch
    /// SkiaSharp's process-wide native state.
    /// </summary>
    public static IPdfJpegCodec CreateJpegCodec() => LibJpegCodec.Instance;
}

/// <summary>
/// <see cref="IPdfJpegCodec"/> over BitMiracle LibJpeg.NET, the library
/// <see cref="DctImageDecoder"/> already decodes PDF JPEGs with.
/// </summary>
internal sealed class LibJpegCodec : IPdfJpegCodec
{
    internal static readonly LibJpegCodec Instance = new();

    private LibJpegCodec()
    {
    }

    public byte[]? Decode(byte[] jpeg, int width, int height, int components, int? colorTransform)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length == 0 || width <= 0 || height <= 0 || components is not (1 or 3))
            return null;

        var decompressor = new jpeg_decompress_struct();
        try
        {
            using var input = new MemoryStream(jpeg, writable: false);
            decompressor.jpeg_stdio_src(input);
            decompressor.jpeg_read_header(true);

            // The codestream must describe exactly the image the dictionary
            // does. A disagreement is not something to paper over here: the
            // optimizer leaves such an image untouched.
            if (decompressor.Image_width != width
                || decompressor.Image_height != height
                || decompressor.Num_components != components)
                return null;

            if (components == 1)
            {
                decompressor.Out_color_space = J_COLOR_SPACE.JCS_GRAYSCALE;
            }
            else
            {
                // Same precedence the renderer applies (Adobe APP14, then
                // /DecodeParms /ColorTransform). No answer leaves libjpeg's
                // own JFIF/component-id guess in place.
                switch (DctImageDecoder.ResolveColorTransform(jpeg, "DeviceRGB", colorTransform))
                {
                    case 0:
                        decompressor.Jpeg_color_space = J_COLOR_SPACE.JCS_RGB;
                        break;
                    case 1:
                        decompressor.Jpeg_color_space = J_COLOR_SPACE.JCS_YCbCr;
                        break;
                }

                decompressor.Out_color_space = J_COLOR_SPACE.JCS_RGB;
            }

            decompressor.jpeg_start_decompress();
            if (decompressor.Output_width != width
                || decompressor.Output_height != height
                || decompressor.Output_components != components)
                return null;

            var stride = checked(width * components);
            var samples = new byte[checked((long)stride * height)];
            var scanline = new[] { new byte[stride] };
            var offset = 0L;
            while (decompressor.Output_scanline < decompressor.Output_height)
            {
                if (decompressor.jpeg_read_scanlines(scanline, 1) != 1)
                    return null;
                Array.Copy(scanline[0], 0, samples, offset, stride);
                offset += stride;
            }

            decompressor.jpeg_finish_decompress();
            return offset == samples.LongLength ? samples : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            try
            {
                decompressor.jpeg_destroy();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Cleanup after a malformed codestream can fail too.
            }
        }
    }

    public byte[]? Encode(byte[] samples, int width, int height, int components, int quality)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (width <= 0 || height <= 0 || components is not (1 or 3)
            || samples.LongLength != (long)width * height * components)
            return null;

        var compressor = new jpeg_compress_struct();
        try
        {
            using var output = new MemoryStream();
            compressor.jpeg_stdio_dest(output);
            compressor.Image_width = width;
            compressor.Image_height = height;
            compressor.Input_components = components;
            compressor.In_color_space = components == 1
                ? J_COLOR_SPACE.JCS_GRAYSCALE
                : J_COLOR_SPACE.JCS_RGB;
            compressor.jpeg_set_defaults();
            compressor.jpeg_set_quality(Math.Clamp(quality, 1, 100), true);
            compressor.Optimize_coding = true;
            compressor.jpeg_start_compress(true);

            var stride = width * components;
            var scanline = new[] { new byte[stride] };
            var offset = 0L;
            while (compressor.Next_scanline < height)
            {
                Array.Copy(samples, offset, scanline[0], 0, stride);
                compressor.jpeg_write_scanlines(scanline, 1);
                offset += stride;
            }

            compressor.jpeg_finish_compress();
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            try
            {
                compressor.jpeg_destroy();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing to recover.
            }
        }
    }
}
