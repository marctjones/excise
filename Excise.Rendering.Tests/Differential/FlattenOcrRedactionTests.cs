using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1186 end-to-end check using tools that do not share Excise's rendering or
/// extraction code. It proves that the image-only escape hatch removes the
/// scanned secret from the saved file and from an independently rasterised view.
/// </summary>
public sealed class FlattenOcrRedactionTests
{
    private const string Term = "IMAGEBAKEDSECRET";

    [Fact]
    public void FlattenOcr_RemovesImageBakedTerm_FromIndependentTextAndVisualOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        // ⚠️ #1702 — the SHARED locator, and a CHECKABLE absence claim.
        //
        // This used to walk upward to the first `.git` and join `test-pdfs`
        // onto it. A worktree's `.git` is a FILE, which that walk accepted, so
        // it stopped at the WORKTREE root — where this gitignored corpus is
        // not — and skipped, claiming the fixture was "not present" while it
        // sat in the main checkout. Measured 2026-09-20: the main checkout ran
        // this test and all three worktrees skipped it, so a redaction
        // differential was silently absent from every worktree session that
        // day. TestRepoLayout reaches the MAIN checkout through git's own
        // worktree plumbing, which is the right answer for a gitignored
        // corpus.
        var input = TestRepoLayout.FindFile(
            "test-pdfs", "redaction-adversarial", "image-baked-text--IMAGEBAKEDSECRET.pdf");
        Assert.SkipWhen(input == null, TestRepoLayout.AbsenceReason(
            "image-baked-text redaction fixture",
            Path.Combine("test-pdfs", "redaction-adversarial", "image-baked-text--IMAGEBAKEDSECRET.pdf")));
        var cli = FindCliAssembly();
        Assert.SkipWhen(cli == null, "Excise.Cli binary unavailable");
        var output = Path.Combine(Path.GetTempPath(), $"excise-flatten-ocr-{Guid.NewGuid():N}.pdf");
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { cli!, "redact", input, output, Term, "--flatten-ocr" })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEnd();
            var stderr = child.StandardError.ReadToEnd();
            child.WaitForExit(120_000).Should().BeTrue("image-only redaction must not hang");
            child.ExitCode.Should().Be(0, $"stdout={stdout} stderr={stderr}");

            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), Term).Should().BeEmpty();
            (MutoolTextExtractor.ExtractPage(output, 1) ?? "").Should().NotContain(Term);
            RedactionBenchmarkRunner.MeasureImageBakedReadable(input, output, Term).Should().Be(0,
                "a Ghostscript render OCR-ed by tesseract must not reveal the baked secret");
        }
        finally { try { File.Delete(output); } catch { } }
    }

    /// <summary>
    /// ⚠️ <b>BUILD OUTPUT — local checkout only, never an upward search.</b>
    /// Same reasoning as <c>JpxSoftMaskRenderTests.FindCliAssembly</c>: reaching
    /// the MAIN checkout from a git worktree is correct for read-only corpus data
    /// (#1527) and wrong here, because it would run another build's binary and
    /// pass. #1525 declined to sweep this locator for that reason; the bound is
    /// gone, the local-only scope stays.
    /// </summary>
    private static string? FindCliAssembly()
    {
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = TestRepoLayout.FindFileInLocalCheckout(
                "Excise.Cli", "bin", configuration, "net10.0", "excise.dll");
            if (candidate != null) return candidate;
        }

        return null;
    }
}
