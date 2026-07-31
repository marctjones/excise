using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// PDFium reference oracle, driven through the shared library directly.
///
/// WHY NOT <see cref="PdfiumReferenceRenderer"/>
/// ---------------------------------------------
/// That one shells out to <c>pdfium_test</c>, Chromium's sample renderer — and
/// that binary is not distributed anywhere. It is built from the pdfium source
/// tree with depot_tools/gn/ninja; no package manager ships it, and
/// bblanchon/pdfium-binaries (the canonical prebuilt distribution) ships
/// <c>lib/libpdfium.*</c> and headers only. Verified by listing the release
/// tarball. So the shell-out renderer could never be provisioned, which is why
/// pdfium sat unusable while appearing in the oracle list (#857).
///
/// This calls the library instead, which scripts/download-pdfium.sh can fetch.
///
/// WHY PDFIUM IS WORTH THE INTEROP
/// -------------------------------
/// It is the Chrome/Foxit lineage — independent of MuPDF (mutool), Poppler
/// (pdftocairo/pdftoppm), Ghostscript and PDFBox. It is also the most widely
/// deployed PDF renderer in existence, so "excise disagrees with pdfium" says
/// something about what most people will actually see, not just what one more
/// library thinks.
/// </summary>
public static class PdfiumNativeReferenceRenderer
{
    private const string LibAlias = "pdfium";

    private static readonly object InitLock = new();
    private static bool _initialised;
    private static readonly Lazy<string?> LibraryPath = new(ResolveLibraryPath);

    public static bool IsAvailable => LibraryPath.Value != null;

    /// <summary>Absolute path of the library in use, for diagnostics.</summary>
    public static string? ResolvedLibraryPath => LibraryPath.Value;

    public static SKBitmap? RenderPage(string pdfPath, int pageNumber, int dpi, string? userPassword = null)
        => TryRenderPage(pdfPath, pageNumber, dpi, userPassword).Bitmap;

    public static ReferenceRenderResult TryRenderPage(
        string pdfPath, int pageNumber, int dpi, string? userPassword = null)
    {
        var sw = Stopwatch.StartNew();
        var lib = LibraryPath.Value;
        if (lib == null)
        {
            return new ReferenceRenderResult(null, "UNAVAILABLE",
                "PDFium library not found; run scripts/download-pdfium.sh or set EXCISE_PDFIUM_LIB",
                sw.ElapsedMilliseconds);
        }

        IntPtr doc = IntPtr.Zero, page = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            EnsureInitialised();

            doc = FPDF_LoadDocument(pdfPath, userPassword);
            if (doc == IntPtr.Zero)
            {
                // FPDF_GetLastError distinguishes "needs a password" from
                // "malformed", which matters when classifying encrypted
                // fixtures — see tests/corpus-passwords.tsv.
                var err = FPDF_GetLastError();
                return new ReferenceRenderResult(null, err == 4 ? "PASSWORD" : "LOAD_FAILED",
                    $"FPDF_LoadDocument failed (FPDF_GetLastError={err})", sw.ElapsedMilliseconds);
            }

            var pageCount = FPDF_GetPageCount(doc);
            if (pageNumber < 1 || pageNumber > pageCount)
            {
                return new ReferenceRenderResult(null, "PAGE_OUT_OF_RANGE",
                    $"requested page {pageNumber} of {pageCount}", sw.ElapsedMilliseconds);
            }

            // Explicit range check, because pdfium clamps silently otherwise —
            // the same trap that made PdfBoxReferenceRenderer hand back the
            // wrong page for a year (#868).
            page = FPDF_LoadPage(doc, pageNumber - 1);
            if (page == IntPtr.Zero)
                return new ReferenceRenderResult(null, "PAGE_LOAD_FAILED", null, sw.ElapsedMilliseconds);

            var scale = dpi / 72.0;
            int width = Math.Max(1, (int)Math.Round(FPDF_GetPageWidthF(page) * scale));
            int height = Math.Max(1, (int)Math.Round(FPDF_GetPageHeightF(page) * scale));

            bitmap = FPDFBitmap_Create(width, height, 0);
            if (bitmap == IntPtr.Zero)
                return new ReferenceRenderResult(null, "BITMAP_ALLOC_FAILED", null, sw.ElapsedMilliseconds);

            // White background: the other oracles rasterise onto white, and an
            // unfilled pdfium bitmap is transparent black, which would show up
            // as a total mismatch rather than a rendering difference.
            FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);

            var managed = CopyToSkBitmap(bitmap, width, height);
            return new ReferenceRenderResult(managed, "OK", null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap);
            if (page != IntPtr.Zero) FPDF_ClosePage(page);
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
        }
    }

    private static SKBitmap CopyToSkBitmap(IntPtr bitmap, int width, int height)
    {
        var buffer = FPDFBitmap_GetBuffer(bitmap);
        var stride = FPDFBitmap_GetStride(bitmap);
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        unsafe
        {
            var src = (byte*)buffer;
            var dst = (byte*)result.GetPixels().ToPointer();
            var rowBytes = result.RowBytes;
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * stride, dst + (long)y * rowBytes,
                                  rowBytes, Math.Min(rowBytes, stride));
        }
        return result;
    }

    private static void EnsureInitialised()
    {
        // FPDF_InitLibrary is process-global and not re-entrant. Never
        // destroyed: pdfium does not support re-initialisation in the same
        // process, and a test host renders many pages across many fixtures.
        lock (InitLock)
        {
            if (_initialised) return;
            FPDF_InitLibrary();
            _initialised = true;
        }
    }

    private static string? ResolveLibraryPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("EXCISE_PDFIUM_LIB");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            Register(explicitPath);
            return explicitPath;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "pdfium.dll" }
                  : OperatingSystem.IsMacOS() ? new[] { "libpdfium.dylib" }
                  : new[] { "libpdfium.so" };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "vendor", "pdfium", "lib", name);
                if (File.Exists(candidate))
                {
                    Register(candidate);
                    return candidate;
                }
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static void Register(string path)
    {
        // The vendored library is outside the probing path, so map the alias
        // explicitly rather than relying on it being next to the assembly.
        NativeLibrary.SetDllImportResolver(typeof(PdfiumNativeReferenceRenderer).Assembly,
            (name, _, _) => name == LibAlias ? NativeLibrary.Load(path) : IntPtr.Zero);
    }

    // ---- pdfium C API (public/fpdfview.h) --------------------------------

    [DllImport(LibAlias)] private static extern void FPDF_InitLibrary();
    [DllImport(LibAlias, CharSet = CharSet.Ansi)]
    private static extern IntPtr FPDF_LoadDocument(string filePath, string? password);
    [DllImport(LibAlias)] private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(LibAlias)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(LibAlias)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(LibAlias)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(LibAlias)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(LibAlias)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(LibAlias)] private static extern uint FPDF_GetLastError();
    [DllImport(LibAlias)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(LibAlias)] private static extern void FPDFBitmap_FillRect(
        IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(LibAlias)] private static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(LibAlias)] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(LibAlias)] private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(LibAlias)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
}
