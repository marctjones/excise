using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #532 — classification of the PASS_ONE font encoding/metrics residual corpus.
/// Each fixture is pinned to a documented verdict (excise-defect vs excise-correct
/// vs no-reliable-ground-truth), distinguishing real glyph-mapping failures from
/// reference-renderer outliers, per the issue's acceptance criteria.
///
/// Verdicts (page 1):
/// - issue4722.pdf         — excise DEFECT, FIXED. Non-embedded CIDFontType2,
///   Identity-H, no /ToUnicode: excise read the raw GID as a Latin-1 code point
///   ("DESCRIPTION" → "'(6&amp;5,37,21", a fixed −29 shift). Fixed by the standard
///   Macintosh glyph-order GID→Unicode fallback (StandardMacGlyphOrder).
/// - issue15977_reduced.pdf — excise DEFECT, FIXED. Same class ("RECOMEDACI…"
///   was "5(&amp;20('$&amp;,").
/// - issue12418_reduced.pdf — excise CORRECT (regression guard). Has
///   /ToUnicode /Identity-H (code == Unicode); the Mac-order fallback must NOT
///   fire here. Extracts "Uvolnění vinkulace" both before and after the fix.
/// - bug920426.pdf         — excise CORRECT; pdftocairo is the outlier (tofu).
///   excise/mutool/Ghostscript all read "Checkliste Service".
/// - copy_paste_ligatures.pdf — excise CORRECT. excise preserves the Unicode
///   ligatures (U+FB00…); references expand them. Not a mapping defect.
/// - issue13916.pdf        — NO RELIABLE GROUND TRUTH. Non-embedded Identity-H
///   fonts with a non-standard glyph order; excise AND mutool both produce
///   garbled output with only fragments of real words. Not spec-provable from a
///   reference; deeper CMap/CID work tracked in #515. The Mac-order fallback
///   changes (marginally improves) the output but cannot make it correct.
/// </summary>
public class EncodingResidualClassificationTests
{
    private static string Extract(string relPath)
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", relPath);
        Assert.SkipWhen(path == null, $"gitignored pdf.js corpus fixture {relPath} not present (scripts/download-pdfjs-corpus.sh).");
        using var doc = PdfDocument.Open(path!);
        return new TextExtractor(doc.GetPage(1)).ExtractText();
    }

    [Fact] // excise DEFECT → FIXED (would fail pre-fix: extracted "'(6&5,37,21")
    public void Issue4722_NonEmbeddedIdentityH_NoToUnicode_ExtractsRealText()
    {
        Extract("issue4722.pdf").Should().Contain("DESCRIPTION",
            "a non-embedded Identity-H CIDFontType2 with no /ToUnicode must fall back to the " +
            "standard Macintosh glyph order, not read the raw GID as a Latin-1 code point");
    }

    [Fact] // excise DEFECT → FIXED (would fail pre-fix: extracted "5(&20('$&,")
    public void Issue15977_NonEmbeddedIdentityH_NoToUnicode_ExtractsRealText()
    {
        Extract("issue15977_reduced.pdf").Should().Contain("RECOMEDACI",
            "same class as issue4722 — the Mac-order fallback recovers the real characters");
    }

    [Fact] // excise CORRECT — regression guard for the /ToUnicode scope
    public void Issue12418_IdentityHToUnicode_IsNotOverriddenByMacOrderFallback()
    {
        Extract("issue12418_reduced.pdf").Should().Contain("Uvolnění",
            "/ToUnicode /Identity-H means code == Unicode; the Mac-order fallback must not " +
            "second-guess a font that declares its own ToUnicode mapping");
    }

    [Fact] // excise CORRECT — pdftocairo is the outlier
    public void Bug920426_ExciseIsCorrect_ReferenceRendererIsTheOutlier()
    {
        Extract("bug920426.pdf").Should().Contain("Checkliste Service",
            "excise/mutool/Ghostscript agree; pdftocairo renders tofu — excise is not the defect here");
    }

    [Fact] // excise CORRECT — ligatures preserved
    public void CopyPasteLigatures_ExcisePreservesUnicodeLigatures()
    {
        var text = Extract("copy_paste_ligatures.pdf");
        text.Should().Contain("abcdef");
        text.Should().Contain("ghijklmno",
            "excise preserves the Unicode ligature glyphs rather than mis-mapping them; " +
            "reference expansion of ligatures is a representation choice, not a excise defect");
    }

    [Fact] // NO RELIABLE GROUND TRUTH — documents the limit, routes to #515
    public void Issue13916_NonStandardIdentityH_HasNoReliableGroundTruth()
    {
        // Both excise and mutool produce garbled output on this fixture's
        // non-embedded, non-standard-glyph-order Identity-H fonts; there is no
        // reference to match against. This test documents the classification
        // (not a correctness assertion) — full CMap/CID coverage is tracked in
        // #515. It must at least not crash or return empty.
        //
        // #515 slice 3 (embedded-cmap reverse lookup) verified this fixture
        // contains ZERO embedded font programs (no /FontFile* anywhere), so it
        // is permanently outside that fix's scope: with neither a /ToUnicode
        // nor an embedded program, the GID→Unicode bridge simply does not
        // exist in the file. Extraction is byte-identical before/after slice 3.
        Extract("issue13916.pdf").Should().NotBeNullOrEmpty(
            "extraction must still produce output (routing to #515 for correct decoding)");
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
}
