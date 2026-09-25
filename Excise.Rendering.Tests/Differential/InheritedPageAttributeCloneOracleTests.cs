using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1833 item 3 — a page cloned out of a nested page tree keeps the
/// attributes it inherits (§7.7.3.4). The source's /MediaBox, /Rotate and
/// /Resources (with the only reference to the font) live on the /Pages node
/// above the page. mutool, which is not our code, reads the SAVED output: the
/// page must be 300x400 on screen (400x300 rotated 90) and must still show
/// the text that needs the inherited font.
/// </summary>
public class InheritedPageAttributeCloneOracleTests : IDisposable
{
    private readonly List<string> _temp = new();

    [Theory]
    [InlineData("insert")]
    [InlineData("merge")]
    [InlineData("split")]
    public void ClonedPage_KeepsInheritedGeometryAndFont_PerMutool(string operation)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var source = PdfDocument.Open(NestedTreeWithInheritedAttributes());
        var outputs = new List<PdfDocument>();
        switch (operation)
        {
            case "insert":
                var target = PdfDocument.CreateNew();
                target.Pages.Add(source.GetPage(1));
                outputs.Add(target);
                break;
            case "merge":
                outputs.Add(PdfDocumentMerger.Merge(new[] { (source, (IReadOnlyList<int>)new[] { 0 }) }));
                break;
            default:
                outputs.AddRange(PdfDocumentSplitter.SplitToSinglePages(source));
                break;
        }

        var output = outputs.Should().ContainSingle().Subject;
        var saved = output.SaveToBytes();
        var path = WriteTemp(saved);
        output.Dispose();

        // mutool draws and extracts text with a substitute font when /F1 has no resource,
        // so the text below cannot see a lost /Resources; the font dictionary only reaches
        // the output if the clone carried it.
        Encoding.Latin1.GetString(saved).Should().Contain("/BaseFont /Helvetica",
            "the font dictionary was reachable only through the ancestor's /Resources");

        using var bitmap = MutoolReferenceRenderer.RenderPage(path, 1, dpi: 72);
        bitmap.Should().NotBeNull();
        (bitmap!.Width, bitmap.Height).Should().Be((300, 400),
            "the inherited /MediaBox [0 0 400 300] and /Rotate 90 must survive the clone");

        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("INHERITEDFONT");
    }

    private static byte[] NestedTreeWithInheritedAttributes()
    {
        const string content = "BT /F1 24 Tf 50 100 Td (INHERITEDFONT) Tj ET";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 /MediaBox [0 0 400 300] /Rotate 90 " +
            "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Page /Parent 3 0 R /Contents 6 0 R >>\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-inherit-clone-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
