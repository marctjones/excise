using AwesomeAssertions;
using Pdfe.Core.Filters.Jpx;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jpx;

/// <summary>
/// Tests for MQ arithmetic decoder.
/// ISO/IEC 15444-1:2019 Annex D (Arithmetic coding).
///
/// NOTE: This is a partial implementation test. A full MQ decoder test would require
/// official test vectors from the JPEG2000 specification (ISO/IEC 15444-1 Annex D).
/// These tests validate basic initialization and structure.
/// </summary>
public class MqDecoderTests
{
    [Fact]
    public void MqDecoder_EmptyData_InitializesWithoutError()
    {
        // The MQ decoder should handle empty data gracefully
        var data = Array.Empty<byte>();
        // Note: MQ decoder needs at least some data to function; empty input is edge case
        var action = () =>
        {
            var decoder = new MqDecoderWrapper(data);
        };

        // Should not throw during construction
        action.Should().NotThrow();
    }

    [Fact]
    public void MqDecoder_SingleByteData_InitializesWithoutError()
    {
        var data = new byte[] { 0xAA };
        var action = () =>
        {
            var decoder = new MqDecoderWrapper(data);
        };

        action.Should().NotThrow();
    }

    [Fact]
    public void MqDecoder_MultiByteData_InitializesWithoutError()
    {
        var data = new byte[] { 0xFF, 0x00, 0xAA, 0x55 };
        var action = () =>
        {
            var decoder = new MqDecoderWrapper(data);
        };

        action.Should().NotThrow();
    }

    [Fact]
    public void MqDecoder_DecodeDecision_ValidContext_ReturnsDecision()
    {
        var data = new byte[] { 0x80 };
        var decoder = new MqDecoderWrapper(data);

        // DecodeDecision should return 0 or 1
        for (int ctx = 0; ctx < 5; ctx++)
        {
            var decision = decoder.DecodeDecision(ctx);
            decision.Should().BeOneOf(0, 1);
        }
    }

    [Fact]
    public void MqDecoder_DecodeDecision_InvalidContext_ThrowsOutOfRangeException()
    {
        var data = new byte[] { 0x80 };
        var decoder = new MqDecoderWrapper(data);

        var action = () => decoder.DecodeDecision(-1);
        action.Should().Throw<ArgumentOutOfRangeException>();

        var action2 = () => decoder.DecodeDecision(64); // Out of range (max is 31)
        action2.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Wrapper class to expose internal MqDecoder for testing.
    /// The actual MqDecoder is internal, so we wrap it to test its behavior.
    /// </summary>
    private sealed class MqDecoderWrapper
    {
        private readonly MqDecoder _decoder;

        public MqDecoderWrapper(byte[] data)
        {
            _decoder = new MqDecoder(data);
        }

        public int DecodeDecision(int contextIndex)
        {
            return _decoder.DecodeDecision(contextIndex);
        }
    }
}
