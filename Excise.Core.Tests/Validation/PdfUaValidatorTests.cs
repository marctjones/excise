using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Validation;
using Xunit;

namespace Excise.Core.Tests.Validation;

/// <summary>
/// Fixture-driven tests for <see cref="PdfUaValidator"/>. The PASS case is a real
/// <see cref="PdfDocumentBuilder.Tagged"/> document round-tripped through save/
/// reparse (so tagging is materialized, not just registered). Each FAIL case is a
/// minimally-crafted document that breaks exactly one rule, so the expected
/// verdict is known by construction — excise is never its own oracle for a PASS.
/// </summary>
public class PdfUaValidatorTests
{
    // ── PASS: a real, well-tagged document ────────────────────────────────────

    private static byte[] WellTaggedBytes()
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        return PdfDocumentBuilder.Create()
            .Tagged().DefaultFont(font).Language("en-US").Title("Accessible Sample")
            .Heading("Overview", 1)
            .Paragraph("Body text with an accent: café.")
            .Heading("Details", 2)
            .BulletList(new[] { "First", "Second" })
            .Table(new[] { new[] { "Item", "Qty" }, new[] { "Widget", "3" } }, headerRow: true)
            .SaveToBytes();
    }

    [Fact]
    public void WellTaggedDocument_PassesCheckedSubset()
    {
        var doc = PdfDocument.Open(WellTaggedBytes());
        var report = PdfUaValidator.Validate(doc);

        report.CheckedSubsetConformant.Should().BeTrue(
            "a Tagged() builder document should pass every checked Error rule. Report:\n" + report);

        Status(report, "UA-Marked").Should().Be(RuleStatus.Pass);
        Status(report, "UA-StructTreeRoot").Should().Be(RuleStatus.Pass);
        Status(report, "UA-Lang").Should().Be(RuleStatus.Pass);
        Status(report, "UA-Title").Should().Be(RuleStatus.Pass);
        Status(report, "UA-DisplayDocTitle").Should().Be(RuleStatus.Pass);
        Status(report, "UA-RoleMap").Should().Be(RuleStatus.Pass);
        Status(report, "UA-Heading-Order").Should().Be(RuleStatus.Pass);
        Status(report, "UA-Table-Structure").Should().Be(RuleStatus.Pass);
        Status(report, "UA-List-Structure").Should().Be(RuleStatus.Pass);
        Status(report, "UA-Content-Tagged").Should().Be(RuleStatus.Pass,
            "the builder tags every text run with an MCID the tree references");
    }

    [Fact]
    public void Report_AlwaysDeclaresUncoveredCheckpoints()
    {
        var report = PdfUaValidator.Validate(PdfDocument.Open(WellTaggedBytes()));
        report.UncoveredCheckpoints.Should().NotBeEmpty(
            "a green report must never imply full PDF/UA conformance");
    }

    // ── FAIL: one broken rule per fixture ─────────────────────────────────────

    [Fact]
    public void MissingMarkInfo_FailsMarkedRule()
    {
        var doc = Craft(SimpleTaggedTree(), marked: false);
        Status(PdfUaValidator.Validate(doc), "UA-Marked").Should().Be(RuleStatus.Fail);
    }

    [Fact]
    public void MissingStructTreeRoot_FailsStructTreeRule()
    {
        var doc = Craft(SimpleTaggedTree(), includeStructTree: false);
        var report = PdfUaValidator.Validate(doc);
        Status(report, "UA-StructTreeRoot").Should().Be(RuleStatus.Fail);
        Status(report, "UA-Content-Tagged").Should().Be(RuleStatus.NotChecked,
            "without a tree we cannot decide what is untagged");
    }

    [Fact]
    public void MissingLang_FailsLangRule()
    {
        var doc = Craft(SimpleTaggedTree(), lang: null);
        var report = PdfUaValidator.Validate(doc);
        Status(report, "UA-Lang").Should().Be(RuleStatus.Fail);
        Status(report, "UA-Marked").Should().Be(RuleStatus.Pass, "only /Lang should be broken");
    }

    [Fact]
    public void MissingTitle_FailsTitleRule()
    {
        var doc = Craft(SimpleTaggedTree(), title: null);
        Status(PdfUaValidator.Validate(doc), "UA-Title").Should().Be(RuleStatus.Fail);
    }

    [Fact]
    public void MissingDisplayDocTitle_FailsDisplayRule()
    {
        var doc = Craft(SimpleTaggedTree(), displayDocTitle: false);
        Status(PdfUaValidator.Validate(doc), "UA-DisplayDocTitle").Should().Be(RuleStatus.Fail);
    }

    [Fact]
    public void FigureWithoutAlt_Fails_WithAlt_Passes()
    {
        var noAlt = Craft(new PdfArray(SE("Document", new PdfArray(SE("Figure")))));
        Status(PdfUaValidator.Validate(noAlt), "UA-Figure-Alt").Should().Be(RuleStatus.Fail);

        var withAlt = Craft(new PdfArray(SE("Document", new PdfArray(SE("Figure", alt: "A photo of a cat")))));
        Status(PdfUaValidator.Validate(withAlt), "UA-Figure-Alt").Should().Be(RuleStatus.Pass);
    }

    [Fact]
    public void SkippedHeadingLevel_Fails_ProperOrder_Passes()
    {
        var skipped = Craft(new PdfArray(SE("Document", new PdfArray(SE("H1"), SE("H3")))));
        Status(PdfUaValidator.Validate(skipped), "UA-Heading-Order").Should().Be(RuleStatus.Fail);

        var ordered = Craft(new PdfArray(SE("Document", new PdfArray(SE("H1"), SE("H2"), SE("H3")))));
        Status(PdfUaValidator.Validate(ordered), "UA-Heading-Order").Should().Be(RuleStatus.Pass);
    }

    [Fact]
    public void TableWithNonCellChild_Fails()
    {
        // /Table -> /TR -> /P (a paragraph where a cell must be).
        var tree = new PdfArray(SE("Document", new PdfArray(
            SE("Table", new PdfArray(SE("TR", new PdfArray(SE("P"))))))));
        Status(PdfUaValidator.Validate(Craft(tree)), "UA-Table-Structure").Should().Be(RuleStatus.Fail);
    }

    [Fact]
    public void ValidTable_Passes()
    {
        var tree = new PdfArray(SE("Document", new PdfArray(
            SE("Table", new PdfArray(
                SE("TR", new PdfArray(SE("TH"), SE("TH"))),
                SE("TR", new PdfArray(SE("TD"), SE("TD"))))))));
        Status(PdfUaValidator.Validate(Craft(tree)), "UA-Table-Structure").Should().Be(RuleStatus.Pass);
    }

    [Fact]
    public void ListWithoutItems_Fails_ValidList_Passes()
    {
        var empty = new PdfArray(SE("Document", new PdfArray(SE("L", new PdfArray(SE("P"))))));
        Status(PdfUaValidator.Validate(Craft(empty)), "UA-List-Structure").Should().Be(RuleStatus.Fail);

        var valid = new PdfArray(SE("Document", new PdfArray(
            SE("L", new PdfArray(
                SE("LI", new PdfArray(SE("Lbl"), SE("LBody"))))))));
        var report = PdfUaValidator.Validate(Craft(valid));
        Status(report, "UA-List-Structure").Should().Be(RuleStatus.Pass);
        Status(report, "UA-List-ItemBody").Should().Be(RuleStatus.Pass);
    }

    [Fact]
    public void UnmappedCustomType_FailsRoleMap_MappedType_Passes()
    {
        var unmapped = new PdfArray(SE("Document", new PdfArray(SE("Chapter"))));
        Status(PdfUaValidator.Validate(Craft(unmapped)), "UA-RoleMap").Should().Be(RuleStatus.Fail);

        // Same custom type, but role-mapped to a standard type.
        var roleMap = new PdfDictionary();
        roleMap.SetName("Chapter", "Sect");
        var doc = Craft(new PdfArray(SE("Document", new PdfArray(SE("Chapter")))), roleMap: roleMap);
        Status(PdfUaValidator.Validate(doc), "UA-RoleMap").Should().Be(RuleStatus.Pass);
    }

    [Fact]
    public void RoleMappedCustomHeading_IsEvaluatedAsHeading()
    {
        // Chapter->H1, Section->H3 : a skipped level even though the raw types are custom.
        var roleMap = new PdfDictionary();
        roleMap.SetName("Chapter", "H1");
        roleMap.SetName("Section", "H3");
        var doc = Craft(new PdfArray(SE("Document", new PdfArray(SE("Chapter"), SE("Section")))), roleMap: roleMap);
        Status(PdfUaValidator.Validate(doc), "UA-Heading-Order").Should().Be(RuleStatus.Fail,
            "role-mapped custom headings must be resolved before the level-skip check");
    }

    // ── FAIL/PASS: untagged page content ──────────────────────────────────────

    [Fact]
    public void UntaggedPageText_FailsContentTagged()
    {
        // A tagged document (MarkInfo + StructTreeRoot present) whose page draws
        // text OUTSIDE any marked-content span — the isolated rule-10 violation.
        var doc = Craft(SimpleTaggedTree());
        SetPageContent(doc, "BT /F1 12 Tf 72 700 Td (Untagged secret) Tj ET");

        var report = PdfUaValidator.Validate(doc);
        Status(report, "UA-Marked").Should().Be(RuleStatus.Pass);
        Status(report, "UA-StructTreeRoot").Should().Be(RuleStatus.Pass);
        Status(report, "UA-Content-Tagged").Should().Be(RuleStatus.Fail);
    }

    [Fact]
    public void ArtifactedPageText_PassesContentTagged()
    {
        var doc = Craft(SimpleTaggedTree());
        SetPageContent(doc, "/Artifact BMC BT /F1 12 Tf 72 20 Td (Page 1) Tj ET EMC");
        Status(PdfUaValidator.Validate(doc), "UA-Content-Tagged").Should().Be(RuleStatus.Pass,
            "content inside an /Artifact span is intentionally untagged and conformant");
    }

    [Fact]
    public void StructReferencedPageText_PassesContentTagged()
    {
        // Tree references MCID 0 on page 1; the page draws that content inside a
        // matching BDC span.
        var p = SE("P", new PdfInteger(0));
        var doc = Craft(new PdfArray(SE("Document", new PdfArray(p))));
        // Attach the page reference so the (page, mcid) qualifies.
        p["Pg"] = doc.GetPageReference(1)!;
        SetPageContent(doc, "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Tagged body) Tj ET EMC");
        Status(PdfUaValidator.Validate(doc), "UA-Content-Tagged").Should().Be(RuleStatus.Pass);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RuleStatus Status(ValidationReport report, string ruleId) =>
        report.Results.Single(r => r.RuleId == ruleId).Status;

    private static PdfArray SimpleTaggedTree() =>
        new(SE("Document", new PdfArray(SE("P"))));

    private static PdfDictionary SE(string s, PdfObject? k = null, string? alt = null, string? actual = null)
    {
        var d = new PdfDictionary();
        d.SetName("Type", "StructElem");
        d.SetName("S", s);
        if (k != null) d["K"] = k;
        if (alt != null) d.SetString("Alt", alt);
        if (actual != null) d.SetString("ActualText", actual);
        return d;
    }

    private static PdfDocument Craft(
        PdfObject structRootK,
        bool marked = true,
        bool includeStructTree = true,
        string? lang = "en-US",
        string? title = "Crafted",
        bool displayDocTitle = true,
        PdfDictionary? roleMap = null)
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        if (title != null) doc.SetTitle(title);
        if (lang != null) doc.Language = lang;

        if (marked)
        {
            var mi = new PdfDictionary();
            mi.SetBool("Marked", true);
            doc.Catalog["MarkInfo"] = mi;
        }
        if (displayDocTitle)
        {
            var vp = new PdfDictionary();
            vp.SetBool("DisplayDocTitle", true);
            doc.Catalog["ViewerPreferences"] = vp;
        }
        if (includeStructTree)
        {
            var root = new PdfDictionary();
            root.SetName("Type", "StructTreeRoot");
            root["K"] = structRootK;
            if (roleMap != null) root["RoleMap"] = roleMap;
            doc.Catalog["StructTreeRoot"] = root;
        }
        return doc;
    }

    private static void SetPageContent(PdfDocument doc, string content) =>
        doc.GetPage(1).SetContentStreamBytes(Encoding.ASCII.GetBytes(content));
}
