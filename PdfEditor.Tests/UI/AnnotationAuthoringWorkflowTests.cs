using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;

namespace PdfEditor.Tests.UI;

public class AnnotationAuthoringWorkflowTests
{
    [Fact]
    public void BasicAnnotationAuthoring_CreatesRealPdfAnnotations()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Office note");
            doc.AddHighlightAnnotation(1, new PdfRectangle(100, 650, 260, 670), "Office highlight");
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotations = reopened.GetPage(1).GetAnnotations();

        annotations.Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Text && a.Contents == "Office note");
        annotations.Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Highlight && a.Contents == "Office highlight");
    }
}
