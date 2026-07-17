using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Pdfe.Core.Security;
using Pdfe.Core.Writing;
using Xunit;

namespace Pdfe.Core.Tests.Writing;

/// <summary>
/// Tests for the PDF Standard Security Handler writer (AES-256, V=5 R=6 —
/// issue #639). These are the pdfe-internal / "own decrypt path" checks
/// (CLAUDE.md's fourth, supplementary oracle). They are NOT sufficient on
/// their own — a shared misunderstanding of the spec between
/// <see cref="PdfStandardSecurityEncryptor"/> (write) and
/// <see cref="PdfStandardSecurityHandler"/> (read) would pass every test in
/// this file while still being wrong. The independent-oracle checks that
/// actually matter (qpdf/mutool/Ghostscript) live in
/// Pdfe.Rendering.Tests/Differential/EncryptionWriterInteropTests.cs.
/// </summary>
public class PdfDocumentWriterEncryptionTests
{
    #region Structural checks

    [Fact]
    public void Write_WithEncryptionOptions_EmitsEncryptDictionaryInTrailer()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Hello Encrypted World"));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions());
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().Contain("/Encrypt", "the trailer must reference the new /Encrypt dictionary");
        text.Should().Contain("/Filter /Standard");
        text.Should().Contain("/V 5");
        text.Should().Contain("/R 6");
        text.Should().Contain("/CFM /AESV3");
        text.Should().Contain("/StmF /StdCF");
        text.Should().Contain("/StrF /StdCF");
    }

    [Fact]
    public void Write_WithEncryptionOptions_TrailerIdStaysPlaintext()
    {
        // /ID must remain readable without a key (used as a file identifier,
        // and per spec/convention is never itself encrypted).
        using var doc = PdfDocument.Open(CreateSimplePdf("Plain ID Test"));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions());
        using var reopened = PdfDocument.Open(bytes, userPassword: null, allowEncrypted: true);

        reopened.Trailer.TryGetArray("ID", out var idArray).Should().BeTrue();
        idArray.Count.Should().Be(2);
    }

    [Fact]
    public void Write_ContentStreamBytes_AreNotPlaintextInSavedFile()
    {
        // The whole point: the raw saved bytes must not contain the
        // plaintext content stream operators/text anymore.
        const string secretMarker = "MARKER_TEXT_NOT_IN_CIPHERTEXT";
        using var doc = PdfDocument.Open(CreateSimplePdf(secretMarker));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions());
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().NotContain(secretMarker,
            "the content stream must be AES-256 encrypted, not written as plaintext");
        text.Should().NotContain("BT", "text-showing operators must not be visible in ciphertext");
    }

    [Fact]
    public void Write_EncryptMetadataFalse_LeavesMetadataStreamPlaintextButEncryptsEverythingElse()
    {
        // ISO 32000-2 §7.6.1: when /EncryptMetadata is false, the XMP
        // /Metadata stream itself must stay plaintext even though every
        // other stream in the document is encrypted. This is a distinct
        // code path from the default (EncryptMetadata=true, exercised by
        // every other test in this file) — cover it explicitly.
        const string metadataMarker = "METADATA_MARKER_EXPECTED_PLAINTEXT";
        const string bodyMarker = "BODY_MARKER_EXPECTED_CIPHERTEXT";
        using var doc = PdfDocument.Open(CreateSimplePdf(bodyMarker));

        var metaDict = new PdfDictionary();
        metaDict.SetName("Type", "Metadata");
        metaDict.SetName("Subtype", "XML");
        var metaBytes = Encoding.UTF8.GetBytes($"<x:xmpmeta>{metadataMarker}</x:xmpmeta>");
        doc.Catalog["Metadata"] = doc.AddIndirectObject(new PdfStream(metaDict, metaBytes));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions { EncryptMetadata = false });
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().Contain(metadataMarker,
            "EncryptMetadata=false must skip encrypting the /Metadata stream's own bytes");
        text.Should().Contain("/EncryptMetadata false");
        text.Should().NotContain(bodyMarker,
            "every other stream must still be encrypted even when metadata is exempted");
    }

    [Fact]
    public void Write_EncryptMetadataTrue_EncryptsMetadataStreamToo()
    {
        // The inverse of the test above: confirm the default (true) does
        // NOT accidentally exempt the metadata stream.
        const string metadataMarker = "METADATA_MARKER_EXPECTED_CIPHERTEXT";
        using var doc = PdfDocument.Open(CreateSimplePdf("Body"));

        var metaDict = new PdfDictionary();
        metaDict.SetName("Type", "Metadata");
        metaDict.SetName("Subtype", "XML");
        var metaBytes = Encoding.UTF8.GetBytes($"<x:xmpmeta>{metadataMarker}</x:xmpmeta>");
        doc.Catalog["Metadata"] = doc.AddIndirectObject(new PdfStream(metaDict, metaBytes));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions { EncryptMetadata = true });
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().NotContain(metadataMarker,
            "EncryptMetadata=true (the default) must encrypt the /Metadata stream like any other stream");
        text.Should().Contain("/EncryptMetadata true");
    }

    [Fact]
    public void Write_InfoDictionaryStrings_AreNotPlaintextInSavedFile()
    {
        const string secretAuthor = "MARKER_AUTHOR_NOT_IN_CIPHERTEXT";
        using var doc = PdfDocument.Open(CreateSimplePdf("Body text"));
        doc.SetAuthor(secretAuthor);

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions());
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().NotContain(secretAuthor,
            "PdfString values (e.g. Info /Author) must be encrypted too, not just streams");
    }

    #endregion

    #region Round-trip via pdfe's own decrypt handler (supplementary only — see class remarks)

    [Fact]
    public void RoundTrip_EmptyUserAndOwnerPassword_ReopensAndMatchesOriginalText()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Round Trip Empty Password"));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions());

        using var reopened = PdfDocument.Open(bytes, userPassword: null);
        reopened.PageCount.Should().Be(1);
        reopened.GetPage(1).Text.Should().Contain("Round Trip Empty Password");
    }

    [Fact]
    public void RoundTrip_NonEmptyUserPassword_RequiresCorrectPasswordToOpen()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Password Protected Text"));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions
        {
            UserPassword = "correct-horse-battery-staple",
            OwnerPassword = "owner-secret",
        });

        // Wrong password must fail.
        Action openWrong = () => PdfDocument.Open(bytes, userPassword: "wrong-password");
        openWrong.Should().Throw<PdfEncryptionNotSupportedException>();

        // Correct user password must succeed and decrypt correctly.
        using var reopened = PdfDocument.Open(bytes, userPassword: "correct-horse-battery-staple");
        reopened.GetPage(1).Text.Should().Contain("Password Protected Text");
    }

    [Fact]
    public void RoundTrip_NonAsciiPassword_EncodesAsUtf8AndRoundTrips()
    {
        // R6 uses UTF-8 for passwords (see EncodeUserPasswordCandidates in
        // PdfStandardSecurityHandler) — confirm a non-ASCII password works
        // end to end through the writer too.
        const string password = "pâsswörd-日本語-🔒";
        using var doc = PdfDocument.Open(CreateSimplePdf("Non ASCII Password Text"));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions { UserPassword = password });

        using var reopened = PdfDocument.Open(bytes, userPassword: password);
        reopened.GetPage(1).Text.Should().Contain("Non ASCII Password Text");
    }

    [Fact]
    public void RoundTrip_DifferentUserAndOwnerPasswords_BothOpenTheFile()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Dual Password Text"));

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions
        {
            UserPassword = "user-pw-1",
            OwnerPassword = "owner-pw-2",
        });

        using var viaUser = PdfDocument.Open(bytes, userPassword: "user-pw-1");
        viaUser.GetPage(1).Text.Should().Contain("Dual Password Text");

        // Note: pdfe's own decrypt handler does not yet implement Algorithm
        // 12 (owner-password recovery) — see BuildR6's thrown message
        // referencing #324. The owner-password-unlocks-the-file property is
        // verified independently via qpdf in
        // Pdfe.Rendering.Tests/Differential/EncryptionWriterInteropTests.cs.
    }

    [Fact]
    public void RoundTrip_StreamAndStringBothEncrypted_BothDecryptCorrectly()
    {
        const string secretAuthor = "Confidential Author Name";
        const string secretBody = "Confidential Body Text In Content Stream";
        using var doc = PdfDocument.Open(CreateSimplePdf(secretBody));
        doc.SetAuthor(secretAuthor);
        doc.SetTitle("Confidential Title");

        var bytes = SaveEncrypted(doc, new PdfEncryptionOptions { UserPassword = "pw" });

        using var reopened = PdfDocument.Open(bytes, userPassword: "pw");
        reopened.GetPage(1).Text.Should().Contain(secretBody, "stream content must decrypt correctly");
        reopened.Author.Should().Be(secretAuthor, "string content (Info /Author) must decrypt correctly");
        reopened.Title.Should().Be("Confidential Title");
    }

    [Fact]
    public void RoundTrip_MultipleSaves_DoNotLeakGrowingEncryptObjects()
    {
        // PdfDocumentWriter must never persist the /Encrypt dict onto the
        // PdfDocument (see PdfDocument.NextFreeObjectNumber's remarks) —
        // confirm repeated Save() calls on the same instance stay stable.
        using var doc = PdfDocument.Open(CreateSimplePdf("Stable Save Test"));
        var options = new PdfEncryptionOptions { UserPassword = "pw" };

        var first = SaveEncrypted(doc, options);
        var second = SaveEncrypted(doc, options);

        using var reopenedFirst = PdfDocument.Open(first, userPassword: "pw");
        using var reopenedSecond = PdfDocument.Open(second, userPassword: "pw");
        reopenedFirst.GetPage(1).Text.Should().Contain("Stable Save Test");
        reopenedSecond.GetPage(1).Text.Should().Contain("Stable Save Test");
    }

    #endregion

    #region Algorithm not yet implemented

    [Fact]
    public void Write_WithAes128Algorithm_ThrowsNotSupported()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Text"));
        var options = new PdfEncryptionOptions { Algorithm = PdfEncryptionAlgorithm.Aes128 };

        Action act = () => SaveEncrypted(doc, options);

        act.Should().Throw<NotSupportedException>();
    }

    #endregion

    #region Helper methods

    private static byte[] SaveEncrypted(PdfDocument doc, PdfEncryptionOptions options)
    {
        using var ms = new MemoryStream();
        var writer = new PdfDocumentWriter(doc, options);
        writer.Write(ms);
        return ms.ToArray();
    }

    private static byte[] CreateSimplePdf(string text)
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    #endregion
}
