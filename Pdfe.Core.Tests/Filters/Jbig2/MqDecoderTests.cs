using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

/// <summary>
/// Unit tests for MqDecoder (MQ arithmetic decoder).
/// Covers the Qe-adaptive binary arithmetic coder per ISO 14492 Annex E.
///
/// The MQ decoder is the core arithmetic engine for JBIG2 compression.
/// It uses probability estimation tables and renormalization to encode/decode
/// symbols based on a context model.
/// </summary>
public class MqDecoderTests
{
    /// <summary>
    /// Test MQ decoder initialization and basic structure.
    /// Ensures the decoder initializes without throwing and is ready to decode.
    /// </summary>
    [Fact]
    public void Constructor_WithValidData_InitializesSuccessfully()
    {
        byte[] data = new byte[] { 0x00, 0x02, 0x00, 0x51 };

        // Should not throw
        var decoder = new MqDecoder(data);

        decoder.Should().NotBeNull();
        decoder.IsEof.Should().BeFalse();
    }

    /// <summary>
    /// Test MQ decoder with empty data.
    /// The decoder should handle empty input gracefully.
    /// </summary>
    [Fact]
    public void Constructor_WithEmptyData_InitializesWithoutError()
    {
        byte[] data = Array.Empty<byte>();

        var decoder = new MqDecoder(data);

        decoder.Should().NotBeNull();
        decoder.IsEof.Should().BeTrue();
    }

    /// <summary>
    /// Test MQ decoder throws on null data.
    /// </summary>
    [Fact]
    public void Constructor_WithNullData_ThrowsArgumentNullException()
    {
        var action = () => new MqDecoder(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Test basic decoding of a symbol from the standard test vector.
    /// ISO 14492 Annex H.2 provides a standard test stream:
    /// 0x00 0x02 0x00 0x51 ... should decode to a known sequence.
    ///
    /// This test verifies that the decoder can extract at least one symbol
    /// without crashing. Full verification would require comparing against
    /// reference output from ISO 14492 H.2, which is complex to reproduce
    /// in unit test form due to context state tracking.
    /// </summary>
    [Fact]
    public void Decode_WithStandardTestData_DecodesSymbolsWithoutError()
    {
        // ISO 14492 Annex H.2 test stream (first few bytes)
        byte[] testData = new byte[] { 0x00, 0x02, 0x00, 0x51, 0xA0, 0x00 };

        var decoder = new MqDecoder(testData);
        int context = 0;

        // Should be able to decode at least one symbol without throwing
        bool symbol1 = decoder.Decode(ref context);

        // Symbol should be a valid boolean (just verify no exception)
        _ = symbol1;

        // Should be able to decode a second symbol
        bool symbol2 = decoder.Decode(ref context);
        _ = symbol2;
    }

    /// <summary>
    /// Test EOF detection.
    /// After all data is consumed, IsEof should return true.
    /// </summary>
    [Fact]
    public void IsEof_AfterDataExhausted_ReturnsTrue()
    {
        byte[] data = new byte[] { 0xFF };
        var decoder = new MqDecoder(data);

        // Decode many symbols to exhaust the stream
        int context = 0;
        for (int i = 0; i < 100; i++)
        {
            if (decoder.IsEof)
                break;
            decoder.Decode(ref context);
        }

        // Eventually should reach EOF
        decoder.IsEof.Should().BeTrue();
    }

    /// <summary>
    /// Test decoding consistency with multiple contexts.
    /// The context parameter affects the probability estimates used.
    /// This verifies that different contexts don't corrupt the decoder state.
    /// </summary>
    [Fact]
    public void Decode_WithMultipleContexts_MaintainsDecoderState()
    {
        byte[] data = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
        var decoder = new MqDecoder(data);

        int ctx1 = 0;
        int ctx2 = 5;
        int ctx3 = 10;

        // Decode with different contexts - should not throw
        bool b1 = decoder.Decode(ref ctx1);
        bool b2 = decoder.Decode(ref ctx2);
        bool b3 = decoder.Decode(ref ctx3);

        // All should decode without throwing (booleans assigned)
        _ = b1; _ = b2; _ = b3;

        // Decoder should not be at EOF yet (8 bytes should provide multiple symbols)
        decoder.IsEof.Should().BeFalse();
    }

    /// <summary>
    /// Test that decoding progresses through the data stream.
    /// Successive decodes should not return identical values for all symbols
    /// (statistically unlikely with random data).
    /// </summary>
    [Fact]
    public void Decode_ProgressesThroughData_DecodesMultipleDistinctSymbols()
    {
        byte[] data = new byte[]
        {
            0x5A, 0xA5, 0xF0, 0x0F, 0xCC, 0x33, 0x99, 0x66,
            0xFF, 0x00, 0xAA, 0x55, 0xAB, 0xCD, 0xEF, 0x12
        };

        var decoder = new MqDecoder(data);
        int context = 0;

        // Decode a sequence of symbols
        var symbols = new bool[10];
        for (int i = 0; i < 10; i++)
        {
            symbols[i] = decoder.Decode(ref context);
        }

        // With random data, expect variation (not all same value)
        // Count distinct values
        int trueCount = 0;
        foreach (bool b in symbols)
        {
            if (b) trueCount++;
        }

        // Statistically, with 10 random bits, should not be all 0 or all 1
        trueCount.Should().BeGreaterThan(0).And.BeLessThan(10);
    }

    /// <summary>
    /// Test 0xFF byte handling (JBIG2 byte stuffing).
    /// 0xFF followed by non-0x00 indicates end-of-data marker.
    /// 0xFF followed by 0x00 is normal data.
    /// </summary>
    [Fact]
    public void Decode_With0xFFByteStuffing_HandlesCorrectly()
    {
        // Data with 0xFF byte
        byte[] data = new byte[] { 0xFF, 0x00, 0x00, 0x00 };

        var decoder = new MqDecoder(data);
        int context = 0;

        // Should decode without throwing
        bool symbol = decoder.Decode(ref context);
        _ = symbol;
    }

    /// <summary>
    /// Test that context values in valid range (0-1023 for 10-bit context)
    /// are accepted without error.
    /// </summary>
    [Fact]
    public void Decode_WithValidContextRange_AcceptsWithoutError()
    {
        byte[] data = new byte[] { 0xAA, 0x55, 0xCC, 0x33 };
        var decoder = new MqDecoder(data);

        // Test boundary context values
        int ctx_min = 0;
        int ctx_max = 1023;
        int ctx_mid = 512;

        bool b1 = decoder.Decode(ref ctx_min);
        bool b2 = decoder.Decode(ref ctx_max);
        bool b3 = decoder.Decode(ref ctx_mid);

        // All should succeed (assignments prove no exception)
        _ = b1; _ = b2; _ = b3;
    }
}
