using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// <c>excise redact --profile maximum</c> must run the Maximum profile's carrier policy. The CLI
/// built its request with the policy parsed from <c>--carrier-policy</c> even when no such flag was
/// given, and that parse starts from <see cref="CarrierScrubPolicy.Default"/> (all Strip), so the
/// profile's remove-whole carriers were silently replaced by strip: a page label holding the term
/// kept the text around it (the residue #1169 describes). An explicit <c>--carrier-policy</c> must
/// override only the carriers it names.
/// </summary>
public class RedactProfileCarrierPolicyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly Dictionary<string, string> _outputs = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-profile-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void TryParseCarrierPolicy_StartsFromTheBaselineItIsGiven()
    {
        var maximum = RedactionOptions.Maximum.CarrierPolicy;

        RedactCommand.TryParseCarrierPolicy(
            new[] { "uri=report-only" }, out var policy, out _, maximum).Should().BeTrue();

        policy.ModeFor(RedactionCarriers.ActionUris).Should().Be(CarrierScrubMode.ReportOnly, "the named carrier changes");
        policy.ModeFor(RedactionCarriers.StructTree).Should().Be(CarrierScrubMode.RemoveWhole,
            "a carrier the user did not name keeps the profile's mode");
        policy.ModeFor(RedactionCarriers.PageLabels).Should().Be(CarrierScrubMode.RemoveWhole);
        policy.ModeFor(RedactionCarriers.Info).Should().Be(CarrierScrubMode.Strip,
            "Maximum leaves /Info at Strip (it is removed by flag)");
    }

    /// <summary>
    /// A page whose first page-label range has a prefix that restates the term inside a longer
    /// string. Strip leaves "ACCT--" (what surrounded the term); RemoveWhole leaves nothing.
    /// </summary>
    private static byte[] PageWithTermInItsLabelPrefix()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (the visible SECRETNAME line) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /D /P (ACCT-SECRETNAME-) >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private byte[] Run(string key, params string[] args)
    {
        var input = TempPath(".pdf");
        File.WriteAllBytes(input, PageWithTermInItsLabelPrefix());
        var output = TempPath(".pdf");
        var all = new List<string> { "redact", input, output, "SECRETNAME" };
        all.AddRange(args);
        var stdout = Console.Out;
        var stderr = Console.Error;
        int exit;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            exit = Program.RunAsync(all.ToArray()).GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        exit.Should().Be(0);
        _outputs[key] = output;
        return File.ReadAllBytes(output);
    }

    [Fact]
    public void Standard_StripsTheTermAndLeavesTheTextAroundIt_TheControl()
    {
        var bytes = Run("standard", "--profile", "standard");

        SavedPdfLeakScanner.FindTerm(bytes, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(bytes, "ACCT").Should().NotBeEmpty(
            "the control: Standard strips the term only, so the oracle can see the residue it is looking for");
    }

    [Fact]
    public void Maximum_RemovesTheWholePageLabelPrefix_NotJustTheTerm()
    {
        var bytes = Run("maximum", "--profile", "maximum");

        SavedPdfLeakScanner.FindTerm(bytes, "SECRETNAME").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(bytes, "ACCT").Should().BeEmpty(
            "Maximum sets page labels to remove-whole, so no text around the term survives (#1169)");
    }

    [Fact]
    public void Maximum_WithAnExplicitCarrierPolicy_OnlyChangesTheCarrierItNames()
    {
        var bytes = Run("maximum+uri", "--profile", "maximum", "--carrier-policy", "uri=report-only");

        SavedPdfLeakScanner.FindTerm(bytes, "ACCT").Should().BeEmpty(
            "naming the uri carrier must not turn the page labels back into Strip");
    }
}
