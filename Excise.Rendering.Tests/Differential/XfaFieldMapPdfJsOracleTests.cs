using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2027 (XFA Phase 3 S0): the field map against pdf.js. The oracle files under
/// <c>tests/xfa-pdfjs-field-boxes/</c> were measured by <c>scripts/xfa-pdfjs-field-boxes.mjs</c>:
/// pdf.js (<c>enableXfa</c>) lays the form out, headless Chrome places it, and the box of every
/// visible <c>.xfaField</c> / <c>.xfaExclgroup</c> and the options of every <c>select</c> are read
/// back. Excise is never its own oracle here.
/// <para>Acceptance: every box pdf.js draws has a map entry of the same name and kind on the same
/// page within 4pt on every edge, and every drop-down's (value, label) list is the one pdf.js shows.
/// Matching is by page, name and kind, then nearest box, one map entry per pdf.js box.</para>
/// <para>Corpus: the four real forms, and the 21 files #1547 Phase 2 laid out (pdfium corpus and
/// pdf.js's issue14130) except <c>xfa/xfa_break_before_after</c> and <c>bug_1301</c>, which pdf.js
/// cannot open: no oracle, so no row. <c>bug_306123</c> is a known divergence (below).</para>
/// </summary>
public class XfaFieldMapPdfJsOracleTests
{
    private const double Tolerance = 4.0;

    private readonly ITestOutputHelper _out;

    public XfaFieldMapPdfJsOracleTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<string> Forms => new()
    {
        "xfa-real/imm5257e", "xfa-real/imm1295e", "xfa-real/ohio-expense-report", "xfa-real/hsbc-cloture-compte",
        "pdfium/simple_xfa", "pdfium/bug_504416752", "pdfium/bug_1058653", "pdfium/bug_1055869",
        "pdfium/xfa/email_recommended", "pdfium/xfa/xfa_combobox", "pdfium/xfa/xfa_date_time_edit",
        "pdfium/xfa/xfa_multiline_textfield", "pdfium/xfa/xfa_image_edit",
        "pdfium/pixel/xfa_specific/barcode_test", "pdfium/pixel/xfa_specific/dynamic_list_box_allow_multiple_selection",
        "pdfium/pixel/xfa_specific/dynamic_password_field_background_fill",
        "pdfium/pixel/xfa_specific/dynamic_table_color_and_width", "pdfium/pixel/xfa_specific/resolve_nodes_0",
        "pdfium/javascript/xfa_specific/resolve_nodes_1", "pdfium/javascript/xfa_specific/resolve_nodes_2",
        "pdfium/pixel/xfa_specific/xfa_node_caption", "pdfjs/issue14130",
    };

    private static string CorpusScript(string form) => form.Split('/')[0] switch
    {
        "xfa-real" => "scripts/download-xfa-real-corpus.sh",
        "pdfium" => "scripts/download-pdfium-corpus.sh",
        _ => "scripts/download-pdfjs-corpus.sh",
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void EveryFieldPdfJsDraws_HasAMapRectWithin4pt_AndTheSameOptions(string form) => Compare(form);

    /// <summary>
    /// Known divergence (#1547 Phase 2): hidden subforms carry <c>breakBefore startNew</c>, which pdf.js
    /// honours while they are hidden, and <c>initialize</c> scripts excise does not run presumably show
    /// them. pdf.js draws 4 pages, excise 2. Page 1 is laid out by both before any of those breaks and
    /// is compared; if the page counts ever agree, this row should join the theory above.
    /// </summary>
    [Fact]
    public void Bug306123_FirstPageAgrees_PageCountDiffersByScriptsOnly()
        => Compare("pdfium/bug_306123", pagesToCompare: 1, excisePageCount: 2);

    private void Compare(string form, int? pagesToCompare = null, int? excisePageCount = null)
    {
        var source = TestRepoLayout.FindFile(new[] { "test-pdfs" }.Concat((form + ".pdf").Split('/')).ToArray());
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            $"corpus file ({CorpusScript(form)})", $"test-pdfs/{form}.pdf"));
        var oraclePath = TestRepoLayout.FindFile("tests", "xfa-pdfjs-field-boxes", form.Replace("/", "__", StringComparison.Ordinal) + ".json");
        oraclePath.Should().NotBeNull("the pdf.js measurement is tracked in the repository");

        var bytes = File.ReadAllBytes(source!);
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(oraclePath!));
        var rootElement = oracle.RootElement;
        rootElement.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)),
            "the oracle was measured on this exact corpus file");
        rootElement.GetProperty("pureXfa").GetBoolean().Should().BeTrue("pdf.js renders the form from its XFA");

        using var document = PdfDocument.Open(bytes);
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);

        var pdfJsPages = rootElement.GetProperty("pages").EnumerateArray().ToList();
        document.PageCount.Should().Be(excisePageCount ?? pdfJsPages.Count, "pdf.js lays the form out on this many pages");

        int boxes = 0, matched = 0, lists = 0, listsEqual = 0, listsFromData = 0;
        double worst = 0;
        var failures = new List<string>();
        var used = new HashSet<XfaFieldInfo>(ReferenceEqualityComparer.Instance);
        for (int pageIndex = 0; pageIndex < (pagesToCompare ?? pdfJsPages.Count); pageIndex++)
        {
            var page = document.Pages[pageIndex];
            foreach (var box in pdfJsPages[pageIndex].GetProperty("boxes").EnumerateArray())
            {
                boxes++;
                var name = box.GetProperty("name").GetString() ?? string.Empty;
                var kind = box.GetProperty("kind").GetString() == "exclGroup" ? "exclGroup" : "field";
                // pdf.js boxes are top-left page points; convert once, as the map does.
                var expected = PdfCoordinateMapper.ToContentPoints(page, PdfPageRect.VisualPoints(page.PageNumber,
                    box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(),
                    box.GetProperty("w").GetDouble(), box.GetProperty("h").GetDouble()));

                XfaFieldInfo? best = null;
                double bestDistance = double.MaxValue;
                foreach (var field in result.Fields)
                {
                    if (field.PageIndex != pageIndex || field.Rect is not { } rect || used.Contains(field)
                        || (field.Name ?? string.Empty) != name
                        || (field.UiKind == "exclGroup" ? "exclGroup" : "field") != kind)
                    {
                        continue;
                    }
                    var distance = EdgeDistance(rect, expected);
                    if (distance < bestDistance)
                    {
                        best = field;
                        bestDistance = distance;
                    }
                }

                var where = $"page {pageIndex + 1} {kind} '{name}' at ({expected.X:F1},{expected.Y:F1},{expected.Width:F1}x{expected.Height:F1})";
                if (best == null)
                {
                    failures.Add($"{where}: no map entry of that name and kind on the page");
                    continue;
                }
                used.Add(best);
                worst = Math.Max(worst, bestDistance);
                if (bestDistance > Tolerance)
                {
                    var r = best.Rect!.Value;
                    failures.Add($"{where}: nearest map entry {best.SomPath} at ({r.X:F1},{r.Y:F1},{r.Width:F1}x{r.Height:F1}), {bestDistance:F1}pt off");
                    continue;
                }
                matched++;

                if (box.TryGetProperty("options", out var options))
                {
                    lists++;
                    var count = options.GetProperty("count").GetInt32();
                    var hash = options.GetProperty("sha256").GetString();
                    if (best.Items.Count == count && OptionsHash(best.Items) == hash)
                    {
                        listsEqual++;
                        if (best.ItemsFromData)
                            listsFromData++;
                    }
                    else
                        failures.Add($"{where}: options differ from pdf.js ({best.Items.Count} items in the map, {count} in pdf.js; {best.SomPath})");
                }
            }
        }

        _out.WriteLine($"{form}: pdf.js {rootElement.GetProperty("pdfjs").GetString()}, {boxes} boxes, {matched} within {Tolerance}pt "
            + $"(worst matched edge distance {worst:F2}pt), option lists {listsEqual}/{lists} equal ({listsFromData} from bindItems); map has {result.Fields.Count} entries, "
            + $"{result.Fields.Count(f => f.Rect != null)} drawn, {result.Fields.Count(f => f.Editable)} editable "
            + $"(blocked: {result.Fields.Count(f => f.Access != XfaAccess.Open)} access, {result.Fields.Count(f => f.CalculateBlocksUserValue)} calculate, "
            + $"{result.Fields.Count(f => f.Binding == XfaBindingKind.None)} unbound, {result.Fields.Count(f => f.HasBindPicture)} bind picture, "
            + $"{result.Fields.Count(f => f.InPageArea)} page area)");
        foreach (var failure in failures)
            _out.WriteLine("  " + failure);

        failures.Should().BeEmpty();
        matched.Should().Be(boxes);
        listsEqual.Should().Be(lists);
        if (form == "xfa-real/imm5257e")
            listsFromData.Should().Be(34, "IMM 5257e's 34 drop-downs take their lists from the datasets through bindItems (#2018)");
    }

    /// <summary>The largest difference between corresponding edges, in points.</summary>
    private static double EdgeDistance(PdfPageRect a, PdfPageRect b)
        => new[]
        {
            Math.Abs(a.X - b.X), Math.Abs(a.Right - b.Right), Math.Abs(a.Y - b.Y), Math.Abs(a.Y2 - b.Y2),
        }.Max();

    /// <summary>
    /// The harness's option hash: value U+001F label U+001E per option, in order, SHA-256. pdf.js
    /// writes the display text as an option's value when the save text is empty (pdf.worker
    /// <c>ChoiceList</c>: <c>values[i] || displayed[i]</c>; IMM's VisaType has an <c>xsi:nil</c>
    /// first save entry). That is pdf.js's HTML, not an XFA rule: the map keeps the empty save value
    /// (p758, p760), and the comparison applies pdf.js's substitution to it.
    /// </summary>
    private static string OptionsHash(IReadOnlyList<XfaItem> items)
    {
        var joined = new StringBuilder();
        foreach (var item in items)
            joined.Append(item.Save.Length > 0 ? item.Save : item.Display).Append('\u001f').Append(item.Display).Append('\u001e');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString())));
    }
}
