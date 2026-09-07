namespace Excise.Rendering.Transparency;

internal readonly record struct DeviceCmykColor(double C, double M, double Y, double K);

internal sealed class DeviceCmykBackdrop
{
    private readonly byte[] _cyan;
    private readonly byte[] _magenta;
    private readonly byte[] _yellow;
    private readonly byte[] _black;
    private readonly byte[] _alpha;

    public DeviceCmykBackdrop(int width, int height)
    {
        Width = width;
        Height = height;
        var count = checked(width * height);
        _cyan = new byte[count];
        _magenta = new byte[count];
        _yellow = new byte[count];
        _black = new byte[count];
        _alpha = new byte[count];
    }

    public int Width { get; }
    public int Height { get; }

    public DeviceCmykColor Get(int x, int y)
    {
        var i = Index(x, y);
        return new DeviceCmykColor(
            _cyan[i] / 255.0,
            _magenta[i] / 255.0,
            _yellow[i] / 255.0,
            _black[i] / 255.0);
    }

    public double GetAlpha(int x, int y) => _alpha[Index(x, y)] / 255.0;

    public void CompositeSourceOver(int x, int y, DeviceCmykColor source, double alpha)
    {
        var i = Index(x, y);
        var sourceAlpha = Math.Clamp(alpha, 0, 1);
        var backdropAlpha = _alpha[i] / 255.0;
        var outputAlpha = sourceAlpha + (backdropAlpha * (1 - sourceAlpha));
        if (outputAlpha <= 1e-9)
        {
            _cyan[i] = 0;
            _magenta[i] = 0;
            _yellow[i] = 0;
            _black[i] = 0;
            _alpha[i] = 0;
            return;
        }

        _cyan[i] = BlendByte(_cyan[i], source.C, sourceAlpha, backdropAlpha, outputAlpha);
        _magenta[i] = BlendByte(_magenta[i], source.M, sourceAlpha, backdropAlpha, outputAlpha);
        _yellow[i] = BlendByte(_yellow[i], source.Y, sourceAlpha, backdropAlpha, outputAlpha);
        _black[i] = BlendByte(_black[i], source.K, sourceAlpha, backdropAlpha, outputAlpha);
        _alpha[i] = ToByte(outputAlpha);
    }

    public void Set(int x, int y, DeviceCmykColor color, double alpha = 1)
    {
        var i = Index(x, y);
        _cyan[i] = ToByte(color.C);
        _magenta[i] = ToByte(color.M);
        _yellow[i] = ToByte(color.Y);
        _black[i] = ToByte(color.K);
        _alpha[i] = ToByte(alpha);
    }

    public DeviceCmykBackdrop Clone()
    {
        var clone = new DeviceCmykBackdrop(Width, Height);
        Array.Copy(_cyan, clone._cyan, _cyan.Length);
        Array.Copy(_magenta, clone._magenta, _magenta.Length);
        Array.Copy(_yellow, clone._yellow, _yellow.Length);
        Array.Copy(_black, clone._black, _black.Length);
        Array.Copy(_alpha, clone._alpha, _alpha.Length);
        return clone;
    }

    /// <summary>
    /// Copies a <c>width</c>x<c>height</c> region of <paramref name="source"/>,
    /// anchored at (<paramref name="srcLeft"/>, <paramref name="srcTop"/>), into
    /// this backdrop's origin. Row-wise <see cref="Array.Copy(Array,int,Array,int,int)"/>
    /// over the raw byte planes — byte-identical to looping
    /// <c>Set(x, y, source.Get(srcLeft + x, srcTop + y), source.GetAlpha(srcLeft + x, srcTop + y))</c>,
    /// because <see cref="Get"/>'s <c>byte / 255.0</c> and <see cref="Set"/>'s
    /// <c>round(clamp(value) * 255)</c> round-trip every byte 0..255 back to
    /// itself (verified exhaustively by
    /// <c>DeviceCmykBackdropCopyRegionTests.CopyRegionFrom_IsByteIdenticalToGetSetRoundTrip</c>).
    /// Performance-only replacement for #1402's O(width*height) per-pixel
    /// double/byte conversion loop — this is the "SeedBackdrop" hot path used
    /// to enter every non-isolated DeviceCMYK transparency group.
    /// </summary>
    public void CopyRegionFrom(DeviceCmykBackdrop source, int srcLeft, int srcTop, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var srcOffset = source.Index(srcLeft, srcTop + y);
            var dstOffset = Index(0, y);
            Array.Copy(source._cyan, srcOffset, _cyan, dstOffset, width);
            Array.Copy(source._magenta, srcOffset, _magenta, dstOffset, width);
            Array.Copy(source._yellow, srcOffset, _yellow, dstOffset, width);
            Array.Copy(source._black, srcOffset, _black, dstOffset, width);
            Array.Copy(source._alpha, srcOffset, _alpha, dstOffset, width);
        }
    }

    private int Index(int x, int y) => y * Width + x;

    private static byte BlendByte(
        byte backdrop,
        double source,
        double sourceAlpha,
        double backdropAlpha,
        double outputAlpha)
    {
        var value = ((backdrop / 255.0 * backdropAlpha * (1 - sourceAlpha)) +
                     (Math.Clamp(source, 0, 1) * sourceAlpha)) /
                    outputAlpha;
        return (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0, 1) * 255), 0, 255);
}
