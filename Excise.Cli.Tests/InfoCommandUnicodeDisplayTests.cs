using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1205 — the CLI's human-readable <c>info</c> output.
///
/// <para>A terminal honours bidi controls exactly as a GUI label does, so a
/// <c>/Title</c> carrying U+202E makes <c>excise info</c> print metadata that
/// reads as something other than what the file holds. This is the boundary that
/// forced <c>UnicodeTextSafety</c> out of <c>Excise.App</c> and into
/// <c>Excise.Core</c>: the CLI cannot reference the GUI, and a second copy of
/// the policy would be the divergence this closes.</para>
///
/// <para>Only the human path is escaped. <c>--json</c> is left alone because
/// <c>System.Text.Json</c>'s default encoder already writes non-ASCII as
/// <c>\uXXXX</c>, so the control is inert there AND the value stays exact for
/// a machine consumer.</para>
/// </summary>
public sealed class InfoCommandUnicodeDisplayTests : IDisposable
{
    private const string Rlo = "‮";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"excise-info-unicode-{Guid.NewGuid():N}");

    public InfoCommandUnicodeDisplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Info_HumanOutput_MakesABidiOverrideInTheTitleExplicit()
    {
        var path = WritePdfWithTitle($"Quarterly{Rlo}Report");

        var stdout = await RunAsync(path);

        stdout.Should().Contain("[U+202E]",
            "the control is displayed, not obeyed — a terminal would otherwise " +
            "reorder the printed title");
        stdout.Should().NotContain(Rlo, "the raw control never reaches the terminal");
    }

    [Fact]
    public async Task Info_HumanOutput_LeavesOrdinaryMetadataExactlyAlone()
    {
        // The rule that keeps this from being corruption. Accents and non-Latin
        // scripts are ordinary text and must survive byte-for-byte.
        var path = WritePdfWithTitle("Rapport trimestriel — Société Générale");

        var stdout = await RunAsync(path);

        stdout.Should().Contain("Rapport trimestriel — Société Générale");
        stdout.Should().NotContain("[U+");
    }

    private static async Task<string> RunAsync(string path)
    {
        var previousOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            (await Program.RunAsync(new[] { "info", path })).Should().Be(0);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
        return captured.ToString();
    }

    private string WritePdfWithTitle(string title)
    {
        var path = Path.Combine(_dir, "titled.pdf");
        File.WriteAllBytes(path, Build(title));
        return path;
    }

    private static byte[] Build(string title)
    {
        // /Info /Title as a UTF-16BE PDF text string (§7.9.2.2) so any script or
        // control character round-trips through the parser unchanged.
        var utf16 = Encoding.BigEndianUnicode.GetBytes(title);
        var hex = new StringBuilder("FEFF");
        foreach (var b in utf16) hex.Append(b.ToString("X2"));

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            "<< /Length 6 >>\nstream\nBT ET\nendstream",
            $"<< /Title <{hex}> >>",
        };

        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Info {bodies.Length} 0 R /Size {bodies.Length + 1} >>\n"
            + $"startxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
