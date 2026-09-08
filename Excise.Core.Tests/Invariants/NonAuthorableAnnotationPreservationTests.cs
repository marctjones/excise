using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Invariants;

/// <summary>
/// Preserve-mode evidence for annotation-subtypes.json capabilities that the
/// app's annotation-AUTHORING API deliberately does not create (Widget/Link
/// are form-field/hyperlink concerns, not user-authored comments; Popup is
/// auto-associated with a markup annotation, never created standalone; and
/// PrinterMark/TrapNet/3D/RichMedia/Projection have no typed model in
/// PdfAnnotationSubtype at all -- they parse as Unknown and are preserved
/// only as opaque dictionaries). AnnotationInvariantTests.cs's
/// AuthoredAnnotation_SurvivesASaveAndReloadUnchanged deliberately excludes
/// all eight of these (see AuthorableSubtypes) because it exercises the
/// authoring API, which is out of scope here -- annotation-authoring is
/// frozen; reading and preserving externally-authored annotations is not.
/// These build the annotation dictionaries directly, the way an external
/// producer's PDF would arrive, and prove the same "opaque dictionary
/// passthrough" write-path guarantee already exercised for images in
/// PdfImagePreservationTests.cs.
/// </summary>
public class NonAuthorableAnnotationPreservationTests
{
    private static PdfReference AddAnnotation(PdfDocument doc, PdfDictionary dict)
    {
        dict.SetName("Type", "Annot");
        return doc.AddIndirectObject(dict);
    }

    private static PdfDocument SaveAndReopen(PdfDocument doc) => PdfDocument.Open(doc.SaveToBytes());

    /// <summary>Widget, Link, and Popup: typed subtypes the authoring API never creates.</summary>
    [Theory]
    [InlineData("Widget")]
    [InlineData("Link")]
    [InlineData("Popup")]
    public void TypedButUnauthorableSubtype_SurvivesASaveAndReload_Unchanged(string subtype)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);

        var dict = new PdfDictionary();
        dict.SetName("Subtype", subtype);
        dict["Rect"] = PdfArray.FromRectangle(10, 10, 90, 90);
        dict.SetString("Contents", "round trip me");
        var annotRef = AddAnnotation(doc, dict);
        var annots = new PdfArray(new PdfObject[] { annotRef });
        page.Dictionary["Annots"] = annots;

        var reopened = SaveAndReopen(doc);
        using var _ = reopened;
        var annot = reopened.GetPage(1).GetAnnotations().Should().ContainSingle().Subject;

        annot.Subtype.ToString().Should().Be(subtype);
        annot.Contents.Should().Be("round trip me");
        var r = annot.Rect.Normalize();
        r.Left.Should().BeApproximately(10, 0.01);
        r.Bottom.Should().BeApproximately(10, 0.01);
        r.Width.Should().BeApproximately(80, 0.01);
        r.Height.Should().BeApproximately(80, 0.01);
    }

    /// <summary>
    /// PrinterMark, TrapNet, 3D, RichMedia, Projection: no PdfAnnotationSubtype
    /// enum member at all, so the typed .Subtype always reads Unknown -- the
    /// preserve claim here is that the RAW /Subtype name and dictionary
    /// content survive a save unchanged even though excise never interprets
    /// them, matching this file's preserve-only decision for these five.
    /// </summary>
    [Theory]
    [InlineData("PrinterMark")]
    [InlineData("TrapNet")]
    [InlineData("3D")]
    [InlineData("RichMedia")]
    [InlineData("Projection")]
    public void UnmodeledExoticSubtype_SurvivesASaveAndReload_RawDictionaryUnchanged(string subtype)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);

        var dict = new PdfDictionary();
        dict.SetName("Subtype", subtype);
        dict["Rect"] = PdfArray.FromRectangle(5, 5, 95, 95);
        dict.SetString("NM", $"{subtype}-marker");
        var annotRef = AddAnnotation(doc, dict);
        var annots = new PdfArray(new PdfObject[] { annotRef });
        page.Dictionary["Annots"] = annots;

        var reopened = SaveAndReopen(doc);
        using var _ = reopened;
        var annot = reopened.GetPage(1).GetAnnotations().Should().ContainSingle().Subject;

        annot.Subtype.Should().Be(PdfAnnotationSubtype.Unknown,
            $"{subtype} has no PdfAnnotationSubtype member; that itself is the documented reason it is preserve-only");
        annot.RawDictionary.GetName("Subtype").Should().Be(subtype,
            "the raw /Subtype name must still survive even though excise assigns it no typed meaning");
        annot.RawDictionary.GetStringOrNull("NM").Should().Be($"{subtype}-marker");
        var r = annot.Rect.Normalize();
        r.Left.Should().BeApproximately(5, 0.01);
        r.Width.Should().BeApproximately(90, 0.01);
    }
}
