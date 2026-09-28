using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// Text excise draws with an embedded font (<see cref="PdfFont.FromTrueType(byte[], double)"/>)
/// must be redactable in the SAME document, before any save and reopen: the app lays out an
/// XFA form at open and the user redacts that live document (#1577). The embedded font's
/// ToUnicode CMap used to be an empty placeholder until save, and was then stored encoded
/// only, so in-memory text redaction found nothing and still reported success.
/// </summary>
public class EmbeddedFontInMemoryRedactionTests
{
    private const string Secret = "Quillfeather";
    private const string Survivor = "Survivor";

    private static PdfDocument Drawn()
    {
        var document = PdfDocument.CreateNew();
        var page = document.Pages.AddBlank(612, 792);
        using (var graphics = page.GetGraphics())
            graphics.DrawString($"{Survivor} {Secret}", PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 12), PdfBrush.Black, 72, 700);
        return document;
    }

    private static void AssertRedacted(PdfDocument document)
    {
        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        if (MutoolTextOracle.IsAvailable)
        {
            var text = MutoolTextOracle.ExtractAllPages(saved);
            text.Should().NotContain(Secret, "an independent extractor must not read the redacted term");
            text.Should().Contain(Survivor, "only the term goes");
        }
    }

    [Fact]
    public void TermDrawnWithAnEmbeddedFont_IsRedactedBeforeAnySave()
    {
        using var document = Drawn();

        document.RedactText(Secret, RedactionOptions.Default).MatchesLocated.Should().Be(1, "excise's own extraction must read the term it drew");
        AssertRedacted(document);
    }

    [Fact]
    public void TermDrawnWithAnEmbeddedFont_IsRedactedAfterASave_InTheSameDocument()
    {
        using var document = Drawn();
        _ = document.SaveToBytes();

        document.RedactText(Secret, RedactionOptions.Default).MatchesLocated.Should().Be(1, "excise's own extraction must read the term it drew");
        AssertRedacted(document);
    }
}
