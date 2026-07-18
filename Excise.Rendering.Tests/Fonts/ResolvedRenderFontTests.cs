using AwesomeAssertions;
using Excise.Core.Primitives;
using Excise.Rendering.Fonts;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Focused tests for <see cref="ResolvedRenderFont"/> (#513) — the single
/// resolved-font object that replaced the 18 scattered <c>_current*</c>
/// fields in <c>SkiaRenderer</c>. The resolution branches themselves (simple
/// Type1/TrueType/CFF, Type0 Identity-H, CIDToGIDMap, CFF CID-keyed,
/// format-0 cmap fallback, Type3) live in private instance methods on the
/// internal <c>RenderContext</c> partial class and are exercised end-to-end
/// by the existing Differential/Visual/Core suites (unchanged branch-for-
/// branch by this refactor — see docs/RELEASE_CHECKLIST.md's local
/// visual-regression gate). What's genuinely new here is the record itself:
/// its forwarding shape onto <see cref="ResolvedPdfFont"/> and its
/// immutability contract, both load-bearing for the save/restore sites this
/// refactor collapsed to plain reference assignment.
/// </summary>
public class ResolvedRenderFontTests
{
    private static ResolvedPdfFont MakePdfFont(
        string subtype = "Type1",
        float[]? widths = null,
        int firstChar = 0,
        float missingWidth = 0f,
        string encodingName = "WinAnsiEncoding")
    {
        var font = new PdfDictionary
        {
            ["Subtype"] = new PdfName(subtype),
            ["BaseFont"] = new PdfName("TestFont"),
        };
        if (widths != null)
        {
            font["FirstChar"] = new PdfInteger(firstChar);
            font["Widths"] = new PdfArray(widths.Select(w => (PdfObject)new PdfReal(w)).ToArray());
        }

        return PdfFontResolver.Resolve("F1", font) with { EncodingName = encodingName, MissingWidth = missingWidth };
    }

    [Fact]
    public void ForwardingProperties_ReadThroughToWrappedPdfFont()
    {
        var pdf = MakePdfFont(widths: new[] { 250f, 500f }, firstChar: 32, missingWidth: 375f, encodingName: "MacRomanEncoding");
        var dict = pdf.Dictionary;

        var resolved = new ResolvedRenderFont(
            pdf,
            CodeToUnicode: null,
            UnicodeToCode: null,
            CodeToGlyphName: null,
            Typeface: null,
            ByteToGlyph: null,
            HasEmbeddedProgram: false,
            CidWidths: null,
            CidDefaultWidth: 1000f,
            CidUseUnicodeCmap: false,
            CidEncodingCMap: null,
            CidToGidMap: null,
            CffCidToGlyph: null,
            Diagnostics: Array.Empty<string>());

        resolved.Dictionary.Should().BeSameAs(dict);
        resolved.Widths.Should().Equal(250f, 500f);
        resolved.FirstChar.Should().Be(32);
        resolved.MissingWidth.Should().Be(375f);
        resolved.EncodingName.Should().Be("MacRomanEncoding");
        resolved.IsType0.Should().BeFalse();
        resolved.IsType3.Should().BeFalse();
    }

    [Fact]
    public void ForwardingProperties_ReflectType0AndType3Subtypes()
    {
        var type0 = new ResolvedRenderFont(
            MakePdfFont(subtype: "Type0"), null, null, null, null, null, false,
            null, 1000f, false, null, null, null, Array.Empty<string>());
        var type3 = new ResolvedRenderFont(
            MakePdfFont(subtype: "Type3"), null, null, null, null, null, false,
            null, 1000f, false, null, null, null, Array.Empty<string>());

        type0.IsType0.Should().BeTrue();
        type0.IsType3.Should().BeFalse();
        type3.IsType0.Should().BeFalse();
        type3.IsType3.Should().BeTrue();
    }

    [Fact]
    public void Diagnostics_PreservesEntriesPassedToConstructor()
    {
        var diagnostics = new List<string>
        {
            "Embedded CFF program produced no glyph outlines; falling back to a system typeface.",
        };

        var resolved = new ResolvedRenderFont(
            MakePdfFont(), null, null, null, null, null, false,
            null, 1000f, false, null, null, null, diagnostics);

        resolved.Diagnostics.Should().ContainSingle()
            .Which.Should().Contain("falling back to a system typeface");
    }

    [Fact]
    public void Diagnostics_DefaultsToEmpty_NotNull_WhenFontResolvesCleanly()
    {
        var resolved = new ResolvedRenderFont(
            MakePdfFont(), null, null, null, null, null, false,
            null, 1000f, false, null, null, null, Array.Empty<string>());

        resolved.Diagnostics.Should().NotBeNull();
        resolved.Diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// Records are immutable by construction (init-only positional
    /// properties) — `with` always produces a NEW instance rather than
    /// mutating the original. This is the property the save/restore sites
    /// collapsed to `var saved = _currentFont; ...; _currentFont = saved;`
    /// depend on: `saved` must still describe the pre-nested-execution font
    /// after nested content runs, which only holds if nothing mutates the
    /// instance `saved` points at.
    /// </summary>
    [Fact]
    public void With_ProducesANewInstance_OriginalIsUnaffected()
    {
        var original = new ResolvedRenderFont(
            MakePdfFont(), null, null, null, null, null, HasEmbeddedProgram: false,
            null, 1000f, false, null, null, null, Array.Empty<string>());

        var modified = original with { HasEmbeddedProgram = true };

        original.HasEmbeddedProgram.Should().BeFalse("the original instance must not be mutated by `with`");
        modified.HasEmbeddedProgram.Should().BeTrue();
        modified.Should().NotBeSameAs(original);
    }
}
