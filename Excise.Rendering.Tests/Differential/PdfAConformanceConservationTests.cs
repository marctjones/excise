using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Authoring;        // PdfDocumentBuilder / PdfAConformance (#1507)
using Excise.Core.Document;
using Excise.Core.Graphics;        // PdfFont (#1507)
using Excise.Core.Operations;
using Excise.Core.Security;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;          // SavedPdfLeakScanner (#1507)
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A document that arrives claiming PDF/A conformance must not lose it by
/// passing through excise.
///
/// <para><b>The first gate here judged by something other than excise or a
/// renderer.</b> Every other differential in this repo asks "does excise draw
/// what other engines draw" or "did excise remove what it said it removed".
/// This asks a question with an external, published answer: veraPDF is the PDF
/// Association's reference validator, and it either accepts the output or it
/// does not.</para>
///
/// <para><b>On PDF 2.0.</b> There is no validator for ISO 32000-2, because it
/// is a format specification rather than a conformance profile — "PDF 2.0
/// conformant" is not a checkable claim. PDF/A-4 IS built on PDF 2.0, so a
/// file that veraPDF accepts as PDF/A-4 is the nearest externally-checkable
/// statement available. Narrower than "excise is PDF 2.0 conformant", and worth
/// more, because somebody other than excise is saying it.</para>
///
/// <para><b>Conservation, not validation.</b> The assertion is that the verdict
/// does not get WORSE — same flavour, still passing. excise is not asked to
/// make a non-conforming file conform, and a file that arrives failing may
/// leave failing. That is the "judge the delta, not the state" rule (#944/#945)
/// with an external oracle on both sides.</para>
///
/// <para>This found #1056 on its first use: <c>excise merge</c> strips XMP
/// <c>pdfaid</c>, so a valid PDF/A-4 document came out conforming to nothing.
/// The file still opened, rendered identically, and passed
/// <c>qpdf --check</c> — no existing gate saw it.</para>
/// </summary>
public class PdfAConformanceConservationTests
{
    private const string CorpusRoot =
        "test-pdfs/verapdf-corpus/veraPDF-corpus-master";

    /// <summary>
    /// A bounded set of PDF/A files the validator already accepts. Bounded on
    /// purpose: veraPDF is a JVM tool at roughly a second per call and this runs
    /// it twice per fixture. The point is a regression tripwire on the writer,
    /// not a survey.
    /// </summary>
    public static TheoryData<string> Fixtures()
    {
        var d = new TheoryData<string>();
        foreach (var rel in new[]
                 {
                     "PDF_A-4/6.9 Embedded files/veraPDF test suite 6-9-t03-pass-a.pdf",
                     "PDF_A-4/6.10 Optional content/veraPDF test suite 6-10-t01-pass-a.pdf",
                     "PDF_A-4/6.10 Optional content/veraPDF test suite 6-10-t02-pass-a.pdf",
                     "PDF_A-2b/6.9 Optional content/veraPDF test suite 6-9-t03-pass-a.pdf",
                 })
            d.Add(rel);
        return d;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SavingAConformingDocument_DoesNotLoseItsConformance(string relative)
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable, "verapdf not installed");

        var path = Resolve(Path.Combine(CorpusRoot, relative));
        Assert.SkipWhen(path == null, "veraPDF corpus not present");

        var before = VeraPdfReferenceValidator.Validate(path!);
        Assert.SkipWhen(before is null or { Ran: false }, "verapdf could not judge the input");

        // Only conservation is asserted, so a fixture the validator already
        // rejects proves nothing and is skipped rather than silently counted.
        Assert.SkipWhen(!before!.Passed, $"input does not conform ({before.Flavour}); nothing to conserve");

        var output = Path.Combine(Path.GetTempPath(), $"excise-pdfa-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                doc.Save(output);

            var after = VeraPdfReferenceValidator.Validate(output);
            after.Should().NotBeNull();
            after!.Ran.Should().BeTrue($"verapdf must be able to judge what excise wrote: {after.Failure}");

            after.Flavour.Should().Be(before.Flavour,
                "the DETECTED flavour comes from the file's own XMP pdfaid — a change here means " +
                "excise altered or dropped the document's conformance claim. A fall back to '1b' " +
                "is the signature of the identification being lost entirely (#1056).");

            after.Passed.Should().BeTrue(
                $"a document that arrived as valid PDF/A-{before.Flavour} must not be downgraded by " +
                "being opened and saved — the claim is an archival guarantee somebody relied on");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    // ── #1057: the same conservation contract, per assembly operation. merge and
    //    split found #1056; encrypt asserts the OPPOSITE (PDF/A forbids
    //    encryption, so conformance MUST be withdrawn, not silently faked); redact
    //    was already measured clean and is gated here for free. ──────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Merge_SingleInput_DoesNotLoseConformance(string relative) =>
        RunConservationRow(relative, (src, outPath) =>
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(src));
            using var merged = PdfDocumentMerger.Merge(
                new[] { (doc, (IReadOnlyList<int>)Enumerable.Range(0, doc.PageCount).ToList()) });
            merged.Save(outPath);
        });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Split_SinglePages_DoesNotLoseConformance(string relative) =>
        RunConservationRow(relative, (src, outPath) =>
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(src));
            var fragments = PdfDocumentSplitter.SplitToSinglePages(doc);
            try { fragments[0].Save(outPath); }
            finally { foreach (var f in fragments) f.Dispose(); }
        });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Encrypt_WithdrawsConformance(string relative) =>
        RunConservationRow(relative, (src, outPath) =>
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(src));
            doc.Save(outPath, new PdfEncryptionOptions
            {
                UserPassword = "pw",
                Algorithm = PdfEncryptionAlgorithm.Aes256,
            });
        }, expectConformanceLost: true);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Redact_DoesNotLoseConformance(string relative) =>
        RunConservationRow(relative, (src, outPath) =>
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(src));
            var term = FirstWord(doc);
            if (term != null) doc.RedactText(term);   // a no-op (no term) must conserve too
            doc.Save(outPath);
        });

    /// <summary>
    /// #1507 — the AREA path, which <see cref="Redact_DoesNotLoseConformance"/>
    /// above does not reach. <c>RedactText</c> passes
    /// <c>scrubDocumentCarriers: false</c> and applies its own term-based carrier
    /// scrub, so it never ran the wholesale strip that deleted the catalog
    /// <c>/Metadata</c> — and with it the <c>pdfaid</c> identification every
    /// PDF/A part requires. A click-to-redact on an archival document therefore
    /// produced a file conforming to nothing, while the sibling row above stayed
    /// green.
    ///
    /// <para>The corpus fixtures matter here rather than an authored one: these
    /// are real PDF/A-2b and PDF/A-4 files, and PDF/A-4 is the case a
    /// "keep pdfaid:part" fix gets wrong (it also needs <c>pdfaid:rev</c>, must
    /// NOT have <c>pdfaid:conformance</c>, and forbids a present-but-empty Info
    /// dictionary). The flavour assertion in
    /// <see cref="RunConservationRow"/> is what catches a lost identification:
    /// veraPDF falls back to '1b' when it cannot detect one (#1056).</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void RedactArea_DoesNotLoseConformance(string relative) =>
        RunConservationRow(relative, (src, outPath) =>
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(src));
            var page = doc.GetPage(1);
            var box = page.MediaBox.Normalize();
            // A small square inset from the bottom-left corner, deliberately:
            // the document-carrier strip runs before any geometry is considered
            // and IS the subject here, while a large rectangle would invite
            // geometry-driven collateral (a flattened Form XObject, a removed
            // annotation) whose conformance effects belong to another gate.
            page.RedactArea(new PdfRectangle(
                box.Left + 20, box.Bottom + 20, box.Left + 60, box.Bottom + 60));
            doc.Save(outPath);
        });

    /// <summary>
    /// #1507's three-oracle row: conformance AND removal, on a document excise
    /// authored as PDF/A so the term is reachable by an independent extractor.
    ///
    /// <para>Why all three are needed. veraPDF alone is satisfied by keeping the
    /// whole XMP packet — including the <c>dc:title</c> that still names the
    /// redacted word. <see cref="SavedPdfLeakScanner"/> alone is blind to the
    /// PAGE, because the builder embeds the font as Identity-H and the word is a
    /// run of two-byte GIDs rather than text. mutool alone says nothing about
    /// conformance. So: veraPDF for the claim, the scanner for the positionless
    /// carriers, mutool for the glyphs — with a guard that mutool could read the
    /// term BEFORE, or "mutool does not see it" would prove nothing.</para>
    /// </summary>
    [Fact]
    public void RedactArea_OnAnAuthoredPdfA_KeepsConformance_AndTheTermIsGonePerMutool()
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable, "verapdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var fontPath = Resolve(Path.Combine("Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf"));
        Assert.SkipWhen(fontPath == null, "DejaVuSans.ttf fixture not present");

        const string term = "CANARYNAME";
        var font = PdfFont.FromTrueType(File.ReadAllBytes(fontPath!), 11);
        var authored = PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title($"Archival {term} Test")
            .DefaultFont(font)
            .PdfA(PdfAConformance.PdfA2B)
            .Heading("Archival Test")
            .Paragraph($"Body naming {term} once.")
            .SaveToBytes();

        byte[] redacted;
        using (var doc = PdfDocument.Open(authored))
        {
            var page = doc.GetPage(1);
            page.RedactArea(GlyphBox(page, term));   // default: carriers stripped
            redacted = doc.SaveToBytes();
        }

        var before = Path.Combine(Path.GetTempPath(), $"excise-pdfa-area-before-{Guid.NewGuid():N}.pdf");
        var after = Path.Combine(Path.GetTempPath(), $"excise-pdfa-area-after-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(before, authored);
            File.WriteAllBytes(after, redacted);

            (MutoolTextExtractor.ExtractPage(before, 1) ?? "").Should().Contain(term,
                "the oracle must be able to see what it is later asked to confirm is gone");
            (MutoolTextExtractor.ExtractPage(after, 1) ?? "").Should().NotContain(term,
                "the glyphs inside the box must be gone from the page, per a tool that is not excise");

            SavedPdfLeakScanner.FindTerm(redacted, term).Should().BeEmpty(
                "and gone from /Info /Title and the XMP dc:title, which the carrier strip owns");

            var verdict = VeraPdfReferenceValidator.Validate(after);
            verdict.Should().NotBeNull();
            verdict!.Ran.Should().BeTrue($"verapdf must be able to judge what excise wrote: {verdict.Failure}");
            verdict.Flavour.Should().Be("2b",
                "the DETECTED flavour comes from the file's own pdfaid — anything else means the " +
                "area redaction withdrew or altered the document's conformance claim (#1507)");
            verdict.Passed.Should().BeTrue("an area-redacted PDF/A-2b file must still BE PDF/A-2b");
        }
        finally
        {
            try { File.Delete(before); } catch { /* best effort */ }
            try { File.Delete(after); } catch { /* best effort */ }
        }
    }

    /// <summary>The union of the glyph boxes of <paramref name="term"/> on the
    /// page — what a user's drag over that word yields. excise's extraction picks
    /// the geometry; no assertion depends on excise's opinion of the result.</summary>
    private static PdfRectangle GlyphBox(PdfPage page, string term)
    {
        var text = new System.Text.StringBuilder();
        var owner = new List<Excise.Core.Text.Letter>();
        foreach (var letter in page.Letters)
        {
            text.Append(letter.Value);
            for (var i = 0; i < letter.Value.Length; i++) owner.Add(letter);
        }

        var at = text.ToString().IndexOf(term, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the fixture must draw '{term}' on the page");

        double left = double.MaxValue, bottom = double.MaxValue;
        double right = double.MinValue, top = double.MinValue;
        for (var i = at; i < at + term.Length && i < owner.Count; i++)
        {
            var box = owner[i].GlyphRectangle.Normalize();
            left = Math.Min(left, box.Left);
            bottom = Math.Min(bottom, box.Bottom);
            right = Math.Max(right, box.Right);
            top = Math.Max(top, box.Top);
        }
        return new PdfRectangle(left, bottom, right, top);
    }

    private static string? FirstWord(PdfDocument doc)
    {
        if (doc.PageCount < 1) return null;
        var text = string.Concat(doc.GetPage(1).Letters.Select(l => l.Value));
        var m = System.Text.RegularExpressions.Regex.Match(text, "[A-Za-z]{4,}");
        return m.Success ? m.Value : null;
    }

    /// <summary>Apply <paramref name="operation"/> to a conforming fixture and
    /// assert veraPDF's verdict on the output: same flavour and still passing
    /// (conservation), unless <paramref name="expectConformanceLost"/> — then it
    /// must NOT pass (the encrypt case). Skips loudly where veraPDF/corpus/term
    /// is absent.</summary>
    private void RunConservationRow(string relative, Action<string, string> operation,
        bool expectConformanceLost = false)
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable, "verapdf not installed");
        var path = Resolve(Path.Combine(CorpusRoot, relative));
        Assert.SkipWhen(path == null, "veraPDF corpus not present");

        var before = VeraPdfReferenceValidator.Validate(path!);
        Assert.SkipWhen(before is null or { Ran: false }, "verapdf could not judge the input");
        Assert.SkipWhen(!before!.Passed, $"input does not conform ({before.Flavour}); nothing to conserve");

        var output = Path.Combine(Path.GetTempPath(), $"excise-pdfa-op-{Guid.NewGuid():N}.pdf");
        try
        {
            operation(path!, output);
            var after = VeraPdfReferenceValidator.Validate(output);
            after.Should().NotBeNull();

            if (expectConformanceLost)
            {
                // Encryption is forbidden in PDF/A: veraPDF either cannot validate
                // the encrypted file at all (no verdict) or reports non-conformant.
                // Both mean the claim was withdrawn; the failure to prevent is a
                // still-PASSING verdict on an encrypted file.
                (after!.Ran && after.Passed).Should().BeFalse(
                    "PDF/A forbids encryption — an encrypted output must not still validate as PDF/A");
            }
            else
            {
                after!.Ran.Should().BeTrue($"verapdf must be able to judge what excise wrote: {after.Failure}");
                after.Flavour.Should().Be(before.Flavour,
                    "the pdfaid flavour must survive — a fall back to '1b' is #1056's " +
                    "identification-lost signature");
                after.Passed.Should().BeTrue(
                    $"a valid PDF/A-{before.Flavour} must not be downgraded by this operation");
            }
        }
        finally { try { File.Delete(output); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Walk up from the test binary looking for a corpus-relative path.
    /// </summary>
    /// <remarks>
    /// ⚠️ The bound was 8, and 8 is one short of a GIT WORKTREE. Measured
    /// 2026-09-16: from
    /// <c>&lt;repo&gt;/.claude/worktrees/&lt;branch&gt;/Excise.Rendering.Tests/bin/Debug/net10.0/</c>
    /// the repo root — the only place <c>test-pdfs/</c> exists — is 8 levels up,
    /// and the loop's first iteration is spent on
    /// <c>Path.GetDirectoryName</c> merely stripping BaseDirectory's trailing
    /// separator, so it stopped at <c>.claude</c>. Every corpus row in this
    /// class therefore skipped with "veraPDF corpus not present" in any
    /// worktree-based session — a declared skip (#1172 is satisfied) whose
    /// stated reason was FALSE: the corpus was there, the search was too
    /// shallow. 12 clears a worktree with room to spare.
    ///
    /// <para>The same bound appears in several other Differential test files,
    /// which are silently losing their corpus rows in a worktree the same way.
    /// Fixed here because this class is what #1507 is gated on; the sweep is
    /// filed separately rather than done blind across files this change does
    /// not otherwise touch.</para>
    /// </remarks>
    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
