using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Writing;
using Xunit;

namespace Excise.Core.Tests.Writing;

public class PdfDocumentSaveLifecycleTests
{
    /// <summary>
    /// #1567: the GUI reads its current document from a shared FileStream
    /// rather than a whole-file copy, so a save back onto that path must not
    /// truncate the file the writer is still copying unmodified streams from.
    /// Save(path) therefore writes a sibling temp and renames over the target;
    /// the reader keeps the old inode. With FileMode.Create on the target this
    /// test's save reads its own truncation and the result is not a document.
    /// </summary>
    [Fact]
    public void SaveToPath_OverTheFileTheDocumentIsReadingFrom_WritesACompleteDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-inplace-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var seed = PdfDocument.CreateNew())
            {
                seed.Pages.AddBlank(200, 200);
                seed.Save(path);
            }

            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var document = PdfDocument.Open(reader, ownsStream: false))
            {
                document.Pages.AddBlank(100, 100);
                document.Save(path);
                document.PageCount.Should().Be(2, "the open document still reads the old inode");
            }

            using var saved = PdfDocument.Open(path);
            saved.PageCount.Should().Be(2, "the file on disk is the complete new document");
            Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*.tmp")
                .Should().BeEmpty("the temporary was renamed away");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveToPath_WhenTheWriteFails_LeavesTheOriginalFileAndNoTemporary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-atomic-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var seed = PdfDocument.CreateNew())
            {
                seed.Pages.AddBlank(200, 200);
                seed.Save(path);
            }
            var original = File.ReadAllBytes(path);

            using (var document = PdfDocument.Open(path))
            {
                document.RegisterPreSaveAction(() => throw new InvalidOperationException("simulated writer failure"));
                var save = () => document.Save(path);
                save.Should().Throw<InvalidOperationException>();
            }

            File.ReadAllBytes(path).Should().Equal(original, "a failed save must not touch the target");
            Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*.tmp")
                .Should().BeEmpty("the temporary is cleaned up on failure");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QueriesAndWriterConstruction_DoNotRunPreSaveActions()
    {
        using var document = PdfDocument.CreateNew();
        var calls = 0;
        document.RegisterPreSaveAction(() => calls++);

        _ = document.PageCount;
        _ = document.GetReferenceTo(document.Catalog);
        var writer = new PdfDocumentWriter(document);

        calls.Should().Be(0,
            "only serialization may finalize fonts, tags, or PDF/A policy");

        using var first = new MemoryStream();
        writer.Write(first);
        calls.Should().Be(1);

        using var second = new MemoryStream();
        writer.Write(second);
        calls.Should().Be(2,
            "each Write is one save lifecycle, even when a writer is reused");

        document.SaveToBytes().Should().NotBeEmpty();
        calls.Should().Be(3,
            "each public Save facade must enter the same lifecycle exactly once");
    }

    [Fact]
    public void PreSaveObjectRegistration_IsIncludedInTheSameSaveGraph()
    {
        using var document = PdfDocument.CreateNew();
        var finalized = false;
        document.RegisterPreSaveAction(() =>
        {
            if (finalized)
                return;

            var policy = new PdfDictionary
            {
                ["Type"] = new PdfName("ExciseSavePolicy"),
                ["Marker"] = new PdfString("finalized-before-snapshot"),
            };
            document.Catalog["ExciseSavePolicy"] = document.AddIndirectObject(policy);
            finalized = true;
        });

        var saved = document.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        var policyReference = reopened.Catalog.GetReference("ExciseSavePolicy");
        var policy = reopened.GetObject(policyReference).Should().BeOfType<PdfDictionary>().Subject;
        policy.GetString("Marker").Should().Be("finalized-before-snapshot");
    }

    [Fact]
    public void RepeatedEncryptedSaves_KeepTemporaryEncryptionOutOfDocumentIdentity()
    {
        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank();
        var catalogReference = document.GetReferenceTo(document.Catalog);
        var options = new PdfEncryptionOptions { UserPassword = "lifecycle-password" };

        var first = document.SaveToBytes(options);
        var second = document.SaveToBytes(options);

        GetEncryptionObjectNumber(first).Should().Be(GetEncryptionObjectNumber(second),
            "the write-only Encrypt dictionary must not consume persistent object numbers");
        document.GetReferenceTo(document.Catalog).Should().Be(catalogReference,
            "writer-only encryption state must not replace document objects");

        using var firstReader = PdfDocument.Open(first, userPassword: "lifecycle-password");
        using var secondReader = PdfDocument.Open(second, userPassword: "lifecycle-password");
        firstReader.PageCount.Should().Be(1);
        secondReader.PageCount.Should().Be(1);

        var plaintext = document.SaveToBytes();
        Encoding.Latin1.GetString(plaintext).Should().NotContain("/Encrypt");
        using var plaintextReader = PdfDocument.Open(plaintext);
        plaintextReader.IsEncrypted.Should().BeFalse();
    }

    private static int GetEncryptionObjectNumber(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes, "lifecycle-password");
        return document.Trailer.GetReference("Encrypt").ObjectNum;
    }
}
