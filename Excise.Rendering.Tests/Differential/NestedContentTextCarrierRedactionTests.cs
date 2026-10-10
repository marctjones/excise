using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Text drawn INSIDE a content stream the page reaches without <c>Do</c>: a
/// tiling-pattern cell used as a fill colour, a soft-mask group reached
/// through <c>gs</c>, a Type3 glyph procedure. Extraction never makes those
/// glyphs page letters, so redaction can neither match nor remove them, and
/// before this it reported <c>IsCleanSuccess</c> over a saved file that still
/// spelled the term (and, for the pattern and the soft mask, still painted it).
/// The engine does not rewrite these streams; it must SAY it left them
/// (CLAUDE.md rules 5 and 6). Form XObjects nested three deep and annotation
/// appearances are walked and removed; those rows pin that.
/// </summary>
public class NestedContentTextCarrierRedactionTests : IDisposable
{
    private const int Dpi = 100;
    private const double PageHeight = 200;

    // Where every carrier fixture paints, and where the visible page copy sits.
    private static readonly PdfRectangle Drawn = new(20, 100, 320, 160);
    private static readonly PdfRectangle VisibleTerm = new(60, 36, 200, 54); // after "Name: "

    private readonly List<string> _temp = new();

    public static TheoryData<string, string, string> TextCarriers() => new()
    {
        { "tiling-pattern", "PATTERNSECRET", "tiling pattern" },
        { "smask-group", "SMASKSECRET", "soft-mask group" },
        { "type3-charproc", "TYPETHREESECRET", "Type3 glyph procedure" },
    };

    [Theory]
    [MemberData(nameof(TextCarriers))]
    public void RedactText_ReportsTheCarrierItCannotRewrite(string carrier, string term, string kind)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The term is also ordinary page text, so the redaction finds and
        // removes one occurrence: the case where a clean report is believed.
        var source = Build(carrier, term, visibleCopy: true);
        var saved = Redact(source, d => d.RedactText(term, RedactionOptions.Default with { DrawBox = false }), out var report);
        var path = WriteTemp(saved);

        report.MatchesLocated.Should().Be(1, "the page-text occurrence is found");
        using (var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi))
            InkFractionIn(after!, VisibleTerm).Should().BeLessThan(0.001, "the page-text occurrence is removed");

        SavedPdfLeakScanner.FindTerm(saved, term).Should().NotBeEmpty(
            $"the {kind} still holds the term; that is why the report must not call this clean");
        report.IsCleanSuccess.Should().BeFalse($"the {kind} drawing the term was left in place");
        report.Carriers.Should().Contain(c => c.RefusedReason != null && c.Carrier.Contains(kind) &&
                                              c.RefusedReason.Contains("containing the term"));
    }

    [Theory]
    [MemberData(nameof(TextCarriers))]
    public void RedactText_TermOnlyInsideTheCarrier_IsNotCalledClean(string carrier, string term, string kind)
    {
        var source = Build(carrier, term, visibleCopy: false);
        var saved = Redact(source, d => d.RedactText(term, RedactionOptions.Default), out var report);

        SavedPdfLeakScanner.FindTerm(saved, term).Should().NotBeEmpty();
        report.MatchesLocated.Should().Be(0, "extraction cannot read the carrier's text");
        report.IsCleanSuccess.Should().BeFalse($"\"0 removed\" over a {kind} that draws the term is not clean");
        report.Carriers.Should().Contain(c => c.RefusedReason != null && c.Carrier.Contains(kind));
    }

    [Theory]
    [MemberData(nameof(TextCarriers))]
    public void RedactArea_OverTheCarrier_ReportsItAndTheIndependentRendererAgrees(string carrier, string term, string kind)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Build(carrier, term, visibleCopy: false);
        double before;
        using (var bmp = MutoolReferenceRenderer.RenderPage(WriteTemp(source), 1, Dpi))
            before = InkFractionIn(bmp!, Drawn);
        before.Should().BeGreaterThan(0.02, "fixture sanity: the carrier paints in the area");

        var saved = Redact(source,
            d => d.Pages[0].RedactAreaWithReport(Drawn, RedactionOptions.Default with { DrawBox = false }), out var report);

        SavedPdfLeakScanner.FindTerm(saved, term).Should().NotBeEmpty($"the {kind} is kept in the saved file");
        report.IsCleanSuccess.Should().BeFalse($"the {kind}'s text was not examined");
        report.Carriers.Should().Contain(c => c.RefusedReason != null && c.Carrier.Contains(kind) &&
                                              c.RefusedReason.Contains("may lie in the redacted area"));

        using var after = MutoolReferenceRenderer.RenderPage(WriteTemp(saved), 1, Dpi);
        var inkAfter = InkFractionIn(after!, Drawn);
        if (carrier == "type3-charproc")
            inkAfter.Should().BeLessThan(0.001, "the Type3 glyph show is page content and is removed; its procedure, and the term, stay in the font");
        else
            inkAfter.Should().BeApproximately(before, 0.005, $"mutool still paints the {kind}: it was covered by nothing and removed by nothing");
    }

    public static TheoryData<string> TextFreeCarriers() => new() { "tiling-pattern", "smask-group", "type3-charproc" };

    /// <summary>
    /// A hatching pattern, a gradient-like mask and a TeX-style Type3 font
    /// whose glyphs are paths or bitmaps carry no text: redacting near them
    /// must stay clean, or every area redaction of a TeX PDF goes unclean.
    /// </summary>
    [Theory]
    [MemberData(nameof(TextFreeCarriers))]
    public void CarrierThatDrawsNoText_IsNotReported(string carrier)
    {
        var source = Build(carrier, term: null, visibleCopy: true, visibleTerm: "PLAINSECRET");

        var savedText = Redact(source, d => d.RedactText("PLAINSECRET", RedactionOptions.Default), out var textReport);
        SavedPdfLeakScanner.FindTerm(savedText, "PLAINSECRET").Should().BeEmpty();
        textReport.IsCleanSuccess.Should().BeTrue(textReport.ToString());

        Redact(source, d => d.Pages[0].RedactAreaWithReport(Drawn, RedactionOptions.Default), out var areaReport);
        areaReport.IsCleanSuccess.Should().BeTrue(areaReport.ToString());
        areaReport.Carriers.Should().NotContain(c => c.Carrier.StartsWith("text inside"));
    }

    [Fact]
    public void RedactText_PatternDrawingOtherText_IsNotReported()
    {
        // A "DRAFT" pattern says nothing about the term.
        var source = Build("tiling-pattern", "DRAFTMARK", visibleCopy: true, visibleTerm: "PLAINSECRET");

        var saved = Redact(source, d => d.RedactText("PLAINSECRET", RedactionOptions.Default), out var report);

        SavedPdfLeakScanner.FindTerm(saved, "PLAINSECRET").Should().BeEmpty();
        report.IsCleanSuccess.Should().BeTrue(report.ToString());
    }

    public static TheoryData<string, string> WalkedCarriers() => new()
    {
        { "form-nested-3", "NESTEDSECRET" },
        { "annotation-appearance", "ANNOTSECRET" },
    };

    [Theory]
    [MemberData(nameof(WalkedCarriers))]
    public void WalkedCarrier_TextIsRemovedByBothEntryPoints(string carrier, string term)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Build(carrier, term, visibleCopy: false);
        MutoolTextExtractor.ExtractPage(WriteTemp(source), 1).Should().Contain(term, "fixture sanity");

        var byTerm = Redact(source, d => d.RedactText(term, RedactionOptions.Default), out var termReport);
        SavedPdfLeakScanner.FindTerm(byTerm, term).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(WriteTemp(byTerm), 1).Should().NotContain(term);
        termReport.IsCleanSuccess.Should().BeTrue(termReport.ToString());

        var byArea = Redact(source,
            d => d.Pages[0].RedactAreaWithReport(Drawn, RedactionOptions.Default with { DrawBox = false }), out var areaReport);
        SavedPdfLeakScanner.FindTerm(byArea, term).Should().BeEmpty();
        using var after = MutoolReferenceRenderer.RenderPage(WriteTemp(byArea), 1, Dpi);
        InkFractionIn(after!, Drawn).Should().BeLessThan(0.001);
        areaReport.IsCleanSuccess.Should().BeTrue(areaReport.ToString());
    }

    private static byte[] Redact(byte[] source, Func<PdfDocument, RedactionReport> redact, out RedactionReport report)
    {
        using var doc = PdfDocument.Open(source);
        report = redact(doc);
        return doc.SaveToBytes();
    }

    // ── Fixtures ────────────────────────────────────────────────────────
    // 1 catalog, 2 pages, 3 page, 4 page content, 5 Helvetica, 6.. carrier.
    // A null term builds the carrier with no text: a path, or an inline image.

    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static byte[] Build(string carrier, string? term, bool visibleCopy, string? visibleTerm = null)
    {
        var visible = visibleCopy ? $"BT /F1 12 Tf 20 40 Td (Name: {visibleTerm ?? term}) Tj ET\n" : "";
        var cellText = term == null ? null : $"BT /F1 24 Tf 10 20 Td ({term}) Tj ET";
        return carrier switch
        {
            "tiling-pattern" => Pdf(
                visible + "/Pattern cs /P1 scn 20 100 300 60 re f", "/Pattern << /P1 6 0 R >>", null,
                Stream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 300 60] /XStep 300 /YStep 60 " +
                       "/Matrix [1 0 0 1 20 100] /Resources << /Font << /F1 5 0 R >> >>",
                    "0 g " + (cellText ?? "0 0 m 300 60 l 0 60 m 300 0 l S"))),
            // Luminosity mask: where the group paints white, the blue fill shows.
            "smask-group" => Pdf(
                visible + "q /GS1 gs 0 0 1 rg 20 100 300 60 re f Q",
                "/ExtGState << /GS1 << /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >> >>", null,
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 200] /Group << /S /Transparency /CS /DeviceGray >> " +
                       "/Resources << /Font << /F1 5 0 R >> >>",
                    "1 g " + (term == null ? "20 100 300 60 re f" : $"BT /F1 24 Tf 30 120 Td ({term}) Tj ET"))),
            "type3-charproc" => Pdf(
                visible + "BT /T3 1 Tf 20 100 Td (a) Tj ET", null, "/T3 6 0 R",
                "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 300 60] /FontMatrix [1 0 0 1 0 0] /CharProcs << /a 7 0 R >> " +
                "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [300] " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
                Stream("", "300 0 0 0 300 60 d1 " + (cellText ??
                    "q 300 0 0 60 0 0 cm BI /W 4 /H 1 /BPC 1 /IM true ID   EI Q"))),
            "form-nested-3" => Pdf(
                visible + "/Fa Do", "/XObject << /Fa 6 0 R >>", null,
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 200] /Resources << /XObject << /Fb 7 0 R >> >>", "/Fb Do"),
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 200] /Resources << /XObject << /Fc 8 0 R >> >>", "/Fc Do"),
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 200] /Resources << /Font << /F1 5 0 R >> >>",
                    $"BT /F1 24 Tf 30 120 Td ({term}) Tj ET")),
            "annotation-appearance" => Pdf(
                visible, null, null, "ANNOTS",
                "<< /Type /Annot /Subtype /FreeText /Rect [20 100 320 160] /DA (/F1 24 Tf 0 g) /AP << /N 7 0 R >> >>",
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 300 60] /Resources << /Font << /F1 5 0 R >> >>", cellText!)),
            _ => throw new ArgumentOutOfRangeException(nameof(carrier)),
        };
    }

    private static byte[] Pdf(string content, string? resources, string? extraFonts, params string[] extra)
    {
        var annots = extra.Length > 0 && extra[0] == "ANNOTS";
        if (annots) extra = extra[1..];
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 {PageHeight}] /Contents 4 0 R " +
            $"/Resources << /Font << /F1 5 0 R {extraFonts} >> {resources} >>{(annots ? " /Annots [6 0 R]" : "")} >>",
            Stream("", content),
            Helvetica,
        };
        bodies.AddRange(extra);

        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Count + 1];
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Count; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private static string Stream(string dict, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dict} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
        int x0 = Math.Max(0, (int)Math.Floor(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right * scale));
        int y0 = Math.Max(0, (int)Math.Floor((PageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling((PageHeight - box.Bottom) * scale));
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                total++;
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-nested-carrier-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
