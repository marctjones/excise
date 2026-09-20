using System.Collections.Generic;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// WHAT AN OPEN-SAVE ROUND TRIP DOES AND DOES NOT PROVE ABOUT AN OPERATOR.
///
/// <c>OperatorVerificationParityTests.AuthoritativeOperatorInventory_SurvivesRoundTrip_ConfirmedByQpdf</c>
/// shows that ~70 content-stream operators survive an excise open-save, as
/// seen by qpdf's own decompression of the saved file. That is a real result
/// and an independent one. Until this file it was also cited in the capability
/// registry as <c>parse</c> and <c>write</c> evidence for 154 capabilities —
/// 229 (capability, mode) rows whose ONLY independent-oracle evidence it was,
/// a quarter of the whole registry's "verified" headline.
///
/// It cannot support those two claims, and this file is why. excise's writer
/// copies a page's content-stream BYTES when the page was not edited: it does
/// not re-serialise them from a parsed model. So an operator's tokens survive
/// the round trip whether or not excise's parser has ever heard of that
/// operator. The registry's own modeDefinitions are explicit —
/// <c>parse</c> is "Recognize, validate, and expose a feature without
/// executing it" and <c>write</c> is "Serialize a valid instance created by a
/// core workflow" — and a byte copy does none of those things. <c>preserve</c>
/// ("Open-save retains it") is exactly what it does prove, and that citation
/// stands.
///
/// The two tests here are the executable form of that argument, so it cannot
/// quietly rot back: the first pins the mechanism, the second shows the
/// mechanism makes the parse claim unfalsifiable. If excise ever starts
/// re-serialising content streams from a parsed model, BOTH fail, and the
/// scope question genuinely reopens — which is the right time to revisit it.
///
/// ⚠️ Neither test is a complaint about the round-trip behaviour. Conserving
/// bytes excise does not understand is the CORRECT conservation policy: it is
/// what stops an editor from silently destroying content it failed to model.
/// The defect was in what the evidence was cited FOR, not in the behaviour.
/// </summary>
public class OperatorRoundTripScopeTests
{
    /// <summary>
    /// Non-canonical spacing and trailing zeros survive byte-for-byte. Any
    /// writer that re-serialised from a parsed model would normalise
    /// <c>1.50000</c> to <c>1.5</c> and collapse the runs of spaces.
    /// </summary>
    [Fact]
    public void UneditedPage_ContentStreamBytes_AreCopiedNotReserialised()
    {
        const string content =
            "q 1.50000  0 0   1.50000 10.0 10.0 cm\n0 0 0 rg\n10    10   100 50 re\nf\nQ";

        var after = RoundTripContent(content);

        after.Should().Be(content,
            "an unedited page's content stream is copied verbatim on save. This is the " +
            "conservation policy working as intended -- and it is also the reason an " +
            "operator surviving a round trip says nothing about excise having parsed it");
    }

    /// <summary>
    /// The load-bearing one. Two operators that do not exist in ISO 32000-2 —
    /// so excise's parser cannot possibly "recognize, validate and expose"
    /// them — survive the round trip exactly as the 70 real operators do.
    ///
    /// A test that asserts "operator X's token is in the saved file" therefore
    /// returns the same verdict for an operator excise fully implements and
    /// for one it has never heard of. It cannot discriminate, so it cannot be
    /// parse evidence for either.
    /// </summary>
    [Fact]
    public void InventedOperators_SurviveTheRoundTrip_SoTokenSurvivalIsNotParseEvidence()
    {
        const string content =
            "q 1 0 0 1 0 0 cm\n0 0 0 rg\n10 10 100 50 re\nf\n99 88 zzNotARealOperator\n7 blorp\nQ";

        var after = RoundTripContent(content);

        after.Should().Contain("zzNotARealOperator").And.Contain("blorp",
            "these operators do not exist in ISO 32000-2, so excise cannot have recognised, " +
            "validated or exposed them -- yet they round-trip exactly as 'd0' or 'sh' do. " +
            "Any assertion of the form 'the token is still there' is therefore satisfied by " +
            "an operator excise ignores entirely, and grades nothing");
    }

    /// <summary>Open the fixture with excise, save it, and read page 1's decoded content stream back.</summary>
    private static string RoundTripContent(string content)
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(BuildPdf(content)))
            saved = doc.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        var contents = reopened.GetPage(1).Dictionary.GetOptional("Contents");
        contents.Should().NotBeNull("the saved page must still have a content stream");
        var stream = reopened.Resolve(contents!) as PdfStream;
        stream.Should().NotBeNull("page 1's /Contents must resolve to a stream");
        return Encoding.ASCII.GetString(stream!.DecodedData);
    }

    private static byte[] BuildPdf(string content)
    {
        var length = Encoding.ASCII.GetByteCount(content);
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {length} >>\nstream\n{content}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
