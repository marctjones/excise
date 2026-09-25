using System.Text;
using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// <see cref="CidCMap"/> and <see cref="ToUnicodeCMapParser"/> lex CMaps with one
/// shared tokenizer (#1831). One quirky CMap is fed to both parsers so a lexical
/// change cannot move one parser without the other.
/// </summary>
public class CMapTokenizerQuirksTests
{
    // CR-only and form-feed separators; a comment ended by CR; a <<...>> dictionary
    // swallowed as one hex token; a stray '>'; non-hex characters and odd digit
    // counts inside hex strings; keywords, names and hex strings with no whitespace
    // between them; a digit-led token that is a number, not a keyword; a literal
    // with nested and escaped parentheses; an unterminated hex string at EOF.
    private const string Quirky =
        "/CIDInit /ProcSet findresource begin\r" +
        "12 dict begin begincmap\f" +
        "%% comment <FFFF> begincodespacerange ( stray paren\r" +
        "/CIDSystemInfo << /Registry (Adobe\\) still) /Supplement 0 >> def\r" +
        "/WMode 1 def\r" +
        "(nested (parens) and \\) escape) pop\r" +
        "2 begincodespacerange\r<00> <7F>\f<8140> <FEFE>>endcodespacerange\r" +
        "4 beginbfchar\r<01> <0041> > <0G2> <0G42>\r<5> <0043>%trailing\r<0004><0044>\rendbfchar\r" +
        "1 beginbfrange<10><12>[<0045><0046>]endbfrange\r" +
        "1 beginbfrange <20><22><0061> endbfrange\r" +
        "2beginbfchar <30> <0047> endbfchar\r" +
        "<00";

    [Fact]
    public void CidCMap_QuirkyLexing_YieldsTheSameCodespacesAndMappings()
    {
        var cmap = CidCMap.Parse(Quirky);

        cmap.CodespaceRanges.Select(r => (r.Low, r.High, r.Bytes))
            .Should().Equal((0x00, 0x7F, 1), (0x8140, 0xFEFE, 2));
        cmap.WMode.Should().Be(1);
        cmap.Mapping.Should().BeEquivalentTo(new Dictionary<int, int>
        {
            [0x01] = 0x41, [0x02] = 0x42, [0x04] = 0x44, [0x05] = 0x43,
            [0x10] = 0x45, [0x11] = 0x46,
            [0x20] = 0x61, [0x21] = 0x62, [0x22] = 0x63,
        });
        cmap.Decode([0x01, 0x05, 0x11, 0x12, 0x21, 0x30])
            .Should().Equal(0x41, 0x43, 0x46, 0x12, 0x62, 0x30);
    }

    [Fact]
    public void ToUnicodeCMapParser_QuirkyLexing_YieldsTheSameCodespacesAndMappings()
    {
        var parser = ToUnicodeCMapParser.ParseDetailed(Encoding.UTF8.GetBytes(Quirky));

        parser.CodespaceRanges.Select(r => (r.Low, r.High, r.Bytes))
            .Should().Equal((0x00, 0x7F, 1), (0x8140, 0xFEFE, 2));
        parser.MaxCodeBytes.Should().Be(2);
        parser.Mapping.Should().BeEquivalentTo(new Dictionary<int, string>
        {
            [0x01] = "A", [0x02] = "B", [0x04] = "D", [0x05] = "C",
            [0x10] = "E", [0x11] = "F",
            [0x20] = "a", [0x21] = "b", [0x22] = "c",
        });
        ToUnicodeCMapParser.Parse(Quirky).Should().BeEquivalentTo(parser.Mapping);
    }
}
