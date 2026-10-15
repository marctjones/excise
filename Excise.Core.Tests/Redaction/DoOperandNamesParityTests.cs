using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Redaction;

/// <summary>
/// #1626: the undrawn-XObject prune reads the <c>Do</c> names through a
/// stateless walker sink instead of a full operator parse. It must read
/// exactly the names the parse read, in order, or an XObject a stream draws
/// could be judged undrawn and freed (or a drawn one kept as undrawn).
/// </summary>
public class DoOperandNamesParityTests
{
    [Theory]
    [InlineData("q 1 0 0 1 0 0 cm /Fm0 Do Q /Im1 Do")]
    [InlineData("BT /F1 12 Tf (Do not) Tj ET /X Do")]
    [InlineData("/OC /L1 BDC /Fm2 Do EMC 5 Do /A /B Do")]
    [InlineData("BI /W 1 /H 1 /BPC 8 /CS /G ID \u0000 EI /Im9 Do")]
    [InlineData("<< /K /V >> /Fm1 Do [ /Q ] Do Do")]
    [InlineData("/Fm1 Do % /Fm2 Do\n/Fm3 Do")]
    [InlineData("no operators here at all Do")]
    public void ReadsTheSameNamesAsTheOperatorParse(string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        var parsed = new ContentStreamParser(bytes) { ComputeOperatorMetadata = false }.Parse().Operators
            .Where(op => op.Name == "Do").Select(op => op.GetName(0)).ToList();

        RedactionFeatureStripper.DoOperandNames(bytes).Should().Equal(parsed);
    }
}
