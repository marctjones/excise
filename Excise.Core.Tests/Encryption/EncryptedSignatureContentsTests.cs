using AwesomeAssertions;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Encryption;

/// <summary>
/// #1823: an AES-encrypted file whose signature dictionary keeps its /Contents in the clear (as the
/// spec requires) must open, save and round-trip. The IRCC forms carry an Adobe usage-rights (UR3)
/// signature, which has no /Type, so it cannot be recognised by /Type.
/// </summary>
public class EncryptedSignatureContentsTests
{
    private const string Relative = "test-pdfs/xfa-real/imm5257e.pdf";

    [Fact]
    public void EncryptedFormWithAUsageRightsSignature_OpensAndSaves()
    {
        var path = TestRepoLayout.FindFile(Relative);
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("real-world XFA corpus (scripts/download-xfa-real-corpus.sh)", Relative));

        using var document = PdfDocument.Open(File.ReadAllBytes(path!));

        // Every object resolves: object 121 is the signature dictionary that used to throw.
        var act = () => document.GetAllObjects().ToArray();
        act.Should().NotThrow();

        var saved = document.SaveToBytes();
        saved.Length.Should().BeGreaterThan(100_000);
        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(document.PageCount);
    }
}
