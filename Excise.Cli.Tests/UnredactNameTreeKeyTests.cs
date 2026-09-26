using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1859 — how <c>excise unredact</c> treats a name-tree key (#1852): reported,
/// classed <see cref="RecoveryFindingClass.DocumentFurniture"/>, and counted in
/// the exit status like every other furniture carrier.
///
/// <para><b>Why the rule did not change, measured 2026-09-25.</b> Over the 79
/// corpus files with a catalog name tree (pdfjs, poppler, federal, pdfua,
/// xfa-real, ghent, local-real-world), 59 report 4,467 keys, and not one exit
/// code depends on them: every one of those files already exits 3 on other
/// furniture (<c>/Info</c>, XMP, <c>/URI</c>, orphans), which is #1703. The 293
/// failed-redaction files hold no key at all. So each candidate rule was judged
/// on what it would lose:</para>
/// <list type="bullet">
///   <item>Only keys with whitespace or a non-ASCII letter: drops the legacy
///     <c>/Dests</c> trap (a PDF name cannot hold a space) and heading-derived
///     keys (<c>adding-maintainers</c> in producingoss.pdf), which are the #1852
///     leak; its 5 survivors on clean files were binary <c>/IDS</c> digests.</item>
///   <item>Only against a term: <c>unredact</c> takes none, so the channel
///     would go and <c>KESTREL chapter</c> with it.</item>
///   <item>Informational, never setting the exit code: changes 0 of 79 exit
///     codes, and exits 0 on a document whose only leak is a key — the silent
///     exit the furniture policy refuses (#608, #1669). A distinct code for
///     unlinked furniture is #1703's.</item>
/// </list>
/// </summary>
public class UnredactNameTreeKeyTests
{
    [Theory]
    [InlineData("page.1", "page.1")]                      // hyperref: a generated identifier
    [InlineData("KESTREL chapter", "KESTREL")]            // #1852: a key named after a redacted heading
    public void ANameTreeKey_IsReportedAsFurniture_AndSetsTheExitStatus(string key, string term)
    {
        var pdf = CarrierTrapFixtures.WithCatalog($"/Names << /Dests << /Names [({key}) [3 0 R /Fit]] >> >>");
        SavedPdfLeakScanner.FindTerm(pdf, term).Should().NotBeEmpty("independent: the key is in the file");
        var path = Path.Combine(Path.GetTempPath(), $"excise-namekey-{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var outcome = UnredactCommandHandler.Execute(
                new UnredactCommandInput(path, "certain", DictionaryPath: null, Tolerance: 0.5,
                    MaxCandidates: 200, UseOcr: false, NoCorroboration: false),
                TestContext.Current.CancellationToken);

            outcome.Report!.Certain.Should().ContainSingle(f => f.Text == key && f.HiddenBy == "name-tree key /Dests")
                .Which.Class.Should().Be(RecoveryFindingClass.DocumentFurniture);
            outcome.Report.Certain.Should().OnlyContain(f => f.HiddenBy == "name-tree key /Dests",
                "the key is the only thing this document holds, so the exit status below is the key's");

            var writer = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: false, writer, TextWriter.Null);
            writer.ToString().Should().Contain("0 finding(s) indicate a failed redaction; 1 are ordinary document metadata");

            outcome.ExitCode.Should().Be(3, "furniture still sets the exit status; the summary ranks it");
        }
        finally { File.Delete(path); }
    }
}
