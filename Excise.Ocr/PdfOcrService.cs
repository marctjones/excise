using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Ocr;

/// <summary>
/// OCR a PDF page via the system <c>tesseract</c> CLI, using
/// <see cref="SkiaRenderer"/> to rasterize pages to PNG.
/// </summary>
/// <remarks>
/// <para>
/// We shell out to the <c>tesseract</c> binary rather than binding to
/// libtesseract via P/Invoke. The library-binding route runs into
/// native-lib version pinning headaches on Linux (Tesseract.Net 5.2.0
/// pins <c>libleptonica-1.82.0</c>, which newer distros don't ship).
/// Shelling out is portable: any system with <c>apt install tesseract-ocr</c>
/// (or equivalent) works.
/// </para>
/// <para>
/// Call <see cref="IsAvailable"/> to check for the binary before use.
/// </para>
/// </remarks>
public sealed class PdfOcrService
{
    private readonly int _dpi;
    private readonly string _language;
    private readonly string _tesseractPath;
    private readonly string? _tessdataPrefix;

    public PdfOcrService(string language = "eng", int dpi = 300, string tesseractPath = "tesseract", string? tessdataPrefix = null)
    {
        _language = language;
        _dpi = dpi;
        _tesseractPath = tesseractPath;
        _tessdataPrefix = tessdataPrefix;
    }

    /// <summary>
    /// True if the <c>tesseract</c> CLI is reachable on PATH (or at the
    /// path given to the constructor).
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = _tesseractPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>OCR a single PDF page.</summary>
    public OcrResult RecognizePage(PdfPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));

        var renderer = new SkiaRenderer();
        using var bitmap = renderer.RenderPage(page, new RenderOptions { Dpi = _dpi });
        return RecognizeBitmap(bitmap, page.Height);
    }

    /// <summary>OCR every page of a document, one result per page.</summary>
    public IEnumerable<OcrResult> RecognizeDocument(PdfDocument document)
    {
        for (int p = 1; p <= document.PageCount; p++)
            yield return RecognizePage(document.GetPage(p));
    }

    /// <summary>
    /// OCR an already-rendered bitmap. <paramref name="pageHeightPoints"/>
    /// is the page height in PDF points so word bboxes can be reported
    /// in page space (PDF bottom-left) rather than pixel space.
    /// </summary>
    public OcrResult RecognizeBitmap(SKBitmap bitmap, double pageHeightPoints)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));

        var pngPath = Path.Combine(Path.GetTempPath(), $"excise-ocr-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(pngPath))
            {
                data.SaveTo(fs);
            }

            return RecognizePngFile(pngPath, pageHeightPoints);
        }
        finally
        {
            try { if (File.Exists(pngPath)) File.Delete(pngPath); } catch { }
        }
    }

    /// <summary>
    /// Invoke tesseract on <paramref name="pngPath"/> with TSV output so
    /// we get per-word bounding boxes. Parse and return.
    /// </summary>
    private OcrResult RecognizePngFile(string pngPath, double pageHeightPoints)
    {
        // tesseract <input> stdout -l eng --psm 6 tsv
        // "stdout" as the output base tells tesseract to write to stdout.
        // TSV config emits tab-separated rows with (level, page_num, ...
        // left, top, width, height, conf, text).
        var psi = new ProcessStartInfo
        {
            FileName = _tesseractPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(pngPath);
        psi.ArgumentList.Add("stdout");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(_language);
        psi.ArgumentList.Add("--psm");
        psi.ArgumentList.Add("6");
        // -c flag enables TSV mode without needing tessdata/configs/tsv
        // to be present — keeps deployment to "just install tesseract".
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("tessedit_create_tsv=1");

        // Explicitly pass TESSDATA_PREFIX when the caller supplied one,
        // otherwise rely on tesseract's own default-path search.
        if (!string.IsNullOrEmpty(_tessdataPrefix))
            psi.Environment["TESSDATA_PREFIX"] = _tessdataPrefix;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start tesseract.");
        string tsv = proc.StandardOutput.ReadToEnd();
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"tesseract exited {proc.ExitCode}. stderr:\n{err}");

        return ParseTsv(tsv, pageHeightPoints);
    }

    /// <summary>
    /// Parse tesseract's TSV output. Columns (1-indexed):
    /// 1 level, 2 page_num, 3 block_num, 4 par_num, 5 line_num, 6 word_num,
    /// 7 left, 8 top, 9 width, 10 height, 11 conf, 12 text.
    /// Only rows at level 5 carry words.
    /// </summary>
    private OcrResult ParseTsv(string tsv, double pageHeightPoints)
    {
        var words = new List<OcrWord>();
        var textBuilder = new System.Text.StringBuilder();
        double pixelsPerPoint = _dpi / 72.0;

        var lines = tsv.Split('\n');
        // Skip header line (lines[0]) plus any blank lines.
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 12) continue;
            if (!int.TryParse(parts[0], out int level)) continue;
            if (level != 5) continue; // word level only

            var text = parts[11];
            if (string.IsNullOrWhiteSpace(text)) continue;

            int left   = Parse(parts[6]);
            int top    = Parse(parts[7]);
            int width  = Parse(parts[8]);
            int height = Parse(parts[9]);
            double conf = Parse(parts[10]) / 100.0;

            // Pixel (top-left origin) → PDF points (bottom-left origin).
            double x1 = left / pixelsPerPoint;
            double x2 = (left + width) / pixelsPerPoint;
            double yTop    = pageHeightPoints - (top / pixelsPerPoint);
            double yBottom = pageHeightPoints - ((top + height) / pixelsPerPoint);

            words.Add(new OcrWord(text, new PdfRectangle(x1, yBottom, x2, yTop), (float)conf));
            textBuilder.Append(text).Append(' ');
        }

        return new OcrResult(textBuilder.ToString().Trim(), words);
    }

    private static int Parse(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
