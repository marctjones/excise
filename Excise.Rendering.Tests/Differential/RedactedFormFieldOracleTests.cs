using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1499 — the independent-extractor half of the redacted-form check, and the
/// only one that can see the appearance stream at all.
///
/// <para><b>Why the embedded font matters here.</b> The PDF/A form draws its
/// field value through an embedded Identity-H font, so the term inside the
/// widget's <c>/AP</c> is a run of two-byte GIDs, not text.
/// <see cref="SavedPdfLeakScanner"/> is structurally blind to that: it goes
/// clean the moment <c>/V</c> is scrubbed, whatever the appearance still draws.
/// The only thing that reads those GIDs back as characters is a tool with a
/// ToUnicode CMap reader — and excise's own re-extraction inside
/// <c>AppearanceStreamRedactor</c> is exactly the self-oracle CLAUDE.md forbids.
/// So mutool reads it.</para>
///
/// <para>This also matters more after the fix than before it: mutool HONOURS
/// <c>/NeedAppearances</c> (see <c>FormOutputFidelityTests</c>), so while the
/// scrub still set that flag mutool would synthesize the field from <c>/V</c>
/// and never look at <c>/AP</c>. With the flag gone, what mutool reports IS the
/// rewritten appearance.</para>
///
/// <para>The document declares PDF/A so the fix's PDF/A branch is the one
/// exercised. The conformance VERDICT is veraPDF's and lives in
/// <c>Excise.Core.Tests/PdfATests</c>; this test asserts nothing about
/// conformance.</para>
/// </summary>
public sealed class RedactedFormFieldOracleTests
{
    private const string Term = "Lovelace";

    [Fact]
    public void RedactedFormFieldValue_IsGoneFromAnIndependentExtractor_AndFromTheSavedBytes()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var fontPath = Path.Combine(RepoRoot(), "Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");
        Assert.SkipWhen(!File.Exists(fontPath), "DejaVuSans.ttf fixture not present");

        var font = PdfFont.FromTrueType(File.ReadAllBytes(fontPath), 11);
        var authored = PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title("Archival Form")
            .DefaultFont(font)
            .PdfA()
            .Paragraph("Fields authored by the builder.")
            .TextField("Name", "name", defaultValue: "Ada Lovelace")
            .SaveToBytes();

        byte[] redacted;
        using (var doc = PdfDocument.Open(authored))
        {
            doc.TargetsPdfA.Should().BeTrue("PdfA() writes the pdfaid XMP the detector reads");
            doc.GetAcroForm()!.FindField("name")!.Value.Should().Contain(Term,
                "a green run must not come from having redacted nothing");

            doc.RedactText(Term);
            redacted = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-redacted-form-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, redacted);
        try
        {
            var beforePath = Path.Combine(Path.GetTempPath(), $"excise-form-before-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(beforePath, authored);
            try
            {
                // Guard: mutool can read the value out of this document to begin
                // with. Without it, "mutool does not see the term afterwards"
                // could just mean mutool never saw it.
                (MutoolTextExtractor.ExtractPage(beforePath, 1) ?? "").Should().Contain(Term,
                    "the oracle must be able to see what it is later asked to confirm is gone");
            }
            finally { try { File.Delete(beforePath); } catch { /* best effort */ } }

            (MutoolTextExtractor.ExtractPage(path, 1) ?? "").Should().NotContain(Term,
                "with /NeedAppearances gone, mutool reads the rewritten /AP rather than " +
                "re-typesetting the field from /V — so the appearance itself must be clean");
            SavedPdfLeakScanner.FindTerm(redacted, Term).Should().BeEmpty(
                "carrier-agnostic backstop: the term must not survive as text in any carrier, " +
                "compressed or not (it cannot see the Identity-H glyph run — that is mutool's job above)");
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null
               && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
               && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("repository root unavailable");
    }
}
