using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1833 item 4: <see cref="PdfStructElement.PageNumber"/> names the page the
/// element's <c>/Pg</c>, or its marked-content reference's <c>/Pg</c>, points
/// at, numbered as <see cref="PdfDocument.Pages"/> numbers it. The page tree's
/// second leaf is <c>/Type /Template</c>, which the page list and MuPDF both
/// count as page 2. Each element's <c>/Alt</c> is the word its page draws, so
/// MuPDF confirms the numbering without excise reading the pages.
/// </summary>
public class PdfStructTreePageNumberTests
{
    [Fact]
    public void Fixture_IndependentReaderShowsEachWordOnItsPage()
    {
        Assert.SkipUnless(MutoolStextOracle.IsAvailable, "mutool not on PATH");
        var pdf = TaggedPdf();
        string[] words = { "ALPHA", "BRAVO", "CHARLIE" };
        for (int page = 1; page <= words.Length; page++)
        {
            MutoolStextOracle.Words(pdf, page, 792).Select(w => w.Text)
                .Should().Contain(words[page - 1], $"MuPDF draws {words[page - 1]} on page {page}");
        }
    }

    [Fact]
    public void PageNumber_IsThePageItsPgOrMarkedContentReferenceNames()
    {
        using var doc = PdfDocument.Open(TaggedPdf());
        var root = doc.GetStructureTree()!;
        var byAlt = Descendants(root).Where(e => e.AltText != null).ToDictionary(e => e.AltText!);

        root.PageNumber.Should().BeNull("the /Document element has no /Pg and no marked-content reference");
        byAlt["ALPHA"].PageNumber.Should().Be(1, "its /Pg is the first leaf");
        byAlt["BRAVO"].PageNumber.Should().Be(2, "its /Pg is the /Template leaf the page list counts as page 2");
        byAlt["BRAVO-CHILD"].PageNumber.Should().Be(2, "an element without /Pg takes its parent's page");
        byAlt["CHARLIE"].PageNumber.Should().Be(3, "its only page is the /Pg of its /MCR kid");
        byAlt["NOPAGE"].PageNumber.Should().BeNull("its /Pg names a content stream, not a page");
    }

    [Fact]
    public void MarkedContent_CarriesTheSpecPageOfEachReference()
    {
        using var doc = PdfDocument.Open(TaggedPdf());
        var byAlt = Descendants(doc.GetStructureTree()!).Where(e => e.AltText != null).ToDictionary(e => e.AltText!);

        byAlt["ALPHA"].MarkedContent.Should().Equal(new PdfMarkedContentReference(0, 1));
        byAlt["CHARLIE"].MarkedContent.Should().Equal(new PdfMarkedContentReference(0, 3));
        byAlt["UNTYPED-MCR"].MarkedContent.Should().Equal(new[] { new PdfMarkedContentReference(0, 3) },
            "a /K dictionary with an /MCID and no /S is a marked-content reference even without /Type /MCR");
        byAlt["BRAVO-CHILD"].MarkedContent.Should().Equal(new[] { new PdfMarkedContentReference(0, null) },
            "an integer MCID's page is its own element's /Pg (ISO 32000-2 Table 355), which it lacks");
        doc.ResolveStructElementText(byAlt["CHARLIE"]).Should().Be("CHARLIE");
        doc.ResolveStructElementText(byAlt["BRAVO-CHILD"]).Should().Be("BRAVO",
            "text resolution falls back to the element's page");
    }

    [Fact]
    public void RoleMappedType_FollowsTheRoleMapToAStandardType()
    {
        using var doc = PdfDocument.Open(TaggedPdf());
        var bravo = Descendants(doc.GetStructureTree()!).Single(e => e.AltText == "BRAVO");

        bravo.Type.Should().Be("/Chapter");
        bravo.RoleMappedType.Should().Be("/Sect");
    }

    [Fact]
    public void CyclicK_ParsesEachElementOnce()
    {
        using var doc = PdfDocument.Open(TaggedPdf(cyclic: true));
        var root = doc.GetStructureTree()!;

        Descendants(root).Count(e => e.AltText == "LOOP").Should().Be(1,
            "an element whose /K names itself is parsed once, not until the stack overflows");
    }

    private static IEnumerable<PdfStructElement> Descendants(PdfStructElement element) =>
        element.Children.SelectMany(Descendants).Prepend(element);

    /// <summary>
    /// Objects 3, 4 and 5 are the page tree's leaves (4 is <c>/Type /Template</c>).
    /// Every page draws its word inside <c>/P &lt;&lt;/MCID 0&gt;&gt; BDC</c>.
    /// </summary>
    private static byte[] TaggedPdf(bool cyclic = false)
    {
        const string font = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        static string Content(string text)
        {
            var ops = $"/P <</MCID 0>> BDC BT /F1 24 Tf 72 700 Td ({text}) Tj ET EMC";
            return $"<< /Length {ops.Length} >>\nstream\n{ops}\nendstream";
        }

        var kids = cyclic ? "[11 0 R 12 0 R 13 0 R 14 0 R 17 0 R 16 0 R]" : "[11 0 R 12 0 R 13 0 R 14 0 R 17 0 R]";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 9 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 6 0 R >>",
            $"<< /Type /Template /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 7 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 8 0 R >>",
            Content("ALPHA"),
            Content("BRAVO"),
            Content("CHARLIE"),
            "<< /Type /StructTreeRoot /K 10 0 R /RoleMap << /Chapter /Sect >> >>",
            $"<< /Type /StructElem /S /Document /P 9 0 R /K {kids} >>",
            "<< /Type /StructElem /S /P /P 10 0 R /Pg 3 0 R /Alt (ALPHA) /K 0 >>",
            "<< /Type /StructElem /S /Chapter /P 10 0 R /Pg 4 0 R /Alt (BRAVO) /K 15 0 R >>",
            "<< /Type /StructElem /S /P /P 10 0 R /Alt (CHARLIE) /K << /Type /MCR /Pg 5 0 R /MCID 0 >> >>",
            "<< /Type /StructElem /S /Figure /P 10 0 R /Pg 6 0 R /Alt (NOPAGE) >>",
            "<< /Type /StructElem /S /Span /P 12 0 R /Alt (BRAVO-CHILD) /K 0 >>",
            "<< /Type /StructElem /S /Div /P 10 0 R /Alt (LOOP) /K [16 0 R 16 0 R] >>",
            "<< /Type /StructElem /S /P /P 10 0 R /Alt (UNTYPED-MCR) /K [<< /Pg 5 0 R /MCID 0 >>] >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
