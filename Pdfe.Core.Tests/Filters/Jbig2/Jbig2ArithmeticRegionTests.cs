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
