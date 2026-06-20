using Pdfe.Core.Primitives;
using Pdfe.Core.Document;
using CorePdfFunctionEvaluator = Pdfe.Core.ColorSpaces.PdfFunctionEvaluator;

namespace Pdfe.Rendering;

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
