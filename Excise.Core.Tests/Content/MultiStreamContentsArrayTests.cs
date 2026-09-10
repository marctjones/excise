using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #1449: a <c>/Contents</c> ARRAY of streams (ISO 32000-2 §7.7.3.3) must not
/// be collapsed to one stream on every write. <see cref="ContentStreamWriter"/>
/// tries to map each original array-join offset onto the spliced output so
/// <c>PdfPage.SetContentStream</c> can write the same number of elements back;
/// it falls back to the pre-#1449 single-stream collapse whenever a join no
/// longer lands at a clean operator seam.
///
/// <para>Writer-level tests exercise the boundary-mapping algorithm directly,
/// the way <see cref="ContentStreamBytePreservationTests"/> exercises #1093's
/// splicing. Page-level tests exercise the real redaction entry point and
/// verify with the carrier-agnostic saved-bytes scanner, per CLAUDE.md — a
/// redaction test may never rely on <c>ExtractAllText</c> alone.</para>
/// </summary>
public class MultiStreamContentsArrayTests
{
    #region Writer-level: boundary mapping

    private const string Chunk0 = "BT (Hello) Tj ET";
    private const string Chunk1 = "BT (World) Tj ET";

    /// <summary>
    /// Exactly what <c>PdfPageContentFacade.TryCollectContentStreamBytes</c>
    /// produces for a two-element array: each element's bytes followed by a
    /// synthetic '\n', concatenated. The boundary is the offset where the
    /// second element's bytes begin.
    /// </summary>
    private static (byte[] Source, int[] Boundaries) BuildConcatenated(params string[] chunks)
    {
        using var ms = new System.IO.MemoryStream();
        var boundaries = new System.Collections.Generic.List<int>();
        for (int i = 0; i < chunks.Length; i++)
        {
            if (i > 0) boundaries.Add((int)ms.Position);
            var bytes = Encoding.Latin1.GetBytes(chunks[i]);
            ms.Write(bytes);
            ms.WriteByte((byte)'\n');
        }
        return (ms.ToArray(), boundaries.ToArray());
    }

    private static ContentStream ParseTracked(byte[] source, int[] boundaries)
    {
        var parser = new ContentStreamParser(source)
        {
            TrackSourceSpans = true,
            ArrayBoundaries = boundaries,
        };
        return parser.Parse();
    }

    [Fact]
    public void UnmodifiedTwoStreamConcatenation_MapsTheBoundaryToItself()
    {
        var (source, boundaries) = BuildConcatenated(Chunk0, Chunk1);
        var content = ParseTracked(source, boundaries);

        var written = new ContentStreamWriter().Write(content, source, boundaries, out var outputBoundaries);

        written.Should().Equal(source, "nothing was edited, so the whole-stream shortcut applies");
        outputBoundaries.Should().Equal(boundaries,
            "an untouched round trip must map every original join to the identical offset");
    }

    [Fact]
    public void RemovingAnOperatorBeforeTheBoundary_StillMapsTheBoundaryCorrectly()
    {
        var (source, boundaries) = BuildConcatenated(Chunk0, Chunk1);
        var content = ParseTracked(source, boundaries);

        // Drop chunk 0's Tj — shortens chunk 0 in the output without touching
        // anything at or after the boundary.
        var kept = content.Operators.Where(o => o.TextContent != "Hello").ToList();
        kept.Select(o => o.Name).Should().Equal(new[] { "BT", "ET", "BT", "Tj", "ET" },
            "sanity: exactly the Hello Tj should be gone, chunk 1 untouched");

        var written = new ContentStreamWriter().Write(
            new ContentStream(kept) { SourceBytes = source, SourceArrayBoundaries = boundaries },
            source, boundaries, out var outputBoundaries);

        outputBoundaries.Should().NotBeNull(
            "the boundary sits inside chunk 1's first operator, which was never touched");
        var boundary = outputBoundaries![0];

        var chunk0Out = Encoding.Latin1.GetString(written, 0, boundary);
        var chunk1Out = Encoding.Latin1.GetString(written, boundary, written.Length - boundary);

        chunk0Out.Should().NotContain("Hello", "the removed operator's text must be gone");
        chunk0Out.Should().Contain("BT").And.Contain("ET");
        chunk1Out.Should().Be(Chunk1 + "\n",
            "chunk 1 was never touched, so it must still be exactly the original bytes plus the trailing separator");
    }

    [Fact]
    public void RewritingTheOperatorContainingTheBoundary_FailsToMapIt()
    {
        var (source, boundaries) = BuildConcatenated(Chunk0, Chunk1);
        var content = ParseTracked(source, boundaries);

        // The boundary sits in the leading whitespace of chunk 1's first "BT"
        // (RecordSourceSpan: a span starts where the previous one ended, so
        // the join is never between two spans — it is INSIDE one). Replacing
        // that exact operator with a synthetic one — no source span — is the
        // shape a real edit takes when it touches the operator straddling a
        // seam.
        var ops = content.Operators.ToList();
        var idx = ops.FindLastIndex(o => o.Name == "BT");
        ops[idx] = new ContentOperator("BT", System.Array.Empty<PdfObject>());

        var written = new ContentStreamWriter().Write(
            new ContentStream(ops) { SourceBytes = source, SourceArrayBoundaries = boundaries },
            source, boundaries, out var outputBoundaries);

        outputBoundaries.Should().BeNull(
            "there is no honest place to cut when the operator straddling the seam was rewritten (#1449)");
        // The edit itself must still take effect — falling back to one stream
        // must not mean falling back to losing the edit.
        Encoding.Latin1.GetString(written).Should().Contain("BT").And.Contain(Chunk1["BT".Length..]);
    }

    [Fact]
    public void RemovingTheOperatorContainingTheBoundary_FailsToMapIt()
    {
        var (source, boundaries) = BuildConcatenated(Chunk0, Chunk1);
        var content = ParseTracked(source, boundaries);

        // Delete chunk 1's opening BT outright rather than rewriting it —
        // every later operator's SourceStart stays ahead of the boundary
        // forever, so the boundary can never be resolved (verified by the
        // "no later operator can resolve it either" argument in the writer).
        var kept = content.Operators.Where(o => o != content.Operators.Last(x => x.Name == "BT")).ToList();

        var written = new ContentStreamWriter().Write(
            new ContentStream(kept) { SourceBytes = source, SourceArrayBoundaries = boundaries },
            source, boundaries, out var outputBoundaries);

        outputBoundaries.Should().BeNull(
            "an edit that removes the operator spanning a boundary has no honest place to cut (#1449)");
    }

    #endregion

    #region Page-level: real /Contents array round trips

    /// <summary>
    /// A one-page PDF whose <c>/Contents</c> is an ARRAY of
    /// <paramref name="streamContents"/> streams, ISO 32000-2 §7.7.3.3.
    /// Mirrors <c>ContentStreamFixture.Build</c>'s single-stream construction.
    /// </summary>
    private static byte[] BuildMultiStreamPdf(params string[] streamContents)
    {
        var bodies = streamContents.Select(Encoding.Latin1.GetBytes).ToArray();
        using var ms = new System.IO.MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");

        var offset1 = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var offset2 = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        const int firstStreamObj = 4;
        var fontObj = firstStreamObj + bodies.Length;
        var streamRefs = string.Join(" ",
            System.Linq.Enumerable.Range(0, bodies.Length).Select(i => $"{firstStreamObj + i} 0 R"));

        var offset3 = ms.Position;
        W($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          + $"/Contents [{streamRefs}] /Resources << /Font << /F1 {fontObj} 0 R >> >> >>\nendobj\n");

        var streamOffsets = new long[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            streamOffsets[i] = ms.Position;
            W($"{firstStreamObj + i} 0 obj\n<< /Length {bodies[i].Length} >>\nstream\n");
            ms.Write(bodies[i]);
            W("\nendstream\nendobj\n");
        }

        var fontOffset = ms.Position;
        W($"{fontObj} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var size = fontObj + 1;
        var xref = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        W($"{offset1:D10} 00000 n \n");
        W($"{offset2:D10} 00000 n \n");
        W($"{offset3:D10} 00000 n \n");
        foreach (var so in streamOffsets)
            W($"{so:D10} 00000 n \n");
        W($"{fontOffset:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    // Deliberately distinct Y positions per call — two text blocks at the
    // SAME page position would spatially overlap, and area/glyph-overlap
    // redaction works on page geometry, not on which array element a glyph
    // came from. Same trap as Pitfall 4 in CLAUDE.md, one level up.
    private static string StreamText(string body, int y = 720) =>
        $"BT\n/F1 12 Tf\n72 {y} Td\n({body}) Tj\nET\n";

    [Fact]
    public void ThreeStreamPage_UnmodifiedRewrite_KeepsAllThreeElementsByteIdentical()
    {
        var stream0 = StreamText("First", 720);
        var stream1 = StreamText("Second", 650);
        var stream2 = StreamText("Third", 580);
        using var doc = PdfDocument.Open(BuildMultiStreamPdf(stream0, stream1, stream2));
        var page = doc.GetPage(1);

        var array = doc.Resolve(page.Dictionary["Contents"]) as PdfArray;
        array.Should().NotBeNull();
        array!.Count.Should().Be(3, "sanity: the fixture really is a 3-element array");

        // Identity round trip through the tracked-span path, the way
        // redaction re-writes a page it touched elsewhere but not here.
        var content = page.GetContentStream(trackSourceSpans: true);
        content.SourceArrayBoundaries.Should().NotBeNull().And.HaveCount(2);
        page.SetContentStream(content);

        var after = doc.Resolve(page.Dictionary["Contents"]) as PdfArray;
        after.Should().NotBeNull();
        after!.Count.Should().Be(3, "an unmodified multi-stream write must not collapse the array (#1449)");

        var expected = new[] { stream0, stream1, stream2 };
        for (int i = 0; i < 3; i++)
        {
            var s = doc.Resolve(after[i]) as PdfStream;
            s.Should().NotBeNull();
            Encoding.Latin1.GetString(s!.DecodedData).Should().Be(expected[i],
                $"element {i} was never touched and must keep its exact original bytes");
        }
    }

    [Fact]
    public void RedactingATermInTheFirstStream_KeepsTheSecondStreamSeparateAndByteIdentical()
    {
        var stream0 = StreamText("SECRET", 720);
        var stream1 = StreamText("Innocuous", 650);
        using var doc = PdfDocument.Open(BuildMultiStreamPdf(stream0, stream1));
        var page = doc.GetPage(1);

        var report = doc.RedactText("SECRET");
        report.MatchesLocated.Should().BeGreaterThan(0);

        var array = doc.Resolve(page.Dictionary["Contents"]) as PdfArray;
        array.Should().NotBeNull();
        array!.Count.Should().Be(2,
            "the edit only touched stream 0's operators, so the array must not collapse to one stream (#1449)");

        var second = doc.Resolve(array[1]) as PdfStream;
        second.Should().NotBeNull();
        // The redaction's visual black-box marker is new content with no
        // source span — it is appended after every existing operator, so it
        // lands in whatever the LAST array element is (here, stream 1's).
        // What matters for #1449 is that stream 1's OWN operators still
        // start the element byte-for-byte, rather than being silently
        // dropped by the pre-#1449 "clear every element but the first".
        Encoding.Latin1.GetString(second!.DecodedData).Should().StartWith(stream1,
            "the second stream's own operators were never in the redaction area and must keep their exact original bytes");

        // CLAUDE.md: a redaction test may not rely on ExtractAllText alone.
        // Carrier-agnostic scan of the SAVED bytes, decompressed streams
        // included.
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "SECRET").Should().BeEmpty(
            "the redacted term must be gone from every carrier in the saved file, " +
            "not merely absent from the in-memory content-stream bytes");
    }

    [Fact]
    public void EditTouchingTheSeamOperator_FallsBackToOneStream_WithoutLosingTheEdit()
    {
        var stream0 = StreamText("First", 720);
        var stream1 = StreamText("Second", 650);
        using var doc = PdfDocument.Open(BuildMultiStreamPdf(stream0, stream1));
        var page = doc.GetPage(1);

        var content = page.GetContentStream(trackSourceSpans: true);
        content.SourceArrayBoundaries.Should().NotBeNull().And.HaveCount(1);

        // Replace stream 1's opening BT — the operator straddling the seam —
        // with a synthetic one, same shape as a real in-place operand edit.
        var ops = content.Operators.ToList();
        var idx = ops.FindLastIndex(o => o.Name == "BT");
        ops[idx] = new ContentOperator("BT", System.Array.Empty<PdfObject>());

        page.SetContentStream(new ContentStream(ops)
        {
            SourceBytes = content.SourceBytes,
            SourceArrayBoundaries = content.SourceArrayBoundaries,
        });

        var array = doc.Resolve(page.Dictionary["Contents"]) as PdfArray;
        array.Should().NotBeNull();
        array!.Count.Should().Be(1,
            "no honest seam to cut at means the pre-#1449 single-stream collapse, unchanged");

        // The edit itself must not be lost by the fallback.
        var merged = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        merged.Should().Contain("First").And.Contain("Second",
            "falling back to one stream must still contain everything both original streams held");
    }

    #endregion
}
