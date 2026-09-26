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
        "Full XMP metadata schema validation (only the dc:title Lang Alt is consulted)",
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
        bool hasStructTree = document.Resolve(document.Catalog.GetOptional("StructTreeRoot") ?? PdfNull.Instance)
            is PdfDictionary;
        var nodes = AllNodes(document.GetStructureTree()).ToList();

        CheckTagged(document, hasStructTree, results);
        CheckLanguage(document, results);
        CheckTitle(document, results);
        CheckDisplayDocTitle(document, results);
        CheckRoleMap(hasStructTree, nodes, results);
        CheckFigures(nodes, results);
        CheckHeadings(nodes, results);
        CheckTables(nodes, results);
        CheckLists(nodes, results);
        CheckContentTagged(document, hasStructTree, nodes, results);

        return new ValidationReport(ConformanceStandard.PdfUA1, results, Uncovered);
    }

    // 7.1 — the document must be tagged and have a structure tree.
    private static void CheckTagged(PdfDocument doc, bool hasStructTree, List<ValidationResult> results)
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
            hasStructTree ? RuleStatus.Pass : RuleStatus.Fail,
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

    // 7.1 — the XMP dc:title must be present and non-empty. Info /Title does not
    // satisfy it (veraPDF fails an Info-only title, #1774).
    private static void CheckTitle(PdfDocument doc, List<ValidationResult> results)
    {
        results.Add(new ValidationResult(
            "UA-Title",
            "Document has a title (XMP dc:title).",
            RuleSeverity.Error,
            XmpHasDcTitle(doc) ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Metadata/dc:title",
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
    private static void CheckRoleMap(bool hasStructTree, List<PdfStructElement> nodes, List<ValidationResult> results)
    {
        if (!hasStructTree)
        {
            results.Add(NotChecked("UA-RoleMap",
                "Custom structure types map to standard types (/RoleMap).",
                "no structure tree", "ISO 14289-1 §7.1; Matterhorn 02-001"));
            return;
        }

        var unmapped = nodes.Where(n => !IsStandard(n.Type) && !IsStandard(n.RoleMappedType))
            .Select(n => n.Type).Distinct().ToList();
        results.Add(new ValidationResult(
            "UA-RoleMap",
            "Custom structure types are mapped to standard types via /RoleMap.",
            RuleSeverity.Error,
            unmapped.Count == 0 ? RuleStatus.Pass : RuleStatus.Fail,
            location: unmapped.Count == 0 ? null : "unmapped types: " + string.Join(", ", unmapped),
            reference: "ISO 14289-1 §7.1; Matterhorn 02-001"));
    }

    // 7.3 — figures need a text alternative.
    private static void CheckFigures(List<PdfStructElement> nodes, List<ValidationResult> results)
    {
        var figures = nodes.Where(n => n.RoleMappedType == "/Figure").ToList();
        if (figures.Count == 0)
        {
            results.Add(NotApplicable("UA-Figure-Alt",
                "Figures have alternative text (/Alt or /ActualText).",
                "no /Figure elements", "ISO 14289-1 §7.3; Matterhorn 13-004"));
            return;
        }

        var missing = figures.Where(f =>
            string.IsNullOrWhiteSpace(f.AltText) && string.IsNullOrWhiteSpace(f.ActualText)).ToList();
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
    private static void CheckHeadings(List<PdfStructElement> nodes, List<ValidationResult> results)
    {
        // Headings in document (reading) order. Level 0 == unnumbered /H.
        var headings = new List<int>();
        foreach (var type in nodes.Select(n => n.RoleMappedType))
        {
            if (type == "/H") headings.Add(0);
            else if (type.Length == 3 && type[1] == 'H' && type[2] is >= '1' and <= '6')
                headings.Add(type[2] - '0');
        }

        if (headings.Count == 0)
        {
            results.Add(NotApplicable("UA-Heading-Order",
                "Heading levels are used without skipping a level.",
                "no heading elements", "ISO 14289-1 §7.4.2; Matterhorn 14-002"));
            return;
        }

        var numbered = headings.Where(level => level > 0).ToList();
        bool usesUnnumbered = headings.Contains(0);

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
    private static void CheckTables(List<PdfStructElement> nodes, List<ValidationResult> results)
    {
        var tables = nodes.Where(n => n.RoleMappedType == "/Table").ToList();
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
            var rows = new List<PdfStructElement>();
            foreach (var c in t.Children)
            {
                if (c.RoleMappedType == "/TR") rows.Add(c);
                else if (c.RoleMappedType is "/THead" or "/TBody" or "/TFoot")
                    rows.AddRange(c.Children.Where(g => g.RoleMappedType == "/TR"));
                else if (c.RoleMappedType is not "/Caption")
                    problems.Add($"/Table has unexpected child {c.RoleMappedType}");
            }
            if (rows.Count == 0)
            {
                problems.Add("/Table has no /TR rows");
                continue;
            }
            foreach (var tr in rows)
            {
                var badCells = tr.Children.Where(cell => cell.RoleMappedType is not ("/TH" or "/TD")).ToList();
                foreach (var bad in badCells)
                    problems.Add($"/TR contains non-cell {bad.RoleMappedType}");
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
    private static void CheckLists(List<PdfStructElement> nodes, List<ValidationResult> results)
    {
        var lists = nodes.Where(n => n.RoleMappedType == "/L").ToList();
        var strayLi = nodes.Where(n => n.RoleMappedType == "/LI")
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
            var items = l.Children.Where(c => c.RoleMappedType == "/LI").ToList();
            var nonItems = l.Children.Where(c => c.RoleMappedType is not ("/LI" or "/Caption")).ToList();
            if (items.Count == 0) problems.Add("/L has no /LI items");
            foreach (var bad in nonItems) problems.Add($"/L has non-item child {bad.RoleMappedType}");
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
        var allItems = lists.SelectMany(l => l.Children).Where(c => c.RoleMappedType == "/LI").ToList();
        if (allItems.Count > 0)
        {
            var noBody = allItems.Where(li => !li.Children.Any(c => c.RoleMappedType == "/LBody")).ToList();
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
    private static void CheckContentTagged(
        PdfDocument doc, bool hasStructTree, List<PdfStructElement> nodes, List<ValidationResult> results)
    {
        if (!hasStructTree)
        {
            results.Add(NotChecked("UA-Content-Tagged",
                "Real page content is tagged (inside the structure tree or marked /Artifact).",
                "no structure tree", "ISO 14289-1 §7.1; Matterhorn 01-002"));
            return;
        }

        // Tagged (page, MCID) pairs. A reference with no /Pg of its own or on its
        // element matches that MCID on any page, so a single-page document, where
        // the page is unambiguous, still matches.
        var qualified = new HashSet<(int, int)>();
        var agnostic = new HashSet<int>();
        foreach (var reference in nodes.SelectMany(n => n.MarkedContent))
        {
            if (reference.PageNumber is int page) qualified.Add((page, reference.Mcid));
            else agnostic.Add(reference.Mcid);
        }
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

    private static IEnumerable<PdfStructElement> AllNodes(PdfStructElement? element) =>
        element == null ? [] : element.Children.SelectMany(AllNodes).Prepend(element);

    private static bool IsStandard(string type) =>
        PdfStructTreeParser.StandardStructureTypes.Contains(type.TrimStart('/'));

    // #1532/#1774: dc:title, parsed as the Lang Alt XMP defines for it
    // (<dc:title><rdf:Alt><rdf:li>T</rdf:li></rdf:Alt></dc:title>), with
    // length-bounded captures and no nested quantifier, so they stay linear on
    // hostile input. Same construction as PdfAIdentityXmp (#1524/#1526).
    private static readonly System.Text.RegularExpressions.Regex DcTitleElement = new(
        @"<dc:title\b[^>]{0,512}>(?<v>.{0,8192}?)</dc:title>",
        System.Text.RegularExpressions.RegexOptions.Singleline
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex RdfListItem = new(
        @"<rdf:li\b(?<attrs>[^>]{0,512})>(?<v>.{0,8192}?)</rdf:li>",
        System.Text.RegularExpressions.RegexOptions.Singleline
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The document's XMP <c>dc:title</c>, or null when there is no USABLE one
    /// (#1532).
    /// </summary>
    /// <remarks>
    /// <para>This used to be <c>xmp.Contains("dc:title")</c> — a substring
    /// match standing in for a parse, the same error as #1524 in a different
    /// schema. It passed <c>&lt;dc:title/&gt;</c>, an <c>rdf:Alt</c> holding an
    /// empty <c>rdf:li</c>, and the literal text "dc:title" occurring anywhere
    /// in the packet: a comment, a custom schema, another property's value.
    /// ISO 14289-1 §7.1 requires the title to be present AND non-empty
    /// (Matterhorn 06-003/06-004 check the title itself), so a file with an
    /// empty title was reported conformant on this rule.</para>
    /// <para>Presence and VALUE are deliberately the same question here: a
    /// title that exists and is empty does not satisfy the rule, so there is
    /// nothing for a caller to do with "present but unusable". veraPDF checks
    /// only that the Lang Alt exists, so an empty <c>rdf:li</c> is the one shape
    /// where this is stricter than it.</para>
    /// <para>Only the <c>rdf:Alt</c>/<c>rdf:li</c> form counts. A bare
    /// <c>&lt;dc:title&gt;Text&lt;/dc:title&gt;</c> and the attribute
    /// <c>dc:title="Text"</c> are not a Lang Alt, and veraPDF does not read
    /// either as a title (#1774). Nor is an <c>rdf:li</c> without an
    /// <c>xml:lang</c> attribute: veraPDF ignores it, and fails a title whose
    /// entries are all untagged (#1875).</para>
    /// </remarks>
    internal static string? ReadDcTitle(string xmp)
    {
        if (string.IsNullOrEmpty(xmp))
            return null;

        var element = DcTitleElement.Match(xmp);
        if (!element.Success)
            return null;

        // Prefer x-default, as XMP readers do.
        string? fallback = null;
        foreach (System.Text.RegularExpressions.Match li in RdfListItem.Matches(element.Groups["v"].Value))
        {
            var attrs = li.Groups["attrs"].Value;
            if (!attrs.Contains("xml:lang", StringComparison.Ordinal))
                continue;
            var value = Clean(li.Groups["v"].Value);
            if (value == null)
                continue;
            if (attrs.Contains("x-default", StringComparison.Ordinal))
                return value;
            fallback ??= value;
        }
        return fallback;
    }

    /// <summary>Decoded and trimmed, or null when nothing usable is left.</summary>
    private static string? Clean(string raw)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(raw).Trim();
        return decoded.Length == 0 ? null : decoded;
    }

    private static bool XmpHasDcTitle(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog.GetOptional("Metadata") ?? PdfNull.Instance) is not PdfStream s)
            return false;
        try
        {
            return ReadDcTitle(s.GetDecodedString(System.Text.Encoding.UTF8)) != null;
        }
        catch { return false; }
    }

    private static ValidationResult NotApplicable(string id, string desc, string why, string reference) =>
        new(id, desc, RuleSeverity.Error, RuleStatus.NotApplicable, why, reference);

    private static ValidationResult NotChecked(string id, string desc, string why, string reference) =>
        new(id, desc, RuleSeverity.Error, RuleStatus.NotChecked, why, reference);
}
