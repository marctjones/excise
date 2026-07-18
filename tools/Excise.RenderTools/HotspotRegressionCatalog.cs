namespace Excise.RenderTools;

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
                "Excise.Core",
                "library",
                "open",
                "excise-owned",
                "gate",
                "PDF document open, parse, and first structural access."),
            Definition(
                "renderer.page-render",
                "Excise.Rendering",
                "library",
                "render",
                "excise-owned",
                "gate",
                "excise in-process page rasterization."),
            Definition(
                "core.text-extract",
                "Excise.Core",
                "library",
                "text",
                "excise-owned",
                "gate",
                "Text extraction and search-input preparation."),
            Definition(
                "cli.render-page",
                "Excise.Cli",
                "cli",
                "render",
                "excise-owned",
                "gate",
                "excise CLI render command subprocess, including process startup and PNG write."),
            Definition(
                "gui.document-open",
                "Excise.App",
                "gui",
                "open",
                "excise-owned",
                "gate",
                "Desktop GUI document-open workflow phases."),
            Definition(
                "gui.input",
                "Excise.App",
                "gui",
                "input",
                "excise-owned",
                "gate",
                "Desktop GUI input responsiveness phases."),
            Definition(
                "gui.render",
                "Excise.App",
                "gui",
                "render",
                "excise-owned",
                "gate",
                "Desktop GUI display-render scheduling and visible-page settle phases."),
            Definition(
                "gui.search",
                "Excise.App",
                "gui",
                "search",
                "excise-owned",
                "gate",
                "Desktop search scheduling, execution, and result navigation phases."),
            Definition(
                "gui.annotation",
                "Excise.App",
                "gui",
                "annotation",
                "excise-owned",
                "gate",
                "Desktop annotation authoring workflow phases."),
            Definition(
                "gui.form",
                "Excise.App",
                "gui",
                "form",
                "excise-owned",
                "gate",
                "Desktop form authoring and form-edit workflow phases."),
            Definition(
                "gui.page-organization",
                "Excise.App",
                "gui",
                "page-organization",
                "excise-owned",
                "gate",
                "Desktop page move, rotate, extract, remove, and thumbnail refresh phases."),
            Definition(
                "gui.redaction",
                "Excise.App",
                "gui",
                "redaction",
                "excise-owned-security-critical",
                "gate",
                "Desktop redaction preview and workflow state phases."),
            Definition(
                "gui.save",
                "Excise.App",
                "gui",
                "save",
                "excise-owned",
                "gate",
                "Desktop save and save-as workflow phases."),
            Definition(
                "gui.close",
                "Excise.App",
                "gui",
                "close",
                "excise-owned",
                "gate",
                "Desktop close-document workflow phases."),
            Definition(
                "gui.display-render-capture",
                "Excise.App",
                "gui",
                "display",
                "excise-owned",
                "gate",
                "Headless GUI display, render, capture, and display-vs-renderer comparison phases."),
            Definition(
                "gui.thumbnail",
                "Excise.App",
                "gui",
                "thumbnail",
                "excise-owned",
                "gate",
                "Visible thumbnail loading workflow."),
            Definition(
                "redaction.synthetic-save",
                "Excise.Core",
                "library",
                "security-redaction",
                "excise-owned-security-critical",
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
                "Excise.RenderTools",
                "tooling",
                "analysis",
                "excise-owned-tooling",
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
            if (phase.StartsWith("gui.render.", StringComparison.Ordinal))
                return Find("gui.render");
            if (phase.StartsWith("gui.search.", StringComparison.Ordinal))
                return Find("gui.search");
            if (phase.StartsWith("gui.annotation.", StringComparison.Ordinal))
                return Find("gui.annotation");
            if (phase.StartsWith("gui.form.", StringComparison.Ordinal))
                return Find("gui.form");
            if (phase.StartsWith("gui.page-organization.", StringComparison.Ordinal))
                return Find("gui.page-organization");
            if (phase.StartsWith("gui.redaction.", StringComparison.Ordinal))
                return Find("gui.redaction");
            if (phase.StartsWith("gui.save.", StringComparison.Ordinal))
                return Find("gui.save");
            if (phase.StartsWith("gui.close.", StringComparison.Ordinal))
                return Find("gui.close");
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

            if (phase.StartsWith("excise-render-", StringComparison.Ordinal))
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
