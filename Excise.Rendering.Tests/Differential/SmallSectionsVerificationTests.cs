using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification for a batch of small, previously
/// self-oracled ("implemented" but not "verified") registry capabilities in
/// <c>test-pdfs/manifests/pdf-spec-registry/sections/08-document.json</c> and
/// <c>12-interactive.json</c> — embedded files (attachments), document
/// metadata (/Info + XMP), and annotation content redaction.
///
/// Every excise-only test that already existed for these capabilities proves
/// excise's writer and excise's reader agree with each other. That is
/// `implemented`, not `verified` — see CLAUDE.md's "no-self-oracle" rule.
/// This file uses <c>qpdf</c> — an independent, non-excise PDF parser — as the
/// oracle throughout: fixtures embedded files are authored with qpdf's own
/// CLI (not excise's writer) before excise reads them, and excise's writes are
/// re-parsed by qpdf's own independent implementation rather than by excise
/// re-opening its own output.
/// </summary>
public class SmallSectionsVerificationTests
{
    // ───────────────────────── embedded files (attachments) ─────────────────

    /// <summary>
    /// document.embedded-files PARSE: a PDF whose attachment was authored by
    /// qpdf's own writer (NOT excise) is opened by excise, and the bytes excise
    /// reports must equal the bytes qpdf attached, byte for byte.
    /// </summary>
    [Fact]
    public void EmbeddedFile_AuthoredByQpdf_IsParsedByExciseWithExactBytes()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var basePath = NewPath();
        var payloadPath = NewPath();
        var withAttachmentPath = NewPath();
        try
        {
            File.WriteAllBytes(basePath, BuildBarePdf());
            var payload = MakeBinaryPayload(seed: 1);
            File.WriteAllBytes(payloadPath, payload);

            RunQpdf(new[]
            {
                basePath,
                "--add-attachment", payloadPath,
                "--key=att1", "--filename=att1.bin", "--mimetype=application/octet-stream", "--",
                withAttachmentPath
            }).Should().NotBeNull("qpdf must be able to author its own fixture");

            using var doc = PdfDocument.Open(File.ReadAllBytes(withAttachmentPath));
            doc.HasEmbeddedFiles.Should().BeTrue();
            var files = doc.GetEmbeddedFiles();
            files.Should().ContainSingle();
            files[0].FileName.Should().Be("att1.bin");
            files[0].Bytes.Should().Equal(payload, "excise must read exactly what an independent writer attached");
        }
        finally
        {
            TryDelete(basePath, payloadPath, withAttachmentPath);
        }
    }

    /// <summary>
    /// document.embedded-files EXTRACT: the bytes excise's
    /// <c>GetEmbeddedFiles()[0].Bytes</c> returns must equal what qpdf's own
    /// <c>--show-attachment</c> independently extracts from the SAME file.
    /// </summary>
    [Fact]
    public void EmbeddedFile_ExtractedBytes_MatchQpdfsIndependentExtraction()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var basePath = NewPath();
        var payloadPath = NewPath();
        var withAttachmentPath = NewPath();
        try
        {
            File.WriteAllBytes(basePath, BuildBarePdf());
            var payload = MakeBinaryPayload(seed: 2);
            File.WriteAllBytes(payloadPath, payload);

            RunQpdf(new[]
            {
                basePath,
                "--add-attachment", payloadPath,
                "--key=att2", "--filename=att2.bin", "--mimetype=application/octet-stream", "--",
                withAttachmentPath
            }).Should().NotBeNull();

            using var doc = PdfDocument.Open(File.ReadAllBytes(withAttachmentPath));
            var exciseBytes = doc.GetEmbeddedFiles().Single().Bytes;

            var qpdfBytes = QpdfShowAttachmentBytes(withAttachmentPath, "att2");
            qpdfBytes.Should().NotBeNull("qpdf must be able to extract its own attachment");

            exciseBytes.Should().Equal(qpdfBytes,
                "excise's extracted attachment bytes must match an independent extractor's, not just excise's own writer");
        }
        finally
        {
            TryDelete(basePath, payloadPath, withAttachmentPath);
        }
    }

    /// <summary>
    /// document.embedded-files PRESERVE: excise opens a qpdf-authored
    /// attachment, saves the document UNCHANGED, and qpdf's own parser
    /// (re-run on excise's output) must still see the same key and the same
    /// bytes.
    /// </summary>
    [Fact]
    public void EmbeddedFile_SurvivesExciseRoundTrip_QpdfConfirmsPreservation()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var basePath = NewPath();
        var payloadPath = NewPath();
        var withAttachmentPath = NewPath();
        var roundTripPath = NewPath();
        try
        {
            File.WriteAllBytes(basePath, BuildBarePdf());
            var payload = MakeBinaryPayload(seed: 3);
            File.WriteAllBytes(payloadPath, payload);

            RunQpdf(new[]
            {
                basePath,
                "--add-attachment", payloadPath,
                "--key=att3", "--filename=att3.bin", "--mimetype=application/octet-stream", "--",
                withAttachmentPath
            }).Should().NotBeNull();

            using (var doc = PdfDocument.Open(File.ReadAllBytes(withAttachmentPath)))
            {
                doc.Save(roundTripPath);
            }

            var list = RunQpdf(new[] { "--list-attachments", roundTripPath });
            list.Should().NotBeNull();
            list!.Contains("att3").Should().BeTrue("qpdf's own parser must still see the attachment key after an excise round trip");

            var qpdfBytes = QpdfShowAttachmentBytes(roundTripPath, "att3");
            qpdfBytes.Should().Equal(payload,
                "an independent reader must see byte-identical attachment content survive an excise open/save cycle");
        }
        finally
        {
            TryDelete(basePath, payloadPath, withAttachmentPath, roundTripPath);
        }
    }

    /// <summary>
    /// document.embedded-files MUTATE + WRITE: <c>ScrubEmbeddedFiles()</c>
    /// followed by a save must leave qpdf's own independent parser seeing ZERO
    /// attachments — not "excise says it removed them", an outsider confirming
    /// the removal actually reached the saved bytes.
    /// </summary>
    [Fact]
    public void ScrubEmbeddedFiles_ThenSave_QpdfConfirmsAttachmentIsGone()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var basePath = NewPath();
        var payloadPath = NewPath();
        var withAttachmentPath = NewPath();
        var scrubbedPath = NewPath();
        try
        {
            File.WriteAllBytes(basePath, BuildBarePdf());
            var payload = MakeBinaryPayload(seed: 4);
            File.WriteAllBytes(payloadPath, payload);

            RunQpdf(new[]
            {
                basePath,
                "--add-attachment", payloadPath,
                "--key=att4", "--filename=att4.bin", "--mimetype=application/octet-stream", "--",
                withAttachmentPath
            }).Should().NotBeNull();

            // Sanity: qpdf sees the attachment before the scrub.
            RunQpdf(new[] { "--list-attachments", withAttachmentPath })!
                .Contains("att4").Should().BeTrue();

            using (var doc = PdfDocument.Open(File.ReadAllBytes(withAttachmentPath)))
            {
                doc.ScrubEmbeddedFiles();
                doc.Save(scrubbedPath);
            }

            var (ok, checkOutput) = QpdfReferenceTool.Check(scrubbedPath)
                ?? throw new InvalidOperationException("qpdf unavailable mid-test");
            ok.Should().BeTrue($"scrubbed output must remain structurally valid to an independent checker:\n{checkOutput}");

            var listAfter = RunQpdf(new[] { "--list-attachments", scrubbedPath }) ?? "";
            listAfter.Contains("att4").Should().BeFalse(
                "an independent parser must see the attachment truly gone, not merely unlisted by excise's own reader");
        }
        finally
        {
            TryDelete(basePath, payloadPath, withAttachmentPath, scrubbedPath);
        }
    }

    /// <summary>
    /// document.embedded-files AUTHOR: excise attaches a brand-new file via
    /// <c>AddEmbeddedFile</c> and saves; qpdf's own parser must independently
    /// see the key, the declared name, and the exact bytes.
    /// </summary>
    [Fact]
    public void AddEmbeddedFile_ThenSave_IsSeenByQpdfsIndependentParser()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var authoredPath = NewPath();
        try
        {
            var payload = MakeBinaryPayload(seed: 5);
            using (var doc = PdfDocument.Open(BuildBarePdf()))
            {
                doc.AddEmbeddedFile("notes.bin", payload, mimeType: "application/octet-stream");
                doc.Save(authoredPath);
            }

            var list = RunQpdf(new[] { "--list-attachments", authoredPath });
            list.Should().NotBeNull();
            list!.Contains("notes.bin").Should().BeTrue("qpdf must independently discover the key excise authored");

            var qpdfBytes = QpdfShowAttachmentBytes(authoredPath, "notes.bin");
            qpdfBytes.Should().Equal(payload, "an independent parser must read back exactly what excise authored");
        }
        finally
        {
            TryDelete(authoredPath);
        }
    }

    // ───────────────────────── document metadata (/Info + XMP) ──────────────

    /// <summary>
    /// document.metadata PARSE + EXTRACT: a PDF's <c>/Info</c> dictionary and
    /// XMP stream are hand-authored (NOT written by excise — literal PDF bytes
    /// in this test), then read independently by BOTH excise's parser and
    /// qpdf's own <c>--show-object</c>. Agreement is evidence about the FILE,
    /// not about excise's internal consistency: neither reader is being fed
    /// the other's answer.
    /// </summary>
    [Fact]
    public void HandAuthoredMetadata_ParsedByExcise_MatchesQpdfsIndependentRead()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        const string title = "Ground Truth Title 998";
        const string author = "Ground Truth Author 998";
        const string xmpMarker = "ground-truth-xmp-marker-998";

        var path = NewPath();
        try
        {
            File.WriteAllBytes(path, BuildPdfWithMetadata(title, author, xmpMarker));

            using var doc = PdfDocument.Open(File.ReadAllBytes(path));
            doc.Title.Should().Be(title);
            doc.Author.Should().Be(author);
            var xmp = doc.GetXmpMetadata();
            xmp.Should().NotBeNull();
            Encoding.UTF8.GetString(xmp!).Should().Contain(xmpMarker);

            // Independent cross-check: qpdf's own parser, reading the SAME
            // hand-authored ground-truth bytes, must report the same values —
            // not values read back from excise.
            var trailer = RunQpdf(new[] { "--show-object=trailer", path });
            trailer.Should().NotBeNull();
            var infoMatch = Regex.Match(trailer!, @"/Info\s+(\d+)\s+0\s+R");
            infoMatch.Success.Should().BeTrue("qpdf must independently resolve the trailer's /Info reference");
            var infoObj = RunQpdf(new[] { $"--show-object={infoMatch.Groups[1].Value}", path });
            infoObj.Should().NotBeNull();
            infoObj!.Should().Contain(title).And.Contain(author);

            var metadataMatch = Regex.Match(trailer!, @"/Metadata"); // presence check only; object number comes from the catalog
            var catalogMatch = Regex.Match(trailer!, @"/Root\s+(\d+)\s+0\s+R");
            catalogMatch.Success.Should().BeTrue();
            var catalogObj = RunQpdf(new[] { $"--show-object={catalogMatch.Groups[1].Value}", path });
            var catalogMetaMatch = Regex.Match(catalogObj ?? "", @"/Metadata\s+(\d+)\s+0\s+R");
            catalogMetaMatch.Success.Should().BeTrue("qpdf must independently resolve the catalog's /Metadata reference");
            var xmpRaw = RunQpdf(new[] { $"--show-object={catalogMetaMatch.Groups[1].Value}", "--raw-stream-data", path });
            xmpRaw.Should().NotBeNull();
            xmpRaw!.Should().Contain(xmpMarker);
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// document.metadata PRESERVE: excise opens the hand-authored metadata
    /// fixture and saves it UNCHANGED; qpdf's own parser, re-run on excise's
    /// output, must still see the same /Info values and XMP body.
    /// </summary>
    [Fact]
    public void Metadata_SurvivesExciseRoundTrip_QpdfConfirmsPreservation()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        const string title = "Round Trip Title 997";
        const string author = "Round Trip Author 997";
        const string xmpMarker = "round-trip-xmp-marker-997";

        var srcPath = NewPath();
        var roundTripPath = NewPath();
        try
        {
            File.WriteAllBytes(srcPath, BuildPdfWithMetadata(title, author, xmpMarker));

            using (var doc = PdfDocument.Open(File.ReadAllBytes(srcPath)))
            {
                doc.Save(roundTripPath);
            }

            var trailer = RunQpdf(new[] { "--show-object=trailer", roundTripPath });
            trailer.Should().NotBeNull();
            var infoMatch = Regex.Match(trailer!, @"/Info\s+(\d+)\s+0\s+R");
            infoMatch.Success.Should().BeTrue();
            var infoObj = RunQpdf(new[] { $"--show-object={infoMatch.Groups[1].Value}", roundTripPath });
            infoObj.Should().Contain(title).And.Contain(author);

            var catalogMatch = Regex.Match(trailer!, @"/Root\s+(\d+)\s+0\s+R");
            var catalogObj = RunQpdf(new[] { $"--show-object={catalogMatch.Groups[1].Value}", roundTripPath });
            var metaMatch = Regex.Match(catalogObj ?? "", @"/Metadata\s+(\d+)\s+0\s+R");
            metaMatch.Success.Should().BeTrue("an independent parser must still see the catalog reference an XMP stream after an excise round trip");
            var xmpRaw = RunQpdf(new[] { $"--show-object={metaMatch.Groups[1].Value}", "--raw-stream-data", roundTripPath });
            xmpRaw.Should().Contain(xmpMarker);
        }
        finally
        {
            TryDelete(srcPath, roundTripPath);
        }
    }

    /// <summary>
    /// document.metadata MUTATE + WRITE: <c>ScrubMetadata(scrubAttachments:
    /// false)</c> followed by a save must leave qpdf's own independent parser
    /// seeing NO trace of the title/author/XMP marker — the removal must
    /// reach the saved bytes as an outsider reads them, not just excise's own
    /// getters.
    /// </summary>
    [Fact]
    public void ScrubMetadata_ThenSave_QpdfConfirmsMetadataIsGone()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        const string title = "Scrub Me Title 996";
        const string author = "Scrub Me Author 996";
        const string xmpMarker = "scrub-me-xmp-marker-996";

        var srcPath = NewPath();
        var scrubbedPath = NewPath();
        try
        {
            File.WriteAllBytes(srcPath, BuildPdfWithMetadata(title, author, xmpMarker));

            using (var doc = PdfDocument.Open(File.ReadAllBytes(srcPath)))
            {
                doc.ScrubMetadata(scrubAttachments: false);
                doc.Save(scrubbedPath);
            }

            var (ok, checkOutput) = QpdfReferenceTool.Check(scrubbedPath)
                ?? throw new InvalidOperationException("qpdf unavailable mid-test");
            ok.Should().BeTrue($"scrubbed output must remain structurally valid:\n{checkOutput}");

            var trailer = RunQpdf(new[] { "--show-object=trailer", scrubbedPath });
            trailer.Should().NotBeNull();
            var infoMatch = Regex.Match(trailer!, @"/Info\s+(\d+)\s+0\s+R");
            infoMatch.Success.Should().BeTrue("the Info dict object itself must survive (emptied, not deleted) per excise's documented scrub behaviour");
            var infoObj = RunQpdf(new[] { $"--show-object={infoMatch.Groups[1].Value}", scrubbedPath });
            infoObj.Should().NotBeNull();
            infoObj!.Should().NotContain(title).And.NotContain(author,
                "an independent parser must see the Title/Author truly gone from the saved bytes");

            var catalogMatch = Regex.Match(trailer!, @"/Root\s+(\d+)\s+0\s+R");
            var catalogObj = RunQpdf(new[] { $"--show-object={catalogMatch.Groups[1].Value}", scrubbedPath }) ?? "";
            catalogObj.Should().NotContain("/Metadata",
                "an independent parser must see the catalog no longer references an XMP stream");
        }
        finally
        {
            TryDelete(srcPath, scrubbedPath);
        }
    }

    // ───────────────────────── annotations ───────────────────────────────────

    /// <summary>
    /// interactive.annotations PARSE: a Square annotation is hand-authored
    /// (NOT via excise's authoring API — literal PDF bytes), then read
    /// independently by BOTH excise's <c>PdfPage.GetAnnotations()</c> and
    /// qpdf's own object-graph parser (<see cref="QpdfReferenceTool.ListAnnotations"/>).
    /// Both readers see the SAME ground-truth bytes; neither is fed the
    /// other's answer.
    /// </summary>
    [Fact]
    public void HandAuthoredAnnotation_ParsedByExcise_MatchesQpdfsIndependentParser()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = NewPath();
        try
        {
            const string contents = "ParseCheckAnnotation995";
            var pdf = Build(
                Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
                Stream("", "BT /F1 12 Tf 72 700 Td (Nothing to see) Tj ET"),
                Obj($"<< /Type /Annot /Subtype /Square /Rect [100 100 300 250] " +
                    $"/Contents ({contents}) >>"));
            File.WriteAllBytes(path, pdf);

            using var doc = PdfDocument.Open(File.ReadAllBytes(path));
            var page = doc.GetPage(1);
            var annots = page.GetAnnotations();
            annots.Should().ContainSingle();
            var seenByExcise = annots[0];
            seenByExcise.Subtype.Should().Be(PdfAnnotationSubtype.Square);
            seenByExcise.Rect.Left.Should().BeApproximately(100, 0.01);
            seenByExcise.Rect.Bottom.Should().BeApproximately(100, 0.01);
            seenByExcise.Rect.Right.Should().BeApproximately(300, 0.01);
            seenByExcise.Rect.Top.Should().BeApproximately(250, 0.01);
            seenByExcise.Contents.Should().Be(contents);

            var seenByQpdf = QpdfReferenceTool.ListAnnotations(path);
            seenByQpdf.Should().NotBeNull();
            var qpdfAnnot = seenByQpdf!.Should().ContainSingle().Subject;
            qpdfAnnot.Subtype.Should().Be("Square");
            qpdfAnnot.Left.Should().BeApproximately(100, 0.01);
            qpdfAnnot.Bottom.Should().BeApproximately(100, 0.01);
            qpdfAnnot.Right.Should().BeApproximately(300, 0.01);
            qpdfAnnot.Top.Should().BeApproximately(250, 0.01);
            qpdfAnnot.Contents.Should().Be(contents);
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// interactive.annotations MUTATE + WRITE: <c>RedactText</c> scrubbing an
    /// annotation's <c>/Contents</c> carrier must leave qpdf's own independent
    /// parser seeing no trace of the secret in ANY annotation — not "excise's
    /// own extractor no longer finds it", the exact self-oracle gap CLAUDE.md
    /// warns about (#636/#608 both passed a green excise-only suite on a
    /// leaking file).
    /// </summary>
    [Fact]
    public void RedactText_ScrubsAnnotationContents_QpdfConfirmsRemoval()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = NewPath();
        var savedPath = NewPath();
        try
        {
            const string secret = "MUTATESECRET994HERE";
            var pdf = Build(
                Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
                Stream("", "BT /F1 12 Tf 72 700 Td (Nothing to see) Tj ET"),
                Obj($"<< /Type /Annot /Subtype /Text /Rect [72 690 92 710] " +
                    $"/Contents ({secret}) >>"));
            File.WriteAllBytes(path, pdf);

            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                // 0 page-content matches: the secret lives only in the
                // annotation carrier, matching the real #1185 shape.
                doc.RedactText(secret, drawBlackRect: false);
                doc.Save(savedPath);
            }

            var (ok, checkOutput) = QpdfReferenceTool.Check(savedPath)
                ?? throw new InvalidOperationException("qpdf unavailable mid-test");
            ok.Should().BeTrue($"redacted output must remain structurally valid:\n{checkOutput}");

            var seenByQpdf = QpdfReferenceTool.ListAnnotations(savedPath);
            seenByQpdf.Should().NotBeNull();
            seenByQpdf!.Any(a => (a.Contents ?? "").Contains(secret)).Should().BeFalse(
                "an independent parser must see the secret truly gone from every annotation's /Contents");
        }
        finally
        {
            TryDelete(path, savedPath);
        }
    }

    // ───────────────────────── shared fixture / qpdf helpers ─────────────────

    private static byte[] BuildBarePdf() => Build(
        Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>"));

    private static byte[] BuildPdfWithMetadata(string title, string author, string xmpMarker)
    {
        var xmpBody = $"<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">{xmpMarker}</x:xmpmeta>";
        return BuildWithInfo(
            $"<< /Title ({title}) /Author ({author}) >>",
            Obj("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>"),
            Stream("/Type /Metadata /Subtype /XML", xmpBody));
    }

    /// <summary>Deterministic, non-textual payload — proves the byte path is binary-safe, not just ASCII-safe.</summary>
    private static byte[] MakeBinaryPayload(int seed)
    {
        var data = new byte[512];
        var state = (uint)(seed * 2654435761u + 1);
        for (var i = 0; i < data.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            data[i] = (byte)(state >> 24);
        }
        return data;
    }

    private static string Obj(string body) => body;

    private static string Stream(string dictExtra, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        var dict = string.IsNullOrEmpty(dictExtra) ? "<<" : $"<< {dictExtra}";
        return $"{dict} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    /// <summary>
    /// Builds a minimal single-generation PDF from numbered object bodies, in
    /// the same shape <c>AdversarialRedactionRegressionTests.Build</c> uses —
    /// duplicated here (rather than shared across assemblies) to keep this
    /// file self-contained.
    /// </summary>
    private static byte[] Build(params string[] bodies) => BuildWithInfo(null, bodies);

    private static byte[] BuildWithInfo(string? infoBody, params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var totalObjects = bodies.Length + (infoBody != null ? 1 : 0);
        var offsets = new long[totalObjects + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int? infoObjNum = null;
        if (infoBody != null)
        {
            infoObjNum = bodies.Length + 1;
            offsets[infoObjNum.Value] = ms.Position;
            Write($"{infoObjNum.Value} 0 obj\n{infoBody}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= totalObjects; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        var trailerExtra = infoObjNum != null ? $" /Info {infoObjNum.Value} 0 R" : "";
        Write($"trailer\n<< /Root 1 0 R /Size {totalObjects + 1}{trailerExtra} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"excise-qpdf-smallsections-{Guid.NewGuid():N}.pdf");

    private static void TryDelete(params string[] paths)
    {
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Runs qpdf with the given arguments and returns combined stdout+stderr
    /// as text. Used for every text-mode qpdf invocation in this file
    /// (--list-attachments, --show-object, --check via
    /// <see cref="QpdfReferenceTool.Check"/>). A dedicated, private copy
    /// rather than extending <see cref="QpdfReferenceTool"/>, since this file
    /// only needs a handful of one-off invocations.
    /// </summary>
    private static string? RunQpdf(string[] args, int timeoutMs = 15_000)
    {
        if (!QpdfReferenceTool.IsAvailable) return null;
        try
        {
            var psi = new ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            p.WaitForExit();

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return stdout + stderr;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Binary-safe capture of <c>qpdf --show-attachment=key</c>, whose stdout
    /// is the raw attachment bytes (not text) — reading it through a
    /// <see cref="StreamReader"/> would corrupt non-UTF8 payloads, which is
    /// the entire point of <see cref="MakeBinaryPayload"/>.
    /// </summary>
    private static byte[]? QpdfShowAttachmentBytes(string pdfPath, string key, int timeoutMs = 15_000)
    {
        if (!QpdfReferenceTool.IsAvailable) return null;
        try
        {
            var psi = new ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"--show-attachment={key}");
            psi.ArgumentList.Add(pdfPath);

            using var p = Process.Start(psi);
            if (p == null) return null;

            var stdoutTask = Task.Run(() =>
            {
                using var ms = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(ms);
                return ms.ToArray();
            });
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            var bytes = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            p.WaitForExit();
            return p.ExitCode == 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
}
