using System.Globalization;
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
        _currentPath.AddRect(new SKRect(
            ClampPathCoordinate(x),
            ClampPathCoordinate(y),
            ClampPathCoordinate(x + w),
            ClampPathCoordinate(y + h)));
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
    /// state. The array tokenizes as separate "[" numbers… "]" operands followed
    /// by the phase. An empty array (or all-zero intervals) means a solid line.
    /// ISO 32000-1 §8.4.3.6.
    /// </summary>
    private void SetDashPattern(List<string> operands)
    {
        var intervals = new List<float>();
        int closeIdx = -1;
        bool inArray = false;
        for (int i = 0; i < operands.Count; i++)
        {
            var t = operands[i];
            if (t == "[") { inArray = true; continue; }
            if (t == "]") { inArray = false; closeIdx = i; continue; }
            if (inArray &&
                float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
                intervals.Add(v);
        }

        // The phase is the operand immediately after the closing ']'.
        float phase = (closeIdx >= 0 && closeIdx + 1 < operands.Count)
            ? (float)ParseNumber(operands[closeIdx + 1])
            : 0f;

        if (intervals.Count == 0 || intervals.All(v => v <= 0))
        {
            _state.DashArray = null;   // solid line
            _state.DashPhase = 0f;
        }
        else
        {
            _state.DashArray = intervals.ToArray();
            _state.DashPhase = phase;
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

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = _state.StrokeColor.WithAlpha((byte)(_state.StrokeAlpha * 255)),
            StrokeWidth = (float)_state.LineWidth,
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

        _canvas.DrawPath(_currentPath, paint);
        _currentPath.Dispose();
        _currentPath = null;
    }

    private void FillPath(bool evenOdd)
    {
        if (_currentPath == null) return;

        _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = _state.FillColor.WithAlpha((byte)(_state.FillAlpha * 255)),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        _canvas.DrawPath(_currentPath, paint);
        _currentPath.Dispose();
        _currentPath = null;
    }

    private void FillAndStroke(bool evenOdd)
    {
        if (_currentPath == null) return;

        _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        // Fill first
        using (var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = _state.FillColor.WithAlpha((byte)(_state.FillAlpha * 255)),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        })
        {
            _canvas.DrawPath(_currentPath, fillPaint);
        }

        // Then stroke
        using (var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = _state.StrokeColor.WithAlpha((byte)(_state.StrokeAlpha * 255)),
            StrokeWidth = (float)_state.LineWidth,
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
            _canvas.DrawPath(_currentPath, strokePaint);
        }

        _currentPath.Dispose();
        _currentPath = null;
    }

    #endregion
}
