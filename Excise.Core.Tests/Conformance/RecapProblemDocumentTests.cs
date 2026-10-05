using System;
using System.IO;
using System.Linq;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Conformance;

public sealed class RecapProblemDocumentTests
{
    private const string FileName = "recap_neg_390a14872e8f0024.pdf";
    private const string RelativeFixture = "test-pdfs/recap/negatives/" + FileName;
    private const string RelativeManifest = "tests/corpora/recap-problem-documents.tsv";

    [Fact]
    public void KnownSlowRecapDocument_HasExplicitUnavailableProvenance()
    {
        var manifest = TestRepoLayout.FindFileInLocalCheckout("tests", "corpora", "recap-problem-documents.tsv");
        Assert.NotNull(manifest);

        var row = File.ReadLines(manifest!)
            .Single(line => line.StartsWith(FileName + "\t", StringComparison.Ordinal));
        var columns = row.Split('\t');

        Assert.Equal(6, columns.Length);
        Assert.Equal("390a14872e8f0024", columns[1]);
        Assert.Equal("about 2.4 MB", columns[2]);
        Assert.Equal("39", columns[3]);
        Assert.Equal("unavailable", columns[4]);
        Assert.Contains("Original URL and full SHA-256", columns[5], StringComparison.Ordinal);
        Assert.Contains("do not substitute a fresh search result", columns[5], StringComparison.Ordinal);
    }

    [Fact]
    public void KnownSlowRecapDocument_IsFetchedAndHasExpectedPageCount()
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "recap", "negatives", FileName);
        Assert.SkipWhen(path is null,
            $"{FileName} is unavailable, so #1670 cannot be reproduced. The tracked manifest " +
            $"({RelativeManifest}) records the known facts and the missing provenance. The original " +
            "RECAP URL and full SHA-256 must be recovered from the machine that ran the #1645 sweep; " +
            "a fresh search is not deterministic. " +
            TestRepoLayout.AbsenceReason("the known-slow RECAP negative (#1670)", RelativeFixture));

        using var document = CorpusPasswords.Open(path!);
        Assert.Equal(39, document.PageCount);
    }
}
