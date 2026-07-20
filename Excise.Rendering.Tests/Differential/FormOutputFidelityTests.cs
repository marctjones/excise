using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Reference-rendered fidelity for saved form output (#611, parent #605).
///
/// Form workflow tests elsewhere prove filling, flattening, and save behavior
/// through excise's own reader. Per the no-self-oracle rule, this suite asks
/// tools that are NOT excise whether the saved files actually SHOW the values:
///
///   • INTERACTIVE saves carry no generated appearance streams — excise sets
///     /NeedAppearances true and the viewer synthesizes them. Tests against
///     mutool state that dependency in their names: they hold only for
///     viewers that honor /NeedAppearances (mutool does; a viewer that
///     ignores it shows blank fields — that is the documented trade-off).
///   • FLATTENED saves bake the value into page content — those must render
///     in ANY renderer and extract as plain page text, with the AcroForm
///     structure gone.
///
/// Fields are authored with excise's own AcroFormAuthoring, so the gate covers
/// the full author → fill → save/flatten → external-tool pipeline.
/// </summary>
public class FormOutputFidelityTests : IDisposable
{
    private const int Dpi = 150;
    private readonly List<string> _temp = new();

    private static readonly PdfRectangle NameBox = new(100, 600, 400, 630);
    private static readonly PdfRectangle CheckBox = new(100, 500, 130, 530);

    [Fact]
    public void FlattenedTextField_ValueIsBakedIntoPageContent()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var doc = AuthorForm();
        doc.GetAcroForm()!.FindField("applicant.name")!.SetValue("JANE EXAMPLE");
        doc.FlattenAcroForm();
        var path = SaveTemp(doc);

        // 1. Independent extractor: the value is page TEXT now, not a widget.
        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull();
        extracted!.Should().Contain("JANE EXAMPLE",
            "flattening must bake the field value into the content stream as real text");

        // 2. Independent renderer: ink where the field was.
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();
        InkFractionIn(rendered!, NameBox).Should().BeGreaterThan(0.01,
            "the baked value must be visible in the field's region");

        // 3. Structure: the AcroForm is gone (or empty) in the saved file.
        var reopened = PdfDocument.Open(File.ReadAllBytes(path));
        var form = reopened.GetAcroForm();
        (form == null || form.Fields.Count == 0).Should().BeTrue(
            "flattening must remove (or empty) the AcroForm — leftover fields would " +
            "let a viewer draw stale interactive values over the baked text");
    }

    [Fact]
    public void FlattenedTextField_RendersInGhostscriptToo()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var doc = AuthorForm();
        doc.GetAcroForm()!.FindField("applicant.name")!.SetValue("JANE EXAMPLE");
        doc.FlattenAcroForm();
        var path = SaveTemp(doc);

        using var rendered = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();
        InkFractionIn(rendered!, NameBox).Should().BeGreaterThan(0.01,
            "a flattened value is plain page content and must render in every renderer — " +
            "if mutool shows it and Ghostscript doesn't, the flattening leans on " +
            "renderer-specific behavior");
    }

    [Fact]
    public void FlattenedCheckbox_CheckmarkIsVisible()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Unchecked, flattened: capture the region's baseline ink (border only).
        var uncheckedDoc = AuthorForm();
        uncheckedDoc.FlattenAcroForm();
        var uncheckedPath = SaveTemp(uncheckedDoc);
        double inkUnchecked;
        using (var r = MutoolReferenceRenderer.RenderPage(uncheckedPath, 1, Dpi))
            inkUnchecked = InkFractionIn(r!, CheckBox);

        // Checked, flattened: the mark must add ink over that baseline.
        var checkedDoc = AuthorForm();
        checkedDoc.GetAcroForm()!.FindField("agree")!.SetValue("Yes");
        checkedDoc.FlattenAcroForm();
        var checkedPath = SaveTemp(checkedDoc);
        double inkChecked;
        using (var r = MutoolReferenceRenderer.RenderPage(checkedPath, 1, Dpi))
            inkChecked = InkFractionIn(r!, CheckBox);

        inkChecked.Should().BeGreaterThan(inkUnchecked + 0.01,
            $"the flattened checked box must draw a visible mark (ink {inkChecked:P2} vs " +
            $"unchecked {inkUnchecked:P2}) — equal ink means the checked state was lost in " +
            "flattening");
    }

    [Fact]
    public void InteractiveFilledTextField_IsShownByViewersThatHonorNeedAppearances()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var doc = AuthorForm();
        doc.GetAcroForm()!.FindField("applicant.name")!.SetValue("JANE EXAMPLE");
        var path = SaveTemp(doc);   // interactive save — NO flatten

        // excise does not generate appearance streams on fill; it sets
        // /NeedAppearances true. mutool synthesizes appearances in that case,
        // so the value must be visible. (A viewer that ignores
        // /NeedAppearances would show a blank field — the documented
        // trade-off of AP-less interactive saves.)
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();
        InkFractionIn(rendered!, NameBox).Should().BeGreaterThan(0.005,
            "mutool honors /NeedAppearances and must synthesize + draw the filled value; " +
            "blank means either /NeedAppearances was not written or the value is malformed");

        // And the value round-trips structurally in the saved bytes.
        var reopened = PdfDocument.Open(File.ReadAllBytes(path));
        reopened.GetAcroForm()!.FindField("applicant.name")!.Value.Should().Be("JANE EXAMPLE",
            "the interactive save must preserve the field value");
    }

    [Fact]
    public void FlattenedMultilineValue_StaysInsideItsFieldBox()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var box = new PdfRectangle(100, 300, 400, 420);
        var doc = NewBlankDoc();
        Excise.Core.Document.AcroFormAuthoring.AddTextField(doc, 1, box, "notes", multiline: true);
        doc.GetAcroForm()!.FindField("notes")!.SetValue(
            "LINEONE OF THE NOTE\nLINETWO OF THE NOTE\nLINETHREE OF THE NOTE");
        doc.FlattenAcroForm();
        var path = SaveTemp(doc);

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull();
        extracted!.Should().Contain("LINEONE").And.Contain("LINETWO").And.Contain("LINETHREE",
            "every line of a multiline value must survive flattening");

        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();
        InkFractionIn(rendered!, box).Should().BeGreaterThan(0.01, "the value must be visible");

        // The baked text must not escape the field box: the bands right of and
        // below the box stay blank.
        var rightStrip = new PdfRectangle(box.Right + 5, box.Bottom, box.Right + 100, box.Top);
        var belowStrip = new PdfRectangle(box.Left, box.Bottom - 60, box.Right, box.Bottom - 5);
        InkFractionIn(rendered!, rightStrip).Should().BeLessThan(0.002,
            "flattened text overflowing the field's right edge means the appearance ignores " +
            "the field geometry");
        InkFractionIn(rendered!, belowStrip).Should().BeLessThan(0.002,
            "flattened text below the field means line wrapping ignores the field height");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Blank page + a text field ("applicant.name") and a checkbox ("agree").</summary>
    private static PdfDocument AuthorForm()
    {
        var doc = NewBlankDoc();
        Excise.Core.Document.AcroFormAuthoring.AddTextField(doc, 1, NameBox, "applicant.name");
        Excise.Core.Document.AcroFormAuthoring.AddCheckBox(doc, 1, CheckBox, "agree");
        return doc;
    }

    private static PdfDocument NewBlankDoc() => PdfDocument.Open(BlankPdf());

    private string SaveTemp(PdfDocument pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-formfidelity-{Guid.NewGuid():N}.pdf");
        pdf.Save(path);
        _temp.Add(path);
        return path;
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
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

    private static byte[] BlankPdf()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
