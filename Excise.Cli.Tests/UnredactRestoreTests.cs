using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1588 — `unredact --restore` writes a rebuilt PDF with recovered material
/// drawn back in place.
///
/// <para>The acceptance criteria in the issue are all "does a tool that is not
/// excise agree", so that is what these check: qpdf accepts the structure,
/// mutool extracts the restored text at the mark, and the output is a NEW file
/// the input survives. An excise-only check would prove the writer and the
/// reader share an opinion, which is not the property that matters for an
/// artefact someone hands to a court.</para>
/// </summary>
public class UnredactRestoreTests
{
    [Fact]
    public void RefusesToOverwriteTheInput()
    {
        // The whole value of a restored copy is that the original is still
        // there to compare against. A tool that can destroy its own evidence
        // is not one you hand a records officer.
        var path = WriteBoxOverText("MANAFORT");
        try
        {
            var outcome = UnredactCommandHandler.Execute(
                Input(path, restore: path), TestContext.Current.CancellationToken);

            outcome.ExitCode.Should().Be(2);
            outcome.Error.Should().Contain("must not overwrite the input");
            outcome.Report.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheInputIsUnchanged()
    {
        var path = WriteBoxOverText("MANAFORT");
        var restored = Path.Combine(Path.GetTempPath(), $"excise-restored-{Guid.NewGuid():N}.pdf");
        var before = File.ReadAllBytes(path);
        try
        {
            UnredactCommandHandler.Execute(
                Input(path, restore: restored), TestContext.Current.CancellationToken);
            File.ReadAllBytes(path).Should().Equal(before);
        }
        finally { Delete(path, restored); }
    }

    [Fact]
    public void TheRestoredCopyIsStructurallyValid()
    {
        Assert.SkipUnless(ToolAvailable("qpdf"), "qpdf is not on PATH");

        var path = WriteBoxOverText("MANAFORT");
        var restored = Path.Combine(Path.GetTempPath(), $"excise-restored-{Guid.NewGuid():N}.pdf");
        try
        {
            var outcome = UnredactCommandHandler.Execute(
                Input(path, restore: restored), TestContext.Current.CancellationToken);
            outcome.Report!.Restore.Should().NotBeNull();
            outcome.Report.Restore!.ItemsDrawn.Should().BeGreaterThan(0);

            var (exit, output) = Run("qpdf", "--check", restored);
            exit.Should().Be(0, $"qpdf must accept the restored copy: {output}");
        }
        finally { Delete(path, restored); }
    }

    [Fact]
    public void TheRecoveredTextIsReadableInTheRestoredCopyByAnIndependentExtractor()
    {
        Assert.SkipUnless(ToolAvailable("mutool"), "mutool is not on PATH");

        var path = WriteBoxOverText("MANAFORT");
        var restored = Path.Combine(Path.GetTempPath(), $"excise-restored-{Guid.NewGuid():N}.pdf");
        try
        {
            UnredactCommandHandler.Execute(
                Input(path, restore: restored), TestContext.Current.CancellationToken);

            var (_, text) = Run("mutool", "draw", "-F", "txt", "-o", "-", restored);

            // Twice: the original occurrence under the box, and the restored
            // one drawn on top. One occurrence would mean nothing was drawn.
            CountOccurrences(text, "MANAFORT").Should().BeGreaterThanOrEqualTo(2,
                "the restored text must be drawn in addition to the original");
            text.Should().Contain("RECONSTRUCTION",
                "a page separated from its report must still say what it is");
        }
        finally { Delete(path, restored); }
    }

    [Fact]
    public void TheRestoredCopyRenders()
    {
        Assert.SkipUnless(ToolAvailable("pdftocairo"), "pdftocairo is not on PATH");

        var path = WriteBoxOverText("MANAFORT");
        var restored = Path.Combine(Path.GetTempPath(), $"excise-restored-{Guid.NewGuid():N}.pdf");
        var prefix = Path.Combine(Path.GetTempPath(), $"excise-render-{Guid.NewGuid():N}");
        try
        {
            UnredactCommandHandler.Execute(
                Input(path, restore: restored), TestContext.Current.CancellationToken);

            var (exit, output) = Run("pdftocairo", "-png", "-r", "40", restored, prefix);
            exit.Should().Be(0, $"pdftocairo must render the restored copy: {output}");
            Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(prefix) + "*.png")
                .Should().NotBeEmpty();
        }
        finally
        {
            Delete(path, restored);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(prefix) + "*.png"))
                File.Delete(f);
        }
    }

    [Fact]
    public void WithoutTheFlag_NoFileIsWritten()
    {
        // Negative control: --restore is opt-in, because the output contains
        // the recovered text and must never appear by surprise.
        var path = WriteBoxOverText("MANAFORT");
        try
        {
            var outcome = UnredactCommandHandler.Execute(
                Input(path), TestContext.Current.CancellationToken);
            outcome.Report!.Restore.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    private static UnredactCommandInput Input(string path, string? restore = null) => new(
        path, "certain", DictionaryPath: null, Tolerance: 0.5,
        MaxCandidates: 200, UseOcr: false, NoCorroboration: false, RestorePath: restore);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static void Delete(params string[] paths)
    {
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    private static (int ExitCode, string Output) Run(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException(tool); }
        return (process.ExitCode, stdout + stderr);
    }

    private static bool ToolAvailable(string tool)
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(d => !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, tool)));

    private static string WriteBoxOverText(string text)
    {
        var width = text.Length * 14 * 0.78 + 6;
        var content = $"BT /F1 14 Tf 72 700 Td ({text}) Tj ET\n" +
            $"q 0 0 0 rg 70 697 {width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} 17 re f Q\n";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objs.Length];
        for (var i = 0; i < objs.Length; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var path = Path.Combine(Path.GetTempPath(), $"excise-restore-src-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }
}
