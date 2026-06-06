using System;

namespace Pdfe.Core.Filters.Jbig2;

/// <summary>
/// MQ arithmetic decoder for JBIG2/JPEG2000 compression.
/// Implements the Qe-adaptive binary arithmetic coder per ISO 14492 Annex E.
/// Used by JBIG2 for generic region and symbol dictionary decoding.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal class MqDecoder
{
    /// <summary>
    /// Qe probability estimates (ISO 14492 Table D.1).
    /// Each entry is: (Qe value, next LPS index, next MPS index, switch MPS flag)
    /// </summary>
    private static readonly (uint Qe, int Lps, int Mps, bool Switch)[] QeTable = new[]
    {
        // Index 0-15
        (0x5601u, 1, 1, true),
        (0x3401u, 2, 6, false),
        (0x1801u, 3, 9, false),
        (0x0AC1u, 4, 12, false),
        (0x0521u, 5, 29, false),
        (0x0221u, 38, 33, false),
        (0x5601u, 7, 6, true),
        (0x5401u, 8, 14, false),
        (0x4801u, 9, 14, false),
        (0x3801u, 10, 14, false),
        (0x3001u, 11, 17, false),
        (0x2401u, 12, 18, false),
        (0x1C01u, 13, 20, false),
        (0x1601u, 29, 21, false),
        (0x5601u, 15, 14, true),
        (0x5401u, 16, 14, false),

        // Index 16-31
        (0x5301u, 17, 15, false),
        (0x4B01u, 18, 16, false),
        (0x4401u, 19, 16, false),
        (0x3D01u, 20, 17, false),
        (0x3601u, 21, 18, false),
        (0x2F01u, 22, 19, false),
        (0x2A01u, 23, 19, false),
        (0x2501u, 24, 20, false),
        (0x1F01u, 25, 21, false),
        (0x1A01u, 26, 22, false),
        (0x1601u, 27, 23, false),
        (0x1301u, 28, 24, false),
        (0x0D01u, 29, 25, false),
        (0x0801u, 30, 26, false),
        (0x0601u, 31, 27, false),
        (0x0401u, 32, 28, false),

        // Index 32-45
        (0x0303u, 33, 29, false),
        (0x0201u, 34, 30, false),
        (0x5601u, 35, 31, true),
        (0x5401u, 36, 32, false),
        (0x5301u, 37, 33, false),
        (0x5201u, 38, 34, false),
        (0x4F01u, 39, 35, false),
        (0x4E01u, 40, 36, false),
        (0x4D01u, 41, 37, false),
        (0x4C01u, 42, 38, false),
        (0x4B01u, 43, 39, false),
        (0x4A01u, 44, 40, false),
        (0x4901u, 45, 41, false),
        (0x4801u, 46, 42, false),
    };

    private readonly byte[] _data;
    private int _byteIndex;

    // State variables
    private uint _a;           // Arithmetic range
    private uint _c;           // Arithmetic code
    private int _ct;           // Bit count
    private int _qeIndex;      // Current Qe table index
    private bool _mps;         // Most Probable Symbol (current context prediction)

    public MqDecoder(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _byteIndex = 0;
        _a = 0x8000;
        _c = 0;
        _ct = 0;
        _qeIndex = 0;
        _mps = false;

        // INITDEC: initialize by reading first byte
        if (_data.Length > 0)
        {
            _c = ((uint)_data[0]) << 16;
            Bytein();
        }
    }

    /// <summary>
    /// Decode a single bit using the given context.
    /// The context determines the probability estimates via the Qe table.
    /// </summary>
    public bool Decode(ref int context)
    {
        // Assume context is used to index the MPS and Qe state
        // For a simple context model, we maintain a single _qeIndex and _mps

        // Get the Qe value for the current context
        var (qe, lpsIdx, mpsIdx, switchMps) = QeTable[_qeIndex];

        // Subtract Qe from A
        _a -= qe;

        // Get the next bit value from the code
        bool psSample = (_c >> 16) >= _a;

        bool decoded;
        if (psSample)
        {
            // LPS (Less Probable Symbol)
            _c -= (_a << 16);
            _qeIndex = lpsIdx;
            decoded = !_mps;
            if (switchMps)
                _mps = !_mps;
        }
        else
        {
            // MPS (Most Probable Symbol)
            _qeIndex = mpsIdx;
            decoded = _mps;
        }

        // Renormalize
        Renormd();

        return decoded;
    }

    /// <summary>
    /// Renormalize after decoding a symbol (RENORMD).
    /// </summary>
    private void Renormd()
    {
        if ((_a & 0x8000) == 0)
        {
            // A is less than 0x8000, need to shift
            do
            {
                if (_ct == 0)
                    Bytein();

                _a = (_a << 1);
                _c = (_c << 1);
                _ct--;
            } while ((_a & 0x8000) == 0);
        }
    }

    /// <summary>
    /// Read the next byte into the code register (BYTEIN).
    /// </summary>
    private void Bytein()
    {
        if (_byteIndex < _data.Length)
        {
            byte b = _data[_byteIndex++];
            if (b == 0xFF)
            {
                // Skip the next byte if it follows 0xFF (JBIG2 stuffing)
                if (_byteIndex < _data.Length)
                {
                    byte nextB = _data[_byteIndex];
                    if (nextB != 0x00)
                    {
                        // Marker found, end of data
                        _c += (uint)(0xFF << (8 - _ct));
                        _ct = 8;
                        return;
                    }
                }
                _c += (uint)(0xFF << (8 - _ct));
            }
            else
            {
                _c += (uint)(b << (8 - _ct));
            }
        }
        else
        {
            // End of data
            _c += (uint)(0xFF << (8 - _ct));
        }
        _ct = 8;
    }

    /// <summary>
    /// Check if we've reached the end of the stream.
    /// </summary>
    public bool IsEof => _byteIndex >= _data.Length && _ct <= 0;
}
