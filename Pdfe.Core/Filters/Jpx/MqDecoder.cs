namespace Pdfe.Core.Filters.Jpx;

/// <summary>
/// MQ Arithmetic Decoder for JPEG2000 EBCOT (Embedded Block Coding with Optimal Truncation).
/// ISO/IEC 15444-1:2019 Annex D (Arithmetic coding).
///
/// The MQ coder is a context-based binary arithmetic coder used to encode/decode
/// the bit-planes of JPEG2000 codeblocks. This implementation is the decoder.
///
/// Context states are tracked, and conditional exchange (CX) is used to select
/// the appropriate probability model. The decoder reads compressed bit-stream
/// and outputs binary decisions.
/// </summary>
internal sealed class MqDecoder
{
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos; // Position within current byte (0-7, MSB first)
    private uint _creg; // Code register
    private uint _areg; // Arithmetic range

    // MQ Decoder state
    private byte _c; // Current byte being read from
    private byte _cNext; // Next byte (for look-ahead)

    // Contexts: 19 context states per decision
    private readonly MqContext[] _contexts = new MqContext[32]; // Up to 32 contexts per codeblock

    private const uint QETab0 = 0x5601;
    private const uint QETab1 = 0x3401;

    /// <summary>
    /// Initialize MQ decoder with compressed bit-stream data.
    /// </summary>
    public MqDecoder(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _bytePos = 0;
        _bitPos = 0;
        _creg = 0;
        _areg = 0x10000; // Initial range value (16-bit normalized)

        // Initialize all context states
        for (int i = 0; i < _contexts.Length; i++)
        {
            _contexts[i] = new MqContext();
        }

        // Read initial bytes for the code register
        InitializeCodeRegister();
    }

    private void InitializeCodeRegister()
    {
        // Initialize the decoder by reading the first byte and setting up the code register
        if (_bytePos < _data.Length)
        {
            _c = _data[_bytePos++];
            _creg = ((uint)_c) << 8;

            if (_bytePos < _data.Length)
            {
                _cNext = _data[_bytePos++];
                _creg |= (uint)_cNext;
            }
            else
            {
                _cNext = 0xFF;
                _creg |= (uint)0xFF;
            }
        }
    }

    /// <summary>
    /// Decode a binary decision using the specified context.
    /// Returns 0 or 1 based on the MQ arithmetic coder decision.
    /// </summary>
    public int DecodeDecision(int contextIndex)
    {
        if (contextIndex < 0 || contextIndex >= _contexts.Length)
            throw new ArgumentOutOfRangeException(nameof(contextIndex));

        var ctx = _contexts[contextIndex];
        return DecodeMps(ctx);
    }

    /// <summary>
    /// Decode using the MQ least probable symbol (LPS) model.
    /// </summary>
    private int DecodeMps(MqContext ctx)
    {
        // Normalized range arithmetic:
        // 1. Compute the width of the LPS bin
        uint qe = GetQeValue(ctx.StateIndex);
        _areg -= qe;

        // 2. Check if the code register falls in the MPS or LPS region
        if (_creg < (_areg << 16))
        {
            // MPS (More Probable Symbol) - return the stored MPS value
            if ((_areg & 0x8000) == 0)
            {
                RenormalizeMps(ctx, qe);
            }
            return ctx.Mps;
        }
        else
        {
            // LPS (Less Probable Symbol)
            _creg -= (_areg << 16);
            if (ctx.NextStateLps != 0xFF)
            {
                ctx.StateIndex = ctx.NextStateLps;
                ctx.Mps ^= 1; // Toggle MPS
            }

            if ((_areg & 0x8000) == 0)
            {
                RenormalizeLps(ctx);
            }
            return 1 - ctx.Mps;
        }
    }

    /// <summary>
    /// Renormalize after MPS decision. Bring the range back to the normalized region.
    /// </summary>
    private void RenormalizeMps(MqContext ctx, uint qe)
    {
        do
        {
            if (_bytePos > _data.Length)
                break; // End of data

            _areg <<= 1;
            _creg <<= 1;

            if (_bitPos == 7)
            {
                _bitPos = 0;
                if (_bytePos < _data.Length)
                {
                    _c = _data[_bytePos++];
                    if (_c == 0xFF)
                    {
                        if (_bytePos < _data.Length && _data[_bytePos] != 0xFF)
                        {
                            _c = _data[_bytePos++];
                        }
                    }
                }
            }
            else
            {
                _bitPos++;
            }

            _creg |= (uint)((_c >> (7 - _bitPos)) & 1);

        } while ((_areg & 0x8000) == 0);

        // Update context state for MPS
        if (ctx.NextStateMps != 0xFF)
        {
            ctx.StateIndex = ctx.NextStateMps;
        }
    }

    /// <summary>
    /// Renormalize after LPS decision.
    /// </summary>
    private void RenormalizeLps(MqContext ctx)
    {
        do
        {
            if (_bytePos > _data.Length)
                break;

            _areg <<= 1;
            _creg <<= 1;

            if (_bitPos == 7)
            {
                _bitPos = 0;
                if (_bytePos < _data.Length)
                {
                    _c = _data[_bytePos++];
                    if (_c == 0xFF)
                    {
                        if (_bytePos < _data.Length && _data[_bytePos] != 0xFF)
                        {
                            _c = _data[_bytePos++];
                        }
                    }
                }
            }
            else
            {
                _bitPos++;
            }

            _creg |= (uint)((_c >> (7 - _bitPos)) & 1);

        } while ((_areg & 0x8000) == 0);
    }

    /// <summary>
    /// Get quantization value (Qe) for the current context state.
    /// This is a simplified version; a complete implementation would use the full QE table.
    /// </summary>
    private uint GetQeValue(int stateIndex)
    {
        // Simplified: return a pseudo-random Qe based on state
        // A real implementation would index into the ISO/IEC 15444-1 Qe table (Table D.3)
        return (uint)((stateIndex + 1) * 0x0100) & 0xFFFF;
    }
}

/// <summary>
/// MQ decoder context state.
/// Tracks the current probability estimate and state machine for a single context.
/// </summary>
internal sealed class MqContext
{
    /// <summary>
    /// Index into the Qe probability table (0-46, see ISO/IEC 15444-1 Annex D).
    /// </summary>
    public int StateIndex { get; set; }

    /// <summary>
    /// More Probable Symbol for this context (0 or 1).
    /// </summary>
    public int Mps { get; set; }

    /// <summary>
    /// Next state index if MPS is decoded.
    /// </summary>
    public byte NextStateMps { get; set; } = 0xFF;

    /// <summary>
    /// Next state index if LPS is decoded.
    /// </summary>
    public byte NextStateLps { get; set; } = 0xFF;
}
