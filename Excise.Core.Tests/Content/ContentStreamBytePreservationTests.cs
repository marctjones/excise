using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #1093 — byte-splicing: an edit to one operator must not put every OTHER
/// operator through the writer's escaping, number formatting and inline-image
/// reconstruction. That round trip has corrupted untouched content three
/// times (inline-image syntax #354, float formatting #762, PDFDocEncoding
/// octal escapes), and each was found by a leak rather than by a gate.
///
/// <para>These tests pin the two halves of the guarantee: bytes that were not
/// touched come back unchanged, and bytes that WERE touched are re-serialized
/// even when the mutation happened in place, deep inside an operand.</para>
/// </summary>
public class ContentStreamBytePreservationTests
{
    /// <summary>
    /// Deliberately awkward source: comment lines, ragged whitespace, a real
    /// number in a form the writer would re-format (<c>0.500</c> → <c>0.5</c>),
    /// a string with an escape, and CR-LF line ends. Re-serializing this
    /// changes bytes everywhere even when nothing is edited.
    /// </summary>
    private static byte[] AwkwardSource() => Encoding.Latin1.GetBytes(
        "% a leading comment\r\n"
        + "q\r\n"
        + "0.500  0.250 0.1250 rg\r\n"
        + "BT\r\n"
        + "  /F1   12.00 Tf\r\n"
        + "  72 720 Td\r\n"
        + "  (Hello \\(world\\) \\247) Tj\r\n"
        + "ET\n"
        + "Q\n"
        + "% trailing comment, no newline");

    private static ContentStream ParseTracked(byte[] source)
    {
        var parser = new ContentStreamParser(source) { TrackSourceSpans = true };
        return parser.Parse();
    }

    [Fact]
    public void UnmodifiedStream_RoundTripsByteIdentically()
    {
        var source = AwkwardSource();
        var content = ParseTracked(source);

        var written = new ContentStreamWriter().Write(content, source);

        written.Should().Equal(source,
            "a page nothing edited must come back byte for byte — comments, spacing, "
            + "number formatting and escapes included (#1093)");
    }

    /// <summary>The control: today's whole-stream re-serialization does NOT.</summary>
    [Fact]
    public void UnmodifiedStream_ReSerialized_DoesNotRoundTripByteIdentically()
    {
        var source = AwkwardSource();
        var content = ParseTracked(source);

        var written = new ContentStreamWriter().Write(content);

        written.Should().NotEqual(source,
            "this is the behaviour #1093 exists to avoid — if it ever becomes byte-exact, "
            + "the test above stops proving anything");
    }

    [Fact]
    public void RemovingOneOperator_LeavesEveryOtherOperatorsBytesUntouched()
    {
        var source = AwkwardSource();
        var content = ParseTracked(source);

        var kept = content.Operators.Where(o => o.Name != "Tj").ToList();
        var written = new ContentStreamWriter().Write(new ContentStream(kept), source);
        var text = Encoding.Latin1.GetString(written);

        text.Should().NotContain("Hello", "the removed operator's bytes must be gone");

        // The survivors keep their SOURCE spelling, not the writer's.
        text.Should().Contain("0.500  0.250 0.1250 rg",
            "an untouched operator must keep its original number formatting and spacing");
        text.Should().Contain("/F1   12.00 Tf",
            "…including the ones the writer would normalise");
        text.Should().Contain("% a leading comment",
            "comments ride along with the operator that followed them");
    }

    [Fact]
    public void ARewrittenOperator_IsSerialized_AndStillParsesBackCleanly()
    {
        var source = AwkwardSource();
        var content = ParseTracked(source);

        // Replace the Tj with a synthetic one, the shape redaction produces.
        var ops = content.Operators.ToList();
        int idx = ops.FindIndex(o => o.Name == "Tj");
        ops[idx] = new ContentOperator("Tj", new PdfObject[] { new PdfString("Goodbye") });

        var written = new ContentStreamWriter().Write(new ContentStream(ops), source);

        var reparsed = new ContentStreamParser(written).Parse();
        reparsed.Operators.Select(o => o.Name).Should().Equal(
            content.Operators.Select(o => o.Name),
            "splicing must not lose, duplicate or reorder operators");
        reparsed.Operators[idx].GetString(0).Should().Be("Goodbye");
    }

    /// <summary>
    /// A verbatim span ends on its operator token, so the writer has to
    /// separate it from anything re-serialized that follows. Without that,
    /// <c>Tj</c> and a following <c>1 0 0 1 …</c> fuse into <c>Tj1</c>.
    /// </summary>
    [Fact]
    public void VerbatimSpanFollowedByASerializedOperator_StaysTokenized()
    {
        var source = Encoding.Latin1.GetBytes("BT (a) Tj ET\n");
        var content = ParseTracked(source);

        var ops = content.Operators.ToList();
        ops.Insert(ops.FindIndex(o => o.Name == "ET"),
            new ContentOperator("Tm", new PdfObject[]
            {
                new PdfInteger(1), new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(1), new PdfInteger(5), new PdfInteger(5),
            }));

        var written = new ContentStreamWriter().Write(new ContentStream(ops), source);
        Encoding.Latin1.GetString(written).Should().NotContain("Tj1");

        new ContentStreamParser(written).Parse().Operators.Select(o => o.Name)
            .Should().Equal("BT", "Tj", "Tm", "ET");
    }

    /// <summary>
    /// ⚠️ THE LEAK CASE. <c>MarkedContentCarrierScrubber</c> removes an
    /// <c>/ActualText</c> carrier — #636's leak carrier — by mutating a parsed
    /// <c>BDC</c> operand dictionary IN PLACE. Nothing tells the writer. If a
    /// span were copied on the strength of "this operator object was never
    /// replaced", the scrubbed text would be written straight back into the
    /// redacted file.
    /// </summary>
    [Fact]
    public void OperandMutatedInPlace_IsReSerialized_NotCopiedFromSource()
    {
        var source = Encoding.Latin1.GetBytes(
            "/Span << /ActualText (SECRET) >> BDC\n(x) Tj\nEMC\n");
        var content = ParseTracked(source);

        var bdc = content.Operators.Single(o => o.Name == "BDC");

        // Non-vacuity: UNMUTATED, this operator is copied verbatim (the whole
        // stream is). So the re-serialization below is caused by the mutation
        // being detected, not by the span being unusable in the first place.
        new ContentStreamWriter().Write(content, source).Should().Equal(source);

        var props = bdc.Operands.OfType<PdfDictionary>().Single();
        props.Remove("ActualText");            // exactly what the scrubber does

        var written = new ContentStreamWriter().Write(content, source);

        Encoding.Latin1.GetString(written).Should().NotContain("SECRET",
            "an operand mutated in place must be re-serialized — copying its original "
            + "bytes would resurrect a carrier redaction had just scrubbed (#1093/#636)");

        // The operators around it are untouched, so they still come through verbatim.
        Encoding.Latin1.GetString(written).Should().Contain("(x) Tj");
    }

    [Fact]
    public void OperatorsWithoutTrackedSpans_FallBackToSerialization()
    {
        var source = AwkwardSource();
        var untracked = new ContentStreamParser(source).Parse();   // tracking off

        untracked.SourceBytes.Should().BeNull();
        untracked.Operators.Should().OnlyContain(o => !o.HasSourceSpan);

        var written = new ContentStreamWriter().Write(untracked, source);
        written.Should().Equal(new ContentStreamWriter().Write(untracked),
            "with no spans the splice writer must behave exactly like today's writer");
    }

    /// <summary>
    /// Inline images are the #354 carrier: their <c>BI…ID…EI</c> syntax is
    /// reconstructed by the writer rather than round-tripped, so copying the
    /// source bytes is the strongest possible fidelity for them.
    /// </summary>
    [Fact]
    public void InlineImage_IsCopiedFromSource_WhenUntouched()
    {
        var source = Encoding.Latin1.GetBytes(
            "q\nBI /W 2 /H 2 /CS /G /BPC 8 ID \x01\x02\x03\x04\nEI\nQ\n");
        var content = ParseTracked(source);

        content.Operators.Select(o => o.Name).Should().Equal("q", "BI", "Q");
        var written = new ContentStreamWriter().Write(content, source);

        written.Should().Equal(source,
            "an untouched inline image must keep its exact source bytes (#354/#1093)");
    }

    /// <summary>
    /// Spans tile the source: each begins where the previous ended, and the
    /// first begins at 0. That property — not per-token spans — is what makes
    /// the untouched round trip byte-exact.
    /// </summary>
    [Fact]
    public void SourceSpans_TileTheStreamWithoutGaps()
    {
        var source = AwkwardSource();
        var ops = ParseTracked(source).Operators;

        ops[0].SourceStart.Should().Be(0);
        for (int i = 1; i < ops.Count; i++)
            ops[i].SourceStart.Should().Be(ops[i - 1].SourceEnd,
                $"operator {i} ({ops[i].Name}) must start where operator {i - 1} ended");

        ops[^1].SourceEnd.Should().BeLessThanOrEqualTo(source.Length);
    }
}
