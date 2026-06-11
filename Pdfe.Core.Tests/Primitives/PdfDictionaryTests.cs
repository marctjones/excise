using AwesomeAssertions;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Primitives;

public class PdfDictionaryTests
{
    [Fact]
    public void Constructor_Empty_CreatesEmptyDictionary()
    {
        var dict = new PdfDictionary();

        dict.Count.Should().Be(0);
        dict.ObjectType.Should().Be(PdfObjectType.Dictionary);
    }

    [Fact]
    public void Constructor_WithEntries_PopulatesDictionary()
    {
        var entries = new[]
        {
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Type"), new PdfName("Page")),
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Count"), new PdfInteger(5))
        };

        var dict = new PdfDictionary(entries);

        dict.Count.Should().Be(2);
        dict["Type"].Should().BeOfType<PdfName>();
        dict["Count"].Should().BeOfType<PdfInteger>();
    }

    [Fact]
    public void Indexer_Get_WithValidKey_ReturnsValue()
    {
        var dict = new PdfDictionary();
        var name = new PdfName("Type");
        var value = new PdfName("Page");
        dict[name] = value;

        var result = dict[name];

        result.Should().Be(value);
    }

    [Fact]
    public void Indexer_Get_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();
        var missingKey = new PdfName("Missing");

        var action = () => _ = dict[missingKey];

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Indexer_Set_AddsPdfNameKey()
    {
        var dict = new PdfDictionary();
        var key = new PdfName("Type");
        var value = new PdfName("Page");

        dict[key] = value;

        dict.Count.Should().Be(1);
        dict[key].Should().Be(value);
    }

    [Fact]
    public void Indexer_Set_WithStringKey_AddEntry()
    {
        var dict = new PdfDictionary();
        var value = new PdfInteger(42);

        dict["Count"] = value;

        dict.Count.Should().Be(1);
        dict["Count"].Should().Be(value);
    }

    [Fact]
    public void Indexer_Set_WithNull_RemovesKey()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");
        dict.Count.Should().Be(1);

        dict["Type"] = null!;

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Indexer_Set_WithPdfNull_RemovesKey()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        dict["Type"] = PdfNull.Instance;

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void ContainsKey_WithPdfName_ReturnsTrueForExistingKey()
    {
        var dict = new PdfDictionary();
        var key = new PdfName("Type");
        dict[key] = new PdfName("Page");

        var result = dict.ContainsKey(key);

        result.Should().BeTrue();
    }

    [Fact]
    public void ContainsKey_WithPdfName_ReturnsFalseForMissingKey()
    {
        var dict = new PdfDictionary();

        var result = dict.ContainsKey(new PdfName("Missing"));

        result.Should().BeFalse();
    }

    [Fact]
    public void ContainsKey_WithString_ReturnsTrueForExistingKey()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.ContainsKey("Type");

        result.Should().BeTrue();
    }

    [Fact]
    public void ContainsKey_WithString_ReturnsFalseForMissingKey()
    {
        var dict = new PdfDictionary();

        var result = dict.ContainsKey("Missing");

        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetValue_WithPdfName_ReturnsValueForExistingKey()
    {
        var dict = new PdfDictionary();
        var value = new PdfName("Page");
        dict["Type"] = value;

        var result = dict.TryGetValue(new PdfName("Type"), out var retrieved);

        result.Should().BeTrue();
        retrieved.Should().Be(value);
    }

    [Fact]
    public void TryGetValue_WithPdfName_ReturnsFalseForMissingKey()
    {
        var dict = new PdfDictionary();

        var result = dict.TryGetValue(new PdfName("Missing"), out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetValue_WithString_ReturnsValueForExistingKey()
    {
        var dict = new PdfDictionary();
        var value = new PdfInteger(42);
        dict["Count"] = value;

        var result = dict.TryGetValue("Count", out var retrieved);

        result.Should().BeTrue();
        retrieved.Should().Be(value);
    }

    [Fact]
    public void GetOptional_WithExistingKey_ReturnsValue()
    {
        var dict = new PdfDictionary();
        var value = new PdfName("Page");
        dict["Type"] = value;

        var result = dict.GetOptional("Type");

        result.Should().Be(value);
    }

    [Fact]
    public void GetOptional_WithMissingKey_ReturnsNull()
    {
        var dict = new PdfDictionary();

        var result = dict.GetOptional("Missing");

        result.Should().BeNull();
    }

    [Fact]
    public void Get_Generic_WithExistingKey_ReturnsCastedValue()
    {
        var dict = new PdfDictionary();
        var value = new PdfName("Page");
        dict["Type"] = value;

        var result = dict.Get<PdfName>("Type");

        result.Should().Be(value);
    }

    [Fact]
    public void Get_Generic_WithWrongType_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var action = () => dict.Get<PdfInteger>("Type");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void TryGet_Generic_WithExistingKey_ReturnsCastedValue()
    {
        var dict = new PdfDictionary();
        var value = new PdfName("Page");
        dict["Type"] = value;

        var result = dict.TryGet<PdfName>("Type");

        result.Should().Be(value);
    }

    [Fact]
    public void TryGet_Generic_WithWrongType_ReturnsNull()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.TryGet<PdfInteger>("Type");

        result.Should().BeNull();
    }

    [Fact]
    public void GetNumber_WithNumericValue_ReturnsDouble()
    {
        var dict = new PdfDictionary();
        dict["Value"] = new PdfReal(3.14);

        var result = dict.GetNumber("Value");

        result.Should().BeApproximately(3.14, 0.001);
    }

    [Fact]
    public void GetNumber_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetNumber("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetNumber_WithDefault_ReturnDefaultForMissingKey()
    {
        var dict = new PdfDictionary();

        var result = dict.GetNumber("Missing", 99.0);

        result.Should().Be(99.0);
    }

    [Fact]
    public void GetInt_WithIntegerValue_ReturnsInt()
    {
        var dict = new PdfDictionary();
        dict["Count"] = new PdfInteger(42);

        var result = dict.GetInt("Count");

        result.Should().Be(42);
    }

    [Fact]
    public void GetInt_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetInt("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetInt_WithDefault_ReturnDefaultForMissingKey()
    {
        var dict = new PdfDictionary();

        var result = dict.GetInt("Missing", 99);

        result.Should().Be(99);
    }

    [Fact]
    public void GetString_WithStringValue_ReturnsString()
    {
        var dict = new PdfDictionary();
        dict["Title"] = new PdfString("Hello");

        var result = dict.GetString("Title");

        result.Should().Be("Hello");
    }

    [Fact]
    public void GetString_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetString("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetStringOrNull_WithStringValue_ReturnsString()
    {
        var dict = new PdfDictionary();
        dict["Title"] = new PdfString("Hello");

        var result = dict.GetStringOrNull("Title");

        result.Should().Be("Hello");
    }

    [Fact]
    public void GetStringOrNull_WithMissingKey_ReturnsNull()
    {
        var dict = new PdfDictionary();

        var result = dict.GetStringOrNull("Missing");

        result.Should().BeNull();
    }

    [Fact]
    public void TryGetString_WithStringValue_ReturnsTrueAndValue()
    {
        var dict = new PdfDictionary();
        dict["Title"] = new PdfString("Hello");

        var result = dict.TryGetString("Title", out var value);

        result.Should().BeTrue();
        value.Should().Be("Hello");
    }

    [Fact]
    public void TryGetString_WithMissingKey_ReturnsFalse()
    {
        var dict = new PdfDictionary();

        var result = dict.TryGetString("Missing", out var value);

        result.Should().BeFalse();
        value.Should().BeEmpty();
    }

    [Fact]
    public void TryGetString_WithWrongType_ReturnsFalse()
    {
        var dict = new PdfDictionary();
        dict["Title"] = new PdfName("NotAString");

        var result = dict.TryGetString("Title", out var value);

        result.Should().BeFalse();
        value.Should().BeEmpty();
    }

    [Fact]
    public void GetName_WithNameValue_ReturnsNameString()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.GetName("Type");

        result.Should().Be("Page");
    }

    [Fact]
    public void GetName_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetName("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetNameOrNull_WithNameValue_ReturnsNameString()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.GetNameOrNull("Type");

        result.Should().Be("Page");
    }

    [Fact]
    public void GetNameOrNull_WithMissingKey_ReturnsNull()
    {
        var dict = new PdfDictionary();

        var result = dict.GetNameOrNull("Missing");

        result.Should().BeNull();
    }

    [Fact]
    public void GetBool_WithBooleanValue_ReturnsBoolean()
    {
        var dict = new PdfDictionary();
        dict["IsActive"] = PdfBoolean.True;

        var result = dict.GetBool("IsActive");

        result.Should().BeTrue();
    }

    [Fact]
    public void GetBool_WithMissingKey_ReturnsDefaultValue()
    {
        var dict = new PdfDictionary();

        var result = dict.GetBool("Missing", false);

        result.Should().BeFalse();
    }

    [Fact]
    public void GetArray_WithArrayValue_ReturnsArray()
    {
        var dict = new PdfDictionary();
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2));
        dict["Values"] = array;

        var result = dict.GetArray("Values");

        result.Should().BeSameAs(array);
    }

    [Fact]
    public void GetArray_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetArray("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetArrayOrNull_WithArrayValue_ReturnsArray()
    {
        var dict = new PdfDictionary();
        var array = new PdfArray();
        dict["Values"] = array;

        var result = dict.GetArrayOrNull("Values");

        result.Should().BeSameAs(array);
    }

    [Fact]
    public void GetArrayOrNull_WithMissingKey_ReturnsNull()
    {
        var dict = new PdfDictionary();

        var result = dict.GetArrayOrNull("Missing");

        result.Should().BeNull();
    }

    [Fact]
    public void TryGetArray_WithArrayValue_ReturnsTrueAndValue()
    {
        var dict = new PdfDictionary();
        var array = new PdfArray(new PdfInteger(1), new PdfInteger(2));
        dict["Values"] = array;

        var result = dict.TryGetArray("Values", out var value);

        result.Should().BeTrue();
        value.Should().BeSameAs(array);
    }

    [Fact]
    public void TryGetArray_WithMissingKey_ReturnsFalse()
    {
        var dict = new PdfDictionary();

        var result = dict.TryGetArray("Missing", out var value);

        result.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGetArray_WithWrongType_ReturnsFalse()
    {
        var dict = new PdfDictionary();
        dict["Values"] = new PdfString("NotAnArray");

        var result = dict.TryGetArray("Values", out var value);

        result.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void GetDictionary_WithDictionaryValue_ReturnsDictionary()
    {
        var dict = new PdfDictionary();
        var innerDict = new PdfDictionary();
        dict["Info"] = innerDict;

        var result = dict.GetDictionary("Info");

        result.Should().BeSameAs(innerDict);
    }

    [Fact]
    public void GetDictionary_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetDictionary("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetDictionaryOrNull_WithDictionaryValue_ReturnsDictionary()
    {
        var dict = new PdfDictionary();
        var innerDict = new PdfDictionary();
        dict["Info"] = innerDict;

        var result = dict.GetDictionaryOrNull("Info");

        result.Should().BeSameAs(innerDict);
    }

    [Fact]
    public void GetDictionaryOrNull_WithMissingKey_ReturnsNull()
    {
        var dict = new PdfDictionary();

        var result = dict.GetDictionaryOrNull("Missing");

        result.Should().BeNull();
    }

    [Fact]
    public void GetReference_WithReferenceValue_ReturnsReference()
    {
        var dict = new PdfDictionary();
        var reference = new PdfReference(5, 0);
        dict["Parent"] = reference;

        var result = dict.GetReference("Parent");

        result.Should().Be(reference);
    }

    [Fact]
    public void GetReference_WithMissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new PdfDictionary();

        var action = () => dict.GetReference("Missing");

        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetReferenceOrNull_WithReferenceValue_ReturnsReference()
    {
        var dict = new PdfDictionary();
        var reference = new PdfReference(5, 0);
        dict["Parent"] = reference;

        var result = dict.GetReferenceOrNull("Parent");

        result.Should().Be(reference);
    }

    [Fact]
    public void GetReferenceOrNull_WithMissingKey_ReturnsNull()
    {
        var dict = new PdfDictionary();

        var result = dict.GetReferenceOrNull("Missing");

        result.Should().BeNull();
    }

    [Fact]
    public void Add_WithPdfName_AddsEntry()
    {
        var dict = new PdfDictionary();
        var key = new PdfName("Type");
        var value = new PdfName("Page");

        dict.Add(key, value);

        dict.Count.Should().Be(1);
        dict[key].Should().Be(value);
    }

    [Fact]
    public void Add_WithString_AddsEntry()
    {
        var dict = new PdfDictionary();
        var value = new PdfInteger(42);

        dict.Add("Count", value);

        dict.Count.Should().Be(1);
        dict["Count"].Should().Be(value);
    }

    [Fact]
    public void Add_WithNull_DoesNotAdd()
    {
        var dict = new PdfDictionary();

        dict.Add(new PdfName("Type"), null!);

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Add_WithPdfNull_DoesNotAdd()
    {
        var dict = new PdfDictionary();

        dict.Add(new PdfName("Type"), PdfNull.Instance);

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Set_WithNewKey_AddsEntry()
    {
        var dict = new PdfDictionary();

        dict.Set("Type", new PdfName("Page"));

        dict.Count.Should().Be(1);
    }

    [Fact]
    public void Set_WithExistingKey_UpdatesEntry()
    {
        var dict = new PdfDictionary();
        dict.Set("Type", new PdfName("Page"));

        dict.Set("Type", new PdfName("XObject"));

        dict["Type"].Should().BeOfType<PdfName>().Which.Value.Should().Be("XObject");
    }

    [Fact]
    public void Set_WithNull_RemovesEntry()
    {
        var dict = new PdfDictionary();
        dict.Set("Type", new PdfName("Page"));

        dict.Set("Type", null!);

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void SetName_SetsNameEntry()
    {
        var dict = new PdfDictionary();

        dict.SetName("Type", "Page");

        dict["Type"].Should().BeOfType<PdfName>().Which.Value.Should().Be("Page");
    }

    [Fact]
    public void SetString_SetsStringEntry()
    {
        var dict = new PdfDictionary();

        dict.SetString("Title", "My Document");

        dict["Title"].Should().BeOfType<PdfString>().Which.Value.Should().Be("My Document");
    }

    [Fact]
    public void SetNumber_SetsRealEntry()
    {
        var dict = new PdfDictionary();

        dict.SetNumber("Value", 3.14);

        dict["Value"].Should().BeOfType<PdfReal>();
    }

    [Fact]
    public void SetInt_SetsIntegerEntry()
    {
        var dict = new PdfDictionary();

        dict.SetInt("Count", 42);

        dict["Count"].Should().BeOfType<PdfInteger>().Which.Value.Should().Be(42);
    }

    [Fact]
    public void SetBool_SetsBooleanEntry()
    {
        var dict = new PdfDictionary();

        dict.SetBool("IsActive", true);

        dict["IsActive"].Should().Be(PdfBoolean.True);
    }

    [Fact]
    public void Remove_WithPdfName_RemovesEntry()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.Remove(new PdfName("Type"));

        result.Should().BeTrue();
        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_WithPdfName_ReturnsFalseForMissing()
    {
        var dict = new PdfDictionary();

        var result = dict.Remove(new PdfName("Missing"));

        result.Should().BeFalse();
    }

    [Fact]
    public void Remove_WithString_RemovesEntry()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.Remove("Type");

        result.Should().BeTrue();
        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_WithString_ReturnsFalseForMissing()
    {
        var dict = new PdfDictionary();

        var result = dict.Remove("Missing");

        result.Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");
        dict["Count"] = new PdfInteger(5);

        dict.Clear();

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Keys_ReturnsAllKeys()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");
        dict["Count"] = new PdfInteger(5);

        var keys = dict.Keys.Select(k => k.Value).ToList();

        keys.Should().Contain("Type");
        keys.Should().Contain("Count");
    }

    [Fact]
    public void Values_ReturnsAllValues()
    {
        var dict = new PdfDictionary();
        var value1 = new PdfName("Page");
        var value2 = new PdfInteger(5);
        dict["Type"] = value1;
        dict["Count"] = value2;

        var values = dict.Values.ToList();

        values.Should().Contain(value1);
        values.Should().Contain(value2);
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllEntries()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");
        dict["Count"] = new PdfInteger(5);

        var entries = dict.ToList();

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Key.Value == "Type");
        entries.Should().Contain(e => e.Key.Value == "Count");
    }

    [Fact]
    public void Clone_CreatesShallowCopy()
    {
        var dict = new PdfDictionary();
        var innerArray = new PdfArray();
        dict["Items"] = innerArray;

        var clone = dict.Clone();

        clone.Count.Should().Be(1);
        clone["Items"].Should().BeSameAs(innerArray);
    }

    [Fact]
    public void Clone_IsIndependentCopy()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var clone = dict.Clone();
        clone["Count"] = new PdfInteger(5);

        dict.Count.Should().Be(1);
        clone.Count.Should().Be(2);
    }

    [Fact]
    public void ObjectType_ReturnsDictionary()
    {
        var dict = new PdfDictionary();

        dict.ObjectType.Should().Be(PdfObjectType.Dictionary);
    }

    [Fact]
    public void IsReadOnly_ReturnsFalse()
    {
        var dict = new PdfDictionary();

        dict.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");

        var result = dict.ToString();

        result.Should().Contain("<<");
        result.Should().Contain(">>");
        result.Should().Contain("/Type");
    }

    [Fact]
    public void InterfaceImplementation_Contains_WithKeyValuePair()
    {
        var dict = new PdfDictionary();
        var kvp = new KeyValuePair<PdfName, PdfObject>(new PdfName("Type"), new PdfName("Page"));
        dict["Type"] = new PdfName("Page");

        bool result = false;
        if (dict is ICollection<KeyValuePair<PdfName, PdfObject>> coll)
        {
            result = coll.Contains(kvp);
        }

        result.Should().BeTrue();
    }

    [Fact]
    public void InterfaceImplementation_CopyTo()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Page");
        dict["Count"] = new PdfInteger(5);
        var array = new KeyValuePair<PdfName, PdfObject>[2];

        if (dict is ICollection<KeyValuePair<PdfName, PdfObject>> coll)
        {
            coll.CopyTo(array, 0);
        }

        array.Length.Should().Be(2);
        var found = array.Any(kvp => kvp.Key.Value == "Type");
        found.Should().BeTrue();
    }
}
