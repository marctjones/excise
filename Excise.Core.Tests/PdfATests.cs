using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Text.Segmentation;   // RedactText (#1499)
using Excise.TestSupport;              // SavedPdfLeakScanner (#1499)
using Xunit;

namespace Excise.Core.Tests;

/// <summary>
/// Verifies <see cref="PdfDocumentBuilder.PdfA"/> emits the document-level
/// structures PDF/A requires: an XMP packet with the pdfaid identifier, an sRGB
/// OutputIntent, a trailer /ID, an embedded font (no base-14), and — for
/// subset CID fonts — a /CIDSet (required by PDF/A-1b §6.3.5). Full conformance
/// is validated with veraPDF when present (both PDF/A-1b and -2b PASS). Uses
/// the DejaVu Sans fixture embedded in this assembly (#603).
/// </summary>
public class PdfATests
{
    private static byte[] BuildPdfA(PdfAConformance conformance)
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        return PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title("Archival Test")
            .DefaultFont(font)
            .PdfA(conformance)
            .Heading("Archival Test")
            .Paragraph("Body text — with an em dash and unicode: café.")
            .SaveToBytes();
    }

    [Fact]
    public void PdfA2B_EmitsXmpPdfaId_OutputIntent_AndTrailerId()
    {
        var latin1 = Encoding.Latin1.GetString(BuildPdfA(PdfAConformance.PdfA2B));

        Assert.Contains("pdfaid:part>2", latin1);
        Assert.Contains("pdfaid:conformance>B", latin1);
        Assert.Contains("/OutputIntents", latin1);
        Assert.Contains("GTS_PDFA1", latin1);
        Assert.Contains("/ID", latin1);
    }

    [Fact]
    public void PdfA1B_EmitsPart1AndCidSet()
    {
        var latin1 = Encoding.Latin1.GetString(BuildPdfA(PdfAConformance.PdfA1B));

        Assert.Contains("pdfaid:part>1", latin1);
        // PDF/A-1b requires a /CIDSet for the embedded subset CID font.
        Assert.Contains("/CIDSet", latin1);
    }

    [Fact]
    public void NewDocument_AlwaysGetsATrailerId()
    {
        var bytes = PdfDocumentBuilder.Create().Heading("Hi").SaveToBytes();
        Assert.Contains("/ID", Encoding.Latin1.GetString(bytes));
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    public async Task PdfA_Output_IsConformant_PerVeraPdf(PdfAConformance conformance, string flavour)
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_{flavour}_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildPdfA(conformance));
        try
        {
            var psi = new ProcessStartInfo(verapdf!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("xml");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(flavour);
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi)!;
            // #925: stderr is redirected, so it MUST be drained concurrently —
            // veraPDF is chatty there, and a full 64KB pipe wedges the child,
            // which wedges ReadToEnd, which trips CI's 2-minute blame timer as
            // a "test host crash" on an innocent commit.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            }
            string report = await stdoutTask;
            _ = await stderrTask;

            report.Should().Contain("isCompliant=\"true\"",
                $"the builder's PdfA({conformance}) output must be PDF/A-{flavour} conformant. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 4000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PdfA2B_RoundTripThroughCompressedWriter_IsConformant_PerVeraPdf()
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        using var doc = PdfDocument.Open(BuildPdfA(PdfAConformance.PdfA2B));
        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().Contain("/Type /ObjStm",
            "PDF/A-2 permits object streams, so this validates the compressed writer path");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_2b_compressed_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        try
        {
            var report = RunVeraPdf(verapdf!, path, "2b");
            report.Should().Contain("isCompliant=\"true\"",
                "compressed writer output must remain PDF/A-2b conformant. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 4000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// #1444: <c>PdfA()</c> plus AcroForm fields. veraPDF reported 6.3.3 (a widget
    /// without an appearance dictionary), 6.3.2 (a widget without /F) and 6.4.1
    /// (NeedAppearances true) for a builder document with one text field, while
    /// the XMP still claimed PDF/A. Text (single and multiline), checkbox (on and
    /// off) and dropdown fields are here, with and without a value.
    /// <para>#1498 added the date field. It used to be deliberately absent: it
    /// carries JavaScript <c>/AA</c> format and keystroke actions, which PDF/A
    /// forbids outright (ISO 19005-1 §6.6.1; veraPDF reports 6.5.1 / 6.6.2)
    /// independently of appearance streams. The PDF/A pre-save pass now removes
    /// them, so the combination conforms — at the cost of the viewer-side
    /// format enforcement.</para>
    /// </summary>
    private static byte[] BuildPdfAWithFormFields(PdfAConformance conformance)
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        return PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title("Archival Form")
            .DefaultFont(font)
            .PdfA(conformance)
            .Heading("Archival Form")
            .Paragraph("Fields authored by the builder.")
            .TextField("Name", "name", defaultValue: "Ada Lovelace")
            .TextField("Notes", "notes", multiline: true)
            .DateField("Date of birth", "dob", format: "yyyy-mm-dd")
            .CheckBox("Subscribe", "subscribe", checkedByDefault: true)
            .CheckBox("Opt out", "optout")
            .Dropdown("Colour", new[] { "Red", "Green" }, "colour", defaultValue: "Green")
            .SaveToBytes();
    }

    /// <summary>
    /// #1498 — the structural half of the date-field fix, readable without
    /// veraPDF installed: <c>PdfA()</c> output carries no JavaScript action at
    /// all, and the date field survives as an ordinary text field whose tooltip
    /// still names the expected format.
    /// </summary>
    [Fact]
    public void PdfA_StripsTheDateFieldsJavaScriptActions_ButKeepsTheField()
    {
        var bytes = BuildPdfAWithFormFields(PdfAConformance.PdfA2B);
        using var doc = PdfDocument.Open(bytes);

        var dob = doc.GetAcroForm()!.FindField("dob");
        dob.Should().NotBeNull("stripping the actions must not remove the field");
        dob!.RawDictionary.GetOptional("AA").Should().BeNull(
            "a widget/field dictionary may not carry /AA AT ALL — ISO 19005-1 6.6.1#3 and 6.6.2, " +
            "ISO 19005-2 6.4.1#1 and #2 ban the key, not merely JavaScript inside it");
        dob.RawDictionary.GetOptional("A").Should().BeNull(
            "the same rules ban /A on a widget");
        dob.RawDictionary.GetStringOrNull("TU").Should().Contain("yyyy-mm-dd",
            "the format hint is what the user is left with once the enforcement is stripped");

        // Scanned rather than string-matched: the writer compresses, so a raw
        // NotContain over the saved bytes would pass vacuously on a file that
        // still carried the action inside a /FlateDecode object stream.
        SavedPdfLeakScanner.FindTerm(bytes, "AFDate_").Should().BeEmpty(
            "no AFDate action may survive anywhere in a PDF/A file, in any carrier");
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    public void PdfA_WithFormFields_IsConformant_PerVeraPdf(PdfAConformance conformance, string flavour)
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_forms_{flavour}_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildPdfAWithFormFields(conformance));
        try
        {
            var report = RunVeraPdf(verapdf!, path, flavour);
            report.Should().Contain("isCompliant=\"true\"",
                $"PdfA({conformance}) with form fields must be PDF/A-{flavour} conformant, not just claim it. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 6000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// #1499 — redacting a form field value must not cost the file its PDF/A
    /// conformance. The scrub used to end with <c>/NeedAppearances true</c>
    /// whenever anything changed, which ISO 19005-2 6.4.1 forbids: excise
    /// happily produced a file whose XMP still claimed PDF/A and which veraPDF
    /// rejected.
    ///
    /// <para>Two oracles, neither of them excise: veraPDF for the conformance
    /// claim (a writer must not grade its own output), and
    /// <see cref="SavedPdfLeakScanner"/> over the SAVED BYTES — including
    /// inside compressed streams — for the redaction claim. The appearance
    /// stream draws the field value as glyphs, so "still conformant" and "the
    /// term is gone" have to be asserted together: keeping an appearance that
    /// still draws the name would satisfy veraPDF perfectly.</para>
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    public void PdfA_WithARedactedFieldValue_StaysConformant_AndLeaksNothing(
        PdfAConformance conformance, string flavour)
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        byte[] redacted;
        using (var doc = PdfDocument.Open(BuildPdfAWithFormFields(conformance)))
        {
            // Guard: the value is really there to begin with, so a green run
            // cannot come from having redacted nothing.
            doc.GetAcroForm()!.FindField("name")!.Value.Should().Contain("Lovelace");

            doc.RedactText("Lovelace");
            redacted = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(redacted, "Lovelace").Should().BeEmpty(
            "the redacted term must be gone from every carrier — /V, the /AP appearance stream, " +
            "and anything the scrub left behind");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_redacted_{flavour}_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, redacted);
        try
        {
            var report = RunVeraPdf(verapdf!, path, flavour);
            report.Should().Contain("isCompliant=\"true\"",
                $"a redacted PDF/A-{flavour} form must still BE PDF/A, not just claim it. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 6000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// #1507 — a PDF/A document whose <c>/Info /Title</c>, XMP <c>dc:title</c>
    /// (the builder writes the title into both) and page text all name the same
    /// string. The area redaction below covers that word on the page; the
    /// wholesale carrier strip is what deals with the other two.
    /// </summary>
    private static byte[] BuildPdfAWithCanary(PdfAConformance conformance)
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        return PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title($"Archival {AreaCanary} Test")
            .DefaultFont(font)
            .PdfA(conformance)
            .Heading("Archival Test")
            .Paragraph($"Body naming {AreaCanary} once.")
            .SaveToBytes();
    }

    private const string AreaCanary = "CANARYNAME";

    /// <summary>
    /// The union of the glyph boxes of <paramref name="term"/> on
    /// <paramref name="page"/> — what a user's drag over that word yields. excise's
    /// own extraction picks the GEOMETRY here, which is fine: no assertion in the
    /// test depends on excise's opinion of what is inside the box.
    /// </summary>
    private static PdfRectangle GlyphBoxOf(PdfPage page, string term)
    {
        var letters = page.Letters;
        var text = new StringBuilder();
        var owner = new List<Excise.Core.Text.Letter>();
        foreach (var letter in letters)
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

    /// <summary>
    /// #1507 — an AREA redaction must not cost a PDF/A file its conformance.
    ///
    /// <para>Until this fix it always did, and nothing said so: <c>RedactArea</c>
    /// strips the positionless document carriers wholesale (#897, correct — it
    /// has no term to scrub them by), and that strip removed the catalog
    /// <c>/Metadata</c> stream, which is where <c>pdfaid:part</c> lives. veraPDF
    /// then fails the file on <c>containsMetadata</c> and
    /// <c>containsPDFAIdentification</c> — the two rules every part of ISO 19005
    /// has. The strip now re-emits an identification-only packet.</para>
    ///
    /// <para>Two oracles, neither of them excise, asserted TOGETHER on purpose:
    /// veraPDF for conformance, and <see cref="SavedPdfLeakScanner"/> over the
    /// saved bytes for the redaction. Keeping the whole XMP packet would satisfy
    /// veraPDF perfectly and leave the canary sitting in <c>dc:title</c>, so
    /// either assertion alone can be passed by a wrong fix.</para>
    ///
    /// <para>⚠️ The scanner cannot see the PAGE glyphs here — the builder embeds
    /// the font as Identity-H, so the word is a run of two-byte GIDs, not text.
    /// That half is mutool's job, in
    /// <c>Excise.Rendering.Tests/Differential/PdfAConformanceConservationTests</c>.
    /// What the scanner covers here is the carriers the strip owns.</para>
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    public void PdfA_WithAnAreaRedaction_StaysConformant_AndLeaksNothing(
        PdfAConformance conformance, string flavour)
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        var authored = BuildPdfAWithCanary(conformance);
        // Guard: the canary really is in a carrier the strip has to deal with, so
        // a green leak scan cannot come from having stripped nothing.
        SavedPdfLeakScanner.FindTerm(authored, AreaCanary).Should().NotBeEmpty(
            "/Info /Title and the XMP dc:title both name it before redaction");

        byte[] redacted;
        using (var doc = PdfDocument.Open(authored))
        {
            doc.TargetsPdfA.Should().BeTrue("PdfA() writes the pdfaid XMP");
            doc.GetPage(1).RedactArea(GlyphBoxOf(doc.GetPage(1), AreaCanary));
            doc.TargetsPdfA.Should().BeTrue(
                "the carrier strip must leave the identification in place, not just the file");
            redacted = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(redacted, AreaCanary).Should().BeEmpty(
            "the positionless carriers must be gone — an area redaction cannot name the term, " +
            "so the strip removes the carrier rather than guessing at its contents (#897)");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_area_{flavour}_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, redacted);
        try
        {
            var report = RunVeraPdf(verapdf!, path, flavour);
            report.Should().Contain("isCompliant=\"true\"",
                $"an area-redacted PDF/A-{flavour} file must still BE PDF/A. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 6000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// #1507's second, quieter consequence — readable without veraPDF installed.
    /// PDF/A-1 forbids object streams, and
    /// <c>PdfDocumentWriter.ShouldUseCompressedObjects</c> decides by reading
    /// <c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c> back out of the XMP. With
    /// the packet deleted by the carrier strip, an area-redacted PDF/A-1 file was
    /// written WITH object streams: conformance lost twice, once for the missing
    /// identification and once for a construct the writer would never have
    /// emitted had it known what the file was.
    /// </summary>
    [Fact]
    public void AreaRedaction_OnAPdfA1_StillSuppressesObjectStreams()
    {
        using var doc = PdfDocument.Open(BuildPdfAWithCanary(PdfAConformance.PdfA1B));
        doc.GetPage(1).RedactArea(GlyphBoxOf(doc.GetPage(1), AreaCanary));

        var redacted = doc.SaveToBytes();
        Encoding.Latin1.GetString(redacted).Should().NotContain("/Type /ObjStm",
            "PDF/A-1 forbids object streams; the writer only knows to suppress them while the " +
            "pdfaid identification is still readable in the file it is writing");
    }

    private static string RunVeraPdf(string verapdf, string path, string flavour)
    {
        var psi = new ProcessStartInfo(verapdf)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("xml");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(flavour);
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
        var report = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        return report;
    }

    private static string? FindVeraPdf()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var local = Path.Combine(home, "verapdf", "verapdf");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "verapdf");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
