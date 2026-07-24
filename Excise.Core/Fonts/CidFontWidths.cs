using System;
using System.Collections.Generic;
using Excise.Core.Primitives;

namespace Excise.Core.Fonts;

/// <summary>
/// Per-CID vertical metrics from a CIDFont's <c>/W2</c> array
/// (PDF 32000-1:2008 §9.7.4.3): <paramref name="W1Y"/> is the vertical
/// displacement component w1y (thousandths of an em, typically negative —
/// vertical text advances DOWN the page in default text space), and
/// (<paramref name="Vx"/>, <paramref name="Vy"/>) is the position vector v
/// from the glyph's horizontal origin to its vertical origin.
/// </summary>
internal readonly record struct CidVerticalMetrics(double W1Y, double Vx, double Vy);

/// <summary>
/// The CID-keyed glyph metrics of a descendant CIDFont — <c>/DW</c>, <c>/W</c>,
/// <c>/DW2</c> and <c>/W2</c> (PDF 32000-1:2008 §9.7.4.3) — parsed once and
/// shared by the three consumers that previously each re-implemented the
/// <c>/W</c> walk (<c>TextExtractor</c>, <c>ContentStreamParser</c>, and the
/// renderer's <c>PdfFontResolver</c> path). #515.
///
/// Malformed-input hardening (all recover, never throw):
/// <list type="bullet">
/// <item>indirect references are resolved at every level (outer array, inner
/// arrays, and individual numbers) when a resolver is supplied;</item>
/// <item>non-numeric junk tokens are skipped;</item>
/// <item>CIDs are clamped to the valid range [0, 65535]: reversed ranges
/// (c<sub>last</sub> &lt; c<sub>first</sub>) are dropped, and range ends are
/// capped at 65535 so a hostile <c>[0 999999999 500]</c> cannot allocate
/// billions of entries;</item>
/// <item><c>/W2</c> inner arrays are consumed in [w1y vx vy] triples; a
/// trailing incomplete triple is ignored;</item>
/// <item>a malformed <c>/DW</c> keeps the spec default 1000; a malformed
/// <c>/DW2</c> keeps the spec default [880 −1000].</item>
/// </list>
/// </summary>
internal sealed class CidFontWidths
{
    /// <summary>Valid CIDs are 0..65535 (CIDFontType2 GIDs are uint16, and
    /// CMap CID output is bounded the same way in practice). Also the range
    /// cap that defuses width-array range bombs.</summary>
    private const int MaxCid = 65535;

    /// <summary>Spec default for <c>/DW</c> (§9.7.4.3).</summary>
    public const double SpecDefaultWidth = 1000;

    /// <summary>Spec default <c>/DW2</c> position-vector y (§9.7.4.3: [880 −1000]).</summary>
    public const double SpecDefaultVerticalOriginY = 880;

    /// <summary>Spec default <c>/DW2</c> vertical displacement w1y (§9.7.4.3).</summary>
    public const double SpecDefaultVerticalDisplacement = -1000;

    private readonly Dictionary<int, double> _widths;
    private readonly Dictionary<int, CidVerticalMetrics> _verticalMetrics;

    private CidFontWidths(
        double defaultWidth,
        double defaultVerticalOriginY,
        double defaultVerticalDisplacement,
        Dictionary<int, double> widths,
        Dictionary<int, CidVerticalMetrics> verticalMetrics)
    {
        DefaultWidth = defaultWidth;
        DefaultVerticalOriginY = defaultVerticalOriginY;
        DefaultVerticalDisplacement = defaultVerticalDisplacement;
        _widths = widths;
        _verticalMetrics = verticalMetrics;
    }

    /// <summary><c>/DW</c> — horizontal width for CIDs not listed in <c>/W</c>.</summary>
    public double DefaultWidth { get; }

    /// <summary><c>/DW2</c>[0] — default position-vector y component vy.</summary>
    public double DefaultVerticalOriginY { get; }

    /// <summary><c>/DW2</c>[1] — default vertical displacement w1y (negative = down).</summary>
    public double DefaultVerticalDisplacement { get; }

    /// <summary>Per-CID horizontal widths from <c>/W</c>.</summary>
    public IReadOnlyDictionary<int, double> Widths => _widths;

    /// <summary>Per-CID vertical metrics from <c>/W2</c>.</summary>
    public IReadOnlyDictionary<int, CidVerticalMetrics> VerticalMetrics => _verticalMetrics;

    /// <summary>Horizontal width w0 for <paramref name="cid"/> in thousandths
    /// of an em — the <c>/W</c> entry, else <c>/DW</c>.</summary>
    public double GetWidth(int cid)
        => _widths.TryGetValue(cid, out var w) ? w : DefaultWidth;

    /// <summary>
    /// Vertical metrics for <paramref name="cid"/> — the <c>/W2</c> entry,
    /// else the §9.7.4.3 defaults: w1y from <c>/DW2</c>[1] and position vector
    /// v = (w0 ⁄ 2, <c>/DW2</c>[0]) built from the glyph's horizontal width.
    /// </summary>
    public CidVerticalMetrics GetVerticalMetrics(int cid)
        => _verticalMetrics.TryGetValue(cid, out var m)
            ? m
            : new CidVerticalMetrics(
                DefaultVerticalDisplacement,
                GetWidth(cid) / 2,
                DefaultVerticalOriginY);

    /// <summary>
    /// Parses the metrics entries of a descendant CIDFont dictionary.
    /// <paramref name="resolve"/> maps indirect references to their targets
    /// (pass <c>document.Resolve</c>); when null, objects are used as-is.
    /// Never throws on malformed input — bad entries are skipped and the
    /// spec defaults kept.
    /// </summary>
    public static CidFontWidths Parse(PdfDictionary cidFont, Func<PdfObject, PdfObject>? resolve = null)
    {
        PdfObject Resolve(PdfObject obj)
        {
            if (resolve == null) return obj;
            try { return resolve(obj); }
            catch { return obj; }
        }

        bool TryNumber(PdfObject? obj, out double value)
        {
            value = 0;
            return obj != null && Resolve(obj).TryGetNumber(out value);
        }

        var defaultWidth = SpecDefaultWidth;
        if (cidFont.GetOptional("DW") is { } dwObj && TryNumber(dwObj, out var dw))
            defaultWidth = dw;

        // /DW2 [vy w1y] — only a well-formed two-number array replaces the
        // spec defaults; anything else (wrong type, wrong arity, junk
        // elements) keeps [880 -1000].
        var defaultVy = SpecDefaultVerticalOriginY;
        var defaultW1Y = SpecDefaultVerticalDisplacement;
        if (cidFont.GetOptional("DW2") is { } dw2Obj
            && Resolve(dw2Obj) is PdfArray dw2 && dw2.Count == 2
            && TryNumber(dw2[0], out var vy) && TryNumber(dw2[1], out var w1y))
        {
            defaultVy = vy;
            defaultW1Y = w1y;
        }

        var widths = new Dictionary<int, double>();
        if (cidFont.GetOptional("W") is { } wObj && Resolve(wObj) is PdfArray w)
            ParseWidthArray(w, valuesPerEntry: 1, (cid, values) => widths[cid] = values[0]);

        var verticalMetrics = new Dictionary<int, CidVerticalMetrics>();
        if (cidFont.GetOptional("W2") is { } w2Obj && Resolve(w2Obj) is PdfArray w2)
            ParseWidthArray(w2, valuesPerEntry: 3, (cid, values) =>
                verticalMetrics[cid] = new CidVerticalMetrics(values[0], values[1], values[2]));

        return new CidFontWidths(defaultWidth, defaultVy, defaultW1Y, widths, verticalMetrics);

        // Shared walk for the two /W-family layouts (§9.7.4.3):
        //   c [v v v ...]            → per-CID groups of valuesPerEntry numbers
        //   cFirst cLast v (…)       → one group of valuesPerEntry for a range
        void ParseWidthArray(PdfArray array, int valuesPerEntry, Action<int, double[]> emit)
        {
            int i = 0;
            while (i < array.Count)
            {
                if (!TryNumber(array[i], out var firstNum)) { i++; continue; }
                var firstCid = (int)firstNum;
                i++;
                if (i >= array.Count) break;

                if (Resolve(array[i]) is PdfArray inner)
                {
                    // Consume complete groups; ignore a trailing partial group.
                    var groups = inner.Count / valuesPerEntry;
                    for (int g = 0; g < groups; g++)
                    {
                        var cid = firstCid + g;
                        if (cid < 0 || cid > MaxCid) continue;
                        var values = new double[valuesPerEntry];
                        var ok = true;
                        for (int k = 0; k < valuesPerEntry; k++)
                            ok &= TryNumber(inner[g * valuesPerEntry + k], out values[k]);
                        if (ok) emit(cid, values);
                    }
                    i++;
                }
                else if (TryNumber(array[i], out var lastNum)
                         && IsRangeForm(array, i, valuesPerEntry))
                {
                    var lastCid = (int)lastNum;
                    i++;
                    var values = new double[valuesPerEntry];
                    var ok = true;
                    for (int k = 0; k < valuesPerEntry; k++)
                        ok &= TryNumber(array[i + k], out values[k]);
                    i += valuesPerEntry;
                    if (!ok) continue;

                    // Harden: drop reversed ranges, clamp to the valid CID
                    // space so hostile ranges cannot allocate unbounded maps.
                    if (lastCid < firstCid) continue;
                    var start = Math.Max(firstCid, 0);
                    var end = Math.Min(lastCid, MaxCid);
                    for (int cid = start; cid <= end; cid++)
                        emit(cid, values);
                }
                else
                {
                    i++; // Malformed — skip and recover.
                }
            }
        }

        // A range entry needs cLast plus valuesPerEntry numbers after it.
        bool IsRangeForm(PdfArray array, int lastCidIndex, int valuesPerEntry)
        {
            for (int k = 1; k <= valuesPerEntry; k++)
            {
                if (lastCidIndex + k >= array.Count) return false;
                if (!TryNumber(array[lastCidIndex + k], out _)) return false;
            }
            return true;
        }
    }
}
