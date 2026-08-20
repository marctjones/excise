using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1040 — <c>/Resources /XObject</c> as an INDIRECT REFERENCE.
///
/// <para>A real name survived redaction on a real Nitro Pro document while
/// excise drew the black box over it and reported success. Both causes were the
/// same expression in <c>FormXObjectFlattener</c>:</para>
///
/// <code>resources.GetDictionaryOrNull("XObject")</code>
///
/// <para>— a bare <c>is PdfDictionary</c> type check with no reference
/// resolution, returning null for the very common shape where <c>/XObject</c>
/// is <c>15 0 R</c>. It produced two independent leaks:</para>
///
/// <list type="number">
///   <item><b>Nothing was ever flattened.</b> <c>ReferencesAnyForm</c> reported
///     no forms, so form content was unreachable by the glyph remover — while
///     the black rectangle was still drawn at the right coordinates. A visually
///     perfect redaction over intact text.</item>
///   <item><b>Nothing was ever pruned.</b> Even with flattening working, the
///     inlined object number was never recorded, so <c>PruneInlinedForms</c>
///     returned on an empty set and the original form object survived in the
///     file still holding the text.</item>
/// </list>
///
/// <para>The second leak is invisible to every extractor — the orphan is not
/// drawn, so mutool read 0 while the bytes held the name. Only a
/// decompress-then-scan finds it, which is what
/// <see cref="SavedPdfLeakScanner"/> exists for.</para>
///
/// <para>This is the same defect shape already documented for
/// <c>/DescendantFonts</c> in <c>ContentStreamFixture</c> — "an INDIRECT
/// REFERENCE, the shape real producers emit and the one a bare cast misses".</para>
/// </summary>
public class IndirectXObjectResourceRedactionTests
{
    private const string Secret = "Farrar";

    /// <summary>
    /// A page whose only text lives in a Form XObject, reached through an
    /// <c>/XObject</c> dictionary that is an indirect reference — the Nitro Pro
    /// shape, reproduced so the gate needs no gitignored corpus.
    /// </summary>
    private static byte[] BuildPdfWithIndirectXObjectResource()
    {
        var formStream = $"BT /F1 12 Tf 5 20 Td (Louise Anne {Secret}) Tj ET\n";
        var formBytes = Encoding.Latin1.GetByteCount(formStream);

        return ContentStreamFixture.Build(
            content: "q 1 0 0 1 100 600 cm /Fm0 Do Q\n",
            extraResources: "/XObject 6 0 R",
            extraObjects:
                // The indirection that broke everything: /XObject is 6 0 R, not
                // a direct dictionary.
                "6 0 obj\n<< /Fm0 7 0 R >>\nendobj\n" +
                "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 300 60] " +
                "/Resources << /Font << /F1 5 0 R >> >> " +
                $"/Length {formBytes} >>\nstream\n{formStream}endstream\nendobj\n");
    }

    private static byte[] RedactAndSave(byte[] pdf, string term, out int reported)
    {
        using var doc = PdfDocument.Open(pdf);
        reported = doc.RedactText(term).VerifiedRemovals;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Guard_TheFixtureTextIsReachableBeforeRedaction()
    {
        // Without this, every assertion below could pass on a fixture whose
        // text excise never saw in the first place.
        using var doc = PdfDocument.Open(BuildPdfWithIndirectXObjectResource());
        doc.GetPage(1).Text.Should().Contain(Secret,
            "the fixture must put the term somewhere excise can actually find it, " +
            "or this class proves nothing");
    }

    [Fact]
    public void TextInsideTheFormIsRemoved_NotJustCoveredWithABlackBox()
    {
        var saved = RedactAndSave(BuildPdfWithIndirectXObjectResource(), Secret, out _);

        // Decompress-then-scan. The raw-byte form of this assertion declared
        // #1040's leaking output CLEAN, because excise compresses on save.
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "the term must be gone from every carrier in the saved file, including " +
            "inside compressed streams — before #1040 the form was never flattened, " +
            "so the glyphs stayed and only a black rectangle was drawn over them");
    }

    [Fact]
    public void TheInlinedFormObject_IsPrunedFromThePageResources()
    {
        var saved = RedactAndSave(BuildPdfWithIndirectXObjectResource(), Secret, out _);

        using var doc = PdfDocument.Open(saved);
        var page = doc.GetPage(1);
        var xobjectRef = page.Resources?.GetOptional("XObject");
        var xobjects = xobjectRef != null ? doc.Resolve(xobjectRef) as PdfDictionary : null;

        // Pins consequence two specifically. An extractor cannot see this: the
        // orphan is not drawn, so text extraction reports clean either way.
        (xobjects?.GetOptional("Fm0")).Should().BeNull(
            "once the form is inlined, the page must stop referencing it — otherwise " +
            "the writer re-emits the original object with the text still in it");
    }

    [Fact]
    public void TheSurroundingTextInTheFormSurvives()
    {
        var saved = RedactAndSave(BuildPdfWithIndirectXObjectResource(), Secret, out _);

        using var doc = PdfDocument.Open(saved);
        doc.GetPage(1).Text.Should().Contain("Louise",
            "redacting the surname must not take the rest of the form's text with it");
    }

    [Fact]
    public void TheReportedCount_MatchesTheSingleOccurrence()
    {
        RedactAndSave(BuildPdfWithIndirectXObjectResource(), Secret, out var reported);

        // Before #1040 this document reported 3 for one occurrence: removal
        // failed, the loop retried, and each pass re-counted the same match
        // (#1043).
        reported.Should().Be(1,
            "the term occurs exactly once; a larger number counts retries as removals");
    }
}
