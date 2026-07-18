using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Exercises every content-stream operator branch in ContentStreamParser (#350) —
/// shading, clipping, marked content, compatibility, Type 3, color spaces — by
/// parsing in-memory streams that contain them (these paths are otherwise only
/// hit by the corpus tests that skip in CI).
/// </summary>
public class OperatorCoverageTests
{
    private static ContentStream Parse(string content) =>
        new ContentStreamParser(Encoding.Latin1.GetBytes(content)).Parse();

    [Fact]
    public void Clipping_And_Shading_Operators_Parse()
    {
        // Clip both even-odd and nonzero, then a shading paint.
        var cs = Parse("q 0 0 100 100 re W n /Sh1 sh Q\nq 0 0 50 50 re W* n Q\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void Type3_GlyphMetrics_d0_d1_Parse()
    {
        // d0 (width only) and d1 (width + bbox).
        var cs = Parse("750 0 d0\n750 0 0 0 700 700 d1\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void MarkedContent_Operators_Parse()
    {
        var cs = Parse(
            "/P <</MCID 0>> BDC BT /F1 12 Tf (hi) Tj ET EMC\n" +
            "/Span BMC (x) Tj EMC\n" +
            "/Excise /Tag DP\n/Pt 1 MP\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void Compatibility_BX_EX_Parse()
    {
        var cs = Parse("BX /Unknown someop EX\nq Q\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void ColorSpace_And_Color_Operators_Parse()
    {
        var cs = Parse(
            "/CS0 CS /CS1 cs\n" +
            "0.1 G 0.2 g\n0.1 0.2 0.3 RG 0.4 0.5 0.6 rg\n" +
            "0 0 0 1 K 0 0 0 1 k\n" +
            "0.5 SC 0.5 SCN 0.5 sc 0.5 scn\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void GraphicsState_And_PathPaint_Operators_Parse()
    {
        var cs = Parse(
            "q 1 0 0 1 10 10 cm 2 w 1 J 1 j 10 M [3 2] 0 d 1.0 ri 1 i /GS1 gs\n" +
            "10 10 m 20 20 l 30 0 40 10 50 20 c 5 5 v 6 6 y h re\n" +
            "0 0 10 10 re S s f F f* B B* b b* n Q\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void Text_Operators_Parse()
    {
        var cs = Parse(
            "BT /F1 12 Tf 14 TL 1 Tc 2 Tw 100 Tz 0 Tr 1 Ts 10 20 Td 5 6 TD " +
            "1 0 0 1 7 8 Tm T* (a) Tj [(b) -10 (c)] TJ (d) ' 1 2 (e) \" ET\n");
        cs.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void XObject_Do_Parses()
    {
        Parse("q /Im1 Do Q\n").Operators.Should().NotBeEmpty();
    }

    /// <summary>
    /// The authoritative operator inventory (#350): a single stream exercising
    /// every standard content-stream operator. Each must (a) survive parsing as a
    /// <see cref="ContentOperator"/> and (b) survive a parse -> write -> parse
    /// round-trip through <see cref="ContentStreamWriter"/>. This is the
    /// regression guard that no operator is silently dropped on read or write.
    /// </summary>
    [Fact]
    public void AuthoritativeOperatorInventory_AllStandardOperators_ParseAndRoundTrip()
    {
        var content = string.Join("\n", new[]
        {
            "q 1 0 0 1 10 10 cm 2 w 1 J 1 j 10 M [3 2] 0 d 1.0 ri 1 i /GS1 gs",
            "10 10 m 20 20 l 30 0 40 10 50 20 c 5 5 v 6 6 y h 0 0 10 10 re",
            "S s f F f* B B* b b* W n W*",
            "/CS0 CS /CS1 cs 0.1 G 0.2 g 0.1 0.2 0.3 RG 0.4 0.5 0.6 rg " +
            "0 0 0 1 K 0 0 0 1 k 0.5 SC 0.5 SCN 0.5 sc 0.5 scn",
            "/Sh1 sh",
            "BT /F1 12 Tf 14 TL 1 Tc 2 Tw 100 Tz 0 Tr 1 Ts 10 20 Td 5 6 TD " +
            "1 0 0 1 7 8 Tm T* (a) Tj [(b) -10 (c)] TJ (d) ' 1 2 (e) \" ET",
            "/P <</MCID 0>> BDC /Span BMC EMC EMC /Pt 1 MP /Tg /Val DP BX /Unknown EX",
            "750 0 d0 750 0 0 0 700 700 d1",
            "/Im1 Do",
            "Q",
        });

        var expected = new[]
        {
            "q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re",
            "S","s","f","F","f*","B","B*","b","b*","W","n","W*",
            "CS","cs","G","g","RG","rg","K","k","SC","SCN","sc","scn",
            "sh",
            "BT","Tf","TL","Tc","Tw","Tz","Tr","Ts","Td","TD","Tm","T*","Tj","TJ","'","\"","ET",
            "BDC","BMC","EMC","MP","DP","BX","EX",
            "d0","d1","Do","Q",
        };

        var parsed = Parse(content).Operators.Select(o => o.Name).ToHashSet();
        foreach (var op in expected)
            parsed.Should().Contain(op, "operator '{0}' must be recognized by the parser", op);

        var bytes = new ContentStreamWriter().Write(Parse(content));
        var roundTripped = new ContentStreamParser(bytes).Parse()
            .Operators.Select(o => o.Name).ToHashSet();
        foreach (var op in expected)
            roundTripped.Should().Contain(op, "operator '{0}' must survive a write->parse round-trip", op);
    }
}
