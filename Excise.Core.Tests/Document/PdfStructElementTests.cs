using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Unit tests for the <see cref="PdfStructElement"/> model — its accessor
/// properties (Alt/ActualText/Lang/Page/RawDictionary) and ToString, which the
/// structure-tree parser doesn't populate for every document.
/// </summary>
public class PdfStructElementTests
{
    [Fact]
    public void AllProperties_AreExposed_AndToStringSummarizes()
    {
        var child = new PdfStructElement("/Span");
        var raw = new PdfDictionary();
        raw["S"] = new PdfName("Figure");

        var el = new PdfStructElement(
            type: "/Figure",
            altText: "a cat",
            actualText: "actual cat",
            language: "en",
            pageNumber: 3,
            children: new[] { child },
            markedContentIds: new[] { 4, 5 },
            rawDictionary: raw);

        el.Type.Should().Be("/Figure");
        el.AltText.Should().Be("a cat");
        el.ActualText.Should().Be("actual cat");
        el.Language.Should().Be("en");
        el.PageNumber.Should().Be(3);
        el.Children.Should().ContainSingle();
        el.MarkedContentIds.Should().Equal(4, 5);
        el.RawDictionary.Should().BeSameAs(raw);

        var s = el.ToString();
        s.Should().Contain("Type=/Figure");
        s.Should().Contain("Alt=a cat");
        s.Should().Contain("ActualText=actual cat");
        s.Should().Contain("MCIDs=[4,5]");
        s.Should().Contain("Children=1");
    }

    [Fact]
    public void Defaults_AreEmptyAndNonNull()
    {
        var el = new PdfStructElement("/P");

        el.AltText.Should().BeNull();
        el.ActualText.Should().BeNull();
        el.Language.Should().BeNull();
        el.PageNumber.Should().BeNull();
        el.Children.Should().BeEmpty();
        el.MarkedContentIds.Should().BeEmpty();
        el.RawDictionary.Should().NotBeNull();

        // ToString with no optional parts present.
        el.ToString().Should().Be("StructElement(Type=/P)");
    }
}
