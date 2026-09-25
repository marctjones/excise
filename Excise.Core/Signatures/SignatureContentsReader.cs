using System;
using System.IO;
using Org.BouncyCastle.Asn1;

namespace Excise.Core.Signatures;

/// <summary>
/// Result of splitting a signature dictionary's <c>/Contents</c> value into the
/// CMS object it carries and the zero padding that follows it.
/// </summary>
internal sealed class SignatureContentsReadResult
{
    /// <summary>True when a complete ASN.1 object was read from the start of <c>/Contents</c>.</summary>
    public bool IsValid { get; init; }

    /// <summary>The CMS bytes, sized to the exact extent of the ASN.1 object — never including padding.</summary>
    public byte[] CmsBytes { get; init; } = Array.Empty<byte>();

    /// <summary>Number of bytes following the ASN.1 object inside <c>/Contents</c>.</summary>
    public int PaddingLength { get; init; }

    /// <summary>
    /// False when the trailing bytes are not all zero. ISO 32000-2 12.8.3.3.1 requires the
    /// <c>/Contents</c> value to be "padded with zeros at the end of the string"; anything else is
    /// unsigned, uninspected data riding along inside the signature field, so it is reported rather
    /// than silently dropped.
    /// </summary>
    public bool PaddingIsAllZero { get; init; } = true;

    /// <summary>Why the ASN.1 object could not be read. Empty when <see cref="IsValid"/>.</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// Extracts the CMS object from a PDF signature dictionary's <c>/Contents</c> value.
/// </summary>
/// <remarks>
/// <para>
/// ISO 32000-2 12.8.3.3.1 requires <c>/Contents</c> to "fit precisely in the space between the
/// ranges specified by ByteRange", and because "the length of CMS objects is not entirely
/// predictable, the value of Contents shall be padded with zeros at the end of the string". So the
/// value handed to a CMS parser is nearly always longer than the CMS object inside it.
/// </para>
/// <para>
/// The extent of that object is determined here by an ASN.1 reader, not by hand-decoding the outer
/// tag-length bytes. Hand-decoding was the defect behind issue #1494: it understood only
/// definite-length DER headers and gave up on BER indefinite length (<c>30 80 … 00 00</c>), which is
/// precisely what BouncyCastle's <c>CmsSignedDataGenerator</c> emits by default — so excise's own
/// signatures were handed to BouncyCastle with several kilobytes of padding still attached.
/// BouncyCastle 2.6.2 read the first object and ignored the rest; 2.7.0's
/// <c>CmsSignedData(CmsTypedData, byte[])</c> routes through <c>Asn1Object.FromByteArray</c>, which
/// throws <c>extra data found after object</c>, and every such signature verified as invalid.
/// </para>
/// <para>
/// Reading the extent with an ASN.1 reader handles definite and indefinite length identically, so
/// documents excise signed before #1494 (BER) and after it (DER) both verify.
/// </para>
/// </remarks>
internal static class SignatureContentsReader
{
    public static SignatureContentsReadResult Read(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (contents.Length == 0)
        {
            return new SignatureContentsReadResult { Error = "signature /Contents is empty" };
        }

        try
        {
            using var stream = new MemoryStream(contents, writable: false);
            using var asn1 = new Asn1InputStream(stream);

            var asn1Object = asn1.ReadObject();
            if (asn1Object == null)
            {
                return new SignatureContentsReadResult
                {
                    Error = "signature /Contents holds no ASN.1 object (padding only)"
                };
            }

            // Asn1InputStream is a filter over the MemoryStream and consumes exactly the bytes of
            // the object it read; BouncyCastle's own Asn1Object.FromMemoryStream relies on the same
            // property to detect trailing data.
            var objectLength = checked((int)stream.Position);
            if (objectLength <= 0 || objectLength > contents.Length)
            {
                return new SignatureContentsReadResult
                {
                    Error = $"ASN.1 object length {objectLength} is outside the /Contents value"
                };
            }

            var cmsBytes = new byte[objectLength];
            Buffer.BlockCopy(contents, 0, cmsBytes, 0, objectLength);

            var paddingLength = contents.Length - objectLength;
            var paddingIsAllZero = true;
            for (var i = objectLength; i < contents.Length; i++)
            {
                if (contents[i] != 0)
                {
                    paddingIsAllZero = false;
                    break;
                }
            }

            return new SignatureContentsReadResult
            {
                IsValid = true,
                CmsBytes = cmsBytes,
                PaddingLength = paddingLength,
                PaddingIsAllZero = paddingIsAllZero
            };
        }
        // Asn1Exception and EndOfStreamException both derive from IOException, so the ASN.1
        // reader's whole failure surface is covered here.
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidCastException
                                      or OverflowException)
        {
            return new SignatureContentsReadResult
            {
                Error = $"signature /Contents is not a readable ASN.1 object: {ex.Message}"
            };
        }
    }
}
