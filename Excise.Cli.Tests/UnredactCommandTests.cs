using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1132/#1146/#1147 — the `excise unredact` CLI. Two structurally separate
/// modes and exit codes that convey the headline: 0 clean, 3 recoverable text
/// (certain), 4 residue-only.
/// </summary>
public class UnredactCommandTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    private static (int Exit, string Out) Run(params string[] args)
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
        p.WaitForExit(120_000);
        return (p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
    }

    /// <summary>A one-page PDF: text drawn, then an opaque box over it (fake redaction).</summary>
    private static string WriteFakeRedaction()
    {
        var content = "BT /F1 24 Tf 72 700 Td (Name: Louise Anne Farrar) Tj ET\n0 0 0 rg\n137 694 232 26 re f\n";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (var i = 0; i < objs.Length; i++)
        { offs[i] = Encoding.Latin1.GetByteCount(sb.ToString()); sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        var path = Path.Combine(Path.GetTempPath(), $"unredact-fake-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    [Fact]
    public void CertainMode_ReportsTextUnderABox_ExitCode3()
    {
        var pdf = WriteFakeRedaction();
        try
        {
            var (exit, output) = Run(pdf, "--mode", "certain");
            exit.Should().Be(3, "certain findings mean recoverable text -> exit 3");
            output.Should().Contain("Farrar", "the text under the box must be reported (it is fact, not a guess)");
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void ResidueMode_WithoutDictionary_FailsCleanly()
    {
        var pdf = WriteFakeRedaction();
        try
        {
            var (exit, output) = Run(pdf, "--mode", "residue");
            exit.Should().Be(2, "residue mode needs a dictionary");
            output.Should().Contain("dictionary");
        }
        finally { File.Delete(pdf); }
    }
}
