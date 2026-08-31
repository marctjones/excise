using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli.Commands;
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
    public void CertainMode_QuantifiesTheChannel_FullyRecoverableCountAndBits()
    {
        // #1126 — the audit answers "how much, by what channel", not just yes/no.
        var pdf = WriteFakeRedaction();
        try
        {
            var (exit, output) = Run(pdf, "--mode", "certain", "--json");
            exit.Should().Be(3);
            output.Should().Contain("\"quantification\"", "the quantification block is the #1126 output");
            output.Should().Contain("\"fullyRecoverable\": 1",
                "text present under a box is fully recoverable — one finding, no bits to guess");
            output.Should().Contain("\"widthResidueBitsTotal\": 0",
                "a text-present finding leaves no width residue to quantify in bits");
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

    [Fact]
    public void Handler_CertainChannelReturnsTypedReportWithoutSecondCliProcess()
    {
        var pdf = WriteFakeRedaction();
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
            outcome.Error.Should().BeNull();
            outcome.Report.Should().NotBeNull();
            outcome.Report!.Certain.Should().ContainSingle();
            outcome.Report.Certain[0].Text.Should().Contain("Farrar");
            outcome.Report.Certain[0].HiddenBy.Should().Contain("rectangle");
            outcome.Report.Residue.Should().BeEmpty();
            outcome.Report.Quantification.Should().Be(new UnredactQuantification(
                Findings: 1,
                FullyRecoverable: 1,
                WidthResidueGaps: 0,
                WidthResidueBitsTotal: 0,
                Recovered: 1,
                Corroboration: "n/a (certain mode)"));
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void Handler_OwnsValidationCancellationAndExitStatus()
    {
        var pdf = WriteFakeRedaction();
        try
        {
            UnredactCommandInput Input(string mode, string? dictionary = null) => new(
                pdf,
                mode,
                dictionary,
                Tolerance: 0.5,
                MaxCandidates: 200,
                UseOcr: false,
                NoCorroboration: false);

            var invalidMode = UnredactCommandHandler.Execute(
                Input("wrong"),
                TestContext.Current.CancellationToken);
            invalidMode.ExitCode.Should().Be(2);
            invalidMode.Error.Should().Be("--mode must be certain, residue, or both");

            var missingDictionary = UnredactCommandHandler.Execute(
                Input("residue"),
                TestContext.Current.CancellationToken);
            missingDictionary.ExitCode.Should().Be(2);
            missingDictionary.Error.Should().Contain("dictionary");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledOutcome = UnredactCommandHandler.Execute(Input("certain"), cancelled.Token);
            cancelledOutcome.ExitCode.Should().Be(1);
            cancelledOutcome.Error.Should().Be("Operation cancelled.");
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void TypedOutput_PreservesJsonAndHumanContracts()
    {
        var report = new UnredactReport(
            new UnredactQuantification(1, 1, 0, 0, 1, "n/a (certain mode)"),
            new[] { new UnredactCertainFinding(1, "SECRET", "black filled rectangle", 12.3, 45.6) },
            Array.Empty<UnredactResidueFinding>());
        var outcome = new UnredactCommandOutcome(3, report, null);

        using var json = new StringWriter();
        using var jsonError = new StringWriter();
        UnredactCommandOutput.Write(outcome, json: true, json, jsonError);
        jsonError.ToString().Should().BeEmpty();
        json.ToString().Should().Contain("\"fullyRecoverable\": 1");
        json.ToString().Should().Contain("\"hiddenBy\": \"black filled rectangle\"");
        json.ToString().Should().NotContain("\"confidence\"",
            "non-OCR findings did not expose a null confidence field before the extraction");
        using (var parsed = JsonDocument.Parse(json.ToString()))
        {
            parsed.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().Equal("quantification", "certain", "residue");
            parsed.RootElement.GetProperty("quantification").EnumerateObject()
                .Select(property => property.Name)
                .Should().Equal(
                    "findings", "fullyRecoverable", "widthResidueGaps",
                    "widthResidueBitsTotal", "recovered", "corroboration");
            parsed.RootElement.GetProperty("certain")[0].EnumerateObject()
                .Select(property => property.Name)
                .Should().Equal("page", "text", "hiddenBy", "x", "y");
        }

        var residueReport = new UnredactReport(
            new UnredactQuantification(1, 0, 1, 1, 0, "mutool (independent)"),
            Array.Empty<UnredactCertainFinding>(),
            new[]
            {
                new UnredactResidueFinding(
                    1, 10.5, "Helvetica", 12, "Standard14Exact",
                    2, 1, 0.5, new[] { "ALPHA", "BRAVO" }, "ok"),
            });
        using var residueJson = new StringWriter();
        UnredactCommandOutput.Write(
            new UnredactCommandOutcome(4, residueReport, null),
            json: true,
            residueJson,
            jsonError);
        using (var parsed = JsonDocument.Parse(residueJson.ToString()))
        {
            parsed.RootElement.GetProperty("residue")[0].EnumerateObject()
                .Select(property => property.Name)
                .Should().Equal(
                    "page", "gapWidthPt", "font", "sizePt", "metricSource",
                    "candidatesFit", "residualEntropyBits", "contextAdjustedBits",
                    "candidates", "status");
        }

        using var human = new StringWriter();
        using var humanError = new StringWriter();
        UnredactCommandOutput.Write(outcome, json: false, human, humanError);
        humanError.ToString().Should().BeEmpty();
        human.ToString().Should().Contain(
            "QUANTIFICATION — 1 finding(s), 1 RECOVERED: 1 text present, 0 width-residue gap(s) leaking 0 bits total.");
        human.ToString().Should().Contain(
            "page 1 (12.3,45.6) [black filled rectangle]: \"SECRET\"");
    }

    private static string RepoRootPath() => RepoRoot();

    /// <summary>A synthetic corpus case path, or null if the corpus is absent.</summary>
    private static string? CorpusCase(string idContains, string method)
    {
        var dir = Path.Combine(RepoRoot(), "test-pdfs", "redaction-synthetic");
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "*.pdf")
            .FirstOrDefault(f => Path.GetFileName(f).Contains(idContains)
                                 && Path.GetFileName(f).Contains(method));
    }

    // ── difficulty-graded recovery through the actual CLI ──────────────────
    // The engine's recall@N per band is proven in ResidueRecoveryRecallTests;
    // these confirm the CLI faithfully EXPOSES that recovery -- easy cases
    // recover, negative controls do not -- end to end through the process.

    [Fact]
    public void WidthPreserving_ResidueMode_ListsTheAnswerAmongCandidates()
    {
        var pdf = CorpusCase("B1-helvetica12-original", "original");
        Assert.SkipWhen(pdf == null, "synthetic corpus absent");
        // Redact the known answer, then residue-recover with a name dictionary.
        var answer = Path.GetFileNameWithoutExtension(pdf!).Split('-').Last();
        var redacted = Path.Combine(Path.GetTempPath(), $"ur-{Guid.NewGuid():N}.pdf");
        var dict = Path.Combine(Path.GetTempPath(), $"dict-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(dict, new[] { answer, "Zzzzzz", "Qqqqqqqqqq" });
        try
        {
            RunRedact(pdf!, redacted, answer);
            var (exit, output) = Run(redacted, "--mode", "residue", "--dictionary", dict);
            exit.Should().Be(4, "a width-preserving redaction leaves residue");
            output.Should().Contain(answer, "the true answer must be among the width-fit candidates");
            output.Should().Contain("bits", "residue reports entropy, never asserts");
        }
        finally { File.Delete(redacted); File.Delete(dict); }
    }

    [Fact]
    public void Handler_ResidueChannelReturnsTypedFindingWithoutSecondUnredactProcess()
    {
        var pdf = CorpusCase("B1-helvetica12-original", "original");
        Assert.SkipWhen(pdf == null, "synthetic corpus absent");
        var answer = Path.GetFileNameWithoutExtension(pdf!).Split('-').Last();
        var redacted = Path.Combine(Path.GetTempPath(), $"ur-handler-{Guid.NewGuid():N}.pdf");
        var dict = Path.Combine(Path.GetTempPath(), $"dict-handler-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(dict, new[] { answer, "Zzzzzz", "Qqqqqqqqqq" });
        try
        {
            RunRedact(pdf!, redacted, answer);
            var outcome = UnredactCommandHandler.Execute(
                new UnredactCommandInput(
                    redacted,
                    "residue",
                    dict,
                    Tolerance: 0.5,
                    MaxCandidates: 200,
                    UseOcr: false,
                    NoCorroboration: false),
                TestContext.Current.CancellationToken);

            outcome.ExitCode.Should().Be(4);
            outcome.Report!.Certain.Should().BeEmpty();
            outcome.Report.Residue.Should().Contain(
                finding => finding.Candidates.Contains(answer));
            outcome.Report.Quantification.Corroboration.Should().Be("mutool (independent)");
        }
        finally { File.Delete(redacted); File.Delete(dict); }
    }

    [Fact]
    public void WidthClosed_ResidueMode_FindsNothing()
    {
        var pdf = CorpusCase("B8-", "width-closing");
        Assert.SkipWhen(pdf == null, "synthetic corpus absent");
        var dict = Path.Combine(Path.GetTempPath(), $"dict-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(dict, new[] { "James", "John", "David" });
        try
        {
            var (exit, _) = Run(pdf!, "--mode", "residue", "--dictionary", dict);
            exit.Should().Be(0, "a width-closed redaction leaves no gap -- the negative control");
        }
        finally { File.Delete(dict); }
    }


    [Fact]
    public void CertainMode_BoxOverJustTheWord_IsRecovered()
    {
        // #1149: the common real fake redaction covers only the sensitive word
        // inside a longer line. This used to read as clean (box < 50% of the
        // operator); now the covered glyph RUN is reported.
        var pdf = CorpusCase("B0-", "under-box-black-on-white");
        Assert.SkipWhen(pdf == null, "synthetic corpus absent");
        var (exit, output) = Run(pdf!, "--mode", "certain");
        exit.Should().Be(3, "a box over just the word still hides recoverable text");
        output.Should().Contain("CERTAIN");
    }

    [Fact]
    public void ResidueMode_RecoversTheRedactedWordFromTheWidthLeak_ExitCode4()
    {
        // #1127 (Marc: recover text). Redact a word with excise, then recover it
        // from the width gap excise leaves — the adversarial proof that the width
        // channel #1116 measured is exploitable, end to end through the CLI.
        var src = Path.Combine(Path.GetTempPath(), $"rec-src-{Guid.NewGuid():N}.pdf");
        var dst = Path.Combine(Path.GetTempPath(), $"rec-dst-{Guid.NewGuid():N}.pdf");
        var dict = Path.Combine(Path.GetTempPath(), $"rec-dict-{Guid.NewGuid():N}.txt");
        try
        {
            // Contiguous so the removed span is exactly the word's width.
            File.WriteAllBytes(src, Encoding.Latin1.GetBytes(
                "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
                "4 0 obj\n<< /Length 46 >>\nstream\nBT /F1 18 Tf 72 700 Td (AAASECRETWORDZZZ) Tj ET\nendstream\nendobj\n" +
                "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n" +
                "trailer\n<< /Size 6 /Root 1 0 R >>\n%%EOF"));
            File.WriteAllText(dict, "SECRETWORD\nHELLOWORLD\nBANANARAMA\nTESTINGXYZ\n");

            RunRedact(src, dst, "SECRETWORD");

            var (exit, output) = Run(dst, "--mode", "residue", "--dictionary", dict);
            exit.Should().Be(4, "residue-only recovery -> exit 4");
            output.Should().Contain("RECOVERED \"SECRETWORD\"",
                "the width gap admits exactly one dictionary word — excise recovers what it redacted");
            output.Should().Contain("1 RECOVERED", "the quantification headline counts the recovery");
        }
        finally { File.Delete(src); File.Delete(dst); File.Delete(dict); }
    }

    [Fact]
    public void WidthClosingRedaction_DefeatsResidueRecovery_WhereDefaultDoesNot()
    {
        // #1145 — the defence for the leak #1116 measured and #1127 exploits.
        var src = Path.Combine(Path.GetTempPath(), $"wc-src-{Guid.NewGuid():N}.pdf");
        var def = Path.Combine(Path.GetTempPath(), $"wc-def-{Guid.NewGuid():N}.pdf");
        var wc = Path.Combine(Path.GetTempPath(), $"wc-wc-{Guid.NewGuid():N}.pdf");
        var dict = Path.Combine(Path.GetTempPath(), $"wc-dict-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(src, Encoding.Latin1.GetBytes(
                "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
                "4 0 obj\n<< /Length 46 >>\nstream\nBT /F1 18 Tf 72 700 Td (AAASECRETWORDZZZ) Tj ET\nendstream\nendobj\n" +
                "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n" +
                "trailer\n<< /Size 6 /Root 1 0 R >>\n%%EOF"));
            File.WriteAllText(dict, "SECRETWORD\nHELLOWORLD\nBANANARAMA\n");

            RunRedact(src, def, "SECRETWORD");                     // default: width-preserving
            RunRedact(src, wc, "SECRETWORD", "--close-width");     // #1145: width-closing

            // Default output leaks the width -> recoverable (exit 4).
            var (defExit, defOut) = Run(def, "--mode", "residue", "--dictionary", dict);
            defExit.Should().Be(4, "the default redaction leaves the width; residue recovers it");
            defOut.Should().Contain("RECOVERED \"SECRETWORD\"");

            // Width-closed output leaks nothing -> clean (exit 0).
            var (wcExit, wcOut) = Run(wc, "--mode", "residue", "--dictionary", dict);
            wcExit.Should().Be(0, "width-closing destroys the residue channel; nothing to recover");
            wcOut.Should().Contain("No recoverable text");
        }
        finally { File.Delete(src); File.Delete(def); File.Delete(wc); File.Delete(dict); }
    }

    private static void RunRedact(string src, string dst, string term, params string[] extra)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = RepoRoot(),
        };
        foreach (var a in new[] { "run", "--project", "Excise.Cli", "--no-build", "--", "redact", src, dst, term })
            psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
    }
}
