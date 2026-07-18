using System.Collections;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Covers <see cref="ContentOperator.ToString"/> operand formatting across
/// operand kinds, and PdfArray's non-generic enumerator.
/// </summary>
public class ContentOperatorToStringTests
{
    [Fact]
    public void ToString_FormatsStringNameArrayAndNumberOperands()
    {
        new ContentOperator("Tj", new PdfObject[] { new PdfString("hi") })
            .ToString().Should().Contain("(hi)").And.EndWith("Tj");

        new ContentOperator("Do", new PdfObject[] { new PdfName("Im0") })
            .ToString().Should().Contain("/Im0");

        new ContentOperator("TJ", new PdfObject[]
            {
                new PdfArray(new PdfObject[] { new PdfString("a"), new PdfInteger(5) })
            })
            .ToString().Should().Contain("[").And.Contain("(a)");

        new ContentOperator("Td", new PdfObject[] { new PdfInteger(1), new PdfReal(2.5) })
            .ToString().Should().Contain("2.5");

        // Default operand-format branch (a non string/name/number/array object).
        new ContentOperator("x", new PdfObject[] { PdfBoolean.True })
            .ToString().Should().Contain("x");
    }

    [Fact]
    public void PdfStream_DecodeParams_AsArray_ReturnsPerFilterDicts()
    {
        var dict = new PdfDictionary();
        dict["Filter"] = new PdfArray(new PdfObject[] { new PdfName("FlateDecode") });
        dict["DecodeParms"] = new PdfArray(new PdfObject[] { new PdfDictionary() });
        var stream = new PdfStream(dict, new byte[] { 1, 2, 3 });

        stream.DecodeParams.Should().NotBeNull("a /DecodeParms array yields one dict per filter");
    }

    [Fact]
    public void ToString_NoOperands_IsJustTheName()
    {
        new ContentOperator("BT").ToString().Should().Be("BT");
    }

    [Fact]
    public void PdfArray_NonGenericEnumerator_Iterates()
    {
        var arr = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(2) });

        IEnumerator e = ((IEnumerable)arr).GetEnumerator();
        int count = 0;
        while (e.MoveNext()) count++;

        count.Should().Be(2);
    }
}
