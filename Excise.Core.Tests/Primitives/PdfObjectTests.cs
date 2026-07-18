using AwesomeAssertions;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfObjectTests
{
    [Fact]
    public void ObjectNumber_CanBeSet()
    {
        var obj = new PdfInteger(42);

        obj.ObjectNumber = 5;

        obj.ObjectNumber.Should().Be(5);
    }

    [Fact]
    public void GenerationNumber_CanBeSet()
    {
        var obj = new PdfInteger(42);

        obj.GenerationNumber = 2;

        obj.GenerationNumber.Should().Be(2);
    }

    [Fact]
    public void IsIndirect_WithoutObjectNumber_ReturnsFalse()
    {
        var obj = new PdfInteger(42);

        obj.IsIndirect.Should().BeFalse();
    }

    [Fact]
    public void IsIndirect_WithObjectNumber_ReturnsTrue()
    {
        var obj = new PdfInteger(42);
        obj.ObjectNumber = 5;

        obj.IsIndirect.Should().BeTrue();
    }

    [Fact]
    public void As_WithCorrectType_ReturnsCasted()
    {
        var obj = (PdfObject)new PdfInteger(42);

        var result = obj.As<PdfInteger>();

        result.Should().NotBeNull();
        result!.Value.Should().Be(42);
    }

    [Fact]
    public void As_WithWrongType_ReturnsNull()
    {
        var obj = (PdfObject)new PdfName("Type");

        var result = obj.As<PdfInteger>();

        result.Should().BeNull();
    }

    [Fact]
    public void Expect_WithCorrectType_ReturnsCasted()
    {
        var obj = (PdfObject)new PdfInteger(42);

        var result = obj.Expect<PdfInteger>();

        result.Value.Should().Be(42);
    }

    [Fact]
    public void Expect_WithWrongType_ThrowsInvalidCastException()
    {
        var obj = (PdfObject)new PdfName("Type");

        var action = () => obj.Expect<PdfInteger>();

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void ObjectType_IsAbstract_ButImplementedBySubclasses()
    {
        var integer = new PdfInteger(42);
        var name = new PdfName("Type");
        var array = new PdfArray();

        integer.ObjectType.Should().Be(PdfObjectType.Integer);
        name.ObjectType.Should().Be(PdfObjectType.Name);
        array.ObjectType.Should().Be(PdfObjectType.Array);
    }
}

public class PdfStreamTests
{
    [Fact]
    public void Constructor_WithDictionaryAndData_StoresValues()
    {
        var dict = new PdfDictionary();
        var data = new byte[] { 1, 2, 3 };

        var stream = new PdfStream(dict, data);

        stream.EncodedData.Should().Equal(data);
    }

    [Fact]
    public void Constructor_Empty_CreatesEmptyStream()
    {
        var stream = new PdfStream();

        stream.EncodedData.Should().BeEmpty();
        stream.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithData_SetsLength()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var stream = new PdfStream(data);

        stream.Length.Should().Be(5);
    }

    [Fact]
    public void ObjectType_ReturnsStream()
    {
        var stream = new PdfStream();

        stream.ObjectType.Should().Be(PdfObjectType.Stream);
    }

    [Fact]
    public void EncodedData_Property_ReturnsData()
    {
        var data = new byte[] { 1, 2, 3 };
        var stream = new PdfStream(data);

        stream.EncodedData.Should().Equal(data);
    }

    [Fact]
    public void Length_Property_ReturnsLength()
    {
        var stream = new PdfStream(new byte[] { 1, 2, 3 });

        stream.Length.Should().Be(3);
    }

    [Fact]
    public void IsFiltered_WithoutFilter_ReturnsFalse()
    {
        var stream = new PdfStream();

        stream.IsFiltered.Should().BeFalse();
    }

    [Fact]
    public void IsFiltered_WithFilter_ReturnsTrue()
    {
        var stream = new PdfStream();
        stream["Filter"] = new PdfName("FlateDecode");

        stream.IsFiltered.Should().BeTrue();
    }

    [Fact]
    public void Filters_WithoutFilter_ReturnsEmptyList()
    {
        var stream = new PdfStream();

        var filters = stream.Filters;

        filters.Should().BeEmpty();
    }

    [Fact]
    public void Filters_WithSingleFilter_ReturnsList()
    {
        var stream = new PdfStream();
        stream["Filter"] = new PdfName("FlateDecode");

        var filters = stream.Filters;

        filters.Should().Equal("FlateDecode");
    }

    [Fact]
    public void Filters_WithMultipleFilters_ReturnsList()
    {
        var stream = new PdfStream();
        var array = new PdfArray(new PdfName("FlateDecode"), new PdfName("ASCII85Decode"));
        stream["Filter"] = array;

        var filters = stream.Filters;

        filters.Should().Equal("FlateDecode", "ASCII85Decode");
    }

    [Fact]
    public void DecodeParams_WithoutParams_ReturnsEmptyList()
    {
        var stream = new PdfStream();

        var params_list = stream.DecodeParams;

        params_list.Should().BeEmpty();
    }

    [Fact]
    public void DecodedData_WithoutFilters_ReturnsSameAsEncoded()
    {
        var data = new byte[] { 1, 2, 3 };
        var stream = new PdfStream(data);

        var decoded = stream.DecodedData;

        decoded.Should().Equal(data);
    }

    [Fact]
    public void DecodedData_WithFilters_ThrowsWithoutDecoding()
    {
        var stream = new PdfStream();
        stream["Filter"] = new PdfName("FlateDecode");

        var action = () => _ = stream.DecodedData;

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void IsDecoded_WhenNotDecoded_ReturnsFalse()
    {
        var stream = new PdfStream();

        stream.IsDecoded.Should().BeFalse();
    }

    [Fact]
    public void IsDecoded_AfterSettingDecodedData_ReturnsTrue()
    {
        var stream = new PdfStream();
        stream.SetDecodedData(new byte[] { 1, 2, 3 });

        stream.IsDecoded.Should().BeTrue();
    }

    [Fact]
    public void SetEncodedData_UpdatesData()
    {
        var stream = new PdfStream(new byte[] { 1, 2, 3 });
        var newData = new byte[] { 4, 5, 6 };

        stream.SetEncodedData(newData);

        stream.EncodedData.Should().Equal(newData);
    }

    [Fact]
    public void SetEncodedData_WithNull_ThrowsArgumentNullException()
    {
        var stream = new PdfStream();

        var action = () => stream.SetEncodedData(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetDecodedData_SetsData()
    {
        var stream = new PdfStream();
        var data = new byte[] { 1, 2, 3 };

        stream.SetDecodedData(data);

        stream.IsDecoded.Should().BeTrue();
    }

    [Fact]
    public void DecodedData_AfterSet_CanBeRetrieved()
    {
        var stream = new PdfStream();
        var data = new byte[] { 1, 2, 3 };
        stream.DecodedData = data;

        stream.DecodedData.Should().Equal(data);
    }

    [Fact]
    public void DecodedData_Set_RemovesFilter()
    {
        var stream = new PdfStream();
        stream["Filter"] = new PdfName("FlateDecode");

        stream.DecodedData = new byte[] { 1, 2, 3 };

        stream.ContainsKey("Filter").Should().BeFalse();
    }

    [Fact]
    public void DecodedData_Set_UpdatesLength()
    {
        var stream = new PdfStream();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        stream.DecodedData = data;

        stream.Length.Should().Be(5);
    }

    [Fact]
    public void GetDecodedString_ReturnsUtf8String()
    {
        var text = "Hello World";
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        var stream = new PdfStream(data);

        var result = stream.GetDecodedString();

        result.Should().Be(text);
    }

    [Fact]
    public void GetDecodedString_WithEncoding_ReturnsStringWithEncoding()
    {
        var text = "Hello";
        var data = System.Text.Encoding.ASCII.GetBytes(text);
        var stream = new PdfStream(data);

        var result = stream.GetDecodedString(System.Text.Encoding.ASCII);

        result.Should().Be(text);
    }

    [Fact]
    public void Constructor_CopiesDictionaryEntries()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Stream");

        var stream = new PdfStream(dict, new byte[] { });

        stream["Type"].Should().BeOfType<PdfName>();
    }

    [Fact]
    public void Constructor_PreservesDictionaryObjectNumbers()
    {
        var dict = new PdfDictionary();
        dict.ObjectNumber = 5;
        dict.GenerationNumber = 0;

        var stream = new PdfStream(dict, new byte[] { });

        stream.ObjectNumber.Should().Be(5);
        stream.GenerationNumber.Should().Be(0);
    }

    [Fact]
    public void ToString_IncludesStreamSize()
    {
        var stream = new PdfStream(new byte[] { 1, 2, 3 });

        var result = stream.ToString();

        result.Should().Contain("3 bytes");
    }

    [Fact]
    public void Constructor_WithData_MarkAsUncompressed()
    {
        var data = new byte[] { 1, 2, 3 };
        var stream = new PdfStream(data);

        stream.DecodedData.Should().Equal(data);
    }
}
