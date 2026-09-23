using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1613: a page walk that has finished with a page's inflated content may drop
/// it (<see cref="PdfPage.ReleaseDecodedContentStreams"/>); the next read
/// re-decodes the unchanged encoded bytes.
///
/// <para><b>What must not change.</b> These are the bytes redaction and text
/// extraction parse. So: a re-read after a release is byte-identical; the stream
/// keeps its identity (an edit through any reference still reaches the save); a
/// stream whose bytes or dictionary anything other than the store's decoder
/// wrote is never released; and a re-decode that does not reproduce the
/// released bytes fails loudly on every read rather than handing back
/// different content once.</para>
/// </summary>
public class ContentStreamReleaseTests
{
    private const string Secret = "SECRETWORD";
    private const string Public = "PUBLICWORD";

    [Fact]
    public void ReleasingPageContent_ThenReadingIt_ReturnsByteIdenticalBytes_OnTheSameStream()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 1));
        var page = doc.GetPage(1);
        var stream = ContentStreams(doc, page).Single();
        stream.IsFiltered.Should().BeTrue("precondition: the fixture's content is Flate-encoded");

        var first = page.GetContentStreamBytes();
        var firstSha = SHA256.HashData(first);

        page.ReleaseDecodedContentStreams().Should().Be(first.Length);
        stream.IsDecoded.Should().BeFalse("a release drops the array");
        stream.IsPristine.Should().BeTrue("a release is not an edit");
        page.ReleaseDecodedContentStreams().Should().Be(0, "there is nothing left to release");

        var second = page.GetContentStreamBytes();
        second.Should().NotBeSameAs(first, "the decode ran again");
        SHA256.HashData(second).Should().Equal(firstSha);
        ContentStreams(doc, page).Single().Should().BeSameAs(stream, "the object stays in the cache: same identity");
        stream.IsPristine.Should().BeTrue();

        page.ReleaseDecodedContentStreams().Should().Be(first.Length, "re-decoded bytes are releasable again");
        new TextExtractor(page).ExtractText().Should().Contain(Secret).And.Contain(Public);
    }

    [Fact]
    public void AMultiStreamContentsArray_ReleasesEveryElement_AndReadsBackIdentically()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 2));
        var page = doc.GetPage(1);
        var streams = ContentStreams(doc, page);
        streams.Should().HaveCount(2, "precondition");

        var first = page.GetContentStreamBytes();
        page.ReleaseDecodedContentStreams().Should().BePositive();
        streams.Should().OnlyContain(s => !s.IsDecoded);

        page.GetContentStreamBytes().Should().Equal(first);
    }

    [Fact]
    public void ContentWrittenByAnEdit_IsNeverReleased()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 1));
        var page = doc.GetPage(1);
        var stream = ContentStreams(doc, page).Single();
        var written = Encoding.ASCII.GetBytes(Body("EDITED") + string.Concat(Enumerable.Repeat("0 0 m 1 1 l n\n", 50)));

        page.SetContentStreamBytes(written);

        page.ReleaseDecodedContentStreams().Should().Be(0, "an edit wrote these bytes, not the decoder");
        stream.DecodedData.Should().BeSameAs(written);
    }

    [Fact]
    public void AStreamWhoseDictionaryWasEdited_IsNeverReleased()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 1));
        var page = doc.GetPage(1);
        var stream = ContentStreams(doc, page).Single();
        var decoded = stream.DecodedData;

        stream.SetName("Marker", "Edited");

        page.ReleaseDecodedContentStreams().Should().Be(0, "/Filter and /DecodeParms drive the re-decode; an edited dictionary is not the file's");
        stream.DecodedData.Should().BeSameAs(decoded);
    }

    [Fact]
    public void ARedecodeThatDoesNotReproduceTheReleasedBytes_FailsEveryRead_AndNeverReturnsOtherBytes()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 1));
        var page = doc.GetPage(1);
        var stream = ContentStreams(doc, page).Single();

        // Nothing in-tree mutates a decoded array in place; this plants exactly
        // that so the verification has something to catch. Without it the
        // re-decode would silently hand back the file's bytes, not these.
        stream.DecodedData[0] ^= 0xFF;
        page.ReleaseDecodedContentStreams().Should().BePositive("precondition: provenance is tracked on the fields, not the array contents");

        var read = () => page.GetContentStreamBytes();
        read.Should().Throw<InvalidOperationException>().WithMessage("*did not re-decode*");
        read.Should().Throw<InvalidOperationException>("every later read fails the same way");
        stream.IsDecoded.Should().BeFalse("the mismatching bytes were never published");
    }

    [Fact]
    public void RedactingAfterARelease_RemovesTheTermFromTheSavedFile()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 1));
        var page = doc.GetPage(1);
        page.GetContentStreamBytes();
        page.ReleaseDecodedContentStreams().Should().BePositive("precondition");

        page.RedactArea(new PdfRectangle(15, 190, 200, 220));

        page.ReleaseDecodedContentStreams().Should().Be(0, "the redaction wrote the content; it must never be released and reverted");
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, Public).Should().NotBeEmpty("only the redacted line goes");

        using var reopened = PdfDocument.Open(saved);
        new TextExtractor(reopened.GetPage(1)).ExtractText().Should().NotContain(Secret).And.Contain(Public);
    }

    [Fact]
    public void ReleasingBetweenARedactionsReadAndItsWrite_DoesNotRevertTheRedaction()
    {
        using var doc = PdfDocument.Open(SavedDocument(streams: 1));
        var page = doc.GetPage(1);
        var stream = ContentStreams(doc, page).Single();
        var original = page.GetContentStreamBytes();

        // A thumbnail render on another thread releases while an edit holds the
        // bytes it read; the write then lands on the SAME cached instance.
        page.ReleaseDecodedContentStreams().Should().BePositive();
        var redacted = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(original).Replace(Secret, "XXXXXXXXXX"));
        page.SetContentStreamBytes(redacted);

        ContentStreams(doc, page).Single().Should().BeSameAs(stream);
        page.ReleaseDecodedContentStreams().Should().Be(0);
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), Secret).Should().BeEmpty();
    }

    private static List<PdfStream> ContentStreams(PdfDocument doc, PdfPage page)
    {
        return doc.Resolve(page.Dictionary.GetOptional("Contents")!) switch
        {
            PdfStream s => [s],
            PdfArray a => a.Select(doc.Resolve).OfType<PdfStream>().ToList(),
            _ => [],
        };
    }

    private static string Body(string text, int y = 200)
        => $"BT /F1 12 Tf 20 {y} Td ({text}) Tj ET\n";

    /// <summary>Flate-encoded page content: the secret line, the public line, and enough no-op paths that Flate wins (#1549).</summary>
    private static byte[] SavedDocument(int streams)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 300);
        var font = new PdfDictionary();
        font.SetName("Type", "Font");
        font.SetName("Subtype", "Type1");
        font.SetName("BaseFont", "Helvetica");
        var fonts = new PdfDictionary();
        fonts["F1"] = doc.AddIndirectObject(font);
        var resources = new PdfDictionary();
        resources["Font"] = fonts;
        page.Dictionary["Resources"] = resources;

        var filler = string.Concat(Enumerable.Repeat("0 0 m 1 1 l n\n", 50));
        var parts = streams == 1
            ? new[] { Body(Secret) + Body(Public, 100) + filler }
            : new[] { Body(Secret) + filler, Body(Public, 100) + filler };

        var contents = new PdfArray();
        foreach (var part in parts)
            contents.Add(doc.AddIndirectObject(PdfStream.CreateCompressed(Encoding.ASCII.GetBytes(part))));
        page.Dictionary["Contents"] = streams == 1 ? contents[0] : contents;
        return doc.SaveToBytes();
    }
}
