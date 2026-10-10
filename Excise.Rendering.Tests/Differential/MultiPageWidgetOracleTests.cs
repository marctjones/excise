using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.MultiPageWidgetFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2040: a field with a widget on each of three pages, each drawing its own text. Tools that are
/// not excise (mutool, pdftotext) paint each widget's appearance on the page whose <c>/Annots</c>
/// lists it; excise must read it there, and after redacting a term only page 3's widget draws,
/// none of them, nor the inflating byte scanner, may find it. The planted run shows each oracle
/// sees the text when it is there.
/// </summary>
public class MultiPageWidgetOracleTests : IDisposable
{
    private const string Shared = "CASE4471";
    private const string Page2Only = "ZEBRAQUILL";
    private const string Page3Only = "MOTHGLASS";

    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static byte[] Build() => F.Build([Shared, Page2Only, Page3Only], Shared, omitP: [3]);

    private static void RequireTools()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed [requires: tool:mutool]");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed [requires: tool:pdftotext]");
    }

    private string Write(byte[] bytes, string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-2040-{tag}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void Planted_EachPagesWidgetText_IsOnThatPage_ForEveryReader()
    {
        RequireTools();
        var bytes = Build();
        var path = Write(bytes, "plant");
        using var document = PdfDocument.Open(bytes);

        foreach (var (page, term) in new[] { (1, Shared), (2, Page2Only), (3, Page3Only) })
        {
            MutoolTextExtractor.ExtractPage(path, page).Should().Contain(term, $"mutool paints page {page}'s widget");
            PdftotextTextExtractor.ExtractPage(path, page).Should().Contain(term, $"so does Poppler, page {page}");
            document.GetPage(page).Text.Should().Contain(term, $"excise reads page {page}'s widget on page {page}");
        }
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Page2Only, "page 2's widget is not drawn on page 1");
        document.GetPage(1).Text.Should().NotContain(Page2Only).And.NotContain(Page3Only);
        SavedPdfLeakScanner.FindTerm(bytes, Page3Only).Should().NotBeEmpty();
    }

    [Fact]
    public void RedactingATermOnlyPage3sWidgetDraws_LeavesNoCopy_ForAnyOracle()
    {
        RequireTools();
        byte[] saved;
        using (var document = PdfDocument.Open(Build()))
        {
            document.RedactText(Page3Only, RedactionOptions.Default with { DrawBox = false })
                .VerifiedRemovals.Should().BeGreaterThan(0, "excise finds it on page 3");
            saved = document.SaveToBytes();
        }
        var path = Write(saved, "redacted");

        SavedPdfLeakScanner.FindTerm(saved, Page3Only).Should().BeEmpty();
        for (var page = 1; page <= 3; page++)
        {
            MutoolTextExtractor.ExtractPage(path, page).Should().NotContain(Page3Only, $"page {page}");
            PdftotextTextExtractor.ExtractPage(path, page).Should().NotContain(Page3Only, $"page {page}");
        }
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain(Shared, "page 1 is not part of the delta");
    }
}
