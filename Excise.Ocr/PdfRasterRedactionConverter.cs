using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Ocr;

/// <summary>Fail-closed image-only PDF redaction for scanned/image-baked text (#1186).</summary>
public sealed class PdfRasterRedactionConverter
{
    private readonly PdfOcrService _ocr;
    private readonly int _dpi;
    public PdfRasterRedactionConverter(PdfOcrService ocr, int dpi = 300)
    { _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr)); _dpi = dpi; }

    /// <summary>
    /// Render every source page into a new image-only document, paint every OCR
    /// match black, and preserve source encryption unless explicitly opted out.
    /// </summary>
    public int RedactToImageOnly(
        string inputPath, string outputPath, string term,
        bool caseSensitive = false, string? password = null, bool allowDecrypt = false,
        Action<int, int>? progress = null)
    {
        if (!_ocr.IsAvailable()) throw new InvalidOperationException("Image-only redaction requires tesseract.");
        ArgumentException.ThrowIfNullOrEmpty(term);
        using var source = PdfDocument.Open(File.ReadAllBytes(inputPath), password);
        var reEncryption = allowDecrypt ? null : source.GetReEncryptionOptions(password);
        using var output = PdfDocument.CreateNew(source.Version);
        var renderer = new SkiaRenderer(); int total = 0;
        progress?.Invoke(0, source.PageCount);
        for (var p = 1; p <= source.PageCount; p++)
        {
            var page = source.GetPage(p);
            using var bitmap = renderer.RenderPage(page, new RenderOptions { Dpi = _dpi });
            var matches = FindMatches(_ocr.RecognizeBitmap(bitmap, page).Words, term, caseSensitive);
            foreach (var match in matches)
                foreach (var word in match)
                    Paint(bitmap, PdfCoordinateMapper.ToViewerDips(page, PdfPageRect.FromContentPoints(p, word.BoundingBox), _dpi));
            PdfRasterPageAuthoring.AddRgbRasterPage(
                output, ToRgb(bitmap), bitmap.Width, bitmap.Height, page.VisualWidth, page.VisualHeight);
            total += matches.Count;
            progress?.Invoke(p, source.PageCount);
        }
        if (total == 0)
            throw new InvalidOperationException($"OCR could not locate '{term}' on any page; no output was written.");
        output.Save(outputPath, reEncryption);
        return total;
    }

    /// <summary>
    /// Find the requested phrase in Tesseract's TSV reading order. A phrase
    /// match is counted once, but every word rectangle in it is painted. This
    /// deliberately does not use substring matching: requesting <c>SECRET</c>
    /// must not black out an unrelated OCR word such as <c>SECRETION</c>.
    /// </summary>
    private static List<IReadOnlyList<OcrWord>> FindMatches(
        IReadOnlyList<OcrWord> words, string term, bool caseSensitive)
    {
        var requested = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<IReadOnlyList<OcrWord>>();
        if (requested.Length == 0) return result;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var first = 0; first <= words.Count - requested.Length; first++)
        {
            if (!string.Equals(words[first].Text, requested[0], comparison)) continue;
            var matched = true;
            for (var offset = 1; offset < requested.Length; offset++)
            {
                if (!string.Equals(words[first + offset].Text, requested[offset], comparison))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) result.Add(words.Skip(first).Take(requested.Length).ToArray());
        }
        return result;
    }

    private static void Paint(SKBitmap bitmap, PdfPageRect pixels)
    {
        int l = Math.Max(0, (int)Math.Floor(pixels.X) - 2), r = Math.Min(bitmap.Width, (int)Math.Ceiling(pixels.Right) + 2);
        int t = Math.Max(0, (int)Math.Floor(pixels.Y) - 2), b = Math.Min(bitmap.Height, (int)Math.Ceiling(pixels.Y2) + 2);
        using var canvas = new SKCanvas(bitmap); canvas.DrawRect(l, t, r - l, b - t, new SKPaint { Color = SKColors.Black });
    }

    private static byte[] ToRgb(SKBitmap bitmap)
    {
        var rgb = new byte[bitmap.Width * bitmap.Height * 3]; int i = 0;
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
        { var c = bitmap.GetPixel(x, y); rgb[i++] = c.Red; rgb[i++] = c.Green; rgb[i++] = c.Blue; }
        return rgb;
    }
}
