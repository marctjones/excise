using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;
using Excise.TestSupport;

namespace Excise.Core.Tests.Text;

/// <summary>
/// OCG-aware text extraction for the carriers the earlier name-based check did
/// not resolve: Optional Content Membership Dictionaries (OCMD) with a <c>/P</c>
/// visibility policy or a <c>/VE</c> And/Or/Not visibility expression, and OCGs
/// governed by <c>/BaseState /OFF</c>. Content inside a default-hidden layer must
/// be flagged <see cref="Excise.Core.Text.Letter.IsInHiddenOptionalContent"/> so
/// security redaction can find it. See issue #336.
///
/// Ground truth is the hand-authored fixture (structure known by construction),
/// not excise's own extractor — and redaction removal is checked against the
/// SAVED BYTES (ASCII + UTF-16BE), never re-extraction. See CLAUDE.md no-self-oracle.
/// </summary>
public sealed class OcgMembershipExtractionTests
{
    private static string HiddenText(byte[] pdf, string secret)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        // Sanity: the hidden text is still extracted (default output unchanged) —
        // only the flag differs.
        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain(secret);
        return string.Concat(page.Letters.Where(l => l.IsInHiddenOptionalContent).Select(l => l.Value));
    }

    [Fact]
    public void Ocmd_AnyOnPolicy_OverOffLayer_IsHidden()
    {
        // OCMD whose single member OCG is OFF; default policy AnyOn -> hidden.
        var pdf = BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>",
            propertyObjectNumber: 7,
            secret: "SECRETA",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
                "<< /Type /OCMD /OCGs [6 0 R] /P /AnyOn >>",            // 7
            });

        HiddenText(pdf, "SECRETA").Should().Be("SECRETA");
    }

    [Fact]
    public void Ocmd_AllOffPolicy_AllMembersOff_IsHidden()
    {
        // Policy AllOff makes content visible when all member OCGs are off.
        // Both members are off here, so the span is visible -> NOT flagged
        // hidden. Pins the AllOff direction (opposite of AnyOn above).
        var pdf = BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R 8 0 R] /D << /OFF [6 0 R 8 0 R] >> >>",
            propertyObjectNumber: 7,
            secret: "SHOWNAO",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
                "<< /Type /OCMD /OCGs [6 0 R 8 0 R] /P /AllOff >>",     // 7
                "<< /Type /OCG /Name (Beta) >>",                        // 8
            });

        HiddenText(pdf, "SHOWNAO").Should().BeEmpty();
    }

    [Fact]
    public void Ocmd_VisibilityExpression_Not_OverOffLayer_IsVisible()
    {
        // /VE [/Not <off ocg>] : Not(hidden) -> visible, so NOT flagged hidden.
        var pdf = BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>",
            propertyObjectNumber: 7,
            secret: "SHOWNVE",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
                "<< /Type /OCMD /VE [/Not 6 0 R] >>",                   // 7
            });

        HiddenText(pdf, "SHOWNVE").Should().BeEmpty();
    }

    [Fact]
    public void Ocmd_VisibilityExpression_And_OffAndOn_IsHidden()
    {
        // /VE [/And <off> <on>] : And(false, true) = false -> hidden.
        var pdf = BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R 8 0 R] /D << /OFF [6 0 R] >> >>",
            propertyObjectNumber: 7,
            secret: "SECRAND",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
                "<< /Type /OCMD /VE [/And 6 0 R 8 0 R] >>",            // 7
                "<< /Type /OCG /Name (Beta) >>",                        // 8
            });

        HiddenText(pdf, "SECRAND").Should().Be("SECRAND");
    }

    [Fact]
    public void Ocmd_VisibilityExpression_Or_OffAndOn_IsVisible()
    {
        // /VE [/Or <off> <on>] : Or(false, true) = true -> visible.
        var pdf = BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R 8 0 R] /D << /OFF [6 0 R] >> >>",
            propertyObjectNumber: 7,
            secret: "SHOWNOR",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
                "<< /Type /OCMD /VE [/Or 6 0 R 8 0 R] >>",             // 7
                "<< /Type /OCG /Name (Beta) >>",                        // 8
            });

        HiddenText(pdf, "SHOWNOR").Should().BeEmpty();
    }

    [Fact]
    public void Ocg_BaseStateOff_UnlistedLayer_IsHidden()
    {
        // BaseState OFF and the OCG is in neither ON nor OFF -> hidden.
        // The old name-based check only consulted the /OFF array and missed this.
        var pdf = BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R] /D << /BaseState /OFF >> >>",
            propertyObjectNumber: 6,
            secret: "BASEOFF",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
            });

        HiddenText(pdf, "BASEOFF").Should().Be("BASEOFF");
    }

    [Fact]
    public void RedactText_RemovesOcmdHiddenText_FromSavedBytes()
    {
        byte[] Fixture() => BuildOcPdf(
            ocProperties: "<< /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>",
            propertyObjectNumber: 7,
            secret: "SECRETA",
            extraObjects: new[]
            {
                "<< /Type /OCG /Name (Alpha) >>",                       // 6
                "<< /Type /OCMD /OCGs [6 0 R] /P /AnyOn >>",            // 7
            });

        // Default (includeHiddenLayers: true): security redaction reaches the
        // OCMD-hidden layer and the secret is gone from every carrier.
        using (var included = PdfDocument.Open(Fixture()))
        {
            // The OCMD layer is the last thing on the page, so its span is removed as a hidden
            // optional-content span before the text search runs; this used to read
            // VerifiedRemovals == 1 only because the writer copied the removed tail back.
            var report = included.RedactText("SECRETA", RedactionOptions.Default);
            report.Removals.Should().ContainSingle(r =>
                r.Feature == "hidden optional-content span(s)" && r.Count == 1);
            report.Survived.Should().Be(0);
            var saved = included.SaveToBytes();
            SavedPdfLeakScanner.FindTerm(saved, "SECRETA").Should().BeEmpty(
                "security redaction must remove text hidden in OCMD-governed optional content");
            SavedPdfLeakScanner.AllCarriersText(saved).Should().Contain("VISIBLE");
        }

        // Opt-out: caller excludes hidden layers -> no match, text retained.
        using (var excluded = PdfDocument.Open(Fixture()))
        {
            excluded.RedactText("SECRETA", RedactionOptions.Default with { IncludeHiddenLayers = false }).VerifiedRemovals.Should().Be(0);
            Encoding.ASCII.GetString(excluded.SaveToBytes()).Should().Contain("SECRETA");
        }
    }

    [Fact]
    public void UnclosedHiddenSpanInFormXObject_DoesNotHideThePageTextAfterDo()
    {
        // #1894: the form opens a hidden /OC span and never closes it. §14.6 confines a
        // marked-content sequence to one content stream, so the span ends with the form;
        // the page text after the Do is visible. Carried onto the page, it flagged the
        // visible secret hidden, and a visible-only redaction skipped it.
        var pdf = BuildFormOcPdf(
            pageContent: "/Fx Do\nBT /F1 12 Tf 100 700 Td (PAGESECRET) Tj ET",
            formContent: "/OC /MC0 BDC\nBT /F1 12 Tf 100 600 Td (FORMJUNK) Tj ET");

        using (var doc = PdfDocument.Open(pdf))
        {
            var letters = doc.GetPage(1).Letters;
            string.Concat(letters.Where(l => l.IsInHiddenOptionalContent).Select(l => l.Value))
                .Should().Be("FORMJUNK");
        }

        using var redacted = PdfDocument.Open(pdf);
        redacted.RedactText("PAGESECRET", RedactionOptions.Default with { IncludeHiddenLayers = false });
        var saved = redacted.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "PAGESECRET").Should().BeEmpty(
            "text the page paints visibly must be redacted when hidden layers are excluded");
        if (MutoolTextOracle.IsAvailable)
            MutoolTextOracle.ExtractAllPages(saved).Should().NotContain("PAGESECRET");
    }

    [Fact]
    public void StrayEmcInFormXObject_LeavesThePageTextAfterDoVisible_AndKeepsItsMcid()
    {
        // #1894: the form's first operator is an EMC it has no BDC for. §14.6 (and Poppler)
        // keep the page's hidden /OC span open, but MuPDF and Ghostscript close it and paint
        // everything after it. A viewer paints the text, so it is not hidden, and a
        // visible-only redaction must reach it. The tagged span around it keeps its /MCID.
        var pdf = BuildFormOcPdf(
            pageContent: "/P <</MCID 3>> BDC\n/OC /MC0 BDC\n/Fx Do\n" +
                         "BT /F1 12 Tf 100 700 Td (HIDDENSECRET) Tj ET\nEMC\nEMC\n" +
                         "BT /F1 12 Tf 100 650 Td (VISIBLE) Tj ET",
            formContent: "EMC\nBT /F1 12 Tf 100 600 Td (FORMTEXT) Tj ET");

        using (var doc = PdfDocument.Open(pdf))
        {
            var letters = doc.GetPage(1).Letters;
            letters.Where(l => l.IsInHiddenOptionalContent).Should().BeEmpty();
            string.Concat(letters.Where(l => l.MarkedContentId == 3).Select(l => l.Value))
                .Should().Be("FORMTEXTHIDDENSECRET");
        }

        using var redacted = PdfDocument.Open(pdf);
        redacted.RedactText("HIDDENSECRET", RedactionOptions.Default with { IncludeHiddenLayers = false });
        var saved = redacted.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "HIDDENSECRET").Should().BeEmpty(
            "text a viewer paints must be redacted when hidden layers are excluded");
        if (MutoolTextOracle.IsAvailable)
            MutoolTextOracle.ExtractAllPages(saved).Should().NotContain("HIDDENSECRET");
    }

    // A page that invokes form XObject /Fx; both streams can reach the default-OFF OCG
    // through /MC0.
    private static byte[] BuildFormOcPdf(string pageContent, string formContent) => Build(
        "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /XObject << /Fx 7 0 R >> /Properties << /MC0 6 0 R >> >> >>",
        Stream(pageContent),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /OCG /Name (Alpha) >>",
        Stream(formContent, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 6 0 R >> >> "));

    // Builds a one-page PDF whose content shows VISIBLE text, then a marked-content
    // /OC span (referencing the property object) containing the secret.
    private static byte[] BuildOcPdf(
        string ocProperties,
        int propertyObjectNumber,
        string secret,
        string[] extraObjects)
    {
        var content =
            "BT /F1 12 Tf 100 720 Td (VISIBLE) Tj ET\n" +
            "/OC /MC0 BDC\n" +
            $"BT /F1 12 Tf 100 690 Td ({secret}) Tj ET\n" +
            "EMC";

        var bodies = new System.Collections.Generic.List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /OCProperties {ocProperties} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> " +
                $"/Properties << /MC0 {propertyObjectNumber} 0 R >> >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        bodies.AddRange(extraObjects);

        return Build(bodies.ToArray());
    }

    private static string Stream(string content, string entries = "")
    {
        var length = Encoding.Latin1.GetBytes(content).Length;
        return $"<< {entries}/Length {length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
