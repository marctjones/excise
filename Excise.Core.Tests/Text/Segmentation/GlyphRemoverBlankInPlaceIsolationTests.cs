using AwesomeAssertions;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// The #1044 blanking flag is per async context, not process-wide. As a static global it leaked from the
/// spike tests into every redaction test running in parallel with them, and failed four
/// <c>GlyphRemoverTests</c> in t1 whenever a chunk boundary put the two classes together.
/// </summary>
public class GlyphRemoverBlankInPlaceIsolationTests
{
    [Fact]
    public async Task ASettingMadeInOneContext_IsNotSeenByAnother()
    {
        var other = Task.Run(() =>
        {
            GlyphRemover.BlankInPlace = true;
            return GlyphRemover.BlankInPlace;
        });
        (await other).Should().BeTrue();

        GlyphRemover.BlankInPlace.Should().BeFalse("a sibling context turned it on; this one must not see it");
    }

    [Fact]
    public async Task ASettingIsSeenByWorkStartedFromTheSameContext()
    {
        GlyphRemover.BlankInPlace = true;
        try
        {
            var child = await Task.Run(() => GlyphRemover.BlankInPlace);
            child.Should().BeTrue("redaction's worker tasks must see the flag the caller set");
        }
        finally { GlyphRemover.BlankInPlace = false; }
    }
}
