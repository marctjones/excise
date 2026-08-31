using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// End-to-end CLI coverage for the two ground-truth channels added to
/// `excise unredact` (RC18): CERTAIN recovery from unscrubbed carriers
/// (/ActualText, annotation /Contents) and the corroboration posture on the
/// residue channel (mutool-corroborated by default, --no-corroboration to opt
/// out). The engine/unit level is covered by CarrierTextRecoveryTests; these
/// prove the behaviour is actually reachable through the shipped command.
/// </summary>
public class UnredactCarrierChannelTests
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

    /// <summary>
    /// A one-page PDF whose VISIBLE content says only "Public", but which carries
    /// the secret twice in carriers a reader never sees: a structure element's
    /// /ActualText and a text annotation's /Contents.
    /// </summary>
    private static string WriteCarrierLeak(string actualText, string annotContents)
    {
        var content = "BT /F1 14 Tf 72 700 Td (Public) Tj ET\n";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [7 0 R] >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K 8 0 R >>",
            $"<< /Type /Annot /Subtype /Text /Rect [72 700 92 720] /Contents ({annotContents}) >>",
            $"<< /Type /StructElem /S /P /ActualText ({actualText}) >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (var i = 0; i < objs.Length; i++)
        {
            offs[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF");
        var path = Path.Combine(Path.GetTempPath(), $"unredact-carrier-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    [Fact]
    public void CertainMode_RecoversTextFromActualTextCarrier_ExitCode3()
    {
        var pdf = WriteCarrierLeak("SECRETALPHA", "unrelated note");
        try
        {
            var (exit, output) = Run(pdf, "--mode", "certain", "--json");
            exit.Should().Be(3, "recoverable text present in a carrier is a certain finding");
            output.Should().Contain("SECRETALPHA", "the /ActualText carrier is recovered verbatim");
            output.Should().Contain("/ActualText", "the finding names the carrier it came from");
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void CertainMode_RecoversTextFromAnnotationContentsCarrier_ExitCode3()
    {
        var pdf = WriteCarrierLeak("unrelated", "SECRETBETA");
        try
        {
            var (exit, output) = Run(pdf, "--mode", "certain", "--json");
            exit.Should().Be(3);
            output.Should().Contain("SECRETBETA", "the annotation /Contents carrier is recovered verbatim");
            output.Should().Contain("annotation /Contents");
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void Handler_CarrierChannelReturnsTypedFindingWithoutSecondCliProcess()
    {
        var pdf = WriteCarrierLeak("SECRETALPHA", "SECRETBETA");
        try
        {
            var outcome = UnredactCommandHandler.Execute(
                new UnredactCommandInput(
                    pdf,
                    "certain",
                    DictionaryPath: null,
                    Tolerance: 0.5,
                    MaxCandidates: 200,
                    UseOcr: false,
                    NoCorroboration: false),
                TestContext.Current.CancellationToken);

            outcome.ExitCode.Should().Be(3);
            outcome.Report!.Certain.Should().Contain(
                finding => finding.Text == "SECRETALPHA" && finding.HiddenBy.EndsWith("/ActualText"));
            outcome.Report.Certain.Should().Contain(
                finding => finding.Text == "SECRETBETA" && finding.HiddenBy == "annotation /Contents");
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void ResidueMode_CorroborationIsOnByDefault_AndSurfacedInOutput()
    {
        var pdf = WriteCarrierLeak("x", "y");
        var dict = Path.Combine(Path.GetTempPath(), $"unredact-dict-{Guid.NewGuid():N}.txt");
        File.WriteAllText(dict, "alpha\nbeta\ngamma\n");
        try
        {
            var (_, output) = Run(pdf, "--mode", "residue", "--dictionary", dict, "--json");
            // The posture must be declared, and the default must be the
            // independent witness — never excise silently vouching for itself.
            output.Should().Contain("mutool (independent)",
                "residue candidates are corroborated by mutool by default");
            output.Should().NotContain("uncorroborated width estimate");
        }
        finally { File.Delete(pdf); File.Delete(dict); }
    }

    [Fact]
    public void ResidueMode_NoCorroboration_IsLabelledAsUncorroborated()
    {
        var pdf = WriteCarrierLeak("x", "y");
        var dict = Path.Combine(Path.GetTempPath(), $"unredact-dict-{Guid.NewGuid():N}.txt");
        File.WriteAllText(dict, "alpha\nbeta\ngamma\n");
        try
        {
            var (_, output) = Run(pdf, "--mode", "residue", "--dictionary", dict, "--json", "--no-corroboration");
            output.Should().Contain("uncorroborated width estimate",
                "opting out of corroboration must be declared, not silent");
        }
        finally { File.Delete(pdf); File.Delete(dict); }
    }
}
