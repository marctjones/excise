using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Regression cover for the <c>/MediaBox</c> default (#884, 7cc3114e), which
/// shipped in v3.6.0 with no test.
///
/// A page with no <c>/MediaBox</c> anywhere in its ancestry is malformed — the
/// spec makes it a required inheritable attribute — but excise substitutes US
/// Letter rather than refusing, because pdftocairo renders all ten corpus
/// files that hit this and a page excise refuses is a page a reviewer cannot
/// redact.
///
/// The SECOND test here matters more than the first. A default that shadowed
/// real inheritance would still pass "no MediaBox → 612x792" while quietly
/// making every page US Letter, and that failure is invisible: pages would
/// render, just at the wrong size, with redaction coordinates computed against
/// a box the document never declared.
/// </summary>
public class DefaultMediaBoxTests
{
    [Fact]
    public void PageWithNoMediaBoxAnywhere_FallsBackToUsLetter()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            // No /MediaBox on the Pages node...
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            // ...and none on the page either.
            "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        page.Width.Should().Be(612, "US Letter width — the documented fallback");
        page.Height.Should().Be(792,
            "refusing the page instead would lose it entirely; pdftocairo renders all " +
            "ten corpus files that reach this");
    }

    /// <summary>
    /// The control. An inherited /MediaBox on the /Pages node must still win —
    /// otherwise the fallback has replaced inheritance rather than backstopping
    /// it, and every page in every document silently becomes US Letter.
    /// </summary>
    [Fact]
    public void InheritedMediaBox_StillWinsOverTheDefault()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        page.Width.Should().Be(300, "the /Pages node declares the box and inheritance must find it");
        page.Height.Should().Be(400);
    }

    /// <summary>A box on the page itself wins over both.</summary>
    [Fact]
    public void MediaBoxOnThePage_WinsOverAnInheritedOne()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 250 350] >>\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).Width.Should().Be(250);
        doc.GetPage(1).Height.Should().Be(350);
    }

    /// <summary>
    /// Two levels up. #881 added a cycle guard to the ancestor walk; this keeps
    /// the walk itself covered, since a guard that accidentally stops the walk
    /// would land on the default and look like the first test passing.
    /// </summary>
    [Fact]
    public void MediaBoxInheritedFromAGrandparent_IsFound()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 320 480] >>\nendobj\n",
            "3 0 obj\n<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 >>\nendobj\n",
            "4 0 obj\n<< /Type /Page /Parent 3 0 R >>\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).Width.Should().Be(320,
            "the walk must climb past an intermediate /Pages node that declares nothing — " +
            "stopping early would silently yield the US Letter default instead");
        doc.GetPage(1).Height.Should().Be(480);
    }

    private static byte[] Assemble(string[] objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
