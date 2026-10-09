using System.IO;
using AwesomeAssertions;
using Excise.Core.Tests.Content;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2034 — the qpdf object dump is the second oracle for carrier traps and
/// stale appearances. It inflates streams but read them as plain text, so a
/// value drawn as a kerned <c>TJ</c> or one glyph per <c>Tj</c> was in no
/// string of the dump. It now carries what each stream draws.
/// </summary>
public class QpdfDumpShownTextTests
{
    private static string Dump(string content)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is required (brew install qpdf)");
        var path = Path.Combine(Path.GetTempPath(), $"qpdf-shown-{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, ContentStreamFixture.Build(content));
        try { return CarrierTrapIndependentCorroborationTests.QpdfDump(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AKernedTjValue_IsInTheDump()
    {
        Dump("BT /F1 12 Tf 20 700 Td [(ALP) -20 (HA) 10 (OLD)] TJ ET\n").Should().Contain("ALPHAOLD");
    }

    [Fact]
    public void OneGlyphPerTj_IsInTheDump()
    {
        Dump("BT /F1 12 Tf 20 700 Td (A) Tj (L) Tj (P) Tj (H) Tj (A) Tj (O) Tj (L) Tj (D) Tj ET\n").Should().Contain("ALPHAOLD");
    }

    [Fact]
    public void TextOnTwoLines_IsNotJoinedInTheDump()
    {
        Dump("BT /F1 12 Tf 20 700 Td (ALPHA) Tj 0 -14 Td (OLD) Tj ET\n").Should().NotContain("ALPHAOLD");
    }
}
