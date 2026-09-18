using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1624 — §8.4.2: <c>q</c> and <c>Q</c> save and restore the fill colour.
///
/// <para>All three recovery detectors tracked colour by hand and none restored
/// it, so a colour set inside a block applied to every later fill. These pin
/// the property directly, on bytes built for the purpose — the real document
/// that exposed it is gitignored and cannot be a unit fixture.</para>
/// </summary>
public class FillColourStateTests
{
    /// <summary>
    /// The exact shape that broke, and the fixture is built so that ONLY the
    /// restore can make it pass.
    ///
    /// <para>White is set OUTSIDE the block; a grey is set inside it; a band is
    /// painted after the <c>Q</c> with no colour operator of its own. Restored
    /// correctly the band is WHITE and not a mark. With the colour leaking it is
    /// grey(0.141), dark enough to be a mark, and the detector reports one.</para>
    ///
    /// <para>⚠️ An earlier version of this test set <c>1 g</c> again after the
    /// <c>Q</c> and asserted the same thing. It passed with the defect
    /// reinstated, because the explicit operator overwrote the leak — the test
    /// could not fail. Verified by planting the defect and watching this go red.</para>
    /// </summary>
    [Fact]
    public void AColourSetInsideABlock_DoesNotLeakPastTheQ()
    {
        var pdf = PageWith(
            "1 g " +                                // white, OUTSIDE the block
            "q 0.141 g 100 700 200 20 re f Q " +    // grey band inside it
            "100 600 200 20 re f");                 // no colour op: must restore to WHITE

        using var document = PdfDocument.Open(pdf);
        var marks = RedactionMarkDetector.Detect(document);

        marks.Should().HaveCount(1,
            "only the grey band is dark enough to be a mark — the second band inherits " +
            "the WHITE restored by Q, and reading it as grey is the #1624 defect");
    }

    /// <summary>
    /// The same property one level down: the colour a block restores is the
    /// ENCLOSING one, not the initial one. A restore-to-black implementation
    /// would pass the test above and fail this.
    /// </summary>
    [Fact]
    public void ARestoreReturnsTheEnclosingColour_NotTheInitialOne()
    {
        var pdf = PageWith(
            "1 g " +
            "q 0 0 1 rg q 0.141 g 100 700 200 20 re f Q 100 600 200 20 re f Q " +
            "100 500 200 20 re f");

        using var document = PdfDocument.Open(pdf);
        var marks = RedactionMarkDetector.Detect(document);

        marks.Should().HaveCount(2,
            "the grey band and the blue one (restored from the inner Q) are dark; " +
            "the last band is white, restored by the outer Q");
    }

    /// <summary>Nesting restores to the enclosing colour, not to the initial one.</summary>
    [Fact]
    public void NestedBlocksRestoreToTheEnclosingColour()
    {
        var state = new FillColourState(0, 0, 0);
        var ops = Ops("1 g q 0.5 g q 0.141 g Q Q");

        foreach (var op in ops) state.Apply(op);

        state.Current.R.Should().BeApproximately(1.0, 1e-9,
            "two Q operators restore the white set before the first q");
    }

    /// <summary>
    /// ⚠️ More <c>Q</c> than <c>q</c> is malformed and real. Throwing here
    /// would lose a whole page's marks over a damaged stream.
    /// </summary>
    [Fact]
    public void AnUnbalancedQ_LeavesTheColourAloneRatherThanThrowing()
    {
        var state = new FillColourState(0, 0, 0);
        var act = () => { foreach (var op in Ops("0.5 g Q Q Q")) state.Apply(op); };

        act.Should().NotThrow();
        state.Current.R.Should().BeApproximately(0.5, 1e-9);
    }

    private static System.Collections.Generic.IReadOnlyList<Excise.Core.Content.ContentOperator> Ops(string content)
    {
        using var document = PdfDocument.Open(PageWith(content));
        return document.GetPage(1).GetContentStream().Operators;
    }

    private static byte[] PageWith(string content)
        => RecoveryFixtureBuilder.Build(content);
}
