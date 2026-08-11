using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #916 / #905 — the audit that lets excise SAY what a redaction did not
/// examine, instead of silently over- or under-scrubbing.
///
/// The decided policy is "surface it, don't guess". These tests pin the two
/// halves of that:
///
///   * it reports the carriers a redaction cannot reach (bookmark titles have
///     no position; annotations away from the box are never visited)
///   * it reports terms below the scrub floor, which page content redacts and
///     document metadata does not — an under-redaction asymmetry that was
///     previously silent
///
/// What it must NOT do is claim those carriers leak. It has no evidence either
/// way, and overstating would train people to dismiss the warning.
/// </summary>
public class RedactionCarrierAuditTests
{
    private const string Secret = "AUDITSECRET";

    [Fact]
    public void ADocumentWithBookmarksAndAnnotations_ReportsBothAsUnexamined()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            doc.GetPage(1).RedactArea(new PdfRectangle(40, 675, 560, 750));

            var audit = RedactionCarrierAudit.Inspect(doc);

            audit.OutlineTitleCount.Should().Be(2,
                "both bookmark titles survive an area redaction — they carry no position, so " +
                "nothing can say which relate to the redacted content");
            audit.AnnotationsWithTextCount.Should().BeGreaterThan(0,
                "the annotation on page 2 is not reachable from a page-1 area redaction");
            audit.HasUnexaminedCarriers.Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The wording matters as much as the count. "Not examined" is what the
    /// audit knows; "may contain the redacted text" is a guess it cannot make.
    /// </summary>
    [Fact]
    public void TheDescriptionSaysNotExamined_NotMayContain()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            var lines = RedactionCarrierAudit.Inspect(doc).Describe();

            lines.Should().NotBeEmpty();
            lines.Should().Contain(l => l.Contains("not examined"),
                "the audit reports what it did not look at");
            lines.Should().NotContain(l => l.Contains("may contain") || l.Contains("leak"),
                "it has no evidence that any surviving carrier relates to the redacted " +
                "content — claiming otherwise is the guess this exists to avoid");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ACleanDocument_ReportsNothing()
    {
        var path = WriteFixture(withOutlines: false, withAnnotations: false);
        try
        {
            using var doc = PdfDocument.Open(path);
            var audit = RedactionCarrierAudit.Inspect(doc);

            audit.HasUnexaminedCarriers.Should().BeFalse(
                "a document with no bookmarks and no annotation text has nothing unexamined, " +
                "and a warning that always fires is one people stop reading");
            audit.Describe().Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Ng")]
    [InlineData("JD")]
    [InlineData("a")]
    public void ATermBelowTheFloor_IsReported(string term)
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            var audit = RedactionCarrierAudit.Inspect(doc, new[] { term });

            audit.TermsBelowScrubFloor.Should().Contain(term);
            audit.Describe().Should().Contain(l => l.Contains($"'{term}'") && l.Contains("metadata"),
                "initials and short surnames are exactly the terms a redaction workflow " +
                "involves, and the metadata skip was previously silent (#905)");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ATermAtOrAboveTheFloor_IsNotReported()
    {
        var path = WriteFixture();
        try
        {
            using var doc = PdfDocument.Open(path);
            RedactionCarrierAudit.Inspect(doc, new[] { "Lee", Secret })
                .TermsBelowScrubFloor.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The audit mirrors PdfDocumentSanitizer's floor as a public constant
    /// because the sanitizer's own copy is private. If they drift, the audit
    /// warns about the wrong terms — silently. This pins them equal
    /// BEHAVIOURALLY, which is the only way to observe a private field.
    /// </summary>
    [Fact]
    public void TheAuditsFloor_MatchesTheSanitizersActualBehaviour()
    {
        // One character below the floor: carriers must survive a scrub.
        var below = new string('x', RedactionCarrierAudit.ScrubFloor - 1);
        // At the floor: carriers must be scrubbed.
        var at = new string('x', RedactionCarrierAudit.ScrubFloor);

        SurvivesScrub(below).Should().BeTrue(
            $"a {below.Length}-character term is below the sanitizer's floor, which is what " +
            "the audit reports on");
        SurvivesScrub(at).Should().BeFalse(
            $"a {at.Length}-character term is AT the floor and must be scrubbed — if this " +
            "fails, RedactionCarrierAudit.ScrubFloor has drifted from PdfDocumentSanitizer");
    }

    private static bool SurvivesScrub(string term)
    {
        var path = WriteFixture(infoTitle: $"prefix {term} suffix");
        try
        {
            using var doc = PdfDocument.Open(path);
            PdfDocumentSanitizer.ScrubTerms(doc, new[] { term });
            var tmp = Path.Combine(Path.GetTempPath(), $"excise-floor-{Guid.NewGuid():N}.pdf");
            try
            {
                doc.Save(tmp);
                return Encoding.Latin1.GetString(File.ReadAllBytes(tmp)).Contains(term, StringComparison.Ordinal);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        finally { File.Delete(path); }
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static string WriteFixture(
        bool withOutlines = true,
        bool withAnnotations = true,
        string? infoTitle = null)
    {
        var title = infoTitle ?? $"{Secret} in the title";
        const string page1 = "BT /F1 24 Tf 60 700 Td (AUDITSECRET on page one) Tj ET";
        const string page2 = "BT /F1 24 Tf 60 700 Td (page two body) Tj ET";

        var objs = new System.Collections.Generic.List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R"
                + (withOutlines ? " /Outlines 8 0 R" : "") + " >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 5 0 R "
                + "/Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 6 0 R "
                + (withAnnotations ? "/Annots [11 0 R] " : "")
                + "/Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n",
            $"5 0 obj\n<< /Length {page1.Length} >>\nstream\n{page1}\nendstream\nendobj\n",
            $"6 0 obj\n<< /Length {page2.Length} >>\nstream\n{page2}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            withOutlines
                ? "8 0 obj\n<< /Type /Outlines /First 9 0 R /Last 10 0 R /Count 2 >>\nendobj\n"
                : "8 0 obj\n<< /Type /Outlines /Count 0 >>\nendobj\n",
            "9 0 obj\n<< /Title (Chapter One) /Parent 8 0 R /Next 10 0 R >>\nendobj\n",
            $"10 0 obj\n<< /Title ({Secret} chapter) /Parent 8 0 R /Prev 9 0 R >>\nendobj\n",
            "11 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 695 120 715] "
                + $"/Contents ({Secret} in an annotation) >>\nendobj\n",
            $"12 0 obj\n<< /Title ({title}) >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objs) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1)
          .Append(" /Root 1 0 R /Info 12 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-audit-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }
}
