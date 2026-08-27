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
    private readonly bool _useNativeFastPath;

    /// <param name="useNativeFastPath">
    /// Opt in to the in-process <c>Excise.Ocr.Native</c> FFI backend (#1139) for
    /// bitmap OCR: pixels are handed straight to libtesseract with no PNG-to-disk
    /// and no subprocess, which is the point when OCR-ing many small cropped gap
    /// regions. Off by default — the subprocess path stays the safe default, and
    /// this flag falls back to it silently when libtesseract is not present, so
    /// enabling it never breaks an under-provisioned environment.
    /// </param>
    public PdfOcrService(string language = "eng", int dpi = 300, string tesseractPath = "tesseract", string? tessdataPrefix = null, bool useNativeFastPath = false)
    {
        _language = language;
        _dpi = dpi;
        _tesseractPath = tesseractPath;
        _tessdataPrefix = tessdataPrefix;
        _useNativeFastPath = useNativeFastPath;
    }

    /// <summary>
    /// True when this instance is configured for the native fast path AND the
    /// native backend actually loaded for this instance's language and tessdata
    /// prefix (library + model present). When false, <see cref="RecognizeBitmap"/>
    /// uses the subprocess path. The first access may construct and cache the
    /// engine (loading the model); subsequent accesses are cheap.
    /// </summary>
    public bool NativeFastPathActive
        => _useNativeFastPath && GetNativeEngine() != null;

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

        // Opt-in FFI fast path (#1139): hand a grayscale buffer straight to
        // libtesseract, no PNG, no subprocess. Falls through to the subprocess
        // path when not enabled or the native backend is unavailable.
        if (NativeFastPathActive)
        {
            var native = TryRecognizeBitmapNative(bitmap, pageHeightPoints);
            if (native != null) return native;
        }

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
    /// How long a single-page tesseract run may take before excise gives up.
    /// Generous — a dense 300-dpi scan is seconds, not minutes — but finite, so
    /// a wedged child process surfaces as an error instead of a hung program.
    /// </summary>
    private const int TesseractTimeoutMs = 120_000;

    /// <summary>Best-effort termination; a process that already exited throws.</summary>
    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
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

        // Drain BOTH pipes concurrently, and bound the wait.
        //
        // The previous version was `ReadToEnd()` on stdout, then `ReadToEnd()`
        // on stderr, then an unbounded `WaitForExit()`. Two ways for that to
        // hang the caller forever:
        //
        //   1. PIPE DEADLOCK. While blocked reading stdout, nothing drains
        //      stderr. tesseract is chatty there ("Estimating resolution as…",
        //      DPI and dictionary warnings); once the stderr buffer fills
        //      (~64 KB) tesseract blocks writing, we block reading stdout, and
        //      neither side moves again.
        //   2. NO TIMEOUT. `WaitForExit()` with no argument waits forever, so a
        //      wedged tesseract wedges excise — in the GUI that is a hang with
        //      no error, which CLAUDE.md's Pitfall 3 (#93) calls out by name.
        //
        // ReadToEndAsync on both before waiting drains them in parallel, and
        // the timeout turns "wedged" into a diagnosable exception.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(TesseractTimeoutMs))
        {
            TryKill(proc);
            throw new TimeoutException(
                $"tesseract did not finish within {TesseractTimeoutMs / 1000}s for '{pngPath}'. " +
                "The process was terminated.");
        }

        // Only safe once the process has exited: the pipes are closed, so these
        // are already complete or about to be.
        string tsv = stdoutTask.GetAwaiter().GetResult();
        string err = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"tesseract exited {proc.ExitCode}. stderr:\n{err}");

        return ParseTsv(tsv, pageHeightPoints);
    }

    // One native engine per (language, dpi) for the process. Init loads the
    // trained model, so this is cached like a font engine rather than rebuilt
    // per call. The engine is thread-safe (it serializes internally); the
    // dictionary access is guarded here. Never disposed — it lives for the
    // process, and its SafeHandle is released on shutdown.
    private static readonly object _nativeEngineLock = new();
    private static readonly Dictionary<(string, int, string), Native.NativeOcrEngine> _nativeEngines = new();

    private Native.NativeOcrEngine? GetNativeEngine()
    {
        // Key on the tessdata prefix too: the native backend must honour the
        // caller's tessdataPrefix exactly like the subprocess path, so two
        // services with different model dirs get different engines.
        var key = (_language, _dpi, _tessdataPrefix ?? "");
        lock (_nativeEngineLock)
        {
            if (_nativeEngines.TryGetValue(key, out var existing)) return existing;
            try
            {
                var engine = Native.NativeOcrEngine.Create(_language, _dpi, _tessdataPrefix);
                _nativeEngines[key] = engine;
                return engine;
            }
            catch (Native.NativeOcrUnavailableException)
            {
                return null;   // caller falls back to the subprocess path.
            }
        }
    }

    /// <summary>
    /// Bitmap OCR via the in-process FFI backend. Returns <c>null</c> to signal
    /// "fall back to the subprocess path" (native unavailable). Uses PSM 6 for
    /// parity with the subprocess <c>--psm 6</c>, and reuses <see cref="ParseTsv"/>
    /// so word bboxes come out identical to the subprocess path.
    /// </summary>
    private OcrResult? TryRecognizeBitmapNative(SKBitmap bitmap, double pageHeightPoints)
    {
        var engine = GetNativeEngine();
        if (engine == null) return null;

        byte[] gray = ToGray8(bitmap, out int width, out int height);
        // PSM 6 == "assume a single uniform block of text", matching the CLI path.
        string tsv = engine.OcrRegionTsv(gray, width, height, pageSegMode: 6);
        return ParseTsv(tsv, pageHeightPoints, hasHeader: false);
    }

    /// <summary>
    /// Copy an <see cref="SKBitmap"/> into a tight, row-major 8-bit grayscale
    /// buffer (one byte per pixel, stride == width) — the layout
    /// <c>TessBaseAPISetImage</c> is fed with bytesPerPixel=1, bytesPerLine=width.
    /// White background so any alpha flattens to paper, not black.
    /// </summary>
    private static byte[] ToGray8(SKBitmap source, out int width, out int height)
    {
        width = source.Width;
        height = source.Height;

        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var gray = new SKBitmap(info);
        using (var canvas = new SKCanvas(gray))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
        }

        int rowBytes = gray.RowBytes;
        var src = gray.GetPixelSpan();
        var dst = new byte[(long)width * height];
        // Gray8 rows may be padded (RowBytes >= width); copy the leading
        // `width` bytes of each row into a tight buffer.
        for (int y = 0; y < height; y++)
        {
            src.Slice(y * rowBytes, width).CopyTo(dst.AsSpan(y * width, width));
        }
        return dst;
    }

    /// <summary>
    /// Parse tesseract's TSV output. Columns (1-indexed):
    /// 1 level, 2 page_num, 3 block_num, 4 par_num, 5 line_num, 6 word_num,
    /// 7 left, 8 top, 9 width, 10 height, 11 conf, 12 text.
    /// Only rows at level 5 carry words.
    /// </summary>
    private OcrResult ParseTsv(string tsv, double pageHeightPoints, bool hasHeader = true)
    {
        var words = new List<OcrWord>();
        var textBuilder = new System.Text.StringBuilder();
        double pixelsPerPoint = _dpi / 72.0;

        var lines = tsv.Split('\n');
        // The CLI's TSV renderer emits a `level\tpage_num\t...` header row;
        // libtesseract's TessBaseAPIGetTsvText does NOT (its first line is
        // already a data row). Skipping unconditionally would drop the first
        // word from the native path — verified empirically (#1139).
        foreach (var raw in (hasHeader ? lines.Skip(1) : lines))
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
