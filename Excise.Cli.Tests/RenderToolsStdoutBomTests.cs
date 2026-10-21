using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #2084, the Excise.RenderTools half: RenderTools is JIT-only and renders
/// corpus PDFs through Excise.Rendering, so its first JPX decode put CSJ2K's
/// UTF-8 BOM into its own stdout and stderr, between its progress lines. Runs
/// the real tool in a child process and reads the raw bytes. The progress-line
/// assertion catches the other failure the helper can cause: stdout silently
/// going to /dev/null for the rest of the process.
/// </summary>
public class RenderToolsStdoutBomTests
{
    [Fact]
    public void ProfileWorkflows_OnJpxPdf_WritesNoBomAndKeepsStdout()
    {
        var work = Directory.CreateTempSubdirectory("excise-2084-rt-");
        try
        {
            var corpus = Directory.CreateDirectory(Path.Combine(work.FullName, "corpus"));
            File.WriteAllBytes(Path.Combine(corpus.FullName, "jpx.pdf"), JpxCodecStdoutBomTests.BuildJpxPdf());
            var output = Path.Combine(work.FullName, "out");

            var (exit, stdout, stderr) = RunRenderTools(
                "profile-workflows", "--corpus", corpus.FullName, "--output-dir", output,
                "--page-limit", "1", "--dpi", "72", "--steps", "first-page-render");

            exit.Should().Be(0, "profile-workflows must succeed. stderr: " + Encoding.UTF8.GetString(stderr));
            var profile = File.ReadAllText(Path.Combine(output, "workflow-profile.ndjson"));
            profile.Should().Contain("\"step\":\"first-page-render\"").And.Contain("\"status\":\"OK\",\"detail\":\"8x8\"",
                "the page must actually have been rendered, or no JPX decode happened and the test proves nothing");
            JpxCodecStdoutBomTests.IndexOf(stdout, JpxCodecStdoutBomTests.Bom).Should().Be(-1,
                "CSJ2K's logger must not write a BOM into RenderTools' stdout");
            JpxCodecStdoutBomTests.IndexOf(stderr, JpxCodecStdoutBomTests.Bom).Should().Be(-1,
                "nor into its stderr");
            Encoding.UTF8.GetString(stdout).Should().Contain("Profiling jpx.pdf").And.Contain("Workflow profile:",
                "stdout written after the first JPX decode must still reach the parent");
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    private static (int Exit, byte[] Stdout, byte[] Stderr) RunRenderTools(params string[] args)
    {
        var root = TestRepoLayout.LocalCheckoutRoot ?? throw new InvalidOperationException(
            "no local checkout above " + AppContext.BaseDirectory);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = root,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(Path.Combine("tools", "Excise.RenderTools"));
        psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = JpxCodecStdoutBomTests.ReadAllAsync(p.StandardOutput.BaseStream);
        var e = JpxCodecStdoutBomTests.ReadAllAsync(p.StandardError.BaseStream);
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Excise.RenderTools profile-workflows did not exit within 120 s");
        }
        return (p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
    }
}
