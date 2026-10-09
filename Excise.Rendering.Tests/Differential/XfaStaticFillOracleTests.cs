using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using F = Excise.TestSupport.XfaStaticFillFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2013: a fill of a STATIC XFA form keeps the datasets packet consistent with
/// the AcroForm <c>/V</c> (ISO 32000-2 Annex K.2), judged on the SAVED file by
/// tools that are not excise: qpdf follows /Root → /AcroForm → /XFA, decodes
/// (and decrypts) the datasets stream and reads <c>/V</c>; mutool and Poppler
/// show the visible value; <see cref="SavedPdfLeakScanner"/> inflates every
/// stream with ZLibStream. Redact-after-fill proves decision 5 of #1547 still
/// holds for a filled form: the value survives in neither /V, the datasets,
/// nor drawn glyphs (mutool text, mutool pixels).
/// </summary>
public sealed class XfaStaticFillOracleTests : IDisposable
{
    private const string W9Name = "topmostSubform[0].Page1[0].f1_01[0]";
    private const string W9Box3 = "topmostSubform[0].Page1[0].Boxes3a-b_ReadOrder[0].c1_1[2]";

    /// <summary>The W-9 line 1 widget /Rect.</summary>
    private static readonly PdfRectangle W9NameRect = new(58.6, 659.967, 576.0, 673.967);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"xfa-static-fill-{Guid.NewGuid():N}");

    public XfaStaticFillOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] W9()
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "smoke", "irs-w9.pdf");
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("smoke corpus", "test-pdfs/smoke/irs-w9.pdf"));
        return File.ReadAllBytes(path!);
    }

    private static byte[] Fill(byte[] source, params (string Name, string? Value)[] values)
    {
        using var doc = PdfDocument.Open(source);
        foreach (var (name, value) in values)
            doc.GetAcroForm()!.FindField(name)!.SetValue(value);
        return doc.SaveToBytes();
    }

    /// <summary>The datasets record group, as qpdf finds and decodes it.</summary>
    private static XElement QpdfDataRoot(string path, string? password = null)
    {
        var packets = QpdfReferenceTool.XfaPacketObjects(path, password);
        packets.Should().NotBeNull("qpdf must find /AcroForm /XFA in the saved file");
        var key = packets!.ContainsKey("datasets") ? "datasets" : "xdp";
        var stream = QpdfReferenceTool.FilteredStreamData(path, packets[key], password);
        stream.IsOk.Should().BeTrue(stream.Diagnostics);
        return F.DataRoot(stream.Bytes);
    }

    private static void AssertQpdfCheckClean(string path, string? password = null)
    {
        var check = QpdfReferenceTool.Check(path, password);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue(check.Value.Output);
    }

    [Fact]
    public void IrsW9_FillForm_QpdfReadsTheSameValueInVAndDatasets()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        const string value = "Jane & <Doe> \"Q\" éè 日本";
        var path = Write(Fill(W9(), (W9Name, value), (W9Box3, "3")), "w9-filled.pdf");

        AssertQpdfCheckClean(path);
        var fields = QpdfReferenceTool.AcroFormFieldValues(path);
        fields.Should().NotBeNull();
        fields![W9Name].Should().Be(value);
        fields[W9Box3].Should().Be("/3");

        var data = QpdfDataRoot(path);
        data.Element("f1_01")!.Value.Should().Be(value, "qpdf's decode of the datasets must agree with /V");
        data.Element("c1_1")!.Value.Should().Be("3", "the checked box's on value (its XFA items entry)");
    }

    [Fact]
    public void IrsW9_FilledValue_IsVisibleToMutoolAndPoppler()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var path = Write(Fill(W9(), (W9Name, "ZQXVISIBLEVALUE")), "w9-visible.pdf");

        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("ZQXVISIBLEVALUE");
        if (PdftotextTextExtractor.IsAvailable)
            PdftotextTextExtractor.ExtractPage(path, 1).Should().Contain("ZQXVISIBLEVALUE");
    }

    [Fact]
    public void IrsW9_ChangeAtoB_ANowhereInTheFile_FullRewriteNoOldRevision()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        var first = Fill(W9(), (W9Name, "ALPHAFIRSTVALUE"));
        SavedPdfLeakScanner.FindTerm(first, "ALPHAFIRSTVALUE").Should().NotBeEmpty("fixture sanity");

        var second = Fill(first, (W9Name, "BETASECONDVALUE"));
        SavedPdfLeakScanner.FindTerm(second, "ALPHAFIRSTVALUE").Should().BeEmpty(
            "the old value must not survive in /V, the datasets or an earlier revision");
        Encoding.Latin1.GetString(second).Split("%%EOF").Length.Should().Be(2,
            "a full rewrite has exactly one %%EOF; an incremental append would keep the old revision");

        var path = Write(second, "w9-a-to-b.pdf");
        AssertQpdfCheckClean(path);
        QpdfDataRoot(path).Element("f1_01")!.Value.Should().Be("BETASECONDVALUE");
    }

    [Fact]
    public void IrsW9_Cleared_DatasetsNodeEmpty_ValueGone()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        var filled = Fill(W9(), (W9Name, "GAMMACLEARVALUE"));
        var cleared = Fill(filled, (W9Name, null));

        SavedPdfLeakScanner.FindTerm(cleared, "GAMMACLEARVALUE").Should().BeEmpty();
        var path = Write(cleared, "w9-cleared.pdf");
        QpdfReferenceTool.AcroFormFieldValues(path)![W9Name].Should().BeNull();
        QpdfDataRoot(path).Element("f1_01")!.Value.Should().BeEmpty();
    }

    [Fact]
    public void Encrypted_StaticForm_QpdfDecryptsTheUpdatedDatasets()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        const string password = "xfa-fill-oracle";
        var plainPath = Write(F.Build(), "static-plain.pdf");
        var encryptedPath = Path.Combine(_dir, "static-encrypted.pdf");
        QpdfReferenceTool.EncryptR4(plainPath, encryptedPath, password, password)
            .Should().BeTrue("qpdf encrypts the source, so excise must decrypt and re-encrypt it");

        byte[] saved;
        using (var doc = PdfDocument.Open(File.ReadAllBytes(encryptedPath), new PdfOpenOptions { UserPassword = password }))
        {
            doc.GetAcroForm()!.FindField(F.NamePath)!.SetValue("DELTAENCRYPTEDVALUE");
            saved = doc.SaveToBytes(doc.GetReEncryptionOptions(password));
        }

        SavedPdfLeakScanner.FindTerm(saved, "DELTAENCRYPTEDVALUE").Should().BeEmpty("the datasets stream is encrypted");
        var path = Write(saved, "static-encrypted-filled.pdf");
        QpdfReferenceTool.IsEncrypted(path).Should().BeTrue();
        AssertQpdfCheckClean(path, password);
        QpdfDataRoot(path, password).Element("Name")!.Value.Should().Be("DELTAENCRYPTEDVALUE");
        QpdfReferenceTool.AcroFormFieldValues(path, password)![F.NamePath].Should().Be("DELTAENCRYPTEDVALUE");
    }

    /// <summary>
    /// Decision 5 of #1547 for a FILLED static form: any redaction removes the
    /// XFA form, so the filled value must not survive in /V, in the datasets, or
    /// as drawn glyphs. Oracles: the leak scanner, mutool text, mutool pixels
    /// compared with the UNFILLED page (the box keeps its printed rule).
    /// </summary>
    [Fact]
    public void IrsW9_RedactAfterFill_ValueGoneFromVDatasetsAndPixels()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        const string secret = "ZQXREDACTSECRET";

        var source = W9();
        var filled = Fill(source, (W9Name, secret));
        var filledPath = Write(filled, "w9-to-redact.pdf");
        QpdfDataRoot(filledPath).Element("f1_01")!.Value.Should().Be(secret, "fixture sanity: the datasets carry it");
        MutoolTextExtractor.ExtractPage(filledPath, 1).Should().Contain(secret, "fixture sanity: it is drawn");

        byte[] redacted;
        using (var doc = PdfDocument.Open(filled))
        {
            doc.RedactText(secret, Excise.Core.Text.Segmentation.RedactionOptions.Default with { DrawBox = false });
            redacted = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(redacted, secret).Should().BeEmpty(
            "the filled value must leave /V, the datasets and every other carrier");
        var redactedPath = Write(redacted, "w9-redacted.pdf");
        QpdfReferenceTool.AcroFormFieldValues(redactedPath).Should().NotBeNull(
            "qpdf can read the redacted file, so a null below means no /XFA rather than a failed qpdf run");
        QpdfReferenceTool.XfaPacketObjects(redactedPath).Should().BeNull("any redaction removes the XFA form (decision 5)");
        MutoolTextExtractor.ExtractPage(redactedPath, 1).Should().NotContain(secret);

        var unfilledPath = Write(source, "w9-unfilled.pdf");
        using var unfilled = MutoolReferenceRenderer.RenderPage(unfilledPath, 1, dpi: 150);
        using var withSecret = MutoolReferenceRenderer.RenderPage(filledPath, 1, dpi: 150);
        using var after = MutoolReferenceRenderer.RenderPage(redactedPath, 1, dpi: 150);
        unfilled.Should().NotBeNull();
        withSecret.Should().NotBeNull();
        after.Should().NotBeNull();
        var baseline = InkFractionIn(unfilled!, W9NameRect);
        InkFractionIn(withSecret!, W9NameRect).Should().BeGreaterThan(baseline + 0.005,
            "fixture sanity: mutool draws the filled value in the line 1 box");
        InkFractionIn(after!, W9NameRect).Should().BeLessThan(baseline + 0.001,
            "after redaction the box carries no more ink than the unfilled form");
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = 150.0 / 72.0;
        const double pageHeight = 792;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }
}
