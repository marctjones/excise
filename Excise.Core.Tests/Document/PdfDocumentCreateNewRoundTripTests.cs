using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1839: the shape of a document <see cref="PdfDocument.CreateNew"/> hands
/// out, pinned through the one writer: what it saves, and what a reopen finds.
/// </summary>
public class PdfDocumentCreateNewRoundTripTests
{
    // 1.4 writes a classic xref table and trailer; 1.7 and 2.0 write object
    // streams and a cross-reference stream.
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void EmptyDocument_SavesAndReopens_WithSaneRootPagesAndSize(string version)
    {
        using var created = PdfDocument.CreateNew(version);
        created.PageCount.Should().Be(0);

        var saved = created.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        reopened.Version.Should().Be(version);
        reopened.PageCount.Should().Be(0);
        AssertStructureIsSane(reopened);
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    public void DocumentWithPages_SurvivesTwoSaveReopenGenerations(string version)
    {
        using var created = PdfDocument.CreateNew(version);
        created.Pages.AddBlank();
        created.Pages.AddBlank(300, 400);

        using var first = PdfDocument.Open(created.SaveToBytes());
        first.PageCount.Should().Be(2);
        AssertStructureIsSane(first);

        first.Pages.AddBlank();
        using var second = PdfDocument.Open(first.SaveToBytes());
        second.PageCount.Should().Be(3);
        second.GetPage(2).Width.Should().BeApproximately(300, 0.01);
        second.GetPage(2).Height.Should().BeApproximately(400, 0.01);
        AssertStructureIsSane(second);
    }

    [Fact]
    public void NewDocument_HasNoSourceFileInfoOrEncryption()
    {
        using var created = PdfDocument.CreateNew();

        created.SourceFileState.Should().BeNull();
        created.Info.Should().BeNull();
        created.IsEncrypted.Should().BeFalse();
        created.IsDecrypting.Should().BeFalse();
        created.Permissions.Should().Be(Excise.Core.Security.PdfPermissions.AllAllowed);
    }

    private static void AssertStructureIsSane(PdfDocument document)
    {
        var rootRef = document.Trailer.GetReference("Root");
        document.GetObject(rootRef).Should().BeSameAs(document.Catalog);
        document.Catalog.GetNameOrNull("Type").Should().Be("Catalog");

        var pages = document.Resolve(document.Catalog.GetOptional("Pages")!)
            .Should().BeOfType<PdfDictionary>().Subject;
        pages.GetNameOrNull("Type").Should().Be("Pages");
        var kids = document.Resolve(pages.GetOptional("Kids")!)
            .Should().BeOfType<PdfArray>().Subject;
        kids.Count.Should().Be(document.PageCount);
        pages.GetInt("Count", -1).Should().Be(document.PageCount);

        var inUse = document.SnapshotInUseObjectNumbers();
        inUse.Should().Contain(rootRef.ObjectNum);
        inUse.Should().Contain(document.Catalog.GetReference("Pages").ObjectNum);
        document.Trailer.GetInt("Size", -1).Should().Be(inUse.Max() + 1,
            "/Size is one more than the highest object number in the file (ISO 32000-2 Table 15)");
    }
}
