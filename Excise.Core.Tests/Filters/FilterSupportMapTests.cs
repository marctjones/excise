using AwesomeAssertions;
using Excise.Core.Filters;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Filters;

/// <summary>
/// The ownership map has to stay true or it is worse than nothing — a stale
/// "excise owns this" sends someone chasing a third-party decoder, and a stale
/// "delegated" excuses a real defect.
///
/// These assertions are structural: every registered filter must declare an
/// owner, and the two delegated-decoder claims that actually change triage
/// decisions (DCT and JPX) are pinned against the code that implements them.
/// </summary>
public class FilterSupportMapTests
{
    /// <summary>
    /// Adding a filter without declaring who decodes it should fail here. That
    /// is the whole mechanism: the question "is a difference here our bug?"
    /// must have an answer before the filter ships.
    /// </summary>
    [Fact]
    public void EveryRegisteredFilter_DeclaresAnOwner()
    {
        var registered = new[]
        {
            "FlateDecode", "ASCIIHexDecode", "ASCII85Decode", "LZWDecode",
            "RunLengthDecode", "DCTDecode", "JPXDecode", "CCITTFaxDecode",
            "JBIG2Decode", "BrotliDecode", "Crypt",
        };

        foreach (var filter in registered)
            FilterSupportMap.Find(filter).Should().NotBeNull(
                $"{filter} is registered in PdfFilterRegistry.CreateDefault, so the map must " +
                "say who decodes it — otherwise a rendering difference has no triage answer");
    }

    /// <summary>
    /// The claim that matters most. excise has NO JPEG decoder: DCTDecode is a
    /// PassThroughFilterDecoder and the bytes reach SKBitmap.Decode. A
    /// pixel-level difference against another renderer on a valid JPEG is
    /// libjpeg-turbo behaving differently from their decoder, and is not worth
    /// opening an issue about.
    /// </summary>
    [Fact]
    public void DctDecode_IsNotDecodedByExcise()
    {
        var dct = FilterSupportMap.Find("DCTDecode");
        dct!.Owner.Should().Be(FilterDecoderOwner.PassThrough,
            "DCTDecode is registered as a PassThroughFilterDecoder — excise never decodes JPEG");
        dct.DelegatedTo.Should().Contain("Skia");
        FilterSupportMap.IsDelegated("DCTDecode").Should().BeTrue();
    }

    /// <summary>
    /// Same rule, different library — and the one people are most likely to get
    /// wrong, because excise DOES own the JPX codestream metadata parser. The
    /// pixels come from CSJ2K; excise's own JpxDecoder.Decode throws rather
    /// than emit silently wrong output.
    /// </summary>
    [Fact]
    public void JpxDecode_DelegatesPixelsToAThirdPartyCodec()
    {
        var jpx = FilterSupportMap.Find("JPXDecode");
        jpx!.Owner.Should().Be(FilterDecoderOwner.ThirdParty);
        jpx.DelegatedTo.Should().Be("CSJ2K");
        FilterSupportMap.IsDelegated("JPXDecode").Should().BeTrue();
    }

    /// <summary>
    /// The counterweight. These are excise's own decoders, so a wrong pixel IS
    /// an excise bug and the delegation rule must not be read as covering them.
    /// LZW is the concrete precedent: /EarlyChange went unread for the life of
    /// the decoder and blanked two corpus pages (#887).
    /// </summary>
    [Theory]
    [InlineData("LZWDecode")]
    [InlineData("CCITTFaxDecode")]
    [InlineData("JBIG2Decode")]
    [InlineData("RunLengthDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("ASCIIHexDecode")]
    public void ExciseOwnedFilters_AreNotMarkedDelegated(string filter)
    {
        FilterSupportMap.Find(filter)!.Owner.Should().Be(FilterDecoderOwner.Excise,
            $"{filter} is implemented in Excise.Core/Filters — a defect here is ours to fix");
        FilterSupportMap.IsDelegated(filter).Should().BeFalse();
    }

    /// <summary>
    /// Every profile has to explain itself. A bare owner value is a fact
    /// without a reason, and the reason is what a future reader needs.
    /// </summary>
    [Fact]
    public void EveryProfile_CarriesItsRationale()
    {
        foreach (var p in FilterSupportMap.All)
        {
            p.Notes.Should().NotBeNullOrWhiteSpace($"{p.Filter} must say WHY it is owned as it is");
            if (p.Owner != FilterDecoderOwner.Excise)
                p.DelegatedTo.Should().NotBeNullOrWhiteSpace(
                    $"{p.Filter} is not excise's, so it must name what does decode it — " +
                    "'somebody else' is not a triage answer");
        }
    }
}

/// <summary>
/// The CCITT capability report — the assessment surface that JBIG2 has had
/// since #402 and CCITT has not.
/// </summary>
public class CcittCapabilityClassifierTests
{
    private static PdfDictionary Parms(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    [Theory]
    [InlineData(-1, "Group4")]
    [InlineData(0, "Group3-1D")]
    [InlineData(4, "Group3-2D")]
    public void ReportsTheCodingScheme(int k, string expected)
    {
        var report = Excise.Core.Filters.Ccitt.CcittCapabilityClassifier.Analyze(
            Parms(("K", new PdfInteger(k))));

        report.Features.Should().Contain(expected);
        report.FullySupported.Should().BeTrue($"K={k} is implemented");
    }

    [Fact]
    public void NoDecodeParms_MeansTheDefaults()
    {
        var report = Excise.Core.Filters.Ccitt.CcittCapabilityClassifier.Analyze(null);

        report.Features.Should().Contain("Group3-1D", "K defaults to 0");
        report.FullySupported.Should().BeTrue();
    }

    /// <summary>
    /// The point of the classifier: naming what a stream needs that excise does
    /// not do, so a blank page can be attributed instead of merely observed.
    /// </summary>
    [Theory]
    [InlineData("EndOfBlock")]
    [InlineData("DamagedRowsBeforeError")]
    public void ReportsParametersTheDecoderDoesNotRead(string key)
    {
        var report = Excise.Core.Filters.Ccitt.CcittCapabilityClassifier.Analyze(
            Parms((key, PdfBoolean.True)));

        report.UnsupportedFeatures.Should().Contain(key,
            $"/{key} is defined by §7.4.6 and CcittFaxDecoder never reads it — a stream " +
            "relying on it will not decode as its author intended, and that must be visible");
        report.FullySupported.Should().BeFalse();
    }

    /// <summary>
    /// Guard against the opposite failure: a classifier that reports everything
    /// as unsupported is as useless as one that reports nothing.
    /// </summary>
    [Fact]
    public void OrdinaryParameters_AreNotReportedAsUnsupported()
    {
        var report = Excise.Core.Filters.Ccitt.CcittCapabilityClassifier.Analyze(Parms(
            ("K", new PdfInteger(-1)),
            ("Columns", new PdfInteger(2550)),
            ("Rows", new PdfInteger(3300)),
            ("BlackIs1", PdfBoolean.True),
            ("EncodedByteAlign", PdfBoolean.True)));

        report.UnsupportedFeatures.Should().BeEmpty(
            "every one of these is read and honoured by CcittFaxDecoder");
        report.Features.Should().Contain("Group4").And.Contain("BlackIs1")
            .And.Contain("EncodedByteAlign").And.Contain("Rows");
    }

    [Fact]
    public void ADegenerateColumnCount_IsDiagnosed()
    {
        var report = Excise.Core.Filters.Ccitt.CcittCapabilityClassifier.Analyze(
            Parms(("Columns", new PdfInteger(0))));

        report.Diagnostics.Should().NotBeEmpty("a zero width cannot produce an image");
        report.FullySupported.Should().BeFalse();
    }
}
