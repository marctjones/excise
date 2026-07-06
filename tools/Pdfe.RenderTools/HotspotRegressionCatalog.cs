namespace Pdfe.RenderTools;

partial class Program
{
    internal static class HotspotRegressionCatalog
    {
        private static readonly HotspotWorkloadDefinition Unknown = new()
        {
            workloadId = "unknown",
            component = "unknown",
            route = "unknown",
            category = "unclassified",
            scope = "diagnostic",
            regressionPolicy = "report-only",
            description = "Unclassified timing bucket; add it to the hotspot catalog before using it as a regression gate.",
        };

        internal static readonly IReadOnlyList<HotspotWorkloadDefinition> Definitions =
        [
            Definition(
                "core.document-open",
                "Pdfe.Core",
                "library",
                "open",
                "pdfe-owned",
                "gate",
                "PDF document open, parse, and first structural access."),
            Definition(
                "renderer.page-render",
                "Pdfe.Rendering",
                "library",
                "render",
                "pdfe-owned",
                "gate",
                "pdfe in-process page rasterization."),
            Definition(
                "core.text-extract",
                "Pdfe.Core",
                "library",
                "text",
                "pdfe-owned",
                "gate",
                "Text extraction and search-input preparation."),
            Definition(
                "cli.render-page",
                "Pdfe.Cli",
                "cli",
                "render",
                "pdfe-owned",
                "gate",
                "pdfe CLI render command subprocess, including process startup and PNG write."),
            Definition(
                "gui.document-open",
                "PdfEditor",
                "gui",
                "open",
                "pdfe-owned",
                "gate",
                "Desktop GUI document-open workflow phases."),
            Definition(
                "gui.input",
                "PdfEditor",
                "gui",
                "input",
                "pdfe-owned",
                "gate",
                "Desktop GUI input responsiveness phases."),
            Definition(
                "gui.display-render-capture",
                "PdfEditor",
                "gui",
                "display",
                "pdfe-owned",
                "gate",
                "Headless GUI display, render, capture, and display-vs-renderer comparison phases."),
            Definition(
                "gui.thumbnail",
                "PdfEditor",
                "gui",
                "thumbnail",
                "pdfe-owned",
                "gate",
                "Visible thumbnail loading workflow."),
            Definition(
                "redaction.synthetic-save",
                "Pdfe.Core",
                "library",
                "security-redaction",
                "pdfe-owned-security-critical",
                "gate",
                "Synthetic glyph-level redaction, save, and text-removal verification."),
            Definition(
                "reference.external-render",
                "ExternalReference",
                "external-cli",
                "render",
                "external-reference",
                "compare-only",
                "External reference renderer subprocess time and visual agreement evidence."),
            Definition(
                "corpus.compare-classify",
                "Pdfe.RenderTools",
                "tooling",
                "analysis",
                "pdfe-owned-tooling",
                "report-only",
                "Corpus image comparison, classification, and report overhead."),
        ];

        internal static HotspotWorkloadDefinition ForBenchmarkBucket(string name)
        {
            return name switch
            {
                "parser.document-open" => Find("core.document-open"),
                "renderer.page-render" => Find("renderer.page-render"),
                "text.extract-search-input" => Find("core.text-extract"),
                "cli.render-page-subprocess" => Find("cli.render-page"),
                "redaction.synthetic-save" => Find("redaction.synthetic-save"),
                "reference.external-render" => Find("reference.external-render"),
                _ => Unknown,
            };
        }

        internal static HotspotWorkloadDefinition ForPhase(string phase)
        {
            if (phase.StartsWith("gui.document-open.", StringComparison.Ordinal))
                return Find("gui.document-open");
            if (phase.StartsWith("gui.input.", StringComparison.Ordinal))
                return Find("gui.input");
            if (phase.StartsWith("gui.thumbnail.", StringComparison.Ordinal))
                return Find("gui.thumbnail");
            if (phase is "viewer-render-and-capture" ||
                phase is "expected-render-direct" ||
                phase is "expected-normalize-png-roundtrip" ||
                phase is "image-source-diff" ||
                phase is "image-source-diff-threshold" ||
                phase is "image-source-opacity-check" ||
                phase is "visual-surface-obscure-check" ||
                phase is "visual-luminance-measure" ||
                phase is "dimension-check" ||
                phase is "pdf-bytes-read-and-cache" ||
                phase is "pdf-bytes-cache-hit" ||
                phase is "semantic-status-policy")
            {
                return Find("gui.display-render-capture");
            }

            if (phase.StartsWith("pdfe-render-", StringComparison.Ordinal))
                return Find("renderer.page-render");
            if (phase.StartsWith("reference-", StringComparison.Ordinal))
                return Find("reference.external-render");
            if (phase == "compare-classify-and-overhead")
                return Find("corpus.compare-classify");

            return Unknown;
        }

        internal static HotspotWorkloadDefinition Find(string workloadId)
            => Definitions.FirstOrDefault(
                definition => definition.workloadId == workloadId,
                Unknown);

        private static HotspotWorkloadDefinition Definition(
            string workloadId,
            string component,
            string route,
            string category,
            string scope,
            string regressionPolicy,
            string description)
            => new()
            {
                workloadId = workloadId,
                component = component,
                route = route,
                category = category,
                scope = scope,
                regressionPolicy = regressionPolicy,
                description = description,
            };
    }

    internal sealed class HotspotWorkloadDefinition
    {
        public string workloadId { get; set; } = "";
        public string component { get; set; } = "";
        public string route { get; set; } = "";
        public string category { get; set; } = "";
        public string scope { get; set; } = "";
        public string regressionPolicy { get; set; } = "";
        public string description { get; set; } = "";
    }
}
