using AwesomeAssertions;
using Excise.App.Models;
using Xunit;

namespace Excise.App.Tests.Unit;

public class ClipboardEntryUnicodeSafetyTests
{
    [Fact]
    public void PreviewText_RevealsBidiControls_ButTextKeepsExactClipboardValue()
    {
        const string copied = "report\u202Efdp.exe";
        var entry = new ClipboardEntry { Text = copied };

        entry.Text.Should().BeSameAs(copied,
            "copy/paste must preserve the PDF's original Unicode sequence");
        entry.PreviewText.Should().Be("report[U+202E]fdp.exe");
        entry.HasUnicodeSecurityControls.Should().BeTrue();
        entry.UnicodeSecurityNotice.Should().Be("Contains bidirectional formatting controls");
    }

    [Fact]
    public void PreviewText_LeavesOrdinaryArabicAndCjkTextUntouched()
    {
        const string copied = "العربية 世界";
        var entry = new ClipboardEntry { Text = copied };

        entry.PreviewText.Should().BeSameAs(copied);
        entry.HasUnicodeSecurityControls.Should().BeFalse();
    }
}
