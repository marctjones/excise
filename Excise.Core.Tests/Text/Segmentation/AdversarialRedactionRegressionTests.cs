using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Security-oriented redaction regressions for content that can overlap the
/// page visually while living outside ordinary page content streams.
/// </summary>
public sealed class AdversarialRedactionRegressionTests
{
    private static int CountImageDo(PdfPage page)
    {
        int n = 0;
        foreach (var op in page.GetContentStream().Operators)
        {
            if (op.Name != "Do" || op.Operands.Count == 0) continue;
            var name = op.GetName(0);
            if (!string.IsNullOrEmpty(name)
                && page.GetXObject(name!) is Excise.Core.Primitives.PdfStream s
                && s.GetNameOrNull("Subtype") == "Image")
                n++;
        }
        return n;
    }

    [Fact]
    public void RedactText_TermOverFullPageImage_RegionRedacts_KeepsImage()
    {
        // #1195: a term whose visible ink is baked into a full-page (Flate)
        // image must have only its REGION destroyed — the image is preserved,
        // not deleted (the pre-#1195 whole-Do behaviour that erased 5-36% of a
        // scanned page). Uses the generated adversarial fixture; skips if absent.
        var path = Path.Combine(RepoRoot(), "test-pdfs", "redaction-adversarial",
            "image-ocr-overlay--IMAGEOCROVERLAYSECRET.pdf");
        Assert.SkipUnless(File.Exists(path),
            "adversarial corpus absent — run scripts/gen-adversarial-redaction-corpus.py " +
            "[requires: corpus:redaction-adversarial]");

        using var doc = PdfDocument.Open(path);
        var before = CountImageDo(doc.GetPage(1));
        before.Should().BeGreaterThan(0, "the fixture draws a full-page image");

        doc.RedactText("IMAGEOCROVERLAYSECRET");
        var saved = doc.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        CountImageDo(reopened.GetPage(1)).Should().Be(before,
            "the image is region-redacted in place, not dropped wholesale (#1195)");
    }

    [Fact]
    public void RedactText_OptionsOverload_ByteEquivalentToParamOverload()
    {
        // #1187: the RedactionOptions overload must be a pure surface over the
        // parameter overload — same output, byte for byte, for the same settings.
        byte[] MakePdf() => Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>"),
            Stream("", "BT /F1 12 Tf 72 700 Td (SECRETWORD and more) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var d1 = PdfDocument.Open(MakePdf());
        var r1 = d1.RedactText("SECRETWORD", caseSensitive: true,
            strategy: GlyphRemovalStrategy.AnyOverlap, drawBlackRect: false,
            includeHiddenLayers: true, scrubDocumentCarriers: false, closeWidth: true);
        var b1 = d1.SaveToBytes();

        using var d2 = PdfDocument.Open(MakePdf());
        var r2 = d2.RedactText("SECRETWORD", new RedactionOptions
        {
            CaseSensitive = true,
            Strategy = GlyphRemovalStrategy.AnyOverlap,
            DrawBox = false,
            IncludeHiddenLayers = true,
            ScrubDocumentCarriers = false,
            Width = WidthPolicy.CloseGap,
        });
        var b2 = d2.SaveToBytes();

        r2.VerifiedRemovals.Should().Be(r1.VerifiedRemovals);
        r2.MatchesLocated.Should().Be(r1.MatchesLocated);

        // Same redaction output (a fresh save may differ only in the random
        // trailer /ID, so compare the reopened page content, not raw bytes).
        string Letters(byte[] pdf)
        {
            using var d = PdfDocument.Open(pdf);
            return string.Concat(d.GetPage(1).Letters.Select(l => l.Value));
        }
        Letters(b2).Should().Be(Letters(b1),
            "the options overload must produce the same redacted content as the param overload (#1187)");
        Letters(b1).Should().NotContain("SECRETWORD", "the term was redacted in both");
    }

    [Fact]
    public void RedactArea_OverAcroFormField_RemovesValueAndAppearanceBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /F1 6 0 R >> >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [100 650 260 675] " +
                "/P 3 0 R /V (FORMSECRET) /DV (FORMSECRET) /AP << /N 7 0 R >> >>"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 160 25] " +
                   "/Resources << /Font << /F1 6 0 R >> >>",
                   "BT /F1 12 Tf 2 8 Td (FORMSECRET) Tj ET"));

        Encoding.Latin1.GetString(pdf).Should().Contain("FORMSECRET");

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("FORMSECRET",
            "AcroForm values are part of searchable and redactable page text");

        page.RedactArea(new PdfRectangle(95, 645, 265, 680));

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("FORMSECRET",
            "redacting the widget area must remove both /V and stale /AP appearance text");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain("FORMSECRET");
    }

    [Fact]
    public void RedactArea_OverAnnotation_RemovesContentsAndAppearanceBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /F1 7 0 R >> >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /FreeText /Rect [90 690 280 725] " +
                "/Contents (ANNOTSECRET) /RC (ANNOTSECRET) /AP << /N 6 0 R >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 190 35] " +
                   "/Resources << /Font << /F1 7 0 R >> >>",
                   "BT /F1 12 Tf 4 12 Td (ANNOTSECRET) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        Encoding.Latin1.GetString(pdf).Should().Contain("ANNOTSECRET");

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).GetAnnotations().Should().ContainSingle();

        doc.GetPage(1).RedactArea(new PdfRectangle(80, 680, 290, 735));

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("ANNOTSECRET",
            "annotation contents and appearance streams must not survive an overlapping redaction");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty();
    }

    [Fact]
    public void RedactText_FindsAndRemovesFreeTextAnnotationContent()
    {
        // #660: before this fix, FreeText /Contents was invisible to
        // page.Text/page.Letters entirely — RedactText("ANNOTSECRET") would
        // find zero matches and report success while the annotation
        // survived untouched. Verified via saved bytes (the ONLY carrier
        // that can prove removal, not page.Text re-reading excise's own
        // synthetic letters — the purest form of the self-oracle mistake
        // CLAUDE.md's redaction-code requirements exist to prevent).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /F1 7 0 R >> >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /FreeText /Rect [90 690 280 725] " +
                "/Contents (ANNOTSECRET) /RC (ANNOTSECRET) /AP << /N 6 0 R >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 190 35] " +
                   "/Resources << /Font << /F1 7 0 R >> >>",
                   "BT /F1 12 Tf 4 12 Td (ANNOTSECRET) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("ANNOTSECRET",
            "FreeText content must be findable by search/RedactText, not just page.GetAnnotations()");

        var removed = doc.RedactText("ANNOTSECRET", drawBlackRect: false).VerifiedRemovals;
        removed.Should().BeGreaterThan(0, "RedactText must actually find the annotation content");

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("ANNOTSECRET",
            "a word RedactText reports as removed must actually be gone from the saved bytes — " +
            "'found but not removable' is a new leak, not a fix");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty(
            "the whole annotation (Contents + AP) must be gone, not just made unfindable");
    }

    [Fact]
    public void RedactText_FindsAndRemovesSignatureAppearanceContent()
    {
        // #669: before this fix, TextExtractor unconditionally skipped
        // Signature fields — RedactText("SIGSECRET") would find zero matches
        // and report success while the widget's /AP/N appearance (real,
        // mutool-visible "Digitally signed by…" style text, confirmed on
        // test-pdfs/pdfjs/bug854315.pdf) survived untouched. This fixture
        // mirrors that file's nesting: /AP/N invokes a /FRM Form XObject,
        // and the Tj call lives inside /FRM, not directly in /AP/N.
        //
        // Also exercises the InteractiveRedactionScrubber half of the fix:
        // ScrubFormFields used to skip Signature fields unconditionally
        // (same "no text here" assumption TextExtractor made), so a match
        // reaching that method for a Signature field would previously be
        // findable but NOT removable — the exact gap #660 already had to
        // close once for FreeText. Verified via saved bytes, not
        // page.Text/page.Letters re-reading excise's own synthetic letters
        // (the self-oracle mistake CLAUDE.md's redaction requirements exist
        // to prevent).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) " +
                "/Rect [100 650 260 700] /P 3 0 R /V 8 0 R /AP << /N 6 0 R >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 160 50] " +
                   "/Resources << /XObject << /FRM 7 0 R >> >>",
                   "q 1 0 0 1 0 0 cm /FRM Do Q"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 160 50] " +
                   "/Resources << /Font << /F1 9 0 R >> >>",
                   "BT /F1 12 Tf 4 12 Td (SIGSECRET) Tj ET"),
            Obj("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
                "/Contents <00> /ByteRange [0 0 0 0] >>"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        Encoding.Latin1.GetString(pdf).Should().Contain("SIGSECRET");

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("SIGSECRET",
            "a Signature widget's /AP/N appearance text must be findable by search/RedactText, " +
            "not just page.GetFormFields() (#669)");

        var removed = doc.RedactText("SIGSECRET", drawBlackRect: false).VerifiedRemovals;
        removed.Should().BeGreaterThan(0, "RedactText must actually find the signature appearance text");

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("SIGSECRET",
            "a word RedactText reports as removed must actually be gone from the saved bytes — " +
            "'found but not removable' is a new leak, not a fix");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContain("SIGSECRET");
    }

    [Fact]
    public void RedactText_FindsAndRemovesOrphanedMergedWidgetValue()
    {
        // #670: before this fix, a Widget annotation that is its own field
        // dictionary (§12.7.3.1 "merged" field/widget) but isn't reachable
        // by walking /AcroForm/Fields was invisible to page.Text/page.Letters
        // entirely — RedactText("WIDGETSECRET") would find zero matches and
        // report success while the value survived untouched in the widget's
        // own /V. No /AcroForm dictionary exists at all here, matching
        // issue17069.pdf's shape (widgets with real /FT+/V living only in
        // the page's own /Annots array). Verified via saved bytes, not
        // page.Text re-reading excise's own synthetic letters.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Orphan) " +
                "/V (WIDGETSECRET) /Rect [100 650 260 675] /P 3 0 R >>"));

        Encoding.Latin1.GetString(pdf).Should().Contain("WIDGETSECRET");

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("WIDGETSECRET",
            "an orphaned merged field/widget's /V must be findable by search/RedactText, " +
            "not just page.GetFormFields() called in isolation");

        // The merged widget has no content-stream glyphs to match against —
        // this must route through InteractiveRedactionScrubber via the
        // "AcroForm:"-prefixed FontName convention, same as #660's
        // FreeText-annotation case, not PdfPage.RedactArea's glyph pass.
        page.Letters.Where(l => l.Value != " ")
            .Should().OnlyContain(l => l.FontName.StartsWith("AcroForm:", StringComparison.Ordinal),
                "orphaned widget letters must carry the AcroForm: FontName prefix so RedactText " +
                "routes them through InteractiveRedactionScrubber instead of the glyph-removal path");

        var removed = doc.RedactText("WIDGETSECRET", drawBlackRect: false).VerifiedRemovals;
        removed.Should().BeGreaterThan(0, "RedactText must actually find the orphaned widget's value");

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("WIDGETSECRET",
            "a word RedactText reports as removed must actually be gone from the saved bytes — " +
            "'found but not removable' is a new leak, not a fix");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetFormFields().Should().NotContain(f => f.Value == "WIDGETSECRET");
    }

    [Fact]
    public void RedactText_FindsAndRemovesLinkedFieldValue_WhenWidgetHasNoP()
    {
        // #671: the field IS properly linked via /AcroForm/Fields, but its
        // widget carries no /P — exactly issue19389.pdf's "Password" and
        // "Text" fields. Before this fix, PdfField.PageNumber stayed null
        // and PdfPage.GetFormFields()'s `PageNumber == pageNum` filter
        // silently dropped it, so RedactText never saw it even though the
        // field dictionary itself was fully populated and reachable.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (NoPage) " +
                "/V (NOPAGESECRET) /Rect [100 650 260 675] >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("NOPAGESECRET",
            "a linked field's value must be findable even when its widget has no /P");

        var removed = doc.RedactText("NOPAGESECRET", drawBlackRect: false).VerifiedRemovals;
        removed.Should().BeGreaterThan(0, "RedactText must find the value of a field whose widget lacks /P");

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("NOPAGESECRET",
            "a word RedactText reports as removed must actually be gone from the saved bytes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetFormFields().Should().NotContain(f => f.Value == "NOPAGESECRET");
    }

    [Fact]
    public void RedactArea_PartialGlyphOverlap_RemovesGlyphButKeepsNeighbor()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            Stream("", "BT /F1 24 Tf 100 700 Td (AB) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var b = page.Letters.Single(l => l.Value == "B");
        var partialB = new PdfRectangle(
            b.GlyphRectangle.Left + (b.GlyphRectangle.Width * 0.5),
            b.GlyphRectangle.Bottom,
            b.GlyphRectangle.Right + 1,
            b.GlyphRectangle.Top);

        page.RedactArea(partialB);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        var text = string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value));
        text.Should().Be("A");

        Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes())
            .Should().Contain("(A)").And.NotContain("(B)");
    }

    [Fact]
    public void RedactArea_OverRotatedText_RemovesSavedBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            Stream("", "BT /F1 24 Tf 0 1 -1 0 300 600 Tm (ROTSECRET) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("ROTSECRET");

        var letters = page.Letters.Where(l => "ROTSECRET".Contains(l.Value)).ToList();
        var bounds = new PdfRectangle(
            letters.Min(l => l.GlyphRectangle.Left),
            letters.Min(l => l.GlyphRectangle.Bottom),
            letters.Max(l => l.GlyphRectangle.Right),
            letters.Max(l => l.GlyphRectangle.Top));

        page.RedactArea(bounds);

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("ROTSECRET");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain("ROTSECRET");
    }

    [Fact]
    public void RedactText_DefaultIncludesHiddenOptionalContentText()
    {
        var pdf = BuildHiddenOcgPdf();

        using (var inspected = PdfDocument.Open(pdf))
        {
            var page = inspected.GetPage(1);
            string.Concat(page.Letters.Where(l => !l.IsInHiddenOptionalContent).Select(l => l.Value))
                .Should().Contain("VISIBLE");
            string.Concat(page.Letters.Where(l => l.IsInHiddenOptionalContent).Select(l => l.Value))
                .Should().Be("HIDDENSECRET");
        }

        using (var excluded = PdfDocument.Open(pdf))
        {
            excluded.RedactText("HIDDENSECRET", includeHiddenLayers: false).VerifiedRemovals.Should().Be(0);
            Encoding.Latin1.GetString(excluded.SaveToBytes()).Should().Contain("HIDDENSECRET",
                "callers can explicitly exclude hidden layers when they are not doing security redaction");
        }

        using (var included = PdfDocument.Open(pdf))
        {
            included.RedactText("HIDDENSECRET").VerifiedRemovals.Should().Be(1);
            var saved = Encoding.Latin1.GetString(included.SaveToBytes());
            saved.Should().NotContain("HIDDENSECRET",
                "security redaction must include text hidden in default-off optional-content layers");
            saved.Should().Contain("VISIBLE");
        }
    }

    [Fact]
    public void RedactArea_OverHiddenOptionalContentText_RemovesSavedBytes()
    {
        var pdf = BuildHiddenOcgPdf();

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var hiddenLetters = page.Letters.Where(l => l.IsInHiddenOptionalContent).ToList();
        hiddenLetters.Should().NotBeEmpty();

        page.RedactArea(BoundingBoxOf(hiddenLetters));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());
        saved.Should().NotContain("HIDDENSECRET");
        saved.Should().Contain("VISIBLE");
    }

    [Fact]
    public void RedactText_ScrubsInlineActualText_ButKeepsUnrelatedSpans()
    {
        // #1182: /ActualText carried INLINE in the content stream as a BDC
        // property list (§14.9.4) — not on a StructElem — survived glyph removal.
        // excise reported IsCleanSuccess=true while an accessibility-aware reader
        // recovered the "redacted" name straight out of the marked content. The
        // structure-tree scrubber (#636) never reaches the inline form.
        //
        // The control span (UNRELATEDLABEL) must SURVIVE: scrubbing keys off the
        // removed text, not "delete every /ActualText on the page".
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream("",
                "/Span << /ActualText (INLINESECRET) >> BDC\n" +
                "BT /F1 12 Tf 72 700 Td (INLINESECRET) Tj ET\n" +
                "EMC\n" +
                "/Span << /ActualText (UNRELATEDLABEL) >> BDC\n" +
                "BT /F1 12 Tf 72 660 Td (UNRELATEDLABEL) Tj ET\n" +
                "EMC\n"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        Encoding.Latin1.GetString(pdf).Should().Contain("INLINESECRET");

        using var doc = PdfDocument.Open(pdf);
        doc.RedactText("INLINESECRET", drawBlackRect: false).VerifiedRemovals
            .Should().BeGreaterThan(0);
        var saved = doc.SaveToBytes();

        // Carrier-agnostic, decompress-aware — the inline /ActualText is inside a
        // /FlateDecode content stream after save, invisible to a raw grep (#1049).
        SavedPdfLeakScanner.FindTerm(saved, "INLINESECRET").Should().BeEmpty(
            "the inline marked-content /ActualText must be scrubbed, not just the glyphs");

        // No over-scrub: the unrelated span keeps its /ActualText.
        SavedPdfLeakScanner.FindTerm(saved, "UNRELATEDLABEL").Should().NotBeEmpty(
            "an /ActualText that does not restate a redacted term must survive");
    }

    [Fact]
    public void RedactText_ScrubsAnnotationRichText_NotJustContents()
    {
        // #1185: a /Text sticky note (NOT /FreeText — deliberately not surfaced as
        // page letters, #660) carries the secret in BOTH /Contents and /RC (the
        // XHTML rich-text variant, §12.5.6.2). RedactText finds 0 page matches, so
        // the annotation is not removed wholesale — only the document-carrier scrub
        // runs, and it excised /Contents but left /RC, an intra-annotation asymmetry
        // the carrier policy calls a leak. Found by the #1185 bench on real pdfjs
        // sticky notes (issue17069 'sticky', file_pdfjs_test 'Source').
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", "BT /F1 12 Tf 72 700 Td (Nothing to see) Tj ET"),
            Obj("<< /Type /Annot /Subtype /Text /Rect [72 690 92 710] " +
                "/Contents (STICKYSECRET here) " +
                "/RC (<body><p style=\"color:#000000\">STICKYSECRET here</p></body>) >>"));

        Encoding.Latin1.GetString(pdf).Should().Contain("STICKYSECRET");

        using var doc = PdfDocument.Open(pdf);
        // 0 page-content matches — the term lives only in the annotation carrier.
        doc.RedactText("STICKYSECRET", drawBlackRect: false);
        var saved = doc.SaveToBytes();

        Encoding.Latin1.GetString(saved).Should().NotContain("STICKYSECRET",
            "a /Text annotation's /RC rich-text carrier must be scrubbed like its /Contents");
    }

    [Theory]
    [InlineData("listbox_form.pdf", "Saskatchewan")]   // choice field /Opt option
    [InlineData("issue15053.pdf", "toggled")]          // widget /MK /CA caption
    public void RedactText_ScrubsFormCarriersBeyondValue(string file, string term)
    {
        // #1194: form/widget string carriers beyond /V — a choice field's /Opt
        // (list of options) and a widget /MK /CA caption. Real bench fixtures
        // (pdfium/pdfjs); skip when the corpus is absent.
        var root = RepoRoot();
        var path = System.IO.Path.Combine(root, "test-pdfs", "pdfium", file);
        if (!System.IO.File.Exists(path))
            path = System.IO.Path.Combine(root, "test-pdfs", "pdfjs", file);
        Xunit.Assert.SkipUnless(System.IO.File.Exists(path),
            $"corpus fixture {file} absent [requires: corpus:pdfium]");

        using var doc = PdfDocument.Open(System.IO.File.ReadAllBytes(path));
        doc.RedactText(term, drawBlackRect: false);
        var saved = doc.SaveToBytes();

        Encoding.Latin1.GetString(saved).Should().NotContain(term,
            $"a form/widget carrier (/Opt or /MK /CA) holding '{term}' must be scrubbed");
        // still a valid document.
        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().BeGreaterThan(0);
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void RedactText_DoesNotMatchAcrossAWordGap_NoSpaceGlyph()
    {
        // #1177: two words positioned with a gap but NO space glyph — "your" then
        // "software" — must NOT let a search for "yours" match across the boundary
        // ("yoursoftware"). The match path used to run over the SPACELESS glyph
        // concatenation; on foss-primer that reported "yours" 29x (your+software/
        // server/self) where the page shows 7. FindTextMatches now infers the word
        // gap the same way JoinText does.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            // "your" at x=100, "software" advanced 40u right — a clear gap, no space glyph.
            Stream("", "BT /F1 12 Tf 100 700 Td (your) Tj 40 0 Td (software) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        using (var probe = PdfDocument.Open(pdf))
            // the search stream really is spaceless (guard: reproduces the trap)
            string.Concat(probe.GetPage(1).Letters.Select(l => l.Value))
                .Should().Contain("yoursoftware");

        using var doc = PdfDocument.Open(pdf);
        doc.RedactText("yours", drawBlackRect: false).VerifiedRemovals.Should().Be(0,
            "'yours' must not match across the your|software word gap (#1177)");
        // sanity: the real words still match.
        using var doc2 = PdfDocument.Open(pdf);
        doc2.RedactText("software", drawBlackRect: false).VerifiedRemovals.Should().Be(1);
    }

    private static string Obj(string body) => body;

    private static PdfRectangle BoundingBoxOf(IReadOnlyList<Excise.Core.Text.Letter> letters)
    {
        return new PdfRectangle(
            letters.Min(l => l.GlyphRectangle.Left),
            letters.Min(l => l.GlyphRectangle.Bottom),
            letters.Max(l => l.GlyphRectangle.Right),
            letters.Max(l => l.GlyphRectangle.Top));
    }

    private static byte[] BuildHiddenOcgPdf()
    {
        return Build(
            Obj("<< /Type /Catalog /Pages 2 0 R " +
                "/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> " +
                "/Properties << /HiddenLayer 6 0 R >> >> >>"),
            Stream("",
                "BT /F1 12 Tf 100 720 Td (VISIBLE) Tj ET\n" +
                "/OC /HiddenLayer BDC\n" +
                "BT /F1 12 Tf 100 690 Td (HIDDENSECRET) Tj ET\n" +
                "EMC"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Obj("<< /Type /OCG /Name (Confidential Layer) >>"));
    }

    private static string Stream(string dictExtra, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dictExtra} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
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
