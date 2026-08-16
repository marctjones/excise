using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Flatten must STAMP a pushbutton's /AP /N appearance into the page, not
/// erase it (#962): a pushbutton has no /V to draw — its entire visible
/// content (e.g. a "Clear Form" label) lives in the widget's appearance
/// stream, and removing the widget without stamping destroyed visible ink.
/// The corpus-backed conservation gate lives in Excise.Rendering.Tests
/// (ConservationGateTests.FormFlatten, mutool oracle on ds11/ds82); these
/// are the corpus-free unit pins on a synthetic form.
/// </summary>
public class AcroFormFlattenerPushButtonTests
{
    private const int PushButtonFlag = 0x10000;

    /// <summary>
    /// One page, one pushbutton field whose widget has an /AP /N form
    /// XObject (BBox 0..10) placed at /Rect [100 100 150 150] — so the
    /// §12.5.5 mapping is scale 5 + translate (100,100).
    /// </summary>
    private static byte[] BuildPushButtonPdf(string bbox = "[0 0 10 10]", string? apEntry = "5 0 R", string? matrix = null)
    {
        var appearanceBody = "0 0 10 10 re f\n";
        var matrixEntry = matrix == null ? "" : $" /Matrix {matrix}";
        var objects = new List<(int objNum, string content)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots [4 0 R] >>"),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (clear) " +
                $"/Ff {PushButtonFlag} /Rect [100 100 150 150] " +
                (apEntry == null ? ">>" : $"/AP << /N {apEntry} >> >>")),
            (5, $"<< /Type /XObject /Subtype /Form /BBox {bbox}{matrixEntry} /Length {appearanceBody.Length} >>\nstream\n{appearanceBody}endstream"),
        };
        AddContentStream(objects, 6, "% base-page-content\n");
        return BuildPdfBytes(objects);
    }

    [Fact]
    public void Flatten_StampsThePushButtonAppearanceIntoThePage()
    {
        using var doc = PdfDocument.Open(BuildPushButtonPdf());
        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        doc.FlattenAcroForm();

        var page = doc.GetPage(1);
        var content = Encoding.ASCII.GetString(page.GetContentStreamBytes());

        content.Should().Contain("% base-page-content", "flatten appends; it must not destroy the page");
        content.Should().Contain("/FlatAP0 Do", "the appearance must be stamped as a form XObject");
        // §12.5.5: BBox [0 0 10 10] onto Rect [100 100 150 150] = scale 5,
        // translate (100,100).
        content.Should().Contain("5 0 0 5 100 100 cm");

        page.GetAnnotations().Should().BeEmpty("the widget itself is removed after stamping");
        page.Resources!.GetDictionaryOrNull("XObject")!.GetOptional("FlatAP0")
            .Should().NotBeNull("the appearance stream is shared into the page resources");
    }

    [Fact]
    public void Flatten_StampSurvivesSaveAndReopen()
    {
        using var doc = PdfDocument.Open(BuildPushButtonPdf());
        doc.FlattenAcroForm();

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var content = Encoding.ASCII.GetString(reopened.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("/FlatAP0 Do");
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty();
    }

    [Fact]
    public void Flatten_DegenerateBBox_StampsNothingAndDoesNotThrow()
    {
        using var doc = PdfDocument.Open(BuildPushButtonPdf(bbox: "[0 0 0 0]"));

        doc.FlattenAcroForm();

        var content = Encoding.ASCII.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().NotContain("Do\n", "a zero-area appearance cannot be mapped onto the rect");
        doc.GetPage(1).GetAnnotations().Should().BeEmpty("the widget is still removed");
    }

    [Fact]
    public void Flatten_MissingAppearance_RemovesTheWidgetWithoutStamping()
    {
        using var doc = PdfDocument.Open(BuildPushButtonPdf(apEntry: null));

        doc.FlattenAcroForm();

        var content = Encoding.ASCII.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().NotContain("FlatAP");
        doc.GetPage(1).GetAnnotations().Should().BeEmpty();
    }

    [Fact]
    public void Flatten_AppearanceMatrix_ComposesIntoTheMapping()
    {
        // /Matrix [2 0 0 2 0 0] doubles the BBox before the rect mapping:
        // transformed bounds 0..20 onto a 50-wide rect = scale 2.5.
        using var doc = PdfDocument.Open(BuildPushButtonPdf(matrix: "[2 0 0 2 0 0]"));

        doc.FlattenAcroForm();

        var content = Encoding.ASCII.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("2.5 0 0 2.5 100 100 cm");
    }

    // ------------------------------------------------------------- plumbing

    private static void AddContentStream(List<(int objNum, string content)> objects, int objNum, string content)
    {
        objects.Add((objNum, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"));
    }

    private static byte[] BuildPdfBytes(List<(int objNum, string content)> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var sorted = objects.OrderBy(x => x.objNum).ToList();
        var offsets = new Dictionary<int, long>();
        foreach (var (objNum, content) in sorted)
        {
            offsets[objNum] = Encoding.UTF8.GetByteCount(sb.ToString());
            sb.Append($"{objNum} 0 obj\n{content}\nendobj\n");
        }

        long xrefPos = Encoding.UTF8.GetByteCount(sb.ToString());
        var maxObj = sorted.Max(x => x.objNum);
        sb.Append("xref\n");
        sb.Append($"0 {maxObj + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= maxObj; i++)
        {
            sb.Append(offsets.TryGetValue(i, out var offset)
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 00000 f \n");
        }
        sb.Append($"trailer\n<< /Size {maxObj + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
