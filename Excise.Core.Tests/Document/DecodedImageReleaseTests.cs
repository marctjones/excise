using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1468 increment 2: <see cref="PdfStream.TryReleaseDecoded"/> gives back an
/// image's decoded samples and re-arms its deferred decode.
///
/// <para><b>Why.</b> Increment 1 stopped image samples inflating on OPEN, but
/// once anything reads them they stay on the stream object in the document's
/// object cache until close — Altona's one page pins ~1 GiB. A render that will
/// not read them again (thumbnail pre-warm, a CLI export) now releases them.</para>
///
/// <para><b>What must not change.</b> Six redaction carriers, the cloner and
/// the writer read these bytes. A release is only safe when decoding again
/// yields the same bytes, so: a re-read after a release is byte-identical; a
/// stream whose bytes anything other than the decoder wrote is NEVER released
/// (releasing a redacted image would silently restore its original samples);
/// saved bytes do not move; and a concurrent reader never fails because a
/// release happened on another thread.</para>
/// </summary>
public class DecodedImageReleaseTests
{
    private const int Width = 4;
    private const int Height = 4;

    private static readonly byte[] Samples = Enumerable.Range(0, Width * Height * 3)
        .Select(i => (byte)(i * 5 + 1))
        .ToArray();

    [Fact]
    public void ReleasingADecodedImage_ThenReadingIt_ReturnsByteIdenticalSamples()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);

        var first = image.DecodedData;
        var firstSha = SHA256.HashData(first);

        image.TryReleaseDecoded().Should().BeTrue("the samples came from the store's deferred decode");
        image.IsDecoded.Should().BeFalse("a release drops the array");

        var second = image.DecodedData;
        second.Should().NotBeSameAs(first, "the decode ran again");
        SHA256.HashData(second).Should().Equal(firstSha,
            "decoding the unchanged encoded bytes again must reproduce the original samples exactly");
        second.Should().Equal(Samples);
        image.DecodeFailureReason.Should().BeNull("a successful re-decode records no refusal");
        image.TryEnsureDecoded().Should().BeTrue();
    }

    [Fact]
    public void ARelease_IsRepeatable_AndOnlyReleasesWhatIsHeld()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);

        image.TryReleaseDecoded().Should().BeFalse("nothing has decoded the samples yet");

        for (var round = 0; round < 3; round++)
        {
            image.DecodedData.Should().Equal(Samples);
            image.TryReleaseDecoded().Should().BeTrue($"round {round}: re-decoded samples are releasable again");
            image.TryReleaseDecoded().Should().BeFalse($"round {round}: there is nothing left to release");
        }
    }

    [Fact]
    public void AStreamThatWasNeverDeferred_IsNeverReleased()
    {
        var eager = new PdfStream(ImageDictionary(), Flate(Samples));
        new StreamDecompressor().Decompress(eager);
        eager.IsDecoded.Should().BeTrue("precondition");
        eager.TryReleaseDecoded().Should().BeFalse(
            "an eagerly decoded stream has no decode to re-arm; dropping its bytes would lose them");
        eager.DecodedData.Should().Equal(Samples);

        var unfiltered = new PdfStream(Samples.ToArray());
        unfiltered.TryReleaseDecoded().Should().BeFalse();
        unfiltered.DecodedData.Should().Equal(Samples);
    }

    [Fact]
    public void ARefusedDecode_IsNotReleasable_AndKeepsItsReason()
    {
        using var doc = PdfDocument.Open(SavedJbig2RefusalDocument());
        var image = GetImage(doc);

        image.TryEnsureDecoded().Should().BeFalse();
        var reason = image.DecodeFailureReason;
        reason.Should().Contain("JBIG2Decode");

        image.TryReleaseDecoded().Should().BeFalse("a refused decode holds nothing to release");
        image.TryEnsureDecoded().Should().BeFalse("and a failed release must not re-arm a decode that refused");
        image.DecodeFailureReason.Should().Be(reason);
    }

    public static TheoryData<string> Writers => new() { "setter", "SetDecodedData", "SetEncodedData" };

    /// <summary>
    /// The redaction case: a render decodes the image, then a redaction pass
    /// rewrites its bytes. Releasing it would re-arm the ORIGINAL decode and the
    /// next reader — the renderer, a later carrier, a clone — would get the
    /// unredacted samples back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writers))]
    public void BytesWrittenAfterTheDecode_AreNeverReleased_AndTheWriteStands(string writer)
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        image.DecodedData.Should().Equal(Samples, "precondition: a render decoded the samples");

        var redacted = new byte[Samples.Length];
        var encodedRedacted = Flate(redacted);
        Write(image, writer, redacted, encodedRedacted);

        image.TryReleaseDecoded().Should().BeFalse($"bytes written through {writer} cannot be rebuilt by the decoder");
        AssertWriteStands(image, writer, redacted, encodedRedacted);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void BytesWrittenAfterAReleaseAndReDecode_AreNeverReleased(string writer)
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        image.DecodedData.Should().Equal(Samples);
        image.TryReleaseDecoded().Should().BeTrue();
        image.DecodedData.Should().Equal(Samples, "precondition: decoded again after a release");

        var redacted = new byte[Samples.Length];
        var encodedRedacted = Flate(redacted);
        Write(image, writer, redacted, encodedRedacted);

        image.TryReleaseDecoded().Should().BeFalse();
        AssertWriteStands(image, writer, redacted, encodedRedacted);
    }

    [Fact]
    public void BytesWrittenBeforeAnyDecode_AreNeverReleased()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        image.IsDecoded.Should().BeFalse("precondition");

        var redacted = new byte[Samples.Length];
        image.SetDecodedData(redacted);

        image.TryReleaseDecoded().Should().BeFalse();
        image.DecodedData.Should().BeSameAs(redacted);
    }

    /// <summary>
    /// The real carrier, not a stand-in: <c>ImageRegionRedactor</c> re-embeds
    /// the zeroed samples as a new stream and publishes them with
    /// <c>SetDecodedData</c> so a second redaction area on the same image sees
    /// pixels. That stream must never be released.
    /// </summary>
    [Fact]
    public void AnImageRegionRedactorResult_IsNeverReleased()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var page = doc.GetPage(1);
        var image = GetImage(doc);

        ImageRegionRedactor.TryRegionRedact(
                page, image, 40, 0, 0, 40, 10, 10, new PdfRectangle(15, 15, 30, 30), out var newName)
            .Should().BeTrue("precondition: the redactor handled this Flate DeviceRGB image");

        var redacted = page.GetXObject(newName).Should().BeOfType<PdfStream>().Subject;
        var redactedSamples = redacted.DecodedData;
        redactedSamples.Should().NotEqual(Samples, "precondition: the region was zeroed");

        redacted.TryReleaseDecoded().Should().BeFalse("the redactor wrote these samples, not a decoder");
        redacted.DecodedData.Should().BeSameAs(redactedSamples);

        image.TryReleaseDecoded().Should().BeTrue(
            "the ORIGINAL image is untouched by a region redaction (it is replaced, not edited) and stays releasable");
        image.DecodedData.Should().Equal(Samples);
    }

    /// <summary>
    /// The GUI sequence end to end: thumbnail pre-warm decodes and releases an
    /// image, then the user redacts it. The redactor re-decodes from the
    /// encoded bytes; what it writes and what gets saved must be exactly what
    /// the same redaction writes when nothing was ever released.
    /// </summary>
    [Fact]
    public void RegionRedactingAReleasedImage_SavesTheSameBytesAsWithoutTheRelease()
    {
        var source = SavedFlateImageDocument();

        byte[] RedactAndSave(bool releaseFirst)
        {
            using var doc = PdfDocument.Open(source);
            var page = doc.GetPage(1);
            var image = GetImage(doc);
            image.DecodedData.Should().Equal(Samples, "precondition: a render decoded the samples");
            if (releaseFirst)
                image.TryReleaseDecoded().Should().BeTrue("precondition: the render released them");

            ImageRegionRedactor.TryRegionRedact(
                    page, image, 40, 0, 0, 40, 10, 10, new PdfRectangle(15, 15, 30, 30), out _)
                .Should().BeTrue();
            return doc.SaveToBytes();
        }

        RedactAndSave(releaseFirst: true).Should().Equal(RedactAndSave(releaseFirst: false),
            "a redaction after a release must see, zero and save exactly the samples it would have otherwise");
    }

    [Fact]
    public void SavedBytes_AreIdentical_AfterAReleaseAndReDecode()
    {
        var source = SavedFlateImageDocument();

        byte[] untouched;
        using (var doc = PdfDocument.Open(source))
            untouched = doc.SaveToBytes();

        byte[] afterRelease;
        using (var doc = PdfDocument.Open(source))
        {
            var image = GetImage(doc);
            image.DecodedData.Should().Equal(Samples);
            image.TryReleaseDecoded().Should().BeTrue();
            afterRelease = doc.SaveToBytes();
            image.DecodedData.Should().Equal(Samples);
        }

        afterRelease.Should().Equal(untouched, "the writer serializes EncodedData, which a release never touches");
    }

    [Fact]
    public void CloningAReleasedImage_ProducesTheOriginalSamples()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        image.DecodedData.Should().Equal(Samples);
        image.TryReleaseDecoded().Should().BeTrue();

        using var target = PdfDocument.CreateNew();
        var clone = new PdfObjectCloner(target).CloneStream(
            doc, image, new Dictionary<(int ObjectNumber, int GenerationNumber), PdfReference>());

        clone.DecodedData.Should().Equal(Samples);
        clone.TryReleaseDecoded().Should().BeFalse("a clone's bytes are written by the cloner, not decoded");
    }

    /// <summary>
    /// Thumbnail pre-warm and the continuous viewer render the same document on
    /// different threads, so one can release while the other reads. A reader
    /// must get the samples, or wait for them to decode again — never a throw,
    /// a false <c>TryEnsureDecoded</c> or a recorded refusal.
    /// </summary>
    /// <remarks>
    /// Mutation-checked: making the getter re-read the field after the
    /// on-demand decode returns (<c>DecodeOnDemand(); Thread.Yield(); return
    /// _decodedData ?? throw</c>) turns this red within the time budget.
    /// </remarks>
    [Fact]
    public void ConcurrentReaders_NeverFail_WhileAnotherThreadReleasesInALoop()
    {
        const int readers = 6;
        var stream = new PdfStream(ImageDictionary(), Flate(Samples));
        var decodes = 0;
        var decompressor = new StreamDecompressor();
        stream.DeferDecode(s =>
        {
            Interlocked.Increment(ref decodes);
            decompressor.Decompress(s);
        });

        var errors = new ConcurrentQueue<string>();
        var releases = 0L;
        var reads = 0L;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var gate = new Barrier(readers + 1);

        var threads = Enumerable.Range(0, readers).Select(index => new Thread(() =>
        {
            gate.SignalAndWait();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // Alternate the two entry points readers use.
                    if (index % 2 == 0)
                    {
                        var data = stream.DecodedData;
                        if (!data.AsSpan().SequenceEqual(Samples))
                            errors.Enqueue("DecodedData returned different bytes");
                    }
                    else if (!stream.TryEnsureDecoded())
                    {
                        errors.Enqueue("TryEnsureDecoded reported not decoded");
                    }

                    if (stream.DecodeFailureReason is { } reason)
                        errors.Enqueue("a refusal was recorded: " + reason);
                    Interlocked.Increment(ref reads);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex.GetType().Name + ": " + ex.Message);
                }

                if (errors.Count > 0)
                    return;
            }
        })).ToList();

        threads.Add(new Thread(() =>
        {
            gate.SignalAndWait();
            while (!stop.IsCancellationRequested && errors.IsEmpty)
            {
                if (stream.TryReleaseDecoded())
                    Interlocked.Increment(ref releases);
                else
                    Thread.SpinWait(8);
            }
        }));

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        errors.Should().BeEmpty("no reader may observe a release; first: " + errors.FirstOrDefault());
        releases.Should().BeGreaterThan(100, "the race has to actually happen for this test to prove anything");
        decodes.Should().BeGreaterThan(100);
        reads.Should().BeGreaterThan(releases);
        stream.DecodedData.Should().Equal(Samples);
    }

    // ---- helpers -------------------------------------------------------

    private static void Write(PdfStream image, string writer, byte[] decoded, byte[] encoded)
    {
        switch (writer)
        {
            case "setter": image.DecodedData = decoded; break;
            case "SetDecodedData": image.SetDecodedData(decoded); break;
            case "SetEncodedData": image.SetEncodedData(encoded); break;
            default: throw new ArgumentOutOfRangeException(nameof(writer));
        }
    }

    private static void AssertWriteStands(PdfStream image, string writer, byte[] decoded, byte[] encoded)
    {
        if (writer == "SetEncodedData")
        {
            // Replacing the encoded bytes leaves a filtered stream "not decoded",
            // exactly as it did before deferral existed; what must stand is the
            // new encoded bytes, and no decode of the OLD ones may come back.
            image.EncodedData.Should().BeSameAs(encoded);
            image.IsDecoded.Should().BeFalse();
            image.TryEnsureDecoded().Should().BeFalse("the old decode must not be re-armed");
            return;
        }

        image.DecodedData.Should().BeSameAs(decoded, "the written bytes, not a re-decode of the original");
        image.TryEnsureDecoded().Should().BeTrue();
    }

    private static PdfStream GetImage(PdfDocument doc)
        => doc.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;

    private static PdfDictionary ImageDictionary(string filter = "FlateDecode", int bitsPerComponent = 8, string colorSpace = "DeviceRGB")
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

    private static byte[] SavedFlateImageDocument()
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(ImageDictionary(), Flate(Samples)));
        PlaceOnNewPage(doc, imageRef);
        return doc.SaveToBytes();
    }

    private static byte[] SavedJbig2RefusalDocument()
    {
        using var doc = PdfDocument.CreateNew();
        // An end-of-file segment and nothing else: the JBIG2 decoder attempts
        // it and refuses (see DeferredImageDecodeTests).
        var segment = new byte[] { 0, 0, 0, 1, 63, 0, 1, 0, 0, 0, 0 };
        var imageRef = doc.AddIndirectObject(new PdfStream(
            ImageDictionary("JBIG2Decode", bitsPerComponent: 1, colorSpace: "DeviceGray"), segment));
        PlaceOnNewPage(doc, imageRef);
        return doc.SaveToBytes();
    }

    private static void PlaceOnNewPage(PdfDocument doc, PdfReference imageRef)
    {
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 40 0 0 40 10 10 cm /Im0 Do Q"));
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}
