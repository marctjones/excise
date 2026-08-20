using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Primitives;

/// <summary>
/// #1050 — every dictionary key excise reads may legally be an indirect
/// reference, and reading one without resolving returns null, which is
/// <b>indistinguishable from the key being absent</b>.
///
/// <para>That silence is not theoretical. #1040 shipped a redaction leak
/// through it: <c>/Resources /XObject</c> as <c>15 0 R</c> in Nitro Pro output
/// meant excise concluded the page referenced no forms, never reached the text
/// inside them, drew a black rectangle over an intact name, and reported
/// success.</para>
///
/// <para>The fixtures below make the key indirect ON PURPOSE — the shape real
/// producers emit and the one a bare <c>is PdfDictionary</c> cast misses. Each
/// asserts a USER-VISIBLE consequence rather than that the accessor returned
/// non-null, because "the accessor works" is the kind of claim that passed
/// while #1040 leaked.</para>
/// </summary>
public class IndirectResourceResolutionTests
{
    private const string Secret = "Farrar";

    /// <summary>
    /// A one-page PDF built by hand, so the object numbering and the indirect
    /// entry are exactly what the test claims.
    ///
    /// <para>⚠️ Deliberately NOT ContentStreamFixture.Build. That helper always
    /// writes /Font as a DIRECT sub-dictionary and numbers extraObjects from 6,
    /// so an attempt to inject "/Font 9 0 R" through it produced a duplicate
    /// key AND an unresolvable object number -- the test passed while proving
    /// nothing. Caught by the negative control below, which is the only reason
    /// this comment exists.</para>
    /// </summary>
    private static byte[] PageWithIndirectFontResource(string text)
    {
        var content = $"BT /F1 12 Tf 20 700 Td ({text}) Tj ET\n";
        return Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font 6 0 R >> /Contents 4 0 R >>\nendobj\n" +
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "6 0 obj\n<< /F1 5 0 R >>\nendobj\n" +
            "trailer\n<< /Size 7 /Root 1 0 R >>\n%%EOF\n");
    }

    /// <summary>
    /// End-to-end: a page whose /Font resource is an indirect reference still
    /// yields its text.
    ///
    /// <para>⚠️ MEASURED SCOPE, so nobody mistakes this for more than it is:
    /// mutating <c>PdfPage</c>'s /Font lookup back to the non-resolving form
    /// does NOT redden this test. Extraction reaches fonts through
    /// <c>ContentStreamWalker</c>, which resolves via
    /// <c>_page.Document.Resolve</c> on its own path. So this covers the
    /// extraction route, not the <c>PdfPage.Resources</c> change — that change
    /// is correctness-by-inspection and is currently UNPROVEN by any test.
    /// Recorded rather than glossed: an unproven fix that looks covered is how
    /// a green suite stops meaning anything.</para>
    /// </summary>
    [Fact]
    public void AnIndirectFontResource_StillYieldsTheText()
    {
        using var doc = PdfDocument.Open(PageWithIndirectFontResource(Secret));
        doc.GetPage(1).Text.Should().Contain(Secret,
            "an indirect /Font dictionary is the shape real producers emit; reading it " +
            "as absent leaves the page's text decoded by guesswork");
    }

    /// <summary>
    /// The #1040 shape itself, kept as a permanent regression: text living
    /// inside a Form XObject reached through an indirect /XObject.
    /// </summary>
    [Fact]
    public void TextInsideAFormReachedByAnIndirectXObject_IsRedacted()
    {
        var form = $"BT /F1 12 Tf 5 20 Td (Louise {Secret}) Tj ET\n";
        var pdf = ContentStreamFixture.Build(
            content: "q 1 0 0 1 100 600 cm /Fm0 Do Q\n",
            extraResources: "/XObject 6 0 R",
            extraObjects:
                "6 0 obj\n<< /Fm0 7 0 R >>\nendobj\n" +
                "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 300 60] " +
                "/Resources << /Font << /F1 5 0 R >> >> " +
                $"/Length {Encoding.Latin1.GetByteCount(form)} >>\nstream\n{form}endstream\nendobj\n");

        using var doc = PdfDocument.Open(pdf);
        doc.RedactText(Secret);
        using var ms = new MemoryStream();
        doc.Save(ms);

        Text.Segmentation.SavedPdfLeakScanner.FindTerm(ms.ToArray(), Secret).Should().BeEmpty(
            "the form is only reachable by resolving /XObject; before #1050 excise saw " +
            "no forms at all and left the name in the file behind a black box");
    }

    /// <summary>
    /// The page TREE. An indirect /Kids is completely ordinary — it is how any
    /// document with more than a trivial page count is written — and reading it
    /// as absent means the pages simply are not found.
    /// </summary>
    [Fact]
    public void AnIndirectKidsArray_StillYieldsThePages()
    {
        var content = "BT /F1 12 Tf 20 700 Td (page one) Tj ET\n";
        var pdf = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids 8 0 R /Count 1 >>\nendobj\n" +
            "8 0 obj\n[3 0 R]\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "trailer\n<< /Size 9 /Root 1 0 R >>\n%%EOF\n");

        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(1,
            "/Kids as an indirect reference is ordinary; a page tree walk that cannot " +
            "follow it finds no pages at all");
        doc.GetPage(1).Text.Should().Contain("page one");
    }

    /// <summary>
    /// The direct accessors still exist and still do exactly what their name
    /// now says — they do NOT follow a reference. This is the negative control:
    /// without it, the rename could have been quietly turned into a resolving
    /// read and nothing would notice.
    /// </summary>
    [Fact]
    public void TheDirectAccessor_DeliberatelyDoesNotFollowAReference()
    {
        using var doc = PdfDocument.Open(PageWithIndirectFontResource("x"));
        var resources = doc.GetPage(1).Resources!;

        resources.GetDirectDictionaryOrNull("Font").Should().BeNull(
            "the DIRECT accessor must keep reading only direct values — that is what its " +
            "name promises, and the sites deliberately left on it depend on the promise");
        resources.ResolveDictionary(doc, "Font").Should().NotBeNull(
            "while the resolving one follows the reference");
    }
}
