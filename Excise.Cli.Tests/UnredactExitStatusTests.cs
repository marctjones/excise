using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1592 — the exit status and the all-clear must reflect EVERY channel.
///
/// <para><b>Why this file exists.</b> The channels added in #1587/#1592 —
/// prior revision, marked content, form fields, thumbnails, attachments —
/// report into the recovery model and not into the two legacy Certain/Residue
/// lists. The exit code, the "0 RECOVERED" headline and the green tick were all
/// computed from those legacy lists, so on an incremental-update fixture whose
/// redacted name the prior-revision channel HAD recovered, `excise unredact`
/// printed "✓ No recoverable text or measurable residue found." and exited 0.
/// A script checking the exit code would have called that document clean.</para>
///
/// <para>That is the precise failure this tool exists to prevent, committed by
/// the tool itself, so it gets its own gate rather than a line in a larger
/// test. A channel added later that forgets to feed the status will fail
/// here.</para>
/// </summary>
public class UnredactExitStatusTests
{
    [Fact]
    public void AModelOnlyChannelRecovery_ExitsRecoveredNotClean()
    {
        var path = WriteIncrementalUpdate("MANAFORT", "REDACTED");
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);

            outcome.Report!.Recovery!.Unlinked
                .Concat(outcome.Report.Recovery.Linked)
                .Should().Contain(f => f.Channel == "prior-revision" && f.Text == "MANAFORT");

            // ⚠️ This used to assert `Certain` was EMPTY — "that is the whole
            // point" — because the prior-revision channel existed only in the
            // recovery model. It is not empty any more: the carrier scan gained
            // its own revision coverage (feat/unredact-carriers), so both paths
            // now see this leak and they AGREE. That is strictly better, and the
            // property under test was never "one path is blind"; it is that the
            // status is read from EVERY channel. Asserting the agreement is the
            // honest version of the same test.
            outcome.Report.Certain.Should().Contain(f => f.Text == "MANAFORT",
                "the carrier scan sees the prior revision too, and the two paths must not disagree");
            outcome.Report.Residue.Should().BeEmpty();

            outcome.ExitCode.Should().Be(3,
                "3 means text was recovered; a status read from the legacy lists alone returned 0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AModelOnlyChannelRecovery_DoesNotPrintTheAllClear()
    {
        var path = WriteIncrementalUpdate("MANAFORT", "REDACTED");
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);
            var writer = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: false, writer, TextWriter.Null);
            var text = writer.ToString();

            text.Should().NotContain("No recoverable text",
                "the green tick over a recovered name is the exact false reassurance " +
                "this tool exists to remove");
            text.Should().Contain("RECOVERED");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ALegacyChannelRecovery_IsCountedOnceNotTwice()
    {
        // hidden-text reports into BOTH the legacy list and the model, so the
        // headline must not double it.
        var path = WriteBoxOverText("MANAFORT");
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);

            outcome.Report!.Quantification.Findings.Should().Be(1);
            outcome.Report.Quantification.FullyRecoverable.Should().Be(1);
            outcome.ExitCode.Should().Be(3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADocumentWithNothingToFind_StillExitsClean()
    {
        // The negative control. Without it, a status that returned 3
        // unconditionally would pass every test above.
        var path = WriteBoxOverText("VISIBLE", drawBox: false);
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);
            outcome.ExitCode.Should().Be(0);

            var writer = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: false, writer, TextWriter.Null);
            writer.ToString().Should().Contain("No recoverable text");
        }
        finally { File.Delete(path); }
    }

    private static UnredactCommandInput Input(string path) => new(
        path, "certain", DictionaryPath: null, Tolerance: 0.5,
        MaxCandidates: 200, UseOcr: false, NoCorroboration: false);

    private static string WriteBoxOverText(string text, bool drawBox = true)
    {
        var width = text.Length * 14 * 0.78 + 6;
        var content = $"BT /F1 14 Tf 72 700 Td ({text}) Tj ET\n";
        if (drawBox)
        {
            content += "q 0 0 0 rg 70 697 " +
                width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                " 17 re f Q\n";
        }
        return WriteFile(BuildPdf(content));
    }

    /// <summary>
    /// A PDF whose first revision shows <paramref name="original"/> and whose
    /// appended revision replaces the content stream with
    /// <paramref name="replacement"/>. The original revision stays whole at the
    /// front of the file (§7.5.6).
    /// </summary>
    private static string WriteIncrementalUpdate(string original, string replacement)
    {
        var first = BuildPdf($"BT /F1 14 Tf 72 700 Td ({original}) Tj ET\n");
        var newContent = Encoding.Latin1.GetBytes($"BT /F1 14 Tf 72 700 Td ({replacement}) Tj ET\n");

        using var ms = new MemoryStream();
        ms.Write(first);
        var offset = ms.Position;
        void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        Write($"4 0 obj\n<< /Length {newContent.Length} >>\nstream\n");
        ms.Write(newContent);
        Write("\nendstream\nendobj\n");

        var xref = ms.Position;
        var firstText = Encoding.Latin1.GetString(first);
        var idx = firstText.LastIndexOf("startxref", System.StringComparison.Ordinal);
        var prev = new string(firstText[(idx + 9)..]
            .SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

        Write($"xref\n4 1\n{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        return WriteFile(ms.ToArray());
    }

    private static byte[] BuildPdf(string content)
    {
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
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string WriteFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-exit-{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
