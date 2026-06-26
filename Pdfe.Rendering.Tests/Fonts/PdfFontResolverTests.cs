using AwesomeAssertions;
using System.Text;
using Pdfe.Core.Primitives;
using Pdfe.Rendering.Fonts;
using Xunit;

namespace Pdfe.Rendering.Tests.Fonts;

public class PdfFontResolverTests
{
    [Fact]
    public void Resolve_UsesEncodingDictionaryBaseEncoding()
    {
        var encoding = new PdfDictionary
        {
            ["BaseEncoding"] = new PdfName("MacRomanEncoding"),
            ["Differences"] = new PdfArray(new PdfInteger(65), new PdfName("A"))
        };
        var font = new PdfDictionary
        {
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName("TestSubset"),
            ["Encoding"] = encoding
        };

        var resolved = PdfFontResolver.Resolve("F1", font);

        resolved.ResourceName.Should().Be("F1");
        resolved.Subtype.Should().Be("Type1");
        resolved.BaseFont.Should().Be("TestSubset");
        resolved.EncodingName.Should().Be("MacRomanEncoding");
        resolved.EncodingDictionary.Should().BeSameAs(encoding);
    }

    [Fact]
    public void Resolve_NameEncodingOverridesDictionaryBaseEncodingFallback()
    {
        var font = new PdfDictionary
        {
            ["Subtype"] = new PdfName("Type3"),
            ["Encoding"] = new PdfName("WinAnsiEncoding")
        };

        var resolved = PdfFontResolver.Resolve("F2", font);

        resolved.IsType3.Should().BeTrue();
        resolved.EncodingName.Should().Be("WinAnsiEncoding");
        resolved.EncodingDictionary.Should().BeNull();
    }

    [Fact]
    public void Resolve_CapturesWidthsFirstCharAndDescriptorMissingWidth()
    {
        var descriptor = new PdfDictionary
        {
            ["MissingWidth"] = new PdfInteger(375)
        };
        var font = new PdfDictionary
        {
            ["Subtype"] = new PdfName("Type1"),
            ["FirstChar"] = new PdfInteger(32),
            ["Widths"] = new PdfArray(new PdfInteger(250), new PdfInteger(500), new PdfInteger(750)),
            ["FontDescriptor"] = descriptor
        };

        var resolved = PdfFontResolver.Resolve("F3", font);

        resolved.FirstChar.Should().Be(32);
        resolved.Widths.Should().Equal(250f, 500f, 750f);
        resolved.MissingWidth.Should().Be(375f);
        resolved.FontDescriptor.Should().BeSameAs(descriptor);
    }

    [Fact]
    public void Resolve_ParsesToUnicodeCMapIntoResolvedModel()
    {
        var cmap = Encoding.ASCII.GetBytes("""
            1 begincodespacerange
            <00> <ff>
            endcodespacerange
            1 beginbfchar
            <41> <0041>
            endbfchar
            """);
        var font = new PdfDictionary
        {
            ["Subtype"] = new PdfName("Type1"),
            ["ToUnicode"] = new PdfStream(cmap)
        };

        var resolved = PdfFontResolver.Resolve("F4", font);

        resolved.ToUnicodeMap.Should().NotBeNull();
        resolved.ToUnicodeMap![0x41].Should().Be("A");
    }

    [Fact]
    public void Resolve_DefaultsToHelveticaWinAnsiAndNonCompositeFlagsWhenFontMissing()
    {
        var resolved = PdfFontResolver.Resolve("Missing", null);

        resolved.BaseFont.Should().Be("Helvetica");
        resolved.EncodingName.Should().Be("WinAnsiEncoding");
        resolved.IsType0.Should().BeFalse();
        resolved.IsType3.Should().BeFalse();
        resolved.Widths.Should().BeNull();
    }
}
