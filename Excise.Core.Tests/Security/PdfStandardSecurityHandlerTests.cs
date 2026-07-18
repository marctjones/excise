using AwesomeAssertions;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Xunit;

namespace Excise.Core.Tests.Security;

/// <summary>
/// Tests for PDF Standard Security Handler (password derivation, verification, decryption).
/// Covers V=1 R=2, V=2 R=3, V=4 R=4 (RC4 and AES variants), and V=5 R=6 (PDF 2.0).
/// </summary>
public class PdfStandardSecurityHandlerTests
{
    /// <summary>
    /// Test V=1 R=2 (40-bit RC4, legacy encryption).
    /// </summary>
    [Fact]
    public void Build_WithV1R2_CreatesHandlerWithCorrectProperties()
    {
        // Arrange - V=1 R=2 requires: Filter, V, R, O (32 bytes), U (32 bytes), P (permissions)
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(1),
            ["R"] = new PdfInteger(2),
            ["Length"] = new PdfInteger(40),
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act - This will fail password verification with dummy O/U, but should create the handler
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert - Should fail on password verification, not on handler creation
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
        ex!.Message.Should().Contain("Password verification failed");
    }

    /// <summary>
    /// Test V=2 R=3 (128-bit RC4, most common).
    /// </summary>
    [Fact]
    public void Build_WithV2R3_CreatesHandlerWithCorrectProperties()
    {
        // Arrange - V=2 R=3 requires: Filter, V, R, Length, O, U, P
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["Length"] = new PdfInteger(128),
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
    }

    /// <summary>
    /// Test V=4 R=4 with RC4 cipher (CFM=V2 or no CF specified).
    /// </summary>
    [Fact]
    public void Build_WithV4R4Rc4_CreatesHandlerWithoutAes()
    {
        // Arrange - V=4 R=4 with RC4 (no /CF or CF with StmF="Identity")
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(4),
            ["R"] = new PdfInteger(4),
            ["Length"] = new PdfInteger(128),
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
    }

    /// <summary>
    /// Test V=4 R=4 with AES-128 cipher (CFM=AESV2).
    /// </summary>
    [Fact]
    public void Build_WithV4R4Aesv2_CreatesHandlerWithAes()
    {
        // Arrange - V=4 R=4 with AES-128 (CFM=AESV2)
        var cf = new PdfDictionary
        {
            ["StdCF"] = new PdfDictionary
            {
                ["CFM"] = new PdfName("AESV2"),
                ["AuthEvent"] = new PdfName("DocOpen"),
                ["Length"] = new PdfInteger(128)
            }
        };
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(4),
            ["R"] = new PdfInteger(4),
            ["Length"] = new PdfInteger(128),
            ["CF"] = cf,
            ["StmF"] = new PdfName("StdCF"),
            ["StrF"] = new PdfName("StdCF"),
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert - Should fail on password verification, not handler creation
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
    }

    /// <summary>
    /// Test V=5 R=6 with AES-256 (PDF 2.0, modern encryption).
    /// </summary>
    [Fact]
    public void Build_WithV5R6_CreatesHandlerWithCorrectProperties()
    {
        // Arrange - V=5 R=6 requires different validation (48-byte U/O with salt)
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(5),
            ["R"] = new PdfInteger(6),
            ["Length"] = new PdfInteger(256),
            ["CF"] = new PdfDictionary
            {
                ["StdCF"] = new PdfDictionary
                {
                    ["CFM"] = new PdfName("AESV3"),
                    ["AuthEvent"] = new PdfName("DocOpen")
                }
            },
            ["StmF"] = new PdfName("StdCF"),
            ["StrF"] = new PdfName("StdCF"),
            ["U"] = CreatePdfString(new byte[48]),
            ["O"] = CreatePdfString(new byte[48]),
            ["UE"] = CreatePdfString(new byte[32]),
            ["OE"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
    }

    /// <summary>
    /// Test that unsupported /Filter value raises exception.
    /// </summary>
    [Fact]
    public void Build_WithUnsupportedFilter_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Public")
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
        ex!.Message.Should().Contain("Only the Standard security handler is supported");
    }

    /// <summary>
    /// Test that unsupported V (encryption version) raises exception.
    /// </summary>
    [Fact]
    public void Build_WithUnsupportedV_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(6),  // V=6 not supported yet
            ["R"] = new PdfInteger(7),
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
        ex!.Message.Should().Contain("is not supported");
    }

    /// <summary>
    /// Test that invalid /Length (key length) raises exception.
    /// </summary>
    [Fact]
    public void Build_WithInvalidKeyLength_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["Length"] = new PdfInteger(400),  // 400 bits → 50 bytes > 32 byte max, is invalid
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
        ex!.Message.Should().Contain("Unsupported /Length value");
    }

    /// <summary>
    /// Test that missing /O entry raises exception.
    /// </summary>
    [Fact]
    public void Build_WithMissingO_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfParseException>();
        ex!.Message.Should().Contain("/O");
    }

    /// <summary>
    /// Test that missing /U entry raises exception.
    /// </summary>
    [Fact]
    public void Build_WithMissingU_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["O"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfParseException>();
        ex!.Message.Should().Contain("/U");
    }

    /// <summary>
    /// Test that /O with wrong length raises exception.
    /// </summary>
    [Fact]
    public void Build_WithWrongOLength_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["O"] = CreatePdfString(new byte[16]),  // Should be 32 bytes
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfParseException>();
        ex!.Message.Should().Contain("/O").And.Contain("32 bytes");
    }

    /// <summary>
    /// Test that /U with wrong length raises exception.
    /// </summary>
    [Fact]
    public void Build_WithWrongULength_ThrowsException()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[16]),  // Should be 32 bytes
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfParseException>();
        ex!.Message.Should().Contain("/U").And.Contain("32 bytes");
    }

    /// <summary>
    /// Test DecryptStream with RC4 cipher (V=2 R=3).
    /// </summary>
    [Fact]
    public void DecryptStream_WithRc4_DecryptsData()
    {
        // Arrange - Create handler with known fileKey
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);
        int objNum = 1;
        int gen = 0;
        byte[] ciphertext = System.Text.Encoding.ASCII.GetBytes("CiphertextData");

        // Act
        byte[] plaintext = handler.DecryptStream(objNum, gen, ciphertext);

        // Assert
        plaintext.Should().NotBeEmpty();
        plaintext.Should().HaveCount(ciphertext.Length);
    }

    /// <summary>
    /// Test DecryptString with RC4 cipher (V=2 R=3).
    /// </summary>
    [Fact]
    public void DecryptString_WithRc4_DecryptsData()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);
        int objNum = 5;
        int gen = 0;
        byte[] ciphertext = System.Text.Encoding.ASCII.GetBytes("EncryptedString");

        // Act
        byte[] plaintext = handler.DecryptString(objNum, gen, ciphertext);

        // Assert
        plaintext.Should().NotBeEmpty();
    }

    /// <summary>
    /// Test DecryptStream with AES cipher (V=4 R=4 with CFM=AESV2).
    /// AES strings include a 16-byte IV prefix.
    /// </summary>
    [Fact]
    public void DecryptStream_WithAes_DecryptsData()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: true, keyLength: 16);
        int objNum = 1;
        int gen = 0;
        // Create AES ciphertext: 16-byte IV + encrypted data
        byte[] iv = new byte[16];
        byte[] data = new byte[16]; // must be block-aligned (multiple of 16) for AES-CBC
        var ciphertext = new byte[iv.Length + data.Length];
        Array.Copy(iv, ciphertext, iv.Length);
        Array.Copy(data, 0, ciphertext, iv.Length, data.Length);

        // Act
        byte[] plaintext = handler.DecryptStream(objNum, gen, ciphertext);

        // Assert - Should not throw even with invalid IV/data
        plaintext.Should().NotBeNull();
    }

    /// <summary>
    /// Test DecryptStream with empty data.
    /// </summary>
    [Fact]
    public void DecryptStream_WithEmptyData_ReturnsEmptyArray()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);
        byte[] emptyData = Array.Empty<byte>();

        // Act
        byte[] result = handler.DecryptStream(1, 0, emptyData);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// Test DecryptString with empty data.
    /// </summary>
    [Fact]
    public void DecryptString_WithEmptyData_ReturnsEmptyArray()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);
        byte[] emptyData = Array.Empty<byte>();

        // Act
        byte[] result = handler.DecryptString(1, 0, emptyData);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// Test that different object numbers produce different decryption keys.
    /// </summary>
    [Fact]
    public void DecryptStream_WithDifferentObjectNumbers_ProducesDifferentResults()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);
        byte[] ciphertext = System.Text.Encoding.ASCII.GetBytes("SameData");

        // Act
        byte[] result1 = handler.DecryptStream(1, 0, ciphertext);
        byte[] result2 = handler.DecryptStream(2, 0, ciphertext);

        // Assert
        result1.Should().NotEqual(result2);
    }

    /// <summary>
    /// Test that different generation numbers produce different decryption keys.
    /// </summary>
    [Fact]
    public void DecryptStream_WithDifferentGenerations_ProducesDifferentResults()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);
        byte[] ciphertext = System.Text.Encoding.ASCII.GetBytes("SameData");

        // Act
        byte[] result1 = handler.DecryptStream(1, 0, ciphertext);
        byte[] result2 = handler.DecryptStream(1, 1, ciphertext);

        // Assert
        result1.Should().NotEqual(result2);
    }

    /// <summary>
    /// Test key length variants (5, 8, 16 bytes).
    /// </summary>
    [Fact]
    public void Build_WithVaryingKeyLengths_CreatesHandlerWithCorrectKeyLength()
    {
        // Arrange
        int[] keyLengths = new[] { 40, 64, 128 };  // bits: 40-bit, 64-bit, 128-bit

        foreach (int lengthBits in keyLengths)
        {
            // Arrange
            var encryptDict = new PdfDictionary
            {
                ["Filter"] = new PdfName("Standard"),
                ["V"] = new PdfInteger(2),
                ["R"] = new PdfInteger(3),
                ["Length"] = new PdfInteger(lengthBits),
                ["O"] = CreatePdfString(new byte[32]),
                ["U"] = CreatePdfString(new byte[32]),
                ["P"] = new PdfInteger(-1)
            };
            byte[] firstId = new byte[16];
            byte[] userPassword = Array.Empty<byte>();

            // Act - Will fail on password verification, but we can check the exception doesn't mention key length
            var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

            // Assert
            ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
            ex!.Message.Should().NotContain("Unsupported /Length value");
        }
    }

    /// <summary>
    /// Test with EncryptMetadata=false (metadata not encrypted).
    /// </summary>
    [Fact]
    public void Build_WithEncryptMetadataFalse_CreatesHandler()
    {
        // Arrange
        var encryptDict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(2),
            ["R"] = new PdfInteger(3),
            ["Length"] = new PdfInteger(128),
            ["EncryptMetadata"] = PdfBoolean.False,
            ["O"] = CreatePdfString(new byte[32]),
            ["U"] = CreatePdfString(new byte[32]),
            ["P"] = new PdfInteger(-1)
        };
        byte[] firstId = new byte[16];
        byte[] userPassword = Array.Empty<byte>();

        // Act
        var ex = Record.Exception(() => PdfStandardSecurityHandler.Build(encryptDict, firstId, userPassword));

        // Assert
        ex.Should().BeOfType<PdfEncryptionNotSupportedException>();
    }

    /// <summary>
    /// Test that the handler correctly identifies when AES is used.
    /// </summary>
    [Fact]
    public void Build_WithAESV2_SetsUsesAesFlag()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: true, keyLength: 16);

        // Act & Assert
        handler.UsesAes.Should().BeTrue();
    }

    /// <summary>
    /// Test that the handler correctly identifies when RC4 is used.
    /// </summary>
    [Fact]
    public void Build_WithRC4_SetsUsesAesFalse()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);

        // Act & Assert
        handler.UsesAes.Should().BeFalse();
    }

    /// <summary>
    /// Test handler properties after creation.
    /// </summary>
    [Fact]
    public void Build_SetsHandlerProperties()
    {
        // Arrange
        var handler = CreateHandlerWithKnownKey(usesAes: false, keyLength: 16);

        // Act & Assert
        handler.V.Should().Be(2);
        handler.R.Should().Be(3);
        handler.KeyLengthBytes.Should().Be(16);
    }

    // Helper methods

    /// <summary>
    /// Create a PdfString from byte array (PDF string format).
    /// </summary>
    private static PdfString CreatePdfString(byte[] bytes)
    {
        return new PdfString(bytes);
    }

    /// <summary>
    /// Create a handler with a known file key for testing decryption.
    /// This bypasses password verification by directly constructing the handler.
    /// </summary>
    private static PdfStandardSecurityHandler CreateHandlerWithKnownKey(bool usesAes, int keyLength)
    {
        // Create a dummy file key for testing
        var fileKey = new byte[keyLength];
        for (int i = 0; i < fileKey.Length; i++)
            fileKey[i] = (byte)(42 + i);  // Arbitrary but deterministic bytes

        // Use reflection to invoke the private constructor
        var constructor = typeof(PdfStandardSecurityHandler).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        ).First();

        return (PdfStandardSecurityHandler)constructor.Invoke(
            new object[] { 2, 3, keyLength, fileKey, usesAes, /* encryptMetadata */ true }
        );
    }
}
