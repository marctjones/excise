using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Text;

public class CidCMapParserTests
{
    [Fact]
    public void Parse_BfCharType0EncodingMap_DecodesCharacterCodesToCids()
    {
        var cmapData = Encoding.UTF8.GetBytes("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            3 beginbfchar
            <0020> <0003>
            <0043> <0026>
            <0068> <004b>
            endbfchar
            endcmap
            end
            end
            """);

        var cmap = CidCMap.Parse(cmapData);

        cmap.Mapping[0x0020].Should().Be(0x0003);
        cmap.Mapping[0x0043].Should().Be(0x0026);
        cmap.Mapping[0x0068].Should().Be(0x004b);
        cmap.Decode([0x00, 0x43, 0x00, 0x68, 0x00, 0x20])
            .Should().Equal(0x0026, 0x004b, 0x0003);
    }

    [Fact]
    public void Parse_CidRangeAndBfRange_DecodesIncrementingAndArrayRanges()
    {
        var cmap = CidCMap.Parse("""
            2 begincodespacerange
            <00> <7f>
            <8100> <81ff>
            endcodespacerange
            1 begincidrange
            <41> <43> 100
            endcidrange
            1 beginbfrange
            <8101> <8103> [<0200> <0205> <0209>]
            endbfrange
            """);

        cmap.Decode([0x41, 0x42, 0x43, 0x81, 0x01, 0x81, 0x02, 0x81, 0x03])
            .Should().Equal(100, 101, 102, 0x0200, 0x0205, 0x0209);
    }

    [Fact]
    public void Parse_UseCMapIdentityH_InheritsTwoByteCodespace()
    {
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <20> <7f>
            endcodespacerange
            /Identity-H usecmap
            """);

        cmap.CodespaceRanges.Should().Contain(r => r.Low == 0 && r.High == 0xffff && r.Bytes == 2);
        cmap.Decode([0x00, 0x41, 0x00, 0x42])
            .Should().Equal(0x0041, 0x0042);
    }
}
