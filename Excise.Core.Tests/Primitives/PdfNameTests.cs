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
    public void ToEncodedString_WithSimpleChars_ReturnsSlashPrefix()
    {
        var name = new PdfName("Type");

        var result = name.ToEncodedString();

        result.Should().Be("/Type");
    }

    [Fact]
    public void ToEncodedString_WithSpace_EncodesAsHex()
    {
        var name = new PdfName("Name With Spaces");

        var result = name.ToEncodedString();

        result.Should().Contain("#20");
    }

    [Fact]
    public void ToEncodedString_WithSpecialChars_EncodesAsHex()
    {
        var name = new PdfName("Name(With)Parens");

        var result = name.ToEncodedString();

        result.Should().Contain("#28");
        result.Should().Contain("#29");
    }

    [Fact]
    public void ToEncodedString_WithSlash_EncodesAsHex()
    {
        var name = new PdfName("Name/With/Slash");

        var result = name.ToEncodedString();

        result.Should().Contain("#2F");
    }

    [Fact]
    public void ToEncodedString_WithHash_EncodesAsHex()
    {
        var name = new PdfName("Name#Hash");

        var result = name.ToEncodedString();

        result.Should().Contain("#23");
    }

    [Fact]
    public void ToEncodedString_WithBraces_EncodesAsHex()
    {
        var name = new PdfName("Name{With}Braces");

        var result = name.ToEncodedString();

        result.Should().Contain("#7B");
        result.Should().Contain("#7D");
    }

    [Fact]
    public void ToEncodedString_WithBrackets_EncodesAsHex()
    {
        var name = new PdfName("Name[With]Brackets");

        var result = name.ToEncodedString();

        result.Should().Contain("#5B");
        result.Should().Contain("#5D");
    }

    [Fact]
    public void ToEncodedString_WithAngleBrackets_EncodesAsHex()
    {
        var name = new PdfName("Name<With>Angles");

        var result = name.ToEncodedString();

        result.Should().Contain("#3C");
        result.Should().Contain("#3E");
    }

    [Fact]
    public void ToEncodedString_WithPercent_EncodesAsHex()
    {
        var name = new PdfName("Name%Percent");

        var result = name.ToEncodedString();

        result.Should().Contain("#25");
    }

    [Fact]
    public void Decode_WithSimpleName_ReturnsValue()
    {
        var result = PdfName.Decode("Type");

        result.Should().Be("Type");
    }

    [Fact]
    public void Decode_WithHexEncodedChars_DecodesCorrectly()
    {
        var result = PdfName.Decode("Name#20With#20Spaces");

        result.Should().Be("Name With Spaces");
    }

    [Fact]
    public void Decode_WithoutHash_ReturnsUnchanged()
    {
        var result = PdfName.Decode("SimpleType");

        result.Should().Be("SimpleType");
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

    [Fact]
    public void Decode_WithInvalidHexSequence_SkipsInvalidSequence()
    {
        var result = PdfName.Decode("Name#GGInvalid");

        result.Should().Contain("Name");
    }

    [Fact]
    public void Decode_WithTruncatedHexSequence_HandlesTruncation()
    {
        var result = PdfName.Decode("Name#2");

        result.Should().Be("Name#2");
    }

    [Fact]
    public void ToEncodedString_WithControlCharacters_EncodesAsHex()
    {
        var name = new PdfName("Name\nNewline");

        var result = name.ToEncodedString();

        result.Should().Contain("#0A");
    }

    [Fact]
    public void ToEncodedString_WithNonPrintableChars_EncodesAsHex()
    {
        var name = new PdfName("Name\x00Null");

        var result = name.ToEncodedString();

        result.Should().Contain("#00");
    }
}
