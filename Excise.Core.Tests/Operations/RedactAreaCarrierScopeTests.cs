using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Operations;

/// <summary>
/// #897 — the same carrier question as #896, for the AREA path.
///
/// #896 gave <c>RedactText</c> a document-carrier scrub because it HAS the term.
/// <c>RedactArea</c> takes a rectangle, so there is no term to hand a sanitizer.
/// Draw a box over a name, save, and the name can still be in the document
/// title.
///
/// WHY THIS FILE MEASURES BEFORE IT ASSERTS
///
/// The obvious way to write these tests is to reason about which scrubber
/// covers which carrier — RedactArea already calls InteractiveRedactionScrubber
/// for annotations, so annotations must be fine, and so on. That reasoning is
/// exactly what produced #636, #608 and #637, all three of which shipped a leak
/// past a suite that read clean.
///
/// So <see cref="CarrierLeakReport"/> below is a MEASUREMENT: it redacts by area
/// and reports, carrier by carrier, what is still in the saved bytes. The
/// assertions are written against what it reports, not against what the call
/// graph suggests.
///
/// THE ANNOTATION CASES THAT REASONING GETS WRONG
///
/// <c>InteractiveRedactionScrubber.ScrubArea(page, area)</c> scrubs annotations
/// ON THAT PAGE that OVERLAP THE BOX. Two cases it cannot reach are in the
/// fixture deliberately:
///
///   * an annotation on the same page, outside the box
///   * an annotation on a different page entirely
///
/// "Handled positionally" is not the same as "satisfies the acceptance
/// criterion", and the difference is only visible if both are in the fixture.
/// </summary>
public class RedactAreaCarrierScopeTests
{
    private const string Secret = "SECRETNAME";

    /// <summary>
    /// The box, in content-stream coordinates, covering the page-1 text run and
    /// the annotation placed inside it — and NOT the annotation placed outside.
    /// </summary>
    private static PdfRectangle Box => new PdfRectangle(60, 690, 400, 730);

    /// <summary>
    /// THE MEASUREMENT. Not an acceptance assertion — this exists so the state
    /// of every carrier after an area redaction is a recorded fact rather than
    /// an inference, and so a future change that moves one shows up here.
    ///
    /// It asserts only the one thing that is unambiguously RedactArea's own job
    /// (the glyphs inside the box are gone). Everything else it prints.
    /// </summary>
    [Fact]
    public void Measure_WhichCarriersSurviveAnAreaRedaction()
    {
        var before = CarrierLeakReport(scrubDocumentCarriers: false);
        var after = CarrierLeakReport(scrubDocumentCarriers: true);

        foreach (var report in new[] { before, after })
            report["page 1 content (inside box)"].Should().BeFalse(
                "removing the glyphs inside the rectangle is RedactArea's own job and is not " +
                "affected by the carrier parameter — if this leaks the rest is meaningless");

        // The two columns are the evidence. Recorded here so the state of every
        // carrier is a measured fact rather than an inference from the call graph.
        var lines = new StringBuilder("carrier survival after RedactArea (scrub off | on):\n");
        foreach (var key in before.Keys)
            lines.Append("  ")
                 .Append(before[key] ? "LEAKS" : "clean").Append(" | ")
                 .Append(after[key] ? "LEAKS" : "clean").Append("  ")
                 .Append(key).Append('\n');

        // The strip must CHANGE something, or the default is decorative.
        var fixedByStrip = before.Keys.Count(k => before[k] && !after[k]);
        fixedByStrip.Should().BeGreaterThan(0, lines.ToString());

        // And it must not change page content — that is the glyph pass's job.
        after["page 1 content (outside box)"].Should()
            .Be(before["page 1 content (outside box)"],
                "the carrier strip is document-level and must not touch page content:\n" + lines);
    }

    /// <summary>
    /// The carriers this fix deliberately does NOT reach, pinned so the gap is a
    /// stated boundary rather than something a reader assumes is covered.
    ///
    /// Every one of these is a real surviving copy of the redacted string. They
    /// are listed in #897 as known gaps: outline titles have no position and no
    /// honest positional rule, and the annotation scrubber reaches only
    /// annotations on THIS page overlapping THIS box.
    ///
    /// If one of these starts coming out clean, that is a fix — invert the entry
    /// and say so, exactly as #896's characterization test was inverted.
    /// </summary>
    [Fact]
    public void KnownGaps_AreStillGaps()
    {
        var after = CarrierLeakReport(scrubDocumentCarriers: true);

        after["outline /Title"].Should().BeTrue(
            "bookmark titles are NOT stripped — destroying a document's whole navigation " +
            "because one box was drawn on one page is disproportionate, and a positional rule " +
            "would be dishonest since a bookmark naming the text can point at any page (#897)");

        after["annot /Contents (p1, outside box)"].Should().BeTrue(
            "InteractiveRedactionScrubber reaches annotations that OVERLAP the box; one " +
            "elsewhere on the same page carries the string and survives");

        after["annot /Contents (p2, other page)"].Should().BeTrue(
            "and an annotation on another page is not reachable from a page-scoped area " +
            "redaction at all");
    }

    /// <summary>
    /// THE ACCEPTANCE CRITERION from #897: the same string in page content and
    /// in the document-level carriers must be gone from the SAVED BYTES, in
    /// ASCII and UTF-16BE.
    ///
    /// Byte-level and both encodings because CLAUDE.md is explicit that a
    /// page-text assertion cannot catch this class — it passed on three separate
    /// shipping leaks.
    /// </summary>
    [Fact]
    public void RedactArea_ByDefault_ClearsTheDocumentLevelCarriers()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.GetPage(1).RedactArea(Box);
            var combined = CombinedEncodings(SaveToBytes(doc));

            foreach (var carrier in WholesaleStrippedCarriers)
                combined.Should().NotContain(carrier,
                    $"{carrier} is a document-level carrier with no position, so an area " +
                    "redaction cannot name what was in the box and must remove the carrier " +
                    "rather than guess at its contents (#897)");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The opt-out, and the proof that the default is doing the work. Without
    /// this, a build where the carriers happened to be empty would pass the
    /// test above and teach us nothing.
    /// </summary>
    [Fact]
    public void RedactArea_WithScrubDisabled_LeavesTheDocumentLevelCarriers()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.GetPage(1).RedactArea(Box, scrubDocumentCarriers: false);
            var combined = CombinedEncodings(SaveToBytes(doc));

            combined.Should().NotContain("SECRETNAME appears here",
                "glyph removal is unaffected by the opt-out");

            foreach (var carrier in WholesaleStrippedCarriers)
                combined.Should().Contain(carrier,
                    $"with the scrub explicitly disabled {carrier} must survive — otherwise the " +
                    "default's behaviour is not attributable to the new parameter");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The strip is WHOLESALE, not term-derived, and this is the test that says
    /// why in a way a reviewer cannot miss.
    ///
    /// The rejected design collected the letters inside the box, split them into
    /// words, and substring-deleted those from every metadata value. Applied to
    /// one ordinary sentence it yields terms like `you got time file` and
    /// corrupts unrelated values — `profile` -> `pro`, `timeline` -> `line`.
    ///
    /// So: a metadata value that shares ordinary words with the redacted text
    /// must come out either intact or absent, never mangled.
    /// </summary>
    [Fact]
    public void TheStripDoesNotMangleUnrelatedValues()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.GetPage(1).RedactArea(Box);
            var combined = CombinedEncodings(SaveToBytes(doc));

            foreach (var fragment in SubstringCorruptionEvidence)
                combined.Should().NotContain(fragment,
                    $"'{fragment}' is what term-derived substring scrubbing leaves behind when " +
                    "a metadata value shares an ordinary word with the redacted text. Seeing it " +
                    "means the wholesale strip was replaced by the design #897 rejected");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// RedactAreas loops over RedactArea, so the strip runs once per rectangle.
    /// Removing an absent key must stay a no-op rather than throwing or
    /// corrupting the second time around.
    /// </summary>
    [Fact]
    public void RedactingSeveralAreas_StripsIdempotently()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            var act = () => doc.GetPage(1).RedactAreas(new[]
            {
                Box,
                new PdfRectangle(60, 600, 400, 640),
                new PdfRectangle(60, 500, 400, 540),
            });

            act.Should().NotThrow("the document-carrier strip must be idempotent — RedactAreas " +
                "applies it once per rectangle");

            var combined = CombinedEncodings(SaveToBytes(doc));
            foreach (var carrier in WholesaleStrippedCarriers)
                combined.Should().NotContain(carrier);
        }
        finally { File.Delete(path); }
    }

    // ── carriers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Carriers the wholesale strip is expected to remove. These have NO
    /// position, so an area redaction has no way to reason about their contents.
    /// </summary>
    private static readonly string[] WholesaleStrippedCarriers =
    {
        "SECRETNAME in Info title",
        "SECRETNAME subject",
        "SECRETNAME keyword",
        "SECRETNAME author",
        "SECRETNAME in XMP title",
    };

    /// <summary>
    /// Values that exist purely to detect the rejected term-derived design. Each
    /// is what substring-deleting an ordinary word out of a legitimate metadata
    /// value would leave behind.
    /// </summary>
    private static readonly string[] SubstringCorruptionEvidence =
    {
        "Ynger",   // "Younger"  minus "you"
        "pro-",    // "profile"  minus "file"
        "-line",   // "timeline" minus "time"
    };

    // ── measurement ──────────────────────────────────────────────────────────

    /// <summary>
    /// Redact by area and report, carrier by carrier, whether the string is
    /// still in the saved bytes. <paramref name="scrubDocumentCarriers"/> null
    /// means "call the default overload", so this works before and after the
    /// parameter exists.
    /// </summary>
    private static Dictionary<string, bool> CarrierLeakReport(bool? scrubDocumentCarriers)
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            var page = doc.GetPage(1);
            if (scrubDocumentCarriers is null)
                page.RedactArea(Box);
            else
                page.RedactArea(Box, scrubDocumentCarriers: scrubDocumentCarriers.Value);

            var combined = CombinedEncodings(SaveToBytes(doc));
            return new Dictionary<string, bool>
            {
                ["page 1 content (inside box)"]        = combined.Contains("SECRETNAME appears here"),
                ["page 1 content (outside box)"]       = combined.Contains("SECRETNAME lower down"),
                ["/Info /Title"]                       = combined.Contains("SECRETNAME in Info title"),
                ["/Info /Subject"]                     = combined.Contains("SECRETNAME subject"),
                ["/Info /Keywords"]                    = combined.Contains("SECRETNAME keyword"),
                ["/Info /Author"]                      = combined.Contains("SECRETNAME author"),
                ["XMP dc:title"]                       = combined.Contains("SECRETNAME in XMP title"),
                ["outline /Title"]                     = combined.Contains("SECRETNAME in bookmark"),
                ["annot /Contents (p1, inside box)"]   = combined.Contains("SECRETNAME annot inside"),
                ["annot /Contents (p1, outside box)"]  = combined.Contains("SECRETNAME annot outside"),
                ["annot /Contents (p2, other page)"]   = combined.Contains("SECRETNAME annot page two"),
            };
        }
        finally { File.Delete(path); }
    }

    private static string CombinedEncodings(byte[] bytes) =>
        Encoding.Latin1.GetString(bytes) + Encoding.BigEndianUnicode.GetString(bytes);

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Two pages. Page 1 carries the secret in a text run inside the box, a
    /// second text run BELOW the box, an annotation inside the box and an
    /// annotation outside it. Page 2 carries a third annotation. Document level:
    /// four /Info fields, the XMP packet, an outline title.
    ///
    /// The three "legitimate" metadata values (Younger / profile / timeline)
    /// exist to catch substring corruption, per #897's rejected design.
    /// </summary>
    private static string WriteFixture()
    {
        const string page1 =
            "BT /F1 24 Tf 72 700 Td (SECRETNAME appears here) Tj ET\n" +
            "BT /F1 24 Tf 72 400 Td (SECRETNAME lower down) Tj ET";
        const string page2 = "BT /F1 24 Tf 72 700 Td (page two body) Tj ET";
        const string xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
            "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">SECRETNAME in XMP title</rdf:li>" +
            "</rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R /Metadata 12 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Annots [7 0 R 8 0 R] " +
            "/Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 9 0 R /Annots [13 0 R] " +
            "/Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n",
            $"5 0 obj\n<< /Length {page1.Length} >>\nstream\n{page1}\nendstream\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            // inside the box
            "7 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 695 120 715] " +
            "/Contents (SECRETNAME annot inside) >>\nendobj\n",
            // same page, well below the box
            "8 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 200 120 220] " +
            "/Contents (SECRETNAME annot outside) >>\nendobj\n",
            $"9 0 obj\n<< /Length {page2.Length} >>\nstream\n{page2}\nendstream\nendobj\n",
            "10 0 obj\n<< /Type /Outlines /First 11 0 R /Last 11 0 R /Count 1 >>\nendobj\n",
            "11 0 obj\n<< /Title (SECRETNAME in bookmark) /Parent 10 0 R >>\nendobj\n",
            $"12 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n",
            // a different page entirely
            "13 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 695 120 715] " +
            "/Contents (SECRETNAME annot page two) >>\nendobj\n",
            "14 0 obj\n<< /Title (SECRETNAME in Info title) /Subject (SECRETNAME subject) " +
            "/Keywords (SECRETNAME keyword) /Author (SECRETNAME author) " +
            "/Creator (Younger profile timeline) >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R /Info 14 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-897-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    private static byte[] SaveToBytes(PdfDocument doc)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"excise-897-out-{Guid.NewGuid():N}.pdf");
        try { doc.Save(tmp); return File.ReadAllBytes(tmp); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
