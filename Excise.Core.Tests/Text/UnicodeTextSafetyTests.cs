using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

public class UnicodeTextSafetyTests
{
    [Fact]
    public void EscapeForDisplay_RevealsBidiOverrideWithoutChangingVisibleText()
    {
        const string raw = "invoice\u202Efdp.exe";

        UnicodeTextSafety.EscapeForDisplay(raw).Should().Be("invoice[U+202E]fdp.exe");
        UnicodeTextSafety.ContainsBidiControl(raw).Should().BeTrue();
        UnicodeTextSafety.ContainsPotentiallyMisleadingControl(raw).Should().BeTrue();
    }

    [Fact]
    public void EscapeForDisplay_RevealsZeroWidthJoinerButPreservesArabicText()
    {
        const string raw = "سلام\u200Dعليكم";

        UnicodeTextSafety.EscapeForDisplay(raw).Should().Be("سلام[U+200D]عليكم");
        UnicodeTextSafety.ContainsBidiControl(raw).Should().BeFalse();
        UnicodeTextSafety.ContainsPotentiallyMisleadingControl(raw).Should().BeTrue();
    }

    [Fact]
    public void EscapeForDisplay_LeavesOrdinaryRtlAndParagraphWhitespaceUnchanged()
    {
        const string raw = "שלום\n世界\tمرحبا";

        UnicodeTextSafety.EscapeForDisplay(raw).Should().BeSameAs(raw);
        UnicodeTextSafety.ContainsPotentiallyMisleadingControl(raw).Should().BeFalse();
    }

    [Fact]
    public void EscapeForDisplay_RevealsSupplementaryPlaneTagControls()
    {
        const string raw = "safe\U000E007Ftext";

        UnicodeTextSafety.EscapeForDisplay(raw).Should().Be("safe[U+0E007F]text");
        UnicodeTextSafety.ContainsPotentiallyMisleadingControl(raw).Should().BeTrue();
    }
}
