using AwesomeAssertions;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfReferenceTests
{
    [Fact]
    public void Constructor_WithObjectNum_StoresValues()
    {
        var reference = new PdfReference(5);

        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithObjectNumAndGeneration_StoresValues()
    {
        var reference = new PdfReference(5, 2);

        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(2);
    }

    [Fact]
    public void Constructor_WithNegativeObjectNum_ThrowsArgumentOutOfRangeException()
    {
        var action = () => new PdfReference(-1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WithNegativeGeneration_ThrowsArgumentOutOfRangeException()
    {
        var action = () => new PdfReference(5, -1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ObjectType_ReturnsReference()
    {
        var reference = new PdfReference(5);

        reference.ObjectType.Should().Be(PdfObjectType.Reference);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var reference = new PdfReference(5, 0);

        reference.ToString().Should().Be("5 0 R");
    }

    [Fact]
    public void ToString_WithNonZeroGeneration_FormatsCorrectly()
    {
        var reference = new PdfReference(5, 2);

        reference.ToString().Should().Be("5 2 R");
    }

    [Fact]
    public void Parse_WithValidString_ReturnsReference()
    {
        var reference = PdfReference.Parse("5 0 R");

        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(0);
    }

    [Fact]
    public void Parse_WithNonZeroGeneration_ReturnsReference()
    {
        var reference = PdfReference.Parse("5 2 R");

        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(2);
    }

    [Fact]
    public void Parse_WithInvalidFormat_ThrowsFormatException()
    {
        var action = () => PdfReference.Parse("5 0 X");

        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_WithMissingR_ThrowsFormatException()
    {
        var action = () => PdfReference.Parse("5 0");

        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_WithExtraSpaces_ReturnsReference()
    {
        var reference = PdfReference.Parse("5  0   R");

        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(0);
    }

    [Fact]
    public void Equals_WithSameValues_ReturnsTrue()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(5, 0);

        ref1.Equals(ref2).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentObjectNum_ReturnsFalse()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(6, 0);

        ref1.Equals(ref2).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithDifferentGeneration_ReturnsFalse()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(5, 1);

        ref1.Equals(ref2).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithNull_ReturnsFalse()
    {
        var reference = new PdfReference(5, 0);

        reference.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void ObjectEquals_WithSameValues_ReturnsTrue()
    {
        var ref1 = new PdfReference(5, 0);
        object ref2 = new PdfReference(5, 0);

        ref1.Equals(ref2).Should().BeTrue();
    }

    [Fact]
    public void ObjectEquals_WithDifferentType_ReturnsFalse()
    {
        var reference = new PdfReference(5, 0);
        object other = "5 0 R";

        reference.Equals(other).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WithSameValues_ReturnsSameHash()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(5, 0);

        ref1.GetHashCode().Should().Be(ref2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithDifferentValues_ReturnsDifferentHash()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(6, 0);

        ref1.GetHashCode().Should().NotBe(ref2.GetHashCode());
    }

    [Fact]
    public void EqualityOperator_WithSameValues_ReturnsTrue()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(5, 0);

        (ref1 == ref2).Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_WithDifferentValues_ReturnsFalse()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(6, 0);

        (ref1 == ref2).Should().BeFalse();
    }

    [Fact]
    public void EqualityOperator_WithBothNull_ReturnsTrue()
    {
        PdfReference? ref1 = null;
        PdfReference? ref2 = null;

        (ref1 == ref2).Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_WithOneNull_ReturnsFalse()
    {
        var ref1 = new PdfReference(5, 0);
        PdfReference? ref2 = null;

        (ref1 == ref2).Should().BeFalse();
    }

    [Fact]
    public void InequalityOperator_WithDifferentValues_ReturnsTrue()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(6, 0);

        (ref1 != ref2).Should().BeTrue();
    }

    [Fact]
    public void InequalityOperator_WithSameValues_ReturnsFalse()
    {
        var ref1 = new PdfReference(5, 0);
        var ref2 = new PdfReference(5, 0);

        (ref1 != ref2).Should().BeFalse();
    }
}

public class PdfIndirectObjectTests
{
    [Fact]
    public void Constructor_StoresValues()
    {
        var obj = new PdfInteger(42);
        var indirect = new PdfIndirectObject(5, 0, obj);

        indirect.ObjectNumber.Should().Be(5);
        indirect.Generation.Should().Be(0);
        indirect.Value.Should().Be(obj);
    }

    [Fact]
    public void Constructor_WithNull_StoresPdfNull()
    {
        // Constructor's runtime contract converts null → PdfNull.Instance;
        // null! silences the non-nullable warning while exercising that path.
        var indirect = new PdfIndirectObject(5, 0, null!);

        indirect.Value.Should().Be(PdfNull.Instance);
    }

    [Fact]
    public void Constructor_SetsObjectNumber()
    {
        var obj = new PdfInteger(42);
        var indirect = new PdfIndirectObject(5, 0, obj);

        obj.ObjectNumber.Should().Be(5);
    }

    [Fact]
    public void Constructor_SetsGenerationNumber()
    {
        var obj = new PdfInteger(42);
        var indirect = new PdfIndirectObject(5, 2, obj);

        obj.GenerationNumber.Should().Be(2);
    }

    [Fact]
    public void Reference_Property_ReturnsReference()
    {
        var indirect = new PdfIndirectObject(5, 0, new PdfInteger(42));

        var reference = indirect.Reference;

        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(0);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var indirect = new PdfIndirectObject(5, 0, new PdfInteger(42));

        var result = indirect.ToString();

        result.Should().Contain("5 0 obj");
        result.Should().Contain("endobj");
    }
}
