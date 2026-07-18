using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Rendering.Fonts;
using SkiaSharp;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Direct tests on the CFF parser and SFNT wrapper using a real embedded
/// CFF font ripped out of a corpus PDF. The goal is to pinpoint which layer
/// (CFF parsing, SFNT synthesis, or Skia's reception) breaks when the
/// wrapped font is used for rendering, without having to go through the
/// full page-render pipeline.
/// </summary>
public class CffWrapperTests
{
    private readonly ITestOutputHelper _output;

    public CffWrapperTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Wrapped_CffSkiaCanResolveKnownGlyphs()
    {
        var cffBytes = TryExtractCdcType1C();
        if (cffBytes == null)
        {
            _output.WriteLine("Corpus missing or no matching CFF font; skipping.");
            return;
        }

        var cffInfo = CffParser.Parse(cffBytes);
        cffInfo.Should().NotBeNull();
        _output.WriteLine($"numGlyphs={cffInfo!.NumGlyphs}");
        for (int i = 0; i < cffInfo.GlyphNames.Length; i++)
            _output.WriteLine($"  glyph {i}: {cffInfo.GlyphNames[i]}");

        // Build a cmap that maps each ASCII letter with a known name to its
        // glyph index, then wrap and load.
        var unicodeToGlyph = new Dictionary<char, int>();
        foreach (var kvp in cffInfo.GlyphNameToIndex)
        {
            // Single-char ASCII glyph names (e.g. "A", "C", "V").
            if (kvp.Key.Length == 1)
            {
                unicodeToGlyph[kvp.Key[0]] = kvp.Value;
            }
            else if (AdobeGlyphList.TryGet(kvp.Key, out var u))
            {
                unicodeToGlyph[u] = kvp.Value;
            }
        }

        var info = new CffToOpenType.PdfFontInfo
        {
            PsName = "TestCff",
            UnicodeToGlyph = unicodeToGlyph,
        };
        var sfnt = CffToOpenType.Wrap(cffBytes, cffInfo.NumGlyphs, info);
        sfnt.Should().NotBeNull();

        // Save for external validation with ots-sanitize / ttx.
        var dumpPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "test-output", "cdc-wrapped.otf");
        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
        File.WriteAllBytes(dumpPath, sfnt!);
        _output.WriteLine($"wrote {sfnt.Length} bytes to {dumpPath}");

        using var data = SKData.CreateCopy(sfnt);
        using var typeface = SKTypeface.FromData(data);
        typeface.Should().NotBeNull("Skia should accept the wrapped SFNT");

        // Probe a handful of Unicode characters and see what Skia resolves them
        // to. A zero result means our cmap didn't register that codepoint.
        foreach (char c in "COVID19Vaccine")
        {
            if (!unicodeToGlyph.TryGetValue(c, out var expected)) continue;
            ushort skiaGlyph = typeface!.GetGlyph(c);
            _output.WriteLine($"'{c}' (U+{(int)c:X4}): expected {expected}, Skia returned {skiaGlyph}");
        }
    }

    private static byte[]? TryExtractCdcType1C()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "test-pdfs", "smoke")))
            dir = dir.Parent;
        if (dir == null) return null;

        var pdfPath = Path.Combine(dir.FullName, "test-pdfs", "smoke", "cdc-vis-covid-19.pdf");
        if (!File.Exists(pdfPath)) return null;

        using var doc = PdfDocument.Open(pdfPath);
        var page = doc.GetPage(1);
        var fonts = page.Resources?.GetDictionaryOrNull("Font");
        if (fonts == null) return null;

        foreach (var keyName in fonts.Keys)
        {
            var fontDict = page.GetFont(keyName.Value);
            if (fontDict == null) continue;
            var descObj = fontDict.GetOptional("FontDescriptor");
            if (descObj == null) continue;
            if (doc.Resolve(descObj) is not Excise.Core.Primitives.PdfDictionary desc) continue;
            var ff3Obj = desc.GetOptional("FontFile3");
            if (ff3Obj == null) continue;
            if (doc.Resolve(ff3Obj) is not Excise.Core.Primitives.PdfStream stream) continue;
            if (stream.GetNameOrNull("Subtype") != "Type1C") continue;
            return stream.DecodedData;
        }
        return null;
    }
}
