using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1468 increment 2: <see cref="PdfStream.TryReleaseDecoded()"/> gives back an
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

        // #1492: the redactor zeroes the ORIGINAL's decoded array in place (the
        // saved output depends on it), so the original is no longer what its
        // decoder produced and must never be released back to it.
        image.TryReleaseDecoded().Should().BeFalse(
            "the region redaction edited the original's samples in place; releasing would restore the unredacted ones");
        image.DecodedData.Should().BeSameAs(redactedSamples);
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

    /// <summary>#1492: the viewer's release metrics need the bytes a release dropped.</summary>
    [Fact]
    public void AReleaseReportsTheDecodedBytesItDropped()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);

        image.TryReleaseDecoded(out long before).Should().BeFalse("nothing decoded yet");
        before.Should().Be(0);

        image.DecodedData.Should().Equal(Samples);
        image.TryReleaseDecoded(out long released).Should().BeTrue();
        released.Should().Be(Samples.Length);

        image.TryReleaseDecoded(out long again).Should().BeFalse();
        again.Should().Be(0);
    }

    /// <summary>
    /// #1492: the viewer releases on the UI thread, and a decode runs inside the
    /// stream's lock. A release that would wait for it must give up instead.
    /// </summary>
    [Fact]
    public async Task AReleaseWithoutWaiting_IsBusyWhileADecodeHoldsTheLock_AndReleasesOnceItIsDone()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var stream = new PdfStream(ImageDictionary(), Flate(Samples));
        stream.DeferDecode(s =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
            s.SetDecodedData(Samples.ToArray());
        });

        stream.TryReleaseDecodedWithoutWaiting(out _).Should().Be(DecodedReleaseOutcome.NotReleasable,
            "nothing is decoded and nobody holds the lock");

        var reader = Task.Run(() => stream.DecodedData);
        entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("fixture: the decode started");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        stream.TryReleaseDecodedWithoutWaiting(out long busyBytes).Should().Be(DecodedReleaseOutcome.Busy);
        watch.Stop();
        busyBytes.Should().Be(0);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "it returns at once rather than waiting for the decode");

        gate.Set();
        (await reader).Should().Equal(Samples);

        stream.TryReleaseDecodedWithoutWaiting(out long released).Should().Be(DecodedReleaseOutcome.Released);
        released.Should().Be(Samples.Length);
        stream.IsDecoded.Should().BeFalse();
        stream.TryReleaseDecodedWithoutWaiting(out _).Should().Be(DecodedReleaseOutcome.NotReleasable);
    }

    [Fact]
    public void AReleaseWithoutWaiting_StillRefusesBytesTheDecoderDidNotWrite()
    {
        using var doc = PdfDocument.Open(SavedFlateImageDocument());
        var image = GetImage(doc);
        var rewritten = new byte[Samples.Length];
        image.SetDecodedData(rewritten);

        image.TryReleaseDecodedWithoutWaiting(out long bytes).Should().Be(DecodedReleaseOutcome.NotReleasable);
        bytes.Should().Be(0);
        image.DecodedData.Should().BeSameAs(rewritten);
    }

    /// <summary>
    /// #1492, the aliasing hazard. A region redaction zeroes the ORIGINAL
    /// image stream's decoded array in place (a later redaction of the same
    /// image object sees those zeroes, which is the saved output the redactor
    /// has always produced). That array must stop counting as the decoder's:
    /// released, the next reader — another page sharing the image, a second
    /// redaction — would decode the unredacted samples again.
    /// </summary>
    [Fact]
    public void ARegionRedaction_LeavesTheEditedOriginalNotReleasable_ByEitherRelease()
    {
        using var doc = PdfDocument.Open(SavedSharedFlateImageDocument());
        var page1 = doc.GetPage(1);
        var shared = GetImage(doc);
        doc.GetPage(2).GetXObject("Im0").Should().BeSameAs(shared, "precondition: both pages draw one image object");

        ImageRegionRedactor.TryRegionRedact(
                page1, shared, 40, 0, 0, 40, 10, 10, new PdfRectangle(15, 15, 30, 30), out var newName)
            .Should().BeTrue("precondition: the redactor handled this Flate DeviceRGB image");
        var redacted = page1.GetXObject(newName).Should().BeOfType<PdfStream>().Subject;
        var zeroed = shared.DecodedData;
        zeroed.Should().NotEqual(Samples, "precondition: the region was zeroed in the original's array");

        shared.TryReleaseDecoded(out long bytes).Should().BeFalse("an in-place edit is not the decoder's output");
        bytes.Should().Be(0);
        shared.TryReleaseDecodedWithoutWaiting(out long noWaitBytes).Should().Be(DecodedReleaseOutcome.NotReleasable,
            "never Released, and never Busy — nothing holds the lock, the answer is final");
        noWaitBytes.Should().Be(0);

        shared.DecodedData.Should().BeSameAs(zeroed, "the edited array stays in place");
        redacted.DecodedData.Should().Equal(zeroed, "the redacted XObject carries the same zeroed samples");
    }

    /// <summary>
    /// #1492: a release racing the redaction never leaves the shared stream (or
    /// the redacted copy) holding the original samples. The redactor's read and
    /// its provenance clear happen in one hold of the decode lock; a release
    /// that lands first only makes the redactor decode again.
    /// </summary>
    [Fact]
    public async Task AReleaseLoopingOnAnotherThread_NeverLeavesTheRedactedImageWithTheOriginalSamples()
    {
        var source = SavedSharedFlateImageDocument();
        for (var round = 0; round < 200; round++)
        {
            using var doc = PdfDocument.Open(source);
            var page1 = doc.GetPage(1);
            var shared = GetImage(doc);
            shared.DecodedData.Should().Equal(Samples, "precondition: a render decoded the image");

            using var stop = new CancellationTokenSource();
            var releaser = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                    shared.TryReleaseDecoded();
            });

            ImageRegionRedactor.TryRegionRedact(
                    page1, shared, 40, 0, 0, 40, 10, 10, new PdfRectangle(15, 15, 30, 30), out var newName)
                .Should().BeTrue($"round {round}");
            var redacted = page1.GetXObject(newName).Should().BeOfType<PdfStream>().Subject;

            stop.Cancel();
            await releaser.WaitAsync(TimeSpan.FromSeconds(30));

            redacted.DecodedData.Should().NotEqual(Samples, $"round {round}: the redacted copy is zeroed");
            shared.DecodedData.Should().BeSameAs(redacted.DecodedData,
                $"round {round}: the shared stream still holds the zeroed array, not a re-decode of the original");
        }
    }

    /// <summary>
    /// #1492: the redaction removes the text from every carrier (scanned inside
    /// compressed streams) and zeroes the region in the redacted page's image,
    /// and the page that shares the original image saves it from its unchanged
    /// encoded bytes.
    /// </summary>
    [Fact]
    public void RedactingAnAreaOverASharedImage_RemovesTheText_ZeroesTheRegion_AndSavesTheSharedOriginalFromItsEncodedBytes()
    {
        const string term = "SECRETNAME";
        byte[] saved;
        using (var doc = PdfDocument.Open(SavedSharedFlateImageDocument(textOnPage1: term)))
        {
            var shared = GetImage(doc);
            shared.DecodedData.Should().Equal(Samples, "precondition: a render decoded the image");

            doc.GetPage(1).RedactArea(new PdfRectangle(15, 15, 30, 30));

            shared.TryReleaseDecoded().Should().BeFalse("the redaction edited the shared original's samples in place");
            saved = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty("the redacted text must not survive in any carrier");

        using var reopened = PdfDocument.Open(saved);
        // The area covers columns 0-1 of rows 2-3.
        AssertZeroedExactly(ImagesOf(reopened.GetPage(1)).Should().ContainSingle().Subject.DecodedData,
            (row, col) => row >= 2 && col <= 1, "page 1");
        reopened.GetPage(2).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject
            .DecodedData.Should().Equal(Samples, "page 2's image saves from the original's unchanged encoded bytes");
    }

    /// <summary>
    /// Pins the redactor's saved output for two pages that share an image, each
    /// region-redacted in its own area (#1492 keeps it byte-identical to
    /// before). The first redaction zeroes the shared original in place, so
    /// page 2's redacted copy carries page 1's zeroed region as well as its own.
    /// </summary>
    [Fact]
    public void RegionRedactingTwoPagesThatShareAnImage_PageTwosCopyCarriesBothZeroedRegions()
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(SavedSharedFlateImageDocument()))
        {
            doc.GetPage(1).RedactArea(new PdfRectangle(15, 15, 30, 30));
            doc.GetPage(2).RedactArea(new PdfRectangle(35, 35, 48, 48));
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        // Page 1's area covers columns 0-1 of rows 2-3; page 2's covers columns 2-3 of rows 0-1.
        AssertZeroedExactly(ImagesOf(reopened.GetPage(1)).Should().ContainSingle().Subject.DecodedData,
            (row, col) => row >= 2 && col <= 1, "page 1");
        AssertZeroedExactly(ImagesOf(reopened.GetPage(2)).Should().ContainSingle().Subject.DecodedData,
            (row, col) => (row >= 2 && col <= 1) || (row <= 1 && col >= 2), "page 2");
    }

    // ---- helpers -------------------------------------------------------

    private static void AssertZeroedExactly(byte[] samples, Func<int, int, bool> zeroed, string page)
    {
        samples.Should().HaveCount(Samples.Length);
        for (var row = 0; row < Height; row++)
        {
            for (var col = 0; col < Width; col++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int i = (row * Width + col) * 3 + c;
                    samples[i].Should().Be(zeroed(row, col) ? (byte)0 : Samples[i],
                        $"{page} sample row {row} col {col}");
                }
            }
        }
    }

    private static List<PdfStream> ImagesOf(PdfPage page)
    {
        var resources = page.Document.Resolve(page.Dictionary.GetOptional("Resources")!) as PdfDictionary;
        var xobjects = resources == null ? null : page.Document.Resolve(resources.GetOptional("XObject")!) as PdfDictionary;
        var images = new List<PdfStream>();
        if (xobjects == null)
            return images;
        foreach (var (_, value) in xobjects)
        {
            if (page.Document.Resolve(value) is PdfStream stream && stream.GetOptional("Subtype") is PdfName { Value: "Image" })
                images.Add(stream);
        }
        return images;
    }

    /// <summary>One Flate image drawn by two pages, optionally with text over its lower-left region on page 1.</summary>
    private static byte[] SavedSharedFlateImageDocument(string? textOnPage1 = null)
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(ImageDictionary(), Flate(Samples)));
        PlaceOnNewPage(doc, imageRef);
        PlaceOnNewPage(doc, imageRef);
        if (textOnPage1 != null)
        {
            var page = doc.GetPage(1);
            var font = new PdfDictionary();
            font.SetName("Type", "Font");
            font.SetName("Subtype", "Type1");
            font.SetName("BaseFont", "Helvetica");
            var fonts = new PdfDictionary();
            fonts["F1"] = doc.AddIndirectObject(font);
            ((PdfDictionary)page.Dictionary.GetOptional("Resources")!)["Font"] = fonts;
            page.SetContentStreamBytes(Encoding.ASCII.GetBytes(
                $"q 40 0 0 40 10 10 cm /Im0 Do Q BT /F1 2 Tf 16 20 Td ({textOnPage1}) Tj ET"));
        }
        return doc.SaveToBytes();
    }

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
