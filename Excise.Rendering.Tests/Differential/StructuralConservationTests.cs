using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1117 — collateral the TEXT delta cannot see. RedactionCollateralHarness
/// already scores text lost beyond the target; two kinds of damage are invisible
/// to it:
///
/// <list type="number">
///   <item><b>Structural conservation.</b> A redaction that silently drops a
///     bookmark tree, a link, a form field, an attachment, or a PDF/A claim has
///     destroyed something no character-level diff detects (#1056–#1059 found
///     exactly this class in merge/split).</item>
///   <item><b>Text that survived but MOVED.</b> #1100 kept every character in the
///     stream and pushed the line off the page — a presence check calls that
///     clean while mutool reads "Issue  - passw". That signal is #1104's
///     glyph-advance parity; this file leaves it there and covers the structural
///     half, which nothing else does.</item>
/// </list>
///
/// <para><see cref="StructuralInventory"/> is the before/after column. The
/// load-bearing test is not "redaction conserves structure" (a clean pass proves
/// little) but that the inventory DETECTS a deliberately dropped bookmark — a
/// measurement that cannot see the damage it exists to catch is worthless.</para>
/// </summary>
public class StructuralConservationTests
{
    // ── the measurement must be able to SEE a dropped structure ──────────────

    [Fact]
    public void Inventory_DetectsADeliberatelyDroppedBookmark()
    {
        var withTwo = StructuralInventory.Of(PdfDocument.Open(BuildRichPdf(bookmarks: 2)));
        var withNone = StructuralInventory.Of(PdfDocument.Open(BuildRichPdf(bookmarks: 0)));

        withTwo.Bookmarks.Should().Be(2, "the fixture declares two outline items");
        withNone.Bookmarks.Should().Be(0, "dropping the outline must be visible in the count");
        withTwo.Bookmarks.Should().BeGreaterThan(withNone.Bookmarks,
            "if the inventory cannot tell a document with a bookmark tree from one without, " +
            "it cannot flag redaction that silently drops it — the whole point of #1117");
    }

    [Fact]
    public void Inventory_DetectsADroppedAttachmentAndFormField()
    {
        var rich = StructuralInventory.Of(PdfDocument.Open(BuildRichPdf(bookmarks: 1, attach: true, field: true)));
        var bare = StructuralInventory.Of(PdfDocument.Open(BuildRichPdf(bookmarks: 1, attach: false, field: false)));

        rich.Attachments.Should().Be(1);
        bare.Attachments.Should().Be(0);
        rich.FormFields.Should().BeGreaterThan(bare.FormFields);
    }

    // ── redaction must not be collateral damage to structure ─────────────────

    [Fact]
    public void RedactingATerm_ConservesEveryStructure()
    {
        var pdf = BuildRichPdf(bookmarks: 2, attach: true, field: true, link: true, body: "Body REDACTME text");

        var before = StructuralInventory.Of(PdfDocument.Open(pdf));

        byte[] saved;
        using (var doc = PdfDocument.Open(pdf))
        {
            doc.RedactText("REDACTME");
            using var ms = new MemoryStream();
            doc.Save(ms);
            saved = ms.ToArray();
        }
        var after = StructuralInventory.Of(PdfDocument.Open(saved));

        // Redaction removes the TERM from carriers; it must not remove the
        // carriers themselves. A dropped bookmark or de-registered attachment
        // here is collateral no text-presence assertion would catch.
        after.Should().Be(before,
            $"redaction must conserve document structure — before {before}, after {after}");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static byte[] BuildRichPdf(
        int bookmarks, bool attach = false, bool field = false, bool link = false,
        string body = "Body text")
    {
        var content = Encoding.Latin1.GetBytes($"BT /F1 14 Tf 72 700 Td ({body}) Tj ET\n");
        var catalogExtras = new StringBuilder();
        var pageExtras = new StringBuilder();
        var extra = new System.Collections.Generic.List<string>();
        int next = 6;
        int R() => next++;

        if (bookmarks > 0)
        {
            int ol = R();
            var items = Enumerable.Range(0, bookmarks).Select(_ => R()).ToArray();
            catalogExtras.Append($" /Outlines {ol} 0 R");
            extra.Add($"{ol} 0 obj\n<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {bookmarks} >>\nendobj\n");
            for (int i = 0; i < items.Length; i++)
            {
                var next_ = i + 1 < items.Length ? $" /Next {items[i + 1]} 0 R" : "";
                var prev_ = i > 0 ? $" /Prev {items[i - 1]} 0 R" : "";
                extra.Add($"{items[i]} 0 obj\n<< /Title (Chapter {i + 1}) /Parent {ol} 0 R{next_}{prev_} >>\nendobj\n");
            }
        }
        if (field)
        {
            int f = R();
            catalogExtras.Append($" /AcroForm << /Fields [{f} 0 R] >>");
            extra.Add($"{f} 0 obj\n<< /FT /Tx /T (field1) /V (value) >>\nendobj\n");
        }
        if (attach)
        {
            int names = R(); int ef = R(); int fs = R();
            catalogExtras.Append($" /Names << /EmbeddedFiles {names} 0 R >>");
            extra.Add($"{names} 0 obj\n<< /Names [(a.txt) {fs} 0 R] >>\nendobj\n");
            extra.Add($"{fs} 0 obj\n<< /Type /Filespec /F (a.txt) /EF << /F {ef} 0 R >> >>\nendobj\n");
            var d = "hello\n";
            extra.Add($"{ef} 0 obj\n<< /Type /EmbeddedFile /Length {d.Length} >>\nstream\n{d}endstream\nendobj\n");
        }
        if (link)
        {
            int an = R();
            pageExtras.Append($" /Annots [{an} 0 R]");
            // Far from the redacted text (body is at y=700). An annotation that
            // OVERLAPS the redaction area is removed on purpose (it can cover or
            // leak the redacted content, #1038); conservation is about structure
            // the redaction does NOT touch, so this link sits at the bottom.
            extra.Add($"{an} 0 obj\n<< /Type /Annot /Subtype /Link /Rect [72 100 200 120] /A << /S /URI /URI (https://example.org) >> >>\nendobj\n");
        }

        var sb = new StringBuilder();
        void Obj(string s) => sb.Append(s);
        sb.Append("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R{catalogExtras} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + $"/Resources << /Font << /F1 5 0 R >> >>{pageExtras} >>\nendobj\n");
        sb.Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        sb.Append(Encoding.Latin1.GetString(content));
        sb.Append("\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        foreach (var o in extra) Obj(o);
        sb.Append($"trailer\n<< /Root 1 0 R /Size {next} >>\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
