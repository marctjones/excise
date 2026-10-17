using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #2075: drawing several covering boxes on one page parses and rewrites its content stream once, not once per
/// box (a 6-page form with hundreds of boxes spent 14% of its redaction time re-parsing). The batch must leave
/// the page exactly as the per-box calls do, and the writer's per-call memo of the "copy this operator verbatim"
/// check must not change which operators are copied (that check is the #1093 fail-closed guard).
/// </summary>
public class BlackRectangleBatchTests
{
    private static readonly PdfRectangle[] Boxes =
    {
        new PdfRectangle(100, 700, 160, 712),
        new PdfRectangle(100, 650, 190, 662),
        new PdfRectangle(300, 400, 340, 415),
    };

    private const string Content = "% comment\r\nq\r\n0.500 0.250 0.1250 rg\r\n/Span << /ActualText (SECRETWORD) >> BDC\r\nBT\r\n /F1 12.00 Tf\r\n 72 720 Td\r\n (Hello \\(world\\)) Tj\r\nET\r\nEMC\r\nQ\r\n";

    private static PdfDocument Open()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(sb.ToString()));
    }

    private static byte[] PageBytes(PdfDocument doc) => doc.GetPage(1).GetContentStreamBytes();

    [Fact]
    public void ABatchOfBoxes_LeavesThePageExactlyAsOneBoxPerCallDoes()
    {
        var sequential = Open();
        foreach (var box in Boxes)
            PdfDocumentRedactionExtensions.AppendBlackRectangles(sequential.GetPage(1), new[] { box });

        var batched = Open();
        PdfDocumentRedactionExtensions.AppendBlackRectangles(batched.GetPage(1), Boxes);

        PageBytes(batched).Should().Equal(PageBytes(sequential));
        Encoding.Latin1.GetString(PageBytes(batched)).Should().Contain("% comment", "untouched source bytes stay verbatim (#1093)");
    }

    [Fact]
    public void ABatchOfNoBoxes_ChangesNothing()
    {
        var doc = Open();
        var before = PageBytes(doc);

        PdfDocumentRedactionExtensions.AppendBlackRectangles(doc.GetPage(1), Array.Empty<PdfRectangle>());

        PageBytes(doc).Should().Equal(before);
    }

    [Fact]
    public void AnOperatorMutatedAfterParsing_IsStillReserialised_NotCopiedFromItsSourceBytes()
    {
        // The guard the per-write memo must preserve: change an operand in place and the writer may not
        // copy the operator's original bytes back (the scrubbed /ActualText leak, #636/#1093).
        var doc = Open();
        var page = doc.GetPage(1);
        var content = page.GetContentStream(trackSourceSpans: true);
        var bdc = content.Operators.Single(o => o.Name == "BDC");
        ((PdfDictionary)bdc.Operands[1]).Remove("ActualText");

        var written = new ContentStreamWriter().Write(content, content.SourceBytes!);
        var text = Encoding.Latin1.GetString(written);

        text.Should().NotContain("SECRETWORD", "the scrubbed carrier must not come back from the original bytes");
        text.Should().Contain("Hello", "the untouched text operator is kept");
        text.Should().Contain("% comment", "the operators before it are still copied verbatim");
        Encoding.Latin1.GetString(PageBytes(Open())).Should().Contain("SECRETWORD", "the fixture does carry the carrier");
    }
}
