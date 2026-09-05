using System;
using System.Diagnostics;
using System.Globalization;
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

    /// <summary>
    /// <c>FPDF_ANNOT</c> from public/fpdfview.h — "Set if annotations are to be
    /// rendered". Without it pdfium draws page content only, so it inks NOTHING
    /// for a page whose only content is an annotation (#1007).
    /// </summary>
    private const int FPDF_ANNOT = 0x01;

    private static readonly object InitLock = new();

    /// <summary>
    /// EVERY FPDF_* call is serialised on this. pdfium's public API is not
    /// thread-safe — one global allocator, font cache and colour-space cache,
    /// with no internal locking — so two threads inside FPDF_LoadPage corrupt
    /// each other's state and the process dies with SIGSEGV somewhere
    /// unrelated to the caller.
    ///
    /// That is not hypothetical. Until #1369 only <see cref="InitLock"/>
    /// existed, guarding one-time init and nothing else. The redaction bench
    /// measured its four tools through Parallel.ForEach (f08b1503, 2026-08-29)
    /// and each branch renders through here, so four threads entered pdfium at
    /// once; on 2026-09-05 the test host took a SIGSEGV in
    /// CPDF_Color::GetColorRef() under FPDF_LoadPage, 13 minutes into the run,
    /// killing every result. The bench had been green with pdfium since
    /// 2026-08-25 — single-threaded. The library did not change; the number of
    /// threads did.
    ///
    /// Serialising costs concurrency on this oracle only. A crash costs the
    /// whole run, and a native crash cannot be caught in managed code: there is
    /// no try/catch that saves the process. Do NOT replace this with per-call
    /// or per-document locking — the shared state pdfium corrupts is global,
    /// not per-document. ConcurrentRenders_DoNotCorruptPdfiumsGlobalState
    /// (PdfiumOracleSmokeTests) fails without it.
    /// </summary>
    private static readonly object NativeLock = new();
    private static bool _initialised;
    private static readonly Lazy<string?> LibraryPath = new(ResolveLibraryPath);

    /// <summary>
    /// TRUE only inside the RenderTools <c>pdfium-render</c> host, which sets
    /// this for itself. Everywhere else the renderer spawns that host instead of
    /// loading pdfium, so a pdfium crash costs a child process rather than the
    /// caller (#1369). Nothing but the host should ever set this.
    /// </summary>
    private static bool InProcess =>
        Environment.GetEnvironmentVariable("EXCISE_PDFIUM_INPROC") == "1";

    private static readonly Lazy<string?> HostPath = new(ResolveHostPath);

    /// <summary>
    /// The library must be present AND, out of process, the host must be
    /// resolvable. A missing host is reported as a distinct status rather than
    /// silently reading as "pdfium unavailable" — an oracle that vanishes
    /// quietly is how a suite loses coverage without anyone noticing.
    /// </summary>
    public static bool IsAvailable => LibraryPath.Value != null && (InProcess || HostPath.Value != null);

    /// <summary>Why the oracle is unusable, for a caller that wants to say so.</summary>
    public static string? UnavailableReason =>
        LibraryPath.Value == null
            ? "PDFium library not found; run scripts/download-pdfium.sh or set EXCISE_PDFIUM_LIB"
            : (InProcess || HostPath.Value != null)
                ? null
                : "PDFium present but the out-of-process host is not built; build tools/Excise.RenderTools "
                  + "or set EXCISE_RENDERTOOLS_DLL";


    public static SKBitmap? RenderPage(
        string pdfPath, int pageNumber, int dpi, string? userPassword = null,
        bool renderAnnotations = false)
        => TryRenderPage(pdfPath, pageNumber, dpi, userPassword, renderAnnotations).Bitmap;

    /// <param name="renderAnnotations">
    /// Pass FPDF_ANNOT, so annotations are drawn (and missing appearances
    /// synthesized, for the subtypes pdfium generates an /AP for).
    ///
    /// WHY THIS DEFAULTS TO FALSE, WHICH IS THE *WRONG* DEFAULT ON THE MERITS
    /// ---------------------------------------------------------------------
    /// The other four oracles draw annotations unconditionally, and so does
    /// excise, so an annotation-blind pdfium is an outlier in any comparison
    /// that includes an annotated page. But every checked-in pdfium baseline —
    /// tests/corpus-expectations*.tsv, test-pdfs/rendering-contracts/**, the
    /// MISSING_CONTENT majority votes in the corpus scan, PdfiumOracleSmokeTests
    /// — was measured with flags=0. Flipping the default silently re-decides all
    /// of them, and re-deriving them means a corpus scan. So the flag is opt-in:
    /// callers that are ASKING about annotations pass true (the #993 annotation
    /// synthesis policy gate does), and nothing else moves. Making it the
    /// default is a separate change that must re-run the corpus scans (#1007).
    /// </param>
    public static ReferenceRenderResult TryRenderPage(
        string pdfPath, int pageNumber, int dpi, string? userPassword = null,
        bool renderAnnotations = false)
    {
        var sw = Stopwatch.StartNew();
        var lib = LibraryPath.Value;
        if (lib == null)
        {
            return new ReferenceRenderResult(null, "UNAVAILABLE",
                "PDFium library not found; run scripts/download-pdfium.sh or set EXCISE_PDFIUM_LIB",
                sw.ElapsedMilliseconds);
        }

        // Out of process by default (#1369): pdfium is the only oracle we could
        // load into the caller, and a native crash cannot be caught in managed
        // code. The in-process body below runs only inside the host.
        if (!InProcess)
            return RenderViaHost(pdfPath, pageNumber, dpi, userPassword, renderAnnotations, sw);

        IntPtr doc = IntPtr.Zero, page = IntPtr.Zero, bitmap = IntPtr.Zero;
        // The whole native span, not just init: load, page load, render and
        // teardown all touch pdfium's global caches (#1369).
        lock (NativeLock)
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
            FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0,
                                  renderAnnotations ? FPDF_ANNOT : 0);

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

    /// <summary>
    /// Spawn the RenderTools host for one page. A crash in pdfium shows up here
    /// as a non-zero exit with no PNG, which becomes a CRASHED status the caller
    /// can record — the whole point of the isolation.
    /// </summary>
    private static ReferenceRenderResult RenderViaHost(
        string pdfPath, int pageNumber, int dpi, string? userPassword,
        bool renderAnnotations, Stopwatch sw)
    {
        var host = HostPath.Value;
        if (host == null)
        {
            return new ReferenceRenderResult(null, "HOST_MISSING", UnavailableReason, sw.ElapsedMilliseconds);
        }

        var png = Path.Combine(Path.GetTempPath(), $"excise-pdfium-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(host);
            psi.ArgumentList.Add("pdfium-render");
            psi.ArgumentList.Add("--pdf"); psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add("--page"); psi.ArgumentList.Add(pageNumber.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--dpi"); psi.ArgumentList.Add(dpi.ToString(CultureInfo.InvariantCulture));
            if (renderAnnotations) psi.ArgumentList.Add("--annots");
            if (!string.IsNullOrEmpty(userPassword)) { psi.ArgumentList.Add("--password"); psi.ArgumentList.Add(userPassword); }
            psi.ArgumentList.Add("--output"); psi.ArgumentList.Add(png);
            // The host renders in process; keep its own serialisation honest.
            psi.Environment["EXCISE_PDFIUM_INPROC"] = "1";

            using var p = Process.Start(psi);
            if (p == null) return new ReferenceRenderResult(null, "HOST_START_FAILED", null, sw.ElapsedMilliseconds);
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(HostTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ReferenceRenderResult(null, "TIMEOUT",
                    $"pdfium host exceeded {HostTimeoutMs} ms", sw.ElapsedMilliseconds);
            }

            if (!File.Exists(png))
            {
                // 3 is the host's own "pdfium refused" exit; anything else with no
                // PNG is the child dying, which is what isolation exists to survive.
                var status = p.ExitCode == 3 ? "LOAD_FAILED" : "CRASHED";
                return new ReferenceRenderResult(null, status,
                    $"pdfium host exit {p.ExitCode}: {stderr.Trim()}", sw.ElapsedMilliseconds);
            }

            var bitmap = SKBitmap.Decode(png);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_FAILED", stderr.Trim(), sw.ElapsedMilliseconds)
                : new ReferenceRenderResult(bitmap, "OK", null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            try { File.Delete(png); } catch { }
        }
    }

    private const int HostTimeoutMs = 120_000;

    /// <summary>
    /// The RenderTools assembly that hosts pdfium. EXCISE_RENDERTOOLS_DLL wins;
    /// otherwise walk up to the repo root and take the build matching our own
    /// configuration, then either configuration.
    /// </summary>
    private static string? ResolveHostPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("EXCISE_RENDERTOOLS_DLL");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        var ours = AppContext.BaseDirectory;
        var configs = ours.Contains("/Release/", StringComparison.Ordinal)
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };

        var dir = new DirectoryInfo(ours);
        while (dir != null)
        {
            foreach (var cfg in configs)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "Excise.RenderTools",
                                             "bin", cfg, "net10.0", "Excise.RenderTools.dll");
                if (File.Exists(candidate)) return candidate;
            }
            dir = dir.Parent;
        }
        return null;
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
