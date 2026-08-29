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
        var input = Path.Combine(RepoRoot(), "test-pdfs", "redaction-adversarial",
            "image-baked-text--IMAGEBAKEDSECRET.pdf");
        Assert.SkipWhen(!File.Exists(input), "image-baked-text fixture not present");
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

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root unavailable");
    }

    private static string? FindCliAssembly()
    {
        var directory = Directory.GetCurrentDirectory();
        for (var up = 0; up < 8 && directory != null; up++)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(directory, "Excise.Cli", "bin", configuration, "net10.0", "excise.dll");
                if (File.Exists(candidate)) return candidate;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
