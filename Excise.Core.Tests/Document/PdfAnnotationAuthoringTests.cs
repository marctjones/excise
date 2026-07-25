using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

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
            author: "EXCISE",
            open: true);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Text);
        annotation.Contents.Should().Be("Review this paragraph");
        annotation.Author.Should().Be("EXCISE");
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

    // ── Shape annotations (#626, ISO 32000-2 §12.5.6.8) ─────────────────────

    [Fact]
    public void AddSquareAnnotation_WritesSpecCorrectDictionaryAndAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddSquareAnnotation(
            pageNumber: 1,
            rect: new PdfRectangle(100, 500, 300, 600),
            contents: "Marked region",
            author: "EXCISE",
            red: 0.8, green: 0.1, blue: 0.1,
            borderWidth: 2);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Square);
        annotation.Contents.Should().Be("Marked region");
        annotation.Author.Should().Be("EXCISE");
        annotation.Color.Should().NotBeNull();
        annotation.Color!.Value.R.Should().BeApproximately(0.8, 0.001);
        annotation.BorderWidth.Should().Be(2);
        annotation.BorderStyle.Should().Be("S");
        annotation.HasAppearance.Should().BeTrue(
            "shape annotations must ship a baked /AP /N so third-party viewers " +
            "render identical pixels (#626)");
        annotation.CreationDate.Should().NotBeNull("markup annotations carry /CreationDate (Table 172)");
        annotation.Flags.Should().HaveFlag(PdfAnnotationFlags.Print);

        // Raw dictionary spec checks that the reader model doesn't surface.
        var raw = annotation.RawDictionary;
        raw.GetNameOrNull("Subtype").Should().Be("Square");
        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        ap.Should().NotBeNull();
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull("/AP /N must resolve to a Form XObject stream");
        n!.GetNameOrNull("Subtype").Should().Be("Form");
        var bbox = n.GetOptional("BBox") as Excise.Core.Primitives.PdfArray;
        bbox.Should().NotBeNull();
        bbox!.GetNumber(2).Should().BeApproximately(200, 0.01, "BBox width must match /Rect width");
        bbox.GetNumber(3).Should().BeApproximately(100, 0.01, "BBox height must match /Rect height");

        var ops = n.GetDecodedString();
        ops.Should().Contain(" re", "square appearance draws a rectangle path");
        ops.Should().Contain("RG", "stroke color must be set from /C");
        ops.Should().Contain("S", "stroke-only shape paints with S");
    }

    [Fact]
    public void AddCircleAnnotation_WritesInteriorColorAndBezierAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddCircleAnnotation(
            pageNumber: 1,
            rect: new PdfRectangle(200, 200, 320, 280),
            red: 0, green: 0, blue: 1,
            borderWidth: 1.5,
            interiorRed: 0.2, interiorGreen: 0.9, interiorBlue: 0.2);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Circle);
        annotation.HasAppearance.Should().BeTrue();

        var raw = annotation.RawDictionary;
        var icArr = raw.GetOptional("IC") as Excise.Core.Primitives.PdfArray;
        icArr.Should().NotBeNull("/IC carries the interior fill color");
        icArr!.GetNumber(1).Should().BeApproximately(0.9, 0.001);

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();
        ops.Should().Contain(" c\n", "circle appearance approximates the ellipse with Bézier arcs");
        ops.Should().Contain("rg", "interior color fills with rg");
        ops.Should().Contain("B", "fill+stroke shape paints with B");
        ops.Should().NotContain(" re", "a circle must not fall back to a rectangle path");
    }

    [Fact]
    public void AddShapeAnnotations_SurviveSaveAndReload_WithAppearanceIntact()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddSquareAnnotation(1, new PdfRectangle(72, 500, 272, 600),
                contents: "Persisted square", red: 1, green: 0, blue: 0,
                interiorRed: 1, interiorGreen: 0.8, interiorBlue: 0.8);
            doc.AddCircleAnnotation(1, new PdfRectangle(300, 300, 420, 380),
                contents: "Persisted circle", red: 0, green: 0, blue: 1);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotations = reopened.GetPage(1).GetAnnotations();

        var square = annotations.Should()
            .ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Square).Subject;
        var circle = annotations.Should()
            .ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Circle).Subject;

        square.Contents.Should().Be("Persisted square");
        square.HasAppearance.Should().BeTrue("/AP must survive the save/reload round-trip");
        square.Color!.Value.R.Should().BeApproximately(1, 0.001);
        square.Rect.Normalize().Width.Should().BeApproximately(200, 0.01);

        circle.Contents.Should().Be("Persisted circle");
        circle.HasAppearance.Should().BeTrue();
        circle.Color!.Value.B.Should().BeApproximately(1, 0.001);

        // The reloaded appearance must still be a decodable Form XObject whose
        // BBox matches the annotation rect — this is what viewers actually draw.
        var ap = reopened.Resolve(square.RawDictionary.GetOptional("AP")!)
            as Excise.Core.Primitives.PdfDictionary;
        var n = reopened.Resolve(ap!.GetOptional("N")!)
            as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull();
        n!.GetNameOrNull("Subtype").Should().Be("Form");
    }

    [Theory]
    [InlineData(0.5, null, null)]      // partial interior color
    [InlineData(null, 0.5, 0.5)]       // partial interior color
    public void AddSquareAnnotation_RejectsPartialInteriorColor(
        double? ir, double? ig, double? ib)
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var action = () => doc.AddSquareAnnotation(
            1, new PdfRectangle(100, 100, 200, 150),
            interiorRed: ir, interiorGreen: ig, interiorBlue: ib);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddCircleAnnotation_RejectsInvisibleShape()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var action = () => doc.AddCircleAnnotation(
            1, new PdfRectangle(100, 100, 200, 150), borderWidth: 0);

        action.Should().Throw<ArgumentException>(
            "zero border width with no interior fill draws nothing");
    }

    [Fact]
    public void AddSquareAnnotation_RejectsNegativeBorderWidthAndBadColor()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var negativeBorder = () => doc.AddSquareAnnotation(
            1, new PdfRectangle(100, 100, 200, 150), borderWidth: -1);
        negativeBorder.Should().Throw<ArgumentOutOfRangeException>();

        var badColor = () => doc.AddSquareAnnotation(
            1, new PdfRectangle(100, 100, 200, 150), red: 1.5);
        badColor.Should().Throw<ArgumentOutOfRangeException>();

        var badInterior = () => doc.AddSquareAnnotation(
            1, new PdfRectangle(100, 100, 200, 150),
            interiorRed: -0.1, interiorGreen: 0, interiorBlue: 0);
        badInterior.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── FreeText annotations (#626, ISO 32000-2 §12.5.6.6) ──────────────────

    [Fact]
    public void AddFreeTextAnnotation_WritesSpecCorrectDictionaryAndAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddFreeTextAnnotation(
            pageNumber: 1,
            rect: new PdfRectangle(100, 500, 400, 560),
            text: "Please review section 3",
            author: "EXCISE",
            fontSize: 14,
            textRed: 0.1, textGreen: 0.1, textBlue: 0.6,
            quadding: PdfFreeTextQuadding.Centered,
            borderWidth: 1);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.FreeText);
        annotation.Contents.Should().Be("Please review section 3");
        annotation.Author.Should().Be("EXCISE");
        annotation.HasAppearance.Should().BeTrue(
            "FreeText annotations must ship a baked /AP /N so third-party viewers " +
            "render identical pixels (#626)");
        annotation.CreationDate.Should().NotBeNull("markup annotations carry /CreationDate (Table 172)");
        annotation.Flags.Should().HaveFlag(PdfAnnotationFlags.Print);
        annotation.BorderWidth.Should().Be(1);

        // Raw dictionary spec checks (Table 177 entries the reader model doesn't surface).
        var raw = annotation.RawDictionary;
        raw.GetNameOrNull("Subtype").Should().Be("FreeText");
        raw.GetStringOrNull("DA").Should().Be("0.1 0.1 0.6 rg /Helv 14 Tf",
            "/DA is required by Table 177 and carries color + font + size");
        raw.GetInt("Q", -1).Should().Be(1, "/Q 1 is centered quadding");

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        ap.Should().NotBeNull();
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull("/AP /N must resolve to a Form XObject stream");
        n!.GetNameOrNull("Subtype").Should().Be("Form");
        var bbox = n.GetOptional("BBox") as Excise.Core.Primitives.PdfArray;
        bbox.Should().NotBeNull();
        bbox!.GetNumber(2).Should().BeApproximately(300, 0.01, "BBox width must match /Rect width");
        bbox.GetNumber(3).Should().BeApproximately(60, 0.01, "BBox height must match /Rect height");

        // The appearance must actually draw the text, not merely decorate.
        var ops = n.GetDecodedString();
        ops.Should().Contain("BT", "the appearance draws text");
        ops.Should().Contain("/Helv 14 Tf", "the /DA font+size must be what the appearance uses");
        ops.Should().Contain("0.1 0.1 0.6 rg", "the /DA color must be what the appearance uses");
        ops.Should().Contain("(Please review section 3) Tj", "the text itself must be drawn");
        ops.Should().Contain("ET");

        // The appearance's own /Resources must resolve /Helv (self-contained).
        var resources = n.GetOptional("Resources") as Excise.Core.Primitives.PdfDictionary;
        resources.Should().NotBeNull();
        var fonts = doc.Resolve(resources!.GetOptional("Font")!) as Excise.Core.Primitives.PdfDictionary;
        fonts.Should().NotBeNull();
        var helv = doc.Resolve(fonts!.GetOptional("Helv")!) as Excise.Core.Primitives.PdfDictionary;
        helv.Should().NotBeNull("/Helv referenced by the Tf must resolve");
        helv!.GetNameOrNull("BaseFont").Should().Be("Helvetica");
        helv.GetNameOrNull("Subtype").Should().Be("Type1");
    }

    [Fact]
    public void AddFreeTextAnnotation_WrapsLongTextAndEscapesDelimiters()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        doc.AddFreeTextAnnotation(
            1, new PdfRectangle(100, 400, 220, 500),
            text: "wrap these (parenthesised) words onto several lines please");

        var raw = doc.GetPage(1).GetAnnotations().Single().RawDictionary;
        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();

        ops.Should().Contain("\\(parenthesised\\)",
            "parentheses in a literal string must be escaped");
        System.Text.RegularExpressions.Regex.Matches(ops, @"\) Tj").Count
            .Should().BeGreaterThan(1, "a 120pt-wide box must wrap this text onto multiple lines");
    }

    [Theory]
    [InlineData(PdfFreeTextQuadding.LeftJustified, 0)]
    [InlineData(PdfFreeTextQuadding.Centered, 1)]
    [InlineData(PdfFreeTextQuadding.RightJustified, 2)]
    public void AddFreeTextAnnotation_WritesQuaddingValue(
        PdfFreeTextQuadding quadding, int expectedQ)
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddFreeTextAnnotation(
            1, new PdfRectangle(100, 100, 300, 150), "Q", quadding: quadding);

        annotation.RawDictionary.GetInt("Q", -1).Should().Be(expectedQ);
    }

    [Fact]
    public void AddFreeTextAnnotation_SurvivesSaveAndReload_WithAppearanceIntact()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddFreeTextAnnotation(1, new PdfRectangle(72, 600, 372, 660),
                text: "Persisted free text", author: "EXCISE", fontSize: 12,
                borderWidth: 1,
                backgroundRed: 1, backgroundGreen: 1, backgroundBlue: 0.85);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var freeText = reopened.GetPage(1).GetAnnotations().Should()
            .ContainSingle(a => a.Subtype == PdfAnnotationSubtype.FreeText).Subject;

        freeText.Contents.Should().Be("Persisted free text");
        freeText.Author.Should().Be("EXCISE");
        freeText.HasAppearance.Should().BeTrue("/AP must survive the save/reload round-trip");
        freeText.Rect.Normalize().Width.Should().BeApproximately(300, 0.01);

        var raw = freeText.RawDictionary;
        raw.GetStringOrNull("DA").Should().Be("0 0 0 rg /Helv 12 Tf");
        raw.GetInt("Q", -1).Should().Be(0);

        // The reloaded appearance must still be a decodable Form XObject that
        // draws the text — this is what viewers actually paint.
        var ap = reopened.Resolve(raw.GetOptional("AP")!)
            as Excise.Core.Primitives.PdfDictionary;
        var n = reopened.Resolve(ap!.GetOptional("N")!)
            as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull();
        n!.GetNameOrNull("Subtype").Should().Be("Form");
        n.GetDecodedString().Should().Contain("(Persisted free text) Tj");
    }

    [Fact]
    public void AddFreeTextAnnotation_RejectsEmptyTextAndBadArguments()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var rect = new PdfRectangle(100, 100, 300, 150);

        var emptyText = () => doc.AddFreeTextAnnotation(1, rect, "   ");
        emptyText.Should().Throw<ArgumentException>();

        var badSize = () => doc.AddFreeTextAnnotation(1, rect, "x", fontSize: 0);
        badSize.Should().Throw<ArgumentOutOfRangeException>();

        var badColor = () => doc.AddFreeTextAnnotation(1, rect, "x", textRed: 1.5);
        badColor.Should().Throw<ArgumentOutOfRangeException>();

        var negativeBorder = () => doc.AddFreeTextAnnotation(1, rect, "x", borderWidth: -1);
        negativeBorder.Should().Throw<ArgumentOutOfRangeException>();

        var partialBackground = () => doc.AddFreeTextAnnotation(
            1, rect, "x", backgroundRed: 0.5);
        partialBackground.Should().Throw<ArgumentException>();
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
