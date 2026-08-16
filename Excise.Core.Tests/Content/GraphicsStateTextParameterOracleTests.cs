using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// The independent-oracle half of the #983 gate.
///
/// <para><b>Why it has to exist.</b> #983 is a defect BOTH content parsers
/// shared: neither restored the §8.4.1 Table 52 text state at <c>Q</c>. A
/// differential (#980) cannot see that — it only asks whether the two agree,
/// and they agreed. <see cref="GraphicsStateTextParameterTests"/> closes half
/// the gap by checking a property instead of a twin, but the property is
/// transcribed from the spec BY THE SAME AUTHOR AS THE FIX, which is the
/// no-self-oracle rule one level up. So this file asks a tool that is not
/// excise: mutool reads the same bytes and reports its own font size and glyph
/// positions for the run AFTER the <c>Q</c>.</para>
///
/// <para><b>What it can catch:</b> any disagreement with mutool about the text
/// state in force after a <c>Q</c> — font size, and the advance widths that
/// Tc/Tw/Tz feed. It caught nothing new when written (both machines had just
/// been fixed); its value is that a future regression cannot be waved through
/// by excise agreeing with excise.</para>
///
/// <para><b>What it cannot catch:</b></para>
/// <list type="bullet">
/// <item>Anything <c>stext</c> does not expose — text rendering mode
/// (<c>Tr</c>), clipping, colour. Those stay unoracled.</item>
/// <item>Anything mutool itself gets wrong. This gate pins AGREEMENT with one
/// reference implementation, not correctness. mupdf's <c>pdf_gstate</c> carries
/// a <c>pdf_text_state</c> and <c>Q</c> pops the whole gstate, which is why it
/// is a usable reference here.</item>
/// <item>Absolute glyph boxes. mutool's ascent/descent modelling is its own;
/// only the font SIZE and the ADVANCE DELTAS between consecutive glyphs are
/// compared, and the deltas with a tolerance. (Inter-glyph spacing in mutool's
/// stext also differs between the macOS and Linux builds — deltas over a plain
/// LTR ASCII fixture are the part that is stable on both.)</item>
/// <item>Nothing at all when mutool is absent: the test skips. It is
/// allow-listed as <c>[requires: tool:mutool]</c>, so CI (which has no mutool)
/// expects the skip and a dev box expects the run.</item>
/// </list>
/// </summary>
public class GraphicsStateTextParameterOracleTests
{
    /// <summary>
    /// A styled run bracketed in <c>q</c>/<c>Q</c>, then an unstyled run that
    /// sets NO text-state operator of its own — so everything it draws with is
    /// whatever <c>Q</c> restored. Before #983 excise drew "After" at 36pt with
    /// 2 units of character spacing; mutool draws it at 12pt with none.
    /// </summary>
    private const string Content =
        "BT /F1 12 Tf 1 0 0 1 72 700 Tm (Base) Tj ET\n"
      + "q\n"
      + "BT /F1 36 Tf 2 Tc 1 0 0 1 72 650 Tm (Big) Tj ET\n"
      + "Q\n"
      + "BT 1 0 0 1 72 600 Tm (After) Tj ET";

    [Fact]
    public void AfterQ_FontSizeAndAdvances_AgreeWithMutool()
    {
        var mutool = FindOnPath("mutool");
        Assert.SkipWhen(mutool is null, "mutool not on PATH");

        var pdf = ParityFixture.Build(Content);
        var oracle = MutoolLine(mutool!, pdf, "After");

        // The oracle's own verdict first, asserted independently of excise: if
        // mutool ever stopped restoring the text state at Q, this fails here
        // rather than silently becoming a gate that pins the defect.
        oracle.FontSize.Should().Be(12,
            "mutool restores the pre-q 12pt font at Q — if this fails, the "
            + "reference changed and the comparison below means nothing");
        oracle.Advances.Should().HaveCount(4, "\"After\" is five glyphs");

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var letters = new TextExtractor(page) { IncludeFormFieldValues = false }
            .ExtractLetters()
            .Where(l => l.StartY > 595 && l.StartY < 605)
            .ToList();

        string.Concat(letters.Select(l => l.Value)).Should().Be("After");
        letters.Should().AllSatisfy(l => l.FontSize.Should().Be(oracle.FontSize,
            "excise must draw the post-Q run with the size mutool sees, not the "
            + "36pt the bracketed run set (#983)"));

        for (int i = 0; i < oracle.Advances.Count; i++)
        {
            var exciseAdvance = letters[i + 1].StartX - letters[i].StartX;
            exciseAdvance.Should().BeApproximately(oracle.Advances[i], 0.05,
                $"advance {i} carries Tc/Tw/Tz — the bracketed 2 Tc must not "
                + "survive the Q");
        }

        // ContentStreamParser is the OTHER machine #983 fixed, and redaction
        // reads ITS boxes. Compared against the same oracle line's horizontal
        // extent: a leaked 36pt/2 Tc makes the operator box far too wide.
        var showOp = new ContentStreamParser(page.GetContentStreamBytes(), page)
            .Parse().Operators
            .Single(op => op.Name == "Tj" && op.TextContent == "After");
        showOp.BoundingBox.Should().NotBeNull();
        showOp.BoundingBox!.Value.Left.Should().BeApproximately(oracle.Left, 0.05,
            "the post-Q run starts where mutool says it does");
        showOp.BoundingBox!.Value.Right.Should().BeApproximately(oracle.Right, 0.05,
            "and ends there — its width is the sum of the advances the restored "
            + "text state produces");
    }

    // ---------------------------------------------------------------

    private readonly record struct OracleLine(
        double FontSize, IReadOnlyList<double> Advances, double Left, double Right);

    /// <summary>
    /// mutool's structured-text view of the line whose text is
    /// <paramref name="text"/>: the font size it reports and the x deltas
    /// between consecutive glyph origins.
    /// </summary>
    private static OracleLine MutoolLine(string mutool, byte[] pdf, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-qq-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var (exitCode, stdout) = RunProcess(mutool, "draw", "-F", "stext", "-o", "-", path, "1");
            exitCode.Should().Be(0, $"mutool should read the fixture:\n{stdout}");

            var line = XDocument.Parse(stdout)
                .Descendants("line")
                .FirstOrDefault(l => (string?)l.Attribute("text") == text);
            line.Should().NotBeNull($"mutool should find the line \"{text}\" in:\n{stdout}");

            var font = line!.Descendants("font").First();
            var size = double.Parse((string)font.Attribute("size")!, CultureInfo.InvariantCulture);

            var xs = font.Descendants("char")
                .Select(c => double.Parse((string)c.Attribute("x")!, CultureInfo.InvariantCulture))
                .ToList();
            var advances = xs.Zip(xs.Skip(1), (a, b) => b - a).ToList();

            // The line's HORIZONTAL extent only. mutool's vertical extent comes
            // from its own ascent/descent modelling and is not comparable with
            // excise's glyph cells; the left and right edges are pen positions
            // and are.
            var bbox = ((string)line.Attribute("bbox")!)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
                .ToArray();
            return new OracleLine(size, advances, bbox[0], bbox[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate)) return candidate;
            var windows = candidate + ".exe";
            if (File.Exists(windows)) return windows;
        }
        return null;
    }

    private static (int ExitCode, string Output) RunProcess(string executable, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000).Should().BeTrue($"{executable} should exit within 30 seconds");
        return (proc.ExitCode, stdout);
    }
}
