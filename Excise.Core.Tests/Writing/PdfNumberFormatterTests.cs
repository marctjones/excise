using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Primitives;
using Excise.Core.Writing;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// Deterministic real-number formatting in PDF output (#762).
///
/// The writer used the "G" format, which emits the shortest string that
/// round-trips the exact double bit pattern — faithfully reproducing
/// accumulated float noise (216.01600000000002, 49.343999999999994). Those
/// digit strings are platform-dependent, so the same document saved on
/// Windows and macOS produced different bytes, and on Windows a noisy
/// coordinate's digit run coincidentally matched a redacted number,
/// tripping the carrier-agnostic saved-bytes redaction check (a false
/// positive of the byte-check, not a leak).
///
/// These tests pin the fix: every real-number emit site rounds to six
/// decimals and trims trailing zeros, so the emitted bytes are identical
/// on every platform and carry no noise digits.
/// </summary>
public class PdfNumberFormatterTests
{
    // The exact noisy values from #762. Each is one ulp away from the clean
    // value, so "G" prints the full noise digits while the fixed-precision
    // format collapses them.
    [Theory]
    [InlineData(216.01600000000002, "216.016")]
    [InlineData(49.343999999999994, "49.344")]
    [InlineData(166.67200000000003, "166.672")]
    [InlineData(-216.01600000000002, "-216.016")]
    public void Format_FloatNoise_CollapsesToCleanValue(double value, string expected)
    {
        PdfNumberFormatter.Format(value).Should().Be(expected);
    }

    [Fact]
    public void Format_ComputedNoise_IsDeterministic()
    {
        // The classic accumulation case: 0.1 + 0.2 is not the double nearest
        // to 0.3, so "G" would print 0.30000000000000004.
        PdfNumberFormatter.Format(0.1 + 0.2).Should().Be("0.3");

        // Advance-style accumulation (the #734 horizontal-advance path that
        // surfaced #762): summing glyph advances drifts off the clean value.
        double x = 0;
        for (int i = 0; i < 10; i++) x += 21.6016;
        PdfNumberFormatter.Format(x).Should().Be("216.016");
    }

    [Fact]
    public void PdfRealEmitters_UseTheSameNearIntegerPolicy()
    {
        const double value = 1.000009;
        const string expected = "1.000009";
        var real = new PdfReal(value);

        PdfNumberFormatter.Format(value).Should().Be(expected);
        PdfObjectWriter.Serialize(real).Should().Be(expected);
        new ContentOperator("w", new PdfObject[] { real }).ToString()
            .Should().Be($"{expected} w");

        var bytes = new ContentStreamWriter().Write(
            new ContentStream(new[] { new ContentOperator("w", new PdfObject[] { real }) }));
        Encoding.Latin1.GetString(bytes).Should().Be($"{expected} w\n");
    }

    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(700.0, "700")]
    [InlineData(0.0, "0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1.23456789, "1.234568")]
    [InlineData(0.001, "0.001")]
    [InlineData(1000000.25, "1000000.25")]
    public void Format_TypicalValues_UsesMinimalDigits(double value, string expected)
    {
        PdfNumberFormatter.Format(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(1e-8, "0")]
    [InlineData(-1e-8, "0")]
    [InlineData(1e-7, "0")]
    public void Format_BelowPrecision_CollapsesToZeroWithoutSign(double value, string expected)
    {
        // "0.######" spells (-5e-7, 0) as "-0"; the formatter normalizes it —
        // a signed zero would be a platform-visible artifact of noise sign.
        PdfNumberFormatter.Format(value).Should().Be(expected);
    }

    [Fact]
    public void Format_NeverUsesExponentNotation()
    {
        // Exponent notation is invalid PDF syntax; "G" produces it for
        // large/small magnitudes, "0.######" never does.
        PdfNumberFormatter.Format(12345678901234.5).Should().Be("12345678901234.5");
        PdfNumberFormatter.Format(0.0000001).Should().NotContainAny("E", "e");
    }

    [Fact]
    public void Format_IsCultureIndependent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            PdfNumberFormatter.Format(216.01600000000002).Should().Be("216.016",
                "PDF syntax requires '.' as the decimal separator regardless of thread culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ===== Emit-site integration: the operands of #762's example operators =====

    [Fact]
    public void ContentStreamWriter_NoisyTmAndRe_EmitsCleanDeterministicBytes()
    {
        // The exact operators from #762: a text matrix and a redaction-style
        // rectangle whose coordinates carry accumulated float noise.
        var tm = new ContentOperator("Tm", new PdfObject[]
        {
            new PdfReal(24), new PdfReal(0), new PdfReal(0), new PdfReal(24),
            new PdfReal(216.01600000000002), new PdfReal(700)
        });
        var re = new ContentOperator("re", new PdfObject[]
        {
            new PdfReal(166.67200000000003), new PdfReal(700),
            new PdfReal(49.343999999999994), new PdfReal(24)
        });

        var bytes = new ContentStreamWriter().Write(new ContentStream(new[] { tm, re }));
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().Be("24 0 0 24 216.016 700 Tm\n166.672 700 49.344 24 re\n",
            "content-stream coordinates must be byte-identical across platforms (#762)");
    }

    [Fact]
    public void PdfObjectWriter_NoisyReal_SerializesClean()
    {
        // Object-level reals (rects, matrices, widths) go through
        // PdfObjectWriter — the second emit path into the saved file.
        PdfObjectWriter.Serialize(new PdfReal(49.343999999999994)).Should().Be("49.344");
        PdfObjectWriter.Serialize(new PdfArray(new PdfObject[]
        {
            new PdfReal(166.67200000000003), new PdfReal(700.0),
            new PdfReal(216.01600000000002), new PdfReal(724.0)
        })).Should().Be("[166.672 700 216.016 724]");
    }

    [Fact]
    public void ContentOperator_ToString_UsesSameCleanFormat()
    {
        var op = new ContentOperator("Td", new PdfObject[]
        {
            new PdfReal(216.01600000000002), new PdfReal(49.343999999999994)
        });

        op.ToString().Should().Be("216.016 49.344 Td");
    }
}
