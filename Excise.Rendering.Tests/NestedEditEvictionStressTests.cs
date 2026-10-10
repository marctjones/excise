using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;
using M = Excise.TestSupport.MarkedContentCarrierFixtures;

namespace Excise.Rendering.Tests;

/// <summary>
/// The object store may forget a cached object and re-parse it from the file
/// (F3, #1207). Its guard reads only the TOP-LEVEL dictionary's pristine flag,
/// so an in-place edit inside a nested array or direct dictionary leaves the
/// owner evictable, and an eviction then restores the file's original at the
/// save. These tests put the strongest eviction there is between each
/// production edit path and the save: every reachable object is offered to
/// <see cref="PdfDocument.TryEvictFromCache"/>, and every page is rendered
/// with the default (releasing) options, which is the evictor the GUI runs.
/// Production only ever offers image and mask streams (SkiaRenderer.Images,
/// the continuous viewer's sample release); offering every object is
/// deliberately wider, so a pass here means the edit is safe by construction
/// or by its site's own re-registration, not merely out of an evictor's reach.
/// Each edit is checked on the saved bytes (<see cref="SavedPdfLeakScanner"/>)
/// and, where a term is drawn, by mutool.
/// </summary>
public sealed class NestedEditEvictionStressTests : IDisposable
{
    private const string Term = "EVICTIONSTRESSSECRET";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nested-edit-eviction-{Guid.NewGuid():N}");

    private readonly ITestOutputHelper _out;

    public NestedEditEvictionStressTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<string> CarrierPlacements() => new(
        "glyphs",
        "inline /ActualText",
        "custom key, nested",
        "named",
        "named, inline in /Resources",
        "named custom key, nested in a direct array");

    [Theory]
    [MemberData(nameof(CarrierPlacements))]
    public void RedactText_ThenEvictEverything_ThenSave_TheTermIsGone(string placement)
    {
        var input = TermFixture(placement);
        SavedPdfLeakScanner.FindTerm(input, Term).Should().NotBeEmpty("the fixture must carry the term ({0})", placement);

        byte[] saved;
        using (var doc = PdfDocument.Open(input))
        {
            Warm(doc);
            doc.RedactText(Term, RedactionOptions.Default);
            Stress(doc).Should().BeGreaterThan(0, "the knob must actually evict something");
            saved = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty("{0}: an eviction re-parsed an unredacted original", placement);
        AssertMutoolDoesNotRead(saved, 1, placement);
    }

    [Theory]
    [InlineData(Back.AcroFormFieldKids)]
    [InlineData(Back.AcroFormWidget)]
    [InlineData(Back.StructureElement)]
    [InlineData(Back.LinkOnKeptPage)]
    public void RemovePage_ScrubThenEvictEverything_ThenSaveAgain_TheRemovedPageIsGone(Back back)
    {
        using var doc = PdfDocument.Open(F.Build(back, "1.7"));
        Warm(doc);
        doc.Pages.RemoveAt(1);
        // The reference scrub runs as a pre-save action and edits arrays nested in
        // parsed dictionaries (/Kids, /Fields, /CO, /K, /Annots). The first save
        // runs it; the eviction lands after it, before the save that counts.
        doc.SaveToBytes();
        Stress(doc).Should().BeGreaterThan(0);
        var saved = doc.SaveToBytes();

        foreach (var token in F.RemovedTokens(back))
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty("{0}: {1} came back through an eviction", back, token);
        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(2);
    }

    [Fact]
    public void RemoveAnnotation_ThenEvictEverything_ThenSave_TheAnnotationIsGone()
    {
        using var doc = PdfDocument.Open(F.Build(Back.None, "1.7"));
        Warm(doc);
        var note = doc.GetPage(2).GetAnnotations().Single(a => a.RawDictionary.GetOptional("Subtype") is PdfName { Value: "Text" });

        // /Annots is a direct array in the page dictionary: RemoveAt edits it in
        // place and leaves the page pristine, so the stress evicts the page and
        // the re-parse lists the note again. What keeps it out of the file is the
        // pre-save reference scrub, which cuts every recorded deleted annotation
        // that no live page lists (#2012); without RecordRemovedAnnotation this fails.
        doc.RemoveAnnotation(2, note).Should().BeTrue();
        Stress(doc).Should().BeGreaterThan(0);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, F.AnnotText).Should().BeEmpty("the deleted note came back through an eviction");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(2).GetAnnotations().Should().BeEmpty();
    }

    /// <summary>
    /// The gap itself, pinned: a nested edit does not clear the owner's flag, so
    /// the guard would hand an edited image back to the file. No production
    /// mutator edits an image or mask dictionary below its top level, and only
    /// those are ever offered for eviction; a site that starts to must
    /// re-register the owner, as <see cref="ReRegisteringTheOwner_KeepsANestedImageEdit_ThroughAReleasingRender"/> does.
    /// </summary>
    [Fact]
    public void ANestedImageEdit_DoesNotClearTheOwnersPristineFlag()
    {
        using var doc = PdfDocument.Open(ImageDocumentWithDecodeArray());
        var image = Image(doc);
        ((PdfArray)image["Decode"])[0] = new PdfInteger(1);

        image.IsPristine.Should().BeTrue("PdfArray has no flag and a nested write does not reach its owner");
        doc.TryEvictFromCache(image).Should().BeTrue(
            "documented gap, not a contract: the top-level guard cannot see a nested edit. Flip this when the guard becomes recursive");
    }

    [Fact]
    public void ReRegisteringTheOwner_KeepsANestedImageEdit_ThroughAReleasingRender()
    {
        using var doc = PdfDocument.Open(ImageDocumentWithDecodeArray());
        var page = doc.GetPage(1);
        var image = Image(doc);
        ((PdfArray)image["Decode"])[0] = new PdfInteger(1);
        doc.ReplaceIndirectObject(image.ObjectNumber!.Value, image);

        using (new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 })) { }
        doc.TryEvictFromCache(image).Should().BeFalse();

        using var saved = PdfDocument.Open(doc.SaveToBytes());
        ((PdfArray)Image(saved)["Decode"]).GetInt(0).Should().Be(1);
    }

    /// <summary>Resolve everything and render every page once, as an open, viewed document has.</summary>
    private static void Warm(PdfDocument doc)
    {
        doc.ComputeReachableObjects();
        for (var p = 1; p <= doc.PageCount; p++)
            using (new SkiaRenderer().RenderPage(doc.GetPage(p), new RenderOptions { Dpi = 36 })) { }
    }

    /// <summary>The production evictor (a releasing render of every page), then every reachable object offered.</summary>
    private int Stress(PdfDocument doc)
    {
        for (var p = 1; p <= doc.PageCount; p++)
            using (new SkiaRenderer().RenderPage(doc.GetPage(p), new RenderOptions { Dpi = 36 })) { }
        var evicted = 0;
        foreach (var number in doc.ComputeReachableObjects().Order())
            if (doc.TryEvictFromCache(doc.GetObject(number)))
            {
                evicted++;
                _out.WriteLine($"evicted {number}");
            }
        return evicted;
    }

    private void AssertMutoolDoesNotRead(byte[] saved, int page, string label)
    {
        if (!MutoolReferenceRenderer.IsAvailable)
            return;
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        var text = MutoolTextExtractor.ExtractPage(path, page);
        text.Should().NotBeNull();
        text.Should().NotContain(Term, "{0}: mutool read the term", label);
    }

    private static byte[] TermFixture(string placement)
    {
        var value = M.Literal($"{Term} chapter");
        const string painted = $"BT /F1 12 Tf 72 700 Td ({M.Painted}) Tj ET";
        return placement switch
        {
            "glyphs" => ContentStreamFixture.Build($"BT /F1 12 Tf 72 700 Td ({Term} here) Tj ET"),
            // A property list held DIRECTLY in the page's /Resources: the scrub edits
            // a dictionary nested two levels inside the page object.
            "named, inline in /Resources" => ContentStreamFixture.Build($"/Span /P1 BDC {painted} EMC",
                extraResources: $"/Properties << /P1 << /ActualText {value} >> >>"),
            // An indirect property list whose term sits in a direct array inside it.
            "named custom key, nested in a direct array" => ContentStreamFixture.Build($"/Span /P1 BDC {painted} EMC",
                extraObjects: $"6 0 obj\n<< /MyNote [<< /Said {value} >>] >>\nendobj\n",
                extraResources: "/Properties << /P1 6 0 R >>"),
            _ => M.Placements[placement](value),
        };
    }

    private static PdfStream Image(PdfDocument doc)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.GetPage(1).Dictionary["Resources"]);
        var xobjects = (PdfDictionary)doc.Resolve(resources["XObject"]);
        return (PdfStream)doc.Resolve(xobjects["Im0"]);
    }

    private static byte[] ImageDocumentWithDecodeArray()
    {
        var samples = new byte[8 * 8 * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 3 + 1);
        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(samples);
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", 8);
        dict.SetInt("Height", 8);
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("ColorSpace", "DeviceRGB");
        dict.SetName("Filter", "FlateDecode");
        dict["Decode"] = new PdfArray(new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1));

        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(dict, z.ToArray()));
        var page = doc.Pages.AddBlank(100, 100);
        page.Dictionary["Resources"] = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im0"] = imageRef } };
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 40 0 0 40 10 10 cm /Im0 Do Q"));
        return doc.SaveToBytes();
    }
}
