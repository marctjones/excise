using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Rendering.Tests;

public sealed class AnnotationAppearancePolicyTests
{
    [Theory]
    [InlineData(PdfAnnotationSubtype.Widget, false, true)]
    [InlineData(PdfAnnotationSubtype.Link, false, true)]
    [InlineData(PdfAnnotationSubtype.Text, true, false)]
    [InlineData(PdfAnnotationSubtype.Square, true, false)]
    public void EvaluateVisibility_UsesTheFieldsAndCommentsBoundary(
        PdfAnnotationSubtype subtype,
        bool showFields,
        bool showComments)
    {
        var decision = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(subtype),
            new RenderOptions
            {
                ShowFieldAndLinkAnnotations = showFields,
                ShowCommentAnnotations = showComments,
            });

        Assert.Equal(AnnotationVisibilityDisposition.CategoryDisabled, decision.Disposition);
        Assert.Equal(subtype is PdfAnnotationSubtype.Widget or PdfAnnotationSubtype.Link, decision.IsFieldOrLink);
    }

    [Theory]
    [InlineData(PdfAnnotationFlags.Hidden)]
    [InlineData(PdfAnnotationFlags.NoView)]
    [InlineData(PdfAnnotationFlags.Hidden | PdfAnnotationFlags.NoView)]
    public void EvaluateVisibility_HiddenFlagsRequireAuditMode(PdfAnnotationFlags flags)
    {
        var annotation = Annotation(PdfAnnotationSubtype.Text, flags);

        var normal = AnnotationAppearancePolicy.EvaluateVisibility(annotation, new RenderOptions());
        var audit = AnnotationAppearancePolicy.EvaluateVisibility(
            annotation,
            new RenderOptions { RevealHiddenAnnotations = true });

        Assert.Equal(AnnotationVisibilityDisposition.HiddenByFlags, normal.Disposition);
        Assert.True(audit.ShouldRender);
    }

    [Fact]
    public void EvaluateVisibility_InvisibleOnlySuppressesUnknownSubtypes()
    {
        var unknown = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Unknown, PdfAnnotationFlags.Invisible),
            new RenderOptions());
        var standard = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Circle, PdfAnnotationFlags.Invisible),
            new RenderOptions());

        Assert.Equal(AnnotationVisibilityDisposition.UnsupportedInvisible, unknown.Disposition);
        Assert.True(standard.ShouldRender);
    }

    [Fact]
    public void ResolveNormalAppearance_ReturnsDirectStream()
    {
        using var document = PdfDocument.CreateNew();
        var stream = new PdfStream([1, 2, 3]);
        var annotation = AnnotationWithAppearance(stream);

        var selected = AnnotationAppearancePolicy.ResolveNormalAppearance(annotation, document, null);

        Assert.Same(stream, selected);
    }

    [Fact]
    public void ResolveNormalAppearance_UsesNamedState()
    {
        using var document = PdfDocument.CreateNew();
        var on = new PdfStream([1]);
        var off = new PdfStream([2]);
        var states = new PdfDictionary { ["On"] = on, ["Off"] = off };
        var annotation = AnnotationWithAppearance(states, "Off");

        var selected = AnnotationAppearancePolicy.ResolveNormalAppearance(annotation, document, null);

        Assert.Same(off, selected);
    }

    [Fact]
    public void ResolveNormalAppearance_DoesNotGuessMissingNamedState()
    {
        using var document = PdfDocument.CreateNew();
        var states = new PdfDictionary { ["On"] = new PdfStream([1]) };
        var annotation = AnnotationWithAppearance(states, "Off");

        var selected = AnnotationAppearancePolicy.ResolveNormalAppearance(annotation, document, null);

        Assert.Null(selected);
    }

    [Fact]
    public void ResolveNormalAppearance_AcceptsOnlyUnambiguousStateWithoutSelector()
    {
        using var document = PdfDocument.CreateNew();
        var only = new PdfStream([1]);
        var single = AnnotationWithAppearance(new PdfDictionary { ["Only"] = only });
        var diagnostics = new List<string>();
        var ambiguous = AnnotationWithAppearance(new PdfDictionary
        {
            ["On"] = new PdfStream([2]),
            ["Off"] = new PdfStream([3]),
        });

        var selected = AnnotationAppearancePolicy.ResolveNormalAppearance(single, document, diagnostics);
        var rejected = AnnotationAppearancePolicy.ResolveNormalAppearance(ambiguous, document, diagnostics);

        Assert.Same(only, selected);
        Assert.Null(rejected);
        Assert.Single(diagnostics);
        Assert.Contains("no /AS", diagnostics[0], StringComparison.Ordinal);
    }

    private static PdfAnnotation Annotation(
        PdfAnnotationSubtype subtype,
        PdfAnnotationFlags flags = PdfAnnotationFlags.None,
        PdfDictionary? dictionary = null)
        => new(
            subtype,
            new PdfRectangle(0, 0, 10, 10),
            contents: null,
            author: null,
            modDate: null,
            creationDate: null,
            color: null,
            flags,
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
            hasAppearance: dictionary?.ContainsKey("AP") == true,
            rawDictionary: dictionary ?? new PdfDictionary());

    private static PdfAnnotation AnnotationWithAppearance(PdfObject normal, string? state = null)
    {
        var dictionary = new PdfDictionary
        {
            ["AP"] = new PdfDictionary { ["N"] = normal },
        };
        if (state != null)
            dictionary["AS"] = new PdfName(state);
        return Annotation(PdfAnnotationSubtype.Widget, dictionary: dictionary);
    }
}
