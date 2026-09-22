using AwesomeAssertions;
using Excise.App.Services;
using Org.BouncyCastle.Asn1;
using System;
using System.Linq;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// The CMS object inside a signature's zero-padded <c>/Contents</c> placeholder has to be sized
/// exactly before BouncyCastle sees it (#1494). These tests pin the extent arithmetic against the
/// two encodings that actually occur — DER definite length, and the BER indefinite length that
/// BouncyCastle's <c>CmsSignedDataGenerator</c> emits by default — without needing a CMS signature.
/// </summary>
public class SignatureContentsReaderTests
{
    private static byte[] Pad(byte[] asn1, int totalLength, byte padByte = 0x00)
    {
        var padded = new byte[totalLength];
        Buffer.BlockCopy(asn1, 0, padded, 0, asn1.Length);
        for (var i = asn1.Length; i < totalLength; i++)
        {
            padded[i] = padByte;
        }
        return padded;
    }

    /// <summary>A short definite-length DER SEQUENCE (single-byte length header).</summary>
    private static byte[] ShortDerSequence() =>
        new DerSequence(DerInteger.ValueOf(1), DerInteger.ValueOf(2)).GetEncoded(Asn1Encodable.Der);

    /// <summary>A definite-length DER SEQUENCE big enough to need a two-byte long-form length.</summary>
    private static byte[] LongDerSequence() =>
        new DerSequence(new DerOctetString(new byte[512])).GetEncoded(Asn1Encodable.Der);

    /// <summary>A BER SEQUENCE encoded with indefinite length: <c>30 80 … 00 00</c>.</summary>
    private static byte[] IndefiniteBerSequence() =>
        new BerSequence(DerInteger.ValueOf(1), DerInteger.ValueOf(2)).GetEncoded(Asn1Encodable.Ber);

    [Fact]
    public void Read_ShortFormDefiniteLength_ReturnsExactlyTheObject()
    {
        var asn1 = ShortDerSequence();

        var result = SignatureContentsReader.Read(Pad(asn1, 8192));

        result.IsValid.Should().BeTrue();
        result.CmsBytes.Should().Equal(asn1);
        result.PaddingLength.Should().Be(8192 - asn1.Length);
        result.PaddingIsAllZero.Should().BeTrue();
    }

    [Fact]
    public void Read_LongFormDefiniteLength_ReturnsExactlyTheObject()
    {
        var asn1 = LongDerSequence();
        asn1[1].Should().Be(0x82, "this fixture is meant to exercise the two-byte long-form length");

        var result = SignatureContentsReader.Read(Pad(asn1, 8192));

        result.IsValid.Should().BeTrue();
        result.CmsBytes.Should().Equal(asn1);
    }

    [Fact]
    public void Read_BerIndefiniteLength_ReturnsExactlyTheObject()
    {
        // This is the case the hand-rolled DER length trimmer could not handle, and it is
        // BouncyCastle's DEFAULT encoding — so it was the everyday case, not an edge case.
        var asn1 = IndefiniteBerSequence();
        asn1[0].Should().Be(0x30);
        asn1[1].Should().Be(0x80, "an indefinite-length header carries no length to read");

        var result = SignatureContentsReader.Read(Pad(asn1, 8192));

        result.IsValid.Should().BeTrue();
        result.CmsBytes.Should().Equal(asn1);
        result.CmsBytes[^2].Should().Be(0x00);
        result.CmsBytes[^1].Should().Be(0x00,
            "the object ends at its end-of-contents marker, not at the end of the placeholder");
        result.PaddingLength.Should().Be(8192 - asn1.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_Output_IsAcceptedByBouncyCastlesStrictByteArrayParser(bool indefiniteLength)
    {
        // The property that matters: whatever this returns must survive
        // Asn1Object.FromByteArray, which is what CmsSignedData's byte[] constructors use
        // in BouncyCastle 2.7.0 and which throws on any trailing byte.
        var asn1 = indefiniteLength ? IndefiniteBerSequence() : LongDerSequence();

        var result = SignatureContentsReader.Read(Pad(asn1, 8192));

        result.IsValid.Should().BeTrue();
        var action = () => Asn1Object.FromByteArray(result.CmsBytes);
        action.Should().NotThrow("the sliced bytes must contain exactly one ASN.1 object");
    }

    [Fact]
    public void Read_NonZeroTrailingBytes_AreReportedButTheObjectIsStillReturned()
    {
        var asn1 = ShortDerSequence();

        var result = SignatureContentsReader.Read(Pad(asn1, 256, padByte: 0x41));

        result.IsValid.Should().BeTrue();
        result.CmsBytes.Should().Equal(asn1);
        result.PaddingIsAllZero.Should().BeFalse();
        result.PaddingLength.Should().Be(256 - asn1.Length);
    }

    [Fact]
    public void Read_ObjectFillingTheWholeValue_HasNoPadding()
    {
        var asn1 = ShortDerSequence();

        var result = SignatureContentsReader.Read(asn1);

        result.IsValid.Should().BeTrue();
        result.PaddingLength.Should().Be(0);
        result.PaddingIsAllZero.Should().BeTrue();
    }

    [Fact]
    public void Read_EmptyValue_IsRejected()
    {
        var result = SignatureContentsReader.Read(Array.Empty<byte>());

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeEmpty();
    }

    [Fact]
    public void Read_AllZeroPlaceholder_IsRejectedRatherThanTreatedAsAnObject()
    {
        // An untouched placeholder starts with a 0x00 tag byte, which the ASN.1 reader
        // treats as a stray end-of-contents marker. It is not a CMS signature and must not
        // be handed on as one.
        var result = SignatureContentsReader.Read(new byte[8192]);

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeEmpty();
    }

    [Fact]
    public void Read_Garbage_IsRejectedWithAMessage()
    {
        var result = SignatureContentsReader.Read(Enumerable.Repeat((byte)0xFF, 64).ToArray());

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeEmpty();
    }

    [Fact]
    public void Read_TruncatedObject_IsRejected()
    {
        // A definite-length header that claims more bytes than are present must not yield a
        // short object: BouncyCastle's DefiniteLengthInputStream throws
        // "DEF length N object truncated by M", and that has to surface as a rejection
        // rather than a partially-read CMS blob.
        var asn1 = LongDerSequence();
        var truncated = asn1.Take(asn1.Length / 2).ToArray();

        var result = SignatureContentsReader.Read(truncated);

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeEmpty();
    }

    [Fact]
    public void Read_TruncatedObjectPaddedBackToFullWidth_ReadsTheDeclaredExtent()
    {
        // The object's length header claims 516 bytes and the object is cut short, but zero
        // padding supplies enough bytes for the declared length. The reader's job is the EXTENT
        // of the object, and it takes that from the declared length: it must return exactly 516
        // bytes, not the whole 8192-byte value, and not a short read. Whether those bytes are
        // the signer's bytes is not decidable here — a splice like this is caught by the CMS
        // signature and the ByteRange digest, which is what the verifier tests pin.
        var asn1 = LongDerSequence();
        var truncated = asn1.Take(asn1.Length - 64).ToArray();

        var result = SignatureContentsReader.Read(Pad(truncated, 8192));

        result.IsValid.Should().BeTrue(
            "the padding supplies enough bytes for the declared length, so the object reads");
        result.CmsBytes.Length.Should().Be(asn1.Length,
            "the extent comes from the declared length, and the reader must not run past it");
        var parse = () => Asn1Object.FromByteArray(result.CmsBytes);
        parse.Should().NotThrow();
    }
}
