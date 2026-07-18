using AwesomeAssertions;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfArrayTests
{
    [Fact]
    public void Constructor_Empty_CreatesEmptyArray()
    {
        var array = new PdfArray();

        array.Count.Should().Be(0);
        array.ObjectType.Should().Be(PdfObjectType.Array);
    }

    [Fact]
    public void Constructor_WithEnumerable_PopulatesArray()
    {
        var items = new[] { (PdfObject)new PdfInteger(1), new PdfInteger(2) };

        var array = new PdfArray(items);

        array.Count.Should().Be(2);
    }

    [Fact]
    public void Constructor_WithParams_PopulatesArray()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2), new PdfInteger(3));

        array.Count.Should().Be(3);
    }

    [Fact]
    public void Indexer_Get_ReturnsItemAtIndex()
    {
        var array = new PdfArray(new PdfInteger(42));

        var result = array[0];

        result.Should().Be(new PdfInteger(42));
    }

    [Fact]
    public void Indexer_Set_UpdatesItemAtIndex()
    {
        var array = new PdfArray(new PdfInteger(1));
        var newValue = new PdfInteger(99);

        array[0] = newValue;

        array[0].Should().Be(newValue);
    }

    [Fact]
    public void Indexer_Set_WithNull_SetsToPdfNull()
    {
        var array = new PdfArray(new PdfInteger(1));

        // The indexer's runtime contract converts null → PdfNull.Instance,
        // but its signature is non-nullable; null! silences the warning while
        // exercising that conversion.
        array[0] = null!;

        array[0].Should().Be(PdfNull.Instance);
    }

    [Fact]
    public void Get_Generic_ReturnsCastedItem()
    {
        var array = new PdfArray(new PdfInteger(42));

        var result = array.Get<PdfInteger>(0);

        result.Should().BeOfType<PdfInteger>();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Get_Generic_WithWrongType_ThrowsInvalidCastException()
    {
        var array = new PdfArray(new PdfName("Type"));

        var action = () => array.Get<PdfInteger>(0);

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void TryGet_Generic_ReturnsCastedItem()
    {
        var array = new PdfArray(new PdfInteger(42));

        var result = array.TryGet<PdfInteger>(0);

        result.Should().NotBeNull();
        result!.Value.Should().Be(42);
    }

    [Fact]
    public void TryGet_Generic_WithWrongType_ReturnsNull()
    {
        var array = new PdfArray(new PdfName("Type"));

        var result = array.TryGet<PdfInteger>(0);

        result.Should().BeNull();
    }

    [Fact]
    public void TryGet_Generic_WithInvalidIndex_ReturnsNull()
    {
        var array = new PdfArray();

        var result = array.TryGet<PdfInteger>(99);

        result.Should().BeNull();
    }

    [Fact]
    public void GetNumber_ReturnsNumericValue()
    {
        var array = new PdfArray(new PdfReal(3.14));

        var result = array.GetNumber(0);

        result.Should().BeApproximately(3.14, 0.001);
    }

    [Fact]
    public void GetNumber_WithInteger_ReturnsAsDouble()
    {
        var array = new PdfArray(new PdfInteger(42));

        var result = array.GetNumber(0);

        result.Should().Be(42.0);
    }

    [Fact]
    public void GetInt_ReturnsIntegerValue()
    {
        var array = new PdfArray(new PdfInteger(42));

        var result = array.GetInt(0);

        result.Should().Be(42);
    }

    [Fact]
    public void GetInt_WithReal_ReturnsAsInteger()
    {
        var array = new PdfArray(new PdfReal(42.7));

        var result = array.GetInt(0);

        result.Should().Be(42);
    }

    [Fact]
    public void GetString_ReturnsStringValue()
    {
        var array = new PdfArray(new PdfString("Hello"));

        var result = array.GetString(0);

        result.Should().Be("Hello");
    }

    [Fact]
    public void GetString_WithNonString_ThrowsInvalidCastException()
    {
        var array = new PdfArray(new PdfInteger(42));

        var action = () => array.GetString(0);

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void GetName_ReturnsNameValue()
    {
        var array = new PdfArray(new PdfName("Type"));

        var result = array.GetName(0);

        result.Should().Be("Type");
    }

    [Fact]
    public void GetName_WithNonName_ThrowsInvalidCastException()
    {
        var array = new PdfArray(new PdfInteger(42));

        var action = () => array.GetName(0);

        action.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Add_WithPdfObject_AddsItem()
    {
        var array = new PdfArray();
        var item = (PdfObject)new PdfInteger(42);

        array.Add(item);

        array.Count.Should().Be(1);
        array[0].Should().Be(item);
    }

    [Fact]
    public void Add_WithNullObject_AddsPdfNull()
    {
        var array = new PdfArray();

        array.Add((PdfObject)PdfNull.Instance);

        array.Count.Should().Be(1);
        array[0].Should().Be(PdfNull.Instance);
    }

    [Fact]
    public void Add_WithInteger_AddsAsInteger()
    {
        var array = new PdfArray();

        array.Add(42);

        array.Count.Should().Be(1);
        array[0].Should().BeOfType<PdfInteger>();
    }

    [Fact]
    public void Add_WithDouble_AddsAsReal()
    {
        var array = new PdfArray();

        array.Add(3.14);

        array.Count.Should().Be(1);
        array[0].Should().BeOfType<PdfReal>();
    }

    [Fact]
    public void Add_WithString_AddsAsString()
    {
        var array = new PdfArray();

        array.Add("Hello");

        array.Count.Should().Be(1);
        array[0].Should().BeOfType<PdfString>();
    }

    [Fact]
    public void Insert_AddsItemAtIndex()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(3));
        var item = new PdfInteger(2);

        array.Insert(1, item);

        array.Count.Should().Be(3);
        array[1].Should().Be(item);
    }

    [Fact]
    public void Insert_WithNullObject_InsertsPdfNull()
    {
        var array = new PdfArray(new PdfInteger(1));

        array.Insert(0, (PdfObject)PdfNull.Instance);

        array.Count.Should().Be(2);
        array[0].Should().Be(PdfNull.Instance);
    }

    [Fact]
    public void Remove_WithItem_RemovesFirstOccurrence()
    {
        var item = new PdfInteger(2);
        var array = new PdfArray(new PdfInteger(1), item, new PdfInteger(3));

        var result = array.Remove(item);

        result.Should().BeTrue();
        array.Count.Should().Be(2);
    }

    [Fact]
    public void Remove_WithNonExistentItem_ReturnsFalse()
    {
        var array = new PdfArray(new PdfInteger(1));

        var result = array.Remove(new PdfInteger(99));

        result.Should().BeFalse();
    }

    [Fact]
    public void RemoveAt_RemovesItemAtIndex()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2), new PdfInteger(3));

        array.RemoveAt(1);

        array.Count.Should().Be(2);
        array[1].Should().Be(new PdfInteger(3));
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2), new PdfInteger(3));

        array.Clear();

        array.Count.Should().Be(0);
    }

    [Fact]
    public void Contains_WithExistingItem_ReturnsTrue()
    {
        var item = new PdfInteger(2);
        var array = new PdfArray(new PdfInteger(1), item);

        var result = array.Contains(item);

        result.Should().BeTrue();
    }

    [Fact]
    public void Contains_WithNonExistentItem_ReturnsFalse()
    {
        var array = new PdfArray(new PdfInteger(1));

        var result = array.Contains(new PdfInteger(99));

        result.Should().BeFalse();
    }

    [Fact]
    public void IndexOf_ReturnsItemIndex()
    {
        var item = new PdfInteger(2);
        var array = new PdfArray(new PdfInteger(1), item, new PdfInteger(3));

        var result = array.IndexOf(item);

        result.Should().Be(1);
    }

    [Fact]
    public void IndexOf_WithNonExistentItem_ReturnsNegativeOne()
    {
        var array = new PdfArray(new PdfInteger(1));

        var result = array.IndexOf(new PdfInteger(99));

        result.Should().Be(-1);
    }

    [Fact]
    public void CopyTo_CopiesItemsToArray()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2));
        var target = new PdfObject[2];

        array.CopyTo(target, 0);

        target.Should().Contain(new PdfInteger(1));
        target.Should().Contain(new PdfInteger(2));
    }

    [Fact]
    public void CopyTo_WithOffset_CopiesAtOffset()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2));
        var target = new PdfObject[3];
        target[0] = new PdfInteger(99);

        array.CopyTo(target, 1);

        target[0].Should().Be(new PdfInteger(99));
        target[1].Should().Be(new PdfInteger(1));
        target[2].Should().Be(new PdfInteger(2));
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllItems()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2), new PdfInteger(3));

        var items = array.ToList();

        items.Should().HaveCount(3);
    }

    [Fact]
    public void ToDoubleArray_ReturnsNumberArray()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfReal(2.5), new PdfInteger(3));

        var result = array.ToDoubleArray();

        result.Should().Equal(1.0, 2.5, 3.0);
    }

    [Fact]
    public void ToIntArray_ReturnsIntegerArray()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfReal(2.7), new PdfInteger(3));

        var result = array.ToIntArray();

        result.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void FromRectangle_CreatesArrayWithCoordinates()
    {
        var array = PdfArray.FromRectangle(10, 20, 100, 200);

        array.Count.Should().Be(4);
        array.ToDoubleArray().Should().Equal(10.0, 20.0, 100.0, 200.0);
    }

    [Fact]
    public void FromMatrix_CreatesArrayWithMatrixValues()
    {
        var array = PdfArray.FromMatrix(1, 0, 0, 1, 50, 100);

        array.Count.Should().Be(6);
        array.ToDoubleArray().Should().Equal(1.0, 0.0, 0.0, 1.0, 50.0, 100.0);
    }

    [Fact]
    public void ObjectType_ReturnsArray()
    {
        var array = new PdfArray();

        array.ObjectType.Should().Be(PdfObjectType.Array);
    }

    [Fact]
    public void IsReadOnly_ReturnsFalse()
    {
        var array = new PdfArray();

        array.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2));

        var result = array.ToString();

        result.Should().Contain("[");
        result.Should().Contain("]");
    }

    [Fact]
    public void Constructor_WithEmptyEnumerable_CreatesEmptyArray()
    {
        var array = new PdfArray(Enumerable.Empty<PdfObject>());

        array.Count.Should().Be(0);
    }

    [Fact]
    public void Indexer_OutOfRange_ThrowsIndexOutOfRangeException()
    {
        var array = new PdfArray(new PdfInteger(1));

        var action = () => _ = array[99];

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RemoveAt_OutOfRange_ThrowsIndexOutOfRangeException()
    {
        var array = new PdfArray(new PdfInteger(1));

        var action = () => array.RemoveAt(99);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Insert_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var array = new PdfArray();

        var action = () => array.Insert(99, new PdfInteger(1));

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
