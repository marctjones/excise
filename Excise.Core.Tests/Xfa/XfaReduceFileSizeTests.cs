using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.Core.Writing;
using Excise.Core.Xfa;
using Excise.TestSupport;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #2038: Reduce File Size's "remove private application data" removes OTHER applications'
/// <c>/PieceInfo</c> (ISO 32000-2 §14.5), never excise's own <c>/PieceInfo /Excise</c> page record. On a
/// dynamic XFA form excise laid out, that record is the decision-4 marker (without it the optimized copy
/// is laid out a second time on reopen) and the generated-widget list (without it decision 17's flatten
/// finds nothing, and a redaction of the optimized copy leaves the hidden widgets' <c>/V</c>). qpdf and
/// mutool read the redaction case in Excise.Rendering.Tests (XfaGeneratedWidgetOracleTests).
/// </summary>
public sealed class XfaReduceFileSizeTests : IDisposable
{
    private const string Secret = "Quillfeather";
    private const string OtherAppSecret = "Illustratorsketch";

    // FullName's box: x 1in..5in, y 1in..1.4in from the top of the content area (inset 0.25in).
    private static readonly PdfRectangle FullNameBox = new(90, 792 - 120, 380, 792 - 88);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-2038-{Guid.NewGuid():N}");

    public XfaReduceFileSizeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// FullName shows the secret; Shadow, hidden, binds the same data node, so its generated widget is a
    /// hidden zero-size copy of the value. With <paramref name="otherApps"/>, the first page and the
    /// catalog also carry another producer's <c>/PieceInfo</c> entry holding <see cref="OtherAppSecret"/>.
    /// </summary>
    private static PdfDocument LaidOutHiddenCopyForm(bool otherApps = false)
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.Template(
                "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>"
                + "<field name=\"Shadow\" presence=\"hidden\" x=\"1in\" y=\"3in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui>"
                + "<bind match=\"dataRef\" ref=\"$record.FullName\"/></field>"
                + "<field name=\"City\" x=\"1in\" y=\"5in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>",
                layout: "position"),
            XfaTestForms.Data($"<FullName>Jane {Secret}</FullName><City>Springfield</City>")));
        var result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = true }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        document.GetAcroForm()!.FindField("form1[0].Shadow[0]")!.Value.Should().Be($"Jane {Secret}",
            "fixture sanity: the hidden generated field holds the value (K.2)");

        if (otherApps)
        {
            var page = document.GetPage(1);
            var pieceInfo = (PdfDictionary)document.Resolve(page.Dictionary.GetOptional("PieceInfo")!);
            pieceInfo["Illustrator"] = OtherAppData();
            page.Dictionary["PieceInfo"] = pieceInfo;
            var catalogPieceInfo = new PdfDictionary();
            catalogPieceInfo["Illustrator"] = OtherAppData();
            document.Catalog["PieceInfo"] = catalogPieceInfo;
        }
        return document;
    }

    private static PdfDictionary OtherAppData()
    {
        var privateData = new PdfDictionary();
        privateData.SetString("Draft", OtherAppSecret);
        var data = new PdfDictionary();
        data.SetString("LastModified", "D:20260917000000Z");
        data["Private"] = privateData;
        return data;
    }

    /// <summary>What the GUI and <c>excise optimize</c> do: an ordinary save, then the optimizer on a copy of it.</summary>
    private byte[] ReduceFileSize(PdfDocument document)
    {
        var path = Path.Combine(_dir, $"optimized-{Guid.NewGuid():N}.pdf");
        PdfDocumentOptimizer.SaveOptimizedCopy(document.SaveToBytes(), path,
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Lossless), cancellationToken: TestContext.Current.CancellationToken);
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void OptimizedCopy_KeepsTheLayoutMarker_AndIsNotLaidOutASecondTime()
    {
        using var laidOut = LaidOutHiddenCopyForm();
        var fieldsBefore = laidOut.GetAcroForm()!.Fields.Select(f => f.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var pagesBefore = laidOut.PageCount;

        using var reopened = PdfDocument.Open(ReduceFileSize(laidOut));

        reopened.HasXfaLayoutPages().Should().BeTrue("decision 4: the optimizer keeps excise's own marker");
        reopened.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken).Status
            .Should().Be(XfaLayoutStatus.AlreadyLaidOut, "a laid-out copy is never laid out again");
        reopened.PageCount.Should().Be(pagesBefore);
        reopened.GetAcroForm()!.Fields.Select(f => f.FullName).OrderBy(n => n, StringComparer.Ordinal).Should().Equal(fieldsBefore,
            "no second set of same-named generated fields");
        reopened.Pages.Sum(p => XfaWidgetWriter.GeneratedWidgets(reopened, p).Count)
            .Should().Be(laidOut.Pages.Sum(p => XfaWidgetWriter.GeneratedWidgets(laidOut, p).Count),
                "the generated-widget record survives with every widget it lists");
    }

    [Fact]
    public void OptimizedCopy_ThenAreaRedaction_FlattensTheHiddenCopy_AndLeavesNoValue()
    {
        using var laidOut = LaidOutHiddenCopyForm();
        var optimized = ReduceFileSize(laidOut);
        SavedPdfLeakScanner.FindTerm(optimized, Secret).Should().NotBeEmpty(
            "positive control: the optimized copy still holds the value in the generated widgets");

        using var document = PdfDocument.Open(optimized);
        var page = document.GetPage(1);
        _ = page.Letters;   // a page read before the redaction, as the GUI does
        var report = page.RedactAreaWithReport(FullNameBox, RedactionOptions.Default with { DrawBox = false });
        var saved = document.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "decision 17's flatten found the generated widgets through the record the optimizer kept, so the hidden widget's /V went too");
        report.Carriers.Should().Contain(c => c.Carrier.StartsWith(PdfXfaLayout.GeneratedFieldsFlattenedRow, StringComparison.Ordinal) && c.Scrubbed);
        (document.GetAcroForm()?.Fields ?? Array.Empty<PdfField>()).Should().BeEmpty();
        System.Text.Encoding.Latin1.GetString(saved).Should().NotContain("/PieceInfo",
            "the redaction's own /PieceInfo strip still removes the record afterwards");
    }

    [Fact]
    public void OptimizedCopy_RemovesOtherApplicationsPieceInfo_AndKeepsOnlyTheExciseEntry()
    {
        using var laidOut = LaidOutHiddenCopyForm(otherApps: true);
        var before = laidOut.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(before, OtherAppSecret).Should().NotBeEmpty("positive control: the plant is in the file");

        var path = Path.Combine(_dir, "other-apps.pdf");
        var result = PdfDocumentOptimizer.SaveOptimizedCopy(before, path,
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Lossless), cancellationToken: TestContext.Current.CancellationToken);
        var optimized = File.ReadAllBytes(path);

        result.PrivateDataEntriesRemoved.Should().Be(2, "the catalog's /PieceInfo and the page's Illustrator entry");
        SavedPdfLeakScanner.FindTerm(optimized, OtherAppSecret).Should().BeEmpty("another producer's private data is still removed");
        using var reopened = PdfDocument.Open(optimized);
        reopened.Catalog.ContainsKey("PieceInfo").Should().BeFalse();
        var pieceInfo = (PdfDictionary)reopened.Resolve(reopened.GetPage(1).Dictionary.GetOptional("PieceInfo")!);
        pieceInfo.Keys.Select(k => k.Value).Should().Equal(new[] { "Excise" }, "only excise's own entry is kept");
        reopened.HasXfaLayoutPages().Should().BeTrue();
        reopened.GetPage(1).Dictionary.ContainsKey("LastModified").Should().BeTrue("§14.5: a page with /PieceInfo keeps /LastModified");
    }

    /// <summary>
    /// The kept entry is the record excise writes and nothing else: an unknown key under
    /// <c>/Excise /Private</c> (another tool writing into excise's slot) is removed, the record's own
    /// keys are kept, and none of them is user text (the datasets hash is a digest of a packet the
    /// optimized copy keeps in <c>/XFA</c> anyway).
    /// </summary>
    [Fact]
    public void OptimizedCopy_KeepsOnlyTheKeysExciseWrites_UnderItsOwnEntry()
    {
        using var laidOut = LaidOutHiddenCopyForm();
        var page = laidOut.GetPage(1);
        var record = XfaWidgetWriter.Record(laidOut, page)!;
        // What the layout wrote: a key added there and not to the optimizer's list would be dropped silently.
        var written = record.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal).ToList();
        record.SetString("Notes", OtherAppSecret);
        page.Dictionary["PieceInfo"] = page.Dictionary.GetOptional("PieceInfo")!;
        var excise = (PdfDictionary)laidOut.Resolve(((PdfDictionary)laidOut.Resolve(page.Dictionary.GetOptional("PieceInfo")!)).GetOptional("Excise")!);
        excise.SetString("Notes", OtherAppSecret);

        var optimized = ReduceFileSize(laidOut);

        SavedPdfLeakScanner.FindTerm(optimized, OtherAppSecret).Should().BeEmpty();
        using var reopened = PdfDocument.Open(optimized);
        var kept = XfaWidgetWriter.Record(reopened, reopened.GetPage(1))!;
        kept.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal).Should().Equal(
            new[] { "XfaDatasetsHash", "XfaLayout", "XfaLayoutEngine", "XfaPageOrdinal", "XfaWidgets" });
        kept.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal).Should().Equal(written,
            "every key the layout writes is kept");
        kept.GetStringOrNull("XfaDatasetsHash").Should().StartWith("sha256:").And.NotContain(Secret);
        var keptExcise = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(reopened.GetPage(1).Dictionary.GetOptional("PieceInfo")!)).GetOptional("Excise")!);
        keptExcise.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal).Should().Equal(new[] { "LastModified", "Private" });
    }
}
