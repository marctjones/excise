using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for the from-scratch authoring API: <see cref="PdfDocument.CreateNew"/>
/// plus <see cref="PageCollection.AddBlank"/> plus
/// <see cref="PdfPage.GetGraphics"/>. End-to-end round-trip checks:
/// author pages in memory, draw text, save, reopen, verify.
/// </summary>
public class PdfDocumentAuthoringTests
{
    [Fact]
    public void CreateNew_ReturnsDocumentWithZeroPages()
    {
        using var doc = PdfDocument.CreateNew();
        doc.PageCount.Should().Be(0);
        doc.Version.Should().Be("1.7");
        doc.Catalog.Should().NotBeNull();
    }

    [Fact]
    public void AddBlank_AppendsPage_WithGivenDimensions()
    {
        using var doc = PdfDocument.CreateNew();

        var page = doc.Pages.AddBlank(widthPoints: 400, heightPoints: 300);

        doc.PageCount.Should().Be(1);
        page.Width.Should().Be(400);
        page.Height.Should().Be(300);
    }

    [Fact]
    public void AddBlank_MultipleTimes_GrowsPageCount()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.Pages.AddBlank();
        doc.Pages.AddBlank();
        doc.PageCount.Should().Be(3);
    }

    [Fact]
    public void AuthorSaveReopen_DrawnTextIsExtractable()
    {
        // Full round-trip: create doc, add page, draw text via
        // PdfGraphics, save to bytes, reopen, extract letters.
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank(612, 792);
            using (var g = page.GetGraphics())
            {
                var font = PdfFont.Helvetica(12);
                g.DrawString("HELLO WORLD", font, PdfBrush.Black, 100, 700);
                g.Flush();
            }
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(1);

        var extracted = string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value));
        extracted.Should().Contain("HELLO");
        extracted.Should().Contain("WORLD");
    }
}
