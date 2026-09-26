using AwesomeAssertions;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfNameTests
{
    [Fact]
    public void Constructor_WithValue_StoresValue()
    {
        var name = new PdfName("Type");

        name.Value.Should().Be("Type");
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        var action = () => new PdfName(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ObjectType_ReturnsName()
    {
        var name = new PdfName("Type");

        name.ObjectType.Should().Be(PdfObjectType.Name);
    }

    [Fact]
    public void ToString_IncludesSolidus()
    {
        var name = new PdfName("Type");

        var result = name.ToString();

        result.Should().Be("/Type");
    }

    [Fact]
    public void Equals_WithSameValue_ReturnsTrue()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Type");

        var result = name1.Equals(name2);

        result.Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentValue_ReturnsFalse()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Name");

        var result = name1.Equals(name2);

        result.Should().BeFalse();
    }

    [Fact]
    public void Equals_WithNull_ReturnsFalse()
    {
        var name = new PdfName("Type");

        var result = name.Equals(null);

        result.Should().BeFalse();
    }

    [Fact]
    public void ObjectEquals_WithSameValue_ReturnsTrue()
    {
        var name1 = new PdfName("Type");
        object name2 = new PdfName("Type");

        var result = name1.Equals(name2);

        result.Should().BeTrue();
    }

    [Fact]
    public void ObjectEquals_WithDifferentType_ReturnsFalse()
    {
        var name = new PdfName("Type");
        object other = "Type";

        var result = name.Equals(other);

        result.Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WithSameValue_ReturnsSameHash()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Type");

        name1.GetHashCode().Should().Be(name2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithDifferentValue_ReturnsDifferentHash()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Name");

        name1.GetHashCode().Should().NotBe(name2.GetHashCode());
    }

    [Fact]
    public void EqualityOperator_WithSameValue_ReturnsTrue()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Type");

        var result = name1 == name2;

        result.Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_WithDifferentValue_ReturnsFalse()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Name");

        var result = name1 == name2;

        result.Should().BeFalse();
    }

    [Fact]
    public void EqualityOperator_WithBothNull_ReturnsTrue()
    {
        PdfName? name1 = null;
        PdfName? name2 = null;

        var result = name1 == name2;

        result.Should().BeTrue();
    }

    [Fact]
    public void EqualityOperator_WithOneNull_ReturnsFalse()
    {
        PdfName name1 = new PdfName("Type");
        PdfName? name2 = null;

        var result = name1 == name2;

        result.Should().BeFalse();
    }

    [Fact]
    public void InequalityOperator_WithDifferentValue_ReturnsTrue()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Name");

        var result = name1 != name2;

        result.Should().BeTrue();
    }

    [Fact]
    public void InequalityOperator_WithSameValue_ReturnsFalse()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Type");

        var result = name1 != name2;

        result.Should().BeFalse();
    }

    [Fact]
    public void ImplicitConversion_FromString_CreatesName()
    {
        PdfName name = "Type";

        name.Value.Should().Be("Type");
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        var name = new PdfName("Type");
        string value = name;

        value.Should().Be("Type");
    }

    [Fact]
    public void CommonNames_TypeConstant()
    {
        PdfName.Type.Value.Should().Be("Type");
    }

    [Fact]
    public void CommonNames_PageConstant()
    {
        PdfName.Page.Value.Should().Be("Page");
    }

    [Fact]
    public void CommonNames_CatalogConstant()
    {
        PdfName.Catalog.Value.Should().Be("Catalog");
    }

    [Fact]
    public void CommonNames_PagesConstant()
    {
        PdfName.Pages.Value.Should().Be("Pages");
    }

    [Fact]
    public void CommonNames_KidsConstant()
    {
        PdfName.Kids.Value.Should().Be("Kids");
    }

    [Fact]
    public void CommonNames_ContentsConstant()
    {
        PdfName.Contents.Value.Should().Be("Contents");
    }

    [Fact]
    public void CommonNames_ResourcesConstant()
    {
        PdfName.Resources.Value.Should().Be("Resources");
    }

    [Fact]
    public void CommonNames_MediaBoxConstant()
    {
        PdfName.MediaBox.Value.Should().Be("MediaBox");
    }
}
