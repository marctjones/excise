using Excise.ImageInspection;
using Excise.Rendering.Differential;
using SkiaSharp;

namespace Excise.RenderTools;

partial class Program
{
    internal static void ApplyCorpusVisualDiffClassification(
        CorpusScanEntry entry,
        SKBitmap excise,
        SKBitmap reference)
    {
        using var comparableExcise = excise.Width == reference.Width && excise.Height == reference.Height
            ? excise.Copy()
            : DifferentialMetrics.ResizeMatch(excise, reference.Width, reference.Height);
        var report = VisualDiffAnalyzer.Analyze(
            comparableExcise,
            reference,
            VisualDiffAnalyzer.DefaultTolerance,
            originalActualWidth: excise.Width,
            originalActualHeight: excise.Height);

        entry.visualCategory = report.category;
        entry.visualHumanImpact = report.humanImpact;
        entry.visualDiffBounds = report.diffBounds;
        entry.visualTopRegions = report.topRegions.Take(5).ToArray();
    }
}
