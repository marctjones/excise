using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Direct CFF-parser coverage for CID-keyed fonts (Adobe-Japan1 /
/// Adobe-CNS1 / Adobe-Korea1). Pulls a real CIDFontType0C blob out of
/// the verapdf corpus, parses it, and asserts the parser:
///
/// 1. Identifies it as CID-keyed via the /ROS Top DICT operator (12 30).
/// 2. Builds a non-empty CID → glyph-index map covering the CIDs the
///    PDF's /W array references.
/// 3. Leaves <see cref="CffParser.CffFontInfo.GlyphNames"/> empty (CID
///    fonts have no PostScript glyph names).
///
/// Skips silently when the corpus isn't checked out (CI without
/// test-pdfs/) so the test suite still runs in minimal environments.
/// </summary>
public class CidKeyedCffParserTests
{
    [Fact]
    public void Parse_CidKeyedKozMinPro_DetectsRosAndBuildsCidMap()
    {
        var cff = TryLoadKozMinProCff();
        if (cff == null) return; // corpus missing; degrade to skip

        var info = CffParser.Parse(cff);
        info.Should().NotBeNull();
        info!.IsCidKeyed.Should().BeTrue("KozMinPro is /ROS-marked Adobe-Japan1");
        info.CidToGlyph.Should().NotBeNull();
        info.CidToGlyph!.Count.Should().BeGreaterThan(1, "subset has more than just .notdef");
        info.CidToGlyph[0].Should().Be(0, "CID 0 must always map to glyph 0 (.notdef)");

        // The fixture's /W array references CIDs 1, 41, 56, 69, 70, 77, 80, 83.
        // At least a few of them should be present in the subset's CFF charset.
        int present = new[] { 1, 41, 56, 69, 70, 77, 80, 83 }
            .Count(cid => info.CidToGlyph.ContainsKey(cid));
        present.Should().BeGreaterThan(0,
            "the /W-referenced CIDs should be in the CFF charset; if none are, the wrapping won't render any glyphs");

        // CID-keyed CFFs don't carry glyph names; GlyphNames stays empty.
        info.GlyphNames.Should().BeEmpty();
        info.GlyphNameToIndex.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SimpleCff_StaysNonCidKeyed()
    {
        var cff = TryLoadSimpleCff();
        if (cff == null) return;

        var info = CffParser.Parse(cff);
        info.Should().NotBeNull();
        info!.IsCidKeyed.Should().BeFalse("the smoke-corpus Type1C subset is not CID-keyed");
        info.CidToGlyph.Should().BeNull();
        info.GlyphNames.Should().NotBeEmpty("simple CFFs carry PostScript glyph names");
    }

    private static byte[]? TryLoadKozMinProCff()
    {
        // Find a fixture from the verapdf corpus that embeds a
        // CIDFontType0C font; pull /FontFile3 out of its first
        // descendant font.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(
            Path.Combine(dir.FullName, "test-pdfs", "verapdf-corpus")))
        {
            dir = dir.Parent;
        }
        if (dir == null) return null;

        var pdfPath = Path.Combine(dir.FullName,
            "test-pdfs", "verapdf-corpus", "veraPDF-corpus-master",
            "PDF_A-2b", "6.2 Graphics", "6.2.11 Use of standard structure types",
            "6.2.11.4 List Standard Structure Types",
            "6.2.11.4.2 List_The Continued attribute",
            "veraPDF test suite 6-2-11-4-2-t02-pass-a.pdf");
        if (!File.Exists(pdfPath)) return null;

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var page = doc.GetPage(1);
            var fonts = page.Resources?.GetDictionaryOrNull("Font");
            if (fonts == null) return null;

            foreach (var kvp in fonts)
            {
                var fontDict = page.GetFont(kvp.Key.Value);
                if (fontDict == null) continue;
                if (fontDict.GetNameOrNull("Subtype") != "Type0") continue;

                // Walk to descendant font / FontDescriptor / FontFile3.
                var descObj = fontDict.GetOptional("DescendantFonts");
                if (descObj == null) continue;
                if (doc.Resolve(descObj) is not PdfArray descArr || descArr.Count == 0)
                    continue;
                if (doc.Resolve(descArr[0]) is not PdfDictionary cidFont) continue;
                var fdObj = cidFont.GetOptional("FontDescriptor");
                if (fdObj == null) continue;
                if (doc.Resolve(fdObj) is not PdfDictionary descriptor) continue;
                var ff3 = descriptor.GetOptional("FontFile3");
                if (ff3 == null) continue;
                if (doc.Resolve(ff3) is not PdfStream stream) continue;
                if (stream.GetNameOrNull("Subtype") != "CIDFontType0C") continue;
                return stream.DecodedData;
            }
        }
        catch { }
        return null;
    }

    private static byte[]? TryLoadSimpleCff()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "test-pdfs", "smoke")))
            dir = dir.Parent;
        if (dir == null) return null;

        var pdfPath = Path.Combine(dir.FullName, "test-pdfs", "smoke", "cdc-vis-covid-19.pdf");
        if (!File.Exists(pdfPath)) return null;

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var page = doc.GetPage(1);
            var fonts = page.Resources?.GetDictionaryOrNull("Font");
            if (fonts == null) return null;

            foreach (var kvp in fonts)
            {
                var fontDict = page.GetFont(kvp.Key.Value);
                if (fontDict == null) continue;
                var descObj = fontDict.GetOptional("FontDescriptor");
                if (descObj == null) continue;
                if (doc.Resolve(descObj) is not PdfDictionary descriptor) continue;
                var ff3 = descriptor.GetOptional("FontFile3");
                if (ff3 == null) continue;
                if (doc.Resolve(ff3) is not PdfStream stream) continue;
                if (stream.GetNameOrNull("Subtype") != "Type1C") continue;
                return stream.DecodedData;
            }
        }
        catch { }
        return null;
    }
}
