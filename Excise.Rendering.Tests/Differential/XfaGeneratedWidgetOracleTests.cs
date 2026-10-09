using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Text.Segmentation;
using Excise.Core.Writing;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2028 (XFA Phase 3 S1): the AcroForm widgets the layout generates for a dynamic form, read by tools
/// that are not excise. mutool renders the pages with the values on the page (Phase 2,
/// <c>EmitWidgets = false</c>) and with the values in widgets; qpdf reads the field tree, the values,
/// the flags and the encryption; the redaction checks use the inflating byte scanner, mutool text,
/// mutool pixels and qpdf's decoded object dump. Each check has a planted failure that turns it red.
/// </summary>
public class XfaGeneratedWidgetOracleTests : IDisposable
{
    private const int Dpi = 72;

    private readonly ITestOutputHelper _out;
    private readonly List<string> _temp = new();

    public XfaGeneratedWidgetOracleTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>The four real forms and the pdfium/pdf.js files Phase 2 lays out.</summary>
    public static TheoryData<string> Forms => new()
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

    private static string CorpusScript(string form) => form.Split('/')[0] switch
    {
        "xfa-real" => "scripts/download-xfa-real-corpus.sh",
        "pdfium" => "scripts/download-pdfium-corpus.sh",
        _ => "scripts/download-pdfjs-corpus.sh",
    };

    private static byte[] Corpus(string form)
    {
        var source = TestRepoLayout.FindFile(new[] { "test-pdfs" }.Concat((form + ".pdf").Split('/')).ToArray());
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason($"corpus file ({CorpusScript(form)})", $"test-pdfs/{form}.pdf"));
        return File.ReadAllBytes(source!);
    }

    private static void RequireTools()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
    }

    private string TempPath(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-s1-{tag}-{Guid.NewGuid():N}.pdf");
        _temp.Add(path);
        return path;
    }

    private static PdfDocument LayOut(byte[] bytes, bool emitWidgets, out XfaLayoutResult result)
    {
        var document = PdfDocument.Open(bytes);
        result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = emitWidgets }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        return document;
    }

    private string Save(PdfDocument document, string tag, PdfEncryptionOptions? encryption = null)
    {
        var path = TempPath(tag);
        document.Save(path, encryption);
        return path;
    }

    /// <summary>Map entries that are AcroForm fields: exclusion-group members are their group's widgets.</summary>
    private static List<XfaFieldInfo> FieldEntries(XfaLayoutResult result)
        => result.Fields
            .Where(f => !(f.GroupSomPath != null && result.Fields.Any(g => g.UiKind == "exclGroup" && g.SomPath == f.GroupSomPath)))
            .ToList();

    // ================================================================ ink: mutool, before vs after

    private sealed record InkComparison(int Page, int OutsideDiff, int InsideDiff, int InsideInkBefore, int InsideInkAfter, int Widgets);

    /// <summary>
    /// Render every page of both files with mutool and compare: pixels outside every shown widget box
    /// (padded 2pt for anti-aliasing) that differ, and dark pixels inside the boxes in each file.
    /// </summary>
    private static List<InkComparison> CompareInk(string before, string after, IReadOnlyList<QpdfFieldWidget> widgets, int pages)
    {
        var result = new List<InkComparison>();
        for (int page = 1; page <= pages; page++)
        {
            using var a = MutoolReferenceRenderer.RenderPage(before, page, Dpi);
            using var b = MutoolReferenceRenderer.RenderPage(after, page, Dpi);
            a.Should().NotBeNull($"mutool renders page {page} before");
            b.Should().NotBeNull($"mutool renders page {page} after");
            a!.Width.Should().Be(b!.Width);
            a.Height.Should().Be(b.Height);

            var boxes = widgets
                .Where(w => w.Page == page && (w.AnnotationFlags & 2) == 0 && w.Rect.Length == 4)
                .Select(w => (X0: Math.Min(w.Rect[0], w.Rect[2]) - 2, Y0: Math.Min(w.Rect[1], w.Rect[3]) - 2,
                              X1: Math.Max(w.Rect[0], w.Rect[2]) + 2, Y1: Math.Max(w.Rect[1], w.Rect[3]) + 2))
                .ToList();
            double height = a.Height * 72.0 / Dpi;
            int outside = 0, insideDiff = 0, inkBefore = 0, inkAfter = 0;
            for (int y = 0; y < a.Height; y++)
            {
                double pdfY = height - (y + 0.5) * 72.0 / Dpi;
                for (int x = 0; x < a.Width; x++)
                {
                    double pdfX = (x + 0.5) * 72.0 / Dpi;
                    var p = a.GetPixel(x, y);
                    var q = b.GetPixel(x, y);
                    bool differs = Math.Abs(p.Red - q.Red) > 40 || Math.Abs(p.Green - q.Green) > 40 || Math.Abs(p.Blue - q.Blue) > 40;
                    bool inside = boxes.Any(r => pdfX >= r.X0 && pdfX <= r.X1 && pdfY >= r.Y0 && pdfY <= r.Y1);
                    if (!inside)
                    {
                        if (differs) outside++;
                        continue;
                    }
                    if (differs) insideDiff++;
                    if (Dark(p)) inkBefore++;
                    if (Dark(q)) inkAfter++;
                }
            }
            result.Add(new InkComparison(page, outside, insideDiff, inkBefore, inkAfter, boxes.Count));
        }
        return result;
    }

    private static bool Dark(SKColor c) => c.Red < 160 || c.Green < 160 || c.Blue < 160;

    [Theory]
    [MemberData(nameof(Forms))]
    public void Corpus_WidgetsChangeNoInkOutsideTheirBoxes_AndDrawTheValuesInside(string form)
    {
        RequireTools();
        var bytes = Corpus(form);
        using var phase2 = LayOut(bytes, emitWidgets: false, out _);
        using var s1 = LayOut(bytes, emitWidgets: true, out var result);
        var before = Save(phase2, "before");
        var after = Save(s1, "after");

        var widgets = QpdfReferenceTool.AcroFormWidgets(after);
        widgets.Should().NotBeNull("qpdf reads the saved field tree");
        var comparison = CompareInk(before, after, widgets!, s1.PageCount);
        foreach (var c in comparison)
            _out.WriteLine($"{form} p{c.Page}: widgets {c.Widgets}, outside diff {c.OutsideDiff}, inside diff {c.InsideDiff}, inside ink {c.InsideInkBefore} -> {c.InsideInkAfter}");

        comparison.Should().OnlyContain(c => c.OutsideDiff == 0,
            "moving values into widgets must not change a pixel outside the widget boxes (decision 10: borders, captions, chrome stay)");
        comparison.Sum(c => c.InsideInkAfter).Should().BeGreaterThanOrEqualTo((int)(comparison.Sum(c => c.InsideInkBefore) * 0.98),
            "the values the page drew are drawn by the widgets");
        comparison.Sum(c => c.InsideDiff).Should().BeLessThanOrEqualTo(Math.Max(4, comparison.Sum(c => c.InsideInkBefore) / 50),
            "the widget appearance is the drawing the page made (BBox = Rect, the same operators)");
        result.GeneratedFieldCount.Should().Be(FieldEntries(result).Count);
    }

    /// <summary>
    /// Extraction is redaction's reach (CLAUDE.md rule 5): every word excise's extractor read on a
    /// Phase 2 page it must still read once the value sits in a widget (the extractor emits a field's
    /// <c>/V</c> as letters in its rectangle), or search and term redaction lose it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Forms))]
    public void Corpus_ExciseStillReadsEveryWordItReadOnThePhase2Pages(string form)
    {
        var bytes = Corpus(form);
        using var phase2 = LayOut(bytes, emitWidgets: false, out _);
        using var s1 = LayOut(bytes, emitWidgets: true, out _);

        // Characters, not words: the field letters are emitted after the page's, so word joins and
        // reading order differ; a character the extractor no longer reads is what matters.
        var lost = new List<string>();
        for (int page = 1; page <= s1.PageCount; page++)
        {
            var after = Characters(new Excise.Core.Text.TextExtractor(s1.GetPage(page)).ExtractText(TestContext.Current.CancellationToken));
            var missing = new StringBuilder();
            foreach (var (ch, count) in Characters(new Excise.Core.Text.TextExtractor(phase2.GetPage(page)).ExtractText(TestContext.Current.CancellationToken)))
            {
                int now = after.TryGetValue(ch, out var n) ? n : 0;
                if (now < count)
                    missing.Append(ch, count - now);
            }
            if (missing.Length > 0)
                lost.Add($"p{page} '{missing}'");
        }
        _out.WriteLine($"{form}: characters lost: {string.Join(", ", lost)}");
        lost.Should().BeEmpty();
    }

    private static Dictionary<char, int> Characters(string text)
        => text.Where(c => !char.IsWhiteSpace(c))
            .GroupBy(c => c)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Planted failure for the ink check: widgets with their appearances dropped lose the values.</summary>
    [Fact]
    public void Planted_AppearancesDropped_TheInkCheckSeesTheValuesGone()
    {
        RequireTools();
        var bytes = XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate(), XfaTestForms.Data("<FullName>Planted Value Here</FullName>"));
        using var phase2 = LayOut(bytes, emitWidgets: false, out _);
        using var s1 = LayOut(bytes, emitWidgets: true, out _);
        foreach (var widget in XfaWidgetWriter.GeneratedWidgets(s1, s1.Pages[0]))
        {
            // The plant: the values drawn nowhere. Without /V too, or mutool synthesises an appearance.
            widget.Remove("AP");
            widget.Remove("V");
        }
        var before = Save(phase2, "plant-before");
        var after = Save(s1, "plant-after");

        var comparison = CompareInk(before, after, QpdfReferenceTool.AcroFormWidgets(after)!, 1);
        comparison[0].InsideInkBefore.Should().BeGreaterThan(50, "fixture sanity: the value has ink");
        comparison[0].InsideInkAfter.Should().BeLessThan((int)(comparison[0].InsideInkBefore * 0.98),
            "without appearances the check must see the values missing");
    }

    // ================================================================ qpdf: names, values, flags

    [Theory]
    [MemberData(nameof(Forms))]
    public void Corpus_QpdfReadsOneReadOnlyFieldPerXfaField_NamedBySom_WithTheXfaValue(string form)
    {
        RequireTools();
        var bytes = Corpus(form);
        using var document = LayOut(bytes, emitWidgets: true, out var result);
        var path = Save(document, "names");

        var check = QpdfReferenceTool.Check(path);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue(check.Value.Output);
        check.Value.Output.Should().NotContainEquivalentOf("warning", "qpdf --check is clean");

        var widgets = QpdfReferenceTool.AcroFormWidgets(path)!;
        var byName = widgets.GroupBy(w => w.FullName).ToDictionary(g => g.Key, g => g.ToList());
        var entries = FieldEntries(result);
        var failures = new List<string>();
        foreach (var entry in entries)
        {
            if (!byName.TryGetValue(entry.SomPath, out var found))
            {
                failures.Add($"no field {entry.SomPath} (K.2: a field for each XFA field)");
                continue;
            }
            var expected = ExpectedQpdfValue(entry);
            if (expected.Check && found[0].Value != expected.Value)
                failures.Add($"{entry.SomPath}: /V {found[0].Value ?? "(none)"}, XFA value {expected.Value ?? "(none)"}");
        }
        var generated = new HashSet<string>(entries.Select(e => e.SomPath), StringComparer.Ordinal);
        foreach (var widget in widgets.Where(w => generated.Contains(w.FullName)))
        {
            if ((widget.FieldFlags & 1) == 0)
                failures.Add($"{widget.FullName}: not read-only (S1 is display only)");
            if (widget.HasAction || widget.HasAdditionalActions)
                failures.Add($"{widget.FullName}: widget carries /A or /AA (K.2 rule 3)");
            if (widget.FullName.Split('.').Any(s => s.Length == 0))
                failures.Add($"{widget.FullName}: empty partial name");
        }
        int hidden = widgets.Count(w => generated.Contains(w.FullName) && (w.AnnotationFlags & 2) != 0 && w.Page == 1
                                         && w.Rect.All(n => n == 0));
        _out.WriteLine($"{form}: {entries.Count} fields, {widgets.Count} widgets, {hidden} hidden zero-size, {failures.Count} failures");
        failures.Should().BeEmpty();
        hidden.Should().Be(entries.Count(e => e.UiKind != "exclGroup" && (e.PageIndex < 0 || e.Rect is not { Width: > 0, Height: > 0 }))
                           + entries.Where(e => e.UiKind == "exclGroup").Sum(g => result.Fields.Count(m => m.GroupSomPath == g.SomPath
                               && m.UiKind == "checkButton" && (m.PageIndex < 0 || m.Rect is not { Width: > 0, Height: > 0 }))),
            "every field not drawn has one hidden zero-size widget on page 1, and the #2012 cut kept it");
    }

    /// <summary>
    /// What qpdf should read as <c>/V</c>: the datasets text for a bound value (else the form value),
    /// the on value as a name or <c>/Off</c> for buttons. No check where K.2's value has no PDF form
    /// here (password, push button, signature, multi-select array).
    /// </summary>
    private static (bool Check, string? Value) ExpectedQpdfValue(XfaFieldInfo entry)
    {
        string? text = entry.BoundData is { HasElements: false } data ? data.Value : entry.Value;
        return entry.UiKind switch
        {
            "passwordEdit" or "button" or "imageEdit" or "signature" => (false, null),
            "choiceList" when entry.MultiSelect => (false, null),
            "checkButton" => (true, entry.Value != null && entry.Value == entry.OnValue ? "/" + entry.OnValue : "/Off"),
            "exclGroup" => (true, string.IsNullOrEmpty(entry.Value) ? "/Off" : "/" + entry.Value),
            _ => (true, text),
        };
    }

    // ================================================================ saving: encryption, round trip, object streams

    [Fact]
    public void Imm5257e_EncryptedSave_KeepsTheEncryption_AndTheFields()
    {
        RequireTools();
        var bytes = Corpus("xfa-real/imm5257e");
        var source = TempPath("imm-source");
        File.WriteAllBytes(source, bytes);
        using var document = LayOut(bytes, emitWidgets: true, out var result);
        document.IsEncrypted.Should().BeTrue("fixture sanity: IMM 5257e is AES-128 with an empty user password");

        var path = Save(document, "imm-encrypted", document.GetReEncryptionOptions(string.Empty));

        QpdfReferenceTool.IsEncrypted(path).Should().BeTrue();
        var encryption = QpdfReferenceTool.ShowEncryption(path, string.Empty);
        encryption.Should().NotBeNull().And.Contain("AESv2", "the source's AES-128 survives");
        QpdfReferenceTool.Check(path, string.Empty)!.Value.Success.Should().BeTrue();
        var names = QpdfReferenceTool.AcroFormWidgets(path, string.Empty)!.Select(w => w.FullName).ToHashSet();
        names.Should().Contain(FieldEntries(result).Select(e => e.SomPath));
        Encoding.Latin1.GetString(File.ReadAllBytes(path)).Should().NotContain("form1[0].Page1[0]",
            "field names are encrypted strings in the saved file");
    }

    [Fact]
    public void Imm5257e_SaveAndReopen_KeepsEveryGeneratedField_AndDropsThePlaceholdersSignatureField()
    {
        RequireTools();
        var bytes = Corpus("xfa-real/imm5257e");
        var sourcePath = TempPath("imm-source");
        File.WriteAllBytes(sourcePath, bytes);
        var original = QpdfReferenceTool.AcroFormWidgets(sourcePath)!.Select(w => w.FullName).ToList();
        original.Should().NotBeEmpty("fixture sanity: the placeholder page carries the DocMDP signature field");

        using var document = LayOut(bytes, emitWidgets: true, out var result);
        var path = Save(document, "imm-roundtrip");

        var savedWidgets = QpdfReferenceTool.AcroFormWidgets(path)!;
        var saved = savedWidgets.Select(w => w.FullName).ToHashSet();
        saved.Should().BeEquivalentTo(FieldEntries(result).Select(e => e.SomPath).ToHashSet(),
            "every generated field, hidden ones included, survives the #2012 cut");
        int generatedWidgets = document.Pages.Sum(p => XfaWidgetWriter.GeneratedWidgets(document, p).Count);
        savedWidgets.Should().HaveCount(generatedWidgets,
            "the removed placeholder page's DocMDP signature widget is cut; only generated widgets remain");
        // Designer named the placeholder's signature field by the same SOM path the generator writes.
        _out.WriteLine($"placeholder fields: {string.Join(", ", original)}");
        saved.Should().Contain(original);

        using var reopened = PdfDocument.Open(File.ReadAllBytes(path));
        reopened.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken).Status
            .Should().Be(XfaLayoutStatus.AlreadyLaidOut, "decision 4: the record keeps the pages");
        reopened.GetAcroForm()!.Fields.Select(f => f.FullName).Distinct().Should().HaveCount(saved.Count);
        reopened.GetAcroForm()!.Fields.Should().OnlyContain(f => f.IsReadOnly);
    }

    [Fact]
    public void ObjectStreamOutput_AndTheReduceFileSizeCopy_KeepTheFields()
    {
        RequireTools();
        var bytes = Corpus("xfa-real/ohio-expense-report");
        using var document = LayOut(bytes, emitWidgets: true, out var result);
        var saved = document.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().Contain("/ObjStm", "fixture sanity: the ordinary save packs objects into object streams");
        var path = TempPath("ohio-objstm");
        File.WriteAllBytes(path, saved);

        var compact = TempPath("ohio-compact");
        PdfDocumentOptimizer.SaveOptimizedCopy(saved, compact, new PdfOptimizationOptions());

        var expected = FieldEntries(result).Select(e => e.SomPath).ToHashSet();
        foreach (var file in new[] { path, compact })
        {
            QpdfReferenceTool.Check(file)!.Value.Success.Should().BeTrue(file);
            QpdfReferenceTool.AcroFormWidgets(file)!.Select(w => w.FullName).ToHashSet().Should().BeEquivalentTo(expected, file);
        }
    }

    // ================================================================ redaction (decision 17, decision 5)

    private const string Secret = "Quillfeather";

    /// <summary>
    /// FullName holds the secret on the page; Shadow, hidden, binds the same data node (dataRef), so its
    /// generated widget is a hidden copy of the value an area redaction of FullName's box removes.
    /// </summary>
    private static byte[] HiddenCopyForm() => XfaTestForms.BuildPdf(
        XfaTestForms.Template(
            "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui>"
            + "<caption reserve=\"1in\"><value><text>Name</text></value></caption></field>"
            + "<field name=\"Shadow\" presence=\"hidden\" x=\"1in\" y=\"3in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui>"
            + "<bind match=\"dataRef\" ref=\"$record.FullName\"/></field>"
            + "<field name=\"City\" x=\"1in\" y=\"5in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>",
            layout: "position"),
        XfaTestForms.Data($"<FullName>Jane {Secret}</FullName><City>Springfield</City>"));

    // FullName's box: x 1in..5in, y 1in..1.4in from the top of the content area (inset 0.25in).
    private static readonly PdfRectangle FullNameBox = new(90, 792 - 120, 380, 792 - 88);

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        // 72 dpi: one pixel per point; the bitmap's height is the page height.
        int x0 = Math.Max(0, (int)Math.Floor(box.Left)), x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right));
        int y0 = Math.Max(0, (int)Math.Floor(bmp.Height - box.Top)), y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling(bmp.Height - box.Bottom));
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                total++;
                if (Dark(bmp.GetPixel(x, y))) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private void AssertNothingLeft(string path, string term)
    {
        var saved = File.ReadAllBytes(path);
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty("no carrier keeps the value, hidden widgets included");
        var dump = QpdfReferenceTool.DecodedObjectDump(path);
        dump.Should().NotBeNull();
        Encoding.Latin1.GetString(dump!).Should().NotContain(term,
            "qpdf's decoded dump of every object holds no stale appearance or /V with the value (#2034: a second oracle)");
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(term);
        QpdfReferenceTool.XfaPacketObjects(path).Should().BeNull("decision 5: no /XFA after a redaction");
    }

    [Fact]
    public void AreaRedaction_AfterTheFlatten_LeavesNoCopyOfTheValue_AndSaysSo()
    {
        RequireTools();
        using var document = LayOut(HiddenCopyForm(), emitWidgets: true, out _);
        document.GetAcroForm()!.FindField("form1[0].Shadow[0]")!.Value.Should().Be($"Jane {Secret}",
            "fixture sanity: the hidden field's widget holds the value (K.2)");

        // What App RedactionService.RedactArea does before the engine runs (decision 17).
        var row = PdfXfaLayout.FlattenGeneratedXfaFields(document);
        document.Pages[0].RedactArea(FullNameBox, RedactionOptions.Default with { DrawBox = false });
        var path = Save(document, "area");

        row.Should().StartWith("generated XFA fields flattened");
        document.RedactionLedger.XfaRemovals.Should().Contain(row).And.Contain(r => r.StartsWith("/XFA", StringComparison.Ordinal));
        AssertNothingLeft(path, Secret);
        using var raster = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(raster!, new PdfRectangle(FullNameBox.Left + 75, FullNameBox.Bottom + 2, FullNameBox.Right - 2, FullNameBox.Top - 2))
            .Should().BeLessThan(0.001, "the value's ink is gone from the box");
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("Springfield", "the field outside the box still shows");
        QpdfReferenceTool.AcroFormWidgets(path)!.Should().BeEmpty("the generated fields were baked into the page");
    }

    /// <summary>
    /// The flattened-copy paths (GUI Save Flattened Form Copy, <c>fill-form --flatten</c>) stamp the
    /// generated widgets from their own appearance before <c>FlattenAcroForm</c>: the copy looks
    /// exactly like the laid-out form and holds no hidden field's value. The planted run (the generic
    /// flatten alone) shows what the stamp prevents: the hidden value written as clipped page text.
    /// </summary>
    [Fact]
    public void FlattenedFormCopy_StampsGeneratedWidgets_AndKeepsHiddenValuesOut()
    {
        RequireTools();
        const string hiddenOnly = "Unseenvalue";
        var bytes = XfaTestForms.BuildPdf(
            XfaTestForms.Template(
                "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>"
                + "<field name=\"Unseen\" presence=\"hidden\" x=\"1in\" y=\"3in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>",
                layout: "position"),
            XfaTestForms.Data($"<FullName>Visible Person</FullName><Unseen>{hiddenOnly}</Unseen>"));
        using var laidOut = LayOut(bytes, emitWidgets: true, out _);
        var saved = laidOut.SaveToBytes();
        var before = TempPath("copy-before");
        File.WriteAllBytes(before, saved);

        // What SaveFlattenedFormCopyAsAsync and FormMutationHandler do.
        using (var copy = PdfDocument.Open(saved))
        {
            PdfXfaLayout.FlattenGeneratedXfaFields(copy, forRedaction: false).Should().NotBeNull();
            copy.RedactionLedger.XfaRemovals.Should().BeEmpty("a flattened copy is not a redaction");
            copy.FlattenAcroForm();
            var path = Save(copy, "copy-flattened");

            MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("Visible Person").And.NotContain(hiddenOnly);
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), hiddenOnly).Should().BeEmpty();
            CompareInk(before, path, Array.Empty<QpdfFieldWidget>(), 1)[0].OutsideDiff.Should().Be(0,
                "the copy draws each value from the widget's own appearance, where the widget drew it");
        }

        // The plant: the generic flatten alone redraws every field from /V.
        using var planted = PdfDocument.Open(saved);
        planted.FlattenAcroForm();
        var plantedPath = Save(planted, "copy-plant");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(plantedPath), hiddenOnly).Should().NotBeEmpty(
            "without the stamp the hidden field's value becomes clipped page text (mutool does not extract a glyph clipped to nothing; the scanner reads the stream), which the check must see");
    }

    /// <summary>Planted failure: the engine alone, without the orchestration flatten, ships the hidden copy.</summary>
    [Fact]
    public void Planted_AreaRedactionWithoutTheFlatten_LeavesTheHiddenWidgetsCopy()
    {
        RequireTools();
        using var document = LayOut(HiddenCopyForm(), emitWidgets: true, out _);
        document.Pages[0].RedactArea(FullNameBox, RedactionOptions.Default with { DrawBox = false });
        var path = Save(document, "area-plant");

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), Secret).Should().NotBeEmpty(
            "the check must catch the leak decision 17 exists for: the hidden widget's /V");
        Encoding.Latin1.GetString(QpdfReferenceTool.DecodedObjectDump(path)!).Should().Contain(Secret);
    }

    [Fact]
    public void TermRedaction_AfterTheFlatten_LeavesNoCopyOfTheValue()
    {
        RequireTools();
        using var document = LayOut(HiddenCopyForm(), emitWidgets: true, out _);

        PdfXfaLayout.FlattenGeneratedXfaFields(document);
        var report = document.RedactText(Secret, RedactionOptions.Default);
        var path = Save(document, "term");

        report.VerifiedRemovals.Should().BeGreaterThan(0);
        AssertNothingLeft(path, Secret);
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("Jane").And.Contain("Springfield");
    }

    /// <summary>
    /// Unfilled real forms: flattening keeps every pixel (the widgets drew exactly what the page now
    /// draws); an area redaction over a line of text mutool locates then removes its ink and text, and
    /// the saved file keeps no generated widget, no <c>/XFA</c> and no <c>/NeedsRendering</c>.
    /// </summary>
    [Theory]
    [InlineData("xfa-real/imm5257e", "RESIDENT VISA)", false)]
    [InlineData("xfa-real/ohio-expense-report", "2020 Annual Expense Report", false)]
    [InlineData("xfa-real/hsbc-cloture-compte", "ORDRE DE CLOTURE", false)]
    [InlineData("xfa-real/hsbc-cloture-compte", "30056", true)]   // a value a generated widget drew
    public void UnfilledForm_FlattenKeepsTheLook_ThenAreaRedactionRemovesTheLine(string form, string line, bool isFieldValue)
    {
        RequireTools();
        var bytes = Corpus(form);
        using var laidOut = LayOut(bytes, emitWidgets: true, out _);
        var before = Save(laidOut, "unfilled-before");
        // The same pages with every generated widget hidden: what the page draws without the values
        // (borders, captions, chrome). Area redaction of vector chrome is not what this slice checks.
        foreach (var page in laidOut.Pages)
        {
            foreach (var widget in XfaWidgetWriter.GeneratedWidgets(laidOut, page))
                widget.SetInt("F", 2);
        }
        var noValues = Save(laidOut, "unfilled-novalues");

        using var document = LayOut(bytes, emitWidgets: true, out _);
        PdfXfaLayout.FlattenGeneratedXfaFields(document).Should().NotBeNull();
        var flattened = Save(document, "unfilled-flattened");
        var comparison = CompareInk(before, flattened, Array.Empty<QpdfFieldWidget>(), document.PageCount);
        foreach (var c in comparison)
            _out.WriteLine($"{form} flatten p{c.Page}: changed pixels {c.OutsideDiff}");
        comparison.Should().OnlyContain(c => c.OutsideDiff == 0, "flattening stamps exactly the appearance each widget drew");

        var box = MutoolLineBox(flattened, 1, line);
        box.Should().NotBeNull($"fixture sanity: mutool finds '{line}' on page 1");
        var rect = box!.Value;
        // A caption is page content and goes with the box; under a field value, the field's own
        // border or comb lines (page chrome) may remain, so they are the floor.
        double chrome = 0;
        if (isFieldValue)
        {
            using var chromeRaster = MutoolReferenceRenderer.RenderPage(noValues, 1, Dpi);
            chrome = InkFractionIn(chromeRaster!, rect);
        }
        using (var flatRaster = MutoolReferenceRenderer.RenderPage(flattened, 1, Dpi))
            InkFractionIn(flatRaster!, rect).Should().BeGreaterThan(chrome + 0.02, "fixture sanity: the line's text has ink");
        _out.WriteLine($"{form} '{line}': chrome ink in the box {chrome:F4}");

        document.Pages[0].RedactArea(rect, RedactionOptions.Default with { DrawBox = false });
        var path = Save(document, "unfilled-redacted");

        using var raster = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(raster!, rect).Should().BeLessThan(chrome + 0.001, "the text's ink is gone; only page chrome under it may remain");
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(line);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), line).Should().BeEmpty("the template packet went with /XFA");
        QpdfReferenceTool.XfaPacketObjects(path).Should().BeNull("decision 5");
        QpdfReferenceTool.AcroFormWidgets(path)!.Should().BeEmpty("no generated field survives the flatten");
        var dump = Encoding.Latin1.GetString(QpdfReferenceTool.DecodedObjectDump(path)!);
        dump.Should().NotContain("/NeedsRendering").And.NotContain("/Widget",
            "no generated widget, reachable or not, is left in the file");
    }

    /// <summary>The bounding box (PDF points, bottom-left origin) of the first line mutool reads containing <paramref name="text"/>.</summary>
    private static PdfRectangle? MutoolLineBox(string pdf, int page, string text)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("mutool") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "draw", "-q", "-F", "stext", "-o", "-", pdf, page.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
            return null;
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        var xml = System.Xml.Linq.XDocument.Parse(stdout.GetAwaiter().GetResult());
        var pageElement = xml.Descendants("page").First();
        double height = double.Parse(pageElement.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        var found = xml.Descendants("line").FirstOrDefault(l => (l.Attribute("text")?.Value ?? string.Empty).Contains(text, StringComparison.Ordinal));
        if (found == null)
            return null;
        var b = found.Attribute("bbox")!.Value.Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // stext boxes are top-left; convert once to PDF's bottom-left space.
        return new PdfRectangle(b[0], height - b[3], b[2], height - b[1]);
    }
}
