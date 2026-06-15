using AwesomeAssertions;
using Pdfe.Core.ColorSpaces;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.ColorSpaces;

public class PdfFunctionEvaluatorTests
{
    [Fact]
    public void Evaluate_NullOrUnsupportedFunction_ReturnsNull()
    {
        PdfFunctionEvaluator.Evaluate(null, 0.5).Should().BeNull();
        PdfFunctionEvaluator.Evaluate(new PdfReal(1.25), 0.5).Should().BeNull();
        PdfFunctionEvaluator.Evaluate(new PdfDictionary { ["FunctionType"] = new PdfInteger(99) }, 0.5)
            .Should().BeNull();
    }

    [Fact]
    public void Evaluate_ArrayFunction_ConcatenatesComponentResultsAndSkipsUnsupportedEntries()
    {
        var functions = new PdfArray(
            ExponentialFunction(new[] { 0.0 }, new[] { 1.0 }, n: 1),
            new PdfName("Ignored"),
            ExponentialFunction(new[] { 1.0, 0.5 }, new[] { 0.0, 1.0 }, n: 1));

        var result = PdfFunctionEvaluator.Evaluate(functions, 0.25);

        result.Should().NotBeNull();
        result!.Should().HaveCount(3);
        result[0].Should().BeApproximately(0.25, 0.0001);
        result[1].Should().BeApproximately(0.75, 0.0001);
        result[2].Should().BeApproximately(0.625, 0.0001);
    }

    [Fact]
    public void Evaluate_EmptyArrayFunction_ReturnsNull()
    {
        PdfFunctionEvaluator.Evaluate(new PdfArray(new PdfName("Unsupported")), 0.5)
            .Should().BeNull();
    }

    [Fact]
    public void Evaluate_ExponentialFunction_UsesDefaultsAndPowersTint()
    {
        var result = PdfFunctionEvaluator.Evaluate(
            new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(2),
                ["N"] = new PdfReal(2)
            },
            0.5);

        result.Should().Equal(0.25);
    }

    [Fact]
    public void Evaluate_ExponentialFunction_InterpolatesMultiComponentArrays()
    {
        var result = PdfFunctionEvaluator.Evaluate(
            ExponentialFunction(new[] { 0.2, 0.8, 0.0 }, new[] { 1.0, 0.0 }, n: 2),
            0.5);

        result.Should().NotBeNull();
        result!.Should().HaveCount(3);
        result[0].Should().BeApproximately(0.4, 0.0001);
        result[1].Should().BeApproximately(0.6, 0.0001);
        result[2].Should().BeApproximately(0.25, 0.0001);
    }

    [Fact]
    public void Evaluate_StitchingFunction_ChoosesFunctionByBoundsAndEncodesInput()
    {
        var stitching = new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Functions"] = new PdfArray(
                ExponentialFunction(new[] { 0.0 }, new[] { 10.0 }, n: 1),
                ExponentialFunction(new[] { 20.0 }, new[] { 30.0 }, n: 1)),
            ["Bounds"] = Numbers(0.5),
            ["Encode"] = Numbers(0.0, 1.0, 1.0, 0.0)
        };

        PdfFunctionEvaluator.Evaluate(stitching, 0.25)![0]
            .Should().BeApproximately(5.0, 0.0001);
        PdfFunctionEvaluator.Evaluate(stitching, 0.75)![0]
            .Should().BeApproximately(25.0, 0.0001);
    }

    [Fact]
    public void Evaluate_StitchingFunction_HandlesMissingFunctionsAndDegenerateBounds()
    {
        PdfFunctionEvaluator.Evaluate(new PdfDictionary { ["FunctionType"] = new PdfInteger(3) }, 0.5)
            .Should().BeNull();

        var degenerate = new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Functions"] = new PdfArray(ExponentialFunction(new[] { 2.0 }, new[] { 4.0 }, n: 1)),
            ["Bounds"] = Numbers(0.0),
            ["Encode"] = Numbers(0.75, 1.0)
        };

        PdfFunctionEvaluator.Evaluate(degenerate, 0.0)![0]
            .Should().BeApproximately(3.5, 0.0001);
    }

    [Fact]
    public void Evaluate_SampledFunction_ReturnsNullForInvalidDefinitions()
    {
        PdfFunctionEvaluator.Evaluate(new PdfDictionary { ["FunctionType"] = new PdfInteger(0) }, 0.5)
            .Should().BeNull();

        PdfFunctionEvaluator.Evaluate(
            new PdfStream(new byte[] { 0 })
            {
                ["FunctionType"] = new PdfInteger(0),
                ["Size"] = Numbers(1)
            },
            0.5).Should().BeNull();

        PdfFunctionEvaluator.Evaluate(
            new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(0),
                ["Size"] = Numbers(2)
            },
            0.5).Should().BeNull();
    }

    [Fact]
    public void Evaluate_SampledFunction_InterpolatesEightBitSamplesAndAppliesRange()
    {
        var function = new PdfStream(new byte[] { 0, 255, 255, 0 })
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Size"] = Numbers(2),
            ["BitsPerSample"] = new PdfInteger(8),
            ["Range"] = Numbers(-1, 1, 10, 20)
        };

        var result = PdfFunctionEvaluator.Evaluate(function, 0.5);

        result.Should().NotBeNull();
        result![0].Should().BeApproximately(0.0, 0.0001);
        result[1].Should().BeApproximately(15.0, 0.0001);
    }

    [Fact]
    public void Evaluate_SampledFunction_DecodesSixteenBitSamples()
    {
        var function = new PdfStream(new byte[] { 0x00, 0x00, 0x80, 0x00, 0xFF, 0xFF })
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Size"] = Numbers(3),
            ["BitsPerSample"] = new PdfInteger(16),
            ["Range"] = Numbers(0, 1)
        };

        var result = PdfFunctionEvaluator.Evaluate(function, 0.5);

        result.Should().NotBeNull();
        result![0].Should().BeApproximately(32768.0 / 65535.0, 0.0001);
    }

    [Fact]
    public void Evaluate_SampledFunction_DecodesPackedSamples()
    {
        var function = new PdfStream(new byte[] { 0b0001_1011 })
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Size"] = Numbers(4),
            ["BitsPerSample"] = new PdfInteger(2),
            ["Range"] = Numbers(0, 1)
        };

        PdfFunctionEvaluator.Evaluate(function, 0.0)![0].Should().BeApproximately(0.0 / 3.0, 0.0001);
        PdfFunctionEvaluator.Evaluate(function, 1.0 / 3.0)![0].Should().BeApproximately(1.0 / 3.0, 0.0001);
        PdfFunctionEvaluator.Evaluate(function, 2.0 / 3.0)![0].Should().BeApproximately(2.0 / 3.0, 0.0001);
        PdfFunctionEvaluator.Evaluate(function, 1.0)![0].Should().BeApproximately(3.0 / 3.0, 0.0001);
    }

    [Fact]
    public void Evaluate_SampledFunction_NonPositiveBitsReturnZeroSamples()
    {
        var function = new PdfStream(new byte[] { 255, 255 })
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Size"] = Numbers(2),
            ["BitsPerSample"] = new PdfInteger(0),
            ["Range"] = Numbers(5, 10)
        };

        PdfFunctionEvaluator.Evaluate(function, 0.5)![0]
            .Should().BeApproximately(5.0, 0.0001);
    }

    [Fact]
    public void Evaluate_CalculatorFunction_ExecutesStackMathAndProcedureIf()
    {
        var program = "{ pop exch 360 mul sin 2 add 4 div sub abs .1 exch sub dup 0 lt { pop 0 } if 10 mul sqrt 1 exch sub dup dup }";
        var function = new PdfStream(System.Text.Encoding.Latin1.GetBytes(program))
        {
            ["FunctionType"] = new PdfInteger(4),
            ["Domain"] = Numbers(0, 1, 0, 1, 0, 1),
            ["Range"] = Numbers(0, 1, 0, 1, 0, 1)
        };

        var result = PdfFunctionEvaluator.Evaluate(function, new[] { 0.0, 0.5, 0.0 });

        result.Should().NotBeNull();
        result!.Should().HaveCount(3);
        result.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);
    }

    [Fact]
    public void Evaluate_CalculatorFunction_SupportsAtanDegrees()
    {
        var program = "{ pop exch 360 mul dup sin exch cos atan 360 div sub abs .1 exch sub dup 0 lt { pop 0 } if 10 mul sqrt 1 exch sub dup dup }";
        var function = new PdfStream(System.Text.Encoding.Latin1.GetBytes(program))
        {
            ["FunctionType"] = new PdfInteger(4),
            ["Domain"] = Numbers(0, 1, 0, 1, 0, 1),
            ["Range"] = Numbers(0, 1, 0, 1, 0, 1)
        };

        var result = PdfFunctionEvaluator.Evaluate(function, new[] { 0.0, 0.25, 0.0 });

        result.Should().NotBeNull();
        result!.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);
    }

    private static PdfDictionary ExponentialFunction(double[] c0, double[] c1, double n)
    {
        return new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(2),
            ["C0"] = Numbers(c0),
            ["C1"] = Numbers(c1),
            ["N"] = new PdfReal(n)
        };
    }

    private static PdfArray Numbers(params double[] values)
    {
        var array = new PdfArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
