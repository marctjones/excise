using AwesomeAssertions;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #1205 — the Core-side interpretation boundaries.
///
/// <para>#1202 made the clipboard-history preview control-safe. The same policy
/// belongs at every place excise shows PDF-sourced text as an IDENTIFIER a user
/// acts on — and those places are spread across the GUI, the reusable viewer
/// control and the CLI, none of which can reach a helper living in
/// <c>Excise.App</c>. Hence the move to Core: one policy, not three copies that
/// drift until one boundary uses the looser one.</para>
///
/// <para>The issue's rules are followed exactly: ordinary PDF prose and raw copy
/// values are preserved byte-for-byte; controls are made explicit only in
/// review/security displays; nothing is normalized, stripped, rejected or
/// confusable-folded.</para>
/// </summary>
public class UnicodeBoundaryDiagnosticsTests
{
    private const string Rlo = "‮";
    private const string Zwj = "‍";

    [Fact]
    public void CarrierAuditDescription_MakesControlsInTheTermExplicit()
    {
        // The reason this policy has to live in Core at all. The term in a
        // below-the-floor carrier warning is EXTRACTED PDF TEXT in the GUI path
        // (it is the preview text of a redaction area), and the warning is shown
        // in the redacted-copy dialog — a security display by definition.
        var audit = new RedactionCarrierAudit(
            OutlineTitleCount: 0,
            AnnotationsWithTextCount: 0,
            UnexaminedXfaPacketCount: 0,
            TermsBelowScrubFloor: new[] { "a" + Rlo + "b" });

        var described = string.Join("\n", audit.Describe());

        described.Should().Contain("[U+202E]",
            "the right-to-left override is made visible rather than reordering the warning");
        described.Should().NotContain(Rlo, "the raw control is not passed through to the display");
    }

    [Fact]
    public void OrdinaryProse_IsUnchanged()
    {
        // The rule that stops this becoming corruption: only controls are
        // touched. Accents, CJK, RTL LETTERS and punctuation all survive.
        const string prose = "Café — 日本語 — العربية — \"quoted\", 50% ± 3";
        UnicodeTextSafety.EscapeForDisplay(prose).Should().Be(prose);
    }

    [Fact]
    public void Newlines_And_Tabs_SurviveAsLayout()
    {
        UnicodeTextSafety.EscapeForDisplay("a\tb\r\nc").Should().Be("a\tb\r\nc");
    }

    [Theory]
    [InlineData("‮")]   // RIGHT-TO-LEFT OVERRIDE
    [InlineData("‭")]   // LEFT-TO-RIGHT OVERRIDE
    [InlineData("⁦")]   // LEFT-TO-RIGHT ISOLATE
    [InlineData("‏")]   // RIGHT-TO-LEFT MARK
    [InlineData("؜")]   // ARABIC LETTER MARK
    public void EveryBidiControl_IsFlaggedSeparately(string control)
    {
        // Bidi controls get their own predicate because they deserve their own
        // user-visible warning: they change what a string LOOKS LIKE without
        // changing what it IS, which is the whole attack.
        UnicodeTextSafety.ContainsBidiControl($"before{control}after").Should().BeTrue();
        UnicodeTextSafety.EscapeForDisplay($"a{control}b").Should().NotContain(control);
    }

    [Fact]
    public void InvisibleNonBidiFormatCharacters_AreAlsoMadeVisible()
    {
        // Not a bidi control, still invisible, still able to make two different
        // identifiers look identical.
        UnicodeTextSafety.ContainsBidiControl($"a{Zwj}b").Should().BeFalse();
        UnicodeTextSafety.ContainsPotentiallyMisleadingControl($"a{Zwj}b").Should().BeTrue();
        UnicodeTextSafety.EscapeForDisplay($"a{Zwj}b").Should().Be("a[U+200D]b");
    }

    [Fact]
    public void TheClassicPhishingUrl_ReadsAsWhatItIs()
    {
        // The case the open-link confirmation exists for: the visible text says
        // one host, the bytes say another.
        var uri = "https://example.com/‮gpj.exe";

        UnicodeTextSafety.ContainsBidiControl(uri).Should().BeTrue(
            "the confirmation dialog raises an extra warning on this");
        UnicodeTextSafety.EscapeForDisplay(uri)
            .Should().Be("https://example.com/[U+202E]gpj.exe");
    }
}
