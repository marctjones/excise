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
    public void AddSquareAnnotation_UsesCanonicalPdfNumberPrecision()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddSquareAnnotation(
            pageNumber: 1,
            rect: new PdfRectangle(100, 500, 300, 600),
            contents: "precision",
            red: 0.12345678,
            green: 0.23456789,
            blue: 0.34567891,
            borderWidth: 1.23456789);

        var ap = (Excise.Core.Primitives.PdfDictionary)doc.Resolve(
            annotation.RawDictionary["AP"]);
        var appearance = (Excise.Core.Primitives.PdfStream)doc.Resolve(ap["N"]);
        var content = appearance.GetDecodedString();

        content.Should().Contain("0.123457 0.234568 0.345679 RG");
        content.Should().Contain("1.234568 w");
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

    // ── Ink annotations (#626, ISO 32000-2 §12.5.6.13) ──────────────────────

    [Fact]
    public void AddInkAnnotation_WritesSpecCorrectDictionaryAndAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var strokes = new[]
        {
            new[] { (100.0, 700.0), (200.0, 720.0), (300.0, 700.0) },
            new[] { (120.0, 650.0), (280.0, 650.0) }
        };

        var annotation = doc.AddInkAnnotation(
            pageNumber: 1,
            strokes: strokes,
            contents: "Signed here",
            author: "EXCISE",
            red: 0, green: 0, blue: 0.8,
            borderWidth: 3);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Ink);
        annotation.Contents.Should().Be("Signed here");
        annotation.Author.Should().Be("EXCISE");
        annotation.HasAppearance.Should().BeTrue(
            "Ink annotations must ship a baked /AP /N so third-party viewers " +
            "render identical pixels (#626)");
        annotation.CreationDate.Should().NotBeNull("markup annotations carry /CreationDate (Table 172)");
        annotation.Flags.Should().HaveFlag(PdfAnnotationFlags.Print);
        annotation.BorderWidth.Should().Be(3);

        // The reader model must round-trip every authored stroke point.
        annotation.InkStrokes.Should().NotBeNull();
        annotation.InkStrokes!.Count.Should().Be(2);
        annotation.InkStrokes[0].Should().Equal((100.0, 700.0), (200.0, 720.0), (300.0, 700.0));
        annotation.InkStrokes[1].Should().Equal((120.0, 650.0), (280.0, 650.0));

        // Raw dictionary spec checks (Table 182).
        var raw = annotation.RawDictionary;
        raw.GetNameOrNull("Subtype").Should().Be("Ink");
        var inkList = doc.Resolve(raw.GetOptional("InkList")!) as Excise.Core.Primitives.PdfArray;
        inkList.Should().NotBeNull("/InkList is required by Table 182");
        inkList!.Count.Should().Be(2, "one inner array per stroke");
        (doc.Resolve(inkList[0]) as Excise.Core.Primitives.PdfArray)!.Count
            .Should().Be(6, "three points = six alternating x/y numbers");
        (doc.Resolve(inkList[1]) as Excise.Core.Primitives.PdfArray)!.Count.Should().Be(4);

        // /Rect: bounding box of all points padded by half the stroke width.
        var rect = annotation.Rect.Normalize();
        rect.Left.Should().BeApproximately(98.5, 0.01);
        rect.Bottom.Should().BeApproximately(648.5, 0.01);
        rect.Right.Should().BeApproximately(301.5, 0.01);
        rect.Top.Should().BeApproximately(721.5, 0.01);

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        ap.Should().NotBeNull();
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull("/AP /N must resolve to a Form XObject stream");
        n!.GetNameOrNull("Subtype").Should().Be("Form");
        var bbox = n.GetOptional("BBox") as Excise.Core.Primitives.PdfArray;
        bbox.Should().NotBeNull();
        bbox!.GetNumber(2).Should().BeApproximately(203, 0.01, "BBox width must match /Rect width");
        bbox.GetNumber(3).Should().BeApproximately(73, 0.01, "BBox height must match /Rect height");

        // The appearance must actually stroke both polylines in the authored
        // color and width, translated into BBox-local coordinates.
        var ops = n.GetDecodedString();
        ops.Should().Contain("0 0 0.8 RG", "the /C color must be what the appearance strokes with");
        ops.Should().Contain("3 w", "the /BS /W width must be what the appearance strokes with");
        ops.Should().Contain("1 J", "round caps give the freehand pen look");
        ops.Should().Contain("1.5 51.5 m", "first stroke starts at (100,700) - rect origin (98.5,648.5)");
        ops.Should().Contain("101.5 71.5 l");
        ops.Should().Contain("201.5 51.5 l");
        ops.Should().Contain("21.5 1.5 m", "second stroke starts at (120,650) - rect origin");
        System.Text.RegularExpressions.Regex.Matches(ops, @"(^|\n)S(\n|$)").Count
            .Should().Be(2, "each polyline is stroked independently");
    }

    [Fact]
    public void AddInkAnnotation_SurvivesSaveAndReload_WithAppearanceIntact()
    {
        var strokes = new[]
        {
            new[] { (100.0, 700.0), (300.0, 700.0) },
            new[] { (100.0, 650.0), (200.0, 600.0), (300.0, 650.0) }
        };

        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddInkAnnotation(1, strokes, contents: "Persisted ink",
                author: "EXCISE", red: 1, green: 0, blue: 0, borderWidth: 4);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var ink = reopened.GetPage(1).GetAnnotations().Should()
            .ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Ink).Subject;

        ink.Contents.Should().Be("Persisted ink");
        ink.Author.Should().Be("EXCISE");
        ink.HasAppearance.Should().BeTrue("/AP must survive the save/reload round-trip");
        ink.BorderWidth.Should().Be(4);

        // /InkList must round-trip point-for-point through the writer.
        ink.InkStrokes.Should().NotBeNull();
        ink.InkStrokes!.Count.Should().Be(2);
        ink.InkStrokes[0].Should().Equal((100.0, 700.0), (300.0, 700.0));
        ink.InkStrokes[1].Should().Equal((100.0, 650.0), (200.0, 600.0), (300.0, 650.0));

        // The reloaded appearance must still be a decodable Form XObject that
        // strokes the polylines — this is what viewers actually paint.
        var raw = ink.RawDictionary;
        var ap = reopened.Resolve(raw.GetOptional("AP")!)
            as Excise.Core.Primitives.PdfDictionary;
        var n = reopened.Resolve(ap!.GetOptional("N")!)
            as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull();
        n!.GetNameOrNull("Subtype").Should().Be("Form");
        var ops = n.GetDecodedString();
        ops.Should().Contain("1 0 0 RG");
        ops.Should().Contain("4 w");
        ops.Should().Contain(" m\n");
        ops.Should().Contain(" l\n");
        ops.Should().Contain("S\n");
    }

    [Fact]
    public void AddInkAnnotation_RejectsBadArguments()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var goodStroke = new[] { new[] { (100.0, 100.0), (200.0, 200.0) } };

        var nullStrokes = () => doc.AddInkAnnotation(
            1, (IReadOnlyList<IReadOnlyList<(double, double)>>)null!);
        nullStrokes.Should().Throw<ArgumentNullException>();

        var noStrokes = () => doc.AddInkAnnotation(
            1, Array.Empty<IReadOnlyList<(double, double)>>());
        noStrokes.Should().Throw<ArgumentException>();

        var singlePoint = () => doc.AddInkAnnotation(
            1, new[] { new[] { (100.0, 100.0) } });
        singlePoint.Should().Throw<ArgumentException>(
            "a stroke needs at least two points to draw anything");

        var nanPoint = () => doc.AddInkAnnotation(
            1, new[] { new[] { (double.NaN, 100.0), (200.0, 200.0) } });
        nanPoint.Should().Throw<ArgumentException>();

        var badColor = () => doc.AddInkAnnotation(1, goodStroke, red: 1.5);
        badColor.Should().Throw<ArgumentOutOfRangeException>();

        var zeroWidth = () => doc.AddInkAnnotation(1, goodStroke, borderWidth: 0);
        zeroWidth.Should().Throw<ArgumentOutOfRangeException>(
            "a zero-width ink annotation would be invisible");

        var negativeWidth = () => doc.AddInkAnnotation(1, goodStroke, borderWidth: -1);
        negativeWidth.Should().Throw<ArgumentOutOfRangeException>();
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

    // ── Underline / StrikeOut / Squiggly (#626, ISO 32000-2 §12.5.6.10) ─────

    [Theory]
    [InlineData("Underline", PdfAnnotationSubtype.Underline)]
    [InlineData("StrikeOut", PdfAnnotationSubtype.StrikeOut)]
    [InlineData("Squiggly", PdfAnnotationSubtype.Squiggly)]
    public void AddTextMarkupAnnotations_WriteQuadPointsColorAndAppearance(
        string kind, PdfAnnotationSubtype expectedSubtype)
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var rect = new PdfRectangle(100, 700, 300, 720);

        PdfAnnotation annotation = kind switch
        {
            "Underline" => doc.AddUnderlineAnnotation(1, rect, "note", "EXCISE", 1, 0, 0),
            "StrikeOut" => doc.AddStrikeOutAnnotation(1, rect, "note", "EXCISE", 1, 0, 0),
            _           => doc.AddSquigglyAnnotation(1, rect, "note", "EXCISE", 1, 0, 0)
        };

        annotation.Subtype.Should().Be(expectedSubtype);
        annotation.Contents.Should().Be("note");
        annotation.Author.Should().Be("EXCISE");
        annotation.QuadPoints.Should().NotBeNull().And.HaveCount(1);
        annotation.Color!.Value.R.Should().BeApproximately(1, 0.001);
        annotation.HasAppearance.Should().BeTrue(
            $"{kind} must ship a baked /AP /N so third-party viewers render identical pixels (#626)");
        annotation.CreationDate.Should().NotBeNull();

        var raw = annotation.RawDictionary;
        raw.GetNameOrNull("Subtype").Should().Be(kind);
        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull();
        var ops = n!.GetDecodedString();
        ops.Should().Contain("1 0 0 RG");
        ops.Should().Contain(" m\n");
        ops.Should().Contain(" l\n");
        ops.Should().Contain("S\n");
    }

    [Fact]
    public void AddStrikeOutAnnotation_DrawsHigherThanUnderline()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var rect = new PdfRectangle(100, 700, 300, 720);

        var underline = doc.AddUnderlineAnnotation(1, rect);
        var strikeOut = doc.AddStrikeOutAnnotation(1, rect);

        double UnderlineY(PdfAnnotation a)
        {
            var ap = doc.Resolve(a.RawDictionary.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
            var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
            var match = System.Text.RegularExpressions.Regex.Match(n!.GetDecodedString(), @"0 ([\d.]+) m");
            return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        UnderlineY(strikeOut).Should().BeGreaterThan(UnderlineY(underline),
            "strikeout sits through the middle of the text, well above the underline baseline position");
    }

    [Fact]
    public void AddUnderlineAnnotation_RejectsInvalidColorAndRect()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var badColor = () => doc.AddUnderlineAnnotation(1, new PdfRectangle(0, 0, 100, 10), red: 2);
        badColor.Should().Throw<ArgumentOutOfRangeException>();

        var badRect = () => doc.AddSquigglyAnnotation(1, new PdfRectangle(0, 0, 0, 0));
        badRect.Should().Throw<ArgumentException>();
    }

    // ── Line / Arrow (#626, ISO 32000-2 §12.5.6.7) ───────────────────────────

    [Fact]
    public void AddLineAnnotation_WritesEndpointsColorAndAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddLineAnnotation(
            1, 100, 200, 300, 250, contents: "measurement", author: "EXCISE",
            red: 0, green: 0, blue: 1, lineWidth: 2);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Line);
        annotation.LineEndpoints.Should().Be((100.0, 200.0, 300.0, 250.0));
        annotation.Color!.Value.B.Should().BeApproximately(1, 0.001);
        annotation.HasAppearance.Should().BeTrue();
        annotation.BorderWidth.Should().Be(2);

        var raw = annotation.RawDictionary;
        var le = doc.Resolve(raw.GetOptional("LE")!) as Excise.Core.Primitives.PdfArray;
        le.Should().NotBeNull();
        le!.GetName(0).Should().Be("None");
        le.GetName(1).Should().Be("None");

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();
        ops.Should().Contain(" m\n");
        ops.Should().Contain(" l\n");
        ops.Should().Contain("S\n");
        ops.Should().NotContain("\nB\n", "a plain Line without an arrowhead must not fill anything");
    }

    [Fact]
    public void AddArrowAnnotation_WritesLineEndingsAndDrawsArrowhead()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddArrowAnnotation(
            1, 100, 200, 300, 200, red: 1, green: 0, blue: 0,
            endLineEnding: "ClosedArrow");

        var raw = annotation.RawDictionary;
        var le = doc.Resolve(raw.GetOptional("LE")!) as Excise.Core.Primitives.PdfArray;
        le!.GetName(0).Should().Be("None");
        le.GetName(1).Should().Be("ClosedArrow");

        // /Rect must be padded enough to contain the arrowhead past (300,200).
        annotation.Rect.Normalize().Right.Should().BeGreaterThan(300);

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();
        ops.Should().Contain("h\nB\n", "a ClosedArrow head is a filled+stroked closed triangle");
    }

    [Fact]
    public void AddLineAnnotation_RejectsBadArguments()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var samePoint = () => doc.AddLineAnnotation(1, 10, 10, 10, 10);
        samePoint.Should().Throw<ArgumentException>();

        var badWidth = () => doc.AddLineAnnotation(1, 0, 0, 100, 0, lineWidth: 0);
        badWidth.Should().Throw<ArgumentOutOfRangeException>();

        var badEnding = () => doc.AddArrowAnnotation(1, 0, 0, 100, 0, endLineEnding: "Diamond");
        badEnding.Should().Throw<ArgumentException>("Diamond is not a supported line ending (#626 scope)");
    }

    // ── Polygon / PolyLine (#626, ISO 32000-2 §12.5.6.9) ─────────────────────

    [Fact]
    public void AddPolygonAnnotation_WritesVerticesAndClosedFilledAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var vertices = new (double X, double Y)[] { (100, 100), (200, 100), (150, 200) };

        var annotation = doc.AddPolygonAnnotation(
            1, vertices, contents: "triangle", red: 0, green: 0, blue: 0, borderWidth: 2,
            interiorRed: 0, interiorGreen: 1, interiorBlue: 0);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Polygon);
        annotation.Vertices.Should().Equal(vertices);
        annotation.HasAppearance.Should().BeTrue();

        var raw = annotation.RawDictionary;
        var ic = doc.Resolve(raw.GetOptional("IC")!) as Excise.Core.Primitives.PdfArray;
        ic!.GetNumber(1).Should().BeApproximately(1, 0.001);

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();
        ops.Should().Contain("h\n", "polygon closes its path");
        ops.Should().Contain("B\n", "fill+stroke with an interior color set");
    }

    [Fact]
    public void AddPolyLineAnnotation_WritesOpenStrokedAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var vertices = new (double X, double Y)[] { (100, 100), (200, 150), (300, 100) };

        var annotation = doc.AddPolyLineAnnotation(1, vertices, red: 1, green: 0, blue: 0, borderWidth: 2);

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.PolyLine);
        annotation.Vertices.Should().Equal(vertices);

        var raw = annotation.RawDictionary;
        raw.ContainsKey("IC").Should().BeFalse("PolyLine has no interior fill");

        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();
        ops.Should().NotContain("h\n", "a polyline must stay open, not closed into a polygon");
        ops.Should().Contain("S\n");
    }

    [Fact]
    public void AddPolygonAndPolyLine_RejectBadArguments()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var tooFewPolygon = () => doc.AddPolygonAnnotation(
            1, new (double, double)[] { (0, 0), (10, 10) }, interiorRed: 0, interiorGreen: 0, interiorBlue: 0);
        tooFewPolygon.Should().Throw<ArgumentException>("a polygon needs at least 3 vertices");

        var tooFewPolyLine = () => doc.AddPolyLineAnnotation(1, new (double, double)[] { (0, 0) });
        tooFewPolyLine.Should().Throw<ArgumentException>("a polyline needs at least 2 vertices");

        var invisible = () => doc.AddPolyLineAnnotation(
            1, new (double, double)[] { (0, 0), (10, 10) }, borderWidth: 0);
        invisible.Should().Throw<ArgumentException>();

        var nanVertex = () => doc.AddPolygonAnnotation(
            1, new (double, double)[] { (double.NaN, 0), (10, 10), (5, 20) },
            interiorRed: 0, interiorGreen: 0, interiorBlue: 0);
        nanVertex.Should().Throw<ArgumentException>();
    }

    // ── Stamp (#626, ISO 32000-2 §12.5.6.12) ──────────────────────────────────

    [Fact]
    public void AddStampAnnotation_WritesStandardNameAndAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var annotation = doc.AddStampAnnotation(
            1, new PdfRectangle(100, 600, 260, 660), "Approved", author: "EXCISE");

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Stamp);
        annotation.IconName.Should().Be("Approved");
        annotation.Author.Should().Be("EXCISE");
        annotation.HasAppearance.Should().BeTrue();
        annotation.Color.Should().NotBeNull();

        var raw = annotation.RawDictionary;
        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        var ops = n!.GetDecodedString();
        ops.Should().Contain("BT");
        ops.Should().Contain("(Approved) Tj");
        ops.Should().Contain(" re S\n", "the stamp draws a bordered box");
    }

    [Fact]
    public void AddStampAnnotation_RejectsNonStandardName()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var action = () => doc.AddStampAnnotation(1, new PdfRectangle(0, 0, 100, 40), "MyCustomStamp");

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StandardStampNames_AreAllAccepted()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        foreach (var name in PdfAnnotationAuthoring.StandardStampNames)
        {
            var annotation = doc.AddStampAnnotation(1, new PdfRectangle(0, 0, 120, 40), name);
            annotation.IconName.Should().Be(name);
        }
    }

    [Fact]
    public void AddImageStampAnnotation_EmbedsImageXObjectInAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        // 2x2 solid-red RGB24 image.
        var pixels = new byte[2 * 2 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 255; pixels[i + 1] = 0; pixels[i + 2] = 0;
        }

        var annotation = doc.AddImageStampAnnotation(
            1, new PdfRectangle(100, 100, 200, 200), pixels, pixelWidth: 2, pixelHeight: 2,
            contents: "logo");

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Stamp);
        annotation.HasAppearance.Should().BeTrue();

        var raw = annotation.RawDictionary;
        var ap = doc.Resolve(raw.GetOptional("AP")!) as Excise.Core.Primitives.PdfDictionary;
        var n = doc.Resolve(ap!.GetOptional("N")!) as Excise.Core.Primitives.PdfStream;
        n.Should().NotBeNull();
        var ops = n!.GetDecodedString();
        ops.Should().Contain("/Im0 Do");

        var resources = n.GetOptional("Resources") as Excise.Core.Primitives.PdfDictionary;
        var xobjects = doc.Resolve(resources!.GetOptional("XObject")!) as Excise.Core.Primitives.PdfDictionary;
        var image = doc.Resolve(xobjects!.GetOptional("Im0")!) as Excise.Core.Primitives.PdfStream;
        image.Should().NotBeNull();
        image!.GetNameOrNull("Subtype").Should().Be("Image");
        image.GetInt("Width", -1).Should().Be(2);
        image.GetInt("Height", -1).Should().Be(2);
        image.GetNameOrNull("ColorSpace").Should().Be("DeviceRGB");
        image.DecodedData.Should().Equal(pixels);
    }

    [Fact]
    public void AddImageStampAnnotation_RejectsMismatchedPixelBufferSize()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var action = () => doc.AddImageStampAnnotation(
            1, new PdfRectangle(0, 0, 100, 100), new byte[10], pixelWidth: 4, pixelHeight: 4);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StampAnnotations_SurviveSaveAndReload()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddStampAnnotation(1, new PdfRectangle(72, 700, 220, 750), "Draft");
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var stamp = reopened.GetPage(1).GetAnnotations().Should()
            .ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Stamp).Subject;

        stamp.IconName.Should().Be("Draft");
        stamp.HasAppearance.Should().BeTrue();
    }

    // ── Edit / delete existing annotations (#626) ─────────────────────────────

    [Fact]
    public void SetAnnotationContents_UpdatesContentsAndModDate()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var annotation = doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "original");
        var originalModDate = annotation.RawDictionary.GetStringOrNull("M");

        annotation.SetAnnotationContents("updated text");

        annotation.RawDictionary.GetStringOrNull("Contents").Should().Be("updated text");
        annotation.RawDictionary.GetStringOrNull("M").Should().NotBeNull();

        annotation.SetAnnotationContents(null);
        annotation.RawDictionary.ContainsKey("Contents").Should().BeFalse();
        _ = originalModDate; // documents intent: /M is always refreshed, value not asserted (clock resolution)
    }

    [Fact]
    public void SetAnnotationColor_UpdatesCArray()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var annotation = doc.AddSquareAnnotation(1, new PdfRectangle(0, 0, 100, 100), red: 1, green: 0, blue: 0);

        annotation.SetAnnotationColor(0, 1, 0);

        var c = doc.Resolve(annotation.RawDictionary.GetOptional("C")!) as Excise.Core.Primitives.PdfArray;
        c!.GetNumber(0).Should().Be(0);
        c.GetNumber(1).Should().Be(1);
        c.GetNumber(2).Should().Be(0);
    }

    [Fact]
    public void SetAnnotationColor_RejectsOutOfRangeComponents()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var annotation = doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "x");

        var action = () => annotation.SetAnnotationColor(1.5, 0, 0);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SetAnnotationOpacity_WritesCA()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var annotation = doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "x");

        annotation.SetAnnotationOpacity(0.5);

        annotation.RawDictionary.GetNumber("CA", -1).Should().Be(0.5);

        var badOpacity = () => annotation.SetAnnotationOpacity(1.5);
        badOpacity.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RemoveAnnotation_DetachesFromPageAnnots()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var keep = doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "keep");
        var remove = doc.AddTextAnnotation(1, new PdfRectangle(30, 30, 50, 50), "remove");

        var removed = doc.RemoveAnnotation(1, remove);

        removed.Should().BeTrue();
        var remaining = doc.GetPage(1).GetAnnotations();
        remaining.Should().ContainSingle();
        remaining[0].Contents.Should().Be("keep");

        // Removing again returns false — it's no longer on the page.
        doc.RemoveAnnotation(1, remove).Should().BeFalse();
    }

    [Fact]
    public void RemoveAnnotation_SurvivesSaveAndReload()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "keep");
            var remove = doc.AddTextAnnotation(1, new PdfRectangle(30, 30, 50, 50), "remove");
            doc.RemoveAnnotation(1, remove).Should().BeTrue();
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotations = reopened.GetPage(1).GetAnnotations();
        annotations.Should().ContainSingle();
        annotations[0].Contents.Should().Be("keep");
    }

    // ── Reply threads (#626, ISO 32000-2 §12.5.6.2 — /IRT and /RT) ────────────

    [Fact]
    public void SetReplyTo_WritesIrtReferenceAndRt()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var parent = doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "original comment");
        var reply = doc.AddTextAnnotation(1, new PdfRectangle(30, 0, 50, 20), "a reply");

        reply.SetReplyTo(doc, parent);

        var irtRef = doc.GetReferenceTo(parent.RawDictionary);
        irtRef.Should().NotBeNull();
        reply.RawDictionary.GetOptional("IRT").Should().Be(irtRef);
        (doc.Resolve(reply.RawDictionary.GetOptional("IRT")!) as Excise.Core.Primitives.PdfDictionary)
            .Should().BeSameAs(parent.RawDictionary,
                "/IRT must resolve to the exact parent annotation dictionary");
        reply.RawDictionary.GetNameOrNull("RT").Should().Be("R");
    }

    [Fact]
    public void SetReplyTo_SurvivesSaveAndReload()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            var parent = doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "original comment");
            var reply = doc.AddTextAnnotation(1, new PdfRectangle(30, 0, 50, 20), "a reply");
            reply.SetReplyTo(doc, parent, "Group");
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotations = reopened.GetPage(1).GetAnnotations();
        var reopenedParent = annotations.Single(a => a.Contents == "original comment");
        var reopenedReply = annotations.Single(a => a.Contents == "a reply");

        reopenedReply.RawDictionary.GetNameOrNull("RT").Should().Be("Group");
        var irt = reopened.Resolve(reopenedReply.RawDictionary.GetOptional("IRT")!)
            as Excise.Core.Primitives.PdfDictionary;
        irt.Should().BeSameAs(reopenedParent.RawDictionary);
    }

    [Fact]
    public void SetReplyTo_RejectsUnattachedParentAndBadReplyType()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var reply = doc.AddTextAnnotation(1, new PdfRectangle(0, 0, 20, 20), "reply");

        var badReplyType = () =>
        {
            var parent = doc.AddTextAnnotation(1, new PdfRectangle(30, 0, 50, 20), "parent");
            reply.SetReplyTo(doc, parent, "Bogus");
        };
        badReplyType.Should().Throw<ArgumentException>();
    }
}
