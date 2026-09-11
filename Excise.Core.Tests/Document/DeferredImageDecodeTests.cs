using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1468 increment 1: an image XObject's samples are decoded when something
/// reads them, not when the object is resolved.
///
/// <para><b>Why.</b> Resolving is what every <c>/Do</c> does just to read
/// <c>/Subtype</c>. Text extraction rejects images that way, and the GUI
/// extracts text from every page on every open, so eager decoding inflated
/// every Flate image in a document (~526 MiB on Altona by qpdf's estimate)
/// and pinned the bytes in the object cache until close.</para>
///
/// <para><b>What must not change.</b> The bytes a reader gets, who sees a
/// refusal (#1396), what a clone carries, and what a save writes. Those are
/// what these tests pin; the retention policy once decoded is unchanged and
/// is increment 2's problem.</para>
/// </summary>
public class DeferredImageDecodeTests
{
    private const int Width = 4;
    private const int Height = 4;

    private static readonly byte[] Samples = Enumerable.Range(0, Width * Height * 3)
        .Select(i => (byte)(i * 5 + 1))
        .ToArray();

    [Fact]
    public void ResolvingAFlateImage_LeavesItUndecoded_AndDecodedDataStillReturnsTheSamples()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());

        var image = GetImage(doc);

        image.IsDecoded.Should().BeFalse(
            "resolving an image XObject is what /Do does just to read /Subtype; it must not inflate the samples");
        var first = image.DecodedData;
        first.Should().Equal(Samples,
            "the deferred decode must produce exactly the bytes the eager decode did");
        image.IsDecoded.Should().BeTrue("IsDecoded still means the decoded bytes are held");
        image.DecodedData.Should().BeSameAs(first,
            "the decode runs once; a later read returns the published array instead of decoding again");
    }

    [Fact]
    public void ResolvingAFilteredContentStream_StillDecodesEagerly()
    {
        byte[] saved;
        using (var source = PdfDocument.CreateNew())
        {
            var sourcePage = source.Pages.AddBlank(100, 100);
            var dict = new PdfDictionary();
            dict.SetName("Filter", "FlateDecode");
            sourcePage.Dictionary["Contents"] = source.AddIndirectObject(
                new PdfStream(dict, Flate(Encoding.ASCII.GetBytes("0 0 10 10 re f"))));
            saved = source.SaveToBytes();
        }

        using var doc = PdfDocument.Open(saved);
        var contents = doc.Resolve(doc.GetPage(1).Dictionary.GetOptional("Contents")!)
            .Should().BeOfType<PdfStream>().Subject;
        contents.IsFiltered.Should().BeTrue("precondition: the content stream is Flate-encoded");

        contents.IsDecoded.Should().BeTrue(
            "only image XObjects defer; content streams have callers that branch on IsDecoded");
    }

    [Fact]
    public void ConcurrentFirstReads_AllReturnTheSameArray_AndTheDecodeRunsOnce()
    {
        const int threads = 8;
        var stream = FlateImageStream();
        var runs = 0;
        stream.DeferDecode(s =>
        {
            Interlocked.Increment(ref runs);
            // Hold the lock long enough that every reader is contending for it.
            Thread.Sleep(50);
            new StreamDecompressor().Decompress(s);
        });

        var results = RaceFirstReads(threads, () => stream.DecodedData);

        runs.Should().Be(1, "the first read decodes; every concurrent reader waits for that one decode");
        results.Should().HaveCount(threads);
        results.Should().OnlyContain(r => ReferenceEquals(r, results[0]),
            "a second decode would publish a different array to some readers");
        results[0].Should().Equal(Samples);
    }

    [Fact]
    public void ConcurrentFirstReadsThroughADocument_AllReturnTheSameSamples()
    {
        const int threads = 8;
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        image.IsDecoded.Should().BeFalse("precondition: the race is over the FIRST read");

        var results = RaceFirstReads(threads, () => image.DecodedData);

        results.Should().HaveCount(threads);
        results.Should().OnlyContain(r => ReferenceEquals(r, results[0]),
            "the object store's deferred decode must run once, not once per racing reader");
        results[0].Should().Equal(Samples);
    }

    /// <summary>
    /// A decode that refuses now refuses on first READ rather than at resolve,
    /// and the #1396 reason must still land on the stream — the renderer checks
    /// it to tell the user why an image is missing.
    /// </summary>
    [Fact]
    public void AFailedDeferredDecode_RecordsTheReason_AndReadsBackAsNotDecoded()
    {
        using var doc = PdfDocument.Open(SavedJbig2RefusalDocument());
        var image = GetImage(doc);

        image.IsDecoded.Should().BeFalse();
        image.DecodeFailureReason.Should().BeNull("nothing has attempted the decode yet");

        var firstRead = () => image.DecodedData;
        firstRead.Should().Throw<InvalidOperationException>(
            "a refused stream reads back as undecoded, the same exception callers always saw");

        image.DecodeFailureReason.Should().NotBeNull(
            "the refusal reason is recorded by the deferred decode exactly as by the eager one (#1396)");
        image.DecodeFailureReason.Should().Contain("JBIG2Decode");
        image.IsDecoded.Should().BeFalse("a refused decode must never read back as decoded samples");

        // The decode is attempted once, as it was at resolve time. Overwrite the
        // recorded reason: a second attempt would refuse again and record the
        // JBIG2 reason over this one.
        image.SetDecodeFailureReason("sentinel: no second attempt");

        var secondRead = () => image.DecodedData;
        secondRead.Should().Throw<InvalidOperationException>();
        image.TryEnsureDecoded().Should().BeFalse();
        image.IsDecoded.Should().BeFalse();
        image.DecodeFailureReason.Should().Be("sentinel: no second attempt",
            "neither a second read nor TryEnsureDecoded may re-run a decode that already refused");
    }

    [Fact]
    public void TryEnsureDecoded_OnARefusedImage_ReturnsFalseAndRecordsTheReason()
    {
        using var doc = PdfDocument.Open(SavedJbig2RefusalDocument());
        var image = GetImage(doc);

        image.TryEnsureDecoded().Should().BeFalse();
        image.DecodeFailureReason.Should().Contain("JBIG2Decode");
    }

    [Fact]
    public void ExtractingTextFromAPageWithAnImage_LeavesTheImageUndecoded()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument(withText: true));
        var page = doc.GetPage(1);

        page.Text.Should().Contain("Marker1468", "precondition: extraction actually walked the page");
        page.GetWords().Should().NotBeEmpty();

        GetImage(doc).IsDecoded.Should().BeFalse(
            "text extraction resolves an image XObject only to reject it by /Subtype; " +
            "it must not inflate the samples (#1468)");
    }

    [Fact]
    public void SavedBytes_AreIdentical_WhetherOrNotTheImageWasReadFirst()
    {
        var source = SavedFlateImageDocument();

        byte[] untouched;
        using (var doc = PdfDocument.Open(source))
        {
            GetImage(doc).IsDecoded.Should().BeFalse();
            untouched = doc.SaveToBytes();
        }

        byte[] afterRead;
        using (var doc = PdfDocument.Open(source))
        {
            GetImage(doc).DecodedData.Should().Equal(Samples);
            afterRead = doc.SaveToBytes();
        }

        afterRead.Should().Equal(untouched,
            "the writer serializes EncodedData; decoding on demand must not change a single saved byte");
    }

    [Fact]
    public void CloningAnUndecodedImage_ProducesAReadableClone()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        image.IsDecoded.Should().BeFalse("precondition: the source's decode is still deferred");

        using var target = PdfDocument.CreateNew();
        var clone = new PdfObjectCloner(target).CloneStream(
            doc, image, new Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference>());

        clone.IsDecoded.Should().BeTrue(
            "the clone holds decoded bytes from the moment it is made; it must not carry a decode " +
            "that reaches back into the source document's store");
        clone.DecodedData.Should().Equal(Samples);
        clone.EncodedData.Should().Equal(image.EncodedData);
    }

    /// <summary>
    /// JBIG2 globals are read DURING an image's decode, so they must be settled
    /// at resolve time — even when the globals stream is itself shaped like an
    /// image, which this repo's own fixtures produce.
    /// </summary>
    [Fact]
    public void Jbig2GlobalsShapedLikeAnImage_AreStillDecodedAtResolveTime()
    {
        using var source = PdfDocument.CreateNew();
        var globalsDict = new PdfDictionary();
        globalsDict.SetName("Type", "XObject");
        globalsDict.SetName("Subtype", "Image");
        globalsDict.SetName("Filter", "FlateDecode");
        var globalsRef = source.AddIndirectObject(new PdfStream(globalsDict, Flate(new byte[] { 1, 2, 3, 4 })));

        var imageDict = ImageDictionary("JBIG2Decode", bitsPerComponent: 1, colorSpace: "DeviceGray");
        var parms = new PdfDictionary();
        parms["JBIG2Globals"] = globalsRef;
        imageDict["DecodeParms"] = parms;
        var imageRef = source.AddIndirectObject(new PdfStream(imageDict, BuildJbig2Segment(1, 63)));
        PlaceOnNewPage(source, imageRef, content: "q 10 0 0 10 5 5 cm /Im0 Do Q");

        using var doc = PdfDocument.Open(source.SaveToBytes());
        var image = GetImage(doc);

        var globals = image.GetDictionary("DecodeParms").GetOptional("JBIG2Globals")
            .Should().BeOfType<PdfStream>().Subject;
        globals.IsDecoded.Should().BeTrue(
            "a decode must never wait on a second stream's lock, so globals are decoded under the parse lock");
        globals.DecodedData.Should().Equal(1, 2, 3, 4);
    }

    /// <summary>
    /// The recoverable content read (the renderer's) skips a filtered stream
    /// that is "not decoded". A /Contents stream shaped like an image XObject
    /// resolves with its decode deferred, and "not decoded YET" must not be
    /// mistaken for "undecodable" — that would drop page content the eager
    /// decode parsed.
    /// </summary>
    [Fact]
    public void AContentStreamShapedLikeAnImage_IsStillReadByTheRecoverableContentPath()
    {
        byte[] saved;
        using (var source = PdfDocument.CreateNew())
        {
            var page = source.Pages.AddBlank(100, 100);
            var dict = new PdfDictionary();
            dict.SetName("Type", "XObject");
            dict.SetName("Subtype", "Image");
            dict.SetName("Filter", "FlateDecode");
            var contentsRef = source.AddIndirectObject(new PdfStream(dict, Flate(Encoding.ASCII.GetBytes("0 0 10 10 re f"))));
            page.Dictionary["Contents"] = contentsRef;
            saved = source.SaveToBytes();
        }

        using var doc = PdfDocument.Open(saved);
        doc.GetPage(1).TryGetContentStreamBytes(out var bytes, out var warnings).Should().BeTrue(
            "a deferred decode that succeeds is decodable content, not a recoverable skip");
        warnings.Should().BeEmpty();
        Encoding.ASCII.GetString(bytes).Should().Contain("0 0 10 10 re f");
    }

    // ---- helpers -------------------------------------------------------

    private static PdfStream GetImage(PdfDocument doc)
        => doc.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;

    private static List<byte[]> RaceFirstReads(int threads, Func<byte[]> read)
    {
        var results = new ConcurrentBag<byte[]>();
        var errors = new ConcurrentQueue<Exception>();
        using var gate = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            try
            {
                gate.SignalAndWait();
                results.Add(read());
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToList();

        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());

        errors.Should().BeEmpty("no racing first read may fail; first: " + errors.FirstOrDefault());
        return results.ToList();
    }

    private static PdfStream FlateImageStream()
        => new(ImageDictionary("FlateDecode", bitsPerComponent: 8, colorSpace: "DeviceRGB"), Flate(Samples));

    private static PdfDictionary ImageDictionary(string filter, int bitsPerComponent, string colorSpace)
    {
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", filter == "JBIG2Decode" ? 8 : Width);
        dict.SetInt("Height", filter == "JBIG2Decode" ? 8 : Height);
        dict.SetInt("BitsPerComponent", bitsPerComponent);
        dict.SetName("ColorSpace", colorSpace);
        dict.SetName("Filter", filter);
        return dict;
    }

    private static byte[] SavedFlateImageDocument(bool withText = false)
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(FlateImageStream());
        var content = withText
            ? "BT /F1 12 Tf 10 80 Td (Marker1468) Tj ET q 40 0 0 40 10 10 cm /Im0 Do Q"
            : "q 40 0 0 40 10 10 cm /Im0 Do Q";
        PlaceOnNewPage(doc, imageRef, content, withText);
        return doc.SaveToBytes();
    }

    private static byte[] SavedJbig2RefusalDocument()
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(
            ImageDictionary("JBIG2Decode", bitsPerComponent: 1, colorSpace: "DeviceGray"),
            BuildJbig2Segment(1, 63)));
        PlaceOnNewPage(doc, imageRef, "q 40 0 0 40 10 10 cm /Im0 Do Q");
        return doc.SaveToBytes();
    }

    private static void PlaceOnNewPage(PdfDocument doc, PdfReference imageRef, string content, bool withFont = false)
    {
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        if (withFont)
        {
            var font = new PdfDictionary();
            font.SetName("Type", "Font");
            font.SetName("Subtype", "Type1");
            font.SetName("BaseFont", "Helvetica");
            var fonts = new PdfDictionary();
            fonts["F1"] = font;
            resources["Font"] = fonts;
        }

        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes(content));
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    // An end-of-file segment and nothing else: the JBIG2 decoder attempts it and
    // refuses with a PdfFilterDecodeException (Jbig2JpxFilterIntegrationTests).
    private static byte[] BuildJbig2Segment(uint segmentNumber, byte segmentType)
        => new[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16), (byte)(segmentNumber >> 8), (byte)segmentNumber,
            segmentType, (byte)0, (byte)1, (byte)0, (byte)0, (byte)0, (byte)0,
        };
}
