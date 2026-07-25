using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;
using CoreCffParser = Excise.Core.Fonts.CffParser;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// The CID glyph-selection matrix (#515, final slice): CIDFontType2 and
/// CIDFontType0 glyph selection for Identity maps, explicit /CIDToGIDMap
/// streams, embedded CFF charset maps, and — the part that was genuinely
/// broken — MISSING maps.
///
/// The invariants under test, each settled against the reference renderers
/// rather than assumed (no-self-oracle — and the references did NOT behave
/// as first guessed; see the per-test comments):
///
///  - /CIDToGIDMap stream: in-range entries define the mapping (including
///    explicit 0 → .notdef); an out-of-range CID falls back to identity —
///    the behavior mutool, poppler and Ghostscript unanimously exhibit.
///  - CID-keyed CFF charset: a charset MISS selects GID 0 (.notdef), never
///    an identity fall-through into the CFF's unrelated glyph-order space
///    (which drew an arbitrary WRONG glyph before this slice) — verified
///    to match poppler and Ghostscript pixel-for-pixel.
///  - /CIDToGIDMap on a CIDFontType0 descendant is ignored (§9.7.4.2:
///    CIDFontType2 only) — matching poppler; the charset governs.
///  - Layout is driven by /W//DW keyed by CID regardless of what the CID
///    resolves to, so a missing glyph still consumes its full advance and
///    neighbouring positions never drift — drifting positions would also
///    drift redaction bounds.
///
/// Two fixture families:
///  - CIDFontType2 over the DejaVuSans.ttf fixture (GID 36='A', 37='B',
///    GID 0 = a VISIBLE .notdef tofu box — same facts the
///    RegisteredCMapRenderingTests rely on).
///  - CIDFontType0 over a synthetic CID-keyed CFF built in this file:
///    3 glyphs (.notdef = empty, glyph 1 = 600x600 square, glyph 2 =
///    300x900 tall rectangle), charset CID 7 → glyph 1, CID 9 → glyph 2.
///    The two shapes have very different ink bounding boxes, so WHICH
///    glyph was selected is provable from pixels alone.
///
/// Ground truth is independent where a reference tool renders the fixture
/// (pdftocairo/Ghostscript differentials below; mutool is skipped only
/// because mupdf 1.27 builds can lack CMap resources — its out-of-range
/// behavior agrees with the other two).
/// </summary>
public class CidGlyphSelectionMatrixTests
{
    private const int Dpi = 150;
    private const double MaxDifferingPixelFraction = 0.02;
    private const double MaxMeanAbsoluteError = 8.0;

    // DejaVuSans.ttf glyph ids (from its cmap table): 'A' → 36, 'B' → 37.
    private const ushort GidA = 36;
    private const ushort GidB = 37;

    // Synthetic CFF charset: CID 7 → glyph 1 (square), CID 9 → glyph 2
    // (tall rect). CID 2 is deliberately ABSENT — but glyph INDEX 2 exists,
    // so the pre-fix identity fall-through drew the tall rect for it.
    private const int CffCidSquare = 7;
    private const int CffCidTallRect = 9;
    private const int CffCidMissing = 2;

    // ==== CIDFontType2: explicit /CIDToGIDMap stream ==========================

    [Fact]
    public void ExplicitMapStream_RemapsCidsToGlyphs()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // CIDs 5/6 are remapped to the 'A'/'B' glyphs; identity would select
        // GIDs 5/6 (some unrelated small glyphs) instead.
        var pdf = CidType2Pdf(ttf!, MapStream(100, (5, GidA), (6, GidB)),
            codesHex: "00050006", wEntry: "5 [684 686]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.02,
            "CIDs 5/6 must go through the explicit /CIDToGIDMap stream to the 'A'/'B' glyphs");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60);
        bounds.Right.Should().BeInRange(215, 260);
        bounds.Top.Should().BeInRange(60, 100);
        bounds.Bottom.Should().BeInRange(170, 205);
    }

    // ==== CIDFontType2: /Identity name and ABSENT map =========================

    [Fact]
    public void IdentityName_UsesCidAsGid()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        var pdf = CidType2Pdf(ttf!, "/Identity",
            codesHex: "00240025", wEntry: "36 [684 686]"); // CIDs 36/37 = GIDs of A/B
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.02,
            "/CIDToGIDMap /Identity must select GID == CID");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60);
        bounds.Right.Should().BeInRange(215, 260);
    }

    [Fact]
    public void AbsentMap_DefaultsToIdentity()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        var pdf = CidType2Pdf(ttf!, cidToGidMap: null,
            codesHex: "00240025", wEntry: "36 [684 686]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.02,
            "a missing /CIDToGIDMap entry defaults to the Identity mapping (§9.7.4.2)");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60);
        bounds.Right.Should().BeInRange(215, 260);
    }

    // ==== CIDFontType2: MISSING map entries (the fixed bug) ===================

    [Fact]
    public void TruncatedMapStream_OutOfRangeCid_FallsBackToIdentity_MatchingReferences()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // The map covers only CIDs 0..9; code <0024> is CID 36, beyond the
        // stream's extent. §9.7.4.2 defines only in-range entries, so the
        // out-of-range behavior was settled EMPIRICALLY against the
        // references: mutool, poppler AND Ghostscript all fall back to
        // GID == CID and draw the real 'A' for this fixture (pinned live by
        // TruncatedMap_MatchesLive* below). Matching that consensus is the
        // contract — NOT .notdef, which no installed reference draws here.
        var truncated = CidType2Pdf(ttf!, MapStream(10),
            codesHex: "0024", wEntry: "36 [684]");
        // Control 1: identity — what the references (and we) render.
        var identityA = CidType2Pdf(ttf!, "/Identity",
            codesHex: "0024", wEntry: "36 [684]");
        // Control 2: an IN-RANGE zero entry — .notdef, which must NOT be what
        // an out-of-range CID renders as.
        var explicitNotdef = CidType2Pdf(ttf!, MapStream(100),
            codesHex: "0024", wEntry: "36 [684]");

        using var truncatedBmp = Render(truncated);
        using var identityBmp = Render(identityA);
        using var notdefBmp = Render(explicitNotdef);

        InkFraction(truncatedBmp).Should().BeGreaterThan(0.001);

        var vsIdentity = DifferentialMetrics.Compare(truncatedBmp, identityBmp);
        vsIdentity.DifferingPixelFraction.Should().BeLessThan(0.001,
            "an out-of-range CID must render exactly like the identity mapping — " +
            "the unanimous reference-renderer behavior");

        var vsNotdef = DifferentialMetrics.Compare(truncatedBmp, notdefBmp);
        vsNotdef.DifferingPixelFraction.Should().BeGreaterThan(0.02,
            "an out-of-range CID must NOT collapse to .notdef — only an in-range zero " +
            "entry selects GID 0 from a map stream");
    }

    [Fact]
    public void InRangeZeroMapEntry_SelectsNotdef()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // CID 36 is IN range of the 100-entry map and its entry is 0: that is
        // a DEFINED mapping to GID 0 — DejaVu's visible .notdef tofu box,
        // materially different pixels from the real 'A'.
        var notdef = CidType2Pdf(ttf!, MapStream(100),
            codesHex: "0024", wEntry: "36 [684]");
        var identityA = CidType2Pdf(ttf!, "/Identity",
            codesHex: "0024", wEntry: "36 [684]");
        using var notdefBmp = Render(notdef);
        using var identityBmp = Render(identityA);

        InkFraction(notdefBmp).Should().BeGreaterThan(0.001,
            "GID 0 in DejaVuSans is a visible .notdef box, not blank");
        var report = DifferentialMetrics.Compare(notdefBmp, identityBmp);
        report.DifferingPixelFraction.Should().BeGreaterThan(0.02,
            "the .notdef box must not look like the real 'A' — an explicit zero entry " +
            "defines the mapping and must not be second-guessed into identity");
    }

    [Fact]
    public void TruncatedMapStream_OutOfRangeCid_StillConsumesAdvance()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // First CID (36) is beyond the 10-entry map → identity 'A'; second
        // CID (5) maps to 'B'. /W gives CID 36 a 684-thousandths advance, so
        // 'B' must start one full advance right: 20pt + 49.2pt ≈ 69pt ≈
        // 144px at 150 DPI. A right edge near 130px would mean the first
        // code's advance was dropped and every subsequent position (and
        // redaction bound) drifted left.
        var pdf = CidType2Pdf(ttf!, MapStream(10, (5, GidB)),
            codesHex: "00240005", wEntry: "36 [684] 5 [686]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.001);
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60,
            "the identity-mapped 'A' renders at the first cell");
        bounds.Right.Should().BeInRange(190, 250,
            "the mapped 'B' must start after the first code's full /W advance");
    }

    [Fact]
    public void AllZeroMap_NotdefEverywhere_AdvancesPreserved()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // Every CID maps to GID 0: two tofu boxes, each consuming its /W
        // advance — the second box must sit one advance right of the first.
        var pdf = CidType2Pdf(ttf!, MapStream(100),
            codesHex: "00240025", wEntry: "36 [684 686]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.001,
            "an all-zero map draws .notdef boxes, not a blank page");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60);
        bounds.Right.Should().BeGreaterThan(180,
            "the second .notdef box must be placed after the first glyph's full /W advance");
    }

    [Fact]
    public void OddLengthMapStream_TruncatesGracefully()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // 40 complete big-endian entries (CID 36 → GID 36) plus one dangling
        // byte. The dangling byte is ignored; the mapped CID still renders.
        var map = MapStream(40, (36, GidA));
        var oddMap = map.Concat(new byte[] { 0xAB }).ToArray();
        var pdf = CidType2Pdf(ttf!, oddMap, codesHex: "0024", wEntry: "36 [684]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.02,
            "an odd-length /CIDToGIDMap stream must not crash or blank the page — " +
            "complete entries still apply, the dangling byte is ignored");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60);
        bounds.Right.Should().BeInRange(100, 160); // a single 'A'
    }

    [Fact]
    public void GidBeyondGlyphCount_DrawsNothing_ButConsumesAdvance()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // Identity mapping, CID 60000 — far beyond DejaVuSans's glyph count.
        // No glyph exists to draw, but /W still gives the code its 684
        // advance, so the following 'B' (CID 37) starts one advance right.
        var pdf = CidType2Pdf(ttf!, "/Identity",
            codesHex: "EA600025", wEntry: "37 [686] 60000 [684]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.001, "the 'B' must render");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeGreaterThan(120,
            "nothing may be drawn in the first cell for a GID beyond the glyph count");
        bounds.Right.Should().BeInRange(190, 250,
            "the 'B' must start after the missing glyph's full /W advance");
    }

    // ==== CIDFontType2: independent references (no-self-oracle) ===============

    [Fact]
    public void TruncatedMap_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // Out-of-range CID 36 against a 10-entry map, then a mapped 'B'.
        // poppler resolves the out-of-range CID as identity (GID 36, a real
        // 'A'); agreeing with it live pins both the identity choice AND the
        // preserved /W advance of the first code.
        var pdf = CidType2Pdf(ttf!, MapStream(10, (5, GidB)),
            codesHex: "00240005", wEntry: "36 [684] 5 [686]");
        var path = WriteTemp(pdf);
        try
        {
            using var excise = Render(pdf);
            using var reference = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(reference == null, "pdftocairo declined to render the fixture.");

            using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
            var report = DifferentialMetrics.Compare(aligned, reference);
            report.DifferingPixelFraction.Should().BeLessThan(MaxDifferingPixelFraction,
                "excise must resolve an out-of-range /CIDToGIDMap CID the way poppler does " +
                $"(differing={report.DifferingPixelFraction:P2}, MAE={report.MeanAbsoluteError:F1})");
            report.MeanAbsoluteError.Should().BeLessThan(MaxMeanAbsoluteError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TruncatedMap_MatchesLiveGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        var pdf = CidType2Pdf(ttf!, MapStream(10, (5, GidB)),
            codesHex: "00240005", wEntry: "36 [684] 5 [686]");
        var path = WriteTemp(pdf);
        try
        {
            using var excise = Render(pdf);
            using var reference = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(reference == null, "ghostscript declined to render the fixture.");

            using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
            var report = DifferentialMetrics.Compare(aligned, reference);
            report.DifferingPixelFraction.Should().BeLessThan(MaxDifferingPixelFraction,
                "excise must resolve an out-of-range /CIDToGIDMap CID the way Ghostscript does " +
                $"(differing={report.DifferingPixelFraction:P2}, MAE={report.MeanAbsoluteError:F1})");
            report.MeanAbsoluteError.Should().BeLessThan(MaxMeanAbsoluteError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==== CIDFontType0: embedded CFF charset ==================================

    [Fact]
    public void CffCharset_NonIdentityCharset_SelectsGlyphViaCharset()
    {
        var pdf = CidType0Pdf(BuildCidKeyedCff(),
            codesHex: Hex4(CffCidSquare), wEntry: $"{CffCidSquare} [700]");
        using var bmp = Render(pdf);

        // Glyph 1 (600x600 square at glyph-space (100,100)-(700,700)) at 72pt
        // from Td 20 30 → device ≈ x 57..147, y 82..173 at 150 DPI. An
        // identity misread (GID 7 in a 3-glyph font) would draw nothing.
        InkFraction(bmp).Should().BeGreaterThan(0.01,
            "CID 7 must resolve through the embedded CFF charset to glyph 1 (the square) — " +
            "a blank page means the charset mapping was ignored");
        var bounds = InkBounds(bmp);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        width.Should().BeInRange(70, 110, "the square is ≈90px wide");
        height.Should().BeInRange(70, 110, "the square is ≈90px tall");
    }

    [Fact]
    public void CffCharset_SecondGlyph_HasDistinctShape()
    {
        var pdf = CidType0Pdf(BuildCidKeyedCff(),
            codesHex: Hex4(CffCidTallRect), wEntry: $"{CffCidTallRect} [700]");
        using var bmp = Render(pdf);

        // Glyph 2 is a 300x900 tall rectangle — provably NOT the square.
        InkFraction(bmp).Should().BeGreaterThan(0.01);
        var bounds = InkBounds(bmp);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        width.Should().BeInRange(30, 60, "the tall rect is ≈45px wide");
        height.Should().BeInRange(115, 155, "the tall rect is ≈135px tall");
    }

    [Fact]
    public void CffCharset_MissingCid_DrawsNotdefNotIdentityGlyph()
    {
        // CID 2 is NOT in the charset, but glyph INDEX 2 exists (the tall
        // rect). The pre-fix identity fall-through indexed the CFF glyph
        // order with the CID and drew the tall rect — an arbitrary WRONG
        // glyph from an unrelated numbering space. The defined behavior is
        // GID 0, this font's .notdef, which is EMPTY → no ink at all.
        var pdf = CidType0Pdf(BuildCidKeyedCff(),
            codesHex: Hex4(CffCidMissing), wEntry: $"{CffCidMissing} [700]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeLessThan(0.0005,
            "a CID absent from the CFF charset must select .notdef (empty in this font) — " +
            "any ink here means the CID was misused as a glyph index and a WRONG glyph " +
            "was drawn (#515)");
    }

    [Fact]
    public void CffCharset_MissingCid_StillConsumesAdvance()
    {
        // Missing CID 2 (.notdef, no ink) followed by CID 7 (square). The
        // square must start one full /W advance (700/1000 * 72pt = 50.4pt ≈
        // 105px) right of the text origin: left edge ≈ 57 + 105 = 162px.
        var pdf = CidType0Pdf(BuildCidKeyedCff(),
            codesHex: Hex4(CffCidMissing) + Hex4(CffCidSquare),
            wEntry: $"{CffCidMissing} [700] {CffCidSquare} [700]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.01, "the square must render");
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(145, 180,
            "the square must start after the missing CID's full /W advance — further left " +
            "means either the missing CID drew ink or its advance was dropped");
    }

    [Fact]
    public void CffCidFontType0_BogusCidToGidMapStream_IsIgnored()
    {
        // §9.7.4.2: /CIDToGIDMap shall be used only with CIDFontType2. This
        // CIDFontType0's bogus stream maps CID 7 → GID 2 (the tall rect);
        // honoring it would draw the wrong glyph. The embedded CFF charset
        // (CID 7 → glyph 1, the square) must govern.
        var bogusMap = MapStream(100, (CffCidSquare, 2));
        var withMap = CidType0Pdf(BuildCidKeyedCff(),
            codesHex: Hex4(CffCidSquare), wEntry: $"{CffCidSquare} [700]",
            cidToGidMap: bogusMap);
        var withoutMap = CidType0Pdf(BuildCidKeyedCff(),
            codesHex: Hex4(CffCidSquare), wEntry: $"{CffCidSquare} [700]");

        using var withMapBmp = Render(withMap);
        using var withoutMapBmp = Render(withoutMap);

        InkFraction(withMapBmp).Should().BeGreaterThan(0.01);
        var report = DifferentialMetrics.Compare(withMapBmp, withoutMapBmp);
        report.DifferingPixelFraction.Should().BeLessThan(0.001,
            "a /CIDToGIDMap on a CIDFontType0 descendant must be ignored — the render " +
            "must be identical to the map-less fixture, with glyph selection via the " +
            "CFF charset (§9.7.4.2)");
    }

    // ==== CIDFontType0: name-keyed (non-CID) CFF fallback =====================

    [Fact]
    public void NameKeyedCff_AsCidFontType0_FallsBackToIdentityGids()
    {
        // A name-keyed (non-CID) Type1C program shipped as a CIDFontType0C
        // descendant has no CID charset at all; references treat the CID as
        // the glyph index directly. Look up a real glyph index from the
        // Inconsolata fixture's own name table so the assertion doesn't
        // depend on hardcoded internals of the fixture font.
        var cff = LoadFixtureFont("Inconsolata.cff");
        Assert.SkipWhen(cff == null, "Inconsolata.cff fixture missing.");

        var info = CoreCffParser.Parse(cff!);
        Assert.SkipWhen(info == null || info.IsCidKeyed, "fixture unexpectedly unusable as name-keyed CFF.");
        Assert.SkipWhen(!info!.GlyphNameToIndex.TryGetValue("A", out _), "fixture has no 'A' glyph.");
        var gidOfA = info.GlyphNameToIndex["A"];

        var pdf = CidType0Pdf(cff!,
            codesHex: Hex4(gidOfA), wEntry: $"{gidOfA} [500]");
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.005,
            "a name-keyed CFF used as CIDFontType0 must fall back to identity CID→GID " +
            "and draw the glyph at index CID — a blank page means the fallback was lost");
    }

    // ==== fixtures ============================================================

    private static string Hex4(int cid) => cid.ToString("X4");

    /// <summary>Big-endian uint16-per-CID /CIDToGIDMap stream covering
    /// <paramref name="cids"/> entries; unlisted entries are 0 (.notdef).</summary>
    private static byte[] MapStream(int cids, params (int Cid, ushort Gid)[] entries)
    {
        var map = new byte[cids * 2];
        foreach (var (cid, gid) in entries)
        {
            map[cid * 2] = (byte)(gid >> 8);
            map[cid * 2 + 1] = (byte)gid;
        }
        return map;
    }

    /// <summary>
    /// Identity-H Type0 font over an embedded CIDFontType2 (TrueType) drawing
    /// <paramref name="codesHex"/> at 72pt from Td 20 30 on a 300x120 page.
    /// <paramref name="cidToGidMap"/>: byte[] → /CIDToGIDMap stream,
    /// string → name (e.g. "/Identity"), null → entry absent.
    /// </summary>
    private static byte[] CidType2Pdf(
        byte[] ttf, object? cidToGidMap, string codesHex, string wEntry)
    {
        var mapEntry = cidToGidMap switch
        {
            byte[] => "/CIDToGIDMap 9 0 R ",
            string name => $"/CIDToGIDMap {name} ",
            _ => string.Empty,
        };

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes($"BT /F1 72 Tf 20 30 Td <{codesHex}> Tj ET")); // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-CID "
              + "/Encoding /Identity-H /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + $"/FontDescriptor 7 0 R {mapEntry}/DW 1000 /W [{wEntry}] >>");               // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        if (cidToGidMap is byte[] mapBytes)
            pdf.Add("<< >>", mapBytes);                                                      // 9
        return pdf.Build(1);
    }

    /// <summary>
    /// Identity-H Type0 font over an embedded CIDFontType0 whose program is
    /// <paramref name="cff"/> (/FontFile3 /Subtype /CIDFontType0C), same page
    /// layout as <see cref="CidType2Pdf"/>. <paramref name="cidToGidMap"/>
    /// optionally attaches a (spec-invalid, must-be-ignored) map stream.
    /// </summary>
    private static byte[] CidType0Pdf(
        byte[] cff, string codesHex, string wEntry, byte[]? cidToGidMap = null)
    {
        var mapEntry = cidToGidMap != null ? "/CIDToGIDMap 9 0 R " : string.Empty;

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes($"BT /F1 72 Tf 20 30 Td <{codesHex}> Tj ET")); // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestCid "
              + "/Encoding /Identity-H /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /TestCid "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + $"/FontDescriptor 7 0 R {mapEntry}/DW 700 /W [{wEntry}] >>");                // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestCid /Flags 4 "
              + "/FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile3 8 0 R >>");                             // 7
        pdf.Add("<< /Subtype /CIDFontType0C >>", cff);                                       // 8
        if (cidToGidMap != null)
            pdf.Add("<< >>", cidToGidMap);                                                   // 9
        return pdf.Build(1);
    }

    // ==== synthetic CID-keyed CFF =============================================

    /// <summary>
    /// A minimal, valid CID-keyed CFF (Adobe TN #5176): 3 glyphs —
    /// glyph 0 = .notdef (empty charstring), glyph 1 = 600x600 square at
    /// (100,100), glyph 2 = 300x900 tall rectangle at (100,50) — with a
    /// format-0 charset mapping glyph 1 → CID 7 and glyph 2 → CID 9.
    /// Top DICT carries /ROS (custom strings "Adobe"/"Identity"), FDArray
    /// with one Font DICT and a shared Private DICT, and FDSelect format 3.
    /// All DICT offsets use the 5-byte (0x1D) integer encoding so section
    /// sizes are computable before offsets are known.
    /// </summary>
    internal static byte[] BuildCidKeyedCff(
        int cidForGlyph1 = CffCidSquare, int cidForGlyph2 = CffCidTallRect)
    {
        // Charstrings (Type 2). Even-argument rmoveto → no leading width →
        // width = defaultWidthX (700) from the Private DICT.
        byte[] notdef = { 0x0E };                                     // endchar
        byte[] square =
        {
            0xEF, 0xEF, 0x15,       // 100 100 rmoveto
            0xF8, 0xEC, 0x06,       // 600 hlineto
            0xF8, 0xEC, 0x07,       // 600 vlineto
            0xFC, 0xEC, 0x06,       // -600 hlineto
            0x0E,                   // endchar (auto-close)
        };
        byte[] tallRect =
        {
            0xEF, 0xBD, 0x15,       // 100 50 rmoveto
            0xF7, 0xC0, 0x06,       // 300 hlineto
            0xFA, 0x18, 0x07,       // 900 vlineto
            0xFB, 0xC0, 0x06,       // -300 hlineto
            0x0E,                   // endchar
        };

        byte[] header = { 0x01, 0x00, 0x04, 0x04 };
        byte[] nameIndex = BuildIndex(Encoding.ASCII.GetBytes("TestCid"));
        byte[] stringIndex = BuildIndex(
            Encoding.ASCII.GetBytes("Adobe"),      // SID 391
            Encoding.ASCII.GetBytes("Identity"));  // SID 392
        byte[] gsubrIndex = { 0x00, 0x00 };        // empty INDEX

        // charset format 0: glyphs 1..n-1 as big-endian uint16 CIDs.
        byte[] charset =
        {
            0x00,
            (byte)(cidForGlyph1 >> 8), (byte)cidForGlyph1,
            (byte)(cidForGlyph2 >> 8), (byte)cidForGlyph2,
        };

        // FDSelect format 3: one range, all 3 glyphs → FD 0.
        byte[] fdSelect = { 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x03 };

        byte[] charStrings = BuildIndex(notdef, square, tallRect);

        // Private DICT: defaultWidthX 700 (op 20), nominalWidthX 700 (op 21).
        byte[] privateDict = Concat(Int5(700), new byte[] { 0x14 }, Int5(700), new byte[] { 0x15 });

        // Font DICT (inside FDArray): Private [size offset] (op 18).
        // 11 bytes with 5-byte ints; FDArray INDEX adds 5 bytes of framing.
        byte[] FontDict(int privOffset) =>
            Concat(Int5(privateDict.Length), Int5(privOffset), new byte[] { 0x12 });
        int fdArrayLen = BuildIndex(FontDict(0)).Length;

        // Top DICT (fixed size — every operand is a 5-byte int):
        //   ROS(12 30), FontBBox(5), CIDCount(12 34),
        //   charset(15), CharStrings(17), FDArray(12 36), FDSelect(12 37)
        byte[] TopDict(int charsetOff, int charStringsOff, int fdArrayOff, int fdSelectOff) =>
            Concat(
                Int5(391), Int5(392), Int5(0), new byte[] { 0x0C, 0x1E },   // ROS
                Int5(0), Int5(0), Int5(1000), Int5(1000), new byte[] { 0x05 }, // FontBBox
                Int5(65535), new byte[] { 0x0C, 0x22 },                     // CIDCount
                Int5(charsetOff), new byte[] { 0x0F },                      // charset
                Int5(charStringsOff), new byte[] { 0x11 },                  // CharStrings
                Int5(fdArrayOff), new byte[] { 0x0C, 0x24 },                // FDArray
                Int5(fdSelectOff), new byte[] { 0x0C, 0x25 });              // FDSelect
        int topDictIndexLen = BuildIndex(TopDict(0, 0, 0, 0)).Length;

        // Layout: header, Name INDEX, Top DICT INDEX, String INDEX, GSubr
        // INDEX, charset, FDSelect, CharStrings INDEX, FDArray, Private DICT.
        int charsetOffset = header.Length + nameIndex.Length + topDictIndexLen
            + stringIndex.Length + gsubrIndex.Length;
        int fdSelectOffset = charsetOffset + charset.Length;
        int charStringsOffset = fdSelectOffset + fdSelect.Length;
        int fdArrayOffset = charStringsOffset + charStrings.Length;
        int privateOffset = fdArrayOffset + fdArrayLen;

        var topDictIndex = BuildIndex(
            TopDict(charsetOffset, charStringsOffset, fdArrayOffset, fdSelectOffset));
        var fdArray = BuildIndex(FontDict(privateOffset));

        return Concat(header, nameIndex, topDictIndex, stringIndex, gsubrIndex,
            charset, fdSelect, charStrings, fdArray, privateDict);
    }

    /// <summary>CFF INDEX with offSize 1 (fine for these tiny payloads).</summary>
    private static byte[] BuildIndex(params byte[][] items)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(items.Length >> 8));
        ms.WriteByte((byte)items.Length);
        ms.WriteByte(0x01); // offSize
        int offset = 1;
        ms.WriteByte((byte)offset);
        foreach (var item in items)
        {
            offset += item.Length;
            ms.WriteByte((byte)offset);
        }
        foreach (var item in items)
            ms.Write(item, 0, item.Length);
        return ms.ToArray();
    }

    /// <summary>5-byte CFF DICT integer (0x1D + int32 big-endian).</summary>
    private static byte[] Int5(int v) => new byte[]
    {
        0x1D, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
    };

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, pos, part.Length);
            pos += part.Length;
        }
        return result;
    }

    private static string WriteTemp(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cid-matrix-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // ==== rendering + measurement =============================================

    private static SKBitmap Render(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static bool IsInk(SKColor p) => p.Red < 128 && p.Green < 128 && p.Blue < 128;

    private static double InkFraction(SKBitmap b)
    {
        long ink = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (IsInk(b.GetPixel(x, y))) ink++;
        return (double)ink / (b.Width * (long)b.Height);
    }

    private static SKRectI InkBounds(SKBitmap b)
    {
        int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (IsInk(b.GetPixel(x, y)))
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static byte[]? LoadFixtureFont(string name)
    {
        var path = FindRepoFile("Excise.Core.Tests", "Fixtures", "Fonts", name);
        return path == null ? null : File.ReadAllBytes(path);
    }

    private static string? FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // Minimal PDF assembler (same shape as RegisteredCMapRenderingTests'):
    // sequential objects, auto /Length for streams, classic xref + trailer.
    private sealed class MinimalPdf
    {
        private readonly List<(string Dict, byte[]? Stream)> _objs = new();

        public int Add(string dict, byte[]? stream = null)
        {
            _objs.Add((dict, stream));
            return _objs.Count;
        }

        public byte[] Build(int rootObj)
        {
            using var ms = new MemoryStream();
            void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

            W("%PDF-1.7\n");
            var offsets = new long[_objs.Count + 1];
            for (int i = 0; i < _objs.Count; i++)
            {
                int n = i + 1;
                offsets[n] = ms.Position;
                var (dict, stream) = _objs[i];
                if (stream != null)
                {
                    int close = dict.LastIndexOf(">>", StringComparison.Ordinal);
                    dict = dict.Substring(0, close) + $" /Length {stream.Length} " + dict.Substring(close);
                }
                W($"{n} 0 obj\n{dict}\n");
                if (stream != null)
                {
                    W("stream\n");
                    ms.Write(stream, 0, stream.Length);
                    W("\nendstream\n");
                }
                W("endobj\n");
            }

            long xref = ms.Position;
            W($"xref\n0 {_objs.Count + 1}\n0000000000 65535 f \n");
            for (int n = 1; n <= _objs.Count; n++)
                W($"{offsets[n]:D10} 00000 n \n");
            W($"trailer\n<< /Root {rootObj} 0 R /Size {_objs.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
            return ms.ToArray();
        }
    }
}
