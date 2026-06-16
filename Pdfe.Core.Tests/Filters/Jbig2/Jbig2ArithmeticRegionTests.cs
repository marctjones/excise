using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

public class Jbig2ArithmeticRegionTests
{
    [Fact]
    public void SymbolDictionary_ArithmeticNoRefinement_DecodesDirectSymbolAndExportFlags()
    {
        var segment = new Jbig2SymbolDictionarySegment(
            IsHuffmanEncoded: false,
            UseRefinementAggregation: false,
            SdHuffDecodeHeightSelection: 0,
            SdHuffDecodeWidthSelection: 0,
            SdHuffBmSizeSelection: 0,
            SdHuffAggInstanceSelection: 0,
            IsCodingContextUsed: false,
            IsCodingContextRetained: false,
            SdTemplate: 0,
            SdrTemplate: 0,
            AdaptiveTemplatePixels: DefaultTemplate0AdaptivePixels(),
            RefinementAdaptiveTemplatePixels: Array.Empty<Jbig2AdaptiveTemplatePixel>(),
            ExportedSymbolCount: 1,
            NewSymbolCount: 1,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        var decoder = new ScriptedArithmeticDecoder(
            false, false, false, true,  // IADH: 1
            false, false, false, true,  // IADW: 1
            true,                       // 1x1 symbol bitmap pixel
            true, false, false, false,  // IADW: OOB ends height class
            false, false, false, false, // IAEX: 0 false flags
            false, false, false, true); // IAEX: 1 true flag

        var decoded = Jbig2SymbolDictionaryDecoder.DecodeArithmeticForTest(
            segment,
            decoder,
            Array.Empty<Jbig2Bitmap>());

        decoded.NewSymbols.Should().HaveCount(1);
        decoded.ExportedSymbols.Should().HaveCount(1);
        decoded.NewSymbols[0].Width.Should().Be(1);
        decoded.NewSymbols[0].Height.Should().Be(1);
        decoded.NewSymbols[0].GetPixel(0, 0).Should().BeTrue();
        decoded.ExportedSymbols[0].Should().BeSameAs(decoded.NewSymbols[0]);
    }

    [Fact]
    public void SymbolDictionary_ArithmeticSingleRefinement_RefinesImportedSymbolAndExportsNewSymbol()
    {
        var importedSymbol = new Jbig2Bitmap(1, 1);
        importedSymbol.SetPixel(0, 0, true);
        var segment = new Jbig2SymbolDictionarySegment(
            IsHuffmanEncoded: false,
            UseRefinementAggregation: true,
            SdHuffDecodeHeightSelection: 0,
            SdHuffDecodeWidthSelection: 0,
            SdHuffBmSizeSelection: 0,
            SdHuffAggInstanceSelection: 0,
            IsCodingContextUsed: false,
            IsCodingContextRetained: false,
            SdTemplate: 0,
            SdrTemplate: 1,
            AdaptiveTemplatePixels: DefaultTemplate0AdaptivePixels(),
            RefinementAdaptiveTemplatePixels: Array.Empty<Jbig2AdaptiveTemplatePixel>(),
            ExportedSymbolCount: 1,
            NewSymbolCount: 1,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        var decoder = new ScriptedArithmeticDecoder(
            false, false, false, true,  // IADH: 1
            false, false, false, true,  // IADW: 1
            false, false, false, true,  // IAAI: one refinement instance
            false,                      // IAID: imported symbol 0
            false, false, false, false, // IARDX: 0
            false, false, false, false, // IARDY: 0
            false,                      // refined bitmap pixel
            true, false, false, false,  // IADW: OOB ends height class
            false, false, false, true,  // IAEX: one false flag for imported symbol
            false, false, false, true); // IAEX: one true flag for new symbol

        var decoded = Jbig2SymbolDictionaryDecoder.DecodeArithmeticForTest(
            segment,
            decoder,
            new[] { importedSymbol });

        decoded.NewSymbols.Should().HaveCount(1);
        decoded.ExportedSymbols.Should().HaveCount(1);
        decoded.NewSymbols[0].GetPixel(0, 0).Should().BeFalse();
        decoded.ExportedSymbols[0].Should().BeSameAs(decoded.NewSymbols[0]);
    }

    [Fact]
    public void TextRegion_ArithmeticNoRefinement_PlacesSingleReferencedSymbol()
    {
        var symbol = new Jbig2Bitmap(1, 1);
        symbol.SetPixel(0, 0, true);
        var segment = new Jbig2TextRegionSegment(
            Region: new Jbig2RegionSegmentInformation(1, 1, 0, 0, Jbig2CombinationOperator.Replace),
            IsHuffmanEncoded: false,
            UseRefinement: false,
            LogSbStrips: 0,
            ReferenceCorner: 0,
            IsTransposed: false,
            CombinationOperator: Jbig2CombinationOperator.Or,
            DefaultPixel: 0,
            SbDsOffset: 0,
            SbrTemplate: 0,
            HuffmanFlags: null,
            RefinementAdaptiveTemplatePixels: Array.Empty<Jbig2AdaptiveTemplatePixel>(),
            DeclaredSymbolInstanceCount: 1,
            SymbolInstanceCount: 1,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        var decoder = new ScriptedArithmeticDecoder(
            false, false, false, false, // IADT initial strip T: 0
            false, false, false, false, // IADT delta T: 0
            false, false, false, false);// IAFS first S: 0

        var bitmap = Jbig2TextRegionDecoder.DecodeArithmeticForTest(
            segment,
            decoder,
            new[] { symbol });

        bitmap.Width.Should().Be(1);
        bitmap.Height.Should().Be(1);
        bitmap.GetPixel(0, 0).Should().BeTrue();
    }

    [Fact]
    public void TextRegion_ArithmeticRefinement_RefinesReferencedSymbolBeforePlacement()
    {
        var symbol = new Jbig2Bitmap(1, 1);
        symbol.SetPixel(0, 0, true);
        var segment = new Jbig2TextRegionSegment(
            Region: new Jbig2RegionSegmentInformation(1, 1, 0, 0, Jbig2CombinationOperator.Replace),
            IsHuffmanEncoded: false,
            UseRefinement: true,
            LogSbStrips: 0,
            ReferenceCorner: 0,
            IsTransposed: false,
            CombinationOperator: Jbig2CombinationOperator.Replace,
            DefaultPixel: 0,
            SbDsOffset: 0,
            SbrTemplate: 1,
            HuffmanFlags: null,
            RefinementAdaptiveTemplatePixels: Array.Empty<Jbig2AdaptiveTemplatePixel>(),
            DeclaredSymbolInstanceCount: 1,
            SymbolInstanceCount: 1,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        var decoder = new ScriptedArithmeticDecoder(
            false, false, false, false, // IADT initial strip T: 0
            false, false, false, false, // IADT delta T: 0
            false, false, false, false, // IAFS first S: 0
            false, false, false, true,  // IARI: refine this symbol
            false, false, false, false, // IARDW: 0
            false, false, false, false, // IARDH: 0
            false, false, false, false, // IARDX: 0
            false, false, false, false, // IARDY: 0
            false);                     // refined bitmap pixel

        var bitmap = Jbig2TextRegionDecoder.DecodeArithmeticForTest(
            segment,
            decoder,
            new[] { symbol });

        bitmap.Width.Should().Be(1);
        bitmap.Height.Should().Be(1);
        bitmap.GetPixel(0, 0).Should().BeFalse();
    }

    [Fact]
    public void TextRegion_HuffmanRefinement_ThrowsUntilRefinementBitmapByteScopingIsCorrect()
    {
        var symbol = new Jbig2Bitmap(1, 1);
        var segment = new Jbig2TextRegionSegment(
            Region: new Jbig2RegionSegmentInformation(1, 1, 0, 0, Jbig2CombinationOperator.Replace),
            IsHuffmanEncoded: true,
            UseRefinement: true,
            LogSbStrips: 0,
            ReferenceCorner: 0,
            IsTransposed: false,
            CombinationOperator: Jbig2CombinationOperator.Replace,
            DefaultPixel: 0,
            SbDsOffset: 0,
            SbrTemplate: 1,
            HuffmanFlags: new Jbig2TextRegionHuffmanFlags(0, 0, 0, 0, 0, 0, 0, 0),
            RefinementAdaptiveTemplatePixels: Array.Empty<Jbig2AdaptiveTemplatePixel>(),
            DeclaredSymbolInstanceCount: 1,
            SymbolInstanceCount: 1,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        var act = () => Jbig2TextRegionDecoder.Decode(
            segment,
            ReadOnlySpan<byte>.Empty,
            new[] { symbol });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Huffman refinement*");
    }

    [Fact]
    public void GenericRegion_Template0_UsesFullBitmapArithmeticContext()
    {
        var decoder = new ScriptedArithmeticDecoder(
            true, true, true, true,
            true, true, true, true,
            true, true, true, true);

        var bitmap = Jbig2ArithmeticGenericRegionDecoder.Decode(
            decoder,
            width: 4,
            height: 3,
            template: 0,
            DefaultTemplate0AdaptivePixels());

        bitmap.GetPixel(3, 2).Should().BeTrue();
        decoder.Contexts.Should().Contain(context => context >= 1024);
        decoder.Contexts.Should().OnlyContain(context => context < Jbig2ArithmeticGenericRegionDecoder.ContextCount);
    }

    [Theory]
    [InlineData(1, 3, -1)]
    [InlineData(2, 2, -1)]
    [InlineData(3, 2, -1)]
    public void GenericRegion_Templates1To3_DecodePixels(int template, sbyte atX, sbyte atY)
    {
        var decoder = new ScriptedArithmeticDecoder(
            true, true, true, true,
            true, true, true, true,
            true, true, true, true);

        var bitmap = Jbig2ArithmeticGenericRegionDecoder.Decode(
            decoder,
            width: 4,
            height: 3,
            template,
            [new Jbig2AdaptiveTemplatePixel(atX, atY)]);

        bitmap.GetPixel(3, 2).Should().BeTrue();
        decoder.Contexts.Should().HaveCount(12);
        decoder.Contexts.Should().OnlyContain(context => context < Jbig2ArithmeticGenericRegionDecoder.ContextCount);
    }

    [Fact]
    public void GenericRegion_CustomAdaptiveTemplatePixel_OverridesContext()
    {
        var decoder = new ScriptedArithmeticDecoder(true, false);

        var bitmap = Jbig2ArithmeticGenericRegionDecoder.Decode(
            decoder,
            width: 2,
            height: 1,
            template: 1,
            [new Jbig2AdaptiveTemplatePixel(-1, 0)]);

        bitmap.GetPixel(0, 0).Should().BeTrue();
        bitmap.GetPixel(1, 0).Should().BeFalse();
        decoder.Contexts.Should().Equal(0, 9);
    }

    [Fact]
    public void GenericRegion_TypicalPrediction_CopiesPreviousLine()
    {
        var decoder = new ScriptedArithmeticDecoder(
            false, // line 0 SLTP: decode line
            true,
            false,
            true); // line 1 SLTP: copy line above

        var bitmap = Jbig2ArithmeticGenericRegionDecoder.Decode(
            decoder,
            width: 2,
            height: 2,
            template: 3,
            [new Jbig2AdaptiveTemplatePixel(2, -1)],
            typicalPredictionGenericDecodingOn: true);

        bitmap.GetPixel(0, 0).Should().BeTrue();
        bitmap.GetPixel(1, 0).Should().BeFalse();
        bitmap.GetPixel(0, 1).Should().BeTrue();
        bitmap.GetPixel(1, 1).Should().BeFalse();
        decoder.Contexts.Should().HaveCount(4);
        decoder.Contexts[0].Should().Be(0x195);
        decoder.Contexts[3].Should().Be(0x195);
    }

    [Fact]
    public void GenericRefinement_Template0_DecodesWithReferenceContext()
    {
        var referenceBitmap = new Jbig2Bitmap(2, 1);
        referenceBitmap.SetPixel(0, 0, true);
        var decoder = new ScriptedArithmeticDecoder(true, false);

        var bitmap = Jbig2GenericRefinementRegionDecoder.Decode(
            decoder,
            width: 2,
            height: 1,
            template: 0,
            typicalPredictionGenericRefinementOn: false,
            referenceBitmap,
            referenceDx: 0,
            referenceDy: 0,
            [
                new Jbig2AdaptiveTemplatePixel(-1, -1),
                new Jbig2AdaptiveTemplatePixel(-1, -1),
            ]);

        bitmap.GetPixel(0, 0).Should().BeTrue();
        bitmap.GetPixel(1, 0).Should().BeFalse();
        decoder.Contexts.Should().Equal(256, 513);
        decoder.Contexts.Should().OnlyContain(context => context < Jbig2GenericRefinementRegionDecoder.ContextCount);
    }

    [Fact]
    public void GenericRefinement_Template1_DecodesWithReferenceContext()
    {
        var referenceBitmap = new Jbig2Bitmap(2, 2);
        referenceBitmap.SetPixel(0, 0, true);
        var decoder = new ScriptedArithmeticDecoder(true);

        var bitmap = Jbig2GenericRefinementRegionDecoder.Decode(
            decoder,
            width: 1,
            height: 1,
            template: 1,
            typicalPredictionGenericRefinementOn: false,
            referenceBitmap,
            referenceDx: 0,
            referenceDy: 0,
            Array.Empty<Jbig2AdaptiveTemplatePixel>());

        bitmap.GetPixel(0, 0).Should().BeTrue();
        decoder.Contexts.Should().Equal(8);
    }

    [Fact]
    public void GenericRefinement_Tpgron_ThrowsUntilTypicalPredictionIsSupported()
    {
        var decoder = new ScriptedArithmeticDecoder();
        var referenceBitmap = new Jbig2Bitmap(1, 1);

        var act = () => Jbig2GenericRefinementRegionDecoder.Decode(
            decoder,
            width: 1,
            height: 1,
            template: 1,
            typicalPredictionGenericRefinementOn: true,
            referenceBitmap,
            referenceDx: 0,
            referenceDy: 0,
            Array.Empty<Jbig2AdaptiveTemplatePixel>());

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*TPGRON*");
    }

    private static Jbig2AdaptiveTemplatePixel[] DefaultTemplate0AdaptivePixels()
        =>
        [
            new(3, -1),
            new(-3, -1),
            new(2, -2),
            new(-2, -2),
        ];

    private sealed class ScriptedArithmeticDecoder : IJbig2ArithmeticDecoder
    {
        private readonly Queue<bool> _bits;

        public ScriptedArithmeticDecoder(params bool[] bits)
        {
            _bits = new Queue<bool>(bits);
        }

        public List<int> Contexts { get; } = new();

        public bool Decode(ref int context)
        {
            if (_bits.Count == 0)
                throw new InvalidOperationException("Scripted arithmetic decoder exhausted");

            Contexts.Add(context);
            return _bits.Dequeue();
        }
    }
}
