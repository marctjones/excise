using System.Collections.Generic;
using Excise.Core.Content;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// §8.4.2 fill-colour state for the recovery detectors: the current colour and
/// the <c>q</c>/<c>Q</c> stack that saves and restores it.
///
/// <para><b>Why this type exists (#1624).</b> Three detectors —
/// <see cref="HiddenTextDetector"/>, <see cref="RedactionMarkDetector"/> and
/// <see cref="CoveredContentRecovery"/> — each hand-rolled the same
/// "track the fill colour" loop, and all three got the same half wrong: they
/// handled <c>g</c>/<c>rg</c>/<c>k</c>/<c>sc</c>/<c>scn</c> and NONE of them
/// restored the colour on <c>Q</c>. §8.4.2 is explicit that <c>q</c> and
/// <c>Q</c> save and restore the ENTIRE graphics state, colour included, so a
/// colour set inside a block leaked out past its <c>Q</c> and applied to every
/// later fill.</para>
///
/// <para><b>What that cost, measured.</b> On an ordinary US court filing
/// (4:23-cv-04911, Doc 82) the pleading paper paints one white full-width band
/// per line, <c>70.5 696 471.06 24 re f</c>. A stale <c>0.141 g</c> leaked
/// across a <c>Q</c> and excise read those bands as gray(0.141) — the same
/// colour as the body text — so every glyph they covered scored zero contrast
/// and <c>unredact</c> reported <b>83.7% of a fully legible document as hidden
/// text</b>: 260 findings over 1,239 glyphs, on a page where nothing is hidden.
/// Not a miss but a flood, which is the failure mode that makes an audit tool
/// useless: the real findings are buried in the noise.</para>
///
/// <para><b>Why one type rather than three fixes.</b> CLAUDE.md's "one walk,
/// many sinks" is about exactly this: the same state machine written more than
/// once drifts, and every RC1 defect lived in the drift. Ideally these
/// detectors would be <see cref="ContentStreamWalker"/> sinks and would inherit
/// its Table 52 handling outright. They are not, and porting them is a larger
/// change than this defect warrants — so at minimum the part they DO share is
/// written once. ⚠️ That is a narrowing of the divergence, not a licence to
/// keep growing private state here: a fourth detector needing graphics state
/// should be a sink, not a fourth copy.</para>
///
/// <para><b>What it deliberately does not track.</b> Stroke colour (these
/// detectors judge fills), and the colour SPACE — <c>sc</c>/<c>scn</c> operands
/// mean different things per space, so they are read by component count and a
/// pattern name leaves the colour alone rather than guessing. That was already
/// the behaviour; it is preserved rather than silently changed.</para>
/// </summary>
internal sealed class FillColourState
{
    private readonly Stack<(double R, double G, double B)> _stack = new();

    /// <param name="r">
    /// §8.6.8: the initial colour is BLACK. Two detectors start there; one
    /// (<see cref="CoveredContentRecovery"/>) starts white and gates on
    /// <c>Set</c>, so the default is a parameter rather than a constant.
    /// ⚠️ Do not "simplify" this to always-black: #1617 is the record of what
    /// assuming a default costs, in both directions.
    /// </param>
    public FillColourState(double r, double g, double b) => Current = (r, g, b);

    /// <summary>The fill colour in force.</summary>
    public (double R, double G, double B) Current { get; private set; }

    /// <summary>True once a colour operator has been seen at this nesting or an enclosing one.</summary>
    public bool Set { get; private set; }

    /// <summary>
    /// Apply one operator. Returns true when it was a colour or state operator
    /// this type consumed — the caller's switch need not handle it again.
    /// </summary>
    public bool Apply(ContentOperator op)
    {
        switch (op.Name)
        {
            case "q":
                _stack.Push(Current);
                return true;

            case "Q":
                // A stream with more Q than q is malformed. Popping an empty
                // stack would throw; leaving the colour alone is what every
                // reader does and keeps a damaged page readable.
                if (_stack.Count > 0) Current = _stack.Pop();
                return true;

            case "g" when op.Operands.Count >= 1:
            {
                var v = op.GetNumber(0);
                Current = (v, v, v);
                Set = true;
                return true;
            }

            case "rg" when op.Operands.Count >= 3:
                Current = (op.GetNumber(0), op.GetNumber(1), op.GetNumber(2));
                Set = true;
                return true;

            case "k" when op.Operands.Count >= 4:
            {
                var c = op.GetNumber(0);
                var m = op.GetNumber(1);
                var y = op.GetNumber(2);
                var kk = op.GetNumber(3);
                Current = ((1 - c) * (1 - kk), (1 - m) * (1 - kk), (1 - y) * (1 - kk));
                Set = true;
                return true;
            }

            case "sc":
            case "scn":
                if (TryReadComponents(op, out var scn))
                {
                    Current = scn;
                    Set = true;
                }
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// §8.6.8 operands depend on the current colour space, which these
    /// detectors do not track. Read by component count; a pattern name (the
    /// <c>/P1 scn</c> form) leaves the colour alone rather than guessing.
    /// </summary>
    private static bool TryReadComponents(ContentOperator op, out (double R, double G, double B) rgb)
    {
        rgb = default;
        var n = op.Operands.Count;
        switch (n)
        {
            case 1:
            {
                var v = op.GetNumber(0);
                rgb = (v, v, v);
                return true;
            }
            case 3:
                rgb = (op.GetNumber(0), op.GetNumber(1), op.GetNumber(2));
                return true;
            case 4:
            {
                var c = op.GetNumber(0);
                var m = op.GetNumber(1);
                var y = op.GetNumber(2);
                var kk = op.GetNumber(3);
                rgb = ((1 - c) * (1 - kk), (1 - m) * (1 - kk), (1 - y) * (1 - kk));
                return true;
            }
            default:
                return false;
        }
    }
}
