using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1444 adds a text carrier to every authored form field: the widget's
/// <c>/AP /N</c> stream now draws the field value, alongside <c>/V</c>. A
/// redaction of that value must remove it from both, in the saved bytes and as
/// read by an extractor that is not excise.
/// </summary>
public class AuthoredWidgetAppearanceRedactionTests
{
    private const string Secret = "SECRETVALUE1444";
    private const string Survivor = "Survivor paragraph text";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactingAnAuthoredFieldValue_RemovesItFromEveryCarrier(bool embeddedFont)
    {
        var builder = PdfDocumentBuilder.Create();
        if (embeddedFont)
            builder = builder.DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11));
        var authored = builder
            .Paragraph(Survivor)
            .TextField("Name", "name", defaultValue: Secret)
            .SaveToBytes();

        SavedPdfLeakScanner.FindTerm(authored, Secret).Should().NotBeEmpty(
            "precondition: the authored value is in the file, so the scan below can fail");

        byte[] redacted;
        using (var doc = PdfDocument.Open(authored))
        {
            doc.RedactText(Secret);
            redacted = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(redacted, Secret).Should().BeEmpty(
            "the value must be gone from /V, /AP and every other carrier in the saved bytes");

        if (MutoolTextOracle.IsAvailable)
        {
            var text = MutoolTextOracle.ExtractAllPages(redacted);
            text.Should().NotContain(Secret, "an independent extractor must not read the redacted value");
            text.Should().Contain(Survivor, "redaction must not destroy unrelated page text");
        }
    }
}
