using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Excise.Ocr.Native;

/// <summary>
/// In-process OCR over the tesseract C API (#1139). This is the <b>opt-in fast
/// path</b>: it hands a raw grayscale buffer straight to libtesseract — no
/// PNG-to-disk, no subprocess — which is what makes OCR-ing many small cropped
/// gap regions cheap. The subprocess <c>PdfOcrService</c> stays the safe
/// default; nothing here runs unless a caller explicitly asks for it.
/// </summary>
/// <remarks>
/// <para>
/// One instance owns exactly one <c>TessBaseAPI</c> handle and reuses it across
/// calls (init loads the trained model, which is the expensive part). The
/// handle carries mutable per-image state, so every recognition call is
/// serialized under a lock and the image/result state is cleared afterwards to
/// keep memory flat across a long run of regions.
/// </para>
/// <para>
/// A bad pointer here segfaults the process (#363/#985), so ownership is never
/// a raw pointer: the API handle and every returned string are
/// <see cref="SafeHandle"/>s, the input buffer is pinned for the exact window
/// libtesseract reads it, and the whole engine is gated behind
/// <see cref="IsAvailable"/>.
/// </para>
/// </remarks>
public sealed class NativeOcrEngine : IDisposable
{
    /// <summary>Tesseract PSM 3 — fully automatic page segmentation.</summary>
    public const int DefaultPageSegMode = 3;

    private readonly object _lock = new();
    private readonly TesseractApiHandle _api;
    private readonly int _dpi;
    private bool _disposed;

    private NativeOcrEngine(TesseractApiHandle api, int dpi)
    {
        _api = api;
        _dpi = dpi;
    }

    /// <summary>
    /// Creates an engine, loading libtesseract and the trained model for
    /// <paramref name="language"/>. Throws if the native library cannot be
    /// located or the model fails to load — call <see cref="IsAvailable"/>
    /// first to avoid the throw.
    /// </summary>
    /// <param name="language">Tesseract language code, e.g. <c>"eng"</c>.</param>
    /// <param name="dpi">Source resolution reported to tesseract, in ppi.</param>
    /// <param name="datapath">
    /// Directory containing the trained data (the one that holds
    /// <c>&lt;language&gt;.traineddata</c>). <c>null</c>/empty falls back to
    /// <c>TESSDATA_PREFIX</c> / a probe of the usual install dirs / tesseract's
    /// compiled-in default — mirroring the subprocess path's
    /// <c>tessdataPrefix</c> handling so the two backends resolve models the
    /// same way.
    /// </param>
    public static NativeOcrEngine Create(string language = "eng", int dpi = 300, string? datapath = null)
    {
        NativeLibraryResolver.EnsureRegistered();

        TesseractApiHandle api;
        try
        {
            api = TesseractCApi.TessBaseAPICreate();
        }
        catch (DllNotFoundException ex)
        {
            throw new NativeOcrUnavailableException(
                "libtesseract could not be loaded. Install it (macOS: `brew install tesseract`, " +
                "Linux: `apt install libtesseract-dev`) or set EXCISE_TESSERACT_LIB.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new NativeOcrUnavailableException(
                "The loaded tesseract library is missing a required C API entry point " +
                "(need libtesseract >= 5).", ex);
        }

        if (api.IsInvalid)
        {
            api.Dispose();
            throw new NativeOcrUnavailableException("TessBaseAPICreate returned a null handle.");
        }

        string? resolvedDatapath = string.IsNullOrEmpty(datapath) ? LocateTessdata() : datapath;
        int rc = TesseractCApi.TessBaseAPIInit3(api, resolvedDatapath, language);
        if (rc != 0)
        {
            api.Dispose();
            throw new NativeOcrUnavailableException(
                $"TessBaseAPIInit3 failed (rc={rc}) for language '{language}'. " +
                "The trained data (e.g. eng.traineddata) may be missing; " +
                "set TESSDATA_PREFIX to the directory that contains it.");
        }

        return new NativeOcrEngine(api, dpi);
    }

    // Cached availability probe: constructing an engine loads the model, which
    // is not free, so the answer is memoized per (language) for the process.
    private static readonly object _availabilityLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, bool> _availability = new();

    /// <summary>
    /// True if an engine for <paramref name="language"/> can be created in this
    /// environment (library present, model loadable). Memoized. Never throws —
    /// use it to gate the opt-in fast path and skip-gate tests.
    /// </summary>
    public static bool IsAvailable(string language = "eng")
    {
        lock (_availabilityLock)
        {
            if (_availability.TryGetValue(language, out var cached))
                return cached;

            bool ok;
            try
            {
                using var probe = Create(language);
                ok = true;
            }
            catch
            {
                ok = false;
            }
            _availability[language] = ok;
            return ok;
        }
    }

    /// <summary>
    /// OCRs an 8-bit grayscale region and returns the recognized text plus the
    /// mean confidence in <c>[0,1]</c>.
    /// </summary>
    /// <param name="pixels">Row-major 8-bit grayscale, length &gt;= <paramref name="width"/> * <paramref name="height"/>.</param>
    /// <param name="width">Region width in pixels.</param>
    /// <param name="height">Region height in pixels.</param>
    /// <param name="pageSegMode">Tesseract page-segmentation mode (PSM).</param>
    public (string Text, float Confidence) OcrRegion(
        byte[] pixels, int width, int height, int pageSegMode = DefaultPageSegMode)
    {
        string? text = null;
        int conf = 0;
        Recognize(pixels, width, height, pageSegMode, api =>
        {
            using var utf8 = TesseractCApi.TessBaseAPIGetUTF8Text(api);
            text = utf8.ReadString();
            conf = TesseractCApi.TessBaseAPIMeanTextConf(api);
        });

        float confidence = conf <= 0 ? 0f : conf >= 100 ? 1f : conf / 100f;
        return (text?.Trim() ?? string.Empty, confidence);
    }

    /// <summary>
    /// OCRs an 8-bit grayscale region and returns tesseract's TSV output
    /// (per-word rows with bounding boxes and confidence). Used by the
    /// word-level bitmap path so the FFI fast path produces the same
    /// structured result as the subprocess TSV.
    /// </summary>
    /// <remarks>
    /// Unlike the CLI renderer, <c>TessBaseAPIGetTsvText</c> emits <b>no</b>
    /// header row — the first line is already a data row. Callers that reuse a
    /// header-skipping parser must account for that.
    /// </remarks>
    public string OcrRegionTsv(
        byte[] pixels, int width, int height, int pageSegMode = DefaultPageSegMode)
    {
        string? tsv = null;
        Recognize(pixels, width, height, pageSegMode, api =>
        {
            using var text = TesseractCApi.TessBaseAPIGetTsvText(api, 0);
            tsv = text.ReadString();
        });
        return tsv ?? string.Empty;
    }

    private void Recognize(byte[] pixels, int width, int height, int pageSegMode, Action<TesseractApiHandle> read)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        long needed = (long)width * height;
        if (pixels.Length < needed)
            throw new ArgumentException(
                $"grayscale buffer too small: have {pixels.Length}, need {needed} for {width}x{height}.",
                nameof(pixels));

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Pin the buffer for the whole SetImage -> GetText window: tesseract
            // reads the pixels lazily during recognition, not just at SetImage,
            // so the pin must outlive the read callback.
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                TesseractCApi.TessBaseAPISetPageSegMode(_api, pageSegMode);
                TesseractCApi.TessBaseAPISetImage(
                    _api, pin.AddrOfPinnedObject(), width, height,
                    bytesPerPixel: 1, bytesPerLine: width);
                TesseractCApi.TessBaseAPISetSourceResolution(_api, _dpi);
                read(_api);
            }
            finally
            {
                // Release the image + result state so memory stays flat across
                // a long run of small regions (the workload this exists for),
                // then unpin.
                try { TesseractCApi.TessBaseAPIClear(_api); } catch { /* handle already dead */ }
                pin.Free();
            }
        }
    }

    /// <summary>
    /// Best-effort location of a tessdata directory when TESSDATA_PREFIX is not
    /// already set. Returns <c>null</c> to let tesseract use its own compiled-in
    /// default (Homebrew builds carry one), which is the common case.
    /// </summary>
    private static string? LocateTessdata()
    {
        // If the environment already points tesseract at a model dir, defer to
        // it (pass null so Init3 reads TESSDATA_PREFIX itself).
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESSDATA_PREFIX")))
            return null;

        string? explicitDir = Environment.GetEnvironmentVariable("EXCISE_TESSDATA_PREFIX");
        if (!string.IsNullOrEmpty(explicitDir) && Directory.Exists(explicitDir))
            return explicitDir;

        foreach (var dir in new[]
        {
            "/opt/homebrew/share/tessdata",
            "/usr/local/share/tessdata",
            "/usr/share/tessdata",
            "/usr/share/tesseract-ocr/5/tessdata",
            "/usr/share/tesseract-ocr/4.00/tessdata",
        })
        {
            if (Directory.Exists(dir))
                return dir;
        }

        return null;   // let tesseract's compiled default handle it.
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _api.Dispose();
        }
    }
}

/// <summary>
/// Thrown when the native tesseract backend cannot be brought up (library or
/// model missing). Callers gate on <see cref="NativeOcrEngine.IsAvailable"/> to
/// avoid it; it exists so a mis-provisioned environment fails loudly rather
/// than silently falling through.
/// </summary>
public sealed class NativeOcrUnavailableException : Exception
{
    public NativeOcrUnavailableException(string message) : base(message) { }
    public NativeOcrUnavailableException(string message, Exception inner) : base(message, inner) { }
}
