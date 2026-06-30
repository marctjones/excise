using Pdfe.ImageInspection;
using Pdfe.Rendering.Differential;
using SkiaSharp;

namespace Pdfe.RenderTools;

partial class Program
{
    internal static void ApplyCorpusVisualDiffClassification(
        CorpusScanEntry entry,
        SKBitmap pdfe,
        SKBitmap reference)
    {
        using var comparablePdfe = pdfe.Width == reference.Width && pdfe.Height == reference.Height
            ? pdfe.Copy()
            : DifferentialMetrics.ResizeMatch(pdfe, reference.Width, reference.Height);
        var report = VisualDiffAnalyzer.Analyze(
            comparablePdfe,
            reference,
            VisualDiffAnalyzer.DefaultTolerance,
            originalActualWidth: pdfe.Width,
            originalActualHeight: pdfe.Height);

        entry.visualCategory = report.category;
        entry.visualHumanImpact = report.humanImpact;
        entry.visualDiffBounds = report.diffBounds;
        entry.visualTopRegions = report.topRegions.Take(5).ToArray();
    }
}
