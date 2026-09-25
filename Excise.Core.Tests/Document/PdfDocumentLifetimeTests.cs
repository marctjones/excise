using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;

namespace Excise.Core.Tests.Document;

public sealed class PdfDocumentLifetimeTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"excise-document-lifetime-{Guid.NewGuid():N}");

    public PdfDocumentLifetimeTests()
        => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void PathsReferToSameFile_NormalizesRelativeSegments()
    {
        var path = Path.Combine(_tempDirectory, "document.pdf");
        var equivalent = Path.Combine(_tempDirectory, "nested", "..", "document.pdf");

        PdfDocumentLifetime.PathsReferToSameFile(path, equivalent).Should().BeTrue();
    }

    [Fact]
    public void OpenInputForOutput_SamePathCanBeSavedBackToSource()
    {
        var path = Path.Combine(_tempDirectory, "same-path.pdf");
        using (var source = PdfDocument.CreateNew())
        {
            var page = source.Pages.AddBlank();
            using (var graphics = page.GetGraphics())
                graphics.DrawString("SAME PATH", PdfFont.Helvetica(14), PdfBrush.Black, 72, 700);
            source.Save(path);
        }

        using (var document = PdfDocumentLifetime.OpenInputForOutput(path, path))
        {
            document.PageCount.Should().Be(1);
            document.Save(path);
        }

        using var reopened = PdfDocument.Open(path);
        reopened.PageCount.Should().Be(1);
        reopened.GetPage(1).Text.Should().Contain("SAME PATH");
    }
}
