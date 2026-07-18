using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Comprehensive tests for <see cref="PdfAcroFormParser"/> covering field tree parsing,
/// widget extraction, field types, flags, options parsing, and edge cases.
/// </summary>
public class PdfAcroFormParserTests
{
    // ─── PDF builders ───────────────────────────────────────────────────────────

    /// <summary>Build a minimal PDF with an AcroForm dict containing the given definition.</summary>
    private static byte[] MakePdfWithAcroForm(string acroFormDef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($"<< /Type /Catalog /Pages 2 0 R {acroFormDef} >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ─── Test: basic parsing ────────────────────────────────────────────────

    [Fact]
    public void Parse_NullAcroForm_ReturnsEmptyFields()
    {
        var pdf = MakePdfWithAcroForm("");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var catalog = doc.Catalog;

        var result = PdfAcroFormParser.Parse(doc, catalog);

        result.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyFieldsArray_ReturnsEmpty()
    {
        var pdf = MakePdfWithAcroForm("/AcroForm << /Fields [] >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NeedsAppearancesTrue_Captured()
    {
        var pdf = MakePdfWithAcroForm("/AcroForm << /Fields [] /NeedsAppearances true >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.NeedsAppearances.Should().BeTrue();
    }

    [Fact]
    public void Parse_NeedsAppearancesFalse_Captured()
    {
        var pdf = MakePdfWithAcroForm("/AcroForm << /Fields [] /NeedsAppearances false >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.NeedsAppearances.Should().BeFalse();
    }

    // ─── Test: field types ──────────────────────────────────────────────────

    [Fact]
    public void Parse_TextFieldType_Recognized()
    {
        var sb = BuildPdfWithSingleField("Tx", "TestField");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields.Should().HaveCount(1);
        result.Fields[0].FieldType.Should().Be(PdfFieldType.Text);
    }

    [Fact]
    public void Parse_ButtonFieldType_Recognized()
    {
        var sb = BuildPdfWithSingleField("Btn", "ButtonField");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].FieldType.Should().Be(PdfFieldType.Button);
    }

    [Fact]
    public void Parse_ChoiceFieldType_Recognized()
    {
        var sb = BuildPdfWithSingleField("Ch", "ChoiceField");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].FieldType.Should().Be(PdfFieldType.Choice);
    }

    [Fact]
    public void Parse_SignatureFieldType_Recognized()
    {
        var sb = BuildPdfWithSingleField("Sig", "SignatureField");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].FieldType.Should().Be(PdfFieldType.Signature);
    }

    [Fact]
    public void Parse_UnknownFieldType_RecognizedAsUnknown()
    {
        var sb = BuildPdfWithSingleField("Unknown", "UnknownField");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].FieldType.Should().Be(PdfFieldType.Unknown);
    }

    // ─── Test: field values ─────────────────────────────────────────────────

    [Fact]
    public void Parse_FieldWithStringValue_Captured()
    {
        var sb = BuildPdfWithSingleFieldAndValue("Tx", "Name", "Alice");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Value.Should().Be("Alice");
    }

    [Fact]
    public void Parse_FieldWithNameValue_Captured()
    {
        var sb = BuildPdfWithNameValue("Btn", "RadioButton", "/Yes");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Value.Should().Be("Yes");
    }

    [Fact]
    public void Parse_FieldWithDefaultValue_Captured()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (Email)
            /V (current@example.com)
            /DV (default@example.com)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Value.Should().Be("current@example.com");
        result.Fields[0].DefaultValue.Should().Be("default@example.com");
    }

    // ─── Test: choice field options ─────────────────────────────────────────

    [Fact]
    public void Parse_ChoiceFieldWithOptions_AllOptionsCaptured()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Ch
            /T (Country)
            /V (USA)
            /Opt [(USA) (Canada) (Mexico)]
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Options.Should().NotBeNull();
        result.Fields[0].Options!.Should().HaveCount(3);
        result.Fields[0].Options!.Should().ContainInOrder("USA", "Canada", "Mexico");
    }

    [Fact]
    public void Parse_ChoiceFieldWithNameOptions_Parsed()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Ch
            /T (Status)
            /Opt [/Active /Inactive /Pending]
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Options.Should().HaveCount(3);
        result.Fields[0].Options.Should().Contain("Active");
    }

    [Fact]
    public void Parse_ChoiceFieldWithPairs_DisplayValueUsed()
    {
        // Opt format: [(exportValue1, displayValue1) (exportValue2, displayValue2)]
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Ch
            /T (PairedOptions)
            /Opt [[(export1) (Display 1)] [(export2) (Display 2)]]
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Options.Should().HaveCount(2);
        result.Fields[0].Options.Should().ContainInOrder("Display 1", "Display 2");
    }

    // ─── Test: field flags ──────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadOnlyFlag_Recognized()
    {
        var sb = BuildPdfWithFieldFlags("Tx", "Field", 0x1);
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].IsReadOnly.Should().BeTrue();
        result.Fields[0].IsRequired.Should().BeFalse();
    }

    [Fact]
    public void Parse_RequiredFlag_Recognized()
    {
        var sb = BuildPdfWithFieldFlags("Tx", "Field", 0x2);
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].IsRequired.Should().BeTrue();
        result.Fields[0].IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Parse_MultilineFlag_Recognized()
    {
        var sb = BuildPdfWithFieldFlags("Tx", "Field", 0x1000);
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].IsMultiline.Should().BeTrue();
    }

    [Fact]
    public void Parse_MultipleFlags_AllRecognized()
    {
        var sb = BuildPdfWithFieldFlags("Tx", "Field", 0x1 | 0x2 | 0x1000);
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].IsReadOnly.Should().BeTrue();
        result.Fields[0].IsRequired.Should().BeTrue();
        result.Fields[0].IsMultiline.Should().BeTrue();
    }

    // ─── Test: widget rectangle ─────────────────────────────────────────────

    [Fact]
    public void Parse_WidgetRect_Captured()
    {
        var sb = BuildPdfWithSingleFieldAndValue("Tx", "Name", "Alice");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        var rect = result.Fields[0].Rect;
        rect.Should().NotBeNull();
        rect!.Value.Left.Should().Be(72);
        rect!.Value.Bottom.Should().Be(700);
        rect!.Value.Right.Should().Be(300);
        rect!.Value.Top.Should().Be(720);
    }

    [Fact]
    public void Parse_WidgetRectInvalid_NoRect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (Field)
            /Rect [1 2]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Rect.Should().BeNull();
    }

    // ─── Test: field tree hierarchy ─────────────────────────────────────────

    [Fact]
    public void Parse_FieldHierarchy_FullNameGenerated()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long parentFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /T (Parent)
            /FT /Tx
            /Kids [6 0 R]
        >>");
        sb.AppendLine("endobj");

        long childFieldPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /T (Child)
            /V (ChildValue)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{parentFieldPos:D10} 00000 n ");
        sb.AppendLine($"{childFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields.Should().HaveCount(1);
        result.Fields[0].FullName.Should().Be("Parent.Child");
        result.Fields[0].PartialName.Should().Be("Child");
    }

    [Fact]
    public void Parse_DeepFieldHierarchy_FullNameCorrect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long level1Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<< /T (Level1) /FT /Tx /Kids [6 0 R] >>");
        sb.AppendLine("endobj");

        long level2Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<< /T (Level2) /FT /Tx /Kids [7 0 R] >>");
        sb.AppendLine("endobj");

        long level3Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /T (Level3)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 8");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{level1Pos:D10} 00000 n ");
        sb.AppendLine($"{level2Pos:D10} 00000 n ");
        sb.AppendLine($"{level3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 8 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].FullName.Should().Be("Level1.Level2.Level3");
    }

    [Fact]
    public void Parse_FieldWithoutName_Skipped()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields.Should().BeEmpty();
    }

    // ─── Test: widget kids ──────────────────────────────────────────────────

    [Fact]
    public void Parse_FieldWithWidgetKids_RectFromWidget()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /T (Field)
            /FT /Tx
            /Kids [6 0 R]
        >>");
        sb.AppendLine("endobj");

        long widgetPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /Rect [100 200 300 250]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine($"{widgetPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as PdfDictionary
            ?? throw new InvalidOperationException();

        var result = PdfAcroFormParser.Parse(doc, acroFormDict);

        result.Fields[0].Rect!.Value.Left.Should().Be(100);
        result.Fields[0].Rect!.Value.Bottom.Should().Be(200);
        result.Fields[0].Rect!.Value.Right.Should().Be(300);
        result.Fields[0].Rect!.Value.Top.Should().Be(250);
    }

    // ─── Helper methods ─────────────────────────────────────────────────────

    private StringBuilder BuildPdfWithSingleField(string fieldType, string fieldName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine($@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /{fieldType}
            /T ({fieldName})
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return sb;
    }

    private StringBuilder BuildPdfWithSingleFieldAndValue(string fieldType, string fieldName, string value)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine($@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /{fieldType}
            /T ({fieldName})
            /V ({value})
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return sb;
    }

    private StringBuilder BuildPdfWithNameValue(string fieldType, string fieldName, string nameValue)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine($@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /{fieldType}
            /T ({fieldName})
            /V {nameValue}
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return sb;
    }

    private StringBuilder BuildPdfWithFieldFlags(string fieldType, string fieldName, int flags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine($@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /{fieldType}
            /T ({fieldName})
            /Ff {flags}
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return sb;
    }
}
