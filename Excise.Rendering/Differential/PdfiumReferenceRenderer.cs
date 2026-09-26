using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to PDFium's standalone <c>pdfium_test</c> sample renderer.
/// This is an optional browser-engine oracle for diagnostic corpus runs.
/// </summary>
internal static class PdfiumReferenceRenderer
{
    private static readonly Lazy<string?> _commandName = new(() =>
    {
        var explicitCommand = Environment.GetEnvironmentVariable("EXCISE_PDFIUM_TEST");
        if (!string.IsNullOrWhiteSpace(explicitCommand) && ReferenceProcess.IsLaunchable(explicitCommand, 2000, "--help"))
            return explicitCommand;

        return ReferenceProcess.IsLaunchable("pdfium_test", 2000, "--help") ? "pdfium_test" : null;
    });

    public static bool IsAvailable => _commandName.Value != null;

    public static SKBitmap? RenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword: null).Bitmap;

    public static ReferenceRenderResult TryRenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword: null);

    public static SKBitmap? RenderPage(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        string? userPassword)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword).Bitmap;

    public static ReferenceRenderResult TryRenderPage(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        string? userPassword)
    {
        var sw = Stopwatch.StartNew();
        var command = _commandName.Value;
        if (command == null)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE",
                "pdfium_test is not on PATH; set EXCISE_PDFIUM_TEST to the standalone renderer",
                sw.ElapsedMilliseconds);

        var tempDir = Path.Combine(Path.GetTempPath(), $"excise-pdfium-ref-{Guid.NewGuid():N}");
        var tempPdf = Path.Combine(tempDir, "input.pdf");
        var zeroBasedPage = checked(pageNumber - 1);
        var expectedPng = tempPdf + "." + zeroBasedPage + ".png";
        var scale = Math.Max(1.0 / 72.0, dpi / 72.0);

        try
        {
            Directory.CreateDirectory(tempDir);
            File.Copy(pdfPath, tempPdf, overwrite: true);

            return ReferenceProcess.RenderPng(sw, "pdfium_test", command,
                BuildPdfiumTestArguments(tempPdf, zeroBasedPage, scale, userPassword), timeoutMs,
                () => File.Exists(expectedPng)
                    ? expectedPng
                    : Directory.GetFiles(tempDir, "input.pdf.*.png", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .FirstOrDefault());
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    internal static IReadOnlyList<string> BuildPdfiumTestArguments(
        string pdfPath,
        int zeroBasedPage,
        double scale,
        string? userPassword)
    {
        var args = new List<string>
        {
            "--png",
            $"--pages={zeroBasedPage}",
            $"--scale={scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        };

        if (userPassword != null)
            args.Add($"--password={userPassword}");

        args.Add(pdfPath);
        return args;
    }
}
