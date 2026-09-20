using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// #1674: the ONE shared locator. A hand walk to `.git` finds a WORKTREE
    /// root, where gitignored corpora and tools/vendor do not exist.
    ///
    /// <para>⚠️ #1700 — BUILD OUTPUT IS THE EXCEPTION, and this is build
    /// output: the subprocess below runs `dotnet run --project Excise.Cli`
    /// from here. The LOCAL checkout has to win, or a test running in a
    /// worktree spawns the MAIN checkout's CLI — exercising whatever branch
    /// happens to be checked out there and passing on code this branch does
    /// not contain. That is the #1527 hazard exactly, and `CLAUDE.md` already
    /// states the rule: `FindFileInLocalCheckout` exists for build output for
    /// this reason. The main checkout stays as the fallback for a layout where
    /// the local root cannot be resolved at all.</para>
    /// </summary>
    private static string RepoRoot() =>
        Excise.TestSupport.TestRepoLayout.LocalCheckoutRoot
        ?? Excise.TestSupport.TestRepoLayout.MainCheckoutRoot
        ?? throw new System.InvalidOperationException("no checkout above the test binary");

    private static (int Exit, string Out) RunUnredact(params string[] args)
    {
        var (exit, stdout, stderr) = RunUnredactSplit(args);
        return (exit, stdout + stderr);
    }

    /// <summary>
    /// stdout and stderr kept APART. The combined form above is fine for
    /// substring assertions, but stdout alone is the only thing that parses as
    /// JSON: tesseract writes progress lines to stderr, and concatenating them
    /// onto a `--json` document makes it trailing garbage.
    /// </summary>
    private static (int Exit, string Out, string Err) RunUnredactSplit(params string[] args)
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
        return (p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
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

    /// <summary>
    /// ⚠️ #1690 — an OCR hit is REPORTED but does not move the TEXT-recovery
    /// headline.
    ///
    /// <para>This is the case the tiering is FOR. OCR read pixels, not bytes:
    /// the reading carries its own error rate, and no independent extractor can
    /// confirm it the way one confirms a string lifted out of the file. Before
    /// #1690 the hit landed in the legacy <c>certain</c> list and was counted
    /// as "text present" — a recognition presented with the same authority as a
    /// byte-for-byte recovery.</para>
    ///
    /// <para>The finding itself is untouched: it is still listed, still tagged
    /// <c>ocr-differential</c>, and the exit status still goes non-zero
    /// (deliberately — the summary ranks, the exit code stays paranoid).</para>
    /// </summary>
    [Fact]
    public void AnOcrHitIsReportedAndExitsNonZero_ButIsNotCountedAsTextRecovered()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract not installed");

        var path = WriteScannedRedaction();
        try
        {
            var (exit, output, stderr) = RunUnredactSplit(path, "--ocr", "--json");

            exit.Should().Be(3, "an OCR reading is still a recovery for the exit status; " + output + stderr);
            output.Should().Contain("ocr-differential", "the finding must still be listed");
            output.Should().Contain("\"deferred\"",
                "the finding carries its tier, so a machine reader can see it is not graded");

            using var json = JsonDocument.Parse(output);
            var quantification = json.RootElement.GetProperty("quantification");
            quantification.GetProperty("findings").GetInt32().Should().BeGreaterThan(0,
                "the OCR hits are REPORTED — deferring them must not hide them");
            quantification.GetProperty("fullyRecoverable").GetInt32().Should().Be(0,
                "an OCR reading is not text read from the file; it must not count as text present");
            quantification.GetProperty("recovered").GetInt32().Should().Be(0,
                "the headline is the TEXT-recovery score and OCR is deferred (#1690)");
        }
        finally { File.Delete(path); }
    }
}
