using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1854 against the independent extractors: mutool and pdftotext both read a
/// span's <c>/ActualText</c> as the page's text, so a property list holding the
/// term over glyphs that do not spell it is a live leak, not dormant bytes.
/// After a redaction of the term neither may read it; with the carrier switched
/// off (the planted failure) each tool that read it before must still read it,
/// or this test could not see the leak it guards.
/// </summary>
public sealed class MarkedContentActualTextOracleTests : IDisposable
{
    private const string Term = "KESTREL";
    private const string Arabic = "سلام";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-1854-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _out;

    public MarkedContentActualTextOracleTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> Placements() => new(MarkedContentCarrierFixtures.Placements.Keys);

    [Theory]
    [InlineData(Term, "literal")]
    [InlineData(Term, "utf16-hex")]
    [InlineData(Term, "utf16-octal")]
    [InlineData(Arabic, "utf16-hex")]
    [InlineData(Arabic, "utf16-octal")]
    public void TheIssueFixture_BothToolsReadTheTerm_UntilItIsRedacted(string term, string encoding)
    {
        SkipUnlessTools();
        var value = $"{term} chapter";
        var input = MarkedContentCarrierFixtures.Inline(encoding switch
        {
            "literal" => MarkedContentCarrierFixtures.Literal(value),
            "utf16-hex" => MarkedContentCarrierFixtures.Utf16Hex(value),
            _ => MarkedContentCarrierFixtures.Utf16Octal(value),
        });

        var (mutool, pdftotext) = Extract("input", input);
        Reads(mutool, term).Should().BeTrue("the issue: mutool reads the span's /ActualText as page 1's text");
        Reads(pdftotext, term).Should().BeTrue("the issue: pdftotext reads it too");

        foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
        {
            var (m, p) = Extract($"{profile}", Redacted(input, term, RedactionOptions.ForProfile(profile)));
            Reads(m, term).Should().BeFalse($"{profile}: mutool read '{m}'");
            Reads(p, term).Should().BeFalse($"{profile}: pdftotext read '{p}'");
        }
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void NoToolReadsTheTermAfterRedaction_WhereverThePropertyListSits(string placement)
    {
        SkipUnlessTools();
        var input = MarkedContentCarrierFixtures.Placements[placement](MarkedContentCarrierFixtures.Literal($"{Term} chapter"));

        var (mutoolBefore, pdftotextBefore) = Extract("input", input);
        var (mutoolPlanted, pdftotextPlanted) = Extract("planted", Redacted(input, Term,
            RedactionOptions.Default with { Carriers = RedactionCarriers.All & ~RedactionCarriers.MarkedContent }));
        _out.WriteLine($"{placement}: before mutool={Reads(mutoolBefore, Term)} pdftotext={Reads(pdftotextBefore, Term)}");

        if (Reads(mutoolBefore, Term))
            Reads(mutoolPlanted, Term).Should().BeTrue("the planted failure must be visible to the tool that read the term");
        if (Reads(pdftotextBefore, Term))
            Reads(pdftotextPlanted, Term).Should().BeTrue("the planted failure must be visible to the tool that read the term");

        foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
        {
            var (m, p) = Extract($"{profile}", Redacted(input, Term, RedactionOptions.ForProfile(profile)));
            Reads(m, Term).Should().BeFalse($"{placement}, {profile}: mutool read '{m}'");
            Reads(p, Term).Should().BeFalse($"{placement}, {profile}: pdftotext read '{p}'");
        }
    }

    /// <summary>
    /// Strict, in either reading direction: pdftotext emits Arabic in visual
    /// order, and mupdf builds differ (see <see cref="RtlOracleText"/>).
    /// </summary>
    private static bool Reads(string reading, string term)
    {
        var text = RtlOracleText.StripSpacing(reading);
        var reversed = new string(term.Reverse().ToArray());
        return text.Contains(term, StringComparison.Ordinal) || text.Contains(reversed, StringComparison.Ordinal);
    }

    private static void SkipUnlessTools() =>
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable && PdftotextTextExtractor.IsAvailable,
            "mutool and pdftotext are the independent extractors (brew install mupdf-tools poppler)");

    private static byte[] Redacted(byte[] input, string term, RedactionOptions options)
    {
        using var document = PdfDocument.Open(input);
        document.RedactText(term, options);
        var saved = document.SaveToBytes();
        if (options.Carriers.HasFlag(RedactionCarriers.MarkedContent))
            SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        return saved;
    }

    private (string Mutool, string Pdftotext) Extract(string name, byte[] pdf)
    {
        var path = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return (MutoolTextExtractor.ExtractPage(path, 1) ?? "", PdftotextTextExtractor.ExtractPage(path, 1) ?? "");
    }
}
