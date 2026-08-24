using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1137 Use A — the OCR differential as a CERTAIN channel of `unredact`. A
/// black box over a SCANNED page has no glyphs, so HiddenTextDetector and the
/// residue engine both see nothing; rendering the page with the overlay
/// stripped and OCR-ing the image underneath recovers text they cannot. This
/// gate drives that end-to-end through the CLI, on the same raster-under-overlay
/// shape DifferentialOcrAuditorTests uses.
/// </summary>
public class UnredactOcrChannelTests
{
    private static bool TesseractAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("tesseract", "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p!.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    private static (int Exit, string Out) RunUnredact(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = RepoRoot(),
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add("Excise.Cli");
        psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("unredact");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(180_000);
        return (p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
    }

    /// <summary>
    /// A one-page PDF whose only content is a rasterized "ACCT 9876-5432" with a
    /// black rectangle painted over the middle digits — structurally invisible
    /// text a glyph-based detector cannot find.
    /// </summary>
    private static string WriteScannedRedaction()
    {
        byte[] rgb; int w, h;
        using (var src = PdfDocument.CreateNew())
        {
            var page = src.Pages.AddBlank(400, 200);
            using (var g = page.GetGraphics())
            {
                g.DrawString("ACCT 9876-5432", PdfFont.Helvetica(30), PdfBrush.Black, 50, 100);
                g.Flush();
            }
            using var scan = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 150 });
            w = scan.Width; h = scan.Height;
            rgb = new byte[w * h * 3];
            int i = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = scan.GetPixel(x, y);
                rgb[i++] = c.Red; rgb[i++] = c.Green; rgb[i++] = c.Blue;
            }
        }

        using var ms = new MemoryStream();
        using var sw = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        sw.WriteLine("%PDF-1.4"); sw.Flush();
        var off = new long[7];
        off[1] = ms.Position; sw.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj"); sw.Flush();
        off[2] = ms.Position; sw.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj"); sw.Flush();
        off[3] = ms.Position;
        sw.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] " +
                     "/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj"); sw.Flush();
        var body = "q 400 0 0 200 0 0 cm /Im0 Do Q\nq 0 0 0 rg 175 80 100 50 re f Q";
        off[4] = ms.Position;
        sw.WriteLine($"4 0 obj\n<< /Length {body.Length} >>\nstream"); sw.Write(body); sw.WriteLine();
        sw.WriteLine("endstream\nendobj"); sw.Flush();
        off[5] = ms.Position;
        sw.WriteLine($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                     $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream"); sw.Flush();
        ms.Write(rgb, 0, rgb.Length); sw.WriteLine(); sw.WriteLine("endstream\nendobj"); sw.Flush();
        long xref = ms.Position;
        sw.WriteLine("xref\n0 6\n0000000000 65535 f ");
        for (int i = 1; i <= 5; i++) sw.WriteLine($"{off[i]:D10} 00000 n ");
        sw.Flush();
        sw.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xref}\n%%EOF"); sw.Flush();

        var path = Path.Combine(Path.GetTempPath(), $"unredact-scan-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void OcrChannel_RecoversTextUnderABoxOnAScannedPage_ExitCode3()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract not installed");

        var path = WriteScannedRedaction();
        try
        {
            // Without --ocr the glyph-based channels see nothing: no text objects
            // exist, so certain mode is empty and the exit is clean.
            var (plainExit, _) = RunUnredact(path);
            plainExit.Should().Be(0,
                "the page has no glyphs — the structural detectors cannot see the scanned text");

            // With --ocr the differential recovers it from the stripped image.
            var (ocrExit, ocrOut) = RunUnredact(path, "--ocr", "--json");
            ocrExit.Should().Be(3, "recoverable text is present under the box; " + ocrOut);
            ocrOut.Should().Contain("ocr-differential", "the hit must be tagged as the OCR channel");
            (ocrOut.Contains("9876") || ocrOut.Contains("5432"))
                .Should().BeTrue($"the hidden account digits must surface; got: {ocrOut}");
        }
        finally { File.Delete(path); }
    }
}
