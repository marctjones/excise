using System.Globalization;
using SkiaSharp;

namespace Pdfe.Rendering;

internal partial class RenderContext
{
    #region Path Construction

    // Path coordinates are subject to the same PDF 32000-2 §6.1.12
    // implementation-limit as matrix components. Conformance fixtures
    // (A019-pdfa2-pass-*) put ±FLT_MAX in `l` to verify the reader
    // doesn't crash or freeze; clamping keeps the path representable
    // in Skia's float matrix without overflowing. Real PDFs always
    // stay within page bounds (typically <5000 pt).
    private void MoveTo(double x, double y)
    {
        _currentPath ??= new SKPath();
        _currentPath.MoveTo(ClampMatrix(x), ClampMatrix(y));
    }

    private void LineTo(double x, double y)
    {
        _currentPath ??= new SKPath();
        _currentPath.LineTo(ClampMatrix(x), ClampMatrix(y));
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _currentPath ??= new SKPath();
        _currentPath.CubicTo(
            ClampMatrix(x1), ClampMatrix(y1),
            ClampMatrix(x2), ClampMatrix(y2),
            ClampMatrix(x3), ClampMatrix(y3));
    }

    private void CurveToV(double x2, double y2, double x3, double y3)
    {
        // v operator: current point replicated as first control point
        if (_currentPath == null) return;
        var last = _currentPath.LastPoint;
        _currentPath.CubicTo(last.X, last.Y, (float)x2, (float)y2, (float)x3, (float)y3);
    }

    private void CurveToY(double x1, double y1, double x3, double y3)
    {
        // y operator: endpoint replicated as second control point
        _currentPath ??= new SKPath();
        _currentPath.CubicTo((float)x1, (float)y1, (float)x3, (float)y3, (float)x3, (float)y3);
    }

    private void ClosePath()
    {
        _currentPath?.Close();
    }

    private void Rectangle(double x, double y, double w, double h)
    {
        _currentPath ??= new SKPath();
        _currentPath.AddRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
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
