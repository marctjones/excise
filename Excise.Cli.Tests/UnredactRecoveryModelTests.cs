using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1587 — the recovery model as the CLI delivers it. These run the handler
/// IN PROCESS rather than spawning the CLI: the contract under test is the
/// report's shape, and a subprocess adds a minute of build and startup per
/// case to test the same objects.
///
/// <para>What matters here is the part a caller can be misled by. A report that
/// lists findings and nothing else invites reading recall into it that was
/// never measured, so these pin the denominator (marks), the per-mark outcome,
/// the location that makes a finding actionable, and the explicit statement of
/// which channels did NOT run.</para>
/// </summary>
public class UnredactRecoveryModelTests
{
    [Fact]
    public void Report_CountsMarksAndGradesEachOne()
    {
        var path = WriteFixture(TextUnderBox("MANAFORT"));
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);

            outcome.Report.Should().NotBeNull();
            var recovery = outcome.Report!.Recovery;
            recovery.Should().NotBeNull();
            recovery!.Marks.Should().Be(1);
            recovery.MarksRecovered.Should().Be(1);
            recovery.MarkSummaries.Should().ContainSingle()
                .Which.Outcome.Should().Be("recovered");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMarkNothingRecovered_IsStillCountedAndGradedNotRecovered()
    {
        // The row the whole model exists for. The box is over EMPTY page space,
        // so no channel finds anything under it -- and the report must still
        // say the redaction is there and held, rather than omitting it and
        // reading as a clean document.
        var path = WriteFixture(
            "BT /F1 14 Tf 72 700 Td (Public heading) Tj ET\n" +
            "q 0 0 0 rg 72 400 200 18 re f Q\n");
        try
        {
            var recovery = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken).Report!.Recovery!;

            recovery.Marks.Should().Be(1);
            recovery.MarksNotRecovered.Should().Be(1);
            recovery.MarkSummaries[0].Outcome.Should().Be("not-recovered");
            recovery.MarkSummaries[0].Findings.Should().Be(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EveryFindingCarriesItsChannelConfidenceAndLocation()
    {
        var path = WriteFixture(TextUnderBox("MANAFORT"));
        try
        {
            var recovery = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken).Report!.Recovery!;
            var finding = recovery.Linked.Single(f => f.Channel == "hidden-text");

            finding.Confidence.Should().Be("certain");
            finding.Text.Should().Be("MANAFORT");
            finding.Page.Should().Be(1);
            finding.Rect.Should().NotBeNull().And.HaveCount(4);
            finding.LocationProvenance.Should().Be("glyph boxes");
            finding.MarkId.Should().Be(recovery.MarkSummaries[0].Id);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChannelsThatDidNotRun_AreNamedWithAReason()
    {
        // Without this the report reads like full coverage over channels it
        // never opened -- and "no finding" from a channel that never ran is
        // indistinguishable from "no leak" to anyone reading the JSON.
        var path = WriteFixture(TextUnderBox("X"));
        try
        {
            var recovery = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken).Report!.Recovery!;

            recovery.ChannelsRun.Should().Contain("hidden-text");
            recovery.ChannelsSkipped.Should().ContainKey("residue")
                .WhoseValue.Should().Contain("--mode certain");
            recovery.ChannelsSkipped.Should().ContainKey("ocr-differential");

            // prior-revision USED to be skipped as "not implemented"; it runs
            // now (#1592), which is why this asserts on xfa instead — and xfa
            // is skipped with a DOCUMENT-SPECIFIC reason rather than a blanket
            // one, so "no XFA findings" cannot be read as "no XFA here".
            recovery.ChannelsRun.Should().Contain("prior-revision");
            recovery.ChannelsSkipped.Should().ContainKey("xfa")
                .WhoseValue.Should().Contain("no /AcroForm /XFA");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheRecoveryModelIsSerialisedIntoTheJsonReport()
    {
        // The JSON is the automation contract. A model that exists only in
        // memory helps nobody scripting against this.
        var path = WriteFixture(TextUnderBox("MANAFORT"));
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);
            var writer = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: true, writer, TextWriter.Null);

            using var document = JsonDocument.Parse(writer.ToString());
            var recovery = document.RootElement.GetProperty("recovery");
            recovery.GetProperty("marks").GetInt32().Should().Be(1);
            recovery.GetProperty("marksRecovered").GetInt32().Should().Be(1);
            recovery.GetProperty("markSummaries")[0]
                .GetProperty("outcome").GetString().Should().Be("recovered");
            recovery.GetProperty("linked")[0]
                .GetProperty("confidence").GetString().Should().Be("certain");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HumanOutput_LeadsWithTheMarkDenominatorNotTheFindingCount()
    {
        var path = WriteFixture(TextUnderBox("MANAFORT"));
        try
        {
            var outcome = UnredactCommandHandler.Execute(Input(path), TestContext.Current.CancellationToken);
            var writer = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: false, writer, TextWriter.Null);
            var text = writer.ToString();

            text.Should().Contain("MARKS — 1 redaction mark(s)");
            text.Should().Contain("channels NOT run",
                "a reader must not take silence about a channel for a clean result");
            text.IndexOf("MARKS", System.StringComparison.Ordinal).Should()
                .BeLessThan(text.IndexOf("QUANTIFICATION", System.StringComparison.Ordinal),
                    "coverage before count: the finding list alone reads as more recovery than was measured");
        }
        finally { File.Delete(path); }
    }

    private static UnredactCommandInput Input(string path) => new(
        path, "certain", DictionaryPath: null, Tolerance: 0.5,
        MaxCandidates: 200, UseOcr: false, NoCorroboration: false);

    private static string TextUnderBox(string text)
    {
        var width = text.Length * 14 * 0.78 + 6;
        return $"BT /F1 14 Tf 72 700 Td ({text}) Tj ET\n" +
               $"q 0 0 0 rg 70 697 {width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} 17 re f Q\n";
    }

    private static string WriteFixture(string content)
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

        var path = Path.Combine(Path.GetTempPath(), $"excise-unredact-model-{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }
}
