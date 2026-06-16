using Pdfe.Core.Primitives;
using CorePdfFunctionEvaluator = Pdfe.Core.ColorSpaces.PdfFunctionEvaluator;

namespace Pdfe.Rendering;

internal static class PdfFunctionEvaluator
{
    public static double[]? Evaluate(PdfObject? funcObj, double t) =>
        CorePdfFunctionEvaluator.Evaluate(funcObj, t);

    public static double[]? Evaluate(PdfObject? funcObj, double[] inputs) =>
        CorePdfFunctionEvaluator.Evaluate(funcObj, inputs);
}
