using AwesomeAssertions;
using Excise.Rendering.Transparency;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1504 — ISO 32000-1/-2 §11.4.4's backdrop-removal step, as a SPEC PROPERTY
/// with no renderer and no oracle involved.
///
/// <para>The clause's own compositing formulas are reproduced here and the
/// removal is asked to invert them. That is the one check a differential
/// cannot make: an oracle differential says "excise now agrees with mutool and
/// Ghostscript", which a sign error compensated for elsewhere could also say.
/// This says "the arithmetic is the clause's arithmetic".</para>
///
/// <para><c>RenderContext.RemoveNonIsolatedGroupInitialBackdrop</c>'s doc
/// comment cites this file by name. Keep the name in sync.</para>
/// </summary>
public sealed class NonIsolatedGroupBackdropRemovalTests
{
    /// <summary>
    /// The defining property. §11.4.4 composites a group's elements ONTO the
    /// group's initial backdrop so blend modes have something to act on, then
    /// removes that backdrop from the result. For a single element under the
    /// Normal blend mode the composition is
    /// <c>Cn = (1 - as/an)*C0 + (as/an)*((1 - a0)*Cs + a0*Cs)</c>, i.e.
    /// <c>Cn = (1 - as/an)*C0 + (as/an)*Cs</c> with <c>an = Union(a0, as)</c>,
    /// and removal must give back exactly <c>Cs</c> — the colour that would
    /// have been the group's source had the group been isolated.
    ///
    /// <para>Rows span a0 (fully opaque backdrop, partial, transparent) and
    /// group alphas from nearly transparent to opaque, in both directions of
    /// the C0-to-Cs delta. Without removal the caller would composite Cn, which
    /// still carries (1 - as) of the backdrop.</para>
    /// </summary>
    [Theory]
    // a0,   as,    C0 component, Cs component
    [InlineData(1.0, 0.5, 0.0, 1.0)]
    [InlineData(1.0, 0.5, 1.0, 0.0)]
    [InlineData(1.0, 0.25, 0.4, 0.9)]
    [InlineData(1.0, 0.75, 0.9, 0.1)]
    [InlineData(1.0, 0.02, 0.2, 0.8)]
    [InlineData(0.5, 0.5, 0.0, 1.0)]
    [InlineData(0.5, 0.25, 0.8, 0.2)]
    [InlineData(0.25, 0.6, 0.3, 0.7)]
    [InlineData(0.0, 0.5, 0.0, 1.0)]
    [InlineData(1.0, 1.0, 0.0, 1.0)]
    public void Removal_InvertsTheClauseComposition_RecoveringTheGroupSourceColour(
        double a0, double groupAlpha, double c0, double cs)
    {
        var initial = Uniform(c0);
        var sourceColour = Uniform(cs);

        var accumulated = CompositeOneElementPerClause(initial, a0, sourceColour, groupAlpha);
        var accumulatedAlpha = Union(a0, groupAlpha);

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, accumulatedAlpha, groupAlpha, initial);

        removed.C.Should().BeApproximately(cs, 1e-9,
            "§11.4.4 removal must recover the group's own source colour (#1504)");
        removed.M.Should().BeApproximately(cs, 1e-9);
        removed.Y.Should().BeApproximately(cs, 1e-9);
        removed.K.Should().BeApproximately(cs, 1e-9);
    }

    /// <summary>
    /// The clause's simplified result formula and the "more intuitive" backdrop
    /// fraction form it says the formula is a simplification OF must agree. The
    /// fraction form is written out here independently of the implementation,
    /// so a transcription slip in the RESULT formula — <c>a0 * agn</c> for
    /// <c>a0 / agn</c>, a dropped <c>- a0</c> — fails this even on inputs the
    /// property test above does not reach.
    ///
    /// <para>⚠️ What it does NOT cover: the <c>a0</c> derivation. These rows
    /// feed <c>an = Union(a0, agn)</c>, so the derivation recovers the same
    /// <c>a0</c> the expected value is computed with, by construction. The
    /// derivation is covered by
    /// <see cref="Removal_DerivesTheBackdropAlphaFromTheChildsOwnAccumulatedAlpha"/>
    /// and the two identity/clamp tests below. Saying so rather than letting
    /// the name imply otherwise (#936).</para>
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.5)]
    [InlineData(1.0, 0.1)]
    [InlineData(0.75, 0.4)]
    [InlineData(0.3, 0.9)]
    [InlineData(0.6, 0.6)]
    public void Removal_AgreesWithTheClausesBackdropFractionForm(double a0, double groupAlpha)
    {
        // An arbitrary accumulated colour, NOT one produced by the Normal
        // composition above, so the two forms are compared as formulas rather
        // than through a case where both reduce to Cs.
        var accumulated = new DeviceCmykColor(0.62, 0.31, 0.47, 0.08);
        var initial = new DeviceCmykColor(0.20, 0.55, 0.05, 0.40);
        var accumulatedAlpha = Union(a0, groupAlpha);

        // phi_b = (1 - agn) * a0 / Union(a0, agn);  C = (Cn - phi_b*C0) / (1 - phi_b)
        var phi = (1 - groupAlpha) * a0 / accumulatedAlpha;
        var expected = new[]
        {
            (accumulated.C - (phi * initial.C)) / (1 - phi),
            (accumulated.M - (phi * initial.M)) / (1 - phi),
            (accumulated.Y - (phi * initial.Y)) / (1 - phi),
            (accumulated.K - (phi * initial.K)) / (1 - phi),
        };

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, accumulatedAlpha, groupAlpha, initial);

        // Clamped to the gamut by the implementation, so compare clamped.
        removed.C.Should().BeApproximately(Math.Clamp(expected[0], 0, 1), 1e-9);
        removed.M.Should().BeApproximately(Math.Clamp(expected[1], 0, 1), 1e-9);
        removed.Y.Should().BeApproximately(Math.Clamp(expected[2], 0, 1), 1e-9);
        removed.K.Should().BeApproximately(Math.Clamp(expected[3], 0, 1), 1e-9);
    }

    /// <summary>
    /// The <c>a0</c> derivation, on inputs where <c>an</c> is NOT
    /// <c>Union(a0, agn)</c> for the <c>a0</c> that produced the colour — which
    /// is exactly the situation the derivation exists for, since
    /// <c>SyncDeviceCmykGroupBackdropFromGroupBitmap</c> can leave a child
    /// backdrop pixel at any <c>an</c> between <c>agn</c> and 1.
    ///
    /// <para>The implementation is given only <c>(Cn, an, agn, C0)</c>, so the
    /// backdrop alpha it removes must be the one <c>an</c> implies. The
    /// expected value here is computed from that implied <c>a0</c> through the
    /// clause's fraction form, so the derivation and the result formula are
    /// checked together against an independent transcription.</para>
    /// </summary>
    [Theory]
    // agn,  an,   implied a0 = (an - agn) / (1 - agn)
    [InlineData(0.5, 1.00, 1.00)]
    [InlineData(0.5, 0.75, 0.50)]
    [InlineData(0.5, 0.60, 0.20)]
    [InlineData(0.2, 0.60, 0.50)]
    [InlineData(0.8, 0.90, 0.50)]
    public void Removal_DerivesTheBackdropAlphaFromTheChildsOwnAccumulatedAlpha(
        double groupAlpha, double accumulatedAlpha, double impliedBackdropAlpha)
    {
        var accumulated = new DeviceCmykColor(0.62, 0.31, 0.47, 0.08);
        var initial = new DeviceCmykColor(0.20, 0.55, 0.05, 0.40);

        // Sanity on the row itself: the third column is what the derivation
        // must produce, restated so a wrong row fails loudly rather than
        // agreeing with a wrong implementation.
        ((accumulatedAlpha - groupAlpha) / (1 - groupAlpha))
            .Should().BeApproximately(impliedBackdropAlpha, 1e-9, "the row's own arithmetic");

        // phi_b = (1 - agn) * a0 / Union(a0, agn), with the IMPLIED a0.
        var union = impliedBackdropAlpha + groupAlpha - (impliedBackdropAlpha * groupAlpha);
        var phi = (1 - groupAlpha) * impliedBackdropAlpha / union;

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, accumulatedAlpha, groupAlpha, initial);

        removed.C.Should().BeApproximately(
            Math.Clamp((accumulated.C - (phi * initial.C)) / (1 - phi), 0, 1), 1e-9);
        removed.M.Should().BeApproximately(
            Math.Clamp((accumulated.M - (phi * initial.M)) / (1 - phi), 0, 1), 1e-9);
        removed.Y.Should().BeApproximately(
            Math.Clamp((accumulated.Y - (phi * initial.Y)) / (1 - phi), 0, 1), 1e-9);
        removed.K.Should().BeApproximately(
            Math.Clamp((accumulated.K - (phi * initial.K)) / (1 - phi), 0, 1), 1e-9);
    }

    /// <summary>
    /// The three identities. An opaque group hides its backdrop, so there is
    /// nothing to remove; a transparent initial backdrop contributed nothing;
    /// and a group that painted nothing contributes nothing. Each of these is
    /// also a place the derivation would divide by zero if it were not guarded.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0, "an opaque group: agn = 1, factor a0 - a0 = 0")]
    [InlineData(0.0, 0.5, "a transparent initial backdrop: a0 = 0")]
    [InlineData(0.0, 0.0, "a group that painted nothing: agn = 0")]
    public void Removal_IsIdentity_WhereThereIsNothingToRemove(
        double a0, double groupAlpha, string why)
    {
        var accumulated = new DeviceCmykColor(0.7, 0.2, 0.9, 0.05);
        var initial = new DeviceCmykColor(0.1, 0.6, 0.3, 0.5);

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, Union(a0, groupAlpha), groupAlpha, initial);

        removed.Should().Be(accumulated, why);
    }

    /// <summary>
    /// The pixels <c>SyncDeviceCmykGroupBackdropFromGroupBitmap</c> rewrites
    /// (Skia-RGB paint inside the child group) end up in the child backdrop at
    /// the group bitmap's OWN alpha, with a colour that never included the
    /// seed. Removal must leave those alone, and it does because the derived a0
    /// is then zero. This is the reason a0 is derived from the child's own
    /// alphas rather than read from the parent backdrop — reading it would
    /// subtract a backdrop that is not in the colour.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(0.99)]
    public void Removal_IsIdentity_WhenTheAccumulatedAlphaIsJustTheGroupAlpha(double groupAlpha)
    {
        var accumulated = new DeviceCmykColor(0.33, 0.44, 0.55, 0.66);
        var initial = new DeviceCmykColor(0.9, 0.1, 0.9, 0.1);

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, groupAlpha, groupAlpha, initial);

        removed.Should().Be(accumulated,
            "an == agn means no backdrop alpha was ever unioned in, so there is nothing to remove");
    }

    /// <summary>
    /// Byte quantisation can leave <c>an</c> a step BELOW <c>agn</c> on a pixel
    /// that was never seeded. The derivation must clamp rather than produce a
    /// negative backdrop alpha, which would push the colour the wrong way.
    /// </summary>
    [Fact]
    public void Removal_ClampsANegativeDerivedBackdropAlpha()
    {
        var accumulated = new DeviceCmykColor(0.5, 0.5, 0.5, 0.5);
        var initial = new DeviceCmykColor(0.0, 0.0, 0.0, 0.0);

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, accumulatedAlpha: 0.49, groupAlpha: 0.50, initial);

        removed.Should().Be(accumulated,
            "a derived a0 below zero is quantisation noise, not a backdrop to remove");
    }

    /// <summary>
    /// The result stays inside the DeviceCMYK gamut even though the removal is
    /// an extrapolation: with an extreme C0 and a small group alpha the
    /// unclamped value leaves [0,1].
    /// </summary>
    [Fact]
    public void Removal_ClampsToTheGamut()
    {
        var accumulated = new DeviceCmykColor(0.5, 0.5, 0.5, 0.5);
        var initial = new DeviceCmykColor(1.0, 0.0, 1.0, 0.0);

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            accumulated, accumulatedAlpha: 1.0, groupAlpha: 0.05, initial);

        foreach (var component in new[] { removed.C, removed.M, removed.Y, removed.K })
            component.Should().BeInRange(0, 1);
    }

    /// <summary>
    /// The error the amplified factor puts on the PAGE is bounded by one
    /// quantisation step regardless of how small the group alpha is, because
    /// the removed colour is then composited at that same small alpha. This is
    /// the measurement behind the decision NOT to floor agn with an epsilon —
    /// a floor would silently reinstate the double count on exactly the pixels
    /// removal matters most for.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.1)]
    [InlineData(0.02)]
    [InlineData(4.0 / 255.0)]
    public void Removal_ErrorReachingThePage_StaysWithinOneQuantisationStep(double groupAlpha)
    {
        const double a0 = 1.0;
        var initial = Uniform(0.2);
        var trueSource = Uniform(0.8);

        var exact = CompositeOneElementPerClause(initial, a0, trueSource, groupAlpha);
        // What the byte-plane backdrop can actually hold for that colour.
        var quantised = Uniform(Math.Round(exact.C * 255) / 255.0);

        var removed = RenderContext.RemoveNonIsolatedGroupInitialBackdrop(
            quantised, Union(a0, groupAlpha), groupAlpha, initial);

        // Composite the removed colour over the backdrop at the group alpha,
        // which is what CompositeDeviceCmykGroupBitmap does next, and compare
        // with compositing the true source the same way.
        var onPage = (removed.C * groupAlpha) + (initial.C * (1 - groupAlpha));
        var ideal = (trueSource.C * groupAlpha) + (initial.C * (1 - groupAlpha));

        Math.Abs(onPage - ideal).Should().BeLessThan(2.0 / 255.0,
            $"agn={groupAlpha}: the factor amplifies Cn's 1/255 noise by a0/agn, but the "
            + "result is composited at agn, so the product is one step (#1504)");
    }

    private static DeviceCmykColor Uniform(double value) => new(value, value, value, value);

    private static double Union(double a, double b) => a + b - (a * b);

    /// <summary>
    /// §11.4.4's per-element composition for one element under the Normal blend
    /// mode (B(Ci-1, Csi) = Csi), transcribed from the clause:
    /// <c>Ci = (1 - asi/ai)*Ci-1 + (asi/ai)*((1 - ai-1)*Csi + ai-1*B(Ci-1, Csi))</c>
    /// with <c>C0</c>/<c>a0</c> as the initial backdrop.
    /// </summary>
    private static DeviceCmykColor CompositeOneElementPerClause(
        DeviceCmykColor initial, double a0, DeviceCmykColor source, double sourceAlpha)
    {
        var a1 = Union(a0, sourceAlpha);
        if (a1 <= 0)
            return initial;

        var w = sourceAlpha / a1;
        double Channel(double c0, double cs) => ((1 - w) * c0) + (w * (((1 - a0) * cs) + (a0 * cs)));
        return new DeviceCmykColor(
            Channel(initial.C, source.C),
            Channel(initial.M, source.M),
            Channel(initial.Y, source.Y),
            Channel(initial.K, source.K));
    }
}
