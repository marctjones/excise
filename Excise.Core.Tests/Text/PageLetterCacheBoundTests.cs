using System.Collections.Concurrent;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// The per-document bound on cached page letters (#1485).
/// </summary>
/// <remarks>
/// <para>Until #1485 a page kept its letters for the document's lifetime once
/// anything read them, and the App's text index read every page: 595,373
/// letters retained on irs-1040-instructions.pdf. The bound drops the letters
/// of pages outside the most recently used few. That is only safe because a
/// dropped cache is re-filled by walking the page's CURRENT bytes, so these
/// tests pin both halves: the bound holds, and what comes back after an
/// eviction — and what redaction removes — is exactly what the cache held.</para>
/// <para>The redaction checks read the saved bytes and an independent extractor
/// (mutool), not excise's own re-read: excise agreeing with itself is not
/// evidence that a term is gone.</para>
/// </remarks>
public class PageLetterCacheBoundTests
{
    private const int Capacity = PdfDocument.PageLetterCacheCapacity;

    [Fact]
    public void Letters_ReadOnEveryPage_KeepsOnlyTheMostRecentlyUsedPagesCached()
    {
        using var doc = PdfDocument.Open(MultiPagePdf(12, p => $"Marker{p} common words"));

        for (int p = 1; p <= doc.PageCount; p++)
            doc.GetPage(p).Letters.Should().NotBeEmpty();

        var cached = Enumerable.Range(1, doc.PageCount).Where(p => doc.GetPage(p).HasCachedLetters).ToList();
        cached.Should().Equal(Enumerable.Range(doc.PageCount - Capacity + 1, Capacity),
            "only the most recently used pages keep their letters");
    }

    [Fact]
    public void Letters_ReadAgainWithinTheBound_ReturnsTheSameList()
    {
        using var doc = PdfDocument.Open(MultiPagePdf(Capacity, p => $"Marker{p}"));
        var first = doc.GetPage(1).Letters;

        for (int p = 2; p <= Capacity; p++)
            _ = doc.GetPage(p).Letters;

        doc.GetPage(1).Letters.Should().BeSameAs(first,
            "reading no more pages than the bound must not evict anything");
    }

    [Fact]
    public void Letters_MostRecentlyUsed_IsNotEvictedByLaterPages()
    {
        using var doc = PdfDocument.Open(MultiPagePdf(Capacity + 2, p => $"Marker{p}"));
        var page1 = doc.GetPage(1).Letters;

        for (int p = 2; p <= Capacity + 2; p++)
        {
            _ = doc.GetPage(p).Letters;
            doc.GetPage(1).Letters.Should().BeSameAs(page1, "page 1 is re-read, so it stays most recently used");
        }
    }

    [Fact]
    public void Letters_AfterEviction_ReWalkToTheSameValuesAndGeometry()
    {
        using var doc = PdfDocument.Open(MultiPagePdf(Capacity + 1, p => $"Evict{p} (x) y"));
        var before = doc.GetPage(1).Letters;
        var wordsBefore = doc.GetPage(1).GetWords();

        for (int p = 2; p <= Capacity + 1; p++)
            _ = doc.GetPage(p).Letters;
        doc.GetPage(1).HasCachedLetters.Should().BeFalse("page 1 is the least recently used");

        var after = doc.GetPage(1).Letters;
        after.Should().NotBeSameAs(before, "an evicted page is walked again");
        Fingerprint(after).Should().Equal(Fingerprint(before),
            "a re-walk of unchanged bytes must produce the letters the cache held, bit for bit");

        var wordsAfter = doc.GetPage(1).GetWords();
        wordsAfter.Should().NotBeSameAs(wordsBefore, "words hold letters and are evicted with them");
        wordsAfter.Select(w => (w.Text, Bits(w.BoundingBox))).Should()
            .Equal(wordsBefore.Select(w => (w.Text, Bits(w.BoundingBox))));
    }

    [Fact]
    public void ExtractTextAndWordsWithoutRetainingLetters_MatchesTextAndGetWords_AndCachesNoLetters()
    {
        var bytes = MultiPagePdf(Capacity + 3, p => $"Index{p} alpha beta");
        using var indexed = PdfDocument.Open(bytes);
        using var reference = PdfDocument.Open(bytes);

        for (int p = 1; p <= indexed.PageCount; p++)
        {
            var (text, words, _) = indexed.GetPage(p).ExtractTextAndWordsWithoutRetainingLetters();

            text.Should().Be(reference.GetPage(p).Text);
            words.Select(w => (w.Text, Bits(w.BoundingBox))).Should()
                .Equal(reference.GetPage(p).GetWords().Select(w => (w.Text, Bits(w.BoundingBox))));
        }

        Enumerable.Range(1, indexed.PageCount).Should()
            .NotContain(p => indexed.GetPage(p).HasCachedLetters,
                "the index path must leave no page holding its letters");
    }

    /// <summary>
    /// Redaction reads <see cref="PdfPage.Letters"/> between rewrites. A cache
    /// that survived a rewrite would hand the next pass, the verification
    /// count and every later reader the letters of bytes that no longer exist.
    /// </summary>
    [Fact]
    public void RedactText_OnAPageWithCachedTextState_LeavesNoStaleLettersWordsOrText()
    {
        using var doc = PdfDocument.Open(MultiPagePdf(2, p => $"Keep{p} STALESECRET tail"));
        var page = doc.GetPage(1);
        page.Letters.Should().NotBeEmpty();
        page.GetWords().Select(w => w.Text).Should().Contain("STALESECRET");
        page.Text.Should().Contain("STALESECRET");

        doc.RedactText("STALESECRET", drawBlackRect: false).VerifiedRemovals.Should().Be(2);

        string.Concat(page.Letters.Select(l => l.Value)).Should().NotContain("STALESECRET",
            "the letters must be re-walked from the rewritten bytes");
        page.GetWords().Select(w => w.Text).Should().NotContain("STALESECRET");
        page.Text.Should().NotContain("STALESECRET");
        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("Keep1");
    }

    [Fact]
    public void RedactText_OnPagesWhoseLettersWereEvicted_RemovesTheTerm_PerSavedBytesAndMutool()
    {
        const string term = "EVICTEDSECRET";
        var pageCount = Capacity + 4;
        using var doc = PdfDocument.Open(MultiPagePdf(pageCount,
            p => p is 1 or 6 ? $"Neighbour{p} {term} after" : $"Neighbour{p} plain"));

        // Prime page 1, then read enough later pages to push it out of the bound.
        doc.GetPage(1).Letters.Should().NotBeEmpty();
        for (int p = 2; p <= Capacity + 1; p++)
            _ = doc.GetPage(p).Letters;
        doc.GetPage(1).HasCachedLetters.Should().BeFalse("page 1 must be evicted before the redaction");

        var report = doc.RedactText(term, drawBlackRect: false);
        report.VerifiedRemovals.Should().Be(2, "both occurrences must be removed and verified");

        using var saved = new MemoryStream();
        doc.Save(saved);
        var bytes = saved.ToArray();
        SavedPdfLeakScanner.FindTerm(bytes, term).Should().BeEmpty(
            "the term must be absent from every carrier of the saved file, compressed streams included");

        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool not installed");
        var mutool = MutoolTextOracle.ExtractAllPages(bytes);
        mutool.Should().NotContain(term, "MuPDF must independently agree the term is gone");
        for (int p = 1; p <= pageCount; p++)
            mutool.Should().Contain($"Neighbour{p}", $"page {p}'s other text must survive the redaction");
    }

    /// <summary>
    /// The bound is shared state: the App's background index and the UI thread
    /// read the same document. A stress run, not a proof — but a torn cache
    /// shows up here as an exception, a wrong letter list, or a bound overrun.
    /// </summary>
    [Fact]
    public async Task Letters_ReadFromManyThreads_StayCorrectAndBounded()
    {
        const int pageCount = 12;
        using var doc = PdfDocument.Open(MultiPagePdf(pageCount, p => $"Thread{p} words here"));
        var expected = Enumerable.Range(1, pageCount)
            .ToDictionary(p => p, p => $"Thread{p}wordshere");
        var errors = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var workers = Enumerable.Range(0, 8).Select(seed => Task.Run(() =>
        {
            var random = new Random(seed);
            while (!stop.IsCancellationRequested)
            {
                var p = random.Next(1, pageCount + 1);
                var page = doc.GetPage(p);
                // Letters include the space glyphs; words do not. Compare the
                // non-space content so all three reads share one expectation.
                var text = (random.Next(3) switch
                {
                    0 => string.Concat(page.Letters.Select(l => l.Value)),
                    1 => string.Concat(page.GetWords().Select(w => w.Text)),
                    _ => string.Concat(page.ExtractTextAndWordsWithoutRetainingLetters().Words.Select(w => w.Text)),
                }).Replace(" ", "");
                if (text != expected[p])
                    errors.Enqueue($"page {p}: '{text}'");
            }
        })).ToArray();
        await Task.WhenAll(workers);

        errors.Should().BeEmpty();
        Enumerable.Range(1, pageCount).Count(p => doc.GetPage(p).HasCachedLetters)
            .Should().BeLessThanOrEqualTo(Capacity);
    }

    private static IEnumerable<string> Fingerprint(IReadOnlyList<Letter> letters) =>
        letters.Select(l =>
            $"{l.Value}|{Bits(l.GlyphRectangle)}|{BitConverter.DoubleToInt64Bits(l.FontSize):X}|{l.FontName}|" +
            $"{BitConverter.DoubleToInt64Bits(l.StartX):X}|{BitConverter.DoubleToInt64Bits(l.StartY):X}|" +
            $"{BitConverter.DoubleToInt64Bits(l.Width):X}|{l.MarkedContentId}|{l.IsInHiddenOptionalContent}");

    private static string Bits(PdfRectangle r) =>
        $"{BitConverter.DoubleToInt64Bits(r.Left):X}/{BitConverter.DoubleToInt64Bits(r.Bottom):X}/" +
        $"{BitConverter.DoubleToInt64Bits(r.Right):X}/{BitConverter.DoubleToInt64Bits(r.Top):X}";

    private static byte[] MultiPagePdf(int pageCount, Func<int, string> pageText)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "__PAGES__",
        };

        int fontObj = 2 + pageCount * 2 + 1;
        var kids = new List<string>();
        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 3 + i * 2;
            kids.Add($"{pageObj} 0 R");
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       $"/Contents {pageObj + 1} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            var content = $"BT /F1 12 Tf 72 700 Td ({pageText(i + 1)}) Tj ET\n";
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pageCount} >>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
