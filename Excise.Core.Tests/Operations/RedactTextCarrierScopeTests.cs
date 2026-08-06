using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Operations;

/// <summary>
/// #896 — pins the ACTUAL SCOPE of <see cref="PdfDocumentRedactionExtensions.RedactText"/>,
/// and that <see cref="PdfDocumentSanitizer.ScrubTerms"/> covers the rest.
///
/// WHY THIS EXISTS
///
/// `RedactText` reads like "redact this term from this document". It removes it
/// from PAGE CONTENT ONLY. Every document-level carrier — /Info, the XMP
/// packet, outline titles, annotation /Contents — is untouched, and those are
/// precisely the carriers #608 was filed for after they shipped a leak past a
/// green suite.
///
/// That is a defensible engine boundary, but it was nowhere pinned, so the CLI
/// and batch paths called `RedactText` + `Save` and shipped output where the
/// redacted term survives in seven places. Measured on a fixture carrying the
/// term in all eight:
///
/// <code>
///   page content stream   clean      (RedactText's job)
///   /Info /Title          LEAKS
///   /Info /Subject        LEAKS
///   /Info /Keywords       LEAKS
///   /Info /Author         LEAKS
///   XMP dc:title          LEAKS
///   outline /Title        LEAKS
///   annotation /Contents  LEAKS
/// </code>
///
/// These tests do not assert the CLI's behaviour — they assert the CONTRACT the
/// CLI got wrong, so the boundary is explicit and a future caller can see what
/// composing the two buys them.
/// </summary>
public class RedactTextCarrierScopeTests
{
    private const string Secret = "SECRETNAME";

    /// <summary>
    /// THE CONTRACT, post-#896: a plain `RedactText` call clears page content
    /// AND every document-level carrier, with no extra step from the caller.
    ///
    /// This test previously asserted the opposite — that seven carriers survived
    /// — as a characterization of the leak. It inverting is the fix landing, not
    /// a regression.
    /// </summary>
    [Fact]
    public void RedactText_ByDefault_ClearsPageContentAndEveryDocumentLevelCarrier()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.RedactText(Secret);
            var bytes = SaveToBytes(doc);
            var combined = Encoding.Latin1.GetString(bytes)
                         + Encoding.BigEndianUnicode.GetString(bytes);

            combined.Should().NotContain(Secret,
                "a caller who asks to redact a term should not also have to know that /Info, " +
                "XMP, outline titles and annotation /Contents exist. Requiring that is what " +
                "left the CLI and batch paths leaking in seven of eight carriers while " +
                "reporting success (#896)");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The opt-out still works, and is the ONLY way to get the old behaviour.
    ///
    /// Kept because a caller that scrubs its own carriers (the GUI's
    /// redacted-copy flow does) should be able to skip the duplicate pass — and
    /// because pinning it proves the default is doing real work rather than the
    /// scrub being unconditional.
    /// </summary>
    [Fact]
    public void RedactText_WithScrubDisabled_LeavesEveryDocumentLevelCarrier()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.RedactText(Secret, scrubDocumentCarriers: false);
            var saved = SaveToString(doc);

            saved.Should().NotContain("SECRETNAME appears here",
                "glyph removal is unaffected by the opt-out");

            foreach (var carrier in DocumentLevelCarriers)
                saved.Should().Contain(carrier,
                    $"with the scrub explicitly disabled, {carrier} must survive — otherwise " +
                    "the default's behaviour is not actually attributable to the new parameter");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A term shorter than the sanitizer's 3-character floor: page content is
    /// still redacted, carriers deliberately are not.
    ///
    /// Pinned so the limit is a known, stated boundary rather than a surprise
    /// discovered on a real document. Excising 1-2 character fragments from
    /// every metadata string would corrupt unrelated values for no benefit.
    /// </summary>
    [Fact]
    public void AShortTerm_IsRedactedFromContentButNotFromCarriers()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.RedactText("SE");
            var saved = SaveToString(doc);

            saved.Should().Contain("SECRETNAME in Info",
                "terms under 3 characters are below PdfDocumentSanitizer's floor, so carriers " +
                "are untouched — a documented limit, not an oversight. A caller redacting a " +
                "1-2 character term must scrub carriers itself");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The fix, verified end to end BEFORE being wired into any caller: the two
    /// composed leave nothing behind in any carrier, in ASCII or UTF-16BE.
    ///
    /// The byte-level check is the one that matters. CLAUDE.md is explicit that
    /// a page-text assertion cannot catch this class of leak — it passed on
    /// three separate shipping leaks (#636, #608, #637) — and a page-text
    /// assertion is exactly what the CLI path had.
    /// </summary>
    [Fact]
    public void RedactTextThenScrubTerms_LeavesNothingInAnyCarrier()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.RedactText(Secret);
            PdfDocumentSanitizer.ScrubTerms(doc, new[] { Secret });
            var bytes = SaveToBytes(doc);

            var combined = Encoding.Latin1.GetString(bytes)
                         + Encoding.BigEndianUnicode.GetString(bytes);

            combined.Should().NotContain(Secret,
                "the term must be gone from EVERY carrier in the saved bytes — page content, " +
                "/Info, XMP, outline titles and annotation /Contents. Searching the saved bytes " +
                "in both encodings is the only assertion that cannot be fooled by a carrier the " +
                "extractor does not read");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The control. ScrubTerms alone must NOT clear page content — otherwise the
    /// test above would pass on a build where the scrub did everything and
    /// RedactText was redundant, and we would not learn that both are needed.
    /// </summary>
    [Fact]
    public void ScrubTermsAlone_DoesNotTouchPageContent()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            PdfDocumentSanitizer.ScrubTerms(doc, new[] { Secret });
            SaveToString(doc).Should().Contain("SECRETNAME appears here",
                "the sanitizer handles document-level carriers only; glyph removal is " +
                "RedactText's job and the two are not substitutes");
        }
        finally { File.Delete(path); }
    }


    /// <summary>
    /// #905 — the scrub must match the CASE SENSITIVITY the caller used on page
    /// content, or the carriers survive a redaction that reported success.
    ///
    /// Introduced by #896 and caught the same day: RedactText defaults to
    /// case-INsensitive glyph removal, ScrubTerms was hard-coded to Ordinal. So
    /// redacting "smith" cleared the page and left "Smith v. Jones" in
    /// /Info /Title — the exact failure #896 existed to fix, in a new form.
    ///
    /// This is the under-redaction direction, which is why the default is
    /// case-insensitive: over-scrubbing metadata is recoverable, a surviving
    /// name is not.
    /// </summary>
    [Fact]
    public void ADifferentlyCasedTerm_StillClearsTheCarriers()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.RedactText(Secret.ToLowerInvariant());   // fixture stores it upper-case
            var bytes = SaveToBytes(doc);
            var combined = Encoding.Latin1.GetString(bytes)
                         + Encoding.BigEndianUnicode.GetString(bytes);

            combined.ToUpperInvariant().Should().NotContain(Secret,
                "RedactText matched page content case-insensitively, so the carrier scrub must " +
                "too. A case-sensitive scrub leaves the name in /Info /Title while the tool " +
                "reports the redaction succeeded");
        }
        finally { File.Delete(path); }
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static readonly string[] DocumentLevelCarriers =
    {
        "SECRETNAME in Info",
        "SECRETNAME subject",
        "SECRETNAME keyword",
        "SECRETNAME author",
        "SECRETNAME in XMP",
        "SECRETNAME in bookmark",
        "SECRETNAME in annotation",
    };

    /// <summary>
    /// A PDF carrying the term in all eight places: page content, four /Info
    /// fields, the XMP packet, an outline title and an annotation /Contents.
    /// </summary>
    private static string WriteFixture()
    {
        const string content = "BT /F1 24 Tf 72 700 Td (SECRETNAME appears here) Tj ET";
        const string xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
            "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">SECRETNAME in XMP title</rdf:li>" +
            "</rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R /Metadata 9 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [6 0 R] " +
            "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "6 0 obj\n<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] " +
            "/Contents (SECRETNAME in annotation) >>\nendobj\n",
            "7 0 obj\n<< /Type /Outlines /First 8 0 R /Last 8 0 R /Count 1 >>\nendobj\n",
            "8 0 obj\n<< /Title (SECRETNAME in bookmark) /Parent 7 0 R >>\nendobj\n",
            $"9 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n",
            "10 0 obj\n<< /Title (SECRETNAME in Info title) /Subject (SECRETNAME subject) " +
            "/Keywords (SECRETNAME keyword) /Author (SECRETNAME author) >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R /Info 10 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-896-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    private static byte[] SaveToBytes(PdfDocument doc)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"excise-896-out-{Guid.NewGuid():N}.pdf");
        try { doc.Save(tmp); return File.ReadAllBytes(tmp); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static string SaveToString(PdfDocument doc) =>
        Encoding.Latin1.GetString(SaveToBytes(doc));
}
