using AwesomeAssertions;
using Excise.Core.Security;
using Xunit;

namespace Excise.Core.Tests.Security;

/// <summary>
/// Tests for RC4 stream cipher implementation.
/// Covers key-scheduling algorithm, pseudo-random generation, and symmetry.
/// </summary>
public class Rc4Tests
{
    /// <summary>
    /// RFC 2268 test vector: key="Key", plaintext="Plaintext" → expected ciphertext.
    /// This is a known-answer test to verify correct RC4 implementation.
    /// </summary>
    [Fact]
    public void Transform_WithRfc2268TestVector_ProducesExpectedCiphertext()
    {
        // Arrange - RFC 2268 test vector
        byte[] key = System.Text.Encoding.ASCII.GetBytes("Key");
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Plaintext");
        // Expected ciphertext from RFC 2268
        byte[] expected = new byte[] { 0xBB, 0xF3, 0x16, 0xE8, 0xD9, 0x40, 0xAF, 0x0A, 0xD3 };

        // Act
        byte[] ciphertext = Rc4.Transform(key, plaintext);

        // Assert
        ciphertext.Should().Equal(expected);
    }

    /// <summary>
    /// RC4 is symmetric: encrypting plaintext gives ciphertext,
    /// and encrypting ciphertext again gives back plaintext.
    /// </summary>
    [Fact]
    public void Transform_IsSymmetric_EncryptThenDecryptRecoverOriginal()
    {
        // Arrange
        byte[] key = System.Text.Encoding.ASCII.GetBytes("MySecretKey");
        byte[] originalPlaintext = System.Text.Encoding.UTF8.GetBytes("This is a secret message for PDF encryption.");

        // Act - Encrypt
        byte[] ciphertext = Rc4.Transform(key, originalPlaintext);
        // Decrypt (same operation with same key)
        byte[] decrypted = Rc4.Transform(key, ciphertext);

        // Assert
        decrypted.Should().Equal(originalPlaintext);
    }

    /// <summary>
    /// Transform on empty data should return empty array (no-op).
    /// </summary>
    [Fact]
    public void Transform_WithEmptyData_ReturnsEmptyArray()
    {
        // Arrange
        byte[] key = System.Text.Encoding.ASCII.GetBytes("AnyKey");
        byte[] emptyData = Array.Empty<byte>();

        // Act
        byte[] result = Rc4.Transform(key, emptyData);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// Transform on single byte should encrypt/decrypt correctly.
    /// </summary>
    [Fact]
    public void Transform_WithSingleByte_EncryptsAndDecrypts()
    {
        // Arrange
        byte[] key = System.Text.Encoding.ASCII.GetBytes("Key");
        byte[] singleByte = new byte[] { 0x41 }; // 'A'

        // Act - Encrypt
        byte[] encrypted = Rc4.Transform(key, singleByte);
        // Decrypt
        byte[] decrypted = Rc4.Transform(key, encrypted);

        // Assert
        encrypted.Should().HaveCount(1);
        encrypted[0].Should().NotBe(0x41, "single byte should be encrypted");
        decrypted.Should().Equal(singleByte);
    }

    /// <summary>
    /// Different keys should produce different ciphertexts for the same plaintext.
    /// </summary>
    [Fact]
    public void Transform_WithDifferentKeys_ProducesDifferentCiphertexts()
    {
        // Arrange
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Message");
        byte[] key1 = System.Text.Encoding.ASCII.GetBytes("Key1");
        byte[] key2 = System.Text.Encoding.ASCII.GetBytes("Key2");

        // Act
        byte[] ciphertext1 = Rc4.Transform(key1, plaintext);
        byte[] ciphertext2 = Rc4.Transform(key2, plaintext);

        // Assert
        ciphertext1.Should().NotEqual(ciphertext2);
    }

    /// <summary>
    /// Same plaintext + key should always produce the same ciphertext.
    /// RC4 is deterministic (stateless between calls).
    /// </summary>
    [Fact]
    public void Transform_WithSamePlaintextAndKey_ProducesSameCiphertext()
    {
        // Arrange
        byte[] key = System.Text.Encoding.ASCII.GetBytes("FixedKey");
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("FixedPlaintext");

        // Act
        byte[] ciphertext1 = Rc4.Transform(key, plaintext);
        byte[] ciphertext2 = Rc4.Transform(key, plaintext);

        // Assert
        ciphertext1.Should().Equal(ciphertext2);
    }

    /// <summary>
    /// Test with a long key (>256 bytes). The KSA should handle key longer than the state array.
    /// </summary>
    [Fact]
    public void Transform_WithLongKey_EncryptsAndDecrypts()
    {
        // Arrange
        byte[] longKey = new byte[300];
        for (int i = 0; i < longKey.Length; i++) longKey[i] = (byte)(i % 256);
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Test data with long key");

        // Act - Encrypt
        byte[] ciphertext = Rc4.Transform(longKey, plaintext);
        // Decrypt
        byte[] decrypted = Rc4.Transform(longKey, ciphertext);

        // Assert
        decrypted.Should().Equal(plaintext);
    }

    /// <summary>
    /// Test with binary data (not just ASCII text).
    /// </summary>
    [Fact]
    public void Transform_WithBinaryData_EncryptsAndDecrypts()
    {
        // Arrange
        byte[] key = new byte[] { 0xFF, 0x00, 0xAA, 0x55 };
        byte[] binaryData = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

        // Act - Encrypt
        byte[] ciphertext = Rc4.Transform(key, binaryData);
        // Decrypt
        byte[] decrypted = Rc4.Transform(key, ciphertext);

        // Assert
        decrypted.Should().Equal(binaryData);
    }

    /// <summary>
    /// PDF Encrypt dictionary example: use a derived key (common in PDF).
    /// </summary>
    [Fact]
    public void Transform_WithPdfDerivedKey_EncryptsAndDecrypts()
    {
        // Arrange - Simulated derived encryption key (typically 5-16 bytes for RC4)
        byte[] fileKey = new byte[] { 0x28, 0x48, 0xF8, 0x7C, 0x2A, 0x60, 0xD4, 0x8E, 0x1F, 0x5F, 0x3A, 0x9B, 0x4D, 0x85, 0x2C, 0x91 };
        byte[] pdfString = System.Text.Encoding.ASCII.GetBytes("(Redacted)");

        // Act - Encrypt
        byte[] encrypted = Rc4.Transform(fileKey, pdfString);
        // Decrypt with same key
        byte[] decrypted = Rc4.Transform(fileKey, encrypted);

        // Assert
        decrypted.Should().Equal(pdfString);
        encrypted.Should().NotEqual(pdfString);
    }

    /// <summary>
    /// Verify that the output length matches the input length.
    /// </summary>
    [Fact]
    public void Transform_OutputLengthMatchesInputLength()
    {
        // Arrange
        byte[] key = System.Text.Encoding.ASCII.GetBytes("Key");
        int[] testLengths = new[] { 0, 1, 15, 16, 256, 1000 };

        foreach (int length in testLengths)
        {
            // Arrange
            byte[] plaintext = new byte[length];
            for (int i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i % 256);

            // Act
            byte[] ciphertext = Rc4.Transform(key, plaintext);

            // Assert
            ciphertext.Should().HaveCount(length, $"output for length {length}");
        }
    }

    /// <summary>
    /// Test with minimal 1-byte key.
    /// </summary>
    [Fact]
    public void Transform_WithMinimalOneByteKey_Works()
    {
        // Arrange
        byte[] minimalKey = new byte[] { 0x42 };
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Test");

        // Act - Encrypt
        byte[] ciphertext = Rc4.Transform(minimalKey, plaintext);
        // Decrypt
        byte[] decrypted = Rc4.Transform(minimalKey, ciphertext);

        // Assert
        decrypted.Should().Equal(plaintext);
    }

    /// <summary>
    /// Test plaintext preservation when key has all bytes 0x00.
    /// This is an edge case but valid for RC4.
    /// </summary>
    [Fact]
    public void Transform_WithZeroKey_EncryptsAndDecrypts()
    {
        // Arrange
        byte[] zeroKey = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Plaintext");

        // Act - Encrypt
        byte[] ciphertext = Rc4.Transform(zeroKey, plaintext);
        // Decrypt
        byte[] decrypted = Rc4.Transform(zeroKey, ciphertext);

        // Assert
        decrypted.Should().Equal(plaintext);
    }

    /// <summary>
    /// Changing a single bit in the plaintext should change the ciphertext.
    /// </summary>
    [Fact]
    public void Transform_BitFlipInPlaintext_ChangesCiphertext()
    {
        // Arrange
        byte[] key = System.Text.Encoding.ASCII.GetBytes("Key");
        byte[] plaintext1 = System.Text.Encoding.ASCII.GetBytes("Test");
        byte[] plaintext2 = System.Text.Encoding.ASCII.GetBytes("Test");
        plaintext2[0] ^= 0x01; // Flip first bit of first byte

        // Act
        byte[] ciphertext1 = Rc4.Transform(key, plaintext1);
        byte[] ciphertext2 = Rc4.Transform(key, plaintext2);

        // Assert
        ciphertext1.Should().NotEqual(ciphertext2);
    }

    /// <summary>
    /// Changing a single bit in the key should change the ciphertext.
    /// </summary>
    [Fact]
    public void Transform_BitFlipInKey_ChangesCiphertext()
    {
        // Arrange
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Test");
        byte[] key1 = System.Text.Encoding.ASCII.GetBytes("Key");
        byte[] key2 = System.Text.Encoding.ASCII.GetBytes("Key");
        key2[0] ^= 0x01; // Flip first bit of first byte

        // Act
        byte[] ciphertext1 = Rc4.Transform(key1, plaintext);
        byte[] ciphertext2 = Rc4.Transform(key2, plaintext);

        // Assert
        ciphertext1.Should().NotEqual(ciphertext2);
    }
}
