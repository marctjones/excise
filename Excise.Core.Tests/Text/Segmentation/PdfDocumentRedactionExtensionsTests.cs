using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public class PdfDocumentRedactionExtensionsTests
{
    /// <summary>
    /// Create a minimal valid PDF for testing with a simple content stream.
    /// </summary>
    private static PdfDocument OpenDoc(string contentStreamBody)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        string streamBody = contentStreamBody;
        long o4 = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(streamBody)} >>\nstream\n{streamBody}\nendstream\nendobj\n");
        long o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = sb.Length;
        sb.Append("xref\n0 6\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append($"{o4:D10} 00000 n \n");
        sb.Append($"{o5:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())), false);
    }

    [Fact]
    public void RedactText_NullDocument_ThrowsArgumentNullException()
    {
        var action = () => PdfDocumentRedactionExtensions.RedactText(null!, "test").VerifiedRemovals;

        action.Should().Throw<ArgumentNullException>().WithParameterName("document");
    }

    [Fact]
    public void RedactText_EmptySearchText_Returns0()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("").VerifiedRemovals;

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_NullSearchText_Returns0()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText(null!).VerifiedRemovals;

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_DocumentWithNoContentStream_Returns0()
    {
        var doc = OpenDoc("");

        var result = doc.RedactText("test").VerifiedRemovals;

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_DocumentWithNoText_Returns0()
    {
        var doc = OpenDoc("q 0 0 0 rg 100 100 50 50 re f Q");

        var result = doc.RedactText("Hello").VerifiedRemovals;

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_WithDrawBlackRectTrue_AppendsBlackRectangle()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello World) Tj ET");

        var originalPageOps = doc.GetPage(1).GetContentStream().Count;
        var result = doc.RedactText("Hello", drawBlackRect: true).VerifiedRemovals;

        var newPageOps = doc.GetPage(1).GetContentStream().Count;
        if (result > 0)
        {
            newPageOps.Should().BeGreaterThan(originalPageOps);
        }
    }

    [Fact]
    public void RedactText_WithDrawBlackRectFalse_DoesNotAppendRect()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello World) Tj ET");

        var result = doc.RedactText("Hello", drawBlackRect: false).VerifiedRemovals;

        if (result > 0)
        {
            doc.GetPage(1).GetContentStream().Operators
                .Should().NotContain(o => o.Name == "re", "no visual marker was requested");
        }
    }

    [Fact]
    public void RedactText_CaseSensitiveTrue_DoesNotMatchDifferentCase()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var resultLower = doc.RedactText("hello", caseSensitive: true).VerifiedRemovals;

        resultLower.Should().Be(0);
    }

    [Fact]
    public void RedactText_CaseSensitiveFalse_MatchesDifferentCase()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var resultLower = doc.RedactText("hello", caseSensitive: false).VerifiedRemovals;

        resultLower.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithCurlyQuote_FindsMatch()
    {
        var contentWithCurlyQuote = "BT /F1 12 Tf 100 700 Td (It's) Tj ET";
        var doc = OpenDoc(contentWithCurlyQuote);

        var result = doc.RedactText("It's", caseSensitive: false).VerifiedRemovals;

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithMultiplePages_RedactsAllPages()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");
        long o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");
        string stream1 = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        long o5 = sb.Length;
        sb.Append($"5 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(stream1)} >>\nstream\n{stream1}\nendstream\nendobj\n");
        string stream2 = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        long o6 = sb.Length;
        sb.Append($"7 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(stream2)} >>\nstream\n{stream2}\nendstream\nendobj\n");
        long o7 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = sb.Length;
        sb.Append("xref\n0 8\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append($"{o4:D10} 00000 n \n");
        sb.Append($"{o5:D10} 00000 n \n");
        sb.Append($"{o6:D10} 00000 n \n");
        sb.Append($"{o7:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");

        var doc = PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())), false);

        var result = doc.RedactText("Hello").VerifiedRemovals;

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithWhitespaceNormalization_MatchesCollapsedWhitespace()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello   World) Tj ET");

        var result = doc.RedactText("Hello World").VerifiedRemovals;

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void FindTextMatches_DoesNotJoinAHyphenWithinALine()
    {
        // #1372: excise does NOT rejoin a word wrapped across a line by a
        // hyphen, which is a known redaction gap — the wrapped occurrence
        // survives and is readable by poppler. This test pins the half that
        // must stay true whatever fixes that: a hyphen INSIDE a line is
        // content, so "well-known" must never match "wellknown". A naive
        // rejoin that ignores this, or that lets a match span two lines,
        // reintroduces #942 — measured: it destroyed remote content on 7
        // corpus fixtures and failed RedactingATerm_DestroysNothingRemote.
        var letters = new[]
        {
            new Letter("w", new PdfRectangle(100, 700, 107, 712), 12, "F1", 100, 700, 7, 'w'),
            new Letter("e", new PdfRectangle(107, 700, 114, 712), 12, "F1", 107, 700, 7, 'e'),
            new Letter("l", new PdfRectangle(114, 700, 118, 712), 12, "F1", 114, 700, 4, 'l'),
            new Letter("l", new PdfRectangle(118, 700, 122, 712), 12, "F1", 118, 700, 4, 'l'),
            new Letter("-", new PdfRectangle(122, 700, 126, 712), 12, "F1", 122, 700, 4, '-'),
            new Letter("k", new PdfRectangle(126, 700, 133, 712), 12, "F1", 126, 700, 7, 'k'),
            new Letter("n", new PdfRectangle(133, 700, 140, 712), 12, "F1", 133, 700, 7, 'n'),
        };

        PdfDocumentRedactionExtensions.FindTextMatches(letters, "wellkn", caseSensitive: false)
            .Should().BeEmpty("a same-line hyphen is content and must keep its meaning");

        // #1372's other half: it must not be REPORTED as a wrap either. The
        // detector below exists to surface hyphen-WRAPPED occurrences, and a
        // detector that also fires on ordinary hyphenated words would flood
        // every report with noise until people stopped reading it.
        PdfDocumentRedactionExtensions
            .FindHyphenWrappedCandidates(letters, "wellkn", caseSensitive: false, pageNumber: 1)
            .Should().BeEmpty("'well-known' is one line — there is no line break to rejoin across");
    }

    /// <summary>Two lines: "Ander-" wrapping onto "son", 14pt apart.</summary>
    private static Letter[] HyphenWrappedAnderson()
    {
        Letter L(string v, double x, double y, double w) =>
            new(v, new PdfRectangle(x, y, x + w, y + 12), 12, "F1", x, y, w, v[0]);

        return
        [
            L("A", 100, 700, 8), L("n", 108, 700, 7), L("d", 115, 700, 7),
            L("e", 122, 700, 7), L("r", 129, 700, 5), L("-", 134, 700, 4),
            // next line, 14pt lower — more than 0.5 * fontSize, so a new line
            L("s", 100, 686, 6), L("o", 106, 686, 7), L("n", 113, 686, 7),
            L(" ", 120, 686, 4), L("v", 124, 686, 6),
        ];
    }

    [Fact]
    public void FindHyphenWrappedCandidates_ReportsAWordSplitAcrossALineBreak()
    {
        // The occurrence FindTextMatches structurally cannot see: the page
        // really reads "Ander-" / "son", so no contiguous letter run spells
        // "Anderson" and the term is never removed. Reported, not joined —
        // joining makes the match span two lines and its removal box cover
        // everything between them, which is #942.
        var letters = HyphenWrappedAnderson();

        PdfDocumentRedactionExtensions.FindTextMatches(letters, "Anderson", caseSensitive: false)
            .Should().BeEmpty("sanity: this is precisely why the occurrence survives");

        var candidates = PdfDocumentRedactionExtensions
            .FindHyphenWrappedCandidates(letters, "Anderson", caseSensitive: false, pageNumber: 11);

        candidates.Should().ContainSingle();
        candidates[0].PageNumber.Should().Be(11);
        candidates[0].BeforeBreak.Should().Be("Ander");
        candidates[0].AfterBreak.Should().Be("son");
        candidates[0].ToString().Should().Be("\"Ander-\" / \"son\"",
            "the reviewer needs to see how the page actually reads");
    }

    [Fact]
    public void FindHyphenWrappedCandidates_IgnoresATermThatDoesNotStraddleTheBreak()
    {
        // Anti-vacuity: the detector must key on the term crossing the break,
        // not merely on a line-end hyphen being somewhere nearby. "son" lies
        // wholly on the second line, so FindTextMatches already handles it and
        // it is not an unmatched candidate.
        var letters = HyphenWrappedAnderson();

        PdfDocumentRedactionExtensions
            .FindHyphenWrappedCandidates(letters, "son", caseSensitive: false, pageNumber: 1)
            .Should().BeEmpty("a term contained in one line is matched normally, not a wrap candidate");
    }

    [Fact]
    public void AHyphenWrappedOccurrence_MakesTheReportNotCleanSuccess()
    {
        // The behaviour that matters to a user. Before #1372 a document with a
        // hyphen-wrapped occurrence reported plain success, which is how the
        // leak class stayed invisible: excise and mutool both keep the real
        // hyphen and never form the match, so a single-extractor check called
        // the file clean while poppler's de-hyphenating reflow read the term.
        var report = new RedactionReport
        {
            Term = "Anderson",
            Pages = [new PageRedactionResult(11, 1, 0, RedactionOutcome.RemovedVerified)],
            Carriers = [],
            HyphenatedCandidates = [new HyphenatedTermCandidate(11, "Ander", "son")],
        };

        report.Survived.Should().Be(0, "the occurrences excise DID match were removed and verified");
        report.IsCleanSuccess.Should().BeFalse(
            "zero survived among matched occurrences is not 'clean' when a readable " +
            "occurrence was never matched at all — that gap is exactly what excise " +
            "used to report as success (#1372)");
        report.ToString().Should().Contain("hyphen-wrapped occurrence(s) NOT removed");
    }

    [Fact]
    public void FindTextMatches_DoesNotIncludeLeadingWhitespaceFromAnotherPageBand()
    {
        var letters = new[]
        {
            new Letter(" ", new PdfRectangle(50, 50, 55, 60), 12, "F1", 50, 50, 5, 32),
            new Letter("F", new PdfRectangle(300, 700, 307, 712), 12, "F1", 300, 700, 7, 'F'),
            new Letter("o", new PdfRectangle(307, 700, 314, 712), 12, "F1", 307, 700, 7, 'o'),
            new Letter("r", new PdfRectangle(314, 700, 321, 712), 12, "F1", 314, 700, 7, 'r'),
            new Letter("m", new PdfRectangle(321, 700, 328, 712), 12, "F1", 321, 700, 7, 'm'),
        };

        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, "Form", false);

        matches.Should().ContainSingle();
        matches[0].Should().Equal(letters.Skip(1));
    }

    [Fact]
    public void FindTextMatches_RejectsAWordAssembledFromDistantRuns()
    {
        var letters = new[]
        {
            new Letter("Y", new PdfRectangle(160, 550, 168, 562), 12, "F1", 160, 550, 8, 'Y'),
            new Letter("o", new PdfRectangle(168, 550, 175, 562), 12, "F1", 168, 550, 7, 'o'),
            new Letter("u", new PdfRectangle(175, 550, 182, 562), 12, "F1", 175, 550, 7, 'u'),
            new Letter("r", new PdfRectangle(42, 522, 46, 532), 10, "F1", 42, 522, 4, 'r'),
        };

        PdfDocumentRedactionExtensions.FindTextMatches(letters, "your", false)
            .Should().BeEmpty();
    }

    [Fact]
    public void FindTextMatches_MapsStringOffsetsPastMultiCharacterGlyphs()
    {
        var target = new[]
        {
            new Letter("C", new PdfRectangle(100, 700, 107, 712), 12, "F1", 100, 700, 7, 'C'),
            new Letter("O", new PdfRectangle(107, 700, 114, 712), 12, "F1", 107, 700, 7, 'O'),
            new Letter("V", new PdfRectangle(114, 700, 121, 712), 12, "F1", 114, 700, 7, 'V'),
            new Letter("I", new PdfRectangle(121, 700, 128, 712), 12, "F1", 121, 700, 7, 'I'),
            new Letter("D", new PdfRectangle(128, 700, 135, 712), 12, "F1", 128, 700, 7, 'D'),
        };
        var letters = new[]
        {
            new Letter("fi", new PdfRectangle(10, 700, 20, 712), 12, "F1", 10, 700, 10, 1),
        }.Concat(target).ToList();

        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, "COVID", false);

        matches.Should().ContainSingle();
        matches[0].Should().Equal(target);
    }

    [Fact]
    public void RedactText_RealCanvasFixture_RemovesVisuallyOrderedWord()
    {
        // #1706 — the shared locator, not a hand-rolled walk to .git.
        var fixture = TestRepoLayout.FindFile("test-pdfs", "pdfjs", "canvas.pdf");
        Assert.SkipWhen(fixture == null,
            TestRepoLayout.AbsenceReason("canvas.pdf corpus fixture", "test-pdfs/pdfjs/canvas.pdf"));
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool not installed");

        MutoolTextOracle.ExtractAllPages(File.ReadAllBytes(fixture!)).Should().Contain("styles",
            "the independent oracle must see the regression term before redaction");

        using var doc = PdfDocument.Open(fixture!);
        var raw = doc.Pages.Sum(page => PdfDocumentRedactionExtensions
            .FindTextMatches(page.Letters, "styles", false).Count);
        raw.Should().Be(2,
            "normal within-word glyph-bound gaps must not be treated as whitespace (#1198)");
        doc.RedactText("styles", drawBlackRect: false).VerifiedRemovals.Should().Be(2,
            "both visible occurrences must be structurally removed (#1198)");
        doc.GetPage(1).Text.Should().NotContain("styles", "the first visible occurrence must be structurally removed");
        doc.GetPage(2).Text.Should().NotContain("styles", "the second visible occurrence must be structurally removed");
        using var saved = new MemoryStream();
        doc.Save(saved);
        MutoolTextOracle.ExtractAllPages(saved.ToArray()).Should().NotContain("styles",
            "MuPDF must independently agree that neither visible occurrence remains in the saved PDF");
    }

    [Fact]
    public void FindTextMatches_RealFreecultureFixture_DoesNotInventWordBreakInsideVisibleThat()
    {
        // #1706 — the shared locator, not a hand-rolled walk to .git.
        var fixture = TestRepoLayout.FindFile("test-pdfs", "pdfjs", "freeculture.pdf");
        Assert.SkipWhen(fixture == null,
            TestRepoLayout.AbsenceReason("freeculture.pdf corpus fixture", "test-pdfs/pdfjs/freeculture.pdf"));

        using var doc = PdfDocument.Open(fixture!);
        var letters = doc.GetPage(201).Letters;

        // 244.70 (was 243.02 before #1391). The old number was excise's own
        // pre-fix output, and it was WRONG: this page kerns with TJ under a
        // scaled Tm, so the §9.4.3 raw-adjustment defect displaced it by
        // 1.68pt. Re-derived from mutool rather than from excise's new output,
        // because a coordinate baseline re-recorded from the tool under test
        // proves only that the tool is self-consistent.
        //
        // mutool reads 11 occurrences of "that" on this page and so does
        // excise; every pair agrees to 0.02pt once the page-box origin is
        // accounted for (mutool reports CropBox-relative, excise
        // MediaBox-relative, and this page's CropBox is l=41.76):
        //     mutool 202.90 + 41.76 = 244.66   ← this occurrence
        //     mutool 270.89 + 41.76 = 312.65, 162.92 → 204.68, … all 11 match.
        // The old 243.02 would require a mutool x of 201.26, which mutool does
        // not report anywhere on the page.
        PdfDocumentRedactionExtensions.FindTextMatches(letters, "that", false)
            .Should().Contain(match => match.Count == 4 &&
                Math.Abs(match[0].StartX - 244.70) < 0.1,
                "the visible word is split across text operators but has no word break (#1198)");
    }

    [Fact]
    public void RedactText_TightlyLedLines_DoesNotRemoveTheAdjacentLine()
    {
        using var doc = OpenDoc(
            "BT /F1 1 Tf 10 0 0 10 50 700 Tm " +
            "(your target) Tj 0 -0.95 Td (remote line survives) Tj ET");

        doc.RedactText("your", drawBlackRect: false).VerifiedRemovals.Should().Be(1);

        doc.GetPage(1).Text.Should().NotContain("your");
        doc.GetPage(1).Text.Should().Contain("remote line survives");
    }

    [Fact]
    public void RedactText_DoesNotThrowOnValidInput()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Test) Tj ET");

        var action = () => doc.RedactText("Test").VerifiedRemovals;

        action.Should().NotThrow();
    }

    [Fact]
    public void RedactText_ReturnsNonNegativeCount()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("Hello").VerifiedRemovals;

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithStrategy_AcceptsGlyphRemovalStrategy()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("Hello", strategy: GlyphRemovalStrategy.AnyOverlap).VerifiedRemovals;

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_NonExistentText_Returns0()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("Nonexistent").VerifiedRemovals;

        result.Should().Be(0);
    }
}
