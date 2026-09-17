using AwesomeAssertions;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1563 — an embedded file's declared name is attacker-controlled; Save All
/// must turn it into one safe name inside the folder the user chose.
/// </summary>
public class AttachmentFileNamesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"excise-attachment-names-{Guid.NewGuid():N}");

    public AttachmentFileNamesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("invoice.xml", "invoice.xml")]
    [InlineData("../../.zshrc", "zshrc")]
    [InlineData("/Users/x/Library/LaunchAgents/a.plist", "a.plist")]
    [InlineData("..\\..\\Windows\\evil.dll", "evil.dll")]
    [InlineData("C:\\temp\\report.pdf", "report.pdf")]
    [InlineData("a:b*c?d.txt", "a_b_c_d.txt")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("tab\there.txt", "tabhere.txt")]
    [InlineData("  trailing. ", "trailing")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("résumé 履歴書.docx", "résumé 履歴書.docx")]
    public void ToSafeFileName_KeepsOnlyASinglePortableName(string declared, string expected)
    {
        AttachmentFileNames.ToSafeFileName(declared, 1).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("dir/")]
    [InlineData("\u202E\u200B")]
    public void ToSafeFileName_FallsBackWhenNothingUsableIsLeft(string? declared)
    {
        AttachmentFileNames.ToSafeFileName(declared, 3).Should().Be("attachment-3");
    }

    [Fact]
    public void ToSafeFileName_CapsTheLength_AndKeepsTheExtension()
    {
        var name = AttachmentFileNames.ToSafeFileName(new string('a', 500) + ".xml", 1);

        name.Length.Should().BeLessThanOrEqualTo(180);
        name.Should().EndWith(".xml");
    }

    [Fact]
    public void UniquePathIn_NumbersAClash_AndNeverReturnsAnExistingPath()
    {
        File.WriteAllText(Path.Combine(_folder, "a.xml"), "x");
        File.WriteAllText(Path.Combine(_folder, "a (2).xml"), "x");

        var path = AttachmentFileNames.UniquePathIn(_folder, "a.xml");

        path.Should().Be(Path.Combine(Path.GetFullPath(_folder), "a (3).xml"));
        File.Exists(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/inner.txt")]
    public void UniquePathIn_RefusesANameThatLeavesTheFolder(string unsafeName)
    {
        var act = () => AttachmentFileNames.UniquePathIn(_folder, unsafeName);

        act.Should().Throw<InvalidOperationException>(
            "the last line of defence holds even if a caller skips ToSafeFileName");
    }
}
