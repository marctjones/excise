using Excise.Core.Primitives;
using Excise.Core.Document;
using CorePdfFunctionEvaluator = Excise.Core.ColorSpaces.PdfFunctionEvaluator;

namespace Excise.Rendering;

internal static class PdfFunctionEvaluator
{
    public static double[]? Evaluate(PdfObject? funcObj, double t) =>
        CorePdfFunctionEvaluator.Evaluate(funcObj, t);

    public static double[]? Evaluate(PdfObject? funcObj, double[] inputs) =>
        CorePdfFunctionEvaluator.Evaluate(funcObj, inputs);

    public static double[]? Evaluate(PdfObject? funcObj, double t, PdfDocument document) =>
        CorePdfFunctionEvaluator.Evaluate(funcObj, t, document.Resolve);

    public static double[]? Evaluate(PdfObject? funcObj, double[] inputs, PdfDocument document) =>
        CorePdfFunctionEvaluator.Evaluate(funcObj, inputs, document.Resolve);
}
