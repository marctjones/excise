using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1587 — the recovery report checked against tools that are NOT excise.
///
/// <para><b>Why these exist separately from <see cref="RecoveryChannelTests"/>.</b>
/// Those tests assert that excise's channels report what excise's fixture
/// builder put in the file. Both halves are ours, so they can only prove
/// self-consistency: if excise misunderstands a carrier's shape, the fixture
/// and the channel misunderstand it together and the suite stays green over a
/// document no other tool reads that way. The rule this repository learned the
/// hard way — <i>a tool must not be its own oracle for the property it exists
/// to guarantee</i> — applies to the recovery side exactly as it does to the
/// redaction side.</para>
///
/// <para>Three independent witnesses, each answering a different question:
/// <b>qpdf</b> that the fixture is a structurally valid PDF and not something
/// only excise will parse; <b>mutool</b> (MuPDF) and <b>pdftotext</b> (Poppler)
/// that the leaked text really is readable out of the file by an unrelated
/// engine; and <b>mutool stext</b> that the POSITION excise reports for it is
/// where the glyphs actually are.</para>
/// </summary>
public class RecoveryOracleTests
{
    private const double PageHeight = 792;

    [Fact]
    public void Fixtures_AreStructurallyValidPdfs()
    {
        Assert.SkipUnless(ToolAvailable("qpdf"), "qpdf is not on PATH");

        // A hand-written fixture that only excise can parse would make every
        // other test in this folder meaningless.
        var fixtures = new (string Name, byte[] Bytes)[]
        {
            ("text under box", RecoveryFixtureBuilder.TextUnderBox("MANAFORT")),
            ("marked-content carrier", RecoveryFixtureBuilder.MarkedContentCarrierUnderBox("HARPER")),
            ("named property list", RecoveryFixtureBuilder.MarkedContentCarrierUnderBox("HANSEN", namedPropertyList: true)),
            ("form field value", RecoveryFixtureBuilder.FormFieldValueUnderBox("ssn", "123-45-6789")),
            ("unapplied /Redact", RecoveryFixtureBuilder.UnappliedRedactAnnotation("CONFIDENTIAL")),
            ("image under box", RecoveryFixtureBuilder.ImageUnderBox()),
            ("vector under box", RecoveryFixtureBuilder.VectorUnderBox()),
        };

        foreach (var (name, bytes) in fixtures)
        {
            var (exitCode, output) = Run("qpdf", bytes, "--check", "{file}");
            exitCode.Should().Be(0, $"qpdf --check must accept the '{name}' fixture, but said: {output}");
        }
    }

    [Fact]
    public void HiddenTextRecovery_IsCorroboratedByTwoIndependentExtractors()
    {
        Assert.SkipUnless(ToolAvailable("mutool"), "mutool is not on PATH");
        Assert.SkipUnless(ToolAvailable("pdftotext"), "pdftotext is not on PATH");

        var bytes = RecoveryFixtureBuilder.TextUnderBox("MANAFORT");
        using var doc = PdfDocument.Open(bytes);
        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.HiddenText);

        finding.Text.Should().Be("MANAFORT");

        // MuPDF and Poppler are different engines. A single extractor cannot
        // report its own blind spot (#1372), and "excise says the text is
        // there" is precisely the claim under test.
        MutoolTextOracle.ExtractAllPages(bytes).Should().Contain("MANAFORT");
        PdftotextExtract(bytes).Should().Contain("MANAFORT");
    }

    [Fact]
    public void TheReportedLocation_MatchesWhereMuPdfSeesTheGlyphs()
    {
        Assert.SkipUnless(MutoolStextOracle.IsAvailable, "mutool is not on PATH");

        var bytes = RecoveryFixtureBuilder.TextUnderBox("MANAFORT");
        using var doc = PdfDocument.Open(bytes);
        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.HiddenText);

        var word = MutoolStextOracle.Words(bytes, 1, PageHeight)
            .Single(w => w.Text == "MANAFORT");
        var box = finding.Location!.Rect.Normalize();

        // HORIZONTAL is the axis that is comparable, and it is also the one the
        // claim rests on: a restored copy draws into this box (#1588), the
        // width residue reasons about its span (#1589), and the mark linkage is
        // decided on it. Two points is under one glyph width at 14pt.
        box.Left.Should().BeApproximately(word.Left, 2.0);
        box.Right.Should().BeApproximately(word.Right, 2.0);

        // VERTICAL is looser ON PURPOSE, because the two tools box glyphs
        // differently and neither is wrong. What they are is DIFFERENT, and
        // this comment used to say which in a way that was false (#1618):
        //
        //   MuPDF's stext quad   the FONT's em box, ascender to descender
        //   excise's box         the §9.4.4 GLYPH CELL — the unit square under
        //                        the text and font matrices, which is what the
        //                        walker hands the sink and why the provenance
        //                        reads "glyph boxes"
        //
        // A cell is NOT an ink box, and the difference is measurable on this
        // fixture: at `/F1 14 Tf 72 700 Td`, excise's top is 714.0 — baseline
        // plus font size, EXACTLY — while MuPDF puts the ascender at 710.4.
        // The old bound asserted excise's box lay INSIDE MuPDF's, which a cell
        // taller than the ascender can never satisfy; it was a false premise
        // that happened to be untested until the first machine ran it.
        //
        // So pin what is actually true and still catches the failure this axis
        // is for — a flipped origin, or a box on the wrong line: the two boxes
        // describe the SAME LINE, i.e. they overlap vertically, and excise's
        // is the right way up.
        //
        // ⚠️ The cell model has a consequence this fixture cannot see, because
        // MANAFORT is all caps: a cell runs from the baseline UP, so a
        // descending glyph's ink falls BELOW its own cell. A restored copy
        // (#1588) drawn into this box would clip the tail off a 'g'. That is
        // the live half of #1618 and it is a geometry question, not a bound.
        box.Top.Should().BeGreaterThan(box.Bottom, "the box must not be inverted");
        box.Bottom.Should().BeLessThan(word.Top, "same line — excise's box starts below MuPDF's top");
        box.Top.Should().BeGreaterThan(word.Bottom, "same line — excise's box ends above MuPDF's bottom");

        // The cell sits on the baseline and is exactly one em tall, so its top
        // is above MuPDF's ascender and never by more than the ascender's
        // shortfall against the full em (~4pt at 14pt Helvetica).
        box.Top.Should().BeGreaterThanOrEqualTo(word.Top,
            "a full-em cell reaches above the ascender MuPDF reports");
        (box.Top - word.Top).Should().BeLessThan(5.0,
            "but only by the em/ascender difference — more than that is a real divergence");
    }

    [Fact]
    public void TheMarkRectangle_ContainsWhereMuPdfSeesTheGlyphs()
    {
        Assert.SkipUnless(MutoolStextOracle.IsAvailable, "mutool is not on PATH");

        // The mark is read from the content stream's fill geometry, the glyph
        // positions from MuPDF's own layout. Nothing forces them to agree
        // except the file actually having a box over that text.
        var bytes = RecoveryFixtureBuilder.TextUnderBox("MANAFORT");
        using var doc = PdfDocument.Open(bytes);
        var report = RecoveryScanner.Scan(doc);
        var mark = report.Marks.Single(m => m.Outcome == MarkRecoveryOutcome.Recovered).Mark;

        var word = MutoolStextOracle.Words(bytes, 1, PageHeight).Single(w => w.Text == "MANAFORT");
        mark.Rect.Left.Should().BeLessThanOrEqualTo(word.Left + 1);
        mark.Rect.Right.Should().BeGreaterThanOrEqualTo(word.Right - 1);
    }

    [Fact]
    public void CarrierRecovery_IsReadBackByAnIndependentEngineOneExtractorCannotSee()
    {
        Assert.SkipUnless(ToolAvailable("mutool"), "mutool is not on PATH");
        Assert.SkipUnless(ToolAvailable("pdftotext"), "pdftotext is not on PATH");

        // MEASURED, and not what this test first asserted. The glyphs really
        // were removed, so the obvious expectation is that no extractor reads
        // HARPER off the page. That is true of MuPDF and FALSE of Poppler:
        // pdftotext honours the inline /ActualText and prints the name, while
        // mutool prints the two blank glyphs that are actually painted.
        //
        // The asymmetry is the finding, and it is the whole argument for
        // reading this carrier. The leak is not dormant bytes waiting for a
        // hex editor -- it is live text in a mainstream extractor, invisible to
        // the other. An audit that consulted one engine would report this page
        // clean or leaking depending purely on which engine it happened to
        // pick, which is the #1372 lesson stated on the recovery side: a single
        // extractor cannot report its own blind spot.
        var bytes = RecoveryFixtureBuilder.MarkedContentCarrierUnderBox("HARPER");
        using var doc = PdfDocument.Open(bytes);
        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.MarkedContent);

        finding.Text.Should().Be("HARPER");

        MutoolTextOracle.ExtractAllPages(bytes).Should().NotContain("HARPER",
            "MuPDF reads the painted glyphs, which the redaction did remove");
        PdftotextExtract(bytes).Should().Contain("HARPER",
            "Poppler honours the inline /ActualText -- an independent engine reads " +
            "the recovered value straight out, so the finding is not an excise artefact");

        // And it is in the bytes, which is what makes the finding CERTAIN
        // rather than a guess.
        SavedPdfLeakScanner.FindTerm(bytes, "HARPER").Should().NotBeEmpty();
    }

    [Fact]
    public void FormFieldRecovery_ReportsAValueAReaderWillRePaintOntoThePage()
    {
        Assert.SkipUnless(ToolAvailable("mutool"), "mutool is not on PATH");

        // MEASURED, and worse than the docstring first assumed. The widget has
        // no appearance stream and the page content is a black box, so the
        // expectation was that no extractor sees the value. MuPDF instead
        // REGENERATES the appearance from /V and reads the value straight back
        // out -- the "redacted" number is not merely present in the file, it is
        // painted onto the page by a mainstream reader.
        //
        // That is the failure mode stated at full strength: /V is authoritative
        // and every reader honouring /NeedAppearances will re-render it. The
        // recovery channel is reporting a leak that is visible, not latent.
        var bytes = RecoveryFixtureBuilder.FormFieldValueUnderBox("ssn", "123-45-6789");
        using var doc = PdfDocument.Open(bytes);
        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.FormField);

        finding.Text.Should().Be("123-45-6789");
        MutoolTextOracle.ExtractAllPages(bytes).Should().Contain("123-45-6789",
            "MuPDF regenerates the widget appearance from /V, so the value is " +
            "re-rendered onto the page a redaction thought it had cleared");
        SavedPdfLeakScanner.FindTerm(bytes, "123-45-6789").Should().NotBeEmpty();
    }

    private static string PdftotextExtract(byte[] pdf)
    {
        var (_, output) = Run("pdftotext", pdf, "{file}", "-");
        return output;
    }

    /// <summary>
    /// Run a tool over <paramref name="pdf"/>. "{file}" in the arguments is
    /// replaced by the temporary path.
    /// </summary>
    /// <summary>
    /// #1707 — the leak is real, and a tool that is not excise says so.
    ///
    /// <para>The exit code this fixture drives was wrong for a reason excise
    /// could not see about itself: excise found the image, reported it, and
    /// then graded the document clean. A gate built only from excise's own
    /// reading of its own fixture cannot establish that there was anything to
    /// find. <c>pdfimages -list</c> enumerates the image XObjects a PDF
    /// actually contains — it knows nothing about marks or redaction — so a
    /// row here is independent evidence that an opaque box was painted OVER an
    /// intact 2x2 image rather than in place of it.</para>
    /// </summary>
    [Fact]
    public void ImageUnderBox_TheXObjectSurvives_ConfirmedByPdfimages()
    {
        Assert.SkipUnless(ToolAvailable("pdfimages"), "pdfimages (Poppler) is not on PATH");

        var bytes = RecoveryFixtureBuilder.ImageUnderBox();
        var (exitCode, output) = Run("pdfimages", bytes, "-list", "{file}");

        exitCode.Should().Be(0, $"pdfimages must read the fixture, but said: {output}");
        output.Should().MatchRegex(@"\bimage\b",
            "Poppler must list an image XObject in the file the box was painted over: " + output);
        output.Should().MatchRegex(@"\b2\s+2\b",
            "and it is the 2x2 original, still at full size, not a blanked replacement: " + output);
    }

    private static (int ExitCode, string Output) Run(string tool, byte[] pdf, params string[] arguments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-oracle-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var start = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument.Replace("{file}", path, StringComparison.Ordinal));

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"{tool} did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{tool} exceeded 30 seconds");
            }
            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool ToolAvailable(string tool)
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(directory => !string.IsNullOrWhiteSpace(directory)
                              && File.Exists(Path.Combine(directory, tool)));
}
