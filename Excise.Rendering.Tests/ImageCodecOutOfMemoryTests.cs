using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Filters.Jpx;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1679, the codec layer. d10f7fbe made the render-level image catches stop
/// swallowing <see cref="OutOfMemoryException"/>; the three codec decoders under
/// them, and the managed JPX decoder in Excise.Core, still ended in a bare
/// <c>catch { return null; }</c>. An out-of-memory inside a JPEG or JPEG 2000
/// decode therefore still became "no image drawn" (or, for DCT, a second try
/// through the generic decoder that ignores the dictionary's colour space) with
/// a success exit code.
///
/// <para>The failure is injected without exhausting the heap: the .NET runtime
/// throws <see cref="OutOfMemoryException"/> for any single array longer than
/// <see cref="Array.MaxLength"/>, so a codestream whose header declares samples
/// just past that limit drives the decoder's own allocation into the exception
/// deterministically, on every machine, in microseconds. The declared sizes
/// below are chosen to fall in the 56-element window between
/// <see cref="Array.MaxLength"/> and <see cref="int.MaxValue"/> — larger and the
/// checked int multiplication throws <see cref="OverflowException"/> instead,
/// which is an ordinary decode failure and is correctly still recoverable.</para>
/// </summary>
public class ImageCodecOutOfMemoryTests
{
    // 53,687,091 x 40 = 2,147,483,640 samples: > Array.MaxLength, < int.MaxValue.
    private const int JpxWidth = 53_687_091;
    private const int JpxHeight = 40;

    // 23,015 x 23,327 x 4 components = 2,147,483,620 bytes: same window.
    private const int CmykWidth = 23_015;
    private const int CmykHeight = 23_327;

    [Fact]
    public void ManagedJpxDecode_OutOfMemory_Propagates()
    {
        var act = () => JpxDecoder.TryDecodeManaged(J2kCodestream(JpxWidth, JpxHeight));

        act.Should().Throw<OutOfMemoryException>(
            "the managed decoder's allocation failure must reach the caller, not become a null image");
    }

    [Fact]
    public void JpxImageDecode_OutOfMemory_Propagates()
    {
        // Dictionary size 1x1 keeps the render layer's own guard
        // ((long)w*h <= MaximumPixels) satisfied so the managed decoder runs; the
        // stream itself declares the oversized image.
        var request = new JpxImageDecodeRequest(
            Bytes: J2kCodestream(JpxWidth, JpxHeight),
            SourceWidth: 1,
            SourceHeight: 1,
            TargetWidth: 1,
            TargetHeight: 1,
            ColorSpace: PdfColorSpace.DeviceGray,
            HasExternalSoftMask: false,
            MaximumPixels: 1024,
            CancellationToken: default);

        var act = () => JpxImageDecoder.Decode(request);

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void DctCmykDecode_OutOfMemory_Propagates()
    {
        var request = new DctImageDecodeRequest(
            Bytes: CmykJpegHeader(CmykWidth, CmykHeight),
            SourceWidth: 1,
            SourceHeight: 1,
            TargetWidth: 1,
            TargetHeight: 1,
            ColorSpaceName: "DeviceCMYK",
            ColorTransform: 0,
            ResolvedColorSpace: PdfColorSpace.DeviceCMYK,
            DecodeArray: null,
            ColorKeyMask: null,
            CancellationToken: default);

        var act = () => DctImageDecoder.Decode(request, out _);

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void DctDecode_OrdinaryFailure_IsStillARecoverableNull()
    {
        // The other half of the contract (#878): a JPEG that cannot be decoded is
        // a refusal, not a crash. Truncated data after a valid header.
        var request = new DctImageDecodeRequest(
            Bytes: CmykJpegHeader(8, 8),
            SourceWidth: 8,
            SourceHeight: 8,
            TargetWidth: 8,
            TargetHeight: 8,
            ColorSpaceName: "DeviceCMYK",
            ColorTransform: 0,
            ResolvedColorSpace: PdfColorSpace.DeviceCMYK,
            DecodeArray: null,
            ColorKeyMask: null,
            CancellationToken: default);

        var bitmap = DctImageDecoder.Decode(request, out _);

        // Either a (blank-filled) bitmap or null is a recovered outcome; what
        // matters is that it does not throw.
        bitmap?.Dispose();
    }

    // ---- fixtures ------------------------------------------------------

    /// <summary>SOC + SIZ + COD + QCD + one empty tile + EOC, single 8-bit component.</summary>
    private static byte[] J2kCodestream(int width, int height)
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(long v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        U16(0xFF4F);                                     // SOC
        U16(0xFF51); U16(41);                            // SIZ, Lsiz = 38 + 3 * 1 component
        U16(0); U32(width); U32(height); U32(0); U32(0); // Rsiz, Xsiz, Ysiz, XOsiz, YOsiz
        U32(width); U32(height); U32(0); U32(0);         // one tile covering the image
        U16(1); ms.WriteByte(7); ms.WriteByte(1); ms.WriteByte(1);  // 1 component, 8-bit unsigned
        U16(0xFF52); U16(12);                            // COD
        ms.WriteByte(0); ms.WriteByte(0); U16(1); ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(4); ms.WriteByte(4); ms.WriteByte(0); ms.WriteByte(1);
        U16(0xFF5C); U16(4); ms.WriteByte(0); ms.WriteByte(0x40);   // QCD
        U16(0xFF90); U16(10); U16(0); U32(0); ms.WriteByte(0); ms.WriteByte(1);  // SOT
        U16(0xFF93);                                     // SOD
        U16(0xFFD9);                                     // EOC
        return ms.ToArray();
    }

    /// <summary>
    /// A baseline 4-component JPEG with valid tables and a header declaring
    /// <paramref name="width"/> x <paramref name="height"/> and no scan data —
    /// enough for libjpeg to accept the header and start decompression.
    /// </summary>
    private static byte[] CmykJpegHeader(int width, int height)
    {
        var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void U16(int v) { U8(v >> 8); U8(v); }
        U16(0xFFD8);                                     // SOI
        U16(0xFFDB); U16(67); U8(0);                     // DQT, table 0
        for (var i = 0; i < 64; i++) U8(1);
        U16(0xFFC0); U16(8 + 3 * 4); U8(8);              // SOF0, 8-bit
        U16(height); U16(width); U8(4);
        for (var c = 1; c <= 4; c++) { U8(c); U8(0x11); U8(0); }
        U16(0xFFC4); U16(20); U8(0x00);                  // DHT: DC table 0, one 1-bit code -> 0
        U8(1); for (var i = 1; i < 16; i++) U8(0); U8(0);
        U16(0xFFC4); U16(20); U8(0x10);                  // DHT: AC table 0, one 1-bit code -> EOB
        U8(1); for (var i = 1; i < 16; i++) U8(0); U8(0);
        U16(0xFFDA); U16(6 + 2 * 4); U8(4);              // SOS
        for (var c = 1; c <= 4; c++) { U8(c); U8(0x00); }
        U8(0); U8(63); U8(0);
        U16(0xFFD9);                                     // EOI
        return ms.ToArray();
    }
}
