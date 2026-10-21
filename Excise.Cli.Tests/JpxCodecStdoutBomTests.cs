using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #2084: the first JPX decode in a process must not put CSJ2K's UTF-8 BOM on
/// the CLI's stdout or stderr. Runs the real CLI in a child process and reads
/// the raw bytes: a <see cref="StreamReader"/> would strip a leading BOM and
/// hide the defect.
/// </summary>
public class JpxCodecStdoutBomTests
{
    internal static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // The 8x8 solid (10,20,200) JP2 from
    // ImageRequirementVerificationDifferentialTests.JpxEncodedSolidColor_MatchesMutoolDecode.
    private static readonly byte[] Jp2 = Convert.FromHexString(
        "0000000c6a5020200d0a870a00000014667479706a703220000000006a7032200000002d6a703268000000166" +
        "968647200000008000000080003070700000000000f636f6c7201000000000010000000a76a703263ff4fff5" +
        "1002f000000000008000000080000000000000000000000080000000800000000000000000003070101070101" +
        "070101ff52000c00000001000304040001ff5c000d4040484850484850484850ff6400250001437265617465" +
        "64206279204f70656e4a5045472076657273696f6e20322e352e34ff90000a0000000000260001ff93cfb0080" +
        "997cfb008081fcfb008044f808080808080808080ffd9");

    [Fact]
    public void RenderJson_OnJpxPdf_WritesNoBomAndParses()
    {
        var work = Directory.CreateTempSubdirectory("excise-2084-");
        try
        {
            var pdf = Path.Combine(work.FullName, "jpx.pdf");
            File.WriteAllBytes(pdf, BuildJpxPdf());
            var png = Path.Combine(work.FullName, "jpx.png");

            var (exit, stdout, stderr) = RunCli("render", pdf, "--page", "1", "--dpi", "72", "--output", png, "--json");

            exit.Should().Be(0, "render must succeed. stderr: " + Encoding.UTF8.GetString(stderr));
            File.Exists(png).Should().BeTrue();
            IndexOf(stdout, Bom).Should().Be(-1, "stdout is the CLI's JSON contract; CSJ2K's logger must not write a BOM into it");
            IndexOf(stderr, Bom).Should().Be(-1, "stderr must not carry CSJ2K's stray BOM either");
            var parse = () => JsonDocument.Parse(stdout);
            parse.Should().NotThrow("render --json stdout must be strict JSON");
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    private static (int Exit, byte[] Stdout, byte[] Stderr) RunCli(params string[] args)
    {
        var root = TestRepoLayout.LocalCheckoutRoot ?? throw new InvalidOperationException(
            "no local checkout above " + AppContext.BaseDirectory);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = root,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add("Excise.Cli");
        psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = ReadAllAsync(p.StandardOutput.BaseStream);
        var e = ReadAllAsync(p.StandardError.BaseStream);
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("excise render did not exit within 120 s");
        }
        return (p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
    }

    internal static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    internal static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    internal static byte[] BuildJpxPdf()
    {
        const string content = "q 8 0 0 8 0 0 cm /Im0 Do Q";
        var latin1 = Encoding.Latin1;
        using var pdf = new MemoryStream();
        var offsets = new long[6];
        void Text(string s) { var b = latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }

        Text("%PDF-1.7\n");
        offsets[1] = pdf.Position; Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = pdf.Position; Text("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 8 8] >>\nendobj\n");
        offsets[3] = pdf.Position; Text("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets[4] = pdf.Position; Text($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = pdf.Position;
        Text($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode /Length {Jp2.Length} >>\nstream\n");
        pdf.Write(Jp2);
        Text("\nendstream\nendobj\n");
        var xref = pdf.Position;
        Text("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}
