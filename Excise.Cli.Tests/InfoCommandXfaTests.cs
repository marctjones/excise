using System.Text;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1547 — <c>excise info</c> reports whether a document is an XFA form, so a
/// user (or a script) learns why excise shows a dynamic form's placeholder page.
/// </summary>
public sealed class InfoCommandXfaTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"excise-info-xfa-{Guid.NewGuid():N}");

    public InfoCommandXfaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteXfaPdf(bool needsRendering)
    {
        const string xdp = "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template/></xdp:xdp>";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R" + (needsRendering ? " /NeedsRendering true" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + (needsRendering ? "" : " /Annots [6 0 R]") + " >>",
            needsRendering ? "<< /Fields [] /XFA 5 0 R >>" : "<< /Fields [6 0 R] /XFA 5 0 R >>",
            $"<< /Length {xdp.Length} >>\nstream\n{xdp}\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 700 300 720] /P 3 0 R >>",
        ];

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(_dir, needsRendering ? "dynamic.pdf" : "static.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    [Theory]
    [InlineData(true, "dynamic")]
    [InlineData(false, "static")]
    public void Info_ReportsTheXfaFormKind(bool needsRendering, string expected)
    {
        var result = InfoCommandHandler.Execute(
            new DocumentInfoRequest(WriteXfaPdf(needsRendering), null),
            TestContext.Current.CancellationToken);

        result.XfaForm.Should().Be(expected);
    }
}
