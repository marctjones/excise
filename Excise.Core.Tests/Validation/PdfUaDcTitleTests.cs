using AwesomeAssertions;
using Excise.Core.Validation;
using Xunit;

namespace Excise.Core.Tests.Validation;

/// <summary>
/// PDF/UA's document-title rule reads a VALUE, not a property name (#1532).
///
/// <para>The check used to be <c>xmp.Contains("dc:title")</c> — a substring
/// match standing in for a parse, the same error as #1524 in a different
/// schema. ISO 14289-1 §7.1 requires the title present AND non-empty, so an
/// empty title was reported conformant.</para>
///
/// <para>This matters because <c>PdfUaValidator</c> is excise's own structural
/// self-check. It is not the authority for a conformance claim — veraPDF is —
/// but a self-check that says Pass on a file veraPDF fails is worse than no
/// self-check, because someone reads the Pass.</para>
/// </summary>
public class PdfUaDcTitleTests
{
    private const string Head = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'>"
        + "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:dc='http://purl.org/dc/elements/1.1/'>";
    private const string Tail = "</rdf:RDF></x:xmpmeta><?xpacket end='w'?>";

    [Theory]
    // The shapes that must FAIL. Every one of these passed the substring match.
    [InlineData("<dc:title/>")]
    [InlineData("<dc:title></dc:title>")]
    [InlineData("<dc:title>   </dc:title>")]
    [InlineData("<dc:title><rdf:Alt><rdf:li xml:lang='x-default'></rdf:li></rdf:Alt></dc:title>")]
    [InlineData("<dc:title><rdf:Alt><rdf:li xml:lang='x-default'>  </rdf:li></rdf:Alt></dc:title>")]
    [InlineData("dc:title=\"\"")]
    // The property NAME occurring outside a title: a comment, another value.
    [InlineData("<!-- dc:title is intentionally absent -->")]
    [InlineData("<dc:description><rdf:Alt><rdf:li>see dc:title elsewhere</rdf:li></rdf:Alt></dc:description>")]
    public void AnEmptyOrAbsentTitle_IsNotATitle(string body)
    {
        PdfUaValidator.ReadDcTitle(Head + body + Tail).Should().BeNull();
    }

    [Theory]
    // The shapes that must PASS, with the value read back.
    [InlineData("<dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Quarterly Report</rdf:li></rdf:Alt></dc:title>",
        "Quarterly Report")]
    [InlineData("<dc:title><rdf:Alt><rdf:li>Untagged Alternative</rdf:li></rdf:Alt></dc:title>",
        "Untagged Alternative")]
    [InlineData("<dc:title>Bare Element</dc:title>", "Bare Element")]
    [InlineData("dc:title=\"Attribute Shorthand\"", "Attribute Shorthand")]
    // Entities are decoded, so an entity-only title is not mistaken for text.
    [InlineData("<dc:title><rdf:Alt><rdf:li>Smith &amp; Jones</rdf:li></rdf:Alt></dc:title>", "Smith & Jones")]
    public void ARealTitle_IsReadBack(string body, string expected)
    {
        PdfUaValidator.ReadDcTitle(Head + body + Tail).Should().Be(expected);
    }

    [Fact]
    public void XDefault_WinsOverTheOtherAlternatives()
    {
        // XMP readers resolve an alt-text array to x-default; picking whichever
        // rdf:li came first would report a language the viewer will not show.
        var xmp = Head
            + "<dc:title><rdf:Alt>"
            + "<rdf:li xml:lang='de-DE'>Vierteljahresbericht</rdf:li>"
            + "<rdf:li xml:lang='x-default'>Quarterly Report</rdf:li>"
            + "</rdf:Alt></dc:title>"
            + Tail;

        PdfUaValidator.ReadDcTitle(xmp).Should().Be("Quarterly Report");
    }

    [Fact]
    public void AnAlternativeWithoutXDefault_StillYieldsATitle()
    {
        // Non-conformant XMP, but a title the reader can show is still a title:
        // failing here would report a usable document as having none.
        var xmp = Head
            + "<dc:title><rdf:Alt><rdf:li xml:lang='de-DE'>Vierteljahresbericht</rdf:li></rdf:Alt></dc:title>"
            + Tail;

        PdfUaValidator.ReadDcTitle(xmp).Should().Be("Vierteljahresbericht");
    }
}
