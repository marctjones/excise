using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for PDF geometry types: PdfPoint, PdfRectangle, and PdfSize.
/// </summary>
public class PdfGeometryTests
{
    #region PdfSize Tests

    [Fact]
    public void PdfSize_Constructor_StoresWidthAndHeight()
    {
        // Act
        var size = new PdfSize(100, 200);

        // Assert
        size.Width.Should().Be(100);
        size.Height.Should().Be(200);
    }

    [Fact]
    public void PdfSize_Empty_HasZeroDimensions()
    {
        var empty = PdfSize.Empty;

        empty.Width.Should().Be(0);
        empty.Height.Should().Be(0);
    }

    [Fact]
    public void PdfSize_ToString_FormatsCorrectly()
    {
        var size = new PdfSize(100.5, 200.75);

        var str = size.ToString();

        str.Should().Contain("100.50");
        str.Should().Contain("200.75");
    }

    [Fact]
    public void PdfSize_Equality_WorksCorrectly()
    {
        var size1 = new PdfSize(100, 200);
        var size2 = new PdfSize(100, 200);
        var size3 = new PdfSize(100, 201);

        size1.Should().Be(size2);
        size1.Should().NotBe(size3);
    }

    [Fact]
    public void PdfSize_NegativeDimensions_Allowed()
    {
        var size = new PdfSize(-100, -200);

        size.Width.Should().Be(-100);
        size.Height.Should().Be(-200);
    }

    #endregion

    #region PdfRectangle Tests

    [Fact]
    public void PdfRectangle_Constructor_StoresCoordinates()
    {
        // Act
        var rect = new PdfRectangle(0, 0, 612, 792);

        // Assert
        rect.Left.Should().Be(0);
        rect.Bottom.Should().Be(0);
        rect.Right.Should().Be(612);
        rect.Top.Should().Be(792);
    }

    [Fact]
    public void PdfRectangle_Width_CalculatesCorrectly()
    {
        var rect = new PdfRectangle(100, 200, 500, 600);

        rect.Width.Should().Be(400);
    }

    [Fact]
    public void PdfRectangle_Height_CalculatesCorrectly()
    {
        var rect = new PdfRectangle(100, 200, 500, 600);

        rect.Height.Should().Be(400);
    }

    [Fact]
    public void PdfRectangle_Width_UsesAbsoluteValue()
    {
        var rect = new PdfRectangle(500, 200, 100, 600); // inverted X

        rect.Width.Should().Be(400); // abs(100 - 500)
    }

    [Fact]
    public void PdfRectangle_Height_UsesAbsoluteValue()
    {
        var rect = new PdfRectangle(100, 600, 500, 200); // inverted Y

        rect.Height.Should().Be(400); // abs(200 - 600)
    }

    [Fact]
    public void PdfRectangle_FromArray_CreatesRectangle()
    {
        var array = new PdfArray();
        array.Add(0.0);
        array.Add(0.0);
        array.Add(612.0);
        array.Add(792.0);

        var rect = PdfRectangle.FromArray(array);

        rect.Left.Should().Be(0);
        rect.Bottom.Should().Be(0);
        rect.Right.Should().Be(612);
        rect.Top.Should().Be(792);
    }

    [Fact]
    public void PdfRectangle_FromArray_WithWrongCount_ThrowsException()
    {
        var array = new PdfArray();
        array.Add(0.0);
        array.Add(0.0);
        array.Add(612.0);
        // Missing one element

        var act = () => PdfRectangle.FromArray(array);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Rectangle array must have 4 elements");
    }

    [Fact]
    public void PdfRectangle_Normalize_WithInvertedCoordinates_FixesOrder()
    {
        var rect = new PdfRectangle(500, 600, 100, 200); // both inverted

        var normalized = rect.Normalize();

        normalized.Left.Should().Be(100);
        normalized.Right.Should().Be(500);
        normalized.Bottom.Should().Be(200);
        normalized.Top.Should().Be(600);
    }

    [Fact]
    public void PdfRectangle_Normalize_WithCorrectCoordinates_NoChange()
    {
        var rect = new PdfRectangle(100, 200, 500, 600);

        var normalized = rect.Normalize();

        normalized.Should().Be(rect);
    }

    [Fact]
    public void PdfRectangle_IntersectsWith_OverlappingRectangles_ReturnsTrue()
    {
        var rect1 = new PdfRectangle(0, 0, 100, 100);
        var rect2 = new PdfRectangle(50, 50, 150, 150);

        var intersects = rect1.IntersectsWith(rect2);

        intersects.Should().BeTrue();
    }

    [Fact]
    public void PdfRectangle_IntersectsWith_NonOverlappingRectangles_ReturnsFalse()
    {
        var rect1 = new PdfRectangle(0, 0, 100, 100);
        var rect2 = new PdfRectangle(200, 200, 300, 300);

        var intersects = rect1.IntersectsWith(rect2);

        intersects.Should().BeFalse();
    }

    [Fact]
    public void PdfRectangle_IntersectsWith_AdjacentRectangles_ReturnsFalse()
    {
        var rect1 = new PdfRectangle(0, 0, 100, 100);
        var rect2 = new PdfRectangle(100, 0, 200, 100);

        var intersects = rect1.IntersectsWith(rect2);

        intersects.Should().BeFalse();
    }

    [Fact]
    public void PdfRectangle_IntersectsWith_ContainedRectangle_ReturnsTrue()
    {
        var outer = new PdfRectangle(0, 0, 100, 100);
        var inner = new PdfRectangle(20, 20, 80, 80);

        var intersects = outer.IntersectsWith(inner);

        intersects.Should().BeTrue();
    }

    [Fact]
    public void PdfRectangle_Contains_PointInside_ReturnsTrue()
    {
        var rect = new PdfRectangle(0, 0, 100, 100);

        var contains = rect.Contains(50, 50);

        contains.Should().BeTrue();
    }

    [Fact]
    public void PdfRectangle_Contains_PointOnBoundary_ReturnsTrue()
    {
        var rect = new PdfRectangle(0, 0, 100, 100);

        var contains1 = rect.Contains(0, 50);
        var contains2 = rect.Contains(100, 50);
        var contains3 = rect.Contains(50, 0);
        var contains4 = rect.Contains(50, 100);

        contains1.Should().BeTrue();
        contains2.Should().BeTrue();
        contains3.Should().BeTrue();
        contains4.Should().BeTrue();
    }

    [Fact]
    public void PdfRectangle_Contains_PointOutside_ReturnsFalse()
    {
        var rect = new PdfRectangle(0, 0, 100, 100);

        var contains = rect.Contains(150, 150);

        contains.Should().BeFalse();
    }

    [Fact]
    public void PdfRectangle_Contains_PointOutsideNegative_ReturnsFalse()
    {
        var rect = new PdfRectangle(0, 0, 100, 100);

        var contains = rect.Contains(-50, -50);

        contains.Should().BeFalse();
    }

    [Fact]
    public void PdfRectangle_ToString_FormatsCorrectly()
    {
        var rect = new PdfRectangle(10.5, 20.75, 100.25, 200.5);

        var str = rect.ToString();

        str.Should().Contain("10.50");
        str.Should().Contain("20.75");
        str.Should().Contain("100.25");
        str.Should().Contain("200.50");
    }

    [Fact]
    public void PdfRectangle_Equality_WorksCorrectly()
    {
        var rect1 = new PdfRectangle(0, 0, 100, 100);
        var rect2 = new PdfRectangle(0, 0, 100, 100);
        var rect3 = new PdfRectangle(0, 0, 100, 101);

        rect1.Should().Be(rect2);
        rect1.Should().NotBe(rect3);
    }

    #endregion

    #region PdfPoint Tests

    [Fact]
    public void PdfPoint_Constructor_StoresCoordinates()
    {
        // Act
        var point = new PdfPoint(100, 200);

        // Assert
        point.X.Should().Be(100);
        point.Y.Should().Be(200);
    }

    [Fact]
    public void PdfPoint_ToString_FormatsCorrectly()
    {
        var point = new PdfPoint(100.5, 200.75);

        var str = point.ToString();

        str.Should().Contain("100.50");
        str.Should().Contain("200.75");
    }

    [Fact]
    public void PdfPoint_Equality_WorksCorrectly()
    {
        var point1 = new PdfPoint(100, 200);
        var point2 = new PdfPoint(100, 200);
        var point3 = new PdfPoint(100, 201);

        point1.Should().Be(point2);
        point1.Should().NotBe(point3);
    }

    [Fact]
    public void PdfPoint_NegativeCoordinates_Allowed()
    {
        var point = new PdfPoint(-100, -200);

        point.X.Should().Be(-100);
        point.Y.Should().Be(-200);
    }

    [Fact]
    public void PdfPoint_ZeroCoordinates_Allowed()
    {
        var point = new PdfPoint(0, 0);

        point.X.Should().Be(0);
        point.Y.Should().Be(0);
    }

    #endregion
}
