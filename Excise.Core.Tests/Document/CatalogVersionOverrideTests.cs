using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// §7.5.5: the catalog's <c>/Version</c> overrides the file header (#1533).
///
/// <para>The entry exists so an incremental update can raise a file's version
/// without rewriting byte 0. excise read the header alone, so a 1.7 document
/// with a 1.4 header was reported as 1.4 — and the one behaviour gate that
/// consumes the version (<c>ShouldUseCompressedObjects</c>, via
/// <c>VersionAtLeast(…, 1, 5)</c>) declined object streams the file was
/// entitled to use.</para>
/// </summary>
public class CatalogVersionOverrideTests
{
    [Theory]
    // The case the spec entry exists for: an update raises the version.
    [InlineData("1.4", "/Version /1.7", "1.7")]
    [InlineData("1.3", "/Version /2.0", "2.0")]
    // No entry: the header stands.
    [InlineData("1.4", "", "1.4")]
    // ⚠️ A catalog claiming LESS than the header does not lower it. Every
    // consumer of this value is a capability gate, so honouring a downgrade
    // would let a malformed file talk excise out of something the header
    // already promised.
    [InlineData("1.7", "/Version /1.4", "1.7")]
    // Garbage in the catalog is ignored rather than taken literally.
    [InlineData("1.5", "/Version /nonsense", "1.5")]
    [InlineData("1.5", "/Version (1.7)", "1.5")]
    public void TheVersionIsTheLaterOfHeaderAndCatalog(string header, string catalogEntry, string expected)
    {
        using var document = PdfDocument.Open(BuildPdf(header, catalogEntry));
        document.Version.Should().Be(expected);
    }

    /// <summary>
    /// A minimal one-page PDF with the given header version and an optional
    /// extra catalog entry. Offsets are computed, so the xref is real.
    /// </summary>
    private static byte[] BuildPdf(string headerVersion, string catalogEntry)
    {
        var objects = new[]
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogEntry} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };

        var sb = new StringBuilder();
        sb.Append($"%PDF-{headerVersion}\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
