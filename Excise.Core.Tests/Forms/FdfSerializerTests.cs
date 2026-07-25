using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Forms;
using Excise.Core.Parsing;
using Xunit;

namespace Excise.Core.Tests.Forms;

/// <summary>
/// FDF annotation import/export round-trip (#626) — the PDF-syntax
/// counterpart to <see cref="XfdfSerializerTests"/>.
///
/// <para>
/// Oracle note (no-self-oracle): the expected FDF grammar in these tests —
/// header line, catalog shape, /FDF /Annots, 0-based /Page, trailer /Root —
/// is hand-derived from ISO 32000-1 §12.7.7/§12.7.8 and from the shape of
/// real Acrobat "Export comments" FDF output, not from excise's own
/// serializer. <see cref="Import_AcrobatStyleSample_ParsesAllSupportedSubtypes"/>
/// feeds a fixture written in that external dialect (xref table, /UF file
/// spec, indirect /Rect values, binary comment line) through the importer.
/// </para>
/// </summary>
public class FdfSerializerTests
{
    private const string SampleDate = "D:20260725093000+00'00'";

    // ── Export ────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_ProducesParsableFdfWithCatalogAndTrailer()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddSquareAnnotation(1, new PdfRectangle(100, 500, 200, 560),
            contents: "Check this", author: "Reviewer A",
            red: 1, green: 0, blue: 0, borderWidth: 2,
            interiorRed: 0, interiorGreen: 1, interiorBlue: 0);

        var fdf = FdfSerializer.ExportAnnotations(doc);

        fdf.Should().StartWith("%FDF-1.2");
        fdf.Should().EndWith("%%EOF\n");
        fdf.Should().Contain("/FDF").And.Contain("/Annots").And.Contain("trailer").And.Contain("/Root 1 0 R");

        // The annotation dictionary carries the PDF-native entries.
        fdf.Should().Contain("/Subtype /Square");
        fdf.Should().Contain("/Page 0", "FDF /Page is 0-based (ISO 32000-1 §12.7.7.3.3)");
        fdf.Should().Contain("/Rect [100 500 200 560]");
        fdf.Should().Contain("/C [1 0 0]");
        fdf.Should().Contain("/IC [0 1 0]");
        fdf.Should().Contain("(Reviewer A)");
        fdf.Should().Contain("(Check this)");
    }

    [Fact]
    public void Export_EmptyDocument_YieldsEmptyAnnotsArray()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var fdf = FdfSerializer.ExportAnnotations(doc);

        fdf.Should().Contain("/Annots []");
        fdf.Should().NotContain("/Subtype /");
    }

    [Fact]
    public void Export_SkipsLinkWidgetAndPopupSubtypes()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Keep me");

        // Hand-plant a Link annotation (no authoring method exports these).
        var link = new Excise.Core.Primitives.PdfDictionary();
        link.SetName("Type", "Annot");
        link.SetName("Subtype", "Link");
        link["Rect"] = Excise.Core.Primitives.PdfArray.FromRectangle(0, 0, 100, 20);
        var pageDict = doc.GetPage(1).Dictionary;
        pageDict.GetArrayOrNull("Annots")!.Add(link);

        var fdf = FdfSerializer.ExportAnnotations(doc);

        fdf.Should().Contain("/Subtype /Text");
        fdf.Should().NotContain("/Subtype /Link");
    }

    [Fact]
    public void Export_StreamOverload_MatchesStringOverload()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Ünïcode ✓ note");

        var asString = FdfSerializer.ExportAnnotations(doc);
        using var ms = new MemoryStream();
        FdfSerializer.ExportAnnotations(doc, ms);

        Encoding.Latin1.GetString(ms.ToArray()).Should().Be(asString);
    }

    // ── Round-trip: authored → export → import → compare ─────────────────────

    [Fact]
    public void RoundTrip_AuthoredSubtypes_SurviveExportAndImport()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();

        source.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736),
            "Sticky note body", author: "Alice", open: true, iconName: "Comment");
        source.AddHighlightAnnotation(1, new PdfRectangle(100, 650, 300, 670),
            contents: "Highlighted claim", author: "Alice", red: 1, green: 0.92, blue: 0.2);
        source.AddSquareAnnotation(1, new PdfRectangle(50, 400, 150, 480),
            contents: "Box comment", author: "Bob",
            red: 0.8, green: 0.1, blue: 0.1, borderWidth: 3,
            interiorRed: 0.9, interiorGreen: 0.9, interiorBlue: 0.2);
        source.AddCircleAnnotation(1, new PdfRectangle(200, 400, 320, 480),
            contents: "Circle comment", author: "Bob",
            red: 0, green: 0, blue: 1, borderWidth: 1.5);
        source.AddFreeTextAnnotation(1, new PdfRectangle(72, 200, 400, 280),
            "Visible text box", author: "Carol", fontSize: 14,
            textRed: 0.2, textGreen: 0.2, textBlue: 0.8,
            quadding: PdfFreeTextQuadding.Centered, borderWidth: 1,
            backgroundRed: 1, backgroundGreen: 1, backgroundBlue: 0.8);
        source.AddInkAnnotation(1,
            new[]
            {
                (IReadOnlyList<(double, double)>)new[] { (100.0, 100.0), (140.0, 130.0), (180.0, 100.0) },
                new[] { (200.0, 100.0), (240.0, 140.0) }
            },
            contents: "Freehand mark", author: "Dave",
            red: 0.1, green: 0.6, blue: 0.1, borderWidth: 2.5);

        var fdf = FdfSerializer.ExportAnnotations(source);

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(target, fdf);

        result.Skipped.Should().BeEmpty();
        result.Imported.Should().HaveCount(6);

        var annotations = target.GetPage(1).GetAnnotations();
        annotations.Should().HaveCount(6);

        var sourceAnnotations = source.GetPage(1).GetAnnotations();
        foreach (var expected in sourceAnnotations)
        {
            var actual = annotations.Single(a => a.Subtype == expected.Subtype);

            actual.Rect.Left.Should().BeApproximately(expected.Rect.Left, 0.001);
            actual.Rect.Bottom.Should().BeApproximately(expected.Rect.Bottom, 0.001);
            actual.Rect.Right.Should().BeApproximately(expected.Rect.Right, 0.001);
            actual.Rect.Top.Should().BeApproximately(expected.Rect.Top, 0.001);
            actual.Contents.Should().Be(expected.Contents);
            actual.Author.Should().Be(expected.Author);
            actual.Name.Should().Be(expected.Name, "/NM must survive the round-trip");
            actual.Flags.Should().Be(expected.Flags);

            if (expected.Color is { } c)
            {
                actual.Color.Should().NotBeNull();
                // FDF passes /C through as PDF reals — tighter than XFDF's
                // 8-bit #RRGGBB channel quantization.
                actual.Color!.Value.R.Should().BeApproximately(c.R, 0.0001);
                actual.Color.Value.G.Should().BeApproximately(c.G, 0.0001);
                actual.Color.Value.B.Should().BeApproximately(c.B, 0.0001);
            }
        }

        // Subtype-specific geometry.
        var ink = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Ink);
        ink.InkStrokes.Should().NotBeNull().And.HaveCount(2);
        ink.InkStrokes![0].Should().HaveCount(3);
        ink.InkStrokes[0][1].X.Should().BeApproximately(140, 0.001);
        ink.InkStrokes[0][1].Y.Should().BeApproximately(130, 0.001);
        ink.BorderWidth.Should().Be(2.5);

        var highlight = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Highlight);
        highlight.QuadPoints.Should().NotBeNull().And.HaveCount(1);
        highlight.QuadPoints![0].Left.Should().BeApproximately(100, 0.001);
        highlight.QuadPoints[0].Top.Should().BeApproximately(670, 0.001);

        var freeText = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.FreeText);
        freeText.RawDictionary.GetInt("Q", -1).Should().Be(1, "centered quadding must survive");
        freeText.HasAppearance.Should().BeTrue("authoring-backed import bakes /AP");

        var sticky = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        sticky.IsOpen.Should().BeTrue();
        sticky.IconName.Should().Be("Comment");

        var square = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Square);
        square.BorderWidth.Should().Be(3);
        square.RawDictionary.GetArrayOrNull("IC").Should().NotBeNull("interior color must survive");
    }

    [Fact]
    public void RoundTrip_ModificationDates_PassThroughVerbatim()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        var authored = source.AddSquareAnnotation(1, new PdfRectangle(10, 10, 60, 60), borderWidth: 1);
        authored.RawDictionary.SetString("M", SampleDate);
        authored.RawDictionary.SetString("CreationDate", SampleDate);

        var fdf = FdfSerializer.ExportAnnotations(source);
        fdf.Should().Contain(SampleDate, "PDF date strings pass through verbatim");

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var imported = FdfSerializer.ImportAnnotations(target, fdf).Imported.Single();

        imported.RawDictionary.GetStringOrNull("M").Should().Be(SampleDate);
        imported.RawDictionary.GetStringOrNull("CreationDate").Should().Be(SampleDate);
    }

    [Fact]
    public void RoundTrip_ImportedAnnotations_SurviveSaveAndReload()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        source.AddFreeTextAnnotation(1, new PdfRectangle(72, 500, 300, 560), "Persisted via FDF");
        var fdf = FdfSerializer.ExportAnnotations(source);

        byte[] saved;
        using (var target = PdfDocument.CreateNew())
        {
            target.Pages.AddBlank();
            FdfSerializer.ImportAnnotations(target, fdf);
            saved = target.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotation = reopened.GetPage(1).GetAnnotations().Should().ContainSingle().Subject;
        annotation.Subtype.Should().Be(PdfAnnotationSubtype.FreeText);
        annotation.Contents.Should().Be("Persisted via FDF");
        annotation.HasAppearance.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_UnicodeContents_SurviveAsUtf16Strings()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        const string tricky = "a < b && \"c\" > 'd' — æøå 中文 ✓ (parens) \\backslash";
        source.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), tricky);

        var fdf = FdfSerializer.ExportAnnotations(source);

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var imported = FdfSerializer.ImportAnnotations(target, fdf).Imported.Single();
        imported.Contents.Should().Be(tricky);
    }

    [Fact]
    public void RoundTrip_GenericSubtypes_AreStableAcrossTwoPasses()
    {
        // Line/Polygon/Underline have no authoring method: the importer builds
        // raw dictionaries. Import → export → import must be lossless.
        string firstPass = $$"""
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R 3 0 R 4 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /Line /Page 0 /Rect [50 50 250 120] /C [0 0 1]
               /L [60 60 240 110] /T (Eve) /NM (line-1) /M (D:20260725093000+00'00') >>
            endobj
            3 0 obj
            << /Type /Annot /Subtype /Polygon /Page 0 /Rect [300 300 420 400] /C [0 0.666667 0]
               /NM (poly-1) /Vertices [300 300 420 300 360 400] >>
            endobj
            4 0 obj
            << /Type /Annot /Subtype /Underline /Page 0 /Rect [100 600 300 615] /C [0.8 0 0]
               /NM (ul-1) /QuadPoints [100 615 300 615 100 600 300 600]
               /Contents (Underlined claim) >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc1 = PdfDocument.CreateNew();
        doc1.Pages.AddBlank();
        var pass1 = FdfSerializer.ImportAnnotations(doc1, firstPass);
        pass1.Skipped.Should().BeEmpty();
        pass1.Imported.Should().HaveCount(3);

        var exported = FdfSerializer.ExportAnnotations(doc1);

        using var doc2 = PdfDocument.CreateNew();
        doc2.Pages.AddBlank();
        var pass2 = FdfSerializer.ImportAnnotations(doc2, exported);
        pass2.Skipped.Should().BeEmpty();

        var annotations = doc2.GetPage(1).GetAnnotations();
        annotations.Should().HaveCount(3);

        var line = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Line);
        line.LineEndpoints.Should().Be((60d, 60d, 240d, 110d));
        line.Author.Should().Be("Eve");
        line.Name.Should().Be("line-1");
        line.RawDictionary.GetStringOrNull("M").Should().Be(SampleDate);

        var polygon = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Polygon);
        polygon.Vertices.Should().Equal((300d, 300d), (420d, 300d), (360d, 400d));

        var underline = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Underline);
        underline.Contents.Should().Be("Underlined claim");
        underline.QuadPoints.Should().ContainSingle();
        underline.QuadPoints![0].Should().Be(new PdfRectangle(100, 600, 300, 615));
    }

    // ── Cross-format: FDF and XFDF of the same annotations are equivalent ────

    [Fact]
    public void CrossFormat_FdfAndXfdf_ImportToEquivalentAnnotations()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        source.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736),
            "Shared note", author: "Alice", open: true, iconName: "Comment");
        source.AddSquareAnnotation(1, new PdfRectangle(50, 400, 150, 480),
            contents: "Shared box", author: "Bob",
            red: 0.8, green: 0.1, blue: 0.1, borderWidth: 3);
        source.AddInkAnnotation(1,
            new[] { (IReadOnlyList<(double, double)>)new[] { (100.0, 100.0), (140.0, 130.0) } },
            contents: "Shared stroke", author: "Dave",
            red: 0.1, green: 0.6, blue: 0.1, borderWidth: 2);

        var fdf = FdfSerializer.ExportAnnotations(source);
        var xfdf = XfdfSerializer.ExportAnnotations(source);

        using var viaFdf = PdfDocument.CreateNew();
        viaFdf.Pages.AddBlank();
        FdfSerializer.ImportAnnotations(viaFdf, fdf).Skipped.Should().BeEmpty();

        using var viaXfdf = PdfDocument.CreateNew();
        viaXfdf.Pages.AddBlank();
        XfdfSerializer.ImportAnnotations(viaXfdf, xfdf).Skipped.Should().BeEmpty();

        var fromFdf = viaFdf.GetPage(1).GetAnnotations();
        var fromXfdf = viaXfdf.GetPage(1).GetAnnotations();
        fromFdf.Should().HaveCount(3);
        fromXfdf.Should().HaveCount(3);

        foreach (var f in fromFdf)
        {
            var x = fromXfdf.Single(a => a.Subtype == f.Subtype);
            x.Rect.Left.Should().BeApproximately(f.Rect.Left, 0.001);
            x.Rect.Bottom.Should().BeApproximately(f.Rect.Bottom, 0.001);
            x.Rect.Right.Should().BeApproximately(f.Rect.Right, 0.001);
            x.Rect.Top.Should().BeApproximately(f.Rect.Top, 0.001);
            x.Contents.Should().Be(f.Contents);
            x.Author.Should().Be(f.Author);
            x.Name.Should().Be(f.Name, "/NM must agree across formats");
            x.Flags.Should().Be(f.Flags);
            if (f.Color is { } fc)
            {
                x.Color.Should().NotBeNull();
                // XFDF quantizes colors to 8 bits per channel; FDF does not.
                x.Color!.Value.R.Should().BeApproximately(fc.R, 1 / 255.0 + 0.0001);
                x.Color.Value.G.Should().BeApproximately(fc.G, 1 / 255.0 + 0.0001);
                x.Color.Value.B.Should().BeApproximately(fc.B, 1 / 255.0 + 0.0001);
            }
        }

        var inkF = fromFdf.Single(a => a.Subtype == PdfAnnotationSubtype.Ink);
        var inkX = fromXfdf.Single(a => a.Subtype == PdfAnnotationSubtype.Ink);
        inkF.InkStrokes.Should().BeEquivalentTo(inkX.InkStrokes);
    }

    // ── Interop: spec-derived external dialect ────────────────────────────────

    [Fact]
    public void Import_AcrobatStyleSample_ParsesAllSupportedSubtypes()
    {
        // Fixture written in the dialect real Acrobat emits for FDF comment
        // export: binary comment line, xref table + startxref, /F file spec,
        // /ID in the /FDF dictionary, indirect object per annotation, and an
        // indirectly-referenced /Rect (all legal PDF syntax the importer must
        // tolerate). Offsets in the xref table are deliberately wrong — FDF
        // readers must not depend on them (§12.7.8: xref is optional).
        const string acrobatFdf = """
            %FDF-1.2
            %âãÏÓ
            1 0 obj
            << /FDF << /Annots [3 0 R 4 0 R 5 0 R 6 0 R] /F (source-document.pdf)
               /ID [<F1E2D3C4B5A69788> <8877A695B4C3D2E1>] >> >>
            endobj
            2 0 obj
            [132.2 679.2 266.5 706.9]
            endobj
            3 0 obj
            << /Type /Annot /Subtype /Highlight /Page 0 /Rect 2 0 R /C [1 0.819608 0]
               /F 4 /NM (8b3f5f64-0f4c-4f7e-9d5a-2f1a3c4d5e6f) /T (jdoe) /Subj (Highlight)
               /M (D:20260420101112-07'00') /CreationDate (D:20260420101112-07'00')
               /QuadPoints [132.2 706.9 266.5 706.9 132.2 694.1 266.5 694.1
                            132.2 692.0 201.0 692.0 132.2 679.2 201.0 679.2]
               /Contents (Two-quad highlight) >>
            endobj
            4 0 obj
            << /Type /Annot /Subtype /Text /Page 0 /Rect [431.6 730.4 449.6 748.4]
               /C [1 0.819608 0] /F 28 /Name /Comment /Open false /NM (note-1) /T (jdoe)
               /Contents (Please re-check the totals) >>
            endobj
            5 0 obj
            << /Type /Annot /Subtype /Ink /Page 0 /Rect [187.3 477.9 364.5 538.1]
               /C [1 0 0] /F 4 /NM (ink-1) /T (jdoe) /BS << /Type /Border /S /S /W 2.1 >>
               /InkList [[190.4 535.0 220.9 510.2 251.5 534.1 282.0 509.3]
                         [300.0 500.0 361.4 481.0]] >>
            endobj
            6 0 obj
            << /Type /Annot /Subtype /FreeText /Page 0 /Rect [72 72 288 130] /F 4
               /NM (ft-1) /T (jdoe) /Q 1 /DA (0 0.4 0 rg /Helv 11 Tf)
               /Contents (Typed comment) >>
            endobj
            7 0 obj
            << /Type /Annot /Subtype /Popup /Page 0 /Rect [450 600 650 730] /NM (popup-1) >>
            endobj
            xref
            0 2
            0000000000 65535 f
            0000000025 00000 n
            trailer
            << /Root 1 0 R >>
            startxref
            9999
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        // Popup is referenced nowhere in /Annots (object 7): it must simply be
        // ignored, and everything in /Annots must import.
        var result = FdfSerializer.ImportAnnotations(doc, acrobatFdf);

        result.Skipped.Should().BeEmpty();
        result.Imported.Should().HaveCount(4);

        var annotations = doc.GetPage(1).GetAnnotations();

        var highlight = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Highlight);
        highlight.Contents.Should().Be("Two-quad highlight");
        highlight.Author.Should().Be("jdoe");
        highlight.Name.Should().Be("8b3f5f64-0f4c-4f7e-9d5a-2f1a3c4d5e6f");
        highlight.Rect.Left.Should().BeApproximately(132.2, 0.001, "indirect /Rect must resolve");
        highlight.QuadPoints.Should().HaveCount(2, "the two Acrobat quads must not collapse");
        highlight.Color!.Value.R.Should().BeApproximately(1.0, 0.0001);
        highlight.Color.Value.G.Should().BeApproximately(0.819608, 0.0001);
        highlight.RawDictionary.GetStringOrNull("Subj").Should().Be("Highlight");
        highlight.CreationDate.Should().NotBeNull();
        highlight.CreationDate!.Value.Offset.Should().Be(TimeSpan.FromHours(-7));

        var sticky = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        sticky.IconName.Should().Be("Comment");
        sticky.IsOpen.Should().BeFalse();
        sticky.Flags.Should().Be(
            PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom | PdfAnnotationFlags.NoRotate);

        var ink = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Ink);
        ink.InkStrokes.Should().HaveCount(2);
        ink.InkStrokes![0].Should().HaveCount(4);
        ink.BorderWidth.Should().Be(2.1);
        ink.Color!.Value.R.Should().BeApproximately(1, 0.0001);

        var freeText = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.FreeText);
        freeText.Contents.Should().Be("Typed comment");
        freeText.RawDictionary.GetInt("Q", -1).Should().Be(1);
        freeText.RawDictionary.GetStringOrNull("DA").Should().Contain("0 0.4 0 rg").And.Contain("11 Tf");
    }

    // ── Error handling and skips ──────────────────────────────────────────────

    [Fact]
    public void Import_MalformedPdfSyntax_ThrowsPdfParseException()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var act = () => FdfSerializer.ImportAnnotations(doc, "this is not FDF at all");
        act.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void Import_PdfObjectsWithoutFdfCatalog_ThrowsInvalidDataException()
    {
        const string notFdf = """
            %FDF-1.2
            1 0 obj
            << /Type /Catalog >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var act = () => FdfSerializer.ImportAnnotations(doc, notFdf);
        act.Should().Throw<InvalidDataException>().WithMessage("*FDF*");
    }

    [Fact]
    public void Import_MissingTrailer_FallsBackToScanningForFdfCatalog()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /Square /Page 0 /Rect [10 10 50 50] /C [1 0 0]
               /BS << /W 1 /S /S >> /NM (no-trailer-square) >>
            endobj
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(doc, fdf);

        result.Imported.Should().ContainSingle().Which.Name.Should().Be("no-trailer-square");
    }

    [Fact]
    public void Import_PageOutOfRange_IsSkippedNotFatal()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R 3 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /Square /Page 7 /Rect [10 10 50 50] /C [1 0 0]
               /BS << /W 1 >> /NM (lost-square) >>
            endobj
            3 0 obj
            << /Type /Annot /Subtype /Square /Page 0 /Rect [10 10 50 50] /C [1 0 0]
               /BS << /W 1 >> /NM (kept-square) >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(doc, fdf);

        result.Imported.Should().ContainSingle().Which.Name.Should().Be("kept-square");
        result.Skipped.Should().ContainSingle().Which.Should().Contain("lost-square").And.Contain("out of range");
    }

    [Fact]
    public void Import_MissingRect_IsSkippedNotFatal()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /Highlight /Page 0 /NM (no-rect)
               /Contents (orphan) >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(doc, fdf);

        result.Imported.Should().BeEmpty();
        result.Skipped.Should().ContainSingle().Which.Should().Contain("no-rect").And.Contain("Rect");
    }

    [Fact]
    public void Import_UnsupportedSubtype_IsSkippedNotFatal()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /FancyFutureType /Page 0 /Rect [1 1 2 2] /NM (unknown-1) >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(doc, fdf);

        result.Imported.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Should().Contain("FancyFutureType").And.Contain("unsupported");
    }

    [Fact]
    public void Import_WithoutAnnotsArray_YieldsEmptyResult()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Fields [] >> >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(doc, fdf);

        result.Imported.Should().BeEmpty();
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Import_StreamOverload_MatchesStringOverload()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /Square /Page 0 /Rect [10 10 90 90] /C [0.2 0.4 0.6]
               /BS << /W 2 >> /NM (stream-square) >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        using var ms = new MemoryStream(Encoding.Latin1.GetBytes(fdf));

        var result = FdfSerializer.ImportAnnotations(doc, ms);
        var square = result.Imported.Should().ContainSingle().Subject;
        square.Subtype.Should().Be(PdfAnnotationSubtype.Square);
        square.Name.Should().Be("stream-square");
        square.Color!.Value.R.Should().BeApproximately(0.2, 0.0001);
        square.BorderWidth.Should().Be(2);
    }

    // ── #626 "remaining subtypes": round-trip position/color/author/contents/opacity ──

    [Fact]
    public void RoundTrip_NewlyAuthoredSubtypes_SurviveExportAndImport()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();

        source.AddUnderlineAnnotation(1, new PdfRectangle(72, 700, 220, 715),
            contents: "underlined", author: "Alice", red: 1, green: 0, blue: 0);
        source.AddStrikeOutAnnotation(1, new PdfRectangle(72, 650, 220, 665),
            contents: "struck", author: "Bob", red: 0.2, green: 0.2, blue: 0.2);
        source.AddSquigglyAnnotation(1, new PdfRectangle(72, 600, 220, 615),
            contents: "squiggled", author: "Carol", red: 0, green: 0, blue: 1);
        source.AddLineAnnotation(1, 72, 550, 220, 560,
            contents: "line note", author: "Dave", red: 0, green: 0.5, blue: 0, lineWidth: 2);
        source.AddArrowAnnotation(1, 72, 500, 220, 500,
            contents: "arrow note", author: "Eve", red: 1, green: 0, blue: 0,
            endLineEnding: "ClosedArrow");
        source.AddPolygonAnnotation(1,
            new (double X, double Y)[] { (72, 400), (150, 400), (110, 450) },
            contents: "polygon note", author: "Frank",
            red: 0, green: 0, blue: 0, borderWidth: 1,
            interiorRed: 1, interiorGreen: 1, interiorBlue: 0);
        source.AddPolyLineAnnotation(1,
            new (double X, double Y)[] { (200, 400), (240, 440), (280, 400) },
            contents: "polyline note", author: "Grace", red: 0.5, green: 0, blue: 0.5);
        source.AddStampAnnotation(1, new PdfRectangle(72, 300, 200, 340), "Approved",
            contents: "stamped", author: "Heidi");

        var fdf = FdfSerializer.ExportAnnotations(source);

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(target, fdf);

        result.Skipped.Should().BeEmpty("every subtype above has export+import support");
        result.Imported.Should().HaveCount(8);

        var sourceAnnotations = source.GetPage(1).GetAnnotations();
        var importedAnnotations = target.GetPage(1).GetAnnotations();
        foreach (var expected in sourceAnnotations)
        {
            // Match by /Contents, not /Subtype — Line and Arrow both carry
            // PdfAnnotationSubtype.Line (an Arrow *is* a Line with /LE), so
            // subtype alone isn't unique across this fixture's eight annotations.
            var actual = importedAnnotations.Single(a => a.Contents == expected.Contents);
            actual.Subtype.Should().Be(expected.Subtype);

            actual.Rect.Left.Should().BeApproximately(expected.Rect.Left, 0.5);
            actual.Rect.Bottom.Should().BeApproximately(expected.Rect.Bottom, 0.5);
            actual.Rect.Right.Should().BeApproximately(expected.Rect.Right, 0.5);
            actual.Rect.Top.Should().BeApproximately(expected.Rect.Top, 0.5);
            actual.Author.Should().Be(expected.Author);

            if (expected.Color is { } c)
            {
                actual.Color.Should().NotBeNull();
                actual.Color!.Value.R.Should().BeApproximately(c.R, 0.01);
                actual.Color.Value.G.Should().BeApproximately(c.G, 0.01);
                actual.Color.Value.B.Should().BeApproximately(c.B, 0.01);
            }
        }

        var stamp = importedAnnotations.Single(a => a.Subtype == PdfAnnotationSubtype.Stamp);
        stamp.IconName.Should().Be("Approved",
            "a standard #626 stamp name must round-trip and re-attach through AddStampAnnotation");
        stamp.HasAppearance.Should().BeTrue("the stamp must be re-authored (baked /AP), not a bare dictionary");

        var polygon = importedAnnotations.Single(a => a.Subtype == PdfAnnotationSubtype.Polygon);
        polygon.RawDictionary.GetArrayOrNull("IC").Should().NotBeNull("polygon interior fill must survive");

        importedAnnotations.Count(a => a.Subtype == PdfAnnotationSubtype.Line).Should().Be(2);
    }

    [Fact]
    public void RoundTrip_StampAnnotation_PreservesIconAndIsReauthored()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        source.AddStampAnnotation(1, new PdfRectangle(100, 600, 260, 650), "Confidential",
            contents: "handle with care", author: "Reviewer");

        var fdf = FdfSerializer.ExportAnnotations(source);
        fdf.Should().Contain("/Subtype /Stamp").And.Contain("/Name /Confidential");

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var imported = FdfSerializer.ImportAnnotations(target, fdf).Imported.Single();

        imported.Subtype.Should().Be(PdfAnnotationSubtype.Stamp);
        imported.IconName.Should().Be("Confidential");
        imported.Contents.Should().Be("handle with care");
        imported.Author.Should().Be("Reviewer");
        imported.HasAppearance.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_NonStandardStampIcon_KeepsDataWithoutRejecting()
    {
        const string fdf = """
            %FDF-1.2
            1 0 obj
            << /FDF << /Annots [2 0 R] >> >>
            endobj
            2 0 obj
            << /Type /Annot /Subtype /Stamp /Page 0 /Rect [10 10 110 60]
               /Name /CompanyLogo /C [0.1 0.2 0.3] /NM (custom-stamp) >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = FdfSerializer.ImportAnnotations(doc, fdf);

        result.Skipped.Should().BeEmpty();
        var stamp = result.Imported.Should().ContainSingle().Subject;
        stamp.Subtype.Should().Be(PdfAnnotationSubtype.Stamp);
        stamp.IconName.Should().Be("CompanyLogo");
        stamp.HasAppearance.Should().BeFalse("a non-standard icon has no bundled #626 artwork to bake");
    }

    [Fact]
    public void RoundTrip_Opacity_SurvivesExportAndImport()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        var annotation = source.AddSquareAnnotation(1, new PdfRectangle(10, 10, 60, 60), borderWidth: 1);
        annotation.SetAnnotationOpacity(0.4);

        var fdf = FdfSerializer.ExportAnnotations(source);
        fdf.Should().Contain("/CA 0.4");

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var imported = FdfSerializer.ImportAnnotations(target, fdf).Imported.Single();

        imported.RawDictionary.GetNumber("CA", -1).Should().BeApproximately(0.4, 0.001);
    }
}
