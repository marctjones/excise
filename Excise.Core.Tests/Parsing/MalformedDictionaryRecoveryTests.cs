using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// #884 — three dictionary malformations that made excise refuse a whole page
/// while mutool and pdftocairo rendered it. Each shape is taken from a pdfium
/// corpus fixture and reproduced here by a file this test authors, so the
/// coverage does not depend on the gitignored corpus.
///
/// The general principle: excise refusing a file that other readers read is a
/// defect, not strictness. A reviewer cannot redact a page they were never
/// shown, and a page excise declines to render still reaches the recipient.
/// </summary>
public class MalformedDictionaryRecoveryTests
{
    /// <summary>
    /// pdfium bug_900552.pdf / bug_901654.pdf: <c>/Font &lt;&lt;F1 7 0 R&gt;&gt;</c>
    /// — the resource key lost its leading slash. The content stream still
    /// selects <c>/F1</c>, so the bare keyword IS the intended key: dropping
    /// the entry loses the font, and refusing the file loses the page.
    ///
    /// Resource names are arbitrary, so the pre-existing known-keys allow-list
    /// could never have covered this.
    /// </summary>
    [Fact]
    public void DictionaryKeyMissingItsSlash_IsReadAsAKey()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Resources << /Font <<F1 4 0 R>> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var resources = page.Resources;
        resources.Should().NotBeNull();
        var fonts = doc.Resolve(resources!.GetOptional("Font")!) as PdfDictionary;
        fonts.Should().NotBeNull();
        fonts!.ContainsKey("F1").Should().BeTrue(
            "the unprefixed key names the font the content stream selects as /F1");
    }

    /// <summary>
    /// A value keyword must NOT be promoted to a key — that is what the
    /// original allow-list was protecting against, and widening the rule to
    /// accept name-shaped keywords must not give it up.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public void ReservedValueKeywords_AreStillNotAcceptedAsKeys(string keyword)
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Bad << {keyword} 1 >> >>\nendobj\n",
        });

        // Refusing outright is an acceptable outcome here and is what happens
        // today; the property under test is narrower than "does it open". What
        // must never happen is the reserved word being ACCEPTED as a key, so
        // both outcomes are allowed and only that one is forbidden.
        try
        {
            using var doc = PdfDocument.Open(pdf);
            var bad = doc.Resolve(doc.GetPage(1).Dictionary.GetOptional("Bad")!) as PdfDictionary;
            (bad?.ContainsKey(keyword) ?? false).Should().BeFalse(
                $"'{keyword}' is a VALUE keyword; accepting it as a key would let a " +
                "malformed value cascade into invented dictionary structure");
        }
        catch (Excise.Core.Parsing.PdfParseException)
        {
            // Rejected before it could become a key — the stricter outcome.
        }
    }

    /// <summary>
    /// pdfium bug_1893.pdf: the dictionary is never closed — <c>endobj</c>
    /// arrives where <c>&gt;&gt;</c> should be.
    /// </summary>
    [Fact]
    public void UnclosedDictionary_TerminatesAtEndobj()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n",
            // No closing >> before endobj.
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        var font = doc.GetObject(4) as PdfDictionary;

        font.Should().NotBeNull("an unclosed dictionary must not condemn the object");
        font!.GetNameOrNull("BaseFont").Should().Be("Times-Roman",
            "everything parsed before the missing >> is still good data");
    }

    /// <summary>
    /// pdfium bug_481363.pdf / bug_488948351.pdf: <c>N 0 obj &lt;&lt; &lt;&lt; …</c>,
    /// a doubled dictionary opening. The inner dictionary carries the object's
    /// real content and is folded in rather than discarded.
    /// </summary>
    [Fact]
    public void DoubledDictionaryOpen_FoldsTheInnerDictionaryIn()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj <<\n<<\n /Type /Page /Parent 2 0 R /Rotate 90\n>>\n>>\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        page.Dictionary.GetNameOrNull("Type").Should().Be("Page");
        page.Rotation.Should().Be(90,
            "the inner dictionary's entries are the object's real content");
    }

    /// <summary>
    /// The redaction-safety half, required by CLAUDE.md: a document reached
    /// through a TOLERANT parse path must still redact completely. Recovering
    /// more files is only a gain if what comes back out is still clean —
    /// otherwise this change would have widened the set of documents excise
    /// renders while quietly widening the set it under-redacts.
    /// </summary>
    [Fact]
    public void ARecoveredDocument_StillRedactsCompletely()
    {
        const string secret = "CONFIDENTIALWITNESS";
        var content = $"BT /F1 24 Tf 20 100 Td ({secret}) Tj ET";
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            // The malformed resource dictionary from bug_900552.
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 5 0 R " +
            "/Resources << /Font <<F1 4 0 R>> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        });

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).Text.Should().Contain(secret, "the recovery must actually recover the text");

        doc.RedactText(secret);
        var saved = doc.SaveToBytes();

        // Carrier-agnostic: search the SAVED BYTES in both encodings, per
        // CLAUDE.md. Extraction alone has passed on leaking files three times.
        (Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved))
            .Should().NotContain(secret,
                "a file excise only opens because of a tolerant parse path must not " +
                "be a file excise silently under-redacts");
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
