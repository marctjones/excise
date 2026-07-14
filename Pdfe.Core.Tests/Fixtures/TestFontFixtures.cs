using System.IO;
using System.Linq;
using System.Reflection;

namespace Pdfe.Core.Tests.Fixtures;

/// <summary>
/// Shared loader for embedded font test fixtures (#603). DejaVu Sans is
/// bundled as an <c>EmbeddedResource</c> (Fixtures/Fonts/DejaVuSans.ttf,
/// see NOTICE.md) rather than read from a system font path — the tests that
/// consume this used to hard-code
/// <c>/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf</c>, a Linux-only
/// path, and silently skipped on every macOS and Windows dev machine.
/// </summary>
internal static class TestFontFixtures
{
    public static byte[] LoadDejaVuSansBytes() => LoadEmbedded("DejaVuSans.ttf");

    /// <summary>A CFF-flavored OpenType ('OTTO') fixture — Libertinus Serif, SIL OFL 1.1.</summary>
    public static byte[] LoadLibertinusSerifCffBytes() => LoadEmbedded("LibertinusSerif-Regular.otf");

    private static byte[] LoadEmbedded(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, System.StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
