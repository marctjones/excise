using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Rendering.Tests;

public sealed class AnnotationAppearanceExecutionTests
{
    [Fact]
    public void Plan_MapsTransformedBoundingBoxOntoAnnotationRect()
    {
        using var document = PdfDocument.CreateNew();
        var appearance = Appearance(
            bbox: new PdfArray(new PdfInteger(0), new PdfInteger(0), new PdfInteger(20), new PdfInteger(10)),
            matrix: new PdfArray(
                new PdfInteger(2), new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(3), new PdfInteger(5), new PdfInteger(7)));

        var plan = AnnotationAppearanceExecution.Plan(
            Annotation(new PdfRectangle(100, 200, 300, 500)),
            appearance,
            document,
            antiAlias: true);

        Assert.Equal(AnnotationAppearanceDisposition.Execute, plan.Disposition);
        Assert.Same(appearance, plan.Request.Appearance);
        Assert.Equal(new SkiaSharp.SKRect(100, 200, 300, 500), plan.Request.ClipRect);
        Assert.True(plan.Request.AntiAlias);
        Assert.Equal(5f, plan.Request.FitMatrix.ScaleX, 4);
        Assert.Equal(10f, plan.Request.FitMatrix.ScaleY, 4);
        Assert.Equal(75f, plan.Request.FitMatrix.TransX, 4);
        Assert.Equal(130f, plan.Request.FitMatrix.TransY, 4);
    }

    [Fact]
    public void Plan_RequestsSynthesisForMissingOrDegenerateBoundingBox()
    {
        using var document = PdfDocument.CreateNew();

        var missing = AnnotationAppearanceExecution.Plan(
            Annotation(new PdfRectangle(0, 0, 20, 20)),
            new PdfStream(),
            document,
            antiAlias: false);
        var degenerate = AnnotationAppearanceExecution.Plan(
            Annotation(new PdfRectangle(0, 0, 20, 20)),
            Appearance(new PdfArray(
                new PdfInteger(1), new PdfInteger(1), new PdfInteger(1), new PdfInteger(5))),
            document,
            antiAlias: false);

        Assert.Equal(AnnotationAppearanceDisposition.Synthesize, missing.Disposition);
        Assert.Equal(AnnotationAppearanceDisposition.Synthesize, degenerate.Disposition);
        Assert.Contains("no usable /BBox", missing.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("is degenerate", degenerate.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SkipsDegenerateAnnotationRect()
    {
        using var document = PdfDocument.CreateNew();
        var plan = AnnotationAppearanceExecution.Plan(
            Annotation(new PdfRectangle(10, 10, 10, 20)),
            Appearance(new PdfArray(
                new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1))),
            document,
            antiAlias: false);

        Assert.Equal(AnnotationAppearanceDisposition.Skip, plan.Disposition);
        Assert.Contains("degenerate /Rect", plan.Diagnostic, StringComparison.Ordinal);
    }

    private static PdfStream Appearance(PdfArray bbox, PdfArray? matrix = null)
    {
        var dictionary = new PdfDictionary { ["BBox"] = bbox };
        if (matrix != null)
            dictionary["Matrix"] = matrix;
        return new PdfStream(dictionary, []);
    }

    private static PdfAnnotation Annotation(PdfRectangle rect)
        => new(
            PdfAnnotationSubtype.Widget,
            rect,
            contents: null,
            author: null,
            modDate: null,
            creationDate: null,
            color: null,
            PdfAnnotationFlags.None,
            name: null,
            quadPoints: null,
            destinationPage: null,
            uri: null,
            isOpen: false,
            iconName: null,
            lineEndpoints: null,
            lineEndings: null,
            vertices: null,
            inkStrokes: null,
            attachmentFileName: null,
            attachmentBytes: null,
            attachmentMimeType: null,
            borderWidth: null,
            interiorColor: null,
            borderStyle: null,
            borderDashPattern: null,
            hasAppearance: true,
            rawDictionary: new PdfDictionary());
}
