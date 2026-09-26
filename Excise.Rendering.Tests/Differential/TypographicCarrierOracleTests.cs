using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1871 against mutool: the carriers a reader shows as page text, a span's
/// <c>/ActualText</c> and a text field's <c>/RV</c>, spell the term with a curly
/// quote, a typographic dash or a doubled space. The page matcher's fold finds
/// it; before #1871 the carrier matcher did not, and mutool still read it. The
/// saved bytes of every carrier are in <c>TypographicCarrierRedactionTests</c>
/// (Excise.Core.Tests).
/// </summary>
public sealed class TypographicCarrierOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-1871-{Guid.NewGuid():N}");

    public TypographicCarrierOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The term as typed, as the carrier spells it, and a part of it no cut may leave.</summary>
    private static readonly (string Term, string Held, string Part)[] Spellings =
    {
        ("O'Brien", "O’Brien", "Brien"),
        ("12-345", "12–345", "345"),
        ("Smith Jones", "Smith  Jones", "Jones"),
    };

    private static byte[] Build(string carrier, string value) => carrier switch
    {
        "marked-content /ActualText" => MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Utf16Hex(value)),
        "field /RV" => CarrierTrapFixtures.Field(null,
            $"/FT /Tx /T (name) /RV {MarkedContentCarrierFixtures.Utf16Hex($"<body><p>{value}</p></body>")}"),
        _ => throw new ArgumentOutOfRangeException(nameof(carrier)),
    };

    public static TheoryData<string, RedactionProfile, int> Cases()
    {
        var data = new TheoryData<string, RedactionProfile, int>();
        foreach (var carrier in new[] { "marked-content /ActualText", "field /RV" })
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                for (var spelling = 0; spelling < Spellings.Length; spelling++)
                    data.Add(carrier, profile, spelling);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MutoolReadsNoneOfTheTerm_WhenTheCarrierSpellsItTypographically(string carrier, RedactionProfile profile, int spelling)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool is the independent extractor (brew install mupdf-tools)");
        var (term, held, part) = Spellings[spelling];
        var input = Build(carrier, $"Wren {held} Heron");
        Read("input", input).Should().Contain(part, "input-side control: mutool reads the carrier as page text");

        using var document = PdfDocument.Open(input);
        document.RedactText(term, RedactionOptions.ForProfile(profile));
        var saved = document.SaveToBytes();

        var reading = Read($"{profile}", saved);
        reading.Should().NotContain(part, $"{carrier}, {profile}: mutool read '{reading}'");
        SavedPdfLeakScanner.FindTerm(saved, held).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
    }

    private string Read(string name, byte[] pdf)
    {
        var path = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return MutoolTextExtractor.ExtractPage(path, 1) ?? "";
    }
}
