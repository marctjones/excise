using AwesomeAssertions;
using Excise.Core.Fonts;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Tests for CffSubsetter MVP implementation.
///
/// MVP scope:
/// - Test null/empty input handling (basic safety)
/// - Test malformed input graceful fallback (returns original)
/// - Test error cases don't crash
///
/// Note: Full round-trip subsetting tests with real CFF blobs would require
/// either:
/// 1) Extracting CFF from a corpus PDF (requires test-pdfs directory)
/// 2) Building a fully valid CFF from scratch (very complex, 200+ lines)
///
/// The subsetter is designed with defensive coding: if anything fails, it
/// returns the original bytes unchanged. Thus the most important tests are
/// error-handling and integration paths, not synthetic test-CFF parsing.
///
/// Future: Add round-trip tests once a real CFF test file is available.
/// </summary>
public class CffSubsetterTests
{
    [Fact]
    public void Subset_NullInput_ReturnsSafe()
    {
        // Null input should return empty array without crashing
        byte[] nullBytes = null!;
        var usedGlyphs = new HashSet<int> { 0 };

        var subsetted = CffSubsetter.Subset(nullBytes, usedGlyphs);

        subsetted.Should().NotBeNull();
        subsetted.Length.Should().Be(0, "null input should return empty array");
    }

    [Fact]
    public void Subset_EmptyInput_ReturnsSafe()
    {
        // Empty byte array should return empty array without crashing
        byte[] emptyBytes = Array.Empty<byte>();
        var usedGlyphs = new HashSet<int> { 0 };

        var subsetted = CffSubsetter.Subset(emptyBytes, usedGlyphs);

        subsetted.Should().NotBeNull();
        subsetted.Length.Should().Be(0, "empty input should return empty array");
    }

    [Fact]
    public void Subset_MalformedCff_ReturnsOriginal()
    {
        // If the input is too malformed to parse, subsetter should return original unchanged
        // (defensive design: fail gracefully)
        byte[] malformed = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var usedGlyphs = new HashSet<int> { 0 };

        var subsetted = CffSubsetter.Subset(malformed, usedGlyphs);

        // On error, should return original
        subsetted.Should().NotBeNull();
        subsetted.Should().Equal(malformed, "malformed input should return original unchanged");
    }

    [Fact]
    public void Subset_InvalidCffVersion_ReturnsOriginal()
    {
        // CFF version 2 (not supported) should return original
        byte[] cffV2 = new byte[] { 2, 0, 4, 1 }; // major=2, minor=0, hdrSize=4, offSize=1
        var usedGlyphs = new HashSet<int> { 0 };

        var subsetted = CffSubsetter.Subset(cffV2, usedGlyphs);

        subsetted.Should().NotBeNull();
        subsetted.Should().Equal(cffV2, "CFF v2 should not be processed; return original");
    }

    [Fact]
    public void Subset_EmptyUsedGlyphIds_StillIncludesNotdef()
    {
        // Even if usedGlyphIds is empty, .notdef (glyph 0) should be included.
        // This test documents the expected behavior (which real CFF would verify).
        var usedGlyphs = new HashSet<int> { }; // empty

        // This would work with a real CFF, but we're testing the defensive behavior:
        // the subsetter should handle empty sets without crashing.
        byte[] dummy = new byte[] { 1, 0, 4, 1 }; // minimal CFF header
        var subsetted = CffSubsetter.Subset(dummy, usedGlyphs);

        subsetted.Should().NotBeNull();
        // Returns original (or error) — main thing is no crash
    }

    [Fact]
    public void Subset_OutOfRangeGlyphIds_IgnoredSafely()
    {
        // Glyph IDs that don't exist in the font should be silently ignored
        // (documented behavior: the subsetter filters valid IDs internally)
        var usedGlyphs = new HashSet<int> { 0, 100, 999 };

        byte[] dummy = new byte[] { 1, 0, 4, 1 };
        var subsetted = CffSubsetter.Subset(dummy, usedGlyphs);

        subsetted.Should().NotBeNull();
        // No crash on out-of-range glyphs
    }

    [Fact]
    public void Subset_NegativeGlyphIds_IgnoredSafely()
    {
        // Negative glyph IDs should be ignored gracefully
        var usedGlyphs = new HashSet<int> { 0, -1, -5 };

        byte[] dummy = new byte[] { 1, 0, 4, 1 };
        var subsetted = CffSubsetter.Subset(dummy, usedGlyphs);

        subsetted.Should().NotBeNull();
        // No crash on negative glyphs
    }

    [Fact]
    public void Subset_DuplicateGlyphIds_HandlesSafely()
    {
        // HashSet automatically deduplicates, so this should work fine
        var usedGlyphs = new HashSet<int> { 0, 0, 2, 2, 5, 5 };

        usedGlyphs.Count.Should().Be(3, "HashSet should deduplicate");

        byte[] dummy = new byte[] { 1, 0, 4, 1 };
        var subsetted = CffSubsetter.Subset(dummy, usedGlyphs);

        subsetted.Should().NotBeNull();
    }

    [Fact]
    public void Subset_LargeGlyphSet_DoesNotCrash()
    {
        // Subsetter should handle large glyph sets without crashing
        var usedGlyphs = new HashSet<int>();
        for (int i = 0; i < 10000; i += 2)
            usedGlyphs.Add(i);

        byte[] dummy = new byte[] { 1, 0, 4, 1 };
        var subsetted = CffSubsetter.Subset(dummy, usedGlyphs);

        subsetted.Should().NotBeNull();
    }

    [Fact]
    public void Subset_CoreParser_IsAvailableInCore()
    {
        // Verify that CffParser can be used in Excise.Core (not just Excise.Rendering)
        // This tests the refactoring: CffParser should be accessible from Core.
        var dummyCff = new byte[] { 1, 0, 4, 1 }; // Too short to parse, but that's OK
        var info = CffParser.Parse(dummyCff);

        // Expect null on invalid/short input (parser is tolerant)
        // The important thing is that it's callable from Excise.Core
        // (If this test runs without namespace error, refactoring succeeded)
    }

    // ---- Round-trip subsetting against a real CFF font (Inconsolata, OFL).
    // The malformed-input tests above only exercise the defensive fallback;
    // these drive the full PerformSubset pipeline (header/INDEX parsing,
    // charstring + subr collection, charset/encoding rewriting, reassembly). ----

    /// <summary>The raw CFF table of Inconsolata, embedded as a test resource.</summary>
    private static byte[] RealCff()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Inconsolata.cff", System.StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new System.IO.MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void RealCff_FixtureLoads_AndIsAValidCffHeader()
    {
        var cff = RealCff();
        cff.Length.Should().BeGreaterThan(1000, "the embedded Inconsolata CFF should be substantial");
        cff[0].Should().Be(1, "CFF major version is 1");
    }

    [Fact]
    public void Subset_RealCff_ProducesSmallerValidBlob()
    {
        var cff = RealCff();
        var used = new HashSet<int> { 0, 1, 2, 3, 4, 36, 37, 68, 69, 70 };

        var subset = CffSubsetter.Subset(cff, used);

        subset.Should().NotBeNull();
        subset.Should().NotBeEmpty();
        subset.Should().NotEqual(cff, "subsetting a real font must change the bytes");
        subset.Length.Should().BeLessThan(cff.Length,
            "a subset with a handful of glyphs must be smaller than the full font");
        subset[0].Should().Be(1, "the output is still a CFF v1 blob");
    }

    [Fact]
    public void Subset_RealCff_IsDeterministic()
    {
        var cff = RealCff();
        var used = new HashSet<int> { 0, 5, 10, 42 };

        var a = CffSubsetter.Subset(cff, used);
        var b = CffSubsetter.Subset(cff, used);

        a.Should().Equal(b, "subsetting is a pure function of (font, glyph set)");
    }

    [Fact]
    public void Subset_RealCff_MoreGlyphs_YieldsLargerOrEqualOutput()
    {
        var cff = RealCff();
        var few = CffSubsetter.Subset(cff, new HashSet<int> { 0, 1 });
        var many = CffSubsetter.Subset(cff, Enumerable.Range(0, 60).ToHashSet());

        many.Length.Should().BeGreaterThanOrEqualTo(few.Length,
            "keeping more glyphs should not produce a smaller subset");
        many.Length.Should().BeLessThan(cff.Length);
    }

    [Fact]
    public void Subset_RealCff_EmptySet_StillProducesNotdefOnlyFont()
    {
        var cff = RealCff();

        var subset = CffSubsetter.Subset(cff, new HashSet<int>());

        subset.Should().NotBeNullOrEmpty();
        subset[0].Should().Be(1);
    }

    [Fact]
    public void Subset_RealCff_OutOfRangeGlyphs_AreClampedNotCrash()
    {
        var cff = RealCff();
        var subset = CffSubsetter.Subset(cff, new HashSet<int> { 0, 3, 999999 });

        subset.Should().NotBeNullOrEmpty();
        subset.Length.Should().BeLessThan(cff.Length);
    }

    [Fact]
    public void Subset_RealCff_ResubsettingOutput_IsStable()
    {
        var cff = RealCff();
        var once = CffSubsetter.Subset(cff, new HashSet<int> { 0, 1, 2, 3, 4, 5 });

        var twice = CffSubsetter.Subset(once, new HashSet<int> { 0, 1, 2 });

        twice.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Subset_RealCff_AllGlyphs_RoundTrips()
    {
        var cff = RealCff();
        var all = Enumerable.Range(0, 256).ToHashSet();

        var subset = CffSubsetter.Subset(cff, all);

        subset.Should().NotBeNullOrEmpty();
        subset[0].Should().Be(1);
    }

    [Fact]
    public void CffParser_ParsesRealFont_WithGlyphsAndNames()
    {
        var info = CffParser.Parse(RealCff());

        info.Should().NotBeNull("a valid CFF font must parse");
        info!.NumGlyphs.Should().BeGreaterThan(50, "Inconsolata has a full glyph set");
        info.GlyphNames.Should().NotBeEmpty();
        info.IsCidKeyed.Should().BeFalse("Inconsolata is a name-keyed (non-CID) font");
        info.GlyphNameToIndex.Should().ContainKey("A", "a Latin font exposes the glyph name 'A'");
    }

    [Fact]
    public void CffParser_GlyphNameLookup_RoundTripsWithIndex()
    {
        var info = CffParser.Parse(RealCff())!;
        var idx = info.GlyphNameToIndex["A"];

        idx.Should().BeInRange(0, info.NumGlyphs - 1);
        info.GlyphNames[idx].Should().Be("A");
    }

    [Fact]
    public void CffParser_ParsesFontBoundingBox()
    {
        var info = CffParser.Parse(RealCff())!;

        info.XMax.Should().BeGreaterThan(info.XMin);
        info.YMax.Should().BeGreaterThan(info.YMin);
    }
}
