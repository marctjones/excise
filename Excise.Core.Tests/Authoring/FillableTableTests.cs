using System.Linq;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Authoring;

/// <summary>
/// Tests for <see cref="PdfDocumentBuilder.FillableTable"/> — a table whose body
/// cells are interactive AcroForm fields.
/// </summary>
public class FillableTableTests
{
    private static byte[] SampleFillableTable(PdfDocumentBuilder builder)
    {
        var rows = new[]
        {
            new FillableTableRow("Alice", new[]
            {
                new FillableTableCell("alice_email", FillableCellKind.Text, Value: "a@x.com"),
                new FillableTableCell("alice_ok", FillableCellKind.CheckBox),
            }),
            new FillableTableRow("Bob", new[]
            {
                new FillableTableCell("bob_email", FillableCellKind.Text),
                new FillableTableCell("bob_tier", FillableCellKind.Choice,
                    Options: new[] { "Basic", "Pro" }, Tooltip: "Bob's tier"),
            }),
        };
        return builder.FillableTable(new[] { "Email", "Flag" }, rows).SaveToBytes();
    }

    [Fact]
    public void FillableTable_CreatesLiveFieldsPerCellWithCorrectTypes()
    {
        var pdf = SampleFillableTable(PdfDocumentBuilder.Create());

        using var doc = PdfDocument.Open(pdf);
        var form = doc.GetAcroForm()!;
        var names = form.Fields.Select(f => f.FullName).ToList();
        names.Should().Contain(new[] { "alice_email", "alice_ok", "bob_email", "bob_tier" });

        form.FindField("alice_email")!.FieldType.Should().Be(PdfFieldType.Text);
        form.FindField("alice_ok")!.FieldType.Should().Be(PdfFieldType.Button);
        form.FindField("bob_tier")!.FieldType.Should().Be(PdfFieldType.Choice);
    }

    [Fact]
    public void FillableTable_RendersHeadersAndRowLabels()
    {
        var pdf = SampleFillableTable(PdfDocumentBuilder.Create());
        using var doc = PdfDocument.Open(pdf);
        var text = string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
        foreach (var s in new[] { "Email", "Flag", "Alice", "Bob" })
            text.Should().Contain(s);
    }

    [Fact]
    public void FillableTable_WorksUnderTaggingAndRoundTrips()
    {
        var pdf = SampleFillableTable(PdfDocumentBuilder.Create().Tagged().Language("en-US"));
        var act = () => PdfDocument.Open(pdf).Dispose();
        act.Should().NotThrow();

        using var doc = PdfDocument.Open(pdf);
        doc.GetAcroForm()!.Fields.Should().NotBeEmpty();
        doc.Catalog.GetOptional("StructTreeRoot").Should().NotBeNull();
    }
}
