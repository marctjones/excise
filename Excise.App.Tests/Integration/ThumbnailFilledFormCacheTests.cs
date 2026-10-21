using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Avalonia.Headless.XUnit;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Integration;

/// <summary>
/// #2023: a page thumbnail of a filled form is a picture of the filled values. A document
/// that carries filled values (AcroForm /V, or XFA datasets with a value) must keep its
/// thumbnails in memory only; nothing may be written to, or read from, the on-disk cache.
/// An unfilled form and an ordinary document keep caching to disk.
/// </summary>
public class ThumbnailFilledFormCacheTests
{
    private const string Secret = "SECRET2023VALUE";

    private static readonly string NameField =
        "<field name=\"Name\" w=\"3in\" h=\"0.3in\"><ui><textEdit/></ui></field>";

    [Fact]
    public async Task FilledAcroForm_WritesNoThumbnailToDisk()
    {
        var bytes = FilledW9Bytes();
        await AssertNotPersistedAsync(bytes);
    }

    [Theory]
    [InlineData("irs-w9.pdf")]
    [InlineData("irs-w4.pdf")]
    public async Task UnfilledAcroForm_StillCachesToDisk(string fixture)
    {
        // The regression guard for "every form stops caching": a blank IRS form carries
        // /V /Off on its check boxes and check buttons at their off value in its XFA
        // datasets (the W-9's <c1_1>0</c1_1>), neither of which is a filled value.
        var source = TestRepoLayout.FindFile("test-pdfs", "smoke", fixture);
        Assert.SkipWhen(source == null,
            TestRepoLayout.AbsenceReason("smoke corpus (scripts/download-smoke-corpus.sh)", $"test-pdfs/smoke/{fixture}"));
        await AssertPersistedAsync(File.ReadAllBytes(source!));
    }

    [Fact]
    public async Task FilledDynamicXfa_WritesNoThumbnailToDisk()
    {
        var bytes = XfaTestForms.BuildPdf(
            XfaTestForms.Template(NameField), XfaTestForms.Data($"<Name>{Secret}</Name>"));
        await AssertNotPersistedAsync(bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<Name/>")]
    [InlineData("<Name>   </Name>")]
    public async Task UnfilledDynamicXfa_StillCachesToDisk(string? dataBody)
    {
        var bytes = XfaTestForms.BuildPdf(
            XfaTestForms.Template(NameField), dataBody == null ? null : XfaTestForms.Data(dataBody));
        await AssertPersistedAsync(bytes);
    }

    private static readonly string CheckField =
        "<field name=\"Agree\" w=\"0.3in\" h=\"0.3in\"><ui><checkButton/></ui></field>";

    [Theory]
    [InlineData("0", true)]   // the default off value (no items): a blank check box
    [InlineData("1", false)]  // the default on value: checked
    public async Task DynamicXfa_CheckButton_OffIsUnfilled_OnIsFilled(string value, bool cached)
    {
        var bytes = XfaTestForms.BuildPdf(
            XfaTestForms.Template(NameField + CheckField),
            XfaTestForms.Data($"<Name/><Agree>{value}</Agree>"));
        if (cached)
            await AssertPersistedAsync(bytes);
        else
            await AssertNotPersistedAsync(bytes);
    }

    [Fact]
    public async Task DynamicXfa_DataNoFieldBinds_IsUnfilled()
    {
        // Not drawn, so not in a thumbnail.
        var bytes = XfaTestForms.BuildPdf(
            XfaTestForms.Template(NameField), XfaTestForms.Data("<Name/><Stray>LOOKUP</Stray>"));
        await AssertPersistedAsync(bytes);
    }

    [Fact]
    public async Task FilledStaticXfa_WritesNoThumbnailToDisk()
    {
        // The datasets and the widgets both hold ORIGINALNAMEVALUE.
        await AssertNotPersistedAsync(XfaStaticFillFixtures.Build());
    }

    [Fact]
    public async Task FilledAcroForm_DemandLoad_ShowsTheThumbnailButWritesNothing()
    {
        var path = WriteTemp(FilledW9Bytes());
        try
        {
            using var doc = PdfDocument.Open(path);
            using var svc = new ThumbnailCacheService(path, doc, NullLogger.Instance);

            using (var bmp = await svc.GetThumbnailAsync(0))
                bmp.Should().NotBeNull("the in-memory thumbnail is unchanged");
            svc.RenderCount.Should().Be(1);

            // The write is queued after the pixels are returned; give it time to land.
            await Task.Delay(500);
            WebpFiles(svc.CacheDir).Should().BeEmpty(
                "a filled form's thumbnail is personal data at rest and must not reach the disk cache");
            svc.DiskWriteCount.Should().Be(0);

            // A second instance over the same file must render again, never read a cached picture.
            using var svc2 = new ThumbnailCacheService(path, doc, NullLogger.Instance);
            using (var again = await svc2.GetThumbnailAsync(0))
                again.Should().NotBeNull();
            svc2.RenderCount.Should().Be(1);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task FormFilledInSession_StopsPersisting_AndPurgesItsEntries()
    {
        var path = WriteTemp(File.ReadAllBytes(W9OrSkip()));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var svc = new ThumbnailCacheService(path, doc, NullLogger.Instance);

            await svc.WarmAsync(0);
            WebpFiles(svc.CacheDir).Should().NotBeEmpty("precondition: an unfilled form caches");
            svc.PersistsToDisk.Should().BeTrue();

            FirstWritableTextField(doc).SetValue(Secret);
            svc.StopPersistingToDisk();

            svc.PersistsToDisk.Should().BeFalse();
            Directory.Exists(svc.CacheDir).Should().BeFalse(
                "the entries this document wrote before it was filled are deleted by key");

            var writesBefore = svc.DiskWriteCount;
            await svc.WarmAsync(1);
            using (var bmp = await svc.GetThumbnailAsync(0))
                bmp.Should().NotBeNull("the in-memory thumbnail still renders");
            await Task.Delay(500);

            svc.DiskWriteCount.Should().Be(writesBefore, "nothing new is written once the form is filled");
            WebpFiles(svc.CacheDir).Should().BeEmpty();
            svc.RenderCount.Should().Be(2, "page 0 is rendered again, not read back from disk");
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task FilledValueAddedWithoutNotice_IsCaughtAtWriteTime()
    {
        // Belt and braces: an edit that reaches the document without the view model telling
        // the cache (a path not yet wired) is still caught before the next write.
        var path = WriteTemp(File.ReadAllBytes(W9OrSkip()));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var svc = new ThumbnailCacheService(path, doc, NullLogger.Instance);
            await svc.WarmAsync(0);
            WebpFiles(svc.CacheDir).Should().NotBeEmpty("precondition: an unfilled form caches");

            FirstWritableTextField(doc).SetValue(Secret);
            await svc.WarmAsync(1);

            svc.PersistsToDisk.Should().BeFalse();
            WebpFiles(svc.CacheDir).Should().BeEmpty();
        }
        finally { TryDelete(path); }
    }

    [FixedAvaloniaFact]
    public async Task ViewModel_FieldFilledInSession_KeepsThumbnailsOffDisk_AlsoAfterSave()
    {
        var path = WriteTemp(File.ReadAllBytes(W9OrSkip()));
        var savedPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-filled-saved-{Guid.NewGuid():N}.pdf");
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            await vm.LoadDocumentAsync(path);
            vm.ThumbnailsPersistToDiskForTests.Should().BeTrue("precondition: the blank form caches");

            // The viewer's input stores the value, then tells the view model.
            var field = FirstWritableTextField(vm.PdfCoreDocument!);
            field.SetValue(Secret);
            vm.OnFormFieldEdited(field, Secret, oldValue: null);
            vm.ThumbnailsPersistToDiskForTests.Should().BeFalse(
                "a field filled in the session turns disk persistence off");

            // The save reopens the document and starts a new thumbnail session.
            await vm.SaveFileAsAsync(savedPath);
            vm.ThumbnailsPersistToDiskForTests.Should().BeFalse(
                "the saved document carries the value, so its thumbnails stay off disk too");
        }
        finally
        {
            TryDelete(path);
            TryDelete(savedPath);
        }
    }

    // --- helpers ---

    private static async Task AssertNotPersistedAsync(byte[] bytes)
    {
        var path = WriteTemp(bytes);
        try
        {
            using var doc = PdfDocument.Open(path);
            using var svc = new ThumbnailCacheService(path, doc, NullLogger.Instance);
            await svc.WarmAsync(0);
            WebpFiles(svc.CacheDir).Should().BeEmpty(
                "a document carrying filled values must not have its thumbnails written to disk (#2023)");
        }
        finally { TryDelete(path); }
    }

    private static async Task AssertPersistedAsync(byte[] bytes)
    {
        var path = WriteTemp(bytes);
        try
        {
            using var doc = PdfDocument.Open(path);
            using var svc = new ThumbnailCacheService(path, doc, NullLogger.Instance);
            await svc.WarmAsync(0);
            WebpFiles(svc.CacheDir).Should().ContainSingle(
                "a document with no filled values keeps its on-disk thumbnail cache");
        }
        finally { TryDelete(path); }
    }

    private static string W9OrSkip()
    {
        var source = TestRepoLayout.FindFile("test-pdfs", "smoke", "irs-w9.pdf");
        Assert.SkipWhen(source == null,
            TestRepoLayout.AbsenceReason("smoke corpus (scripts/download-smoke-corpus.sh)", "test-pdfs/smoke/irs-w9.pdf"));
        return source!;
    }

    private static byte[] FilledW9Bytes()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(W9OrSkip()));
        FirstWritableTextField(doc).SetValue(Secret);
        return doc.SaveToBytes();
    }

    private static PdfField FirstWritableTextField(PdfDocument doc) =>
        doc.GetAcroForm()!.GetTextFields().First(f => !f.IsReadOnly);

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-thumb-filled-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string[] WebpFiles(string cacheDir) =>
        Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.webp") : Array.Empty<string>();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
