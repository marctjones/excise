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

    /// <summary>
    /// §12.5.3's PRINT rule, all four combinations of Print × NoView (#1573).
    /// The two that the VIEW rule gets wrong on paper are the interesting ones:
    /// an annotation with no <c>/F</c> at all must NOT print (review markup
    /// Acrobat and PDFKit leave off paper), and NoView+Print MUST (a print-only
    /// watermark, the one thing the author meant for paper).
    /// </summary>
    [Theory]
    [InlineData(PdfAnnotationFlags.None, false)]
    [InlineData(PdfAnnotationFlags.Print, true)]
    [InlineData(PdfAnnotationFlags.NoView, false)]
    [InlineData(PdfAnnotationFlags.NoView | PdfAnnotationFlags.Print, true)]
    public void EvaluateVisibility_PrintIntentSelectsOnThePrintFlag(
        PdfAnnotationFlags flags,
        bool expectedOnPaper)
    {
        var decision = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Square, flags),
            new RenderOptions { PrintIntent = true });

        Assert.Equal(expectedOnPaper, decision.ShouldRender);
        if (!expectedOnPaper)
            Assert.Equal(AnnotationVisibilityDisposition.NotPrintable, decision.Disposition);
    }

    /// <summary>The VIEW rule is unchanged by #1573 — the same four flags.</summary>
    [Theory]
    [InlineData(PdfAnnotationFlags.None, true)]
    [InlineData(PdfAnnotationFlags.Print, true)]
    [InlineData(PdfAnnotationFlags.NoView, false)]
    [InlineData(PdfAnnotationFlags.NoView | PdfAnnotationFlags.Print, false)]
    public void EvaluateVisibility_WithoutPrintIntentTheViewRuleIsUnchanged(
        PdfAnnotationFlags flags,
        bool expectedOnScreen)
    {
        var decision = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Square, flags),
            new RenderOptions());

        Assert.Equal(expectedOnScreen, decision.ShouldRender);
    }

    /// <summary>Hidden (bit 2) suppresses paper too, Print flag or not.</summary>
    [Theory]
    [InlineData(PdfAnnotationFlags.Hidden)]
    [InlineData(PdfAnnotationFlags.Hidden | PdfAnnotationFlags.Print)]
    public void EvaluateVisibility_PrintIntentStillObeysHidden(PdfAnnotationFlags flags)
    {
        var decision = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Square, flags),
            new RenderOptions { PrintIntent = true });

        Assert.False(decision.ShouldRender);
        Assert.Equal(AnnotationVisibilityDisposition.NotPrintable, decision.Disposition);
    }

    /// <summary>
    /// Audit mode has NO say under print intent (#1573). RevealHiddenAnnotations
    /// draws what no conforming viewer shows and its own remarks say it must
    /// never reach an export path; a print raster is one, and a Hidden
    /// annotation revealed onto paper is invented ink in a shared artefact.
    /// </summary>
    [Fact]
    public void EvaluateVisibility_PrintIntentIgnoresAuditMode()
    {
        var options = new RenderOptions { PrintIntent = true, RevealHiddenAnnotations = true };

        Assert.False(AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Square, PdfAnnotationFlags.Hidden), options).ShouldRender);
        Assert.False(AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Square, PdfAnnotationFlags.None), options).ShouldRender);
    }

    /// <summary>
    /// The category switches are asked FIRST, print intent or not: "print what
    /// is on the page" never overrides "the user hid review markup".
    /// </summary>
    [Fact]
    public void EvaluateVisibility_PrintIntentStillHonoursTheCategorySwitches()
    {
        var decision = AnnotationAppearancePolicy.EvaluateVisibility(
            Annotation(PdfAnnotationSubtype.Square, PdfAnnotationFlags.Print),
            new RenderOptions { PrintIntent = true, ShowCommentAnnotations = false });

        Assert.Equal(AnnotationVisibilityDisposition.CategoryDisabled, decision.Disposition);
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

    [Fact]
    public void SelectVisibleAnnotations_ReturnsTypedSelectionsAndDiagnostics()
    {
        var widget = Annotation(PdfAnnotationSubtype.Widget);
        var invisibleUnknown = Annotation(
            PdfAnnotationSubtype.Unknown,
            PdfAnnotationFlags.Invisible);
        var diagnostics = new List<string>();

        var selected = AnnotationAppearancePolicy.SelectVisibleAnnotations(
            [widget, invisibleUnknown],
            new RenderOptions(),
            diagnostics);

        var only = Assert.Single(selected);
        Assert.Same(widget, only.Annotation);
        Assert.True(only.IsFieldOrLink);
        Assert.Single(diagnostics);
        Assert.Contains("non-standard subtype", diagnostics[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfAnnotationSubtype.Widget, nameof(AnnotationSynthesisKind.Widget))]
    [InlineData(PdfAnnotationSubtype.Link, nameof(AnnotationSynthesisKind.Link))]
    [InlineData(PdfAnnotationSubtype.Text, nameof(AnnotationSynthesisKind.StickyNote))]
    [InlineData(PdfAnnotationSubtype.Square, nameof(AnnotationSynthesisKind.Square))]
    [InlineData(PdfAnnotationSubtype.Circle, nameof(AnnotationSynthesisKind.Circle))]
    [InlineData(PdfAnnotationSubtype.FreeText, nameof(AnnotationSynthesisKind.FreeText))]
    [InlineData(PdfAnnotationSubtype.Highlight, nameof(AnnotationSynthesisKind.TextMarkup))]
    [InlineData(PdfAnnotationSubtype.Underline, nameof(AnnotationSynthesisKind.TextMarkup))]
    [InlineData(PdfAnnotationSubtype.Squiggly, nameof(AnnotationSynthesisKind.TextMarkup))]
    [InlineData(PdfAnnotationSubtype.StrikeOut, nameof(AnnotationSynthesisKind.TextMarkup))]
    [InlineData(PdfAnnotationSubtype.Line, nameof(AnnotationSynthesisKind.Line))]
    [InlineData(PdfAnnotationSubtype.Polygon, nameof(AnnotationSynthesisKind.Polygon))]
    [InlineData(PdfAnnotationSubtype.PolyLine, nameof(AnnotationSynthesisKind.PolyLine))]
    [InlineData(PdfAnnotationSubtype.Ink, nameof(AnnotationSynthesisKind.Ink))]
    [InlineData(PdfAnnotationSubtype.Stamp, nameof(AnnotationSynthesisKind.None))]
    public void SelectSynthesis_RegistersFallbackBoundary(
        PdfAnnotationSubtype subtype,
        string expected)
    {
        Assert.Equal(expected, AnnotationAppearancePolicy.SelectSynthesis(subtype).ToString());
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
