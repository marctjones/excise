using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Validation;

/// <summary>
/// A bounded, honest PDF/UA-1 (ISO 14289-1) conformance <b>checker</b> — it
/// verifies the structurally checkable rules excise can decide from the data it
/// already parses (document catalog, structure tree, page content), and reports
/// every rule with a pass / fail / not-applicable / not-checked status and a
/// location.
///
/// <para><b>Scope, stated up front.</b> PDF/UA-1 is defined by roughly 136
/// Matterhorn-Protocol checkpoints. This validator implements a deliberately
/// small subset — the tag/metadata/structure rules that can be decided
/// mechanically — and lists everything it does NOT cover in
/// <see cref="ValidationReport.UncoveredCheckpoints"/>. A green report means
/// "the checked subset passed", never "PDF/UA conformant". For an authoritative
/// verdict, run a reference validator such as veraPDF.</para>
/// </summary>
public static class PdfUaValidator
{
    /// <summary>Matterhorn areas this checker does not evaluate — surfaced in every report.</summary>
    private static readonly string[] Uncovered =
    {
        "Reading-order correctness (that the tag order matches the visual/logical order)",
        "Semantic correctness of tags (that a P is really a paragraph, that Alt text is meaningful)",
        "Table header-cell associations (/Scope, /Headers, /THead-/TBody roles) — only TR/TH/TD nesting is checked",
        "Colour contrast and use-of-colour (01-004, 04-*)",
        "Font embedding and character-to-Unicode mapping for every glyph (glyphs without a ToUnicode map)",
        "Annotations, links, and form-field accessibility (widgets, tab order, /TU) beyond tag presence",
        "Optional-content, XObject, and annotation appearance-stream tagging (only the page content stream is scanned for untagged text)",
        "Full XMP metadata schema validation (only dc:title presence is consulted as a Title fallback)",
        "Multi-page marked-content-to-page qualification when /Pg is absent (single-page association only)",
        "Approximately 120 further Matterhorn checkpoints not listed above",
    };

    /// <summary>
    /// Validate <paramref name="document"/> against the checked PDF/UA-1 subset.
    /// </summary>
    public static ValidationReport Validate(PdfDocument document)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        var results = new List<ValidationResult>();
        var tree = StructTreeAnalysis.Build(document);

        CheckTagged(document, tree, results);
        CheckLanguage(document, results);
        CheckTitle(document, results);
        CheckDisplayDocTitle(document, results);
        CheckRoleMap(tree, results);
        CheckFigures(tree, results);
        CheckHeadings(tree, results);
        CheckTables(tree, results);
        CheckLists(tree, results);
        CheckContentTagged(document, tree, results);

        return new ValidationReport(ConformanceStandard.PdfUA1, results, Uncovered);
    }

    // 7.1 — the document must be tagged and have a structure tree.
    private static void CheckTagged(PdfDocument doc, StructTreeAnalysis tree, List<ValidationResult> results)
    {
        bool marked = doc.Resolve(doc.Catalog.GetOptional("MarkInfo") ?? PdfNull.Instance)
            is PdfDictionary mi && mi.GetBool("Marked");
        results.Add(new ValidationResult(
            "UA-Marked",
            "Document is marked as tagged (/MarkInfo /Marked true).",
            RuleSeverity.Error,
            marked ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Catalog/MarkInfo",
            reference: "ISO 14289-1 §7.1; Matterhorn 01-005"));

        results.Add(new ValidationResult(
            "UA-StructTreeRoot",
            "Document has a structure tree (/StructTreeRoot).",
            RuleSeverity.Error,
            tree.StructTreeRoot != null ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Catalog/StructTreeRoot",
            reference: "ISO 14289-1 §7.1; Matterhorn 01-006"));
    }

    // 7.2 — natural language must be declared at the document level.
    private static void CheckLanguage(PdfDocument doc, List<ValidationResult> results)
    {
        bool hasLang = !string.IsNullOrWhiteSpace(doc.Language);
        results.Add(new ValidationResult(
            "UA-Lang",
            "Document declares a natural language (/Lang in the catalog).",
            RuleSeverity.Error,
            hasLang ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Catalog/Lang",
            reference: "ISO 14289-1 §7.2; Matterhorn 11-001"));
    }

    // 7.1 — a title must be present (Info /Title or XMP dc:title).
    private static void CheckTitle(PdfDocument doc, List<ValidationResult> results)
    {
        bool infoTitle = !string.IsNullOrWhiteSpace(doc.Title);
        bool xmpTitle = XmpHasDcTitle(doc);
        results.Add(new ValidationResult(
            "UA-Title",
            "Document has a title (Info /Title or XMP dc:title).",
            RuleSeverity.Error,
            (infoTitle || xmpTitle) ? RuleStatus.Pass : RuleStatus.Fail,
            location: infoTitle ? "Info/Title" : "Metadata/dc:title",
            reference: "ISO 14289-1 §7.1; Matterhorn 06-004"));
    }

    // 7.1 — the viewer must be told to show the title, not the file name.
    private static void CheckDisplayDocTitle(PdfDocument doc, List<ValidationResult> results)
    {
        bool display = doc.Resolve(doc.Catalog.GetOptional("ViewerPreferences") ?? PdfNull.Instance)
            is PdfDictionary vp && vp.GetBool("DisplayDocTitle");
        results.Add(new ValidationResult(
            "UA-DisplayDocTitle",
            "/ViewerPreferences /DisplayDocTitle is true.",
            RuleSeverity.Error,
            display ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Catalog/ViewerPreferences/DisplayDocTitle",
            reference: "ISO 14289-1 §7.1; Matterhorn 07-001"));
    }

    // 7.1 — every non-standard structure type must be role-mapped to a standard one.
    private static void CheckRoleMap(StructTreeAnalysis tree, List<ValidationResult> results)
    {
        if (tree.StructTreeRoot == null)
        {
            results.Add(NotChecked("UA-RoleMap",
                "Custom structure types map to standard types (/RoleMap).",
                "no structure tree", "ISO 14289-1 §7.1; Matterhorn 02-001"));
            return;
        }

        var unmapped = tree.UnmappedCustomTypes;
        results.Add(new ValidationResult(
            "UA-RoleMap",
            "Custom structure types are mapped to standard types via /RoleMap.",
            RuleSeverity.Error,
            unmapped.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
            location: unmapped.Count == 0 ? null : "unmapped types: " + string.Join(", ", unmapped.Select(t => "/" + t)),
            reference: "ISO 14289-1 §7.1; Matterhorn 02-001"));
    }

    // 7.3 — figures need a text alternative.
    private static void CheckFigures(StructTreeAnalysis tree, List<ValidationResult> results)
    {
        var figures = AllNodes(tree).Where(n => n.ResolvedType == "Figure").ToList();
        if (figures.Count == 0)
        {
            results.Add(NotApplicable("UA-Figure-Alt",
                "Figures have alternative text (/Alt or /ActualText).",
                "no /Figure elements", "ISO 14289-1 §7.3; Matterhorn 13-004"));
            return;
        }

        var missing = figures.Where(f =>
            string.IsNullOrWhiteSpace(f.Alt) && string.IsNullOrWhiteSpace(f.ActualText)).ToList();
        results.Add(new ValidationResult(
            "UA-Figure-Alt",
            "Every /Figure has alternative text (/Alt or /ActualText).",
            RuleSeverity.Error,
            missing.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
            location: missing.Count == 0
                ? $"{figures.Count} figure(s)"
                : $"{missing.Count} of {figures.Count} /Figure element(s) lack /Alt and /ActualText",
            reference: "ISO 14289-1 §7.3; Matterhorn 13-004"));
    }

    // 7.4.2 — heading levels must not skip (H1 → H3 without H2).
    private static void CheckHeadings(StructTreeAnalysis tree, List<ValidationResult> results)
    {
        // Collect headings in document (reading) order. Level 0 == unnumbered /H.
        var headings = new List<(int Level, string Type)>();
        void Walk(StructNode n)
        {
            if (n.ResolvedType == "H") headings.Add((0, n.ResolvedType));
            else if (n.ResolvedType.Length == 2 && n.ResolvedType[0] == 'H'
                     && n.ResolvedType[1] is >= '1' and <= '6')
                headings.Add((n.ResolvedType[1] - '0', n.ResolvedType));
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var r in tree.Roots) Walk(r);

        if (headings.Count == 0)
        {
            results.Add(NotApplicable("UA-Heading-Order",
                "Heading levels are used without skipping a level.",
                "no heading elements", "ISO 14289-1 §7.4.2; Matterhorn 14-002"));
            return;
        }

        var numbered = headings.Where(h => h.Level > 0).Select(h => h.Level).ToList();
        bool usesUnnumbered = headings.Any(h => h.Level == 0);

        // Mixing the strong (H1..H6) and weak (H) heading models is not conformant.
        if (numbered.Count > 0 && usesUnnumbered)
        {
            results.Add(new ValidationResult(
                "UA-Heading-Order",
                "A document must use either numbered headings (H1..H6) or unnumbered (H), not both.",
                RuleSeverity.Error,
                RuleStatus.Fail,
                location: "mixed /H and /H1../H6 headings",
                reference: "ISO 14289-1 §7.4.2; Matterhorn 14-002/14-003"));
            return;
        }

        if (numbered.Count == 0)
        {
            // Pure /H model — level nesting is not expressible; treat as pass.
            results.Add(new ValidationResult(
                "UA-Heading-Order",
                "Document uses the unnumbered /H heading model.",
                RuleSeverity.Warning,
                RuleStatus.Pass,
                location: $"{headings.Count} /H element(s)",
                reference: "ISO 14289-1 §7.4.2"));
            return;
        }

        // Numbered model: first heading should be H1; each heading at most one
        // level deeper than the previous.
        string? violation = null;
        if (numbered[0] != 1)
            violation = $"first heading is H{numbered[0]}, expected H1";
        int prev = numbered[0];
        for (int i = 1; i < numbered.Count && violation == null; i++)
        {
            if (numbered[i] > prev + 1)
                violation = $"H{prev} → H{numbered[i]} skips level(s)";
            prev = numbered[i];
        }

        results.Add(new ValidationResult(
            "UA-Heading-Order",
            "Numbered heading levels start at H1 and never skip a level.",
            RuleSeverity.Error,
            violation == null ? RuleStatus.Pass : RuleStatus.Fail,
            location: violation ?? $"{numbered.Count} numbered heading(s)",
            reference: "ISO 14289-1 §7.4.2; Matterhorn 14-002"));
    }

    // 7.5 — table structure: Table → (THead/TBody/TFoot →)? TR → TH|TD.
    private static void CheckTables(StructTreeAnalysis tree, List<ValidationResult> results)
    {
        var tables = AllNodes(tree).Where(n => n.ResolvedType == "Table").ToList();
        if (tables.Count == 0)
        {
            results.Add(NotApplicable("UA-Table-Structure",
                "Tables use TR / TH / TD structure.",
                "no /Table elements", "ISO 14289-1 §7.5; Matterhorn 15-003"));
            return;
        }

        var problems = new List<string>();
        foreach (var t in tables)
        {
            var rows = new List<StructNode>();
            foreach (var c in t.Children)
            {
                if (c.ResolvedType == "TR") rows.Add(c);
                else if (c.ResolvedType is "THead" or "TBody" or "TFoot")
                    rows.AddRange(c.Children.Where(g => g.ResolvedType == "TR"));
                else if (c.ResolvedType is not ("Caption"))
                    problems.Add($"/Table has unexpected child /{c.ResolvedType}");
            }
            if (rows.Count == 0)
            {
                problems.Add("/Table has no /TR rows");
                continue;
            }
            foreach (var tr in rows)
            {
                var badCells = tr.Children.Where(cell => cell.ResolvedType is not ("TH" or "TD")).ToList();
                foreach (var bad in badCells)
                    problems.Add($"/TR contains non-cell /{bad.ResolvedType}");
            }
        }

        results.Add(new ValidationResult(
            "UA-Table-Structure",
            "Every /Table contains /TR rows whose children are /TH or /TD cells.",
            RuleSeverity.Error,
            problems.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
            location: problems.Count == 0
                ? $"{tables.Count} table(s)"
                : string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? $"; +{problems.Count - 5} more" : ""),
            reference: "ISO 14289-1 §7.5; Matterhorn 15-003/15-005"));
    }

    // 7.6 — list structure: L → LI → (Lbl?, LBody).
    private static void CheckLists(StructTreeAnalysis tree, List<ValidationResult> results)
    {
        var lists = AllNodes(tree).Where(n => n.ResolvedType == "L").ToList();
        var strayLi = AllNodes(tree).Where(n => n.ResolvedType == "LI").ToList()
            .Where(li => !lists.Any(l => l.Children.Contains(li))).ToList();

        if (lists.Count == 0 && strayLi.Count == 0)
        {
            results.Add(NotApplicable("UA-List-Structure",
                "Lists use L / LI / Lbl / LBody structure.",
                "no /L elements", "ISO 14289-1 §7.6; Matterhorn 16-001"));
            return;
        }

        var problems = new List<string>();
        foreach (var l in lists)
        {
            var items = l.Children.Where(c => c.ResolvedType == "LI").ToList();
            var nonItems = l.Children.Where(c => c.ResolvedType is not ("LI" or "Caption")).ToList();
            if (items.Count == 0) problems.Add("/L has no /LI items");
            foreach (var bad in nonItems) problems.Add($"/L has non-item child /{bad.ResolvedType}");
        }
        foreach (var _ in strayLi) problems.Add("/LI is not a child of an /L");

        results.Add(new ValidationResult(
            "UA-List-Structure",
            "Every /L contains /LI items, and every /LI is a child of an /L.",
            RuleSeverity.Error,
            problems.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
            location: problems.Count == 0 ? $"{lists.Count} list(s)" : string.Join("; ", problems.Take(5)),
            reference: "ISO 14289-1 §7.6; Matterhorn 16-001/16-003"));

        // Recommended (not strictly required): each LI has an LBody.
        var allItems = lists.SelectMany(l => l.Children).Where(c => c.ResolvedType == "LI").ToList();
        if (allItems.Count > 0)
        {
            var noBody = allItems.Where(li => !li.Children.Any(c => c.ResolvedType == "LBody")).ToList();
            results.Add(new ValidationResult(
                "UA-List-ItemBody",
                "Each /LI contains an /LBody (recommended list-item structure).",
                RuleSeverity.Warning,
                noBody.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
                location: noBody.Count == 0 ? $"{allItems.Count} item(s)" : $"{noBody.Count} of {allItems.Count} /LI lack an /LBody",
                reference: "ISO 14289-1 §7.6"));
        }
    }

    // 7.1 — real (non-artifact) page content must be inside the structure tree.
    private static void CheckContentTagged(PdfDocument doc, StructTreeAnalysis tree, List<ValidationResult> results)
    {
        if (tree.StructTreeRoot == null)
        {
            results.Add(NotChecked("UA-Content-Tagged",
                "Real page content is tagged (inside the structure tree or marked /Artifact).",
                "no structure tree", "ISO 14289-1 §7.1; Matterhorn 01-002"));
            return;
        }

        var (qualified, agnostic) = tree.TaggedContent();
        var untaggedPages = new List<string>();

        for (int p = 1; p <= doc.PageCount; p++)
        {
            PdfPage page;
            try { page = doc.GetPage(p); }
            catch { continue; }

            int untagged = ContentTaggingScanner.CountUntaggedTextRuns(page, p, qualified, agnostic);
            if (untagged > 0)
                untaggedPages.Add($"page {p}: {untagged} untagged text run(s)");
        }

        results.Add(new ValidationResult(
            "UA-Content-Tagged",
            "Page text is either inside the structure tree or marked as an /Artifact.",
            RuleSeverity.Error,
            untaggedPages.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
            location: untaggedPages.Count == 0 ? null : string.Join("; ", untaggedPages.Take(5)),
            reference: "ISO 14289-1 §7.1; Matterhorn 01-002"));
    }

    private static IEnumerable<StructNode> AllNodes(StructTreeAnalysis tree) =>
        tree.Roots.SelectMany(r => r.DescendantsAndSelf());

    private static bool XmpHasDcTitle(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog.GetOptional("Metadata") ?? PdfNull.Instance) is not PdfStream s)
            return false;
        try
        {
            var xmp = s.GetDecodedString(System.Text.Encoding.UTF8);
            return xmp.Contains("dc:title", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static ValidationResult NotApplicable(string id, string desc, string why, string reference) =>
        new(id, desc, RuleSeverity.Error, RuleStatus.NotApplicable, why, reference);

    private static ValidationResult NotChecked(string id, string desc, string why, string reference) =>
        new(id, desc, RuleSeverity.Error, RuleStatus.NotChecked, why, reference);
}
