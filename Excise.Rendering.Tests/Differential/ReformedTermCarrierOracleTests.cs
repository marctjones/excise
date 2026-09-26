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
/// #1860 against mutool: the carriers a reader shows as page text, a span's
/// <c>/ActualText</c> and a text field's value, hold the term nested inside
/// itself. A cut that ran once would leave mutool reading <c>KESTREL</c> out of
/// <c>KESKESTRELTREL</c>, or out of <c>KESkestrelTREL</c> when the term is
/// <c>kestrel</c>. The saved bytes are in <c>ReformedTermCarrierRedactionTests</c>
/// (Excise.Core.Tests) for every carrier.
/// </summary>
public sealed class ReformedTermCarrierOracleTests : IDisposable
{
    private const string Reformed = "KESTREL";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-1860-{Guid.NewGuid():N}");

    public ReformedTermCarrierOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly (string Term, string Nested)[] Nestings =
    {
        ("KESTREL", "KESKESTRELTREL"),
        ("kestrel", "KESkestrelTREL"),
    };

    private static byte[] Build(string carrier, string value) => carrier switch
    {
        "marked-content /ActualText" => MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Literal(value)),
        "field /RV" => CarrierTrapFixtures.Field(null, $"/FT /Tx /T (name) /RV (<body><p>{value}</p></body>)"),
        _ => throw new ArgumentOutOfRangeException(nameof(carrier)),
    };

    public static TheoryData<string, RedactionProfile, int> Cases()
    {
        var data = new TheoryData<string, RedactionProfile, int>();
        foreach (var carrier in new[] { "marked-content /ActualText", "field /RV" })
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                for (var nesting = 0; nesting < Nestings.Length; nesting++)
                    data.Add(carrier, profile, nesting);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MutoolReadsNoneOfTheTerm_AfterACutThatWouldReformIt(string carrier, RedactionProfile profile, int nesting)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool is the independent extractor (brew install mupdf-tools)");
        var (term, nested) = Nestings[nesting];
        var input = Build(carrier, $"Wren {nested} Heron");
        Read("input", input).Should().ContainEquivalentOf(Reformed, "input-side control: mutool reads the carrier as page text");

        using var document = PdfDocument.Open(input);
        document.RedactText(term, RedactionOptions.ForProfile(profile));
        var saved = document.SaveToBytes();

        var reading = Read($"{profile}", saved);
        reading.Should().NotContainEquivalentOf(Reformed, $"{carrier}, {profile}: mutool read '{reading}'");
        SavedPdfLeakScanner.FindTerm(saved, Reformed).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
    }

    private string Read(string name, byte[] pdf)
    {
        var path = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return MutoolTextExtractor.ExtractPage(path, 1) ?? "";
    }
}
