using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1850: a trailer <c>/Encrypt N 0 R</c> whose object N is the literal
/// <c>null</c> is an unencrypted file, checked against qpdf.
/// </summary>
/// <remarks>
/// <para>ISO 32000-2 §7.3.9 reads a null entry as an absent one, and the direct
/// <c>/Encrypt null</c> already opened as plaintext (the dictionary drops null
/// values). The reference form did not: <c>NegotiateEncryption</c> saw the
/// resolved <c>PdfNull</c> and refused it as an unreadable /Encrypt (#1828).
/// #1828 stays: a MISSING or UNPARSEABLE /Encrypt object also resolves to null,
/// and opening those with no handler hands ciphertext to every reader, so only
/// an object the file really declares as <c>null</c> is absent.</para>
///
/// <para><b>Measured 2026-09-26, qpdf 12.3.2.</b> The literal-null reference is
/// "File is not encrypted" (agreement). A non-dictionary object (<c>42</c>) is
/// an error in qpdf too (agreement). A missing or unparseable object is read
/// as absent by qpdf with a warning; excise refuses it on purpose
/// (<see cref="AMissingEncryptObject_ExciseRefuses_QpdfReadsItAsAbsent_RegisteredDivergence"/>).</para>
/// </remarks>
public class TrailerEncryptReferenceQpdfTests
{
    private const string Text = "NULLREF";

    /// <summary>A one-page plaintext PDF; object 6 is <paramref name="sixthObject"/> when given.</summary>
    private static byte[] BuildPdf(string? sixthObject, string trailerEntry)
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({Text}) Tj ET";
        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (sixthObject != null)
            objects.Add(sixthObject);

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPos = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Root 1 0 R /Size {objects.Count + 1} {trailerEntry} >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void RequireQpdf() =>
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable,
            "qpdf is the independent parser for this check and is not installed (brew install qpdf)");

    private static string TempPath(string suffix = "") =>
        Path.Combine(Path.GetTempPath(), $"encref_{Guid.NewGuid():N}{suffix}.pdf");

    private static string Write(byte[] pdf)
    {
        var path = TempPath();
        File.WriteAllBytes(path, pdf);
        return path;
    }

    [Fact]
    public void AnEncryptReferenceToTheLiteralNull_IsAnUnencryptedDocument()
    {
        RequireQpdf();
        var path = Write(BuildPdf("null", "/Encrypt 6 0 R"));
        try
        {
            QpdfReferenceTool.RequiresPassword(path).Should().Be(QpdfPasswordStatus.NotEncrypted,
                "qpdf reads a /Encrypt that resolves to null as no encryption at all");
            QpdfReferenceTool.PageCount(path).Should().Be(1, "qpdf opens the file and finds its page");

            using var doc = PdfDocument.Open(File.ReadAllBytes(path));
            doc.IsEncrypted.Should().BeFalse("§7.3.9: a reference to the null object is null, and a null entry is absent");
            doc.GetPage(1).Text.Should().Contain(Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ARealEncryptDictionary_StillOpensEncrypted()
    {
        RequireQpdf();
        var plain = Write(BuildPdf(null, ""));
        var encrypted = TempPath("_enc");
        try
        {
            QpdfReferenceTool.EncryptR4(plain, encrypted, userPassword: "", ownerPassword: "owner")
                .Should().BeTrue("qpdf's own writer produces the /Encrypt dictionary under test");
            QpdfReferenceTool.RequiresPassword(encrypted).Should().Be(QpdfPasswordStatus.PasswordCorrect,
                "qpdf: encrypted, and the empty user password opens it");

            using var doc = PdfDocument.Open(File.ReadAllBytes(encrypted));
            doc.IsEncrypted.Should().BeTrue();
            doc.IsDecrypting.Should().BeTrue("the handler was built, so the ciphertext is read as plaintext text");
            doc.GetPage(1).Text.Should().Contain(Text);
        }
        finally { File.Delete(plain); File.Delete(encrypted); }
    }

    [Fact]
    public void AnEncryptObjectThatIsNotADictionary_ExciseRefuses_AsQpdfDoes()
    {
        RequireQpdf();
        var path = Write(BuildPdf("42", "/Encrypt 6 0 R"));
        try
        {
            QpdfReferenceTool.PageCount(path).Should().BeNull(
                "qpdf reports \"/Encrypt in trailer dictionary is not a dictionary\" and does not open the file");

            var open = () => PdfDocument.Open(File.ReadAllBytes(path));
            open.Should().Throw<PdfParseException>().WithMessage("*/Encrypt*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingEncryptObject_ExciseRefuses_QpdfReadsItAsAbsent_RegisteredDivergence()
    {
        RequireQpdf();
        var path = Write(BuildPdf(null, "/Encrypt 6 0 R"));
        try
        {
            QpdfReferenceTool.RequiresPassword(path).Should().Be(QpdfPasswordStatus.NotEncrypted,
                "qpdf 12.3.2 reads a reference to an undefined object as null, i.e. no /Encrypt");

            var open = () => PdfDocument.Open(File.ReadAllBytes(path));
            open.Should().Throw<PdfEncryptionNotSupportedException>().WithMessage("*/Encrypt*",
                "REGISTERED DIVERGENCE (#1828): a missing /Encrypt object may be hiding a real one, and " +
                "opening without a handler reads ciphertext; only a declared null is absent (#1850)");
        }
        finally { File.Delete(path); }
    }
}
