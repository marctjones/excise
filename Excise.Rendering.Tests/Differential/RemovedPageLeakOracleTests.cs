using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Security;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A page the user removes must not survive in the saved file (#2012): not its
/// content stream, its page-only font, its image, its annotations or the value
/// of a field drawn only on it. Checked on the saved BYTES with two oracles that
/// are not excise's reader: <see cref="SavedPdfLeakScanner"/> (inflates every
/// stream with <c>ZLibStream</c>) and qpdf's JSON dump of every object in the
/// file, reachable or not; plus <c>qpdf --check</c> clean and
/// <c>--show-npages</c>. An encrypted save is decrypted by qpdf, not excise.
/// </summary>
/// <remarks>
/// Before the fix every <see cref="RemovedPageBackReference"/> except
/// <c>None</c> shipped the removed page's text on every save path; Extract
/// Pages never did (it builds a new document from the kept pages).
/// </remarks>
public sealed class RemovedPageLeakOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"removed-page-leak-{Guid.NewGuid():N}");

    public RemovedPageLeakOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public enum SavePath
    {
        Classic,      // %PDF-1.4: classic xref table
        Compressed,   // %PDF-1.7: object streams + xref stream
        Encrypted,    // encrypted on save (AES-256)
        ReopenResave, // save, reopen, save again
    }

    public static TheoryData<Back, SavePath> Cases()
    {
        var data = new TheoryData<Back, SavePath>();
        foreach (var b in Enum.GetValues<Back>())
            foreach (var s in Enum.GetValues<SavePath>())
                data.Add(b, s);
        return data;
    }

    public static TheoryData<Back> BackReferences() => new(Enum.GetValues<Back>());

    [Theory]
    [MemberData(nameof(Cases))]
    public void RemovePage_ThenSave_RemovedPageIsNotInTheFile(Back back, SavePath path)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        var source = F.Build(back, path == SavePath.Classic ? "1.4" : "1.7");
        foreach (var token in F.RemovedTokens(back))
            SavedPdfLeakScanner.FindTerm(source, token).Should().NotBeEmpty($"the fixture must carry {token}");

        byte[] saved;
        using (var doc = PdfDocument.Open(source))
        {
            doc.Pages.RemoveAt(1);
            saved = Save(doc, path);
        }

        var plain = path == SavePath.Encrypted ? Decrypt(saved) : saved;
        var label = $"{back}/{path}";
        AssertAbsent(plain, label, F.RemovedTokens(back));
        AssertValidWithPages(plain, 2, label);
    }

    [Theory]
    [MemberData(nameof(BackReferences))]
    public void ExtractPages_UnselectedPageIsNotInTheFile(Back back)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(back, "1.7")))
        using (var extracted = PdfDocumentSplitter.ExtractPages(doc, [0, 2]))
            saved = extracted.SaveToBytes();

        AssertAbsent(saved, $"extract/{back}", F.RemovedTokens(back));
        AssertValidWithPages(saved, 2, $"extract/{back}");
    }

    [Fact]
    public void RemovePage_FieldWithAWidgetOnAKeptPage_KeepsTheField()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.AcroFormFieldKids, "1.7", widgetOnPageOneToo: true)))
        {
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        AssertAbsent(saved, "field kept", F.PageTokens);
        QpdfDump(saved).Should().Contain(F.FieldValue, "the field still has a widget on page 1");
        AssertValidWithPages(saved, 2, "field kept");
    }

    [Theory]
    [InlineData(Back.Outline)]
    [InlineData(Back.AcroFormFieldKids)]
    public void EncryptedSource_RemovePage_ThenSaveReEncrypted_RemovedPageIsNotInTheFile(Back back)
    {
        // The cut resolves objects of an encrypted document inside the save: qpdf
        // encrypts the source (AES-128, R4), excise opens it with the password,
        // removes the page and saves re-encrypted; qpdf decrypts the result.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        var encryptedSource = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        QpdfReferenceTool.EncryptR4(Write(F.Build(back, "1.7")), encryptedSource, Password, Password)
            .Should().BeTrue("qpdf must encrypt the fixture");

        byte[] saved;
        using (var doc = PdfDocument.Open(File.ReadAllBytes(encryptedSource), new PdfOpenOptions { UserPassword = Password }))
        {
            doc.Pages.RemoveAt(1);
            var reEncrypt = doc.GetReEncryptionOptions(Password);
            reEncrypt.Should().NotBeNull("an encrypted source saves encrypted");
            saved = doc.SaveToBytes(reEncrypt);
        }

        QpdfReferenceTool.IsEncrypted(Write(saved)).Should().BeTrue("the round trip keeps the file encrypted");
        var plain = Decrypt(saved);
        AssertAbsent(plain, $"encrypted source/{back}", F.RemovedTokens(back));
        AssertValidWithPages(plain, 2, $"encrypted source/{back}");
    }

    [Fact]
    public void NestedPageTree_RemovePage_RemovedPageIsNotInTheFile()
    {
        // RemoveAt flattens a nested tree first (EnsureFlatKids); the page an
        // outline points at sits under an intermediate /Pages node.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        var source = F.Build(Back.Outline, "1.4", nestPagesTwoAndThree: true);
        var sourceFile = Write(source);
        QpdfReferenceTool.Check(sourceFile)!.Value.Success.Should().BeTrue("the nested fixture must be valid");
        QpdfReferenceTool.PageCount(sourceFile).Should().Be(3, "the nested fixture has three pages");

        byte[] saved;
        using (var doc = PdfDocument.Open(source))
        {
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        AssertAbsent(saved, "nested tree", F.RemovedTokens(Back.Outline));
        AssertValidWithPages(saved, 2, "nested tree");
    }

    // ── Oracles ─────────────────────────────────────────────────────────

    private string Write(byte[] bytes)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(file, bytes);
        return file;
    }

    private string QpdfDump(byte[] saved) => CarrierTrapIndependentCorroborationTests.QpdfDump(Write(saved));

    private void AssertAbsent(byte[] saved, string label, string[] tokens)
    {
        var dump = QpdfDump(saved);
        foreach (var token in tokens)
        {
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty(
                $"{label}: the removed page's {token} must not be in the saved bytes");
            dump.Should().NotContain(token, $"{label}: qpdf's dump of every object must not hold {token}");
        }
    }

    private void AssertValidWithPages(byte[] saved, int expectedPages, string label)
    {
        var file = Write(saved);
        var check = QpdfReferenceTool.Check(file);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"{label}: qpdf --check must pass: {check.Value.Output}");
        check.Value.Output.Should().NotContain("WARNING", $"{label}: qpdf --check must be clean");
        QpdfReferenceTool.PageCount(file).Should().Be(expectedPages, label);
        var dump = QpdfDump(saved);
        dump.Should().Contain(F.KeptOne, $"{label}: page 1 is kept");
        dump.Should().Contain(F.KeptThree, $"{label}: page 3 is kept");
    }

    private const string Password = "removed-page-test";

    private static byte[] Save(PdfDocument doc, SavePath path)
    {
        switch (path)
        {
            case SavePath.Encrypted:
                return doc.SaveToBytes(new PdfEncryptionOptions
                {
                    UserPassword = Password,
                    OwnerPassword = Password,
                    Algorithm = PdfEncryptionAlgorithm.Aes256,
                });
            case SavePath.ReopenResave:
            {
                using var reopened = PdfDocument.Open(doc.SaveToBytes());
                return reopened.SaveToBytes();
            }
            default:
                return doc.SaveToBytes();
        }
    }

    private byte[] Decrypt(byte[] encrypted)
    {
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        QpdfReferenceTool.Decrypt(Write(encrypted), output, Password).Should().BeTrue("qpdf must decrypt excise's output");
        return File.ReadAllBytes(output);
    }
}
