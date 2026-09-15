using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Primitives;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;
using PixelCopy = Excise.App.Tests.Controls.ContinuousTileEvictionCompositeTests.PixelCopy;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1492: the continuous view releases the decoded image samples of pages that
/// are no longer realized. Found live: scrolling Altona's 17 pages left ~0.7 GB
/// of decoded samples pinned, because band renders (unlike thumbnails) never
/// release them — a page renders as several bands, and releasing per band
/// would re-inflate every large image once per band. So the samples a page
/// reads stay pinned while the page is realized or has a render in flight, and
/// go once it is neither; an image another realized page reads stays.
///
/// <para>On Skia with real renders. The fixture's images are Flate-filtered and
/// the document is saved and re-opened, because only the object store installs
/// the deferred decode that makes samples releasable at all — an unfiltered or
/// in-memory image would read "not decoded" for the wrong reason.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousImageSampleReleaseTests
{
    private const int ImageSize = 16;
    private readonly ITestOutputHelper _out;
    public ContinuousImageSampleReleaseTests(ITestOutputHelper output) => _out = output;

    [FixedAvaloniaFact]
    public async Task PagingThroughTheDocument_ReleasesTheSamplesOfUnrealizedPages_KeepsRealizedAndSharedOnes_AndRedrawsIdentically()
    {
        const int pageCount = 12;
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(ImageDocument(pageCount));
        try
        {
            var first = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var reference = PixelCopy.Of(first);
            reference.InkFraction().Should().BeGreaterThan(0.0005, "fixture: page 1 must show its images");

            var doc = viewer.Document!;
            var own1 = XObject(doc, 1, "Own");
            var logo = XObject(doc, 1, "Logo");
            var pair = XObject(doc, 1, "Pair");
            XObject(doc, pageCount, "Logo").Should().BeSameAs(logo, "fixture: every page draws one logo object");
            XObject(doc, 2, "Pair").Should().BeSameAs(pair, "fixture: pages 1 and 2 draw one pair object");
            own1.IsDecoded.Should().BeTrue("precondition: page 1's render decoded its image, so a release has something to release");

            for (int page = 2; page <= pageCount; page++)
            {
                viewer.CurrentPage = page;
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, page);
            }
            Dispatcher.UIThread.RunJobs();

            var realized = RealizedPages(items);
            _out.WriteLine($"realized=[{string.Join(",", realized.Order())}] " +
                           $"recorded=[{string.Join(",", viewer.ContinuousImageSamplesForTests.RecordsForTests.Keys.Order())}] " +
                           $"decodedOwn=[{string.Join(",", Enumerable.Range(1, pageCount).Where(p => XObject(doc, p, "Own").IsDecoded))}]");
            realized.Should().NotContain(1).And.NotContain(2).And.Contain(pageCount, "fixture");

            for (int page = 1; page <= pageCount; page++)
            {
                if (!realized.Contains(page))
                {
                    XObject(doc, page, "Own").IsDecoded.Should().BeFalse(
                        $"page {page} is no longer realized, and nothing else draws its image");
                }
            }
            XObject(doc, pageCount, "Own").IsDecoded.Should().BeTrue("the realized page keeps its samples");
            logo.IsDecoded.Should().BeTrue("the realized page still draws the logo that unrealized pages shared");
            pair.IsDecoded.Should().BeFalse("both pages that drew it are unrealized");
            viewer.ContinuousImageSamplesForTests.RecordsForTests.Keys.Should().OnlyContain(p => realized.Contains(p),
                "a page that is no longer realized keeps no record");

            // Drop every tile so page 1 has to render again, from re-decoded samples.
            viewer.TrimCaches(PdfViewerCacheTrimLevel.Critical);
            int startsBefore = viewer.ContinuousRenderStartCount;
            viewer.CurrentPage = 1;
            var rebuilt = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            viewer.ContinuousRenderStartCount.Should().BeGreaterThan(startsBefore, "fixture: page 1 rendered again");
            own1.IsDecoded.Should().BeTrue("page 1's render decoded its image again");

            var actual = PixelCopy.Of(rebuilt);
            actual.Width.Should().Be(reference.Width, "same band, same DPI");
            actual.Height.Should().Be(reference.Height, "same band, same DPI");
            actual.Bgra.Should().Equal(reference.Bgra,
                "a page drawn from released-and-re-decoded samples is pixel-identical to the page before the release");
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    /// <summary>
    /// The in-flight hazard: a page scrolled away while its band render is still
    /// running is kept (its samples are being read), and the render then pins
    /// its samples when it lands. Nothing else runs after that for a page that
    /// is not realized, so the render's own completion must release them.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task APageUnrealizedWhileItsRenderIsInFlight_GivesBackItsSamples_OnceThatRenderLands()
    {
        const int pageCount = 12;
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(ImageDocument(pageCount));
        // Hold EVERY band render of page 1 (a page can render as more than one
        // batch), so none of them reads its images before the page is unrealized.
        viewer.ContinuousBandRenderStartingForTests = page =>
        {
            if (page == 1)
            {
                entered.Set();
                gate.Wait(TimeSpan.FromSeconds(60));
            }
        };
        using var releases = new ReleaseCapture();
        try
        {
            await PumpUntilAsync(window, () => entered.IsSet, "page 1's first band render started");
            var doc = viewer.Document!;
            var own1 = XObject(doc, 1, "Own");

            viewer.CurrentPage = pageCount;
            await PumpUntilAsync(window,
                () => SlotOf(items, pageCount)?.Bitmap != null && !RealizedPages(items).Contains(1),
                "page 12 composited and page 1 unrealized while page 1's render is held");
            viewer.ContinuousInFlightCount.Should().BeGreaterThan(0, "fixture: page 1's render is still in flight");
            // The viewer is opened in single-page view before the helper switches
            // it to continuous, and that first single-page render may already have
            // decoded page 1's image. Nothing continuous has read it (every page 1
            // band render is held), so give it back to start from "not decoded".
            _out.WriteLine($"before hold release: own1.IsDecoded={own1.IsDecoded} singlePagePublishes={viewer.SinglePagePublishCount} " +
                           $"recorded=[{string.Join(",", viewer.ContinuousImageSamplesForTests.RecordsForTests.Keys.Order())}]");
            viewer.ContinuousImageSamplesForTests.StreamsOf(1).Should().BeEmpty("fixture: no band render of page 1 has landed");
            own1.TryReleaseDecoded();
            own1.IsDecoded.Should().BeFalse("fixture: the held render has not read page 1's image yet");

            long releasedBefore = releases.Streams;
            gate.Set();
            await PumpUntilAsync(window, () => viewer.ContinuousInFlightCount == 0, "page 1's render landed");
            Dispatcher.UIThread.RunJobs();

            _out.WriteLine($"released streams before={releasedBefore} after={releases.Streams} " +
                           $"recorded=[{string.Join(",", viewer.ContinuousImageSamplesForTests.RecordsForTests.Keys.Order())}]");
            releases.Streams.Should().BeGreaterThan(releasedBefore,
                "fixture: the landed render read page 1's images, and they were released");
            own1.IsDecoded.Should().BeFalse(
                "page 1 was unrealized while its render ran; what that render pinned must be released when it lands");
            viewer.ContinuousImageSamplesForTests.StreamsOf(1).Should().BeEmpty();
            XObject(doc, pageCount, "Own").IsDecoded.Should().BeTrue("the realized page keeps its samples");
        }
        finally
        {
            gate.Set();
            viewer.ContinuousBandRenderStartingForTests = null;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    // ---- fixture -------------------------------------------------------

    /// <summary>
    /// <paramref name="pageCount"/> pages. Each draws its own image (/Own) and a
    /// logo every page shares (/Logo); pages 1 and 2 also draw one shared /Pair.
    /// Flate-filtered and saved, so the re-opened images decode on demand.
    /// </summary>
    internal static byte[] ImageDocument(int pageCount)
    {
        using var doc = PdfCoreDocument.CreateNew();
        var logo = doc.AddIndirectObject(Image(seed: 1));
        var pair = doc.AddIndirectObject(Image(seed: 2));
        for (int p = 1; p <= pageCount; p++)
        {
            var own = doc.AddIndirectObject(Image(seed: 10 + p));
            var page = doc.Pages.AddBlank(612, 792);
            var xobjects = new PdfDictionary();
            xobjects["Own"] = own;
            xobjects["Logo"] = logo;
            var content = new StringBuilder("q 300 0 0 300 60 420 cm /Own Do Q q 120 0 0 120 420 600 cm /Logo Do Q");
            if (p <= 2)
            {
                xobjects["Pair"] = pair;
                content.Append(" q 200 0 0 200 360 80 cm /Pair Do Q");
            }
            var resources = new PdfDictionary();
            resources["XObject"] = xobjects;
            page.Dictionary["Resources"] = resources;
            page.SetContentStreamBytes(Encoding.ASCII.GetBytes(content.ToString()));
        }
        return doc.SaveToBytes();
    }

    internal static PdfStream XObject(PdfCoreDocument doc, int page, string name) =>
        doc.GetPage(page).GetXObject(name).Should().BeOfType<PdfStream>().Subject;

    internal static HashSet<int> RealizedPages(ItemsControl items) =>
        items.GetRealizedContainers().Select(c => c.DataContext).OfType<PdfPageSlot>().Select(s => s.PageNumber).ToHashSet();

    private static PdfStream Image(int seed)
    {
        var rgb = Enumerable.Range(0, ImageSize * ImageSize * 3)
            .Select(i => (byte)((i * (seed * 7 + 13) + seed * 31) % 230))
            .ToArray();
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", ImageSize);
        dict.SetInt("Height", ImageSize);
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("ColorSpace", "DeviceRGB");
        dict.SetName("Filter", "FlateDecode");
        using var output = new System.IO.MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rgb);
        return new PdfStream(dict, output.ToArray());
    }

    private static PdfPageSlot? SlotOf(ItemsControl items, int page) =>
        items.ItemsSource?.Cast<PdfPageSlot>().FirstOrDefault(s => s.PageNumber == page);

    private static async Task PumpUntilAsync(Window window, Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"timed out waiting for: {what}");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    /// <summary>Sums the viewer's "unrealized" decoded-sample release counter.</summary>
    private sealed class ReleaseCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _streams;

        public ReleaseCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Excise.Viewer" && instrument.Name == "excise.viewer.decoded_samples.releases")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && (string?)tag.Value == "unrealized")
                        Interlocked.Add(ref _streams, value);
                }
            });
            _listener.Start();
        }

        public long Streams => Interlocked.Read(ref _streams);

        public void Dispose() => _listener.Dispose();
    }
}
