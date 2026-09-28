using System.Buffers.Binary;
using System.Security.Cryptography;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Graphics;

/// <summary>
/// An image for <see cref="PdfGraphics.DrawImage"/>: decoded RGB pixels, or a JPEG embedded
/// as is. Excise.Core decodes no image file format; the caller does.
/// </summary>
public sealed class PdfImage
{
    private readonly byte[] _data;
    private readonly byte[]? _alpha;
    private readonly string? _jpegColorSpace;

    private PdfImage(int width, int height, byte[] data, byte[]? alpha, string? jpegColorSpace)
    {
        (Width, Height, _data, _alpha, _jpegColorSpace) = (width, height, data, alpha, jpegColorSpace);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[9];
        header[0] = jpegColorSpace == null ? (byte)(alpha == null ? 0 : 1) : (byte)2;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], width);
        BinaryPrimitives.WriteInt32BigEndian(header[5..], height);
        hash.AppendData(header);
        hash.AppendData(data);
        if (alpha != null)
            hash.AppendData(alpha);
        Identity = Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>SHA-256 of the kind, size, pixels or JPEG bytes, and alpha: equal images share one XObject.</summary>
    internal string Identity { get; }

    /// <summary>
    /// An image from decoded pixels, stored lossless. Decode PNG, BMP and other formats with
    /// your own decoder, for example SkiaSharp, and pass the pixels.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="rgb">Three bytes (R, G, B) per pixel, rows top to bottom.</param>
    /// <param name="alpha">Optional opacity, one byte per pixel in the same order, 0 transparent;
    /// kept as a soft mask unless every pixel is opaque.</param>
    /// <exception cref="ArgumentException">A size is not positive or an array length does not match it.</exception>
    public static PdfImage FromRgb(int width, int height, byte[] rgb, byte[]? alpha = null)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"The image size {width} x {height} is not positive.", width <= 0 ? nameof(width) : nameof(height));
        long pixels = (long)width * height;
        if (rgb.LongLength != pixels * 3)
            throw new ArgumentException($"A {width} x {height} image needs {pixels * 3} RGB bytes, not {rgb.Length}.", nameof(rgb));
        if (alpha != null && alpha.LongLength != pixels)
            throw new ArgumentException($"A {width} x {height} image needs {pixels} alpha bytes, not {alpha.Length}.", nameof(alpha));
        return new PdfImage(width, height, (byte[])rgb.Clone(), (byte[]?)alpha?.Clone(), null);
    }

    /// <summary>
    /// A JPEG embedded untouched as <c>/DCTDecode</c>: only the frame header is read, for the
    /// size and component count.
    /// </summary>
    /// <param name="jpeg">A JPEG file: baseline, extended or progressive, 8-bit gray or colour.</param>
    /// <exception cref="ArgumentException">The bytes are not such a JPEG (CMYK, 12-bit, lossless and others are refused).</exception>
    public static PdfImage FromJpeg(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length <= 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw Refuse("is not a JPEG");
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                throw Refuse("is a malformed JPEG");
            byte marker = jpeg[pos + 1];
            if (marker is 0xFF)
            {
                pos++;
                continue;
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                pos += 2;
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (marker > 0xC2 || pos + 10 > jpeg.Length)
                    throw Refuse("is a JPEG coding PDF readers do not decode (lossless, hierarchical or arithmetic)");
                int precision = jpeg[pos + 4], components = jpeg[pos + 9];
                int height = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 5));
                int width = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 7));
                if (precision != 8 || width == 0 || height == 0 || components is not (1 or 3))
                    throw Refuse($"is a {precision}-bit, {components}-component JPEG; only 8-bit gray or colour with its size in the frame header is supported");
                return new PdfImage(width, height, (byte[])jpeg.Clone(), null, components == 1 ? "DeviceGray" : "DeviceRGB");
            }
            pos += 2 + length;
        }
        throw Refuse("is a JPEG with no frame header");
    }

    private static ArgumentException Refuse(string why) => new($"The image {why}.", "jpeg");

    /// <summary>Add this image to <paramref name="document"/> as a new image XObject.</summary>
    internal PdfReference AddTo(PdfDocument document)
    {
        if (_jpegColorSpace == null)
            return PdfImageXObject.AddRgb(document, _data, Width, Height, _alpha);
        var image = new PdfStream();
        image.ReplaceEncoding(_data, _data, "DCTDecode");
        return document.AddIndirectObject(PdfImageXObject.NewImage(image, Width, Height, _jpegColorSpace));
    }
}
