using System;
using System.Collections.Generic;
using System.IO;
using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Xunit;

namespace Excise.Core.Tests.ColorSpaces;

/// <summary>
/// Direct tests for the internal ICC profile evaluator (#603). excise cannot
/// write ICC profiles, and no small, license-clear real-world profile is
/// checked into the repo, so these hand-build minimal, spec-valid ICC v2
/// profiles byte-for-byte (header, tag table, XYZ/curv tag data — PDF32000
/// references ICC.1:2001-04 for the profile format excise's evaluator reads)
/// rather than depending on <c>test-pdfs/</c> (the acceptance criterion for
/// this coverage restoration explicitly requires no corpus dependency).
/// </summary>
public class PdfIccProfileTests
{
    public enum CurveKind { Linear, Gamma, Table }

    /// <summary>
    /// A minimal, spec-valid ICC v2 matrix/TRC RGB profile: header (132
    /// bytes) + a 6-entry tag table (rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC) + tag
    /// data. Primaries are the standard sRGB-to-XYZ-D50 values (ICC's
    /// reference sRGB profile); exact colorimetry doesn't matter for these
    /// tests, only that the structure is valid enough for excise's evaluator
    /// to parse and evaluate it.
    /// </summary>
    private static byte[] BuildMatrixRgbProfile(CurveKind rCurve, CurveKind gCurve, CurveKind bCurve)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("rXYZ", BuildXyzTag(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", BuildXyzTag(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", BuildXyzTag(0.1430804, 0.0606936, 0.7139721)),
            ("rTRC", BuildCurveTag(rCurve)),
            ("gTRC", BuildCurveTag(gCurve)),
            ("bTRC", BuildCurveTag(bCurve)),
        };
        return BuildProfile("RGB ", tags);
    }

    /// <summary>
    /// A minimal, spec-valid ICC v2 lut16 (mft2) profile with both A2B0
    /// (device -&gt; PCS Lab) and B2A0 (PCS Lab -&gt; device) tags — the
    /// print/CMYK-preview path (#603). <paramref name="inputChannels"/> is
    /// A2B0's device channel count (e.g. 4 for CMYK); B2A0 is built as the
    /// mirror (3-channel Lab in, <paramref name="inputChannels"/>-channel
    /// device out) so the same profile can stand in as its own "output
    /// intent" in tests. Grid points and table sizes are kept at the
    /// smallest values the parser accepts (gridPoints=2, table entries=2)
    /// — colorimetric accuracy doesn't matter here, only that the mft2
    /// structure is valid enough to parse and evaluate.
    /// </summary>
    internal static byte[] BuildLut16CmykProfile(int inputChannels = 4)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("A2B0", BuildMft2Tag(inputChannels, outputChannels: 3, gridPoints: 2)),
            ("B2A0", BuildMft2Tag(3, outputChannels: inputChannels, gridPoints: 2)),
        };
        return BuildProfile("CMYK", tags);
    }

    private static byte[] BuildMft2Tag(int inputChannels, int outputChannels, int gridPoints)
    {
        const int inputEntries = 2;
        const int outputEntries = 2;

        using var ms = new MemoryStream();
        WriteAscii(ms, "mft2");
        WriteU32(ms, 0); // reserved
        ms.WriteByte((byte)inputChannels);
        ms.WriteByte((byte)outputChannels);
        ms.WriteByte((byte)gridPoints);
        ms.WriteByte(0); // reserved padding
        ms.Write(new byte[36], 0, 36); // 3x3 e-parameter matrix — unused by excise's evaluator
        WriteU16(ms, inputEntries);
        WriteU16(ms, outputEntries);

        // Input tables: identity-ish 2-entry ramp per channel.
        for (var c = 0; c < inputChannels; c++)
        {
            WriteU16(ms, 0);
            WriteU16(ms, 65535);
        }

        // CLUT: gridPoints^inputChannels entries, each outputChannels wide.
        // Fill with a simple deterministic ramp so output isn't degenerate.
        int clutEntries = 1;
        for (var i = 0; i < inputChannels; i++) clutEntries *= gridPoints;
        for (var i = 0; i < clutEntries; i++)
            for (var c = 0; c < outputChannels; c++)
                WriteU16(ms, (ushort)((i * 7 + c * 4001) % 65536));

        // Output tables: identity-ish 2-entry ramp per channel.
        for (var c = 0; c < outputChannels; c++)
        {
            WriteU16(ms, 0);
            WriteU16(ms, 65535);
        }

        return ms.ToArray();
    }

    private static byte[] BuildXyzTag(double x, double y, double z)
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, "XYZ ");
        WriteU32(ms, 0); // reserved
        WriteS15Fixed16(ms, x);
        WriteS15Fixed16(ms, y);
        WriteS15Fixed16(ms, z);
        return ms.ToArray();
    }

    private static byte[] BuildCurveTag(CurveKind kind)
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, "curv");
        WriteU32(ms, 0); // reserved
        switch (kind)
        {
            case CurveKind.Linear:
                WriteU32(ms, 0); // count = 0 -> identity
                break;
            case CurveKind.Gamma:
                WriteU32(ms, 1); // count = 1 -> single u8Fixed8 gamma value
                WriteU16(ms, (ushort)Math.Round(2.2 * 256));
                break;
            case CurveKind.Table:
                ushort[] table = { 0, 8192, 24576, 49152, 65535 };
                WriteU32(ms, (uint)table.Length);
                foreach (var v in table) WriteU16(ms, v);
                break;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Assembles header + tag table + tag data into one profile byte array.
    /// <paramref name="pcs"/> defaults to "Lab " -- the PCS every existing
    /// test's hand-built profile implicitly needs (#1424 added reading this
    /// field; before that fix it was never even looked at, so these profiles
    /// never specified it and got zero bytes, which also decode as "not XYZ").
    /// </summary>
    internal static byte[] BuildProfile(string colorSpace, List<(string Sig, byte[] Data)> tags, string pcs = "Lab ")
    {
        const int headerSize = 132;
        int tagTableSize = tags.Count * 12;
        int dataStart = headerSize + tagTableSize;

        using var ms = new MemoryStream();
        ms.Write(new byte[headerSize], 0, headerSize); // placeholder header, patched below
        var tableStart = ms.Position;
        ms.Write(new byte[tagTableSize], 0, tagTableSize); // placeholder tag table

        var offsets = new List<(string Sig, int Offset, int Size)>();
        foreach (var (sig, data) in tags)
        {
            offsets.Add((sig, (int)ms.Position, data.Length));
            ms.Write(data, 0, data.Length);
        }

        var bytes = ms.ToArray();

        // Patch header: total size (0-3), colorSpace (16-19), PCS (20-23), tagCount (128-131).
        PatchU32(bytes, 0, (uint)bytes.Length);
        PatchAscii(bytes, 16, colorSpace);
        PatchAscii(bytes, 20, pcs);
        PatchU32(bytes, 128, (uint)tags.Count);

        // Patch tag table.
        for (var i = 0; i < offsets.Count; i++)
        {
            int entryOffset = (int)tableStart + i * 12;
            PatchAscii(bytes, entryOffset, offsets[i].Sig);
            PatchU32(bytes, entryOffset + 4, (uint)offsets[i].Offset);
            PatchU32(bytes, entryOffset + 8, (uint)offsets[i].Size);
        }

        return bytes;
    }

    private static void WriteAscii(Stream s, string text) => s.Write(System.Text.Encoding.ASCII.GetBytes(text));
    private static void WriteU32(Stream s, uint v) => s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    private static void WriteU16(Stream s, ushort v) => s.Write(new[] { (byte)(v >> 8), (byte)v });
    private static void WriteS15Fixed16(Stream s, double v) => WriteU32(s, unchecked((uint)(int)Math.Round(v * 65536.0)));

    private static void PatchU32(byte[] data, int offset, uint v)
    {
        data[offset] = (byte)(v >> 24); data[offset + 1] = (byte)(v >> 16);
        data[offset + 2] = (byte)(v >> 8); data[offset + 3] = (byte)v;
    }

    private static void PatchAscii(byte[] data, int offset, string text)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(text.PadRight(4).Substring(0, 4));
        Array.Copy(b, 0, data, offset, 4);
    }

    [Theory]
    [InlineData(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear)]
    [InlineData(CurveKind.Gamma, CurveKind.Gamma, CurveKind.Gamma)]
    [InlineData(CurveKind.Table, CurveKind.Table, CurveKind.Table)]
    public void TryParse_ValidMatrixRgbProfile_ParsesAndEvaluatesToRgb(CurveKind r, CurveKind g, CurveKind b)
    {
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(r, g, b));
        profile.Should().NotBeNull("a spec-valid matrix/TRC RGB profile must parse");

        foreach (var input in new[] { new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0.5, 0.5, 0.5 } })
        {
            var rgb = profile!.ToRgb(input);
            rgb.Should().NotBeNull();
            rgb!.Value.R.Should().BeInRange(0, 1);
            rgb.Value.G.Should().BeInRange(0, 1);
            rgb.Value.B.Should().BeInRange(0, 1);
        }
    }

    [Fact]
    public void TryParse_ToRgb_IsCachedAndConsistentAcrossCalls()
    {
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Gamma, CurveKind.Gamma, CurveKind.Gamma));
        var first = profile!.ToRgb(new[] { 0.25, 0.6, 0.9 });
        var second = profile.ToRgb(new[] { 0.25, 0.6, 0.9 });
        second.Should().Be(first, "repeated evaluation of the same input must hit the cache and agree");
    }

    [Fact]
    public void ToRgb_EmptyValues_ReturnsNull()
    {
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear));
        profile!.ToRgb(Array.Empty<double>()).Should().BeNull();
    }

    [Fact]
    public void ToRgb_TooFewChannelsForEitherProfileKind_ReturnsNull()
    {
        // A matrix RGB profile needs >= 3 values; 2 isn't enough and there's
        // no lut16 fallback, so ToPcsLab falls through to null.
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear));
        profile!.ToRgb(new[] { 0.5, 0.5 }).Should().BeNull();
    }

    [Fact]
    public void ToOutputIntentPreviewRgb_SourceHasNoUsablePcsLab_FallsBackToToRgb()
    {
        // Same "too few channels" case as above, but through the preview
        // path: ToPcsLab(values) is null, so ToOutputIntentPreviewRgb must
        // fall back to ToRgb(values) rather than reaching FromPcsLab at all.
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear));
        var outputIntent = PdfIccProfile.TryParse(BuildLut16CmykProfile());

        var values = new[] { 0.5, 0.5 };
        profile!.ToOutputIntentPreviewRgb(values, outputIntent).Should().Be(profile.ToRgb(values));
    }

    [Fact]
    public void ToOutputIntentPreviewRgb_ThreeChannelOutputIntent_TreatsOutputAsDirectRgb()
    {
        // A B2A0 with exactly 3 output channels hits the ">= 3" (not ">= 4")
        // switch arm: the output is used as RGB directly, not routed through
        // the CMYK process-screen preview conversion.
        var profile = PdfIccProfile.TryParse(BuildLut16CmykProfile());
        var rgbOutputIntent = PdfIccProfile.TryParse(BuildLut16CmykProfile(inputChannels: 3));

        var preview = profile!.ToOutputIntentPreviewRgb(new[] { 0.1, 0.2, 0.3, 0.4 }, rgbOutputIntent);

        preview.Should().NotBeNull();
        preview!.Value.R.Should().BeInRange(0, 1);
    }

    [Fact]
    public void TryParseMatrixProfile_XyzTagsPresentButCurveTagsMissing_FailsAndFallsThroughToNull()
    {
        // rXYZ/gXYZ/bXYZ present (so the XYZ-tag read succeeds) but no
        // rTRC/gTRC/bTRC at all -> TryParseMatrixProfile's curve-tag check
        // fails distinctly from its XYZ-tag check, and with no A2B0/B2A0
        // either, TryParse falls through to null.
        var tags = new List<(string, byte[])>
        {
            ("rXYZ", BuildXyzTag(0.43, 0.22, 0.01)),
            ("gXYZ", BuildXyzTag(0.38, 0.71, 0.09)),
            ("bXYZ", BuildXyzTag(0.14, 0.06, 0.71)),
        };
        var profile = BuildProfile("RGB ", tags);
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void TryParse_TagCountTooLargeForDeclaredData_ReturnsNull()
    {
        var profile = BuildMatrixRgbProfile(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear);
        // Header's tagCount (offset 128) claims far more tags than the tag
        // table + data could possibly hold in this many bytes.
        PatchU32(profile, 128, 10_000);
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void ToOutputIntentPreviewRgb_SingleChannelOutputIntent_TreatsOutputAsGray()
    {
        // A B2A0 with exactly 1 output channel hits the ">= 1" (gray) arm:
        // R, G, and B all come from the same single device value.
        var profile = PdfIccProfile.TryParse(BuildLut16CmykProfile());
        var grayOutputIntent = PdfIccProfile.TryParse(BuildLut16CmykProfile(inputChannels: 1));

        var preview = profile!.ToOutputIntentPreviewRgb(new[] { 0.1, 0.2, 0.3, 0.4 }, grayOutputIntent);

        preview.Should().NotBeNull();
        preview!.Value.R.Should().Be(preview.Value.G).And.Be(preview.Value.B);
    }

    [Fact]
    public void TryReadCurveTag_TableDeclaredCountExceedsTagSize_FailsGracefully()
    {
        // A curv tag claims a 100-entry table but the tag's declared size
        // only actually holds 2 entries — a truncated/adversarial tag
        // (#648's threat model) that must be rejected, not read out of bounds.
        using var ms = new MemoryStream();
        WriteAscii(ms, "curv");
        WriteU32(ms, 0);
        WriteU32(ms, 100); // count = 100, but...
        WriteU16(ms, 0);
        WriteU16(ms, 65535); // ...only 2 entries actually follow
        var badCurve = ms.ToArray();

        var tags = new List<(string, byte[])>
        {
            ("rXYZ", BuildXyzTag(0.43, 0.22, 0.01)),
            ("gXYZ", BuildXyzTag(0.38, 0.71, 0.09)),
            ("bXYZ", BuildXyzTag(0.14, 0.06, 0.71)),
            ("rTRC", badCurve),
            ("gTRC", BuildCurveTag(CurveKind.Linear)),
            ("bTRC", BuildCurveTag(CurveKind.Linear)),
        };
        var profile = BuildProfile("RGB ", tags);
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void TryParse_TagTableEntryWithOverflowingOffset_IsCaughtAndReturnsNull()
    {
        // A tag table entry whose offset field is 0xFFFFFFFF overflows the
        // checked((int)...) cast while TryParse reads the tag table — this
        // must land in TryParse's own catch block and return null, not
        // propagate an unhandled OverflowException (#648's threat model:
        // an adversarial profile must fail closed, never crash the host).
        var profile = new byte[132 + 12];
        PatchU32(profile, 0, (uint)profile.Length);
        PatchAscii(profile, 16, "RGB ");
        PatchU32(profile, 128, 1); // tagCount = 1
        PatchAscii(profile, 132, "rXYZ");
        PatchU32(profile, 136, 0xFFFFFFFF); // tagOffset -> overflows checked(int) cast
        PatchU32(profile, 140, 20);

        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void ToRgb_A2B0ProducesFewerThanThreeChannels_ReturnsNull()
    {
        // An A2B0 lut16 with only 2 output channels can't be decoded as Lab
        // (DecodeIccLab needs 3), so ToPcsLab's lut16 branch itself falls
        // through to null rather than the "no lut16 profile at all" case.
        var profile = PdfIccProfile.TryParse(BuildProfile("CMYK",
            new List<(string, byte[])> { ("A2B0", BuildMft2Tag(2, outputChannels: 2, gridPoints: 2)) }));
        profile.Should().NotBeNull();
        profile!.ToRgb(new[] { 0.3, 0.6 }).Should().BeNull();
    }

    [Fact]
    public void TryParseLut16Profile_TooFewInputTableEntries_FailsGracefully()
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, "mft2");
        WriteU32(ms, 0);
        ms.WriteByte(2); ms.WriteByte(3); ms.WriteByte(2); ms.WriteByte(0);
        ms.Write(new byte[36], 0, 36);
        WriteU16(ms, 1); // inputEntries = 1 -> rejected (must be > 1)
        WriteU16(ms, 2);
        var tags = new List<(string, byte[])> { ("A2B0", ms.ToArray()) };
        var profile = BuildProfile("CMYK", tags);
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void TryParseLut16Profile_WrongTagSignature_FailsGracefully()
    {
        // An A2B0 tag entry whose bytes don't start with "mft2" (a
        // corrupt/adversarial profile, #648's threat model) must be
        // rejected, not misread.
        using var ms = new MemoryStream();
        WriteAscii(ms, "bad!");
        ms.Write(new byte[60], 0, 60);
        var tags = new List<(string, byte[])> { ("A2B0", ms.ToArray()) };
        var profile = BuildProfile("CMYK", tags);
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void ToOutputIntentPreviewRgb_NullOutputIntent_FallsBackToToRgb()
    {
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear));

        var values = new[] { 0.2, 0.4, 0.6 };
        var viaPreview = profile!.ToOutputIntentPreviewRgb(values, null);
        var viaDirect = profile.ToRgb(values);

        viaPreview.Should().Be(viaDirect);
    }

    [Fact]
    public void ToOutputIntentPreviewRgb_WithOutputIntent_UsesBToALut()
    {
        // Same profile used as both the source and a (degenerate) output
        // intent: the matrix profile has no B2A0/bToALut, so FromPcsLab
        // returns null and the preview path falls back to ToRgb — this
        // exercises the "output intent present but has no usable LUT"
        // branch distinctly from the "output intent is null" branch above.
        var profile = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Gamma, CurveKind.Gamma, CurveKind.Gamma));
        var outputIntent = PdfIccProfile.TryParse(BuildMatrixRgbProfile(CurveKind.Gamma, CurveKind.Gamma, CurveKind.Gamma));

        var values = new[] { 0.3, 0.5, 0.7 };
        var viaPreview = profile!.ToOutputIntentPreviewRgb(values, outputIntent);
        var viaDirect = profile.ToRgb(values);

        viaPreview.Should().Be(viaDirect, "a matrix-only output intent has no B2A0 LUT, so preview falls back to direct ToRgb");
    }

    /// <summary>
    /// A 3-in/3-out mft2 tag with identity input/output tables (so
    /// EvaluateLut16's per-channel table lookups are no-ops) and a single
    /// controlled CLUT corner, for tests that need to know EXACTLY what
    /// <c>EvaluateLut16</c> will return for a given input rather than a
    /// deterministic-but-opaque ramp (<see cref="BuildMft2Tag"/>'s formula).
    /// gridPoints=2 means input (0,0,0) lands exactly on the (0,0,0) grid
    /// corner with weight 1 and every other corner at weight 0 (see
    /// InterpolateClut), so <paramref name="corner000"/> passes straight
    /// through.
    /// </summary>
    private static byte[] BuildMft2TagWithCorner(ushort[] corner000)
    {
        const int channels = 3;
        const int gridPoints = 2;
        using var ms = new MemoryStream();
        WriteAscii(ms, "mft2");
        WriteU32(ms, 0);
        ms.WriteByte(channels);
        ms.WriteByte(channels);
        ms.WriteByte(gridPoints);
        ms.WriteByte(0);
        ms.Write(new byte[36], 0, 36);
        WriteU16(ms, 2); // inputEntries
        WriteU16(ms, 2); // outputEntries

        for (var c = 0; c < channels; c++) { WriteU16(ms, 0); WriteU16(ms, 65535); } // identity input tables

        // CLUT: 2^3 = 8 corners, 3 channels each. Only corner index 0 (all-zero) matters here.
        for (var i = 0; i < 8; i++)
            for (var c = 0; c < channels; c++)
                WriteU16(ms, i == 0 ? corner000[c] : (ushort)0);

        for (var c = 0; c < channels; c++) { WriteU16(ms, 0); WriteU16(ms, 65535); } // identity output tables

        return ms.ToArray();
    }

    [Fact]
    public void TryParse_XyzPcsLut16Profile_DecodesClutOutputAsXyzNotLab()
    {
        // #1424: the ICC v2 header's PCS field (offset 20) governs how a
        // lut16 A2B tag's CLUT output is encoded. Build a CLUT whose (0,0,0)
        // corner is the D50 white point, encoded per the u1Fixed15Number
        // PCSXYZ wire format (1.0 -> 0x8000/32768). With PCS correctly read
        // as XYZ, feeding (0,0,0) must decode near-white. The bug this pins
        // (decoding the identical bytes as Lab instead) would instead read
        // L~48, a~-0.5, b~-23 -- a medium grey with a green-blue tint, NOT
        // white -- which is exactly the class of error that turned a red
        // FOGRA logo navy on the real Altona fixture this issue was found on.
        var d50WhiteAsXyzU16 = new ushort[]
        {
            (ushort)Math.Round(0.96422 * 32768.0), // 31601
            (ushort)Math.Round(1.00000 * 32768.0), // 32768
            (ushort)Math.Round(0.82521 * 32768.0), // 27041
        };
        var tags = new List<(string, byte[])> { ("A2B0", BuildMft2TagWithCorner(d50WhiteAsXyzU16)) };
        var profile = PdfIccProfile.TryParse(BuildProfile("CMYK", tags, pcs: "XYZ "));

        profile.Should().NotBeNull();
        var rgb = profile!.ToRgb(new[] { 0.0, 0.0, 0.0 });

        rgb.Should().NotBeNull();
        rgb!.Value.R.Should().BeGreaterThan(0.95, "the (0,0,0) corner encodes the D50 white point in XYZ and must render near-white, not the medium grey a Lab misdecode would produce");
        rgb.Value.G.Should().BeGreaterThan(0.95);
        rgb.Value.B.Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void TryParse_LabPcsLut16Profile_UnaffectedByPcsField_StillDecodesAsLab()
    {
        // Same CLUT bytes as the XYZ test above, but with PCS left at the
        // default "Lab " -- must decode via the ORIGINAL Lab path and must
        // NOT be near-white (confirms the branch in ToPcsLab is actually
        // conditioned on the PCS field, not just always taking the XYZ path).
        var sameBytes = new ushort[]
        {
            (ushort)Math.Round(0.96422 * 32768.0),
            (ushort)Math.Round(1.00000 * 32768.0),
            (ushort)Math.Round(0.82521 * 32768.0),
        };
        var tags = new List<(string, byte[])> { ("A2B0", BuildMft2TagWithCorner(sameBytes)) };
        var profile = PdfIccProfile.TryParse(BuildProfile("CMYK", tags)); // default pcs: "Lab "

        profile.Should().NotBeNull();
        var rgb = profile!.ToRgb(new[] { 0.0, 0.0, 0.0 });

        rgb.Should().NotBeNull();
        (rgb!.Value.R > 0.95 && rgb.Value.G > 0.95 && rgb.Value.B > 0.95).Should().BeFalse(
            "decoding the same bytes as Lab (not XYZ) must NOT produce near-white -- otherwise the two paths aren't actually distinct");
    }

    [Fact]
    public void TryParse_ValidLut16CmykProfile_ParsesAndEvaluatesToRgbViaA2B0()
    {
        var profile = PdfIccProfile.TryParse(BuildLut16CmykProfile());
        profile.Should().NotBeNull("a spec-valid mft2 A2B0/B2A0 profile must parse");

        var rgb = profile!.ToRgb(new[] { 0.1, 0.2, 0.3, 0.4 });
        rgb.Should().NotBeNull("a 4-channel (CMYK) input must evaluate through the A2B0 lut16 -> Lab -> sRGB path");
        rgb!.Value.R.Should().BeInRange(0, 1);
        rgb.Value.G.Should().BeInRange(0, 1);
        rgb.Value.B.Should().BeInRange(0, 1);
    }

    [Fact]
    public void ToOutputIntentPreviewRgb_CmykOutputIntent_UsesB2A0AndCmykPreviewBranch()
    {
        var profile = PdfIccProfile.TryParse(BuildLut16CmykProfile());
        var outputIntent = PdfIccProfile.TryParse(BuildLut16CmykProfile());

        var preview = profile!.ToOutputIntentPreviewRgb(new[] { 0.1, 0.2, 0.3, 0.4 }, outputIntent);

        // The output intent's B2A0 produces a 4-channel (CMYK) device value,
        // which ToOutputIntentPreviewRgb routes through the CMYK process-
        // screen preview conversion rather than treating it as RGB/gray.
        preview.Should().NotBeNull();
        preview!.Value.R.Should().BeInRange(0, 1);
        preview.Value.G.Should().BeInRange(0, 1);
        preview.Value.B.Should().BeInRange(0, 1);
    }

    [Fact]
    public void TryParse_Lut16Profile_WithTooFewGridPointsOrEntries_ReturnsFalseGracefully()
    {
        // gridPoints=1 is rejected by the parser (<=1 check) — build a
        // profile whose A2B0 tag has gridPoints=1 and confirm TryParse
        // still returns null rather than throwing.
        var tags = new List<(string, byte[])> { ("A2B0", BuildMft2Tag(2, 3, gridPoints: 1)) };
        var bogusProfile = BuildProfile("CMYK", tags);
        PdfIccProfile.TryParse(bogusProfile).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })] // far too short
    [InlineData(new byte[0])]
    public void TryParse_TooShortOrEmpty_ReturnsNullGracefully(byte[] bogus)
    {
        PdfIccProfile.TryParse(bogus).Should().BeNull();
    }

    [Fact]
    public void TryParse_DeclaredSizeExceedsActualLength_ReturnsNull()
    {
        var profile = BuildMatrixRgbProfile(CurveKind.Linear, CurveKind.Linear, CurveKind.Linear);
        PatchU32(profile, 0, (uint)(profile.Length + 1000)); // lie about the size
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void TryParse_MissingRequiredTags_ReturnsNull()
    {
        // RGB colorSpace but no XYZ/curv tags at all -> not a usable matrix profile,
        // and no A2B0/B2A0 either -> TryParse falls through to null.
        var profile = BuildProfile("RGB ", new List<(string, byte[])>());
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }

    [Fact]
    public void TryParse_NonRgbColorSpaceWithoutLutTags_ReturnsNull()
    {
        var tags = new List<(string, byte[])>
        {
            ("rXYZ", BuildXyzTag(0.43, 0.22, 0.01)),
            ("gXYZ", BuildXyzTag(0.38, 0.71, 0.09)),
            ("bXYZ", BuildXyzTag(0.14, 0.06, 0.71)),
            ("rTRC", BuildCurveTag(CurveKind.Linear)),
            ("gTRC", BuildCurveTag(CurveKind.Linear)),
            ("bTRC", BuildCurveTag(CurveKind.Linear)),
        };
        // Matrix tags present, but colorSpace isn't "RGB " -> matrix path is
        // skipped by design (TryParse only tries it for colorSpace == "RGB ").
        var profile = BuildProfile("GRAY", tags);
        PdfIccProfile.TryParse(profile).Should().BeNull();
    }
}
