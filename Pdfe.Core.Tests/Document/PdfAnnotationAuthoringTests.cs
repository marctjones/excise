using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;

namespace Pdfe.Core.Tests.Document;

public class PdfAnnotationAuthoringTests
{
    [Fact]
    public void AddTextAnnotation_AppendsStickyNoteToPageAnnots()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddTextAnnotation(
            pageNumber: 1,
            rect: new PdfRectangle(72, 700, 108, 736),
            contents: "Review this paragraph",
            author: "PDFE",
            open: true);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Text);
        annotation.Contents.Should().Be("Review this paragraph");
        annotation.Author.Should().Be("PDFE");
        annotation.IsOpen.Should().BeTrue();

        doc.GetPage(1).GetAnnotations().Should().ContainSingle();
    }

    [Fact]
    public void AddHighlightAnnotation_WritesQuadPointsAndColor()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddHighlightAnnotation(
            pageNumber: 1,
            rect: new PdfRectangle(100, 700, 300, 720),
            contents: "Important",
            red: 1,
            green: 0.92,
            blue: 0.2);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Highlight);
        annotation.Contents.Should().Be("Important");
        annotation.QuadPoints.Should().NotBeNull().And.HaveCount(1);
        annotation.Color.Should().NotBeNull();
        annotation.Color!.Value.G.Should().BeApproximately(0.92, 0.001);
    }

    [Fact]
    public void AddAnnotations_SurviveSaveAndReload()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Persisted note");
            doc.AddHighlightAnnotation(1, new PdfRectangle(100, 650, 240, 670), "Persisted highlight");
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotations = reopened.GetPage(1).GetAnnotations();

        annotations.Select(a => a.Subtype).Should().Contain(new[]
        {
            PdfAnnotationSubtype.Text,
            PdfAnnotationSubtype.Highlight
        });
        annotations.Should().Contain(a => a.Contents == "Persisted note");
        annotations.Should().Contain(a => a.Contents == "Persisted highlight");
    }

    [Fact]
    public void AddHighlightAnnotation_RejectsInvalidColor()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var action = () => doc.AddHighlightAnnotation(
            1,
            new PdfRectangle(100, 100, 200, 120),
            red: 1.1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
