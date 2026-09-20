using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// PARSE-mode evidence for the Annex A content-stream operators: excise's
/// parser must RECOGNISE each operator, and qpdf must independently confirm
/// the operator is really in the bytes being parsed.
///
/// WHY THIS EXISTS. The registry's parse evidence for these capabilities used
/// to be <c>AuthoritativeOperatorInventory_SurvivesRoundTrip_ConfirmedByQpdf</c>,
/// which asks whether an operator's token is still in the file after an
/// open-save. <see cref="OperatorRoundTripScopeTests"/> shows that check is
/// satisfied by operators excise has never heard of, because an unedited
/// page's content stream is copied byte-for-byte and the parser is not in the
/// path at all. It was re-scoped to preserve, which is what it does prove, and
/// this file supplies what parse actually needs.
///
/// THE THREE PARTS, and why each is load-bearing:
///
///  1. <b>qpdf establishes the fixture is real.</b> qpdf's own decompressed
///     token dump must contain the operator. Without this the test could pass
///     on a fixture that never contained the operator, with excise agreeing
///     about nothing.
///  2. <b>excise must RECOGNISE it</b> — the registry defines parse as
///     "Recognize, validate, and expose a feature without executing it", so
///     the assertion is <c>Category != OperatorCategory.Unknown</c> plus the
///     operands being exposed, not merely that a token came back.
///  3. <b>A negative control proves part 2 can fail.</b>
///     <see cref="ExciseDoesNotRecogniseAnInventedOperator_SoRecognitionIsFalsifiable"/>
///     feeds an operator that is not in ISO 32000-2 through the same path and
///     requires <c>Unknown</c>. Without it, "Category != Unknown" would be an
///     untested claim about excise's own enum.
///
/// This is a genuinely weaker oracle than a raster or byte differential: qpdf
/// certifies the INPUT, excise's recognition is still excise's own judgement.
/// It is recorded as differential because an independent tool decides the
/// ground truth the assertion is made against and because part 3 makes it
/// discriminating — but it is deliberately NOT claimed to verify that any
/// operator is implemented CORRECTLY. The render-mode differentials in
/// <c>OperatorVerificationParityTests</c> are what carry that.
/// </summary>
public class OperatorParseRecognitionTests
{
    /// <summary>
    /// One content stream exercising the Annex A operator set, with the
    /// operand arity each operator requires. Shared with the round-trip test's
    /// fixture shape deliberately: the two now make different claims about the
    /// same bytes, which is the point.
    /// </summary>
    private const string Inventory =
        "q 1 0 0 1 10 10 cm 2 w 1 J 1 j 10 M [3 2] 0 d 1.0 ri 1 i /GS1 gs\n" +
        "10 10 m 20 20 l 30 0 40 10 50 20 c 5 5 v 6 6 y h 0 0 10 10 re\n" +
        "S s f F f* B B* b b* W n W*\n" +
        "/CS0 CS /CS1 cs 0.1 G 0.2 g 0.1 0.2 0.3 RG 0.4 0.5 0.6 rg " +
        "0 0 0 1 K 0 0 0 1 k 0.5 SC 0.5 SCN 0.5 sc 0.5 scn\n" +
        "/Sh1 sh\n" +
        "BT /F1 12 Tf 14 TL 1 Tc 2 Tw 100 Tz 0 Tr 1 Ts 10 20 Td 5 6 TD " +
        "1 0 0 1 7 8 Tm T* (a) Tj [(b) -10 (c)] TJ (d) ' 1 2 (e) \" ET\n" +
        "/P <</MCID 0>> BDC /Span BMC EMC EMC /Pt 1 MP /Tg /Val DP BX /Unknown EX\n" +
        "750 0 d0 750 0 0 0 700 700 d1\n" +
        "/Im1 Do\n" +
        "Q";

    public static TheoryData<string> Operators()
    {
        var data = new TheoryData<string>();
        foreach (var op in new[]
        {
            "q","Q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re",
            "S","s","f","F","f*","B","B*","b","b*","W","n","W*",
            "CS","cs","G","g","RG","rg","K","k","SC","SCN","sc","scn",
            "sh",
            "BT","ET","Tf","TL","Tc","Tw","Tz","Tr","Ts","Td","TD","Tm","T*","Tj","TJ","'","\"",
            "BDC","BMC","EMC","MP","DP","BX","EX",
            "d0","d1","Do",
        }) data.Add(op);
        return data;
    }

    [Theory]
    [MemberData(nameof(Operators))]
    public void EachAnnexAOperator_IsSeenByQpdf_AndRecognisedByExcisesParser(string op)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        // (1) the oracle first: the operator really is in the bytes.
        QpdfTokensOfInventory().Should().Contain(op,
            $"qpdf's own decompressed view of the fixture must contain '{op}' — otherwise this " +
            "test would be asking excise to recognise something that is not there");

        // (2) excise must RECOGNISE it, not merely echo it.
        var parsed = new ContentStreamParser(Encoding.ASCII.GetBytes(Inventory)).Parse();
        var matches = parsed.Operators.Where(o => o.Name == op).ToList();

        matches.Should().NotBeEmpty($"excise's parser must expose '{op}' as an operator");
        matches.Should().Contain(o => o.Category != OperatorCategory.Unknown,
            $"excise must RECOGNISE '{op}' — the registry defines parse as \"Recognize, validate, " +
            "and expose a feature\", and an unrecognised operator is echoed with " +
            "Category=Unknown, which is what an operator excise has never heard of also produces");
    }

    /// <summary>
    /// The control that makes the assertion above falsifiable: the same parse
    /// path, an operator that does not exist, and the recognition check must
    /// FAIL for it. If this ever starts reporting a real category, every row
    /// of the theory above silently stops discriminating.
    /// </summary>
    [Fact]
    public void ExciseDoesNotRecogniseAnInventedOperator_SoRecognitionIsFalsifiable()
    {
        var parsed = new ContentStreamParser(
            Encoding.ASCII.GetBytes("q 99 88 zzNotARealOperator 7 blorp Q")).Parse();

        foreach (var invented in new[] { "zzNotARealOperator", "blorp" })
        {
            // The parser DROPS an operator it does not know rather than
            // echoing it, so "not recognised" shows up as absence here and as
            // Category=Unknown when it does surface. Either satisfies the
            // control; what must never happen is a real category.
            var found = parsed.Operators.Where(o => o.Name == invented).ToList();
            found.Should().OnlyContain(o => o.Category == OperatorCategory.Unknown,
                $"'{invented}' is not in ISO 32000-2, so it must never come back with a real " +
                "category. If it did, Category != Unknown would be true of everything and the " +
                "recognition assertion in the theory above would grade nothing");
        }
    }

    /// <summary>
    /// Every whitespace-delimited token in qpdf's own decompressed dump of the
    /// fixture's content stream — an independent tokenization, not excise's.
    /// </summary>
    private static HashSet<string> QpdfTokensOfInventory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"excise-opparse-{System.Guid.NewGuid():N}.pdf");
        System.IO.File.WriteAllBytes(path, BuildPdf(Inventory));
        try
        {
            var result = QpdfReferenceTool.FilteredStreamData(path, 4);
            if (result.Status == QpdfStreamDataStatus.ToolUnavailable)
                Assert.Skip($"qpdf unavailable: {result.Diagnostics}");
            result.Status.Should().Be(QpdfStreamDataStatus.Ok,
                $"qpdf must decode the fixture's content stream — {result.Diagnostics}");
            return Encoding.ASCII.GetString(result.Bytes)
                .Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet();
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
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
