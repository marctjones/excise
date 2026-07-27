using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Document;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Spec-driven tests for ICCBased-CMYK (N=4) overprint (#803, follow-up to
/// #634). An ICCBased colour space with four components carries raw CMYK
/// values; #803 lets such a fill/stroke participate in overprint under the
/// same nonzero-overprint-mode (/OPM 1) rule as DeviceCMYK — a component that
/// is exactly zero leaves that colorant of the backdrop unchanged instead of
/// knocking it out.
///
/// The discriminating fixture paints a yellow square (C=M=K=0) via an ICCBased
/// CMYK space over a DeviceCMYK cyan square:
///   - overprint ON + OPM 1 → overlap keeps the cyan colorant → green;
///   - overprint OFF        → overlap knocks cyan out → plain yellow.
///
/// Every expectation is RELATIVE (overprint output equals an explicitly
/// painted merged DeviceCMYK colour) so it does not depend on the CMYK→RGB
/// preview formula. The same fixture is corroborated against an independent
/// oracle (Ghostscript -dOverprint=/simulate) in
/// Differential/IccBasedOverprintDifferentialTests.
///
/// A guard test proves the fixture's colour space really resolves to ICCBased
/// N=4: an UNPARSEABLE N=4 profile falls back to DeviceCMYK upstream and would
/// exercise the pre-existing DeviceCMYK path instead of #803's new branch,
/// making these tests vacuous.
/// </summary>
public sealed class IccBasedCmykOverprintTests
{
    // Device coordinates at 72 DPI on the 300x300 page (deviceY = 300 - pdfY).
    private static readonly (int X, int Y) Overlap = (100, 200);

    // Cyan DeviceCMYK backdrop, then a yellow square set through the ICCBased
    // CMYK colour space /ICCCS. The gs dict /GSop is /OP /op /OPM 1.
    private const string OverprintFill =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "/ICCCS cs /GSop gs 0 0 1 0 scn 60 60 80 80 re f\n";

    // Same yellow through the ICCBased space but WITHOUT overprint → knockout.
    private const string KnockoutFill =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "/ICCCS cs 0 0 1 0 scn 60 60 80 80 re f\n";

    // The overprinted overlap must equal this explicitly painted merged CMYK
    // colour (cyan colorant survives the zero-C overprint: 1 0 1 0).
    private const string ExplicitMergedFill =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "1 0 1 0 k 60 60 80 80 re f\n";

    // A zero-C ICCBased-CMYK STROKE (via /ICCCS CS + SCN) over the cyan
    // backdrop, with overprint on. The stroke runs along pdf y=100
    // (device y=200), x 60..140, so it passes through the Overlap probe.
    private const string OverprintStroke =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "/ICCCS CS /GSop gs 0 0 1 0 SCN 20 w 60 100 m 140 100 l S\n";

    [Fact]
    public void Fixture_ColorSpaceResolvesToIccBasedN4()
    {
        using var doc = PdfDocument.Open(BuildIccBasedOverprintPdf(OverprintFill, deviceCmykGroup: false));
        var page = doc.GetPage(1);
        var csObj = page.GetColorSpaceObject("ICCCS");
        csObj.Should().NotBeNull("the fixture must define an /ICCCS colour space");

        var cs = PdfColorSpace.Parse(csObj!, doc);
        cs.Type.Should().Be(PdfColorSpaceType.ICCBased,
            "an unparseable N=4 profile would fall back to DeviceCMYK and make the #803 branch untested");
        cs.Components.Should().Be(4, "the profile is CMYK (N=4)");
    }

    [Fact]
    public void GroupPage_Opm1Overprint_EqualsExplicitlyPaintedMergedColor()
    {
        var overprint = ProbeOverlap(RenderVariant(OverprintFill, deviceCmykGroup: true));
        var merged = ProbeOverlap(RenderVariant(ExplicitMergedFill, deviceCmykGroup: true));
        var knockout = ProbeOverlap(RenderVariant(KnockoutFill, deviceCmykGroup: true));

        // Inside a DeviceCMYK group the merge is exact and driven by the raw
        // ICCBased components (#803 populates FillDeviceCmyk for N=4).
        AssertSameColor(overprint, merged, 2,
            "OPM 1 zero components of an ICCBased-CMYK fill must take the group backdrop's colorants exactly");
        // The surviving cyan colorant suppresses red in the overprinted overlap
        // (merged 1 0 1 0 → red ≈ 0); a knocked-out yellow leaves red ≈ 255.
        Math.Abs(overprint.Red - knockout.Red).Should().BeGreaterThan(100,
            "the overprinted overlap must NOT be the knocked-out yellow fill");
    }

    [Fact]
    public void PlainPage_Opm1Overprint_PreservesUnderlyingColorant()
    {
        using var bitmap = RenderVariant(OverprintFill, deviceCmykGroup: false);
        var overlap = Probe(bitmap, Overlap);

        // Outside a group the backdrop colorants are estimated, but the
        // defining property still holds: the cyan colorant SURVIVES rather than
        // being knocked out. Knocked-out yellow would leave red ≈ 255.
        overlap.Red.Should().BeLessThan(100,
            "the cyan colorant under a zero-C ICCBased-CMYK overprint fill must survive (knockout leaves red ≈ 255)");
        overlap.Green.Should().BeGreaterThan(100, "cyan + yellow reads green");
    }

    [Fact]
    public void PlainPage_StrokeOverprint_PreservesUnderlyingColorant()
    {
        // Parity with #634's stroke coverage: the ICCBased-CMYK stroke path
        // runs through the same TryParseDeviceCmykOperands + IsOverprintActive
        // (style-aware) mechanism as the fill.
        using var bitmap = RenderVariant(OverprintStroke, deviceCmykGroup: false);
        var onStroke = Probe(bitmap, Overlap);

        onStroke.Red.Should().BeLessThan(100,
            "a zero-C ICCBased-CMYK overprint STROKE must keep the cyan colorant underneath");
        onStroke.Green.Should().BeGreaterThan(100, "cyan + yellow reads green");
    }

    // ------------------------------------------------------------------

    private static SKBitmap RenderVariant(string content, bool deviceCmykGroup)
    {
        using var doc = PdfDocument.Open(BuildIccBasedOverprintPdf(content, deviceCmykGroup));
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });
    }

    private static SKColor ProbeOverlap(SKBitmap bitmap)
    {
        using (bitmap)
        {
            return Probe(bitmap, Overlap);
        }
    }

    private static SKColor Probe(SKBitmap bitmap, (int X, int Y) point)
        => bitmap.GetPixel(point.X, point.Y);

    private static void AssertSameColor(SKColor actual, SKColor expected, int tolerance, string because)
    {
        var delta = Math.Max(
            Math.Abs(actual.Red - expected.Red),
            Math.Max(Math.Abs(actual.Green - expected.Green), Math.Abs(actual.Blue - expected.Blue)));
        delta.Should().BeLessThanOrEqualTo(tolerance,
            $"{because} (expected {expected}, was {actual})");
    }

    // ------------------------------------------------------------------
    // Byte-oriented single-page PDF builder. Unlike the ASCII builder in
    // OverprintRenderingTests, this one embeds a binary ICC profile stream so
    // /ICCCS resolves to a genuine ICCBased N=4 space.
    // ------------------------------------------------------------------

    internal static byte[] BuildIccBasedOverprintPdf(string content, bool deviceCmykGroup)
    {
        var icc = BuildLut16CmykProfile();
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var buffer = new List<byte>();
        var offsets = new long[6];

        void Append(string s) => buffer.AddRange(Encoding.ASCII.GetBytes(s));
        void AppendBytes(byte[] b) => buffer.AddRange(b);

        Append("%PDF-1.7\n");

        offsets[1] = buffer.Count;
        Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = buffer.Count;
        Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = buffer.Count;
        Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R\n");
        if (deviceCmykGroup)
            Append("   /Group << /S /Transparency /CS /DeviceCMYK >>\n");
        Append("   /Resources <<\n");
        Append("      /ColorSpace << /ICCCS [/ICCBased 5 0 R] >>\n");
        Append("      /ExtGState << /GSop << /Type /ExtGState /OP true /op true /OPM 1 >> >>\n");
        Append("   >>\n>>\nendobj\n");

        offsets[4] = buffer.Count;
        Append($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        AppendBytes(contentBytes);
        Append("\nendstream\nendobj\n");

        offsets[5] = buffer.Count;
        Append($"5 0 obj\n<< /N 4 /Length {icc.Length} >>\nstream\n");
        AppendBytes(icc);
        Append("\nendstream\nendobj\n");

        var xref = buffer.Count;
        Append("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            Append($"{offsets[i]:D10} 00000 n \n");
        Append($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xref}\n%%EOF\n");

        return buffer.ToArray();
    }

    // ------------------------------------------------------------------
    // Minimal, spec-valid ICC v2 lut16 (mft2) CMYK profile with A2B0 and B2A0
    // tags — the smallest structure PdfIccProfile.TryParse accepts. Colour
    // accuracy is irrelevant here; the profile only has to PARSE so the space
    // resolves to ICCBased (Type stays ICCBased, N=4) and exercises #803.
    // Mirrors Excise.Core.Tests' BuildLut16CmykProfile helper.
    // ------------------------------------------------------------------

    internal static byte[] BuildLut16CmykProfile()
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("A2B0", BuildMft2Tag(inputChannels: 4, outputChannels: 3, gridPoints: 2)),
            ("B2A0", BuildMft2Tag(inputChannels: 3, outputChannels: 4, gridPoints: 2)),
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

        for (var c = 0; c < inputChannels; c++)
        {
            WriteU16(ms, 0);
            WriteU16(ms, 65535);
        }

        var clutEntries = 1;
        for (var i = 0; i < inputChannels; i++) clutEntries *= gridPoints;
        for (var i = 0; i < clutEntries; i++)
            for (var c = 0; c < outputChannels; c++)
                WriteU16(ms, (ushort)((i * 7 + c * 4001) % 65536));

        for (var c = 0; c < outputChannels; c++)
        {
            WriteU16(ms, 0);
            WriteU16(ms, 65535);
        }

        return ms.ToArray();
    }

    private static byte[] BuildProfile(string colorSpace, List<(string Sig, byte[] Data)> tags)
    {
        const int headerSize = 132;
        var tagTableSize = tags.Count * 12;

        using var ms = new MemoryStream();
        ms.Write(new byte[headerSize], 0, headerSize); // placeholder header
        var tableStart = ms.Position;
        ms.Write(new byte[tagTableSize], 0, tagTableSize); // placeholder tag table

        var offsets = new List<(string Sig, int Offset, int Size)>();
        foreach (var (sig, data) in tags)
        {
            offsets.Add((sig, (int)ms.Position, data.Length));
            ms.Write(data, 0, data.Length);
        }

        var bytes = ms.ToArray();

        PatchU32(bytes, 0, (uint)bytes.Length);
        PatchAscii(bytes, 16, colorSpace);
        PatchU32(bytes, 128, (uint)tags.Count);

        for (var i = 0; i < offsets.Count; i++)
        {
            var entryOffset = (int)tableStart + i * 12;
            PatchAscii(bytes, entryOffset, offsets[i].Sig);
            PatchU32(bytes, entryOffset + 4, (uint)offsets[i].Offset);
            PatchU32(bytes, entryOffset + 8, (uint)offsets[i].Size);
        }

        return bytes;
    }

    private static void WriteAscii(Stream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    private static void WriteU32(Stream s, uint v) =>
        s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }, 0, 4);

    private static void WriteU16(Stream s, ushort v) =>
        s.Write(new[] { (byte)(v >> 8), (byte)v }, 0, 2);

    private static void PatchU32(byte[] data, int offset, uint v)
    {
        data[offset] = (byte)(v >> 24); data[offset + 1] = (byte)(v >> 16);
        data[offset + 2] = (byte)(v >> 8); data[offset + 3] = (byte)v;
    }

    private static void PatchAscii(byte[] data, int offset, string text)
    {
        var b = Encoding.ASCII.GetBytes(text.PadRight(4).Substring(0, 4));
        Array.Copy(b, 0, data, offset, 4);
    }
}
