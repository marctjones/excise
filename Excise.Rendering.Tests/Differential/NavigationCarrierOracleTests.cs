using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Navigation carriers read back by qpdf, not by excise: a page-label prefix
/// (#1853). qpdf must see the term in the input, must not see it after either
/// profile's <c>RedactText</c>, and must pass the output with <c>--check</c>.
/// </summary>
public sealed class NavigationCarrierOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"navigation-carriers-{Guid.NewGuid():N}");

    public NavigationCarrierOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    [Theory]
    [InlineData("literal", RedactionProfile.Standard)]
    [InlineData("literal", RedactionProfile.Maximum)]
    [InlineData("arabic", RedactionProfile.Standard)]
    [InlineData("arabic", RedactionProfile.Maximum)]
    public void PageLabelPrefix_IsGoneToQpdf(string shape, RedactionProfile profile)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent reader (brew install qpdf)");
        var (prefix, term) = shape == "arabic"
            ? (Utf16Hex("سلام chapter"), "سلام")
            : ("(KESTREL chapter)", "KESTREL");
        var input = Path.Combine(_dir, $"{shape}-in.pdf");
        File.WriteAllBytes(input, CarrierTrapFixtures.WithCatalog($"/PageLabels << /Nums [0 << /S /D /P {prefix} >>] >>"));
        CarrierTrapIndependentCorroborationTests.QpdfDump(input).Should().Contain(term, "input-side control");

        var output = Path.Combine(_dir, $"{shape}-{profile}.pdf");
        using (var document = PdfDocument.Open(File.ReadAllBytes(input)))
        {
            document.RedactText(term, RedactionOptions.ForProfile(profile));
            document.Save(output);
        }

        QpdfReferenceTool.Check(output)!.Value.Success.Should().BeTrue("the redacted file must stay valid to qpdf");
        var dump = CarrierTrapIndependentCorroborationTests.QpdfDump(output);
        dump.Should().NotContain(term, "qpdf decodes every string in the file, the prefix included");
        if (profile == RedactionProfile.Standard)
            dump.Should().Contain(" chapter", "Strip keeps the rest of the prefix");
    }
}
