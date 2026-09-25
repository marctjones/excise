using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1846: <see cref="SavedPdfLeakScanner"/> must see a UTF-16BE text string in
/// the form excise writes it. qpdf decodes the comment from the same saved file,
/// so a clean scan there is the scanner's blindness, not an absent term.
/// </summary>
public sealed class LeakScannerTextStringOracleTests
{
    private const string TitleTerm = "سلام";
    private const string CommentTerm = "مرحبا";
    private const string LatinInUtf16 = "KESTREL";

    [Fact]
    public void TextStringsQpdfDecodes_AreFoundByTheScanner()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = Path.Combine(Path.GetTempPath(), $"excise-1846-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(Fixture()))
                document.Save(path);
            var saved = File.ReadAllBytes(path);

            var annotations = QpdfReferenceTool.ListAnnotations(path);
            annotations.Should().NotBeNull("qpdf is the independent reader of the comment");
            annotations!.Single(a => a.Subtype == "Text").Contents.Should()
                .Contain(CommentTerm).And.Contain(LatinInUtf16, "qpdf decodes both terms from the saved comment");
            using (var reopened = PdfDocument.Open(path))
                PdfOutlineParser.Parse(reopened).Should().Contain(o => o.Title.Contains(TitleTerm),
                    "the saved outline keeps its title");

            SavedPdfLeakScanner.FindTerm(saved, CommentTerm).Should().NotBeEmpty(
                "qpdf reads the Arabic comment from this file");
            SavedPdfLeakScanner.FindTerm(saved, LatinInUtf16).Should().NotBeEmpty(
                "qpdf reads the Latin term that shares the comment's UTF-16BE string");
            SavedPdfLeakScanner.FindTerm(saved, TitleTerm).Should().NotBeEmpty(
                "the outline title is in the file");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// One page with a comment whose /Contents is a UTF-16BE literal with
    /// octal escapes and an outline item whose /Title is a UTF-16BE hex string.
    /// </summary>
    private static byte[] Fixture()
    {
        static string Utf16Hex(string text) =>
            "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";
        static string Utf16Octal(string text) =>
            @"(\376\377" + string.Concat(Encoding.BigEndianUnicode.GetBytes(text)
                .Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + ")";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            $"<< /Title {Utf16Hex(TitleTerm + " chapter")} /Parent 4 0 R >>",
            $"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents {Utf16Octal($"{LatinInUtf16} and {CommentTerm}")} >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
