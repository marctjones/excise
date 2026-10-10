using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using static Excise.Core.Tests.Text.Segmentation.FormXObjectRedactionTests;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #1901: a <c>Q</c> inside a nested stream (a form XObject, or an annotation
/// appearance) that has no matching <c>q</c> in that stream must not pop the
/// INVOKER's saved graphics state. §8.10.1 brackets a <c>Do</c> in an implicit
/// <c>q … Q</c> and §8.4.2 requires q/Q to balance within a content stream, so
/// the stray <c>Q</c> is ignored; mutool does the same, and it is the oracle
/// here.
///
/// <para>Before the fix, on the first fixture below, the walker put FORMTEXT
/// at y=500 and LATERTEXT at y=500 where mutool reads 300 and 700: the form's
/// <c>Q</c> popped the page's <c>q</c>, <c>RunNested</c> restored the current
/// state but not the popped stack entry, and the page's own <c>Q</c> then had
/// nothing to pop, so the page's <c>cm</c> leaked onto everything after it.
/// <c>ContentStreamParser</c> (which never nests) put LATERTEXT at 700, so a
/// term redaction located the text through the letters at one place and
/// removed glyphs at another: both terms survived in the saved file, reported
/// as <c>RemovalUnverified</c>.</para>
///
/// <para>The walker recurses only into form XObjects and annotation
/// appearances (both through <c>RunNested</c>); it does not execute Type3
/// CharProcs or tiling pattern cells, so there is no third nesting kind to
/// pin here.</para>
/// </summary>
public class StrayRestoreInNestedStreamTests
{
    private const double Tolerance = 0.05;

    private static byte[] Page(string pageContent, params string[] forms)
    {
        var objects = new List<byte[]>
        {
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << " +
                string.Concat(forms.Select((_, i) => $"/Fm{i} {6 + i} 0 R ")) + ">> >> >>"),
            Stream("", pageContent),
            Obj(HelveticaFont),
        };
        // Every form can invoke the NEXT one as /Inner (for the nested case).
        for (var i = 0; i < forms.Length; i++)
        {
            var inner = i + 1 < forms.Length ? $"/XObject << /Inner {7 + i} 0 R >> " : "";
            objects.Add(Stream(
                "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 5 0 R >> {inner}>>",
                forms[i]));
        }
        return Build(objects.ToArray());
    }

    /// <summary>The excise letter origin of the first glyph of <paramref name="word"/>.</summary>
    private static (double X, double Y) ExciseStart(byte[] pdf, string word)
    {
        using var doc = PdfDocument.Open(pdf);
        var letters = new TextExtractor(doc.GetPage(1)) { IncludeFormFieldValues = false }
            .ExtractLetters()
            .ToList();
        var text = string.Concat(letters.Select(l => l.Value));
        var index = text.IndexOf(word, System.StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, $"excise should extract \"{word}\" from \"{text}\"");
        return (letters[index].StartX, letters[index].StartY);
    }

    /// <summary>
    /// mutool's word box for <paramref name="word"/>. Its BOTTOM is not the
    /// baseline (mutool models descent), so only the left edge is compared
    /// exactly; the vertical check uses mutool's box as a band.
    /// </summary>
    private static MutoolStextOracle.Word MutoolWord(byte[] pdf, string word)
    {
        var words = MutoolStextOracle.Words(pdf, 1, 792);
        return words.Should().ContainSingle(w => w.Text == word,
            $"mutool should read \"{word}\" exactly once").Subject;
    }

    private static void AssertAgreesWithMutool(byte[] pdf, params string[] wordsToCheck)
    {
        Assert.SkipWhen(!MutoolStextOracle.IsAvailable, "mutool not on PATH");
        foreach (var word in wordsToCheck)
        {
            var oracle = MutoolWord(pdf, word);
            var (x, y) = ExciseStart(pdf, word);
            x.Should().BeApproximately(oracle.Left, Tolerance, $"{word} starts where mutool draws it");
            y.Should().BeInRange(oracle.Bottom - 0.5, oracle.Top,
                $"{word}'s baseline lies inside mutool's box [{oracle.Bottom}, {oracle.Top}]");
        }
    }

    [Fact]
    public void StrayQInForm_DoesNotPopThePageState()
    {
        // mutool: FORMTEXT at y≈300 (the page cm applies inside the form),
        // LATERTEXT at y≈700 (the page's own Q undid the cm).
        var pdf = Page(
            "q 1 0 0 1 0 -200 cm /Fm0 Do Q BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "Q BT /F1 12 Tf 100 500 Td (FORMTEXT) Tj ET");

        AssertAgreesWithMutool(pdf, "FORMTEXT", "LATERTEXT");
        ExciseStart(pdf, "FORMTEXT").Y.Should().BeApproximately(300, Tolerance);
        ExciseStart(pdf, "LATERTEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Fact]
    public void StrayQInForm_TextInsideThePageBracket_KeepsThePageCm()
    {
        // The page text between the Do and the page's Q is drawn under the
        // page's cm. RunNested restores the CURRENT state, so this held before
        // #1901 too; pinned so the fix cannot trade one case for the other.
        var pdf = Page(
            "q 1 0 0 1 0 -200 cm /Fm0 Do BT /F1 12 Tf 100 700 Td (INSIDETEXT) Tj ET Q " +
            "BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "Q BT /F1 12 Tf 100 600 Td (FORMTEXT) Tj ET");

        AssertAgreesWithMutool(pdf, "FORMTEXT", "INSIDETEXT", "LATERTEXT");
        ExciseStart(pdf, "INSIDETEXT").Y.Should().BeApproximately(500, Tolerance);
        ExciseStart(pdf, "LATERTEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Fact]
    public void UnclosedqInForm_DoesNotLeakIntoThePage()
    {
        var pdf = Page(
            "q 1 0 0 1 0 -200 cm /Fm0 Do Q BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "q 1 0 0 1 0 50 cm BT /F1 12 Tf 100 500 Td (FORMTEXT) Tj ET");

        AssertAgreesWithMutool(pdf, "FORMTEXT", "LATERTEXT");
        ExciseStart(pdf, "FORMTEXT").Y.Should().BeApproximately(350, Tolerance);
        ExciseStart(pdf, "LATERTEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Fact]
    public void BalancedForm_IsUnchanged()
    {
        var pdf = Page(
            "q 1 0 0 1 0 -200 cm /Fm0 Do Q BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "q 1 0 0 1 0 50 cm BT /F1 12 Tf 100 500 Td (FORMTEXT) Tj ET Q " +
            "BT /F1 12 Tf 100 400 Td (FORMTAIL) Tj ET");

        AssertAgreesWithMutool(pdf, "FORMTEXT", "FORMTAIL", "LATERTEXT");
        ExciseStart(pdf, "FORMTEXT").Y.Should().BeApproximately(350, Tolerance);
        ExciseStart(pdf, "FORMTAIL").Y.Should().BeApproximately(200, Tolerance);
        ExciseStart(pdf, "LATERTEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Fact]
    public void SequentialForms_EachStrayQIsIgnored()
    {
        // Two page-level brackets, each with its own cm; both forms carry a
        // stray Q. Before #1901 the first form emptied the inner bracket, the
        // second popped the outer one, and every later glyph inherited both cms.
        var pdf = Page(
            "q 1 0 0 1 0 -100 cm q 1 0 0 1 0 -100 cm /Fm0 Do Q " +
            "BT /F1 12 Tf 100 700 Td (MIDTEXT) Tj ET /Fm1 Do Q " +
            "BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "Q BT /F1 12 Tf 100 500 Td (FIRSTFORM) Tj ET",
            "Q BT /F1 12 Tf 100 400 Td (SECONDFORM) Tj ET");

        AssertAgreesWithMutool(pdf, "FIRSTFORM", "MIDTEXT", "SECONDFORM", "LATERTEXT");
        ExciseStart(pdf, "FIRSTFORM").Y.Should().BeApproximately(300, Tolerance);
        ExciseStart(pdf, "MIDTEXT").Y.Should().BeApproximately(600, Tolerance);
        ExciseStart(pdf, "SECONDFORM").Y.Should().BeApproximately(300, Tolerance);
        ExciseStart(pdf, "LATERTEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Fact]
    public void StrayQInInnerForm_DoesNotPopTheOuterFormsState()
    {
        // Page → Fm0 (balanced, brackets a cm around the inner Do) → Inner
        // (stray Q). The inner form's floor includes the outer form's push, so
        // its Q must not pop that either.
        var pdf = Page(
            "q 1 0 0 1 0 -100 cm /Fm0 Do Q BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "q 1 0 0 1 0 -50 cm /Inner Do BT /F1 12 Tf 100 500 Td (OUTERAFTER) Tj ET Q " +
            "BT /F1 12 Tf 100 500 Td (OUTERTAIL) Tj ET",
            "Q BT /F1 12 Tf 100 600 Td (INNERTEXT) Tj ET");

        AssertAgreesWithMutool(pdf, "INNERTEXT", "OUTERAFTER", "OUTERTAIL", "LATERTEXT");
        ExciseStart(pdf, "INNERTEXT").Y.Should().BeApproximately(450, Tolerance);
        ExciseStart(pdf, "OUTERAFTER").Y.Should().BeApproximately(350, Tolerance);
        ExciseStart(pdf, "OUTERTAIL").Y.Should().BeApproximately(400, Tolerance);
        ExciseStart(pdf, "LATERTEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Fact]
    public void StrayQInPageStream_IsStillIgnored()
    {
        // The page's own floor is 0: a Q with nothing to pop is a no-op, as
        // before.
        var pdf = Page("Q BT /F1 12 Tf 100 700 Td (PAGETEXT) Tj ET");
        AssertAgreesWithMutool(pdf, "PAGETEXT");
        ExciseStart(pdf, "PAGETEXT").Y.Should().BeApproximately(700, Tolerance);
    }

    [Theory]
    [InlineData("LATERTEXT")]
    [InlineData("FORMTEXT")]
    public void RedactText_AfterAFormWithAStrayQ_RemovesTheTerm(string term)
    {
        Assert.SkipWhen(!MutoolTextOracle.IsAvailable, "mutool not on PATH");
        var pdf = Page(
            "q 1 0 0 1 0 -200 cm /Fm0 Do Q BT /F1 12 Tf 100 700 Td (LATERTEXT) Tj ET",
            "Q BT /F1 12 Tf 100 500 Td (FORMTEXT) Tj ET");
        var other = term == "LATERTEXT" ? "FORMTEXT" : "LATERTEXT";

        using var doc = PdfDocument.Open(pdf);
        var report = doc.RedactText(term, RedactionOptions.Default with { DrawBox = false });
        var saved = doc.SaveToBytes();

        report.Pages.Should().ContainSingle().Which.OccurrencesRemainingAfter.Should().Be(0);
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty(
            "the term must be gone from every stream of the saved file");
        var mutoolText = MutoolTextOracle.ExtractAllPages(saved);
        mutoolText.Should().NotContain(term, "an independent reader must not find the term");
        mutoolText.Should().Contain(other, "the other string is outside the redaction and survives");
    }
}
