using AwesomeAssertions;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfIntegerTests
{
    [Fact]
    public void Constructor_WithLong_StoresValue()
    {
        var integer = new PdfInteger(42);

        integer.Value.Should().Be(42);
    }

    [Fact]
    public void Constructor_WithNegative_StoresNegativeValue()
    {
        var integer = new PdfInteger(-42);

        integer.Value.Should().Be(-42);
    }

    [Fact]
    public void Constructor_WithZero_StoresZero()
    {
        var integer = new PdfInteger(0);

        integer.Value.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithLargeValue_StoresValue()
    {
        var integer = new PdfInteger(9223372036854775807);

        integer.Value.Should().Be(9223372036854775807);
    }

    [Fact]
    public void ObjectType_ReturnsInteger()
    {
        var integer = new PdfInteger(42);

        integer.ObjectType.Should().Be(PdfObjectType.Integer);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var integer = new PdfInteger(42);

        integer.ToString().Should().Be("42");
    }

    [Fact]
    public void ToString_WithNegative_FormatsCorrectly()
    {
        var integer = new PdfInteger(-42);

        integer.ToString().Should().Be("-42");
    }

    [Fact]
    public void ImplicitConversion_ToLong()
    {
        var integer = new PdfInteger(42);
        long value = integer;

        value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_ToInt()
    {
        var integer = new PdfInteger(42);
        int value = integer;

        value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_ToDouble()
    {
        var integer = new PdfInteger(42);
        double value = integer;

        value.Should().Be(42.0);
    }

    [Fact]
    public void ImplicitConversion_FromLong()
    {
        PdfInteger integer = 42L;

        integer.Value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_FromInt()
    {
        PdfInteger integer = 42;

        integer.Value.Should().Be(42);
    }

    [Fact]
    public void Equals_WithSameValue_ReturnsTrue()
    {
        var int1 = new PdfInteger(42);
        var int2 = new PdfInteger(42);

        int1.Equals(int2).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentValue_ReturnsFalse()
    {
        var int1 = new PdfInteger(42);
        var int2 = new PdfInteger(99);

        int1.Equals(int2).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WithSameValue_ReturnsSameHash()
    {
        var int1 = new PdfInteger(42);
        var int2 = new PdfInteger(42);

        int1.GetHashCode().Should().Be(int2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithDifferentValue_ReturnsDifferentHash()
    {
        var int1 = new PdfInteger(42);
        var int2 = new PdfInteger(99);

        int1.GetHashCode().Should().NotBe(int2.GetHashCode());
    }
}

public class PdfRealTests
{
    [Fact]
    public void Constructor_WithDouble_StoresValue()
    {
        var real = new PdfReal(3.14);

        real.Value.Should().Be(3.14);
    }

    [Fact]
    public void Constructor_WithNegative_StoresNegativeValue()
    {
        var real = new PdfReal(-3.14);

        real.Value.Should().Be(-3.14);
    }

    [Fact]
    public void Constructor_WithZero_StoresZero()
    {
        var real = new PdfReal(0.0);

        real.Value.Should().Be(0.0);
    }

    [Fact]
    public void Constructor_WithVerySmall_StoresValue()
    {
        var real = new PdfReal(0.00001);

        real.Value.Should().BeApproximately(0.00001, 0.000001);
    }

    [Fact]
    public void Constructor_WithVeryLarge_StoresValue()
    {
        var real = new PdfReal(1000000.123);

        real.Value.Should().BeApproximately(1000000.123, 0.001);
    }

    [Fact]
    public void ObjectType_ReturnsReal()
    {
        var real = new PdfReal(3.14);

        real.ObjectType.Should().Be(PdfObjectType.Real);
    }

    [Fact]
    public void ToString_IncludesDecimalPoint()
    {
        var real = new PdfReal(3.14);

        real.ToString().Should().Contain(".");
    }

    [Fact]
    public void ToString_WithWholeNumber_IncludesDecimal()
    {
        var real = new PdfReal(42.0);

        real.ToString().Should().Contain(".0");
    }

    [Fact]
    public void ToString_WithSmallNumber_FormatsCorrectly()
    {
        var real = new PdfReal(0.001);

        var result = real.ToString();

        result.Should().NotBeEmpty();
    }

    [Fact]
    public void ImplicitConversion_ToDouble()
    {
        var real = new PdfReal(3.14);
        double value = real;

        value.Should().Be(3.14);
    }

    [Fact]
    public void ImplicitConversion_FromDouble()
    {
        PdfReal real = 3.14;

        real.Value.Should().Be(3.14);
    }

    [Fact]
    public void Equals_WithSameValue_ReturnsTrue()
    {
        var real1 = new PdfReal(3.14);
        var real2 = new PdfReal(3.14);

        real1.Equals(real2).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithinEpsilon_ReturnsTrue()
    {
        var real1 = new PdfReal(3.14);
        var real2 = new PdfReal(3.14 + 1e-11);

        real1.Equals(real2).Should().BeTrue();
    }

    [Fact]
    public void Equals_OutsideEpsilon_ReturnsFalse()
    {
        var real1 = new PdfReal(3.14);
        var real2 = new PdfReal(3.24);

        real1.Equals(real2).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WithSameValue_ReturnsSameHash()
    {
        var real1 = new PdfReal(3.14);
        var real2 = new PdfReal(3.14);

        real1.GetHashCode().Should().Be(real2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithDifferentValue_ReturnsDifferentHash()
    {
        var real1 = new PdfReal(3.14);
        var real2 = new PdfReal(2.71);

        real1.GetHashCode().Should().NotBe(real2.GetHashCode());
    }
}

public class PdfNumberExtensionsTests
{
    [Fact]
    public void GetNumber_WithInteger_ReturnsDouble()
    {
        var obj = new PdfInteger(42);

        var result = obj.GetNumber();

        result.Should().Be(42.0);
    }

    [Fact]
    public void GetNumber_WithReal_ReturnsDouble()
    {
        var obj = new PdfReal(3.14);

        var result = obj.GetNumber();

        result.Should().Be(3.14);
    }

    [Fact]
    public void GetNumber_WithNonNumber_ThrowsInvalidCastException()
    {
        var obj = new PdfName("Type");

        var action = () => obj.GetNumber();

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void GetInt_WithInteger_ReturnsInt()
    {
        var obj = new PdfInteger(42);

        var result = obj.GetInt();

        result.Should().Be(42);
    }

    [Fact]
    public void GetInt_WithReal_ReturnsInt()
    {
        var obj = new PdfReal(42.7);

        var result = obj.GetInt();

        result.Should().Be(42);
    }

    [Fact]
    public void GetInt_WithNonNumber_ThrowsInvalidCastException()
    {
        var obj = new PdfName("Type");

        var action = () => obj.GetInt();

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void GetLong_WithInteger_ReturnsLong()
    {
        var obj = new PdfInteger(42);

        var result = obj.GetLong();

        result.Should().Be(42);
    }

    [Fact]
    public void GetLong_WithReal_ReturnsLong()
    {
        var obj = new PdfReal(42.7);

        var result = obj.GetLong();

        result.Should().Be(42);
    }

    [Fact]
    public void GetLong_WithNonNumber_ThrowsInvalidCastException()
    {
        var obj = new PdfName("Type");

        var action = () => obj.GetLong();

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void TryGetNumber_WithInteger_ReturnsTrue()
    {
        var obj = new PdfInteger(42);

        var result = obj.TryGetNumber(out var value);

        result.Should().BeTrue();
        value.Should().Be(42.0);
    }

    [Fact]
    public void TryGetNumber_WithReal_ReturnsTrue()
    {
        var obj = new PdfReal(3.14);

        var result = obj.TryGetNumber(out var value);

        result.Should().BeTrue();
        value.Should().Be(3.14);
    }

    [Fact]
    public void TryGetNumber_WithNonNumber_ReturnsFalse()
    {
        var obj = new PdfName("Type");

        var result = obj.TryGetNumber(out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryGetNumber_WithNull_ReturnsFalse()
    {
        PdfObject? obj = null;

        var result = obj.TryGetNumber(out var value);

        result.Should().BeFalse();
        value.Should().Be(0);
    }
}
