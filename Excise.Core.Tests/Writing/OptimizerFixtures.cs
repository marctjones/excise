using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// Hand-assembled PDFs for the Reduce File Size tests (#1550). Built byte by
/// byte so each one carries exactly the shape under test — an uncompressed
/// content stream, a thumbnail, two identical images — rather than whatever
/// excise's own writer would produce.
/// </summary>
internal static class OptimizerFixtures
{
    internal const string Term = "Farrar";

    /// <summary>
    /// Object bodies by number (1-based). A body is either PDF syntax, or
    /// syntax plus stream bytes.
    /// </summary>
    internal sealed class MiniPdf
    {
        private readonly List<(string Dictionary, byte[]? Stream)> _objects = new();

        public int Add(string dictionary, byte[]? stream = null)
        {
            _objects.Add((dictionary, stream));
            return _objects.Count;
        }

        public void Set(int number, string dictionary, byte[]? stream = null)
            => _objects[number - 1] = (dictionary, stream);

        public int Reserve() => Add("null");

        public byte[] Build(int root, string trailerExtra = "")
        {
            using var file = new MemoryStream();
            void Ascii(string s)
            {
                var b = Encoding.Latin1.GetBytes(s);
                file.Write(b, 0, b.Length);
            }

            Ascii("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
            var offsets = new long[_objects.Count + 1];
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets[i + 1] = file.Position;
                var (dictionary, stream) = _objects[i];
                Ascii($"{i + 1} 0 obj\n");
                if (stream == null)
                {
                    Ascii(dictionary);
                }
                else
                {
                    // Dictionary text ends with ">>"; splice /Length in.
                    Ascii(dictionary.Substring(0, dictionary.LastIndexOf(">>", StringComparison.Ordinal)));
                    Ascii($" /Length {stream.Length} >>\nstream\n");
                    file.Write(stream, 0, stream.Length);
                    Ascii("\nendstream");
                }

                Ascii("\nendobj\n");
            }

            var xref = file.Position;
            Ascii($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            for (var i = 1; i <= _objects.Count; i++)
                Ascii($"{offsets[i]:D10} 00000 n \n");
            Ascii($"trailer\n<< /Size {_objects.Count + 1} /Root {root} 0 R {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
            return file.ToArray();
        }
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    internal static byte[] Inflate(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>60 lines of text, <see cref="Term"/> on every fifth.</summary>
    internal static byte[] TextContent(string term = Term)
    {
        var content = new StringBuilder("BT /F1 10 Tf 12 TL 40 760 Td\n");
        for (var i = 0; i < 60; i++)
        {
            var name = i % 5 == 0 ? $"Louise Anne {term}" : "the applicant";
            content.Append(CultureInfoInvariant($"(Line {i}: this declaration was signed by {name} before the clerk.) Tj T*\n"));
        }

        content.Append("ET\n");
        return Encoding.Latin1.GetBytes(content.ToString());
    }

    private static string CultureInfoInvariant(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>
    /// A text page whose content stream is stored UNCOMPRESSED, with a page
    /// thumbnail and <c>/PieceInfo</c> on the page and the catalog — all three
    /// things Lossless should remove or shrink.
    /// </summary>
    internal static byte[] UncompressedTextPageWithThumbnail(bool compressContent = false)
    {
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var content = TextContent();
        var contents = compressContent
            ? pdf.Add("<< /Filter /FlateDecode >>", Deflate(content))
            : pdf.Add("<< >>", content);
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var thumbPixels = Enumerable.Range(0, 20 * 20 * 3).Select(i => (byte)(i * 7)).ToArray();
        var thumb = pdf.Add("<< /Width 20 /Height 20 /ColorSpace /DeviceRGB /BitsPerComponent 8 >>", thumbPixels);
        var piece = pdf.Add("<< /Illustrator << /LastModified (D:20260101) /Private (editing data) >> >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /PieceInfo {piece} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {contents} 0 R " +
            $"/Thumb {thumb} 0 R /PieceInfo {piece} 0 R /LastModified (D:20260101) >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// Deterministic RGB (or gray) samples with enough structure that Flate
    /// cannot squeeze them to nothing.
    /// </summary>
    internal static byte[] NoisySamples(int width, int height, int components, int seed = 1550)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * components];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < components; c++)
                {
                    var smooth = (x * (c + 1) + y * (3 - c)) & 0xFF;
                    samples[(y * width + x) * components + c] = (byte)((smooth + random.Next(-24, 25)) & 0xFF);
                }
            }
        }

        return samples;
    }

    /// <summary>
    /// One page drawing <paramref name="placements"/> Do operators of a single
    /// Flate RGB image, each placement a square of the given size in points.
    /// With <paramref name="viaForm"/> the image is drawn from inside a form
    /// XObject instead, where the optimizer cannot measure it.
    /// </summary>
    internal static byte[] ImagePage(
        int pixels = 600,
        double[]? placements = null,
        bool viaForm = false,
        string extraImageEntries = "")
    {
        placements ??= new[] { 72.0 };
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var image = pdf.Add(
            $"<< /Type /XObject /Subtype /Image /Width {pixels} /Height {pixels} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode {extraImageEntries}>>",
            Deflate(NoisySamples(pixels, pixels, 3)));

        var draw = new StringBuilder();
        var y = 700.0;
        foreach (var size in placements)
        {
            draw.Append(FormattableString.Invariant($"q {size} 0 0 {size} 40 {y - size} cm /Im1 Do Q\n"));
            y -= size + 10;
        }

        string resources;
        byte[] pageContent;
        if (viaForm)
        {
            var form = pdf.Add(
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                $"/Resources << /XObject << /Im1 {image} 0 R >> >> >>",
                Encoding.Latin1.GetBytes(draw.ToString()));
            resources = $"<< /XObject << /Fm1 {form} 0 R >> >>";
            pageContent = Encoding.Latin1.GetBytes("/Fm1 Do\n");
        }
        else
        {
            resources = $"<< /XObject << /Im1 {image} 0 R >> >>";
            pageContent = Encoding.Latin1.GetBytes(draw.ToString());
        }

        var contents = pdf.Add("<< >>", pageContent);
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources {resources} /Contents {contents} 0 R >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// Two separately stored images with the same samples. With
    /// <paramref name="secondDecode"/> the second one carries a
    /// <c>/Decode</c> array that inverts it — same bytes, different picture.
    /// </summary>
    internal static byte[] TwoIdenticalImages(string? secondDecode = null)
    {
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var samples = Deflate(NoisySamples(64, 64, 3));
        const string common = "/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode";
        var first = pdf.Add($"<< {common} >>", samples);
        var second = pdf.Add(
            secondDecode == null ? $"<< {common} >>" : $"<< {common} /Decode {secondDecode} >>",
            samples);
        var contents = pdf.Add("<< >>", Encoding.Latin1.GetBytes(
            "q 100 0 0 100 40 600 cm /ImA Do Q\nq 100 0 0 100 200 600 cm /ImB Do Q\n"));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /XObject << /ImA {first} 0 R /ImB {second} 0 R >> >> /Contents {contents} 0 R >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// A text page carrying <see cref="Term"/>, plus an UNREACHABLE object in
    /// the file that also carries it — the kind of leftover a prior edit
    /// leaves behind.
    /// </summary>
    internal static byte[] TextPageWithOrphanCarryingTerm()
    {
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var contents = pdf.Add("<< /Filter /FlateDecode >>", Deflate(TextContent()));
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        pdf.Add("<< /Filter /FlateDecode >>", Deflate(Encoding.Latin1.GetBytes(
            $"BT /F1 10 Tf 40 40 Td (orphaned copy of {Term}) Tj ET\n")));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {contents} 0 R >>");
        return pdf.Build(catalog);
    }
}
