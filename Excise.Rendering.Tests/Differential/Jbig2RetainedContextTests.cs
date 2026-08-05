using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #656 — JBIG2 symbol-dictionary retained coding contexts. What this file
/// pins is NOT a decoder fix. It is the discovery that the instrument used to
/// decide whether a decoder fix was needed was lying.
///
/// HOW THIS WENT
///
/// The capability classifier reported <c>symbol-dictionary.context-retained</c>
/// as unsupported on two corpus files. That is the evidence R8 uses to decide
/// what to implement, so it looked like two witnesses for implementing context
/// retention. One of them, <c>bitmap-initially-unknown-size.pdf</c>, then
/// turned out to RENDER CORRECTLY — three independent oracles agreeing to
/// within 0.002 ink fraction (mutool 0.0686, pdftocairo 0.0707, Ghostscript
/// 0.0686) and excise matching them.
///
/// A file cannot both need an unimplemented feature and render correctly
/// without it. The classifier was wrong, and its own diagnostics said so if
/// read carefully:
///
/// <code>
///   segment 1   ImmediateGenericRegion: data length 4294967295 exceeds supported limits
///   segment 399 SymbolDictionary:       data length 3254779904 exceeds supported limits
/// </code>
///
/// 4294967295 is 0xFFFFFFFF — T.88 §7.2.7's UNKNOWN-LENGTH marker, legal on an
/// immediate generic region, and the whole point of a fixture named
/// "initially-unknown-size". The classifier treated it as an overflow, left
/// <c>dataLength</c> at 0, and then advanced by <c>DataOffset + 0</c> — landing
/// on the START OF THE IMAGE DATA and parsing it as the next segment header.
///
/// That does not fail. It invents. "Segment 399" does not exist; its flags are
/// generic-region bitmap bytes reinterpreted as a symbol-dictionary header, and
/// one of those bits happened to read as "contexts retained".
///
/// So the fix is to stop the walk when a length cannot be skipped, and the real
/// finding is about instruments: a capability classifier that fabricates
/// features is worse than none, because it is trusted. This one nearly bought
/// an implementation of arithmetic context import for a file containing no
/// symbol dictionary at all.
///
/// WHY CONTEXT IMPORT IS STILL REFUSED
///
/// After the fix exactly ONE corpus file across all four corpora (116 JBIG2
/// streams) genuinely sets these flags: <c>bitmap-symbol-context-reuse.pdf</c>.
/// No oracle decodes it. mutool reports "cannot decode jbig2 image" and emits a
/// 100%-black page; pdftocairo reports "Too many symbols in JBIG2 symbol
/// dictionary" and emits a blank one. They do not agree even on how to fail.
///
/// With no oracle there is no way to distinguish a correct implementation from
/// a plausible one, and for a SYMBOL DICTIONARY the plausible-but-wrong outcome
/// is confidently-painted wrong glyph bitmaps — which in a redaction tool is
/// the failure class that matters, since a wrong glyph looks exactly as
/// convincing as a right one. Refusing precisely beats guessing.
/// </summary>
public class Jbig2RetainedContextTests
{
    private const int Dpi = 150;

    /// <summary>
    /// The page the classifier libelled. Asserted against the oracle rather
    /// than a number of my own choosing, so the test cannot drift into
    /// endorsing whatever excise happens to do.
    /// </summary>
    [Fact]
    public void UnknownLengthFixture_RendersAndAgreesWithTheOracle()
    {
        var path = FindCorpusFile("bitmap-initially-unknown-size.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();
        double oracle = InkFraction(reference!);
        oracle.Should().BeGreaterThan(0.01,
            "if mutool stops decoding this file the premise of this test — that the page is " +
            "genuinely decodable and the classifier report was spurious — no longer holds");

        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc, new List<string>());

        InkFraction(excise).Should().BeApproximately(oracle, 0.02,
            "this page contains one generic region and no symbol dictionary whatsoever; " +
            "it decodes correctly and always did");
    }

    /// <summary>
    /// THE ACTUAL REGRESSION TEST. The classifier must not report features it
    /// read out of image data.
    ///
    /// Note what is asserted: not "reports nothing", but "reports nothing about
    /// symbol dictionaries" — the file has none. A weaker assertion (say, that
    /// the diagnostic list is non-empty) would pass on the fabricating build,
    /// since it fabricated a diagnostic too.
    /// </summary>
    [Fact]
    public void UnknownLengthSegment_DoesNotFabricateDownstreamSegments()
    {
        var path = FindCorpusFile("bitmap-initially-unknown-size.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present."); // [requires: corpus:pdfjs]

        var report = ClassifyFirstJbig2Stream(path!);
        report.Should().NotBeNull("the fixture must contain a /JBIG2Decode stream");

        report!.Value.Unsupported.Should().NotContain(f => f.StartsWith("symbol-dictionary", StringComparison.Ordinal),
            "the walk stops at the unknown-length generic region, so no symbol dictionary is " +
            "ever reached — every symbol-dictionary feature previously reported here was read " +
            "out of bitmap data misparsed as a segment header");

        report.Value.SegmentTypeCounts.Should().NotContainKey("SymbolDictionary",
            "there is no symbol dictionary in this file at all");

        report.Value.Diagnostics.Should().Contain(d => d.Contains("unknown-length marker"),
            "an unskippable length must say WHY the walk stopped — silently returning a short " +
            "feature list would look identical to a file that genuinely has few features");
    }

    /// <summary>
    /// The one real witness, and the reason the refusal stays. excise must not
    /// paint a guess where every independent renderer fails outright.
    /// </summary>
    [Fact]
    public void ContextImport_IsRefusedRatherThanGuessed()
    {
        var path = FindCorpusFile("bitmap-symbol-context-reuse.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present."); // [requires: corpus:pdfjs]

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc, diagnostics);

        diagnostics.Should().NotBeEmpty(
            "refusing an image in silence is the failure mode #874 and #878 were both about");

        InkFraction(excise).Should().BeLessThan(0.01,
            "no oracle decodes this file — mutool emits a fully black page, pdftocairo a blank " +
            "one. Substantial ink from excise would mean it had invented symbol bitmaps from a " +
            "zeroed arithmetic context, which is worse than refusing");
    }

    /// <summary>
    /// Guards the scope of the classifier change: a healthy JBIG2 stream must
    /// still be walked to the end. Stopping the walk too eagerly would silently
    /// shrink every report and make the whole instrument under-report instead
    /// of over-report — trading one flavour of wrong for another.
    /// </summary>
    [Fact]
    public void AHealthyStream_IsStillWalkedCompletely()
    {
        var path = FindCorpusFile("bitmap-symbol-context-reuse.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present."); // [requires: corpus:pdfjs]

        var report = ClassifyFirstJbig2Stream(path!);
        report.Should().NotBeNull();

        report!.Value.Diagnostics.Should().BeEmpty(
            "this stream has ordinary segment lengths and must classify cleanly");
        report.Value.SegmentTypeCounts.Should().ContainKey("SymbolDictionary")
            .WhoseValue.Should().Be(4, "all four dictionaries must still be reached");
        report.Value.Unsupported.Should().Contain("symbol-dictionary.context-used",
            "this file is the one genuine witness for context import and must keep reporting it");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private readonly record struct Report(
        IReadOnlyList<string> Unsupported,
        IReadOnlyDictionary<string, int> SegmentTypeCounts,
        IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// Reaches the internal classifier the same way Excise.RenderTools does.
    /// Reflection rather than widening visibility: the classifier is internal by
    /// design (it matches Jbig2CapabilityClassifier's own accessibility), and a
    /// test is not a reason to make an implementation detail public.
    /// </summary>
    private static Report? ClassifyFirstJbig2Stream(string path)
    {
        using var doc = PdfDocument.Open(path);
        var asm = typeof(PdfDocument).Assembly;
        var classifier = asm.GetType("Excise.Core.Filters.Jbig2.Jbig2CapabilityClassifier");
        classifier.Should().NotBeNull("the classifier type must still exist under that name");
        var analyze = classifier!.GetMethod("Analyze",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        analyze.Should().NotBeNull();

        foreach (var stream in EnumerateJbig2Streams(doc))
        {
            // Only safe when JBIG2Decode is the sole filter — otherwise EncodedData
            // is still wrapped (Flate, etc.) and would classify as garbage. Both
            // fixtures here are single-filter; anything else is skipped rather than
            // silently misread.
            if (stream.Filters.Count != 1) continue;
            object? result = analyze!.Invoke(null, new object?[] { stream.EncodedData, null });
            if (result == null) continue;

            var t = result.GetType();
            return new Report(
                (IReadOnlyList<string>)t.GetProperty("UnsupportedFeatures")!.GetValue(result)!,
                (IReadOnlyDictionary<string, int>)t.GetProperty("SegmentTypeCounts")!.GetValue(result)!,
                (IReadOnlyList<string>)t.GetProperty("Diagnostics")!.GetValue(result)!);
        }

        return null;
    }

    private static IEnumerable<PdfStream> EnumerateJbig2Streams(PdfDocument doc)
    {
        for (int p = 1; p <= doc.PageCount; p++)
        {
            if (doc.GetPage(p).Dictionary.GetOptional("Resources") is not PdfDictionary resources)
                continue;
            if (resources.GetOptional("XObject") is not { } xobjRef)
                continue;
            if (doc.Resolve(xobjRef) is not PdfDictionary xobjects)
                continue;

            foreach (var key in xobjects.Keys)
            {
                if (xobjects.GetOptional(key) is not { } entry) continue;
                if (doc.Resolve(entry) is not PdfStream s) continue;
                if (s.Filters.Any(f => f == "JBIG2Decode")) yield return s;
            }
        }
    }

    private static SKBitmap Render(PdfDocument doc, List<string> diagnostics) =>
        new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions
            {
                Dpi = Dpi,
                AntiAlias = false,
                BackgroundColor = SKColors.White,
                Diagnostics = diagnostics,
            });

    private static double InkFraction(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.Red < 200 || c.Green < 200 || c.Blue < 200) n++;
            }
        return (double)n / (b.Width * b.Height);
    }

    private static string? FindCorpusFile(string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", "pdfjs"));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
