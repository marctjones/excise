using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A fast, excise-only slice of the redaction bench over the carrier traps:
/// redact each trap's token with <c>RedactText</c> (default options), save, and
/// ask two readers whether the token survived — qpdf's object dump (not excise)
/// and the unredact carrier channel (per-carrier attribution). A SURVEY: it
/// prints the table and asserts only that it ran. The cross-tool version is
/// <see cref="RedactionBenchmarkRunner"/>'s <c>redaction-carrier-traps</c> corpus.
/// </summary>
public sealed class CarrierTrapExciseRedactionSurveyTests
{
    private readonly ITestOutputHelper _out;
    public CarrierTrapExciseRedactionSurveyTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Survey_ExciseRedactText_OverEveryCarrierTrap()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CARRIER_TRAP_SURVEY") == "1",
            "set CARRIER_TRAP_SURVEY=1 to run the excise carrier-trap redaction survey");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent reader (brew install qpdf)");

        // CARRIER_TRAP_SURVEY_KEEP=<dir> keeps each input and redacted output
        // there, for reproduction with independent tools.
        var keep = Environment.GetEnvironmentVariable("CARRIER_TRAP_SURVEY_KEEP");
        var dir = string.IsNullOrEmpty(keep) ? Path.Combine(Path.GetTempPath(), $"carrier-survey-{Guid.NewGuid():N}") : keep;
        Directory.CreateDirectory(dir);
        var rows = new List<string>();
        var leaks = 0;
        try
        {
            foreach (var trap in CarrierTrapFixtures.All.Where(t => t.InBench))
            {
                var output = Path.Combine(dir, trap.Id + ".pdf");
                string verdict;
                try
                {
                    var input = trap.Build(true);
                    if (!string.IsNullOrEmpty(keep))
                        File.WriteAllBytes(Path.Combine(dir, $"{trap.Id}--{trap.Token}.input.pdf"), input);
                    using (var doc = PdfDocument.Open(input))
                    {
                        doc.RedactText(trap.Token);
                        doc.Save(output);
                    }
                    var qpdf = CarrierTrapIndependentCorroborationTests.QpdfDump(output)
                        .Contains(trap.Token, StringComparison.OrdinalIgnoreCase);
                    using var after = PdfDocument.Open(output);
                    var carriers = CarrierTextRecovery.Scan(after)
                        .Where(f => f.Kind == CarrierTextRecovery.CarrierFindingKind.Text
                                    && f.Text.Contains(trap.Token, StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.Carrier).Distinct().ToList();
                    var leaked = qpdf || carriers.Count > 0;
                    if (leaked) leaks++;
                    verdict = leaked
                        ? $"LEAK  qpdf={(qpdf ? "yes" : "no ")} carriers=[{string.Join("; ", carriers)}]"
                        : "clean";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    verdict = $"ERROR {ex.GetType().Name}: {ex.Message}";
                }
                rows.Add($"{trap.Id,-32} {verdict}");
            }
        }
        finally
        {
            if (string.IsNullOrEmpty(keep))
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        _out.WriteLine($"excise RedactText over {rows.Count} carrier traps — {leaks} leak(s):");
        foreach (var r in rows) _out.WriteLine("  " + r);
        Assert.True(rows.Count > 20, "the survey must actually exercise the traps");
    }
}
