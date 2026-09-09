using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// #1431/#1432/#1434 (one root cause) -- since #923 turned on object-stream
/// compression by default (PDF 1.5+, unencrypted, non-PDF/A-1), AcroForm
/// field/widget dictionaries and font resource dictionaries could land inside
/// a compressed <c>/ObjStm</c>, same as any other non-carrier dictionary.
/// That's spec-valid (qpdf's own decode always saw the data -- confirmed
/// while diagnosing these issues) but breaks a raw-byte scan for
/// <c>/TU</c>/<c>/Widget</c>/a font's <c>/BaseFont</c>, exactly the kind of
/// inspection <see cref="PdfDocumentWriter.ContainsDocumentCarrierText"/>
/// already exists to keep working for <c>/Title</c>/<c>/Author</c>/etc. This
/// class pins the same guarantee for the two dictionary kinds those three
/// issues actually hit.
/// </summary>
public class ObjectStreamGreppabilityTests
{
    [Fact]
    public void WidgetAndFieldDicts_StayOutOfObjectStreams()
    {
        var bytes = PdfDocumentBuilder.Create()
            .TextField("label", tooltip: "some tooltip")
            .Dropdown("choice", ["A", "B"])
            .SaveToBytes();

        var raw = Encoding.Latin1.GetString(bytes);
        raw.Should().Contain("/Widget",
            "widget annotations must stay findable by a raw byte scan, not only via a PDF parser (#1434)");
        System.Text.RegularExpressions.Regex.Matches(raw, @"/TU\s*\(").Count.Should().BeGreaterThan(0,
            "a field's /TU tooltip must stay findable by a raw byte scan (#1431)");
    }

    [Fact]
    public void FontDicts_StayOutOfObjectStreams()
    {
        var fontBytes = Fixtures.TestFontFixtures.LoadDejaVuSansBytes();
        var bytes = PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(fontBytes, 11))
            .PdfA()
            .Paragraph("Hello — café · naïve")
            .SaveToBytes();

        var raw = Encoding.Latin1.GetString(bytes);
        raw.Should().Contain("DejaVuSans",
            "an embedded font's name must stay findable by a raw byte scan, not only via a PDF parser (#1432)");
    }
}
