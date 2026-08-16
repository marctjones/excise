using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Shared fixture plumbing for the content-stream gates: a minimal single-page
/// PDF around arbitrary content bytes, and access to what each SINK made of
/// them.
///
/// <para>Named <c>ParityFixture</c> and living inside
/// <c>ParserDifferentialTests</c> until #997, when that gate was deleted — it
/// existed to compare two state machines, and there is one. The builder
/// outlived it because the gates that DO still have a subject
/// (<see cref="GraphicsStateTextParameterTests"/>, which checks a spec
/// property, and <c>GraphicsStateTextParameterOracleTests</c>, which checks
/// mutool) need the same one-page PDF.</para>
/// </summary>
internal static class ContentStreamFixture
{
    public static ContentStream ParseOperators(PdfPage page) =>
        new ContentStreamParser(page.GetContentStreamBytes(), page).Parse();

    public static IReadOnlyList<Letter> ExtractLetters(PdfPage page) =>
        new TextExtractor(page) { IncludeFormFieldValues = false }.ExtractLetters();

    /// <summary>
    /// "ok" or the exception type name — so a rejection can be compared
    /// between the two machines without either being allowed to throw raw.
    /// </summary>
    public static string Outcome(Action action)
    {
        try
        {
            action();
            return "ok";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>
    /// The private static <c>Operators</c> table of either state machine.
    /// Reflection is the honest tool here: the tables ARE the thing under
    /// comparison, and reading them directly is exact where a behavioural
    /// probe has false negatives (see the caller). A rename fails this
    /// loudly rather than silently comparing nothing.
    /// </summary>
    /// <summary>
    /// Whether a type carries a private static <c>Operators</c> table of its
    /// own — i.e. whether it is a second content-stream state machine.
    /// </summary>
    public static bool HasOperatorTable(Type type) =>
        type.GetField("Operators",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) != null;

    public static ISet<string> OperatorSet(Type stateMachine)
    {
        var field = stateMachine.GetField("Operators",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull(
            $"{stateMachine.Name} must keep its operator table in a static field " +
            "named Operators for the #980 parity gate to compare it");

        var value = field!.GetValue(null) as IEnumerable<string>;
        value.Should().NotBeNull($"{stateMachine.Name}.Operators must be a string set");
        return new HashSet<string>(value!, StringComparer.Ordinal);
    }

    /// <summary>
    /// A one-page PDF whose /F1 is a Type0 / Identity-H font whose
    /// <c>/DescendantFonts</c> is an INDIRECT REFERENCE (object 6), the shape
    /// real producers emit and the one a bare `is PdfArray` cast misses. The
    /// descendant's /W gives CIDs 0x48/0x65/0x6C the widths 1500/250/500, all
    /// far from /DW and from any flat default, so a parser that fails to reach
    /// the table cannot land on the right answer by accident.
    /// </summary>
    public static byte[] BuildType0(string content) => Build(
        content,
        fontObject:
            "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Parity "
          + "/Encoding /Identity-H /DescendantFonts 6 0 R >>\nendobj\n",
        extraObjects:
            "6 0 obj\n[ 7 0 R ]\nendobj\n"
          + "7 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Parity "
          + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
          + "/DW 1000 /W [ 72 [1500] 101 [250] 108 [500] ] >>\nendobj\n");

    /// <summary>
    /// A minimal one-page PDF whose content stream is exactly
    /// <paramref name="content"/>, Latin-1 encoded so binary sample bytes
    /// survive verbatim.
    /// </summary>
    /// <param name="extraFontResources">Additional entries for the page's
    /// <c>/Resources /Font</c> dictionary, e.g. <c>"/F2 6 0 R"</c>, whose
    /// objects come in via <paramref name="extraObjects"/>. Used by the #983
    /// gate, which needs a SECOND font to make a bracketed <c>Tf</c> observable
    /// through something other than the size.</param>
    /// <param name="extraResources">Additional entries for the page's
    /// <c>/Resources</c> dictionary itself, e.g. an <c>/ExtGState</c>
    /// sub-dictionary. Used by the #990 gate, which needs a <c>gs</c> that
    /// carries a Table 58 <c>/Font</c> entry.</param>
    public static byte[] Build(
        string content,
        string fontObject = "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        string extraObjects = "",
        string extraFontResources = "",
        string extraResources = "")
    {
        var body = Encoding.Latin1.GetBytes(content);
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        // Objects after the font are written as one block; the xref below is a
        // single free entry plus a run, and any object it does not name is
        // still reachable because PdfDocument reconstructs on a bad offset.
        // Keeping the table honest for 1-5 is enough for every fixture here.
        W("%PDF-1.7\n");
        var offsets = new long[6];

        offsets[1] = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R " + extraFontResources + " >> "
          + extraResources + " >> >>\nendobj\n");
        offsets[4] = ms.Position;
        W($"4 0 obj\n<< /Length {body.Length} >>\nstream\n");
        ms.Write(body);
        W("\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        W(fontObject);

        var extraOffsets = new List<long>();
        foreach (var obj in SplitObjects(extraObjects))
        {
            extraOffsets.Add(ms.Position);
            W(obj);
        }

        var size = 6 + extraOffsets.Count;
        var xref = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        foreach (var offset in extraOffsets)
            W($"{offset:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Split a concatenation of "N 0 obj … endobj\n" bodies.</summary>
    private static IEnumerable<string> SplitObjects(string objects)
    {
        int pos = 0;
        while (pos < objects.Length)
        {
            var end = objects.IndexOf("endobj\n", pos, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return objects[pos..(end + "endobj\n".Length)];
            pos = end + "endobj\n".Length;
        }
    }
}
