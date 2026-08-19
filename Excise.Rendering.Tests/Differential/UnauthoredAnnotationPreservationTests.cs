using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1017 — annotation subtypes excise cannot AUTHOR still have to survive a
/// save. <c>AnnotationInvariantTests</c> author-then-reads, so it can only
/// reach the 12 subtypes excise can create; the other 11 arrive only in files
/// someone else made and were verified end to end by nothing.
///
/// <para><b>Why this matters more than it looks.</b> A PDF editor that silently
/// drops a form field, a link action or an embedded attachment on save is
/// broken in a way no rendering test catches — the page looks identical. For a
/// redaction tool it is worse: <b>22 of the 28 subtypes in
/// <c>tests/annotation-support-matrix.json</c> are redaction carriers</b>.
/// Annotation <c>/Contents</c>, <c>/T</c> and FileAttachment streams are text
/// and data OUTSIDE the content stream, and you cannot scrub a carrier you
/// silently dropped — nor one you kept without knowing it was there.</para>
///
/// <para><b>The inventory is qpdf's, never excise's.</b> Asking excise whether
/// excise preserved something is the self-oracle this project's rules exist to
/// prevent: a parser that cannot see a subtype would report it absent both
/// before and after, and pass.</para>
///
/// <para>These subtypes are NOT interpreted, and that is a settled decision,
/// not a gap — media is never played, 3D and RichMedia are never rendered, and
/// JavaScript and Launch actions are never executed
/// (<c>annotation-support-matrix.json</c> → <c>notSupported</c>). Declining to
/// PLAY a Sound annotation and declining to KEEP it are different things: the
/// first is a scope decision, the second is data loss.</para>
/// </summary>
public class UnauthoredAnnotationPreservationTests
{
    /// <summary>
    /// One corpus fixture per subtype excise cannot author. All from the
    /// veraPDF PDF/A suite except the pdf.js ones — these subtypes are
    /// genuinely rare in the wild (Sound 5, Movie 11, RichMedia 5, Projection 2
    /// occurrences across 4,147 corpus documents), which is exactly why nothing
    /// exercised them.
    /// </summary>
    /// <summary>
    /// One EXPLICIT fixture per subtype excise cannot author, each verified to
    /// be qpdf-readable and to contain the subtype.
    ///
    /// <para>Pinned rather than searched, after a search-based version skipped
    /// three rows for two different wrong reasons: Redact's only readable
    /// fixture is in the <c>pdfium</c> corpus, not <c>pdfjs</c>, and a cheap
    /// byte pre-filter could not see <c>/FileAttachment</c> because the
    /// annotation dictionary lives in a COMPRESSED OBJECT STREAM. A skip that
    /// looks like "no fixture" and actually means "my search was wrong" is the
    /// worst outcome for a gate whose whole job is noticing absence.</para>
    ///
    /// <para>These subtypes are genuinely rare — Sound 5, Movie 11, RichMedia
    /// 5, Projection 2 occurrences across 4,147 corpus documents, nearly all in
    /// the veraPDF conformance suite rather than in real files. That rarity is
    /// exactly why nothing exercised them.</para>
    /// </summary>
    public static TheoryData<string, string> Fixtures()
    {
        var d = new TheoryData<string, string>();
        foreach (var (subtype, path) in new[]
                 {
                     ("Sound", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.1 Annotation types/veraPDF test suite 6-3-1-t01-fail-c.pdf"),
                     ("Movie", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.1 Annotation types/veraPDF test suite 6-3-1-t01-fail-e.pdf"),
                     ("Screen", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.1 Annotation types/veraPDF test suite 6-3-1-t01-fail-d.pdf"),
                     ("RichMedia", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-4e/6.3 Annotations/6.3.1 Annotation types/veraPDF test suite 6-3-1-t01-pass-d.pdf"),
                     ("3D", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-4e/6.3 Annotations/6.3.1 Annotation types/veraPDF test suite 6-3-1-t01-fail-a.pdf"),
                     ("Projection", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-4/6.3 Annotations/6.3.3 Annotation appearances/veraPDF test suite 6-3-3-t01-pass-d.pdf"),
                     ("TrapNet", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.3 Annotation appearances/veraPDF test suite 6-3-3-t01-fail-v.pdf"),
                     ("PrinterMark", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.3 Annotation appearances/veraPDF test suite 6-3-3-t01-fail-u.pdf"),
                     ("Watermark", @"test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.3 Annotations/6.3.3 Annotation appearances/veraPDF test suite 6-3-3-t01-fail-w.pdf"),
                     ("Caret", @"test-pdfs/pdfjs/annotation-caret-ink.pdf"),
                     ("FileAttachment", @"test-pdfs/pdfjs/annotation-fileattachment.pdf"),
                     ("Redact", @"test-pdfs/pdfium/redact_annot.pdf"),
                 })
            d.Add(subtype, path);
        return d;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ASubtypeExciseCannotAuthor_SurvivesAnOpenSaveRoundTrip(string subtype, string relativePath)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");

        var expected = QpdfSubtypeCount(path!, subtype);
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0,
            $"guard: the chosen fixture must actually contain a /{subtype} annotation, " +
            "or this row proves nothing");

        var output = Path.Combine(Path.GetTempPath(), $"excise-preserve-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                doc.Save(output);

            var after = QpdfSubtypeCount(output, subtype);
            after.Should().BeGreaterThanOrEqualTo(0, "qpdf must be able to read what excise wrote");

            after.Should().Be(expected,
                $"/{subtype} must survive an open→save round trip. excise does not " +
                "interpret this subtype and never will, but preserving it and playing " +
                "it are different things — dropping it is data loss, and 22 of 28 " +
                "subtypes are redaction carriers whose payload ships to the recipient.");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }


    /// <summary>
    /// THE NEGATIVE CONTROL. Every row above compares a count before and after a
    /// round trip; if the measurement could not distinguish "preserved" from
    /// "dropped", all twelve would pass on a writer that discarded everything.
    ///
    /// <para>This removes <c>/Annots</c> from the page before saving and
    /// requires the count to fall to zero. Without it the suite would be twelve
    /// green rows asserting that qpdf can count to one.</para>
    /// </summary>
    [Fact]
    public void TheMeasurementNoticesWhenAnAnnotationIsDropped()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = Resolve("test-pdfs/pdfjs/annotation-fileattachment.pdf");
        Assert.SkipWhen(path == null, "corpus fixture not present");

        QpdfSubtypeCount(path!, "FileAttachment").Should().Be(1, "guard: the fixture carries one");

        var output = Path.Combine(Path.GetTempPath(), $"excise-drop-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
            {
                for (var p = 1; p <= doc.PageCount; p++)
                    doc.GetPage(p).Dictionary.Remove("Annots");
                doc.Save(output);
            }

            QpdfSubtypeCount(output, "FileAttachment").Should().Be(0,
                "with /Annots removed the annotation must be gone — if this still reported 1, " +
                "every preservation row above would be vacuous");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// How many objects qpdf reports with <c>/Subtype /X</c>, or -1 when qpdf
    /// refuses the file.
    ///
    /// <para>Deliberately the RAW object graph rather than
    /// <see cref="QpdfReferenceTool.ListAnnotations"/>. That helper parses
    /// annotations into excise's own subtype model, which is narrower than the
    /// file's — it reported nothing for a fixture whose bytes qpdf plainly shows
    /// carrying <c>/FileAttachment</c>, and three rows here skipped as a result.
    /// A gate for "did this subtype survive" must not be filtered through a
    /// model that may not know the subtype exists; that is the same
    /// self-oracle shape one level removed.</para>
    /// </summary>
    private static int QpdfSubtypeCount(string pdfPath, string subtype)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--json=1");
            psi.ArgumentList.Add("--json-key=objects");
            psi.ArgumentList.Add(pdfPath);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return -1;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return -1; }

            // qpdf exits non-zero on warnings but still emits usable JSON.
            if (stdout.Length == 0) return -1;

            return System.Text.RegularExpressions.Regex
                .Matches(stdout, "\"/Subtype\": \"/" + System.Text.RegularExpressions.Regex.Escape(subtype) + "\"")
                .Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return -1; }
    }


    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
