using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1492: the continuous viewer's per-page image-sample records. The set
/// arithmetic decides which decoded samples a release pass gives back, so it
/// is pinned here without a renderer: a page outside the kept set releases
/// what only it read, a stream a kept page also read stays, a page with a
/// render in flight is kept, and a stream whose lock is busy stays recorded
/// for the next attempt. The streams are real deferred-decode streams, so a
/// release here is the same <see cref="PdfStream"/> release the viewer makes.
/// </summary>
public class DecodedImageSampleRetentionTests
{
    [Fact]
    public void ReleaseAllExcept_ReleasesWhatOnlyUnkeptPagesRead_AndReportsTheDecodedBytes()
    {
        var retention = new DecodedImageSampleRetention();
        var own1 = DecodedStream(100);
        var own2 = DecodedStream(250);
        var own3 = DecodedStream(40);
        retention.Record(1, [own1]);
        retention.Record(2, [own2]);
        retention.Record(3, [own3]);

        var (streams, bytes) = retention.ReleaseAllExcept(new HashSet<int> { 3 });

        streams.Should().Be(2);
        bytes.Should().Be(350, "the released decoded arrays' lengths");
        own1.IsDecoded.Should().BeFalse();
        own2.IsDecoded.Should().BeFalse();
        own3.IsDecoded.Should().BeTrue("page 3 is kept");
        retention.RecordsForTests.Keys.Should().Equal(3);
    }

    [Fact]
    public void AStreamAKeptPageAlsoRead_IsNeverReleased()
    {
        var retention = new DecodedImageSampleRetention();
        var shared = DecodedStream(64);
        var own1 = DecodedStream(64);
        retention.Record(1, [shared, own1]);
        retention.Record(2, [shared]);

        DecodedImageSampleRetention.SelectReleasable(retention.RecordsForTests, new HashSet<int> { 2 })
            .Should().BeEquivalentTo(new[] { own1 }, "the shared image is still drawn by page 2");

        retention.ReleaseAllExcept(new HashSet<int> { 2 }).Should().Be((1, 64L));

        shared.IsDecoded.Should().BeTrue("releasing it would only make page 2 decode it again");
        own1.IsDecoded.Should().BeFalse();
        retention.StreamsOf(2).Should().Contain(shared, "page 2's record still pins it");
        retention.RecordsForTests.ContainsKey(1).Should().BeFalse("page 1's record is done with");

        // Once page 2 goes too, the shared image is released.
        retention.ReleaseAllExcept(new HashSet<int>()).Should().Be((1, 64L));
        shared.IsDecoded.Should().BeFalse();
        retention.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void APageWithARenderInFlight_IsKept_EvenWhenItIsNotRealized()
    {
        var retention = new DecodedImageSampleRetention();
        var own5 = DecodedStream(32);
        retention.Record(5, [own5]);

        var keep = PdfViewerControl.ContinuousImageSampleKeepPages(
            pages: [1],
            inFlight: [new PdfViewerControl.ContinuousTileKey(Page: 5, Dpi: 120, PageWidthDip: 816, PageHeightDip: 1056, Col: 0, Row: 2)]);

        keep.Should().BeEquivalentTo(new[] { 1, 5 });
        retention.ReleaseAllExcept(keep).Should().Be((0, 0L));
        own5.IsDecoded.Should().BeTrue("page 5's band render is still running and reads these samples");
        retention.StreamsOf(5).Should().Contain(own5);
    }

    /// <summary>
    /// F3 for the viewer (#1207/#1461): once a page's samples are released, the
    /// object itself is offered for eviction so its ENCODED bytes can go too —
    /// gcdump at the altona-scroll peak put ~100 MB of live Byte[] in exactly
    /// those objects. Only what was actually released is offered: a kept page's
    /// stream and a stream the release could not take are not, because
    /// evicting a stream that is still drawn would just make the next band
    /// re-parse it. Whether an offered object may be forgotten at all (edited
    /// objects may not) is the object store's decision, pinned in
    /// Excise.Core.Tests' ObjectCacheEvictionTests.
    /// </summary>
    [Fact]
    public void ReleaseAllExcept_OffersEveryReleasedStreamForEviction_AndNothingElse()
    {
        var retention = new DecodedImageSampleRetention();
        var released = DecodedStream(100);
        var released2 = DecodedStream(50);
        var kept = DecodedStream(60);
        var rewritten = DecodedStream(16);
        rewritten.SetDecodedData(new byte[16]);              // cannot be released, so must not be offered
        retention.Record(1, [released, released2, rewritten]);
        retention.Record(2, [kept]);

        var offered = new List<PdfStream>();
        var (streams, bytes) = retention.ReleaseAllExcept(new HashSet<int> { 2 }, offered.Add);

        streams.Should().Be(2);
        bytes.Should().Be(150);
        offered.Should().BeEquivalentTo(new[] { released, released2 }, "exactly the streams whose samples were released");
        kept.IsDecoded.Should().BeTrue();
    }

    [Fact]
    public void ReleaseAllExcept_WithoutAnEvictor_ReleasesSamplesExactlyAsBefore()
    {
        var retention = new DecodedImageSampleRetention();
        var own = DecodedStream(100);
        retention.Record(1, [own]);

        retention.ReleaseAllExcept(new HashSet<int>()).Should().Be((1, 100L));
        own.IsDecoded.Should().BeFalse();
    }

    [Fact]
    public void Records_MergeAcrossRenders_AndIgnoreDuplicates()
    {
        var retention = new DecodedImageSampleRetention();
        var a = DecodedStream(8);
        var b = DecodedStream(8);
        retention.Record(1, [a]);
        retention.Record(1, [a, b]);
        retention.Record(2, []);

        retention.StreamsOf(1).Should().BeEquivalentTo(new[] { a, b });
        retention.RecordsForTests.ContainsKey(2).Should().BeFalse("an empty render records nothing");
        retention.ReleaseAllExcept(new HashSet<int>()).Should().Be((2, 16L));
    }

    [Fact]
    public void AStreamThatCannotBeReleased_IsDroppedFromTheRecord_AndCountsNothing()
    {
        var retention = new DecodedImageSampleRetention();
        var rewritten = DecodedStream(16);
        rewritten.SetDecodedData(new byte[16]); // what a redaction or edit does
        var neverDeferred = new PdfStream(new byte[] { 1, 2, 3 });
        retention.Record(1, [rewritten, neverDeferred]);

        retention.ReleaseAllExcept(new HashSet<int>()).Should().Be((0, 0L));

        rewritten.IsDecoded.Should().BeTrue("bytes the decoder did not write are never released");
        retention.IsEmpty.Should().BeTrue("there is nothing a later attempt could release either");
    }

    [Fact]
    public async Task AStreamBusyDecoding_StaysRecorded_AndIsReleasedByALaterAttempt()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var samples = new byte[48];
        var stream = new PdfStream(FilteredDictionary(), new byte[] { 1 });
        stream.DeferDecode(s =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
            s.SetDecodedData(samples.ToArray());
        });

        var retention = new DecodedImageSampleRetention();
        retention.Record(1, [stream]);

        var reader = Task.Run(() => stream.DecodedData);
        entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("fixture: the decode started");

        retention.ReleaseAllExcept(new HashSet<int>()).Should().Be((0, 0L),
            "a release that would have to wait for a decode gives up instead of blocking the UI thread");
        retention.StreamsOf(1).Should().Contain(stream, "a busy stream is tried again later, not forgotten");

        gate.Set();
        (await reader).Should().HaveCount(samples.Length);
        stream.IsDecoded.Should().BeTrue();

        retention.ReleaseAllExcept(new HashSet<int>()).Should().Be((1, 48L));
        stream.IsDecoded.Should().BeFalse();
        retention.IsEmpty.Should().BeTrue();
    }

    private static PdfDictionary FilteredDictionary()
    {
        var dict = new PdfDictionary();
        dict.SetName("Filter", "FlateDecode");
        return dict;
    }

    /// <summary>A deferred-decode stream whose samples have been decoded, like a rendered image.</summary>
    private static PdfStream DecodedStream(int length)
    {
        var samples = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var stream = new PdfStream(FilteredDictionary(), new byte[] { 1 });
        stream.DeferDecode(s => s.SetDecodedData(samples.ToArray()));
        stream.DecodedData.Should().Equal(samples, "fixture: the deferred decode produced the samples");
        return stream;
    }
}
