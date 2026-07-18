using AwesomeAssertions;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfBooleanTests
{
    [Fact]
    public void True_Singleton_HasValue()
    {
        PdfBoolean.True.Value.Should().BeTrue();
    }

    [Fact]
    public void False_Singleton_HasValue()
    {
        PdfBoolean.False.Value.Should().BeFalse();
    }

    [Fact]
    public void Get_WithTrue_ReturnsTrueSingleton()
    {
        var result = PdfBoolean.Get(true);

        result.Should().BeSameAs(PdfBoolean.True);
    }

    [Fact]
    public void Get_WithFalse_ReturnsFalseSingleton()
    {
        var result = PdfBoolean.Get(false);

        result.Should().BeSameAs(PdfBoolean.False);
    }

    [Fact]
    public void ObjectType_ReturnsBoolean()
    {
        var boolean = PdfBoolean.True;

        boolean.ObjectType.Should().Be(PdfObjectType.Boolean);
    }

    [Fact]
    public void ToString_WithTrue_ReturnsTrue()
    {
        PdfBoolean.True.ToString().Should().Be("true");
    }

    [Fact]
    public void ToString_WithFalse_ReturnsFalse()
    {
        PdfBoolean.False.ToString().Should().Be("false");
    }

    [Fact]
    public void ImplicitConversion_ToBool_WithTrue()
    {
        bool value = PdfBoolean.True;

        value.Should().BeTrue();
    }

    [Fact]
    public void ImplicitConversion_ToBool_WithFalse()
    {
        bool value = PdfBoolean.False;

        value.Should().BeFalse();
    }

    [Fact]
    public void ImplicitConversion_FromBool_WithTrue()
    {
        PdfBoolean boolean = true;

        boolean.Should().BeSameAs(PdfBoolean.True);
    }

    [Fact]
    public void ImplicitConversion_FromBool_WithFalse()
    {
        PdfBoolean boolean = false;

        boolean.Should().BeSameAs(PdfBoolean.False);
    }
}

public class PdfNullTests
{
    [Fact]
    public void Instance_Singleton_IsNotNull()
    {
        PdfNull.Instance.Should().NotBeNull();
    }

    [Fact]
    public void Instance_Singleton_IsSingleton()
    {
        var null1 = PdfNull.Instance;
        var null2 = PdfNull.Instance;

        null1.Should().BeSameAs(null2);
    }

    [Fact]
    public void ObjectType_ReturnsNull()
    {
        var nullObj = PdfNull.Instance;

        nullObj.ObjectType.Should().Be(PdfObjectType.Null);
    }

    [Fact]
    public void ToString_ReturnsNull()
    {
        PdfNull.Instance.ToString().Should().Be("null");
    }
}
