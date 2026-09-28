using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1577: XFA text outside WinAnsi is drawn with an installed Unicode font, embedded as a
/// subset, instead of '?'. Read back by mutool (not excise), and redacted by the ordinary
/// text redaction, which must still find and remove it.
/// </summary>
public class XfaUnicodeTextTests : IDisposable
{
    private const string Chinese = "王小明";     // a Chinese name
    private const string Cyrillic = "Жанна"; // a Russian given name
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static readonly Lazy<bool> SystemFont = new(() => XfaFallbackFont.ForDocument(1) != null);

    private static bool HasSystemFont => SystemFont.Value;

    private static PdfDocument LaidOutForm(string value, out XfaLayoutResult result)
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.PositionedTemplate(),
            XfaTestForms.Data($"<FullName>{value}</FullName>")));
        result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        return document;
    }

    private string Save(PdfDocument document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-unicode-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void NonWinAnsiValue_IsDrawnWithAnEmbeddedFont_AndMutoolReadsIt()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(HasSystemFont, "no wide-coverage Unicode font from XfaFallbackFont's list is installed");

        using var document = LaidOutForm($"{Chinese} Ivanova {Cyrillic}", out var result);
        var path = Save(document);

        var text = MutoolTextExtractor.ExtractPage(path, 1);
        text.Should().Contain(Chinese).And.Contain(Cyrillic).And.Contain("Ivanova");
        text.Should().NotContain("???", "no character falls back to '?'");
        result.Omissions.Should().NotContain(o => o.Contains("WinAnsi", StringComparison.Ordinal));
        File.ReadAllBytes(path).Length.Should().BeLessThan(1_000_000, "the system font is embedded as a subset");
    }

    [Fact]
    public void NonWinAnsiValue_IsRemovedByTextRedaction()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(HasSystemFont, "no wide-coverage Unicode font from XfaFallbackFont's list is installed");

        using var document = LaidOutForm($"{Chinese} Ivanova", out _);
        document.RedactText(Chinese, RedactionOptions.Default).MatchesLocated.Should().Be(1,
            "the live document the app redacts must read the text it laid out");
        var path = Save(document);

        var text = MutoolTextExtractor.ExtractPage(path, 1);
        text.Should().NotContain(Chinese);
        text.Should().Contain("Ivanova", "only the term goes");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), Chinese).Should().BeEmpty();
    }

    [Fact]
    public void FirstFontOfACollection_IsAStandaloneFont()
    {
        var collection = new[]
        {
            "/System/Library/Fonts/Hiragino Sans GB.ttc",
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"),
        }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(collection == null, "no known CJK font collection (.ttc) is installed");

        var sfnt = XfaFallbackFont.FirstFontOfCollection(File.ReadAllBytes(collection!));

        sfnt.Should().NotBeNull();
        PdfFont.FromTrueType(sfnt!, 10).CanEncodeFully("中").Should().BeTrue();
    }
}
