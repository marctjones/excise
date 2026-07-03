using Pdfe.Core.Primitives;
using SkiaSharp;

namespace Pdfe.Rendering;

internal partial class RenderContext
{
    #region Path Construction

    // Path coordinates can be large in producer coordinate systems that
    // immediately scale down through the CTM. Keep a finite guard for hostile
    // ±FLT_MAX conformance probes, but do not use the stricter matrix-component
    // limit here or valid scaled content collapses into a corner.
    private void MoveTo(double x, double y)
    {
        _currentPath ??= new SKPath();
        _currentPath.MoveTo(ClampPathCoordinate(x), ClampPathCoordinate(y));
    }

    private void LineTo(double x, double y)
    {
        _currentPath ??= new SKPath();
        _currentPath.LineTo(ClampPathCoordinate(x), ClampPathCoordinate(y));
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _currentPath ??= new SKPath();
        _currentPath.CubicTo(
            ClampPathCoordinate(x1), ClampPathCoordinate(y1),
            ClampPathCoordinate(x2), ClampPathCoordinate(y2),
            ClampPathCoordinate(x3), ClampPathCoordinate(y3));
    }

    private void CurveToV(double x2, double y2, double x3, double y3)
    {
        // v operator: current point replicated as first control point
        if (_currentPath == null) return;
        var last = _currentPath.LastPoint;
        _currentPath.CubicTo(
            last.X,
            last.Y,
            ClampPathCoordinate(x2),
            ClampPathCoordinate(y2),
            ClampPathCoordinate(x3),
            ClampPathCoordinate(y3));
    }

    private void CurveToY(double x1, double y1, double x3, double y3)
    {
        // y operator: endpoint replicated as second control point
        _currentPath ??= new SKPath();
        var endX = ClampPathCoordinate(x3);
        var endY = ClampPathCoordinate(y3);
        _currentPath.CubicTo(ClampPathCoordinate(x1), ClampPathCoordinate(y1), endX, endY, endX, endY);
    }

    private void ClosePath()
    {
        _currentPath?.Close();
    }

    private void Rectangle(double x, double y, double w, double h)
    {
        _currentPath ??= new SKPath();
        var x0 = ClampPathCoordinate(x);
        var y0 = ClampPathCoordinate(y);
        var x1 = ClampPathCoordinate(x + w);
        var y1 = ClampPathCoordinate(y + h);

        _currentPath.MoveTo(x0, y0);
        _currentPath.LineTo(x1, y0);
        _currentPath.LineTo(x1, y1);
        _currentPath.LineTo(x0, y1);
        _currentPath.Close();
    }

    private const float PathCoordinateMax = 10_000_000f;

    private static float ClampPathCoordinate(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0f;
        if (v > PathCoordinateMax) return PathCoordinateMax;
        if (v < -PathCoordinateMax) return -PathCoordinateMax;
        return (float)v;
    }

    #endregion

    #region Path Painting

    /// <summary>
    /// Parse a PDF dash specification (`[ dashArray ] dashPhase d`) into graphics
    /// state. An empty array (or all-zero intervals) means a solid line.
    /// ISO 32000-1 §8.4.3.6.
    /// </summary>
    private void SetDashPattern(PdfArray? dashArray, double phase)
    {
        var intervals = new List<float>();
        if (dashArray != null)
        {
            foreach (var item in dashArray)
            {
                if (item.TryGetNumber(out var number) && number >= 0)
                {
                    var v = (float)number;
                    intervals.Add(v);
                }
            }
        }

        if (intervals.Count == 0 || intervals.All(v => v <= 0))
        {
            _state.DashArray = null;   // solid line
            _state.DashPhase = 0f;
        }
        else
        {
            _state.DashArray = intervals.ToArray();
            _state.DashPhase = (float)phase;
        }
    }

    /// <summary>
    /// Build the Skia dash <see cref="SKPathEffect"/> for the current state, or
    /// null for a solid line. Skia requires an even number of positive intervals;
    /// PDF allows an odd-length array (it repeats), so we double it.
    /// </summary>
    private SKPathEffect? CreateDashEffect()
    {
        var arr = _state.DashArray;
        if (arr == null || arr.Length == 0) return null;

        var intervals = arr;
        if (intervals.Length % 2 != 0)
        {
            intervals = new float[arr.Length * 2];
            Array.Copy(arr, 0, intervals, 0, arr.Length);
            Array.Copy(arr, 0, intervals, arr.Length, arr.Length);
        }
        if (intervals.All(v => v <= 0)) return null;

        try { return SKPathEffect.CreateDash(intervals, _state.DashPhase); }
        catch { return null; }   // never let a degenerate dash array abort rendering
    }

    private void StrokePath()
    {
        if (_currentPath == null) return;

        ApplyPendingClipToCurrentPath();

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = _state.StrokeColor.WithAlpha((byte)(_state.StrokeAlpha * 255)),
            StrokeWidth = ResolvePathStrokeWidth(),
            StrokeCap = _state.LineCap switch
            {
                1 => SKStrokeCap.Round,
                2 => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            },
            StrokeJoin = _state.LineJoin switch
            {
                1 => SKStrokeJoin.Round,
                2 => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            },
            StrokeMiter = _state.MiterLimit,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        using var dash = CreateDashEffect();
        if (dash != null) paint.PathEffect = dash;

        if (_deviceCmykKnockoutGroupDepth <= 0 &&
            TryPaintDeviceCmykBlendPath(
                _currentPath,
                _state.StrokeDeviceCmyk,
                _state.StrokeAlpha,
                SKPaintStyle.Stroke,
                ResolvePathStrokeWidth(),
                _state.LineCap,
                _state.LineJoin,
                _state.MiterLimit,
                dash))
        {
            _currentPath.Dispose();
            _currentPath = null;
            return;
        }

        RenderWithCurrentSoftMask(
            () => _canvas.DrawPath(_currentPath, paint),
            paint);
        _currentPath.Dispose();
        _currentPath = null;
    }

    private void FillPath(bool evenOdd)
    {
        if (_currentPath == null) return;

        ApplyPendingClipToCurrentPath();
        _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        if (_state.FillPatternName != null)
        {
            RenderFillPattern(_currentPath);
            _currentPath.Dispose();
            _currentPath = null;
            return;
        }

        if (TryPaintDeviceCmykBlendPath(_currentPath, _state.FillDeviceCmyk, _state.FillAlpha))
        {
            _currentPath.Dispose();
            _currentPath = null;
            return;
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = _state.FillColor.WithAlpha((byte)(_state.FillAlpha * 255)),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        RenderWithCurrentSoftMask(
            () => _canvas.DrawPath(_currentPath, paint),
            paint);
        _currentPath.Dispose();
        _currentPath = null;
    }

    private void FillAndStroke(bool evenOdd)
    {
        if (_currentPath == null) return;

        ApplyPendingClipToCurrentPath();
        _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        // Fill first
        if (_state.FillPatternName == null || !RenderFillPattern(_currentPath))
        {
            if (_state.FillPatternName == null)
            {
                var filledWithDeviceCmykBlend = TryPaintDeviceCmykBlendPath(
                    _currentPath,
                    _state.FillDeviceCmyk,
                    _state.FillAlpha);
                if (!filledWithDeviceCmykBlend)
                {
                    using var fillPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = _state.FillColor.WithAlpha((byte)(_state.FillAlpha * 255)),
                        BlendMode = _state.BlendMode,
                        IsAntialias = _options.AntiAlias
                    };
                    RenderWithCurrentSoftMask(
                        () => _canvas.DrawPath(_currentPath, fillPaint),
                        fillPaint);
                }
            }
        }

        // Then stroke
        using (var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = _state.StrokeColor.WithAlpha((byte)(_state.StrokeAlpha * 255)),
            StrokeWidth = ResolvePathStrokeWidth(),
            StrokeCap = _state.LineCap switch
            {
                1 => SKStrokeCap.Round,
                2 => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            },
            StrokeJoin = _state.LineJoin switch
            {
                1 => SKStrokeJoin.Round,
                2 => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            },
            StrokeMiter = _state.MiterLimit,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        })
        using (var dash = CreateDashEffect())
        {
            if (dash != null) strokePaint.PathEffect = dash;
            RenderWithCurrentSoftMask(
                () => _canvas.DrawPath(_currentPath, strokePaint),
                strokePaint);
        }

        _currentPath.Dispose();
        _currentPath = null;
    }

    private bool TryPaintDeviceCmykBlendPath(SKPath path, DeviceCmykColor? sourceCmyk, float alpha)
        => TryPaintDeviceCmykBlendPath(
            path,
            sourceCmyk,
            alpha,
            SKPaintStyle.Fill,
            strokeWidth: 1,
            lineCap: 0,
            lineJoin: 0,
            miterLimit: 10,
            pathEffect: null);

    private bool TryPaintDeviceCmykBlendPath(
        SKPath path,
        DeviceCmykColor? sourceCmyk,
        float alpha,
        SKPaintStyle style,
        float strokeWidth,
        int lineCap,
        int lineJoin,
        float miterLimit,
        SKPathEffect? pathEffect)
    {
        if (_rootBitmap == null ||
            _deviceCmykBackdrop == null ||
            _deviceCmykTransparencyGroupDepth <= 0 ||
            sourceCmyk == null ||
            _state.SoftMask != null)
        {
            return false;
        }

        var isNormalBlend = _state.BlendMode == SKBlendMode.SrcOver;
        PdfSeparableBlendMode blend = default;
        if (!isNormalBlend && !TryMapSkiaBlendToPdfBlend(_state.BlendMode, out blend))
            return false;

        var matrix = _canvas.TotalMatrix;
        var bounds = matrix.MapRect(path.Bounds);
        var left = Math.Clamp((int)Math.Floor(bounds.Left) - 1, 0, _rootBitmap.Width);
        var top = Math.Clamp((int)Math.Floor(bounds.Top) - 1, 0, _rootBitmap.Height);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right) + 1, 0, _rootBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + 1, 0, _rootBitmap.Height);
        if (right <= left || bottom <= top)
            return true;

        using var mask = new SKBitmap(_rootBitmap.Width, _rootBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var maskCanvas = new SKCanvas(mask))
        using (var maskPaint = new SKPaint
        {
            Style = style,
            Color = SKColors.White,
            StrokeWidth = strokeWidth,
            StrokeCap = lineCap switch
            {
                1 => SKStrokeCap.Round,
                2 => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            },
            StrokeJoin = lineJoin switch
            {
                1 => SKStrokeJoin.Round,
                2 => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            },
            StrokeMiter = miterLimit,
            IsAntialias = _options.AntiAlias
        })
        {
            if (pathEffect != null)
                maskPaint.PathEffect = pathEffect;
            maskCanvas.Clear(SKColors.Transparent);
            maskCanvas.SetMatrix(matrix);
            maskCanvas.DrawPath(path, maskPaint);
        }

        _canvas.Flush();
        var source = sourceCmyk.Value;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var coverage = mask.GetPixel(x, y).Alpha / 255.0;
                if (coverage <= 0)
                    continue;

                var effectiveAlpha = Math.Clamp(alpha * coverage, 0, 1);
                if (effectiveAlpha <= 0)
                {
                    if (_deviceCmykKnockoutGroupDepth > 0)
                        ResetDeviceCmykKnockoutPixel(x, y, 0);
                    else if (_deviceCmykPreserveZeroAlphaShape)
                        PreserveDeviceCmykShapePixel(x, y, coverage);
                    continue;
                }

                var useDirectBlendFunctions =
                    // Isolated Ghent CMYK groups rely on direct component blending
                    // for these retained-backdrop blend modes, while knockout groups
                    // must use the subtractive DeviceCMYK path so neutral nested
                    // forms do not leave a visible X.
                    _deviceCmykIsolatedGroupDepth > 0 &&
                    !isNormalBlend &&
                    blend is PdfSeparableBlendMode.Lighten or
                        PdfSeparableBlendMode.Screen or
                        PdfSeparableBlendMode.ColorDodge;
                if (IsZeroInk(source) &&
                    _deviceCmykKnockoutGroupDepth <= 0 &&
                    !useDirectBlendFunctions &&
                    !isNormalBlend &&
                    blend is not PdfSeparableBlendMode.Difference and
                        not PdfSeparableBlendMode.Exclusion and
                        not PdfSeparableBlendMode.Hue and
                        not PdfSeparableBlendMode.Saturation and
                        not PdfSeparableBlendMode.Color and
                        not PdfSeparableBlendMode.Luminosity)
                {
                    continue;
                }

                var backdrop = _deviceCmykBackdrop.Get(x, y);
                if (IsFullBlackInk(source) &&
                    IsZeroInk(backdrop) &&
                    !useDirectBlendFunctions &&
                    !isNormalBlend &&
                    blend is PdfSeparableBlendMode.Overlay or PdfSeparableBlendMode.SoftLight)
                {
                    continue;
                }

                var dst = _rootBitmap.GetPixel(x, y);
                if (_deviceCmykKnockoutGroupDepth > 0)
                {
                    dst = ResetDeviceCmykKnockoutPixel(x, y, 0);
                    backdrop = _deviceCmykBackdrop.Get(x, y);
                }

                var blended = isNormalBlend
                    ? source
                    : useDirectBlendFunctions
                        ? BlendDeviceCmykDirect(backdrop, source, blend)
                        : BlendDeviceCmyk(backdrop, source, blend);
                _deviceCmykBackdrop.CompositeSourceOver(x, y, blended, effectiveAlpha);
                var output = _deviceCmykBackdrop.Get(x, y);
                var (r, g, b) = DeviceCmykToRgb(output);
                var dstAlpha = dst.Alpha / 255.0;
                var outAlpha = effectiveAlpha + (dstAlpha * (1 - effectiveAlpha));
                var outColor = new SKColor(
                    ToByte(r),
                    ToByte(g),
                    ToByte(b),
                    ToByte(outAlpha));
                _rootBitmap.SetPixel(x, y, outColor);
            }
        }

        return true;
    }

    private SKColor ResetDeviceCmykKnockoutPixel(int x, int y, byte alpha)
    {
        var initialBackdrop = _deviceCmykKnockoutInitialBackdrop?.Get(x, y)
                              ?? new DeviceCmykColor(0, 0, 0, 0);
        _deviceCmykBackdrop!.Set(x, y, initialBackdrop);
        var (initialR, initialG, initialB) = DeviceCmykToRgb(initialBackdrop);
        var dst = new SKColor(
            ToByte(initialR),
            ToByte(initialG),
            ToByte(initialB),
            alpha);
        _rootBitmap!.SetPixel(x, y, dst);
        return dst;
    }

    private void PreserveDeviceCmykShapePixel(int x, int y, double coverage)
    {
        var backdrop = _deviceCmykBackdrop!.Get(x, y);
        var (r, g, b) = DeviceCmykToRgb(backdrop);
        var dst = _rootBitmap!.GetPixel(x, y);
        var dstAlpha = dst.Alpha / 255.0;
        var outAlpha = Math.Clamp(coverage, 0, 1) + (dstAlpha * (1 - Math.Clamp(coverage, 0, 1)));
        _rootBitmap.SetPixel(x, y, new SKColor(
            ToByte(r),
            ToByte(g),
            ToByte(b),
            ToByte(outAlpha)));
    }

    private enum PdfSeparableBlendMode
    {
        Multiply,
        Screen,
        Overlay,
        Darken,
        Lighten,
        ColorDodge,
        ColorBurn,
        HardLight,
        SoftLight,
        Difference,
        Exclusion,
        Hue,
        Saturation,
        Color,
        Luminosity
    }

    private static bool TryMapSkiaBlendToPdfBlend(SKBlendMode mode, out PdfSeparableBlendMode blend)
    {
        switch (mode)
        {
            case SKBlendMode.Multiply: blend = PdfSeparableBlendMode.Multiply; return true;
            case SKBlendMode.Screen: blend = PdfSeparableBlendMode.Screen; return true;
            case SKBlendMode.Overlay: blend = PdfSeparableBlendMode.Overlay; return true;
            case SKBlendMode.Darken: blend = PdfSeparableBlendMode.Darken; return true;
            case SKBlendMode.Lighten: blend = PdfSeparableBlendMode.Lighten; return true;
            case SKBlendMode.ColorDodge: blend = PdfSeparableBlendMode.ColorDodge; return true;
            case SKBlendMode.ColorBurn: blend = PdfSeparableBlendMode.ColorBurn; return true;
            case SKBlendMode.HardLight: blend = PdfSeparableBlendMode.HardLight; return true;
            case SKBlendMode.SoftLight: blend = PdfSeparableBlendMode.SoftLight; return true;
            case SKBlendMode.Difference: blend = PdfSeparableBlendMode.Difference; return true;
            case SKBlendMode.Exclusion: blend = PdfSeparableBlendMode.Exclusion; return true;
            case SKBlendMode.Hue: blend = PdfSeparableBlendMode.Hue; return true;
            case SKBlendMode.Saturation: blend = PdfSeparableBlendMode.Saturation; return true;
            case SKBlendMode.Color: blend = PdfSeparableBlendMode.Color; return true;
            case SKBlendMode.Luminosity: blend = PdfSeparableBlendMode.Luminosity; return true;
            default:
                blend = default;
                return false;
        }
    }

    private static DeviceCmykColor BlendDeviceCmyk(
        DeviceCmykColor backdrop,
        DeviceCmykColor source,
        PdfSeparableBlendMode blend)
    {
        if (blend is PdfSeparableBlendMode.Hue or
            PdfSeparableBlendMode.Saturation or
            PdfSeparableBlendMode.Color or
            PdfSeparableBlendMode.Luminosity)
        {
            return BlendNonseparableDeviceCmykForScreen(backdrop, source, blend);
        }

        return new DeviceCmykColor(
            1 - BlendAdditiveComponent(1 - backdrop.C, 1 - source.C, blend),
            1 - BlendAdditiveComponent(1 - backdrop.M, 1 - source.M, blend),
            1 - BlendAdditiveComponent(1 - backdrop.Y, 1 - source.Y, blend),
            1 - BlendAdditiveComponent(1 - backdrop.K, 1 - source.K, blend));
    }

    private static DeviceCmykColor BlendDeviceCmykDirect(
        DeviceCmykColor backdrop,
        DeviceCmykColor source,
        PdfSeparableBlendMode blend)
    {
        if (blend is PdfSeparableBlendMode.Hue or
            PdfSeparableBlendMode.Saturation or
            PdfSeparableBlendMode.Color or
            PdfSeparableBlendMode.Luminosity)
        {
            return BlendNonseparableDeviceCmykForScreen(backdrop, source, blend);
        }

        return new DeviceCmykColor(
            BlendAdditiveComponent(backdrop.C, source.C, blend),
            BlendAdditiveComponent(backdrop.M, source.M, blend),
            BlendAdditiveComponent(backdrop.Y, source.Y, blend),
            BlendAdditiveComponent(backdrop.K, source.K, blend));
    }

    private static bool IsZeroInk(DeviceCmykColor color)
        => Math.Abs(color.C) < 1e-9 &&
           Math.Abs(color.M) < 1e-9 &&
           Math.Abs(color.Y) < 1e-9 &&
           Math.Abs(color.K) < 1e-9;

    private static bool IsFullBlackInk(DeviceCmykColor color)
        => Math.Abs(color.C) < 1e-9 &&
           Math.Abs(color.M) < 1e-9 &&
           Math.Abs(color.Y) < 1e-9 &&
           Math.Abs(color.K - 1) < 1e-9;

    private static double BlendAdditiveComponent(double b, double s, PdfSeparableBlendMode blend)
    {
        b = Math.Clamp(b, 0, 1);
        s = Math.Clamp(s, 0, 1);
        return blend switch
        {
            PdfSeparableBlendMode.Multiply => b * s,
            PdfSeparableBlendMode.Screen => b + s - (b * s),
            PdfSeparableBlendMode.Overlay => b <= 0.5 ? 2 * b * s : 1 - (2 * (1 - b) * (1 - s)),
            PdfSeparableBlendMode.Darken => Math.Min(b, s),
            PdfSeparableBlendMode.Lighten => Math.Max(b, s),
            PdfSeparableBlendMode.ColorDodge => s >= 1 ? 1 : b <= 0 ? 0 : Math.Min(1, b / (1 - s)),
            PdfSeparableBlendMode.ColorBurn => s <= 0 ? 0 : b >= 1 ? 1 : 1 - Math.Min(1, (1 - b) / s),
            PdfSeparableBlendMode.HardLight => s <= 0.5 ? 2 * b * s : 1 - (2 * (1 - b) * (1 - s)),
            PdfSeparableBlendMode.SoftLight => SoftLight(b, s),
            PdfSeparableBlendMode.Difference => Math.Abs(b - s),
            PdfSeparableBlendMode.Exclusion => b + s - (2 * b * s),
            _ => s
        };
    }

    private static DeviceCmykColor BlendNonseparableDeviceCmykForScreen(
        DeviceCmykColor backdrop,
        DeviceCmykColor source,
        PdfSeparableBlendMode blend)
    {
        var backdropRgb = Pdfe.Core.ColorSpaces.PdfColorSpace.ConvertDeviceCmykToRgb(
            backdrop.C,
            backdrop.M,
            backdrop.Y,
            backdrop.K);
        var sourceRgb = Pdfe.Core.ColorSpaces.PdfColorSpace.ConvertDeviceCmykToRgb(
            source.C,
            source.M,
            source.Y,
            source.K);
        var backdropColor = new RgbColor(backdropRgb.R, backdropRgb.G, backdropRgb.B);
        var sourceColor = new RgbColor(sourceRgb.R, sourceRgb.G, sourceRgb.B);
        var rgb = blend switch
        {
            PdfSeparableBlendMode.Hue => SetLum(SetSat(sourceColor, Sat(backdropColor)), Lum(backdropColor)),
            PdfSeparableBlendMode.Saturation => SetLum(SetSat(backdropColor, Sat(sourceColor)), Lum(backdropColor)),
            PdfSeparableBlendMode.Color => SetLum(sourceColor, Lum(backdropColor)),
            PdfSeparableBlendMode.Luminosity => SetLum(backdropColor, Lum(sourceColor)),
            _ => sourceColor
        };
        return RgbToDeviceCmyk(rgb.R, rgb.G, rgb.B);
    }

    private readonly record struct RgbColor(double R, double G, double B);

    private static double Lum(RgbColor color)
        => (0.3 * color.R) + (0.59 * color.G) + (0.11 * color.B);

    private static double Sat(RgbColor color)
        => Math.Max(color.R, Math.Max(color.G, color.B)) -
           Math.Min(color.R, Math.Min(color.G, color.B));

    private static RgbColor SetLum(RgbColor color, double targetLum)
    {
        var delta = targetLum - Lum(color);
        return ClipColor(new RgbColor(color.R + delta, color.G + delta, color.B + delta));
    }

    private static RgbColor ClipColor(RgbColor color)
    {
        var lum = Lum(color);
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var r = color.R;
        var g = color.G;
        var b = color.B;

        if (min < 0)
        {
            r = lum + (((r - lum) * lum) / (lum - min));
            g = lum + (((g - lum) * lum) / (lum - min));
            b = lum + (((b - lum) * lum) / (lum - min));
        }

        if (max > 1)
        {
            r = lum + (((r - lum) * (1 - lum)) / (max - lum));
            g = lum + (((g - lum) * (1 - lum)) / (max - lum));
            b = lum + (((b - lum) * (1 - lum)) / (max - lum));
        }

        return new RgbColor(
            Math.Clamp(r, 0, 1),
            Math.Clamp(g, 0, 1),
            Math.Clamp(b, 0, 1));
    }

    private static RgbColor SetSat(RgbColor color, double targetSat)
    {
        var channels = new[]
        {
            new ColorChannel(0, color.R),
            new ColorChannel(1, color.G),
            new ColorChannel(2, color.B)
        };
        Array.Sort(channels, static (a, b) => a.Value.CompareTo(b.Value));

        var min = channels[0];
        var mid = channels[1];
        var max = channels[2];
        if (max.Value > min.Value)
        {
            mid = mid with { Value = ((mid.Value - min.Value) * targetSat) / (max.Value - min.Value) };
            max = max with { Value = targetSat };
        }
        else
        {
            mid = mid with { Value = 0 };
            max = max with { Value = 0 };
        }
        min = min with { Value = 0 };

        var result = new double[3];
        result[min.Index] = min.Value;
        result[mid.Index] = mid.Value;
        result[max.Index] = max.Value;
        return new RgbColor(result[0], result[1], result[2]);
    }

    private readonly record struct ColorChannel(int Index, double Value);

    private static DeviceCmykColor RgbToDeviceCmyk(double r, double g, double b)
    {
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
        var k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1 - 1e-9)
            return new DeviceCmykColor(0, 0, 0, 1);

        var denominator = 1 - k;
        return new DeviceCmykColor(
            (1 - r - k) / denominator,
            (1 - g - k) / denominator,
            (1 - b - k) / denominator,
            k);
    }

    private static double SoftLight(double b, double s)
    {
        if (s <= 0.5)
            return b - ((1 - (2 * s)) * b * (1 - b));

        var d = b <= 0.25
            ? (((16 * b - 12) * b) + 4) * b
            : Math.Sqrt(b);
        return b + ((2 * s - 1) * (d - b));
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0, 1) * 255), 0, 255);

    private float ResolvePathStrokeWidth()
    {
        var width = (float)_state.LineWidth;
        if (width <= 0 || _tilingPatternDepth <= 0)
            return width;

        var matrix = _canvas.TotalMatrix;
        var scaleX = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var scaleY = Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);
        var deviceWidth = width * (float)Math.Max(scaleX, scaleY);
        return deviceWidth is > 0 and < 1 ? 0 : width;
    }

    #endregion
}
