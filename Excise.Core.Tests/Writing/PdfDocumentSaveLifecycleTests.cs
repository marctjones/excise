using System.Diagnostics;
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
            using (var document = PdfDocument.Open(reader))
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

    private static string SeedFile(string path, int pages = 1)
    {
        using var seed = PdfDocument.CreateNew();
        for (var i = 0; i < pages; i++)
            seed.Pages.AddBlank(200, 200);
        seed.Save(path);
        return path;
    }

    private static FileStream OpenLikeTheGui(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// #1683: a sync client (Dropbox, iCloud, OneDrive) replaces the file by
    /// writing a sibling and renaming it over, while the GUI still reads the
    /// old inode. A save back onto the path would put the stale document over
    /// the newer one; it must be refused and the newer file left alone.
    /// </summary>
    [Fact]
    public void SaveToPath_AfterAnotherProgramReplacedTheFile_RefusesAndKeepsTheNewerVersion()
    {
        var dir = Directory.CreateTempSubdirectory("excise-stale-").FullName;
        try
        {
            var path = SeedFile(Path.Combine(dir, "doc.pdf"));
            using var reader = OpenLikeTheGui(path);
            using var document = PdfDocument.Open(reader);

            var sibling = SeedFile(Path.Combine(dir, "sync-download.pdf"), pages: 3);
            File.Move(sibling, path, overwrite: true);
            var newer = File.ReadAllBytes(path);

            document.Pages.AddBlank(100, 100);
            var save = () => document.Save(path);

            save.Should().Throw<FileChangedOnDiskException>()
                .Which.Message.Should().Contain("changed on disk");
            File.ReadAllBytes(path).Should().Equal(newer, "the newer version must survive");
            Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories)
                .Should().BeEmpty("the refused save cleans up its temporary");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #1683: the same guard for a program that rewrites the file in place
    /// (same inode, same length) — only the modification time moves.
    /// </summary>
    [Fact]
    public void SaveToPath_AfterTheFileWasTouchedOnDisk_Refuses()
    {
        var dir = Directory.CreateTempSubdirectory("excise-stale-").FullName;
        try
        {
            var path = SeedFile(Path.Combine(dir, "doc.pdf"));
            using var document = PdfDocument.Open(path);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
            var before = File.ReadAllBytes(path);

            var save = () => document.Save(path);

            save.Should().Throw<FileChangedOnDiskException>();
            File.ReadAllBytes(path).Should().Equal(before);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #1683: the guard's baseline moves with excise's own saves, so saving
    /// the same open document twice is not mistaken for an outside change,
    /// and Save As to another path is never refused.
    /// </summary>
    [Fact]
    public void SaveToPath_RepeatedOwnSavesAndSaveAs_AreNotRefused()
    {
        var dir = Directory.CreateTempSubdirectory("excise-stale-").FullName;
        try
        {
            var path = SeedFile(Path.Combine(dir, "doc.pdf"));
            var other = SeedFile(Path.Combine(dir, "other.pdf"));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-5));
            using var reader = OpenLikeTheGui(path);
            using var document = PdfDocument.Open(reader);

            document.Pages.AddBlank(100, 100);
            document.Save(path);
            document.Pages.AddBlank(100, 100);
            document.Save(path);
            document.Save(other);

            using var saved = PdfDocument.Open(path);
            saved.PageCount.Should().Be(3);
            using var copy = PdfDocument.Open(other);
            copy.PageCount.Should().Be(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>#1802: an owner-only file must stay owner-only after a save.</summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void SaveToPath_KeepsTheFilesUnixPermissions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix mode bits do not exist on Windows");
        var dir = Directory.CreateTempSubdirectory("excise-mode-").FullName;
        try
        {
            var path = SeedFile(Path.Combine(dir, "private.pdf"));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            using (var document = PdfDocument.Open(path))
            {
                document.Pages.AddBlank(100, 100);
                document.Save(path);
            }

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "an owner-only file must not become readable by others after a save (#1802)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #1802: the save's rename gives the file a new inode, which used to drop
    /// its extended attributes (a Finder comment, measured). The macOS
    /// <c>xattr</c> tool sets and reads them, not excise. The attribute copy
    /// must not change a byte of the PDF either.
    /// </summary>
    [Fact]
    public void SaveToPath_KeepsTheFilesExtendedAttributes_AndWritesTheSameBytes()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "xattr copy is verified on macOS, with its xattr tool");
        var dir = Directory.CreateTempSubdirectory("excise-xattr-").FullName;
        try
        {
            var plain = SeedFile(Path.Combine(dir, "plain.pdf"));
            var tagged = Path.Combine(dir, "tagged.pdf");
            File.Copy(plain, tagged);
            Run("xattr", "-w", "com.apple.metadata:kMDItemFinderComment", "signed copy", tagged);

            foreach (var path in new[] { plain, tagged })
            {
                using var document = PdfDocument.Open(path);
                document.Pages.AddBlank(100, 100);
                document.Save(path);
            }

            Run("xattr", "-p", "com.apple.metadata:kMDItemFinderComment", tagged)
                .Should().Be("signed copy\n", "the Finder comment survives the save (#1802)");
            File.ReadAllBytes(tagged).Should().Equal(File.ReadAllBytes(plain),
                "carrying attributes over must not change what is written to the PDF");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #1802: every attribute, byte for byte: a Finder tag (binary plist), an
    /// empty value, a 128 KiB binary value and quarantine (carried on purpose:
    /// dropping it would clear a download's quarantine on its first save). The
    /// file is read-only, so the attributes must reach the temporary before
    /// its mode does: a read-only file refuses them.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void SaveToPath_KeepsEveryExtendedAttribute_OfAReadOnlyFile()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "xattr copy is verified on macOS, with its xattr tool");
        var dir = Directory.CreateTempSubdirectory("excise-xattr-").FullName;
        try
        {
            var path = SeedFile(Path.Combine(dir, "tagged.pdf"));
            var large = new byte[128 * 1024];
            new Random(1802).NextBytes(large);
            var attributes = new Dictionary<string, string>
            {
                ["com.apple.metadata:_kMDItemUserTags"] =
                    "62706c6973743030a101555265640a36080a0000000000000101000000000000000200000000000000000000000000000010",
                ["com.apple.quarantine"] = Convert.ToHexString("0081;00000000;Safari;"u8),
                ["user.excise.empty"] = "",
                ["user.excise.large"] = Convert.ToHexString(large),
            };
            foreach (var (name, hex) in attributes)
                Run("xattr", "-wx", name, hex, path);
            var readOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(path, readOnly);

            using (var document = PdfDocument.Open(path))
            {
                document.Pages.AddBlank(100, 100);
                document.Save(path);
            }

            File.GetUnixFileMode(path).Should().Be(readOnly);
            foreach (var (name, hex) in attributes)
                string.Concat(Run("xattr", "-px", name, path).Where(c => !char.IsWhiteSpace(c)))
                    .Should().BeEquivalentTo(hex, $"{name} survives the save byte for byte (#1802)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #1802: the attribute copy is best effort. Attributes the save cannot
    /// read (an ACL on the file) or cannot write (an ACL the temporary
    /// inherits from its directory) are dropped; the save itself succeeds.
    /// </summary>
    [Theory]
    [InlineData("deny readextattr", false)]
    [InlineData("deny writeextattr,file_inherit", true)]
    public void SaveToPath_WhenAttributesCannotBeCopied_StillSaves(string acl, bool onDirectory)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "xattr copy is verified on macOS, with its xattr and chmod +a tools");
        var dir = Directory.CreateTempSubdirectory("excise-xattr-").FullName;
        try
        {
            var path = SeedFile(Path.Combine(dir, "locked.pdf"));
            Run("xattr", "-w", "user.excise.note", "kept if possible", path);
            Run("chmod", "+a", $"user:{Environment.UserName} {acl}", onDirectory ? dir : path);

            using (var document = PdfDocument.Open(path))
            {
                document.Pages.AddBlank(100, 100);
                document.Save(path);
            }

            using var saved = PdfDocument.Open(path);
            saved.PageCount.Should().Be(2, "an attribute that cannot be copied must not fail the save");
            Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Run(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue($"{tool} finishes");
        process.ExitCode.Should().Be(0, $"{tool} {string.Join(' ', args.Take(2))}: {error.Result}");
        return output;
    }

    /// <summary>
    /// #1683, measured: saving onto a symlink used to replace the link with a
    /// regular file, and the file it pointed at never received the save. The
    /// save now writes through the link.
    /// </summary>
    [Fact]
    public void SaveToPath_OntoASymlink_WritesThroughAndKeepsTheLink()
    {
        var dir = Directory.CreateTempSubdirectory("excise-link-").FullName;
        try
        {
            var target = SeedFile(Path.Combine(dir, "target.pdf"));
            var link = Path.Combine(dir, "link.pdf");
            try
            {
                File.CreateSymbolicLink(link, "target.pdf");
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("creating a symlink needs Developer Mode or elevation on Windows");
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("creating a symlink needs Developer Mode or elevation on Windows");
            }

            using (var document = PdfDocument.Open(link))
            {
                document.Pages.AddBlank(100, 100);
                document.Save(link);
            }

            new FileInfo(link).LinkTarget.Should().Be("target.pdf", "the link itself survives");
            using var saved = PdfDocument.Open(target);
            saved.PageCount.Should().Be(2, "the save went to the file the link points at");
            Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
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

        using var firstReader = PdfDocument.Open(first, new PdfOpenOptions { UserPassword = "lifecycle-password" });
        using var secondReader = PdfDocument.Open(second, new PdfOpenOptions { UserPassword = "lifecycle-password" });
        firstReader.PageCount.Should().Be(1);
        secondReader.PageCount.Should().Be(1);

        var plaintext = document.SaveToBytes();
        Encoding.Latin1.GetString(plaintext).Should().NotContain("/Encrypt");
        using var plaintextReader = PdfDocument.Open(plaintext);
        plaintextReader.IsEncrypted.Should().BeFalse();
    }

    private static int GetEncryptionObjectNumber(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes, new PdfOpenOptions { UserPassword = "lifecycle-password" });
        return document.Trailer.GetReference("Encrypt").ObjectNum;
    }
}
