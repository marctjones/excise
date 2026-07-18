using AwesomeAssertions;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// Unit tests for PdfColor struct.
/// Tests color creation, validation, conversions, and equality.
/// </summary>
public class PdfColorTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_ValidRgb_StoresValues()
    {
        var color = new PdfColor(0.5, 0.3, 0.8);

        color.R.Should().BeApproximately(0.5, 0.0001);
        color.G.Should().BeApproximately(0.3, 0.0001);
        color.B.Should().BeApproximately(0.8, 0.0001);
    }

    [Fact]
    public void Constructor_ClampsNegativeValues()
    {
        var color = new PdfColor(-0.5, -1.0, -0.3);

        color.R.Should().BeApproximately(0, 0.0001);
        color.G.Should().BeApproximately(0, 0.0001);
        color.B.Should().BeApproximately(0, 0.0001);
    }

    [Fact]
    public void Constructor_ClampsValuesGreaterThanOne()
    {
        var color = new PdfColor(1.5, 2.0, 0.5);

        color.R.Should().BeApproximately(1.0, 0.0001);
        color.G.Should().BeApproximately(1.0, 0.0001);
        color.B.Should().BeApproximately(0.5, 0.0001);
    }

    [Fact]
    public void Constructor_ZeroValues_Valid()
    {
        var color = new PdfColor(0, 0, 0);

        color.R.Should().Be(0);
        color.G.Should().Be(0);
        color.B.Should().Be(0);
    }

    [Fact]
    public void Constructor_OneValues_Valid()
    {
        var color = new PdfColor(1, 1, 1);

        color.R.Should().Be(1);
        color.G.Should().Be(1);
        color.B.Should().Be(1);
    }

    #endregion

    #region FromGray Factory Tests

    [Fact]
    public void FromGray_ValidValue_CreateGrayscaleColor()
    {
        var color = PdfColor.FromGray(0.5);

        color.R.Should().BeApproximately(0.5, 0.0001);
        color.G.Should().BeApproximately(0.5, 0.0001);
        color.B.Should().BeApproximately(0.5, 0.0001);
        color.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void FromGray_Zero_CreatesBlack()
    {
        var color = PdfColor.FromGray(0);

        color.R.Should().Be(0);
        color.G.Should().Be(0);
        color.B.Should().Be(0);
        color.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void FromGray_One_CreatesWhite()
    {
        var color = PdfColor.FromGray(1);

        color.R.Should().Be(1);
        color.G.Should().Be(1);
        color.B.Should().Be(1);
        color.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void FromGray_NegativeValue_Clamped()
    {
        var color = PdfColor.FromGray(-0.5);

        color.R.Should().Be(0);
        color.G.Should().Be(0);
        color.B.Should().Be(0);
    }

    [Fact]
    public void FromGray_ValueGreaterThanOne_Clamped()
    {
        var color = PdfColor.FromGray(1.5);

        color.R.Should().Be(1);
        color.G.Should().Be(1);
        color.B.Should().Be(1);
    }

    #endregion

    #region FromRgb Factory Tests

    [Fact]
    public void FromRgb_ValidBytes_ConvertsToPdfRange()
    {
        var color = PdfColor.FromRgb(128, 64, 192);

        color.R.Should().BeApproximately(128.0 / 255.0, 0.0001);
        color.G.Should().BeApproximately(64.0 / 255.0, 0.0001);
        color.B.Should().BeApproximately(192.0 / 255.0, 0.0001);
    }

    [Fact]
    public void FromRgb_ZeroBytes_CreatesBlack()
    {
        var color = PdfColor.FromRgb(0, 0, 0);

        color.R.Should().Be(0);
        color.G.Should().Be(0);
        color.B.Should().Be(0);
    }

    [Fact]
    public void FromRgb_MaxBytes_CreatesWhite()
    {
        var color = PdfColor.FromRgb(255, 255, 255);

        color.R.Should().BeApproximately(1.0, 0.0001);
        color.G.Should().BeApproximately(1.0, 0.0001);
        color.B.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void FromRgb_RoundTrip_PreservesApproximateValue()
    {
        var original = PdfColor.FromRgb(100, 150, 200);

        // Convert back to bytes (approximate)
        var r = (byte)(original.R * 255);
        var g = (byte)(original.G * 255);
        var b = (byte)(original.B * 255);

        // Should be within 1/255 of original
        var reconstructed = PdfColor.FromRgb(r, g, b);
        reconstructed.R.Should().BeApproximately(original.R, 0.01);
        reconstructed.G.Should().BeApproximately(original.G, 0.01);
        reconstructed.B.Should().BeApproximately(original.B, 0.01);
    }

    #endregion

    #region IsGrayscale Property Tests

    [Fact]
    public void IsGrayscale_AllComponentsEqual_True()
    {
        var color = new PdfColor(0.5, 0.5, 0.5);

        color.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void IsGrayscale_ComponentsDifferent_False()
    {
        var color = new PdfColor(0.5, 0.3, 0.7);

        color.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void IsGrayscale_RAndGEqual_ButNotB_False()
    {
        var color = new PdfColor(0.5, 0.5, 0.3);

        color.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void IsGrayscale_AllZero_True()
    {
        var color = new PdfColor(0, 0, 0);

        color.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void IsGrayscale_AllOne_True()
    {
        var color = new PdfColor(1, 1, 1);

        color.IsGrayscale.Should().BeTrue();
    }

    #endregion

    #region Static Constants Tests

    [Fact]
    public void Black_IsCorrect()
    {
        PdfColor.Black.R.Should().Be(0);
        PdfColor.Black.G.Should().Be(0);
        PdfColor.Black.B.Should().Be(0);
        PdfColor.Black.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void White_IsCorrect()
    {
        PdfColor.White.R.Should().Be(1);
        PdfColor.White.G.Should().Be(1);
        PdfColor.White.B.Should().Be(1);
        PdfColor.White.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void Red_IsCorrect()
    {
        PdfColor.Red.R.Should().Be(1);
        PdfColor.Red.G.Should().Be(0);
        PdfColor.Red.B.Should().Be(0);
        PdfColor.Red.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void Green_IsCorrect()
    {
        PdfColor.Green.R.Should().Be(0);
        PdfColor.Green.G.Should().Be(1);
        PdfColor.Green.B.Should().Be(0);
        PdfColor.Green.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void Blue_IsCorrect()
    {
        PdfColor.Blue.R.Should().Be(0);
        PdfColor.Blue.G.Should().Be(0);
        PdfColor.Blue.B.Should().Be(1);
        PdfColor.Blue.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void Yellow_IsCorrect()
    {
        PdfColor.Yellow.R.Should().Be(1);
        PdfColor.Yellow.G.Should().Be(1);
        PdfColor.Yellow.B.Should().Be(0);
        PdfColor.Yellow.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void Cyan_IsCorrect()
    {
        PdfColor.Cyan.R.Should().Be(0);
        PdfColor.Cyan.G.Should().Be(1);
        PdfColor.Cyan.B.Should().Be(1);
        PdfColor.Cyan.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void Magenta_IsCorrect()
    {
        PdfColor.Magenta.R.Should().Be(1);
        PdfColor.Magenta.G.Should().Be(0);
        PdfColor.Magenta.B.Should().Be(1);
        PdfColor.Magenta.IsGrayscale.Should().BeFalse();
    }

    #endregion

    #region Equality Tests

    [Fact]
    public void Equals_SameValues_True()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.3, 0.8);

        color1.Equals(color2).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentRed_False()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.4, 0.3, 0.8);

        color1.Equals(color2).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentGreen_False()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.2, 0.8);

        color1.Equals(color2).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentBlue_False()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.3, 0.7);

        color1.Equals(color2).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithinTolerance_True()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.50005, 0.30005, 0.80005);

        color1.Equals(color2).Should().BeTrue();
    }

    [Fact]
    public void Equals_OutsideTolerance_False()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.501, 0.3, 0.8);

        color1.Equals(color2).Should().BeFalse();
    }

    [Fact]
    public void ObjectEquals_SameValues_True()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        object color2 = new PdfColor(0.5, 0.3, 0.8);

        color1.Equals(color2).Should().BeTrue();
    }

    [Fact]
    public void ObjectEquals_DifferentType_False()
    {
        var color = new PdfColor(0.5, 0.3, 0.8);
        object other = "not a color";

        color.Equals(other).Should().BeFalse();
    }

    [Fact]
    public void ObjectEquals_Null_False()
    {
        var color = new PdfColor(0.5, 0.3, 0.8);

        color.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void OperatorEquals_SameValues_True()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.3, 0.8);

        (color1 == color2).Should().BeTrue();
    }

    [Fact]
    public void OperatorEquals_DifferentValues_False()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.4, 0.3, 0.8);

        (color1 == color2).Should().BeFalse();
    }

    [Fact]
    public void OperatorNotEquals_SameValues_False()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.3, 0.8);

        (color1 != color2).Should().BeFalse();
    }

    [Fact]
    public void OperatorNotEquals_DifferentValues_True()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.4, 0.3, 0.8);

        (color1 != color2).Should().BeTrue();
    }

    #endregion

    #region GetHashCode Tests

    [Fact]
    public void GetHashCode_SameValues_SameHash()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.3, 0.8);

        color1.GetHashCode().Should().Be(color2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentValues_LikelyDifferentHash()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.4, 0.3, 0.8);

        // Not guaranteed to be different, but likely
        color1.GetHashCode().Should().NotBe(color2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_CanBeUsedInSet()
    {
        var color1 = new PdfColor(0.5, 0.3, 0.8);
        var color2 = new PdfColor(0.5, 0.3, 0.8);
        var color3 = new PdfColor(0.4, 0.3, 0.8);

        var set = new HashSet<PdfColor> { color1, color2, color3 };

        set.Should().HaveCount(2, "identical colors should be deduplicated");
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_Grayscale_FormattedCorrectly()
    {
        var color = new PdfColor(0.5, 0.5, 0.5);

        var str = color.ToString();

        str.Should().Contain("Gray");
        str.Should().Contain("0.50");
    }

    [Fact]
    public void ToString_Rgb_FormattedCorrectly()
    {
        var color = new PdfColor(0.5, 0.3, 0.8);

        var str = color.ToString();

        str.Should().Contain("RGB");
        str.Should().Contain("0.50");
        str.Should().Contain("0.30");
        str.Should().Contain("0.80");
    }

    [Fact]
    public void ToString_Black_Grayscale()
    {
        var str = PdfColor.Black.ToString();

        str.Should().Contain("Gray");
    }

    [Fact]
    public void ToString_Red_Rgb()
    {
        var str = PdfColor.Red.ToString();

        str.Should().Contain("RGB");
    }

    #endregion
}
