using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2039: extraction parity on DYNAMIC XFA forms laid out by excise, graded against mutool by
/// <c>scripts/check-extraction-parity.sh --xfa</c> (floors in
/// <c>tests/extraction-parity/xfa-baseline.json</c>). The smoke corpus the main parity report
/// measures has no dynamic form, so the generated widgets of XFA Phase 3 (#2028), whose
/// appearances now draw every field value, were measured by nothing that is not excise.
///
/// <para>Each form is laid out with widgets, SAVED and REOPENED, so excise and mutool read the same
/// bytes; mutool paints the appearances as written (excise never sets <c>/NeedAppearances</c> on a
/// laid-out form). The corpus forms are unfilled, so a synthetic FILLED form is measured with them:
/// a drop-down whose save value differs from its display text, a comb, a multiline and a plain text
/// field. Without it a regression that stopped reading the widgets' drawn text would move no
/// number.</para>
/// </summary>
public sealed class XfaExtractionParityTests
{
    private readonly ITestOutputHelper _output;

    public XfaExtractionParityTests(ITestOutputHelper output) => _output = output;

    /// <summary>The four real forms and the pdfium/pdf.js files Phase 2 lays out (the XFA oracle set).</summary>
    public static readonly string[] Forms =
    {
        "xfa-real/imm5257e", "xfa-real/imm1295e", "xfa-real/ohio-expense-report", "xfa-real/hsbc-cloture-compte",
        "pdfium/simple_xfa", "pdfium/bug_504416752", "pdfium/bug_1058653", "pdfium/bug_1055869", "pdfium/bug_306123",
        "pdfium/bug_1301", "pdfium/xfa/xfa_break_before_after",
        "pdfium/xfa/email_recommended", "pdfium/xfa/xfa_combobox", "pdfium/xfa/xfa_date_time_edit",
        "pdfium/xfa/xfa_multiline_textfield", "pdfium/xfa/xfa_image_edit",
        "pdfium/pixel/xfa_specific/barcode_test", "pdfium/pixel/xfa_specific/dynamic_list_box_allow_multiple_selection",
        "pdfium/pixel/xfa_specific/dynamic_password_field_background_fill",
        "pdfium/pixel/xfa_specific/dynamic_table_color_and_width", "pdfium/pixel/xfa_specific/resolve_nodes_0",
        "pdfium/javascript/xfa_specific/resolve_nodes_1", "pdfium/javascript/xfa_specific/resolve_nodes_2",
        "pdfium/pixel/xfa_specific/xfa_node_caption", "pdfjs/issue14130",
    };

    /// <summary>The synthetic filled form: every value is drawn by a generated widget.</summary>
    internal static byte[] FilledForm()
    {
        static string Field(string name, string y, string ui, string extra = "") =>
            $"<field name=\"{name}\" x=\"1in\" y=\"{y}\" w=\"5in\" h=\"0.4in\"><ui>{ui}</ui>{extra}</field>";
        var template = XfaTestForms.Template(
            "<subform name=\"S\" x=\"0in\" y=\"0in\" w=\"8in\" h=\"10in\" layout=\"position\">"
            + Field("Name", "0.5in", "<textEdit/>")
            + Field("Country", "1.5in", "<choiceList/>",
                "<items><text>Saint Vincent and the Grenadines</text><text>Bosnia and Herzegovina</text></items>"
                + "<items save=\"1\"><text>VCT</text><text>BIH</text></items>")
            + Field("Code", "2.5in", "<textEdit><comb numberOfCells=\"8\"/></textEdit>", "<value><text maxChars=\"8\"/></value>")
            + "<field name=\"Notes\" x=\"1in\" y=\"3.5in\" w=\"5in\" h=\"1in\"><ui><textEdit multiLine=\"1\"/></ui></field>"
            + "</subform>",
            layout: "position");
        var data = XfaTestForms.Data(
            "<S><Name>Grace Brewster Murray Hopper</Name><Country>VCT</Country><Code>QX7R2M9K</Code>"
            + "<Notes>Rear admiral, computer scientist, compiler pioneer</Notes></S>");
        return XfaTestForms.BuildPdf(template, data);
    }

    [Fact]
    public void GenerateXfaExtractionParityReport()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH — install mupdf-tools to generate the XFA extraction parity report");
        var root = TestRepoLayout.MainCheckoutRoot;
        Assert.SkipWhen(root == null, "could not locate the main checkout");
        var imm = TestRepoLayout.FindFile("test-pdfs", "xfa-real", "imm5257e.pdf");
        Assert.SkipWhen(imm == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", "test-pdfs/xfa-real/imm5257e.pdf"));

        var sources = new List<(string Key, byte[] Bytes)>();
        var missing = new List<string>();
        foreach (var form in Forms)
        {
            var path = TestRepoLayout.FindFile(new[] { "test-pdfs" }.Concat((form + ".pdf").Split('/')).ToArray());
            if (path == null) { missing.Add($"test-pdfs/{form}.pdf"); continue; }
            sources.Add(($"test-pdfs/{form}.pdf", File.ReadAllBytes(path)));
        }
        sources.Add(("xfa-synthetic/filled.pdf", FilledForm()));

        var pages = new List<ExtractionParityTests.PageEntry>();
        var failures = new List<string>();
        var temp = new List<string>();
        try
        {
            foreach (var (key, bytes) in sources)
            {
                var saved = Path.Combine(Path.GetTempPath(), $"excise-xfa-parity-{Guid.NewGuid():N}.pdf");
                temp.Add(saved);
                using (var document = PdfDocument.Open(bytes))
                {
                    var result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = true },
                        TestContext.Current.CancellationToken);
                    if (result.Status != XfaLayoutStatus.LaidOut)
                    {
                        failures.Add($"{key}: {result.Status} {result.FailureReason}");
                        continue;
                    }
                    document.Save(saved);
                }
                using var reopened = PdfDocument.Open(saved);
                for (int page = 1; page <= reopened.PageCount; page++)
                    pages.Add(ExtractionParityTests.ScanPage(key, saved, reopened, page));
            }
        }
        finally
        {
            foreach (var path in temp)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        long excise = pages.Sum(p => (long)p.ExciseChars), mutool = pages.Sum(p => (long)p.MutoolChars);
        var report = new XfaParityReport
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            MutoolVersion = ExtractionParityTests.MutoolVersion(),
            Corpus = "dynamic XFA forms laid out with generated widgets, saved and reopened",
            PageCount = pages.Count,
            PdfCount = sources.Count,
            AggregateCoverage = mutool == 0 ? 1.0 : (double)excise / mutool,
            Pages = pages,
            Missing = missing,
            LayoutFailures = failures,
        };

        _output.WriteLine($"XFA extraction parity: {pages.Count} pages across {sources.Count} forms, aggregate {report.AggregateCoverage:P1}");
        foreach (var p in pages.Where(p => p.CoverageRatio < 0.95 || p.Similarity < 0.95))
            _output.WriteLine($"  {p.File} p{p.Page}: coverage={p.CoverageRatio:F3} similarity={p.Similarity:F3}");
        foreach (var m in missing) _output.WriteLine($"  MISSING {m}");
        foreach (var f in failures) _output.WriteLine($"  NOT LAID OUT {f}");

        var dir = ExtractionParityTests.ReportDirectory(root!);
        Directory.CreateDirectory(dir);
        var reportPath = Path.Combine(dir, "xfa-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        _output.WriteLine($"report: {reportPath}");

        pages.Should().NotBeEmpty();
    }

    public sealed class XfaParityReport
    {
        public string? GeneratedUtc { get; set; }
        public string MutoolVersion { get; set; } = "";
        public string Corpus { get; set; } = "";
        public int PageCount { get; set; }
        public int PdfCount { get; set; }
        public double AggregateCoverage { get; set; }
        public List<ExtractionParityTests.PageEntry> Pages { get; set; } = new();
        public List<string> Missing { get; set; } = new();
        public List<string> LayoutFailures { get; set; } = new();
    }
}
