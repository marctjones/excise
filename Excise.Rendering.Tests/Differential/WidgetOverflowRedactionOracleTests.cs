using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.WidgetOverflowFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2041: a term a widget's appearance draws outside its <c>/Rect</c>, clipped by its
/// <c>/BBox</c> (a scrolled multiline field), must be removed from the appearance and the value,
/// and the count reported must be the number of occurrences the INPUT holds. mutool does not paint
/// clipped text, so it cannot see the term either way; the oracles for the term are qpdf's decoded
/// object dump (every object, reachable or not, streams decoded) and the inflating byte scanner.
/// mutool checks the collateral: the text the page does show is still there. The expected count is
/// read from the input by qpdf, not from excise.
/// </summary>
public class WidgetOverflowRedactionOracleTests : IDisposable
{
    private const string Password = "overflow-2041";
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static void RequireTools()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed [requires: tool:mutool]");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed [requires: tool:qpdf]");
    }

    private string TempPath(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-2041-{tag}-{Guid.NewGuid():N}.pdf");
        _temp.Add(path);
        return path;
    }

    private string Write(byte[] bytes, string tag)
    {
        var path = TempPath(tag);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>qpdf's decoded dump of <paramref name="path"/> as Latin-1 text.</summary>
    private static string Dump(string path, string? password = null)
    {
        var dump = QpdfReferenceTool.DecodedObjectDump(path, password);
        dump.Should().NotBeNull("qpdf must be able to read the file");
        return Encoding.Latin1.GetString(dump!);
    }

    /// <summary>
    /// Occurrences of <paramref name="term"/> SHOWN by a content-stream operator in a qpdf dump: a
    /// literal or hex string operand of <c>Tj</c>, or an element of a <c>TJ</c> array read as one
    /// run (kerning numbers dropped). A field's <c>/V</c>, an XFA or XMP packet and the bodies of
    /// the object streams qpdf preserves are text, not shown strings, and do not count.
    /// </summary>
    private static int CountShown(string dump, string term)
    {
        var count = 0;
        foreach (Match m in Regex.Matches(dump, @"(\((?:[^()\\]|\\.)*\)|<[0-9A-Fa-f\s]*>)\s*Tj|\[((?:[^\]\\]|\\.)*)\]\s*TJ"))
        {
            var shown = m.Groups[1].Success
                ? StringValue(m.Groups[1].Value)
                : string.Concat(Regex.Matches(m.Groups[2].Value, @"\((?:[^()\\]|\\.)*\)|<[0-9A-Fa-f\s]*>")
                    .Select(s => StringValue(s.Value)));
            count += Regex.Matches(shown, Regex.Escape(term)).Count;
        }
        return count;
    }

    private static string StringValue(string token)
    {
        if (token.StartsWith('<'))
        {
            var hex = Regex.Replace(token[1..^1], @"\s", "");
            return Encoding.Latin1.GetString(Convert.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0"));
        }
        return Regex.Replace(token[1..^1], @"\\(.)", "$1");
    }

    public static TheoryData<string> Variants => new() { "plain", "encrypted", "object-streams" };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Synthetic_ClippedOverflowTerm_LeavesNoCopy_AndTheCountIsTheInputs(string variant)
    {
        RequireTools();
        var plain = Write(F.Build(), "in");
        var input = plain;
        string? password = null;
        switch (variant)
        {
            case "encrypted":
                input = TempPath("in-enc");
                QpdfReferenceTool.EncryptR4(plain, input, Password, Password + "-owner").Should().BeTrue();
                password = Password;
                break;
            case "object-streams":
                input = TempPath("in-objstm");
                QpdfReferenceTool.GenerateObjectStreams(plain, input).Should().BeTrue();
                Encoding.Latin1.GetString(File.ReadAllBytes(input)).Should().Contain("/ObjStm");
                break;
        }

        // The input, read by qpdf: the appearance draws the term twice, mutool paints neither.
        var inputDump = Dump(input, password);
        var expected = CountShown(inputDump, F.Term);
        expected.Should().Be(F.TermOccurrences, "planted: qpdf reads the clipped lines in the appearance");
        QpdfReferenceTool.AcroFormFieldValues(input, password)!["Notes"].Should().Contain(F.Term);
        var shownBefore = MutoolTextExtractor.ExtractPage(input, 1, password);
        shownBefore.Should().Contain(F.VisibleLine).And.Contain(F.NeighbourValue).And.NotContain(F.Term,
            "the clipped lines are not painted");

        RedactionReport report;
        byte[] saved;
        using (var document = PdfDocument.Open(File.ReadAllBytes(input), new PdfOpenOptions { UserPassword = password }))
        {
            report = document.RedactText(F.Term, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes(document.GetReEncryptionOptions(password));
        }
        var output = Write(saved, "out");

        report.MatchesLocated.Should().Be(expected);
        report.VerifiedRemovals.Should().Be(expected);
        report.Survived.Should().Be(0);

        var outputDump = Dump(output, password);
        outputDump.Should().NotContain(F.Term, "no appearance stream, value or orphan object keeps the term");
        QpdfReferenceTool.AcroFormFieldValues(output, password)!["Notes"].Should().NotContain(F.Term);
        if (password == null)
        {
            SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty();
        }
        else
        {
            QpdfReferenceTool.IsEncrypted(output).Should().BeTrue("an encrypted input saves encrypted");
            var decrypted = TempPath("out-dec");
            QpdfReferenceTool.Decrypt(output, decrypted, password, uncompressStreams: true).Should().BeTrue();
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(decrypted), F.Term).Should().BeEmpty();
        }

        // Collateral, read by mutool: the shown lines and the neighbour the clipped lines overlap.
        var shownAfter = MutoolTextExtractor.ExtractPage(output, 1, password);
        shownAfter.Should().Contain(F.VisibleLine).And.Contain(F.SecondLine).And.Contain(F.NeighbourValue);
        QpdfReferenceTool.AcroFormFieldValues(output, password)!["Keep"].Should().Be(F.NeighbourValue);
    }

    [Fact]
    public void Synthetic_SharedAppearance_BothFieldsClean_BothStillDrawTheirRest()
    {
        RequireTools();
        var input = Write(F.BuildShared(), "shared-in");
        var expected = CountShown(Dump(input), F.Term);
        expected.Should().Be(1, "planted: one shared stream draws the term once");
        Regex.Matches(MutoolTextExtractor.ExtractPage(input, 1)!, F.Term).Count.Should().Be(2, "both widgets paint it");

        RedactionReport report;
        byte[] saved;
        using (var document = PdfDocument.Open(File.ReadAllBytes(input)))
        {
            report = document.RedactText(F.Term, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }
        var output = Write(saved, "shared-out");

        report.MatchesLocated.Should().Be(2, "two widgets paint it, as mutool counts");
        report.Survived.Should().Be(0);
        Dump(output).Should().NotContain(F.Term);
        SavedPdfLeakScanner.FindTerm(saved, F.Term).Should().BeEmpty();
        var values = QpdfReferenceTool.AcroFormFieldValues(output)!;
        values["Alpha"].Should().NotContain(F.Term);
        values["Beta"].Should().NotContain(F.Term);
        var shown = MutoolTextExtractor.ExtractPage(output, 1)!;
        shown.Should().NotContain(F.Term);
        Regex.Matches(shown, F.SharedSurvivor).Count.Should().Be(2, "both widgets still draw the rest of the value");
        shown.Should().Contain(F.NeighbourValue);
    }

    /// <summary>
    /// The real file the issue was measured on: pdfjs annotation-text-widget.pdf, whose multiline
    /// field Tekstveld7 draws "Etiam" on its seventh line, below its <c>/Rect</c>, clipped. Before
    /// #2041: 1 located, 0 removed, reported survived, the term still in the saved file.
    /// </summary>
    [Fact]
    public void Corpus_AnnotationTextWidget_ClippedLineTerm_IsRemoved()
    {
        RequireTools();
        const string rel = "test-pdfs/pdfjs/annotation-text-widget.pdf";
        var fixture = TestRepoLayout.FindFile(rel);
        Assert.SkipWhen(fixture == null, TestRepoLayout.AbsenceReason("pdf.js corpus fixture annotation-text-widget.pdf", rel));
        const string term = "Etiam";

        var inputDump = Dump(fixture!);
        var expected = CountShown(inputDump, term);
        expected.Should().Be(1, "planted: qpdf reads one shown string operand of the term, in the field's appearance");
        var shownBefore = MutoolTextExtractor.ExtractPage(fixture!, 1)!;
        shownBefore.Should().NotContain(term, "the line is clipped");
        shownBefore.Should().Contain("Single line, read-only").And.Contain("Aliquam vitae").And.Contain("Pellentesque habitant");

        RedactionReport report;
        byte[] saved;
        using (var document = PdfDocument.Open(File.ReadAllBytes(fixture!)))
        {
            report = document.RedactText(term, RedactionOptions.Default with { DrawBox = false });
            saved = document.SaveToBytes();
        }
        var output = Write(saved, "corpus-out");

        report.MatchesLocated.Should().Be(expected);
        report.VerifiedRemovals.Should().Be(expected);
        report.Survived.Should().Be(0);
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        Dump(output).Should().NotContain(term);
        QpdfReferenceTool.AcroFormFieldValues(output)!.Values.Should().NotContain(v => v != null && v.Contains(term));

        var shownAfter = MutoolTextExtractor.ExtractPage(output, 1)!;
        shownAfter.Should().Contain("Single line, read-only", "the page text is not part of the delta");
        shownAfter.Should().Contain("Aliquam vitae").And.Contain("Pellentesque habitant",
            "the field's shown lines survive: its appearance is rewritten, not dropped");
    }
}
