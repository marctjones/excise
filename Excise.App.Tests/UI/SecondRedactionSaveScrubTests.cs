using System;
using System.Reactive.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #898 — settle, by driving the real GUI commands, whether redacting an
/// already-redacted copy skips the metadata scrub.
///
/// ANSWER: NOT A BUG, for two independent reasons. Both are pinned below.
///
/// The suspicion was that <c>SaveFileAsync</c> forces the redacted-copy
/// workflow only when <c>FileState.IsOriginalFile &amp;&amp; HasUnsavedChanges</c>,
/// so once a redacted copy has been saved and reloaded the guard would stop
/// applying and Ctrl+S would reach <c>SaveDocument()</c> without the scrub.
///
///   1. <b>The guard still applies.</b> Loading the copy runs
///      <c>DocumentStateManager.SetDocument</c>, which sets
///      <c>OriginalFilePath = CurrentFilePath</c> — the copy is re-opened as a
///      fresh ORIGINAL, so <c>IsOriginalFile</c> stays true.
///   2. <b>There is nothing left to scrub.</b> The first pass already removed
///      /Info, XMP and the embedded files, so a later save has no carrier to
///      skip even if it took the plain path.
///
/// Only (1) is mutation-sensitive; (2) is why the end-to-end byte check cannot
/// discriminate. Both are stated at their assertions.
///
/// WHAT THAT DOES AND DOES NOT COST, POST-#896/#897
///
/// Two engine-level changes landed after this issue was filed and cover most of
/// it regardless of which save path runs:
///
///   #896  RedactText scrubs document carriers itself
///   #897  RedactArea strips /Info and the XMP packet wholesale
///
/// What <c>PrepareRedactedCopy</c> still adds beyond those is
/// <c>ScrubMetadata(scrubAttachments: true)</c> — embedded files — plus the
/// verification and audit passes. So the residual question is specifically
/// about ATTACHMENTS, and that is what the fixture carries the secret in.
///
/// The point of keeping this file after answering "no bug" is that it pins WHICH
/// mechanisms protect the path, so the next reader does not re-derive the same
/// worry from the same `if` — and so a regression in the guard fails loudly.
/// </summary>
[Collection("AvaloniaTests")]
public class SecondRedactionSaveScrubTests : IDisposable
{
    private const string Secret = "SECONDPASSSECRET";
    private readonly string _tempDir;

    public SecondRedactionSaveScrubTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"excise-898-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The exact sequence from the issue: redact the original, save the copy,
    /// redact again inside that copy, Ctrl+S. Assert on the saved bytes in both
    /// encodings — the only assertion shape that cannot be fooled by a carrier
    /// the extractor does not read.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task RedactingAnAlreadyRedactedCopy_ThenSaving_LeavesNoSecretInAnyCarrier()
    {
        var source = Path.Combine(_tempDir, "source.pdf");
        var copy = Path.Combine(_tempDir, "redacted-copy.pdf");
        WriteFixture(source);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        // ── first pass: the redacted-copy workflow, which DOES scrub ──────────
        vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(copy));
        vm.IsRedactionMode = true;
        vm.RedactionWorkflow.MarkArea(
            PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 675, 560, 750)), Secret);
        await vm.ApplyAllRedactionsCommand!.Execute();

        File.Exists(copy).Should().BeTrue("the Apply All command writes the redacted copy");

        // ── THE ANSWER TO #898, AND IT IS NOT WHAT THE ISSUE SUSPECTED ───────
        //
        // The worry was that after saving a redacted copy, IsOriginalFile goes
        // FALSE, the guard in SaveFileAsync stops applying, and Ctrl+S reaches
        // SaveDocument() without the scrub.
        //
        // It stays TRUE. Loading the copy runs DocumentStateManager.SetDocument,
        // which sets CurrentFilePath AND OriginalFilePath to the same path:
        //
        //     CurrentFilePath  = filePath;
        //     OriginalFilePath = filePath;
        //
        // So the copy is re-opened as a fresh ORIGINAL — the third of the three
        // possibilities #898 listed — and every subsequent redact-then-save is
        // protected by exactly the same guard as the first.
        //
        // This is the ONLY mutation-sensitive assertion in the file: forcing
        // SetDocument to stop resetting OriginalFilePath makes it fail, and
        // nothing else here notices. That is what gives it its value.
        vm.FileState.IsOriginalFile.Should().BeTrue(
            "loading the redacted copy re-opens it as a fresh original (SetDocument sets " +
            "OriginalFilePath = CurrentFilePath), so SaveFileAsync keeps forcing the " +
            "redacted-copy workflow on every later save — the first of the two reasons " +
            "#898 is a non-bug, and the only one this file can detect a regression in");

        // ── second pass: redact again, then the REAL Ctrl+S path ─────────────
        vm.IsRedactionMode = true;
        vm.RedactionWorkflow.MarkArea(
            PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 560, 560, 640)), Secret);
        await vm.ApplyAllRedactionsCommand!.Execute();

        await vm.SaveFileCommand!.Execute();
        window.Close();

        var bytes = File.ReadAllBytes(copy);
        var combined = Encoding.Latin1.GetString(bytes) + Encoding.BigEndianUnicode.GetString(bytes);

        // A REGRESSION CHECK, NOT THE DISCRIMINATING EVIDENCE — labelled so
        // because I mutation-tested it and it has no teeth on its own.
        //
        // Forcing IsOriginalFile false (so Ctrl+S really does fall through to
        // SaveDocument without PrepareRedactedCopy) leaves this assertion
        // PASSING. The reason is the second, stronger answer to #898: the FIRST
        // pass already scrubbed /Info, XMP and the embedded file, so by the
        // second save there is no carrier left to skip.
        //
        // So #898 is a non-bug for two independent reasons — the guard still
        // applies (asserted above, and that assertion DOES have teeth), and
        // there is nothing left to scrub either way. Keeping this because it
        // pins the end-to-end property a reader of #898 actually cares about.
        combined.Should().NotContain(Secret,
            "the redacted copy must carry the term in no carrier — page content, /Info, " +
            "XMP, or an embedded file — after a second redaction saved through the " +
            "ordinary Ctrl+S path (#898)");
    }

    /// <summary>
    /// The control, and the reason the test above is meaningful: the fixture
    /// really does carry the secret in every carrier before anything runs. A
    /// fixture that never had the term in an attachment would let the test above
    /// pass on a build that scrubs nothing.
    /// </summary>
    [Fact]
    public void TheFixtureCarriesTheSecretInEveryCarrier()
    {
        var source = Path.Combine(_tempDir, "fixture-check.pdf");
        WriteFixture(source);

        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(source));
        raw.Should().Contain($"{Secret} on the page");
        raw.Should().Contain($"{Secret} in the title");
        raw.Should().Contain($"{Secret} in XMP");
        raw.Should().Contain($"{Secret} inside the attachment");

        using var doc = PdfDocument.Open(source);
        doc.HasEmbeddedFiles.Should().BeTrue(
            "the attachment must be reachable through /Names/EmbeddedFiles, or " +
            "ScrubEmbeddedFiles has nothing to find and the test above is vacuous");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Page content, /Info /Title, an XMP packet, and an embedded file — each
    /// carrying the term. Two text runs so there is something left to redact on
    /// the second pass.
    /// </summary>
    private static void WriteFixture(string path)
    {
        string content =
            $"BT /F1 24 Tf 60 700 Td ({Secret} on the page) Tj ET\n" +
            $"BT /F1 24 Tf 60 580 Td ({Secret} again lower down) Tj ET";
        string xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
            "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{Secret} in XMP</rdf:li>" +
            "</rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        string attachment = $"note.txt contents: {Secret} inside the attachment";

        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 7 0 R "
                + "/Names << /EmbeddedFiles << /Names [(note.txt) 8 0 R] >> >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Title ({Secret} in the title) >>\nendobj\n",
            $"7 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n",
            "8 0 obj\n<< /Type /Filespec /F (note.txt) /UF (note.txt) "
                + "/EF << /F 9 0 R >> >>\nendobj\n",
            $"9 0 obj\n<< /Type /EmbeddedFile /Subtype /text#2Fplain /Length {attachment.Length} >>\n"
                + $"stream\n{attachment}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R /Info 6 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
    }
}
