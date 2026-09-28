using System.Buffers.Binary;
using Excise.Core.Document;
using Excise.Core.Filters;
using Excise.Core.Primitives;

namespace Excise.Core.Graphics;

/// <summary>
/// Image XObjects (ISO 32000-2 §8.9.5) written into a document: the one builder behind
/// <see cref="PdfGraphics.DrawImage"/>, image stamps and raster pages. JPEG passes through
/// as <c>/DCTDecode</c>; PNG is inflated with the document's own Flate and PNG-predictor
/// decoders and stored lossless, its alpha as an <c>/SMask</c>. No image codec dependency.
/// </summary>
internal static class PdfImageXObject
{
    /// <summary>Largest decoded PNG accepted, in pixels.</summary>
    private const long MaxPixels = 40_000_000;

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Add a lossless DeviceRGB image from top-down RGB24 pixels, with an <c>/SMask</c> from
    /// <paramref name="alpha"/> (one byte per pixel, 0 transparent) unless every pixel is opaque.
    /// </summary>
    internal static PdfReference AddRgb(PdfDocument document, byte[] rgb, int width, int height, byte[]? alpha)
    {
        var image = NewImage(PdfStream.CreateCompressed(rgb), width, height, "DeviceRGB"); // #1549: lossless

        // A soft mask is its own DeviceGray image of the same size: the reader multiplies the
        // picture's opacity by it. Skipped when every pixel is opaque, which is the common case
        // for a photograph and saves a whole extra image.
        if (alpha != null && alpha.Any(a => a != 255))
            image["SMask"] = document.AddIndirectObject(NewImage(PdfStream.CreateCompressed(alpha), width, height, "DeviceGray"));
        return document.AddIndirectObject(image);
    }

    /// <summary>Add a JPEG or PNG file's image.</summary>
    /// <exception cref="ArgumentException">The bytes are not a JPEG or PNG this can embed faithfully.</exception>
    internal static PdfReference AddEncoded(PdfDocument document, byte[] imageBytes)
    {
        if (imageBytes.Length > 2 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
            return document.AddIndirectObject(Jpeg(imageBytes));
        if (imageBytes.AsSpan().StartsWith(PngSignature))
            return AddPng(document, imageBytes);
        throw Refuse("is neither a JPEG nor a PNG");
    }

    private static PdfStream NewImage(PdfStream image, int width, int height, string colorSpace)
    {
        image.SetName("Type", "XObject");
        image.SetName("Subtype", "Image");
        image.SetInt("Width", width);
        image.SetInt("Height", height);
        image.SetName("ColorSpace", colorSpace);
        image.SetInt("BitsPerComponent", 8);
        return image;
    }

    private static ArgumentException Refuse(string why) => new($"The image {why}.", "imageBytes");

    /// <summary>
    /// The JPEG itself as a <c>/DCTDecode</c> image: only the frame header is read, for the size
    /// and component count. Baseline, extended and progressive 8-bit gray or YCbCr frames only.
    /// </summary>
    private static PdfStream Jpeg(byte[] jpeg)
    {
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

                var image = new PdfStream();
                image.ReplaceEncoding(jpeg, jpeg, "DCTDecode");
                return NewImage(image, width, height, components == 1 ? "DeviceGray" : "DeviceRGB");
            }
            pos += 2 + length;
        }
        throw Refuse("is a JPEG with no frame header");
    }

    /// <summary>
    /// Decode a non-interlaced PNG of 8 or 16 bits per sample (palette: 8) to RGB and alpha:
    /// the IDAT stream is zlib and its rows use the PNG filters, which are exactly FlateDecode
    /// with <c>/Predictor 15</c> (§7.4.4.4). 16-bit samples keep their high byte.
    /// </summary>
    private static PdfReference AddPng(PdfDocument document, byte[] png)
    {
        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[] palette = [], transparency = [];
        using var idat = new MemoryStream();
        for (int pos = PngSignature.Length; pos + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            if (length < 0 || pos + 12L + length > png.Length)
                throw Refuse("is a truncated PNG");
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            pos += 12 + length;
            if (type == "IHDR" && length >= 13)
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                (depth, colorType, interlace) = (data[8], data[9], data[12]);
            }
            else if (type == "PLTE") palette = data.ToArray();
            else if (type == "tRNS") transparency = data.ToArray();
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw Refuse($"is a PNG of {width} x {height} pixels");
        if (channels == 0 || interlace != 0 || !(depth == 8 || (depth == 16 && colorType != 3)))
            throw Refuse($"is a PNG of colour type {colorType}, {depth} bits, interlace {interlace}; only non-interlaced 8-bit (or 16-bit non-palette) PNGs are supported");

        int bytesPerSample = depth / 8, bytesPerPixel = channels * bytesPerSample;
        long filtered = height * ((long)width * bytesPerPixel + 1);
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 15);
        parms.SetInt("Colors", channels);
        parms.SetInt("BitsPerComponent", depth);
        parms.SetInt("Columns", width);
        byte[] inflated;
        try
        {
            inflated = FlateFilterDecoder.DecodeFlateData(idat.ToArray(), filtered, filtered);
        }
        catch (Parsing.PdfParseException)
        {
            throw Refuse("is a PNG whose image data is corrupt or larger than its header says");
        }
        if (inflated.LongLength != filtered)
            throw Refuse("is a PNG whose image data is truncated");
        var samples = PdfPredictor.ApplyIfNeeded(inflated, parms);

        long count = (long)width * height;
        var rgb = new byte[count * 3];
        var alpha = new byte[count];
        for (long i = 0; i < count; i++)
        {
            long at = i * bytesPerPixel;
            byte Sample(int c) => samples[at + c * bytesPerSample];
            // tRNS for gray or RGB names one colour, as 16-bit samples, that is fully transparent.
            bool KeyedTransparent()
            {
                if (transparency.Length < 2 * channels)
                    return false;
                for (int c = 0; c < channels; c++)
                {
                    int value = bytesPerSample == 1 ? samples[at + c] : samples[at + 2 * c] << 8 | samples[at + 2 * c + 1];
                    if (value != (transparency[2 * c] << 8 | transparency[2 * c + 1]))
                        return false;
                }
                return true;
            }

            byte r, g, b, a;
            switch (colorType)
            {
                case 3:
                    int index = samples[at];
                    if (3 * index + 2 >= palette.Length)
                        throw Refuse("is a PNG whose pixels index past its palette");
                    (r, g, b) = (palette[3 * index], palette[3 * index + 1], palette[3 * index + 2]);
                    a = index < transparency.Length ? transparency[index] : (byte)255;
                    break;
                case 0 or 4:
                    r = g = b = Sample(0);
                    a = colorType == 4 ? Sample(1) : KeyedTransparent() ? (byte)0 : (byte)255;
                    break;
                default:
                    (r, g, b) = (Sample(0), Sample(1), Sample(2));
                    a = colorType == 6 ? Sample(3) : KeyedTransparent() ? (byte)0 : (byte)255;
                    break;
            }
            (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2], alpha[i]) = (r, g, b, a);
        }
        return AddRgb(document, rgb, width, height, alpha);
    }
}
