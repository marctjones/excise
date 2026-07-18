using System;
using System.Diagnostics;
using System.IO;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// #650: real timing for <see cref="RedactionConfidenceChecker"/> against a
/// large real-world document. This is what drove two design decisions away
/// from the first, naive implementation: one mutool invocation for the
/// whole page range instead of one process per page (dominant cost was
/// per-process spawn overhead, not compute — see
/// <see cref="MutoolTextExtractor.ExtractAllPages"/>), and reusing the
/// caller's own unmutated source file instead of re-serializing via
/// <c>PdfDocument.SaveToBytes</c> (measured 3-7s alone, pure waste when the
/// original bytes are already on disk — see the <c>sourceFilePath</c>
/// parameter on <see cref="RedactionConfidenceChecker.CheckDocument"/>).
/// Not a pass/fail gate — logs numbers for the wiring step (GUI/CLI
/// integration) to design its UX around real data, not a guess.
/// </summary>
public class RedactionConfidenceCheckerBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public RedactionConfidenceCheckerBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CheckDocument_LargeRealWorldDocument_LogsPerPageCost()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed — this is specifically timing the mutool-oracle path");

        var repoRoot = LocateRepoRoot();
        Assert.SkipWhen(repoRoot == null, "could not locate repo root from test working directory");

        var path = Path.Combine(repoRoot!, "test-pdfs", "smoke", "irs-1040-instructions.pdf");
        Assert.SkipWhen(!File.Exists(path), $"smoke corpus fixture not present: {path}");

        using var doc = PdfDocument.Open(path);
        _output.WriteLine($"Document: {Path.GetFileName(path)}, {doc.PageCount} pages");

        var sw = Stopwatch.StartNew();
        var report = new RedactionConfidenceChecker().CheckDocument(doc, sourceFilePath: path);
        sw.Stop();

        var perPageMs = sw.Elapsed.TotalMilliseconds / doc.PageCount;
        _output.WriteLine($"Total: {sw.Elapsed.TotalMilliseconds:F0}ms for {doc.PageCount} pages ({perPageMs:F1}ms/page)");
        _output.WriteLine($"Overall tier: {report.Tier}, oracle: {report.Oracle}");

        // Not a hard gate — this class exists to produce the number, not
        // enforce a budget yet. Document it loudly if it's slow so the
        // wiring step (GUI/CLI integration) starts from data, not a guess:
        // a document this large still costs several seconds even after
        // the optimizations above (dominated by the mutool subprocess call
        // itself and excise's own per-page extraction, both intrinsic to
        // what this check does), which the GUI wiring needs to show
        // progress for rather than freezing invisibly on a large redaction.
        if (sw.Elapsed.TotalSeconds > 5)
        {
            _output.WriteLine(
                $"NOTE: {sw.Elapsed.TotalSeconds:F1}s for a {doc.PageCount}-page document. Most " +
                "everyday redactions are far shorter documents and will be sub-second, but the " +
                "GUI wiring step should show a progress indicator for large documents rather " +
                "than blocking with no feedback.");
        }
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
