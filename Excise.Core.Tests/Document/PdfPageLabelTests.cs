using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Comprehensive tests for page label parsing and formatting.
/// Tests cover style enum, PdfPageLabel record formatting, and PdfPageLabelParser integration.
/// </summary>
public class PdfPageLabelTests
{
    // ─── PdfPageLabel formatting tests ───────────────────────────────────────

    [Fact]
    public void PdfPageLabel_Decimal_FormatsCorrectly()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.Decimal, 1);
        label.Format(0).Should().Be("1");
        label.Format(1).Should().Be("2");
        label.Format(9).Should().Be("10");
    }

    [Fact]
    public void PdfPageLabel_DecimalWithPrefix_IncludesPrefix()
    {
        var label = new PdfPageLabel("Chapter-", PdfPageLabelStyle.Decimal, 1);
        label.Format(0).Should().Be("Chapter-1");
        label.Format(5).Should().Be("Chapter-6");
    }

    [Fact]
    public void PdfPageLabel_DecimalWithStartNumber_StartsAtCorrectNumber()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.Decimal, 5);
        label.Format(0).Should().Be("5");
        label.Format(1).Should().Be("6");
        label.Format(10).Should().Be("15");
    }

    [Fact]
    public void PdfPageLabel_UppercaseRoman_FormatsCorrectly()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.UppercaseRoman, 1);
        label.Format(0).Should().Be("I");
        label.Format(1).Should().Be("II");
        label.Format(2).Should().Be("III");
        label.Format(3).Should().Be("IV");
        label.Format(4).Should().Be("V");
        label.Format(8).Should().Be("IX");
        label.Format(9).Should().Be("X");
    }

    [Fact]
    public void PdfPageLabel_LowercaseRoman_FormatsCorrectly()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.LowercaseRoman, 1);
        label.Format(0).Should().Be("i");
        label.Format(1).Should().Be("ii");
        label.Format(4).Should().Be("v");
        label.Format(9).Should().Be("x");
    }

    [Fact]
    public void PdfPageLabel_UppercaseLetters_FormatsCorrectly()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.UppercaseLetters, 1);
        label.Format(0).Should().Be("A");
        label.Format(1).Should().Be("B");
        label.Format(25).Should().Be("Z");
        label.Format(26).Should().Be("AA");
        label.Format(27).Should().Be("AB");
        label.Format(51).Should().Be("AZ");
        label.Format(52).Should().Be("BA");
    }

    [Fact]
    public void PdfPageLabel_LowercaseLetters_FormatsCorrectly()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.LowercaseLetters, 1);
        label.Format(0).Should().Be("a");
        label.Format(25).Should().Be("z");
        label.Format(26).Should().Be("aa");
        label.Format(27).Should().Be("ab");
    }

    [Fact]
    public void PdfPageLabel_NoneStyle_ReturnsPrefix()
    {
        var label = new PdfPageLabel("Appendix-", PdfPageLabelStyle.None, 1);
        label.Format(0).Should().Be("Appendix-");
        label.Format(5).Should().Be("Appendix-");  // No numeric suffix
    }

    [Fact]
    public void PdfPageLabel_NoneStyleNoPrefix_ReturnsEmpty()
    {
        var label = new PdfPageLabel(null, PdfPageLabelStyle.None, 1);
        label.Format(0).Should().Be(string.Empty);
        label.Format(5).Should().Be(string.Empty);
    }

    // ─── PDF integration tests ───────────────────────────────────────────────

    [Fact]
    public void GetPageLabel_NoPageLabels_ReturnsNull()
    {
        var pdf = MakePdfWithPageLabels(null);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().BeNull();
        doc.GetPageLabel(2).Should().BeNull();
        doc.GetPageLabel(3).Should().BeNull();
    }

    [Fact]
    public void GetPageLabel_SimpleDecimal_ReturnsFormattedLabels()
    {
        // All pages use decimal labels starting at 1
        var pdf = MakePdfWithPageLabels("/PageLabels << /Nums [0 << /S /D >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("1");
        doc.GetPageLabel(2).Should().Be("2");
        doc.GetPageLabel(3).Should().Be("3");
    }

    [Fact]
    public void GetPageLabel_RomanThenDecimal_ReturnsCorrectSequence()
    {
        // Pages 0-1 (1-2) use lowercase roman, page 2+ (3+) uses decimal
        var pdf = MakePdfWithPageLabels(
            "/PageLabels << /Nums [0 << /S /r >> 2 << /S /D >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("i");
        doc.GetPageLabel(2).Should().Be("ii");
        doc.GetPageLabel(3).Should().Be("1");  // Switches to decimal starting at page 3
    }

    [Fact]
    public void GetPageLabel_WithPrefix_IncludesPrefixInLabel()
    {
        var pdf = MakePdfWithPageLabels(
            "/PageLabels << /Nums [0 << /S /D /P (Chapter ) >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("Chapter 1");
        doc.GetPageLabel(2).Should().Be("Chapter 2");
        doc.GetPageLabel(3).Should().Be("Chapter 3");
    }

    [Fact]
    public void GetPageLabel_WithStartNumber_StartsAtSpecifiedNumber()
    {
        var pdf = MakePdfWithPageLabels(
            "/PageLabels << /Nums [0 << /S /D /St 10 >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("10");
        doc.GetPageLabel(2).Should().Be("11");
        doc.GetPageLabel(3).Should().Be("12");
    }

    [Fact]
    public void GetPageLabel_PrefixOnly_ReturnsPrefixLiteral()
    {
        var pdf = MakePdfWithPageLabels(
            "/PageLabels << /Nums [0 << /P (Appendix) >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("Appendix");
        doc.GetPageLabel(2).Should().Be("Appendix");
        doc.GetPageLabel(3).Should().Be("Appendix");
    }

    [Fact]
    public void GetPageLabel_MixedPrefixAndStyle_CombinesBoth()
    {
        // Prefix + style combination
        var pdf = MakePdfWithPageLabels(
            "/PageLabels << /Nums [0 << /S /A /P (A-) >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("A-A");
        doc.GetPageLabel(2).Should().Be("A-B");
        doc.GetPageLabel(3).Should().Be("A-C");
    }

    [Fact]
    public void GetPageLabel_InvalidPageNumber_ReturnsNull()
    {
        var pdf = MakePdfWithPageLabels("/PageLabels << /Nums [0 << /S /D >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(0).Should().BeNull();    // Page 0 doesn't exist
        doc.GetPageLabel(4).Should().BeNull();    // PDF only has 3 pages
        doc.GetPageLabel(-1).Should().BeNull();   // Negative page number
    }

    [Fact]
    public void GetPageLabel_MultipleRanges_AppliesCorrectRange()
    {
        // Pages 0-1: lowercase roman, pages 2+: decimal starting at 1
        var pdf = MakePdfWithPageLabels(
            "/PageLabels << /Nums [0 << /S /r >> 2 << /S /D >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("i");
        doc.GetPageLabel(2).Should().Be("ii");
        doc.GetPageLabel(3).Should().Be("1");  // Switches to decimal at page 3
    }

    [Fact]
    public void GetPageLabel_UppercaseRoman_FormatsAsExpected()
    {
        var pdf = MakePdfWithPageLabels("/PageLabels << /Nums [0 << /S /R >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("I");
        doc.GetPageLabel(2).Should().Be("II");
        doc.GetPageLabel(3).Should().Be("III");
    }

    [Fact]
    public void GetPageLabel_UppercaseLetters_FormatsAsExpected()
    {
        var pdf = MakePdfWithPageLabels("/PageLabels << /Nums [0 << /S /A >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("A");
        doc.GetPageLabel(2).Should().Be("B");
        doc.GetPageLabel(3).Should().Be("C");
    }

    [Fact]
    public void GetPageLabel_LowercaseLetters_FormatsAsExpected()
    {
        var pdf = MakePdfWithPageLabels("/PageLabels << /Nums [0 << /S /a >>] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetPageLabel(1).Should().Be("a");
        doc.GetPageLabel(2).Should().Be("b");
        doc.GetPageLabel(3).Should().Be("c");
    }

    /// <summary>
    /// Page label with /St as PdfReal (e.g., /St 5.0) instead of PdfInteger.
    /// Covers TryGetInteger branch: PdfReal conversion (lines ~109-112).
    /// </summary>
    [Fact]
    public void GetPageLabel_StartNumberAsReal_ConvertedToInteger()
    {
        var pdf = MakePdfWithPageLabels("/PageLabels << /Nums [0 << /S /D /St 7.5 >>] >>");
        using var doc = PdfDocument.Open(pdf);

        // 7.5 should be cast to 7 (truncated)
        doc.GetPageLabel(1).Should().Be("7");
        doc.GetPageLabel(2).Should().Be("8");
        doc.GetPageLabel(3).Should().Be("9");
    }

    /// <summary>
    /// Number tree with /Kids array spanning multiple subtrees.
    /// Covers WalkNumberTree recursion: /Kids branch (lines ~62-70).
    /// </summary>
    [Fact]
    public void GetPageLabel_NumberTreeWithKidsSubtrees_WalksAllSubtrees()
    {
        var pdf = MakePdfWithNumberTreeSubtrees();
        using var doc = PdfDocument.Open(pdf);

        // First subtree: pages 0-1 use roman
        doc.GetPageLabel(1).Should().Be("i");
        doc.GetPageLabel(2).Should().Be("ii");

        // Second subtree: pages 2-3 use decimal
        doc.GetPageLabel(3).Should().Be("1");
        doc.GetPageLabel(4).Should().Be("2");
    }

    // ─── Helper: PDF builder ───────────────────────────────────────────────

    /// <summary>
    /// Build a minimal 3-page PDF with optional /PageLabels entry in catalog.
    /// </summary>
    private static byte[] MakePdfWithPageLabels(string? pageLabelsDict)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($@"<<
            /Type /Catalog
            /Pages 2 0 R
            {(pageLabelsDict ?? string.Empty)}
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page3Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine($"{page3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($@"<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Build a 4-page PDF with /PageLabels number tree using /Kids subtrees.
    /// Tests the WalkNumberTree recursion branch (lines ~62-70).
    /// Subtree 1 (obj 6): pages 0-1 with lowercase roman.
    /// Subtree 2 (obj 7): pages 2-3 with decimal.
    /// </summary>
    private static byte[] MakePdfWithNumberTreeSubtrees()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /PageLabels << /Kids [6 0 R 7 0 R] >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 8 0 R] /Count 4 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page3Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page4Pos = sb.Length;
        sb.AppendLine("8 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        // Subtree 1: Pages 0-1 with lowercase roman
        long subtree1Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Nums [0 << /S /r >>] >>");
        sb.AppendLine("endobj");

        // Subtree 2: Pages 2-3 with decimal
        long subtree2Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine("<< /Nums [2 << /S /D >>] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 9");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine($"{page3Pos:D10} 00000 n ");
        sb.AppendLine($"{subtree1Pos:D10} 00000 n ");
        sb.AppendLine($"{subtree2Pos:D10} 00000 n ");
        sb.AppendLine($"{page4Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($@"<< /Size 9 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
