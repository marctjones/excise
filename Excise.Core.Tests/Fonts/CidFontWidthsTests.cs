using AwesomeAssertions;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Spec-driven tests for <see cref="CidFontWidths"/> — the shared parser for
/// the CIDFont metric tables /DW, /W, /DW2, /W2 (PDF 32000-1:2008 §9.7.4.3),
/// including the malformed-input hardening #515's acceptance criteria call
/// out ("malformed width arrays"). All fixture objects are built in code;
/// the oracle is the spec's own worked syntax, not another PDF library.
/// </summary>
public class CidFontWidthsTests
{
    private static PdfDictionary CidFont(params (string Key, PdfObject Value)[] entries)
    {
        var dict = new PdfDictionary();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    private static PdfArray Arr(params PdfObject[] items) => new(items);
    private static PdfInteger I(int v) => new(v);
    private static PdfReal R(double v) => new(v);

    // ---------- /W (horizontal widths) ----------

    [Fact] // §9.7.4.3 example: both forms interleaved in a single array
    public void W_ParsesListAndRangeFormsInterleaved()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(I(120), Arr(I(400), I(325), I(500)),
                      I(7080), I(8032), I(1000)))));

        w.GetWidth(120).Should().Be(400);
        w.GetWidth(121).Should().Be(325);
        w.GetWidth(122).Should().Be(500);
        w.GetWidth(7080).Should().Be(1000);
        w.GetWidth(8032).Should().Be(1000);
        w.GetWidth(123).Should().Be(1000, "unlisted CIDs fall back to the default /DW of 1000");
    }

    [Fact]
    public void W_DwOverridesTheSpecDefaultForUnlistedCids()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("DW", I(750)),
            ("W", Arr(I(5), Arr(I(300))))));

        w.GetWidth(5).Should().Be(300);
        w.GetWidth(6).Should().Be(750);
        w.DefaultWidth.Should().Be(750);
    }

    [Fact] // real numbers are legal everywhere a number is expected
    public void W_AcceptsRealNumbers()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(I(10), Arr(R(455.5)), I(20), I(21), R(612.25)))));

        w.GetWidth(10).Should().Be(455.5);
        w.GetWidth(20).Should().Be(612.25);
        w.GetWidth(21).Should().Be(612.25);
    }

    [Fact] // indirect references are legal at every level (§7.3.10) and appear
           // in real-world files; the old per-consumer parsers missed them
    public void W_ResolvesIndirectReferencesAtEveryLevel()
    {
        var innerArray = Arr(I(640), I(660));
        var wArray = Arr(I(40), new PdfReference(9), new PdfReference(7), I(52), I(500));

        PdfObject Resolve(PdfObject obj) => obj switch
        {
            PdfReference { ObjectNum: 9 } => innerArray,
            PdfReference { ObjectNum: 7 } => I(50),
            _ => obj,
        };

        var w = CidFontWidths.Parse(CidFont(("W", wArray)), Resolve);

        w.GetWidth(40).Should().Be(640);
        w.GetWidth(41).Should().Be(660);
        w.GetWidth(50).Should().Be(500, "cFirst was an indirect reference to 50");
        w.GetWidth(52).Should().Be(500);
    }

    // ---------- /W malformed-input hardening ----------

    [Fact] // range bomb: a hostile range must not allocate billions of entries
    public void W_RangeBomb_IsClampedToTheValidCidSpace()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(I(0), I(999_999_999), I(500)))));

        w.Widths.Count.Should().Be(65536, "CIDs are clamped to [0, 65535]");
        w.GetWidth(0).Should().Be(500);
        w.GetWidth(65535).Should().Be(500);
        w.GetWidth(70000).Should().Be(1000, "beyond the clamp the default applies");
    }

    [Fact]
    public void W_ReversedRange_IsDroppedNotLooped()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(I(500), I(100), I(250), I(7), Arr(I(333))))));

        w.Widths.Should().NotContainKey(100);
        w.Widths.Should().NotContainKey(500);
        w.GetWidth(7).Should().Be(333, "parsing must recover after the dropped range");
    }

    [Fact]
    public void W_NegativeCids_AreSkippedWithoutThrowing()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(I(-5), Arr(I(400), I(410)), I(3), Arr(I(275))))));

        w.Widths.Should().NotContainKey(-5);
        w.Widths.Should().NotContainKey(-4);
        w.GetWidth(3).Should().Be(275);
    }

    [Fact] // junk tokens (names, strings, nulls) between entries must be skipped
    public void W_JunkTokens_AreSkippedAndParsingRecovers()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(new PdfName("Bogus"), I(12), Arr(I(600)),
                      PdfNull.Instance, I(30), I(31), I(450)))));

        w.GetWidth(12).Should().Be(600);
        w.GetWidth(30).Should().Be(450);
        w.GetWidth(31).Should().Be(450);
    }

    [Fact] // a lone trailing cFirst with nothing after it must not throw
    public void W_TruncatedArray_IsTolerated()
    {
        var w = CidFontWidths.Parse(CidFont(("W", Arr(I(120)))));
        w.Widths.Should().BeEmpty();

        var w2 = CidFontWidths.Parse(CidFont(("W", Arr(I(120), I(125)))));
        w2.Widths.Should().BeEmpty("cFirst cLast with no width is incomplete");
    }

    [Fact]
    public void W_WrongTypeEntirely_IsIgnored()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", new PdfName("NotAnArray")),
            ("DW", new PdfName("NotANumber"))));

        w.Widths.Should().BeEmpty();
        w.DefaultWidth.Should().Be(1000, "a malformed /DW keeps the spec default");
    }

    // ---------- /DW2 and /W2 (vertical metrics) ----------

    [Fact] // §9.7.4.3: without /DW2 the defaults are vy=880, w1y=-1000, vx=w0/2
    public void VerticalDefaults_MatchTheSpec()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W", Arr(I(36), Arr(I(684))))));

        w.DefaultVerticalOriginY.Should().Be(880);
        w.DefaultVerticalDisplacement.Should().Be(-1000);

        var m = w.GetVerticalMetrics(36);
        m.W1Y.Should().Be(-1000);
        m.Vx.Should().Be(342, "the default position vector x is w0/2 (§9.7.4.3)");
        m.Vy.Should().Be(880);

        w.GetVerticalMetrics(99).Vx.Should().Be(500, "unlisted CID: w0 = /DW default 1000");
    }

    [Fact]
    public void Dw2_OverridesTheVerticalDefaults()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("DW2", Arr(I(900), I(-1200)))));

        w.DefaultVerticalOriginY.Should().Be(900);
        w.DefaultVerticalDisplacement.Should().Be(-1200);
        w.GetVerticalMetrics(1).W1Y.Should().Be(-1200);
        w.GetVerticalMetrics(1).Vy.Should().Be(900);
    }

    [Fact] // /DW2 must be exactly [vy w1y]; anything else keeps the defaults
    public void Dw2_Malformed_KeepsSpecDefaults()
    {
        CidFontWidths.Parse(CidFont(("DW2", Arr(I(900)))))
            .DefaultVerticalDisplacement.Should().Be(-1000, "one element is not a valid /DW2");
        CidFontWidths.Parse(CidFont(("DW2", Arr(I(900), new PdfName("x")))))
            .DefaultVerticalDisplacement.Should().Be(-1000, "non-numeric w1y is not a valid /DW2");
        CidFontWidths.Parse(CidFont(("DW2", I(900))))
            .DefaultVerticalDisplacement.Should().Be(-1000, "/DW2 must be an array");
    }

    [Fact] // §9.7.4.3: c [w1y vx vy ...] — consecutive triples starting at c
    public void W2_ParsesTripleGroups()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W2", Arr(I(120), Arr(I(-1000), I(250), I(772),
                                   I(-900), I(300), I(800))))));

        w.GetVerticalMetrics(120).Should().Be(new CidVerticalMetrics(-1000, 250, 772));
        w.GetVerticalMetrics(121).Should().Be(new CidVerticalMetrics(-900, 300, 800));
        w.GetVerticalMetrics(122).W1Y.Should().Be(-1000, "CID 122 has no entry — defaults apply");
    }

    [Fact] // §9.7.4.3: cFirst cLast w1y vx vy — one metric for a whole range
    public void W2_ParsesRangeForm()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W2", Arr(I(7080), I(8032), I(-1000), I(500), I(900)))));

        w.GetVerticalMetrics(7080).Should().Be(new CidVerticalMetrics(-1000, 500, 900));
        w.GetVerticalMetrics(8032).Should().Be(new CidVerticalMetrics(-1000, 500, 900));
        w.GetVerticalMetrics(8033).Vy.Should().Be(880);
    }

    [Fact] // a trailing incomplete triple must be dropped, not misread
    public void W2_TrailingPartialTriple_IsIgnored()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W2", Arr(I(50), Arr(I(-1000), I(250), I(772), I(-900), I(300))))));

        w.VerticalMetrics.Should().ContainKey(50);
        w.VerticalMetrics.Should().NotContainKey(51, "only 2 of 3 numbers remain for CID 51");
    }

    [Fact]
    public void W2_RangeBomb_IsClampedLikeW()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("W2", Arr(I(0), I(999_999_999), I(-1000), I(500), I(880)))));

        w.VerticalMetrics.Count.Should().Be(65536);
    }

    [Fact] // both tables coexist; /W supplies w0 (and default vx) per CID
    public void W_AndW2_Coexist()
    {
        var w = CidFontWidths.Parse(CidFont(
            ("DW", I(1000)),
            ("W", Arr(I(36), Arr(I(684)))),
            ("W2", Arr(I(37), Arr(I(-800), I(343), I(880))))));

        w.GetWidth(36).Should().Be(684);
        w.GetVerticalMetrics(36).Vx.Should().Be(342, "no /W2 entry for 36 → vx = w0/2 = 684/2");
        w.GetVerticalMetrics(37).Should().Be(new CidVerticalMetrics(-800, 343, 880));
    }
}
