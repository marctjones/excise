using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Redaction.Recovery;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using Obj = Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder.Obj;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// CLAUDE.md rule 6 for the output-profile stripper: a carrier the strip cannot
/// reach is removed or REFUSED, never skipped in silence. Each fixture here is
/// one place the strip used to stop (a depth cap, a swallowed exception) while
/// the report said clean success; each was measured leaking before the fix.
/// </summary>
/// <remarks>
/// The oracle is <see cref="SavedPdfLeakScanner"/>, which reads the saved bytes
/// with every stream inflated, not excise's text extractor (rule 4). Every
/// fixture redacts "VISIBLE", an unrelated term: what is under test is the
/// profile's promise to remove a carrier whole, which holds with no term match.
/// </remarks>
public class StripperReachRegressionTests
{
    private const string Visible = "BT /F1 12 Tf 72 700 Td (VISIBLE) Tj ET\n";
    private const string HiddenLayer = "/OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >>";
    private static readonly PdfRectangle VisibleBox = new(60, 690, 200, 720);

    private static Obj Stream(string dict, string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        return new Obj($"<< {dict} /Length {bytes.Length} >>", bytes);
    }

    /// <summary>Never both: the term in the saved file and a report that says clean.</summary>
    private static void NeverLeakAndClean(byte[] saved, string token, RedactionReport report) =>
        (SavedPdfLeakScanner.FindTerm(saved, token).Any() && report.IsCleanSuccess).Should().BeFalse(
            $"{token} is in the saved file, so the report must not say clean success: {report}");

    // ── action /Next chains (was: depth > 32 return) ──────────────────────

    /// <summary>
    /// A link whose <c>/A</c> is a kept <c>/GoTo</c> heading a chain of
    /// <paramref name="length"/> <c>/GoTo</c> actions, the last naming a script.
    /// Object 7 is the link; 8 onward the chain; the script is last.
    /// </summary>
    private static byte[] ActionChain(int length, bool cycle = false)
    {
        var objects = new List<Obj> { new("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A 8 0 R >>") };
        for (var i = 0; i < length; i++)
        {
            var next = cycle && i == length - 1 ? 8 : 9 + i;
            objects.Add(new($"<< /S /GoTo /D [3 0 R /Fit] /Next {next} 0 R >>"));
        }
        if (!cycle) objects.Add(new("<< /S /JavaScript /JS (app.alert('CHAINSCRIPTSECRET')) >>"));
        return RecoveryFixtureBuilder.Build(Visible, objects, pageExtra: "/Annots [7 0 R]");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(200)]
    public void AScriptAtTheEndOfALongActionChain_IsRemovedAndCounted(int length)
    {
        using var doc = PdfDocument.Open(ActionChain(length));
        var report = doc.RedactText("VISIBLE", RedactionOptions.Default);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "CHAINSCRIPTSECRET").Should().BeEmpty(
            "Standard removes every script, however far down a /Next chain it runs");
        report.Removals.Should().ContainSingle(r => r.Feature == "JavaScript action(s)")
            .Which.Count.Should().Be(1, "every removal is reported");
        report.IsCleanSuccess.Should().BeTrue();
    }

    [Fact]
    public void AnActionChainThatLeadsBackToItself_IsWalkedOnce()
    {
        using var doc = PdfDocument.Open(ActionChain(6, cycle: true));
        var report = doc.RedactText("VISIBLE", RedactionOptions.Default);

        report.IsCleanSuccess.Should().BeTrue("a cycle of internal navigation holds nothing to remove");
        doc.SaveToBytes().Should().NotBeEmpty();
    }

    [Fact]
    public void AnActionChain_PlantedFailure_KeepsTheScriptWhenScriptsAreKept()
    {
        using var doc = PdfDocument.Open(ActionChain(40));
        doc.RedactText("VISIBLE", RedactionOptions.Default with { RemoveScripts = false });
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "CHAINSCRIPTSECRET").Should().NotBeEmpty(
            "the fixture carries the script: without this the test above could pass on a file that never had it");
    }

    // ── direct-object nesting (was: ReachableDictionaries depth > 64) ──────

    private static byte[] DeeplyNestedLaunch(int depth)
    {
        var inner = "<< /A << /S /Launch /F (LAUNCHSECRET.exe) >> >>";
        for (var i = 0; i < depth; i++) inner = $"<< /X {inner} >>";
        return RecoveryFixtureBuilder.Build(Visible, catalogExtra: $"/Deep {inner}");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(70)]
    [InlineData(500)]
    public void AnExternalActionNestedDeepInDirectDictionaries_IsRemovedAndCounted(int depth)
    {
        using var doc = PdfDocument.Open(DeeplyNestedLaunch(depth));
        var report = doc.RedactText("VISIBLE", RedactionOptions.Default);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "LAUNCHSECRET").Should().BeEmpty(
            "the object-graph walk reaches a direct dictionary at any depth");
        report.Removals.Should().ContainSingle(r => r.Feature == "external-effect action(s)")
            .Which.Count.Should().Be(1);
        report.IsCleanSuccess.Should().BeTrue();
    }

    [Fact]
    public void DeepDirectNesting_PlantedFailure_KeepsTheLaunchWhenExternalActionsAreKept()
    {
        using var doc = PdfDocument.Open(DeeplyNestedLaunch(70));
        doc.RedactText("VISIBLE", RedactionOptions.Default with { RemoveExternalActions = false });
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "LAUNCHSECRET").Should().NotBeEmpty();
    }

    // ── field tree (was: RemoveFieldNames depth > 64) ──────────────────────

    /// <summary>A chain of <paramref name="levels"/> field nodes from <c>/AcroForm /Fields</c>, each named.</summary>
    private static byte[] DeepFieldTree(int levels)
    {
        var objects = new List<Obj>();
        for (var i = 0; i < levels; i++)
        {
            var number = 7 + i;
            var kids = i == levels - 1 ? "/FT /Tx" : $"/Kids [{number + 1} 0 R]";
            var parent = i == 0 ? "" : $"/Parent {number - 1} 0 R";
            objects.Add(new($"<< /T (FIELDNAMESECRET{i}X) /TU (FIELDTIPSECRET{i}X) {kids} {parent} >>"));
        }
        return RecoveryFixtureBuilder.Build(Visible, objects, catalogExtra: "/AcroForm << /Fields [7 0 R] >>");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    public void EveryFieldNameInADeepFieldTree_IsRemovedAndCounted(int levels)
    {
        // RemoveFieldNames without the flatten: under Maximum the flatten removes
        // /AcroForm first, so this is the hand-built option set that reaches the walk.
        var options = RedactionOptions.Default with { RemoveFieldNames = true };
        using var doc = PdfDocument.Open(DeepFieldTree(levels));
        var report = doc.RedactText("VISIBLE", options);
        var saved = doc.SaveToBytes();

        for (var i = 0; i < levels; i++)
        {
            SavedPdfLeakScanner.FindTerm(saved, $"FIELDNAMESECRET{i}X").Should().BeEmpty($"level {i}");
            SavedPdfLeakScanner.FindTerm(saved, $"FIELDTIPSECRET{i}X").Should().BeEmpty($"level {i}");
        }
        report.Removals.Should().ContainSingle(r => r.Feature == "form field name(s) and tooltip(s)")
            .Which.Count.Should().Be(2 * levels);
    }

    [Fact]
    public void AFieldTreeThatLeadsBackToItself_IsWalkedOnce()
    {
        var input = RecoveryFixtureBuilder.Build(Visible, new List<Obj>
            {
                new("<< /T (CYCLEFIELDSECRETA) /Kids [8 0 R] >>"),
                new("<< /T (CYCLEFIELDSECRETB) /Kids [7 0 R] /Parent 7 0 R >>"),
            },
            catalogExtra: "/AcroForm << /Fields [7 0 R] >>");
        using var doc = PdfDocument.Open(input);
        var report = doc.RedactText("VISIBLE", RedactionOptions.Default with { RemoveFieldNames = true });
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "CYCLEFIELDSECRETA").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "CYCLEFIELDSECRETB").Should().BeEmpty();
        report.Removals.Should().ContainSingle(r => r.Feature == "form field name(s) and tooltip(s)")
            .Which.Count.Should().Be(2);
    }

    /// <summary>
    /// The page tree gives no page 2 (it has no <c>/Type</c>, as in pdfium's
    /// bug_555784.pdf). Whatever the area path does with it, it never saves the
    /// page's hidden-layer text with a clean report. Measured: it throws on the
    /// tree before the profile pass, so <c>Apply</c>'s page refusal is a second
    /// line, pinned by <c>RedactionProfileTests.AnXObjectOnlyAPageTheWalkMissesDraws_IsKept</c>.
    /// </summary>
    [Fact]
    public void APageThePageTreeCannotGive_OnTheAreaPath_NeverLeaksAndReportsClean()
    {
        var input = RecoveryFixtureBuilder.Build(Visible, new List<Obj>
            {
                new("<< /Type /OCG /Name (Draft) >>"),
                new("<< /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 9 0 R " +
                    "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 7 0 R >> >> >>"),
                Stream("", "/OC /MC0 BDC BT /F1 12 Tf 72 600 Td (UNTREEDPAGESECRET) Tj ET EMC"),
            },
            catalogExtra: HiddenLayer, extraPages: new[] { 8 });
        using var doc = PdfDocument.Open(input);
        RedactionReport report;
        try { report = doc.GetPage(1).RedactAreaWithReport(VisibleBox, RedactionOptions.Default); }
        catch (Excise.Core.Parsing.PdfParseException) { return; }   // refused loudly: no saved copy
        NeverLeakAndClean(doc.SaveToBytes(), "UNTREEDPAGESECRET", report);
    }

    [Fact]
    public void DeepFieldTree_PlantedFailure_KeepsTheNamesWhenNamesAreKept()
    {
        using var doc = PdfDocument.Open(DeepFieldTree(40));
        doc.RedactText("VISIBLE", RedactionOptions.Default);
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "FIELDNAMESECRET39X").Should().NotBeEmpty();
    }

    // ── nested forms in the hidden-layer pass (was: depth > 8 return) ──────

    /// <summary>
    /// The page draws form 8, which draws form 9, and so on for
    /// <paramref name="levels"/> forms; the innermost holds a span in the OFF
    /// layer (object 7) that draws DEEPHIDDENSECRET. With
    /// <paramref name="selfDrawing"/> every form also draws itself.
    /// </summary>
    private static byte[] NestedForms(int levels, bool selfDrawing = false)
    {
        var objects = new List<Obj> { new("<< /Type /OCG /Name (Draft) >>") };
        for (var i = 0; i < levels; i++)
        {
            var number = 8 + i;
            var innermost = i == levels - 1;
            var self = selfDrawing ? $"/Me {number} 0 R" : "";
            var resources = innermost
                ? $"/Font << /F1 5 0 R >> /Properties << /MC0 7 0 R >> /XObject << {self} >>"
                : $"/XObject << /Fx {number + 1} 0 R {self} >>";
            var content = (innermost ? "/OC /MC0 BDC BT /F1 12 Tf 72 500 Td (DEEPHIDDENSECRET) Tj ET EMC" : "q /Fx Do Q")
                + (selfDrawing ? " q /Me Do Q" : "");
            objects.Add(Stream($"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << {resources} >>", content));
        }
        return RecoveryFixtureBuilder.Build(Visible + "q /Fx Do Q\n", objects,
            catalogExtra: HiddenLayer, resourcesExtra: "/XObject << /Fx 8 0 R >>");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(3, true)]
    public void AHiddenSpanInANestedForm_IsRemovedAndCounted(int levels, bool selfDrawing)
    {
        using var doc = PdfDocument.Open(NestedForms(levels, selfDrawing));
        var report = doc.RedactText("VISIBLE", RedactionOptions.Default);

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "DEEPHIDDENSECRET").Should().BeEmpty();
        report.Removals.Should().ContainSingle(r => r.Feature == "hidden optional-content span(s)")
            .Which.Count.Should().Be(1);
        report.Carriers.Should().NotContain(c => c.RefusedReason != null,
            "a form that draws itself is examined once, not refused at the depth limit");
        report.IsCleanSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Measured before the fix: 12 nested forms, the hidden span kept in the
    /// saved file and <c>IsCleanSuccess</c> true. The recursion still stops
    /// below 9 levels, and the first form it does not examine is refused.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void AFormNestedBelowTheHiddenLayerLimit_IsRefused_NotSkipped(int levels)
    {
        using var doc = PdfDocument.Open(NestedForms(levels));
        var report = doc.RedactText("VISIBLE", RedactionOptions.Default);
        var saved = doc.SaveToBytes();

        report.Carriers.Should().ContainSingle(c => c.Carrier == "form XObject 17 0 R")
            .Which.Should().Match<CarrierResult>(c => !c.Scrubbed
                && c.RefusedReason!.Contains("more than 9 form XObjects deep")
                && c.RefusedReason.Contains("hidden-layer pass did not examine it"));
        report.IsCleanSuccess.Should().BeFalse("a form the hidden-layer pass could not reach was left in place");
        report.ToString().Should().Contain("form XObject 17 0 R NOT scrubbed");
        SavedPdfLeakScanner.FindTerm(saved, "DEEPHIDDENSECRET").Should().NotBeEmpty(
            "kept and reported, never stripped or skipped in silence");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void NestedForms_OnTheAreaPath_NeverLeakAndReportClean(int levels)
    {
        using var doc = PdfDocument.Open(NestedForms(levels));
        var report = doc.GetPage(1).RedactAreaWithReport(VisibleBox, RedactionOptions.Default);
        NeverLeakAndClean(doc.SaveToBytes(), "DEEPHIDDENSECRET", report);
    }

    // ── a page whose content cannot be read (was: catch { continue; }) ─────

    /// <summary>
    /// Page 2 (object 8) has a two-stream <c>/Contents</c>: object 10 draws a
    /// span in the OFF layer and form 9, which holds one of its own; object 11
    /// names a filter nothing decodes.
    /// </summary>
    private static byte[] PageWithAnUnreadableContentStream() => RecoveryFixtureBuilder.Build(Visible,
        new List<Obj>
        {
            new("<< /Type /OCG /Name (Draft) >>"),
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [10 0 R 11 0 R] " +
                "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 7 0 R >> /XObject << /Fx 9 0 R >> >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 7 0 R >> >>",
                "/OC /MC0 BDC BT /F1 12 Tf 72 500 Td (FORMHIDDENSECRET) Tj ET EMC"),
            Stream("", "/OC /MC0 BDC BT /F1 12 Tf 72 600 Td (PAGEHIDDENSECRET) Tj ET EMC q /Fx Do Q"),
            Stream("/Filter /Nonexistent", "garbage"),
        },
        catalogExtra: HiddenLayer, extraPages: new[] { 8 });

    /// <summary>
    /// Measured before the fix, on the area path (RedactText throws on the
    /// page): both secrets kept and <c>IsCleanSuccess</c> true. The form is
    /// reachable without the page content, so its span now goes; the page's own
    /// is kept and the page refused.
    /// </summary>
    [Fact]
    public void APageWhoseContentCannotBeRead_IsRefused_AndItsFormsAreStillWalked()
    {
        using var doc = PdfDocument.Open(PageWithAnUnreadableContentStream());
        var report = doc.GetPage(1).RedactAreaWithReport(VisibleBox, RedactionOptions.Default);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "FORMHIDDENSECRET").Should().BeEmpty(
            "the form's own hidden span is removed whether or not its page can be read");
        report.Carriers.Should().ContainSingle(c => c.Carrier == "page 2 content stream")
            .Which.Should().Match<CarrierResult>(c => !c.Scrubbed
                && c.RefusedReason!.Contains("could not be read")
                && c.RefusedReason.Contains("hidden-layer pass"));
        report.IsCleanSuccess.Should().BeFalse();
        SavedPdfLeakScanner.FindTerm(saved, "PAGEHIDDENSECRET").Should().NotBeEmpty(
            "kept and reported, never stripped or skipped in silence");
    }
}
