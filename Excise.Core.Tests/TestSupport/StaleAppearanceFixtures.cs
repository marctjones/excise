using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Excise.TestSupport;

/// <summary>How the fixture's old appearance streams write the old value (#2017).</summary>
public enum OldAppearanceText
{
    /// <summary><c>(ALPHAOLD) Tj</c>, as Acrobat writes it.</summary>
    Tj,

    /// <summary><c>[(ALP) -20 (HA) 10 (OLD)] TJ</c>: never contiguous in the bytes.</summary>
    KernedTj,

    /// <summary><c>&lt;414C...&gt; Tj</c>.</summary>
    Hex,
}

/// <summary>
/// #2017: a form as a producer that writes appearances (Acrobat) leaves it:
/// every widget has an <c>/AP</c> that DRAWS its current value, which excise
/// did not author. Each old appearance stream (normal and down) carries a
/// unique <c>/StaleMarker</c> name in its dictionary, so a test can prove the
/// STREAM is gone from the saved file however its text was written (a kerned
/// <c>TJ</c> array is invisible to a contiguous-bytes search). Streams are
/// uncompressed so the markers and Tj values are plain in the source.
/// </summary>
internal static class StaleAppearanceFixtures
{
    /// <summary>A text, choice or button field in the fixture and the values a test moves it between.</summary>
    public sealed record Field(string Name, string OldValue, string NewValue, double[] Rect, bool Regenerates);

    // Single-widget text fields, all /Helv (WinAnsi, not embedded).
    public static readonly Field Name = new("Name", "ALPHAOLD", "BETANEW", [100, 700, 400, 720], true);
    public static readonly Field Comb = new("Comb", "COMBOLD1", "COMBNEW8", [100, 600, 260, 620], true);
    public static readonly Field Notes = new("Notes", "NOTESOLD", "NOTESNEW", [100, 520, 400, 580], true);
    public static readonly Field Secret = new("Secret", "PASSOLD", "PASSNEW", [100, 480, 300, 500], true);
    public static readonly Field Pick = new("Pick", "PICKOLD", "PICKNEW", [100, 440, 300, 460], true);

    /// <summary>A list box: excise does not regenerate it, the stale appearance is dropped.</summary>
    public static readonly Field List = new("List", "LISTOLD", "LISTNEW", [100, 380, 300, 430], false);

    /// <summary>Rotated (<c>/MK /R 90</c>): dropped, not regenerated.</summary>
    public static readonly Field Turned = new("Turned", "TURNEDOLD", "TURNEDNEW", [450, 380, 470, 560], false);

    /// <summary>A field with two widget kids, each with its own old appearance.</summary>
    public static readonly Field Multi = new("Multi", "MULTIOLD", "MULTINEW", [100, 650, 250, 670], true);
    public static readonly double[] MultiSecondRect = [300, 650, 450, 670];

    /// <summary>Two fields whose widgets share ONE indirect /AP and one /N stream.</summary>
    public static readonly Field SharedA = new("SharedA", "SHAREDOLD", "SHAREDNEW", [100, 300, 300, 320], true);
    public static readonly Field SharedB = new("SharedB", "SHAREDOLD", "SHAREDOLD", [100, 260, 300, 280], true);

    public const string CheckBoxName = "Box";

    /// <summary>The text fields (and the combo) whose value the tests change.</summary>
    public static readonly Field[] Changed = [Name, Comb, Notes, Secret, Pick, List, Turned, Multi, SharedA];

    /// <summary>The marker on a field's old normal appearance (<paramref name="kid"/> for Multi's kids).</summary>
    public static string NormalMarker(Field field, int kid = 1) => $"STALEAP{field.Name.ToUpperInvariant()}N{kid}";

    /// <summary>The marker on a field's old down appearance.</summary>
    public static string DownMarker(Field field, int kid = 1) => $"STALEAP{field.Name.ToUpperInvariant()}D{kid}";

    /// <summary>The shared normal appearance SharedA and SharedB both point at.</summary>
    public const string SharedMarker = "STALEAPSHAREDN";

    /// <summary>Every marker that must be gone once all <see cref="Changed"/> fields change (the shared stream stays: SharedB still uses it).</summary>
    public static IEnumerable<string> RemovedMarkers()
    {
        foreach (var f in Changed)
        {
            if (f == SharedA) continue;
            yield return NormalMarker(f);
            yield return DownMarker(f);
            if (f == Multi)
            {
                yield return NormalMarker(f, 2);
                yield return DownMarker(f, 2);
            }
        }
    }

    public static byte[] Build(OldAppearanceText form = OldAppearanceText.Tj, string version = "1.7")
    {
        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; }
        int Reserve() { objects.Add(""); return objects.Count; }
        void Set(int number, string body) => objects[number - 1] = body;

        var catalog = Reserve();
        var pages = Reserve();
        var page = Reserve();
        var helv = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var contents = Add(RawStream("", "BT /Helv 10 Tf 20 760 Td (Stale appearance fixture) Tj ET"));

        string Show(string value) => form switch
        {
            OldAppearanceText.Tj => $"({value}) Tj",
            OldAppearanceText.Hex => $"<{Convert.ToHexString(Encoding.ASCII.GetBytes(value))}> Tj",
            _ => KernedTj(value),
        };

        int Appearance(double[] rect, string marker, string value)
        {
            var w = rect[2] - rect[0];
            var h = rect[3] - rect[1];
            var body = $"/Tx BMC q BT 0 g /Helv 12 Tf 2 {F(Math.Min(5, h / 3))} Td {Show(value)} ET Q EMC";
            return Add(RawStream(
                $"/Type /XObject /Subtype /Form /BBox [0 0 {F(w)} {F(h)}] /Resources << /Font << /Helv {helv} 0 R >> >> /StaleMarker /{marker}",
                body));
        }

        string Ap(Field f, double[] rect, int kid = 1, string? old = null)
        {
            var n = Appearance(rect, NormalMarker(f, kid), old ?? f.OldValue);
            var d = Appearance(rect, DownMarker(f, kid), old ?? f.OldValue);
            return $"/AP << /N {n} 0 R /D {d} 0 R >>";
        }

        static string RectOf(double[] r) => $"[{F(r[0])} {F(r[1])} {F(r[2])} {F(r[3])}]";

        var annots = new List<int>();
        var fields = new List<int>();

        int Widget(Field f, string extra, string? ap = null)
        {
            var n = Add($"<< /Type /Annot /Subtype /Widget /F 4 /T ({f.Name}) /V ({f.OldValue}) /Rect {RectOf(f.Rect)} " +
                        $"/P {page} 0 R {extra} {ap ?? Ap(f, f.Rect)} >>");
            annots.Add(n);
            fields.Add(n);
            return n;
        }

        Widget(Name, "/FT /Tx /DA (/Helv 12 Tf 0 g)");
        Widget(Comb, "/FT /Tx /Ff 16777216 /MaxLen 8 /DA (/Helv 0 Tf 0 g)");
        Widget(Notes, "/FT /Tx /Ff 4096 /DA (/Helv 10 Tf 0 0 1 rg) /Q 1");
        Widget(Secret, "/FT /Tx /Ff 8192 /DA (/Helv 12 Tf 0 g)");
        Widget(Pick, $"/FT /Ch /Ff 131072 /Opt [({Pick.OldValue}) ({Pick.NewValue})] /DA (/Helv 12 Tf 0 g) /MK << /BG [0.9] /BC [0 0 1] >> /BS << /W 1 /S /S >>");
        Widget(List, $"/FT /Ch /Opt [({List.OldValue}) ({List.NewValue})] /DA (/Helv 12 Tf 0 g)");
        Widget(Turned, "/FT /Tx /DA (/Helv 12 Tf 0 g) /MK << /R 90 >>");

        // Multi: a parent field with two widget kids.
        var multiParent = Reserve();
        var kid1 = Add($"<< /Type /Annot /Subtype /Widget /F 4 /Parent {multiParent} 0 R /Rect {RectOf(Multi.Rect)} /P {page} 0 R {Ap(Multi, Multi.Rect, 1)} >>");
        var kid2 = Add($"<< /Type /Annot /Subtype /Widget /F 4 /Parent {multiParent} 0 R /Rect {RectOf(MultiSecondRect)} /P {page} 0 R {Ap(Multi, MultiSecondRect, 2)} >>");
        Set(multiParent, $"<< /FT /Tx /T ({Multi.Name}) /V ({Multi.OldValue}) /DA (/Helv 0 Tf 0 g) /Kids [{kid1} 0 R {kid2} 0 R] >>");
        annots.Add(kid1);
        annots.Add(kid2);
        fields.Add(multiParent);

        // SharedA / SharedB: one indirect /AP dictionary, one /N stream.
        var sharedN = Appearance(SharedA.Rect, SharedMarker, SharedA.OldValue);
        var sharedAp = Add($"<< /N {sharedN} 0 R >>");
        Widget(SharedA, "/FT /Tx /DA (/Helv 12 Tf 0 g)", $"/AP {sharedAp} 0 R");
        Widget(SharedB, "/FT /Tx /DA (/Helv 12 Tf 0 g)", $"/AP {sharedAp} 0 R");

        // A checkbox: /AS selects between fixed state drawings (no value text).
        var on = Add(RawStream("/Type /XObject /Subtype /Form /BBox [0 0 14 14] /Resources << >>", "q 0 G 1 w 2 7 m 6 3 l 12 12 l S Q"));
        var off = Add(RawStream("/Type /XObject /Subtype /Form /BBox [0 0 14 14] /Resources << >>", ""));
        var box = Add($"<< /Type /Annot /Subtype /Widget /F 4 /FT /Btn /T ({CheckBoxName}) /V /Yes /AS /Yes /Rect [100 220 114 234] " +
                      $"/P {page} 0 R /AP << /N << /Yes {on} 0 R /Off {off} 0 R >> >> >>");
        annots.Add(box);
        fields.Add(box);

        var acroForm = Add($"<< /Fields [{string.Join(' ', fields.Select(n => $"{n} 0 R"))}] /DA (/Helv 0 Tf 0 g) " +
                           $"/DR << /Font << /Helv {helv} 0 R >> >> >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm {acroForm} 0 R >>");
        Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R " +
                  $"/Resources << /Font << /Helv {helv} 0 R >> >> /Annots [{string.Join(' ', annots.Select(n => $"{n} 0 R"))}] >>");

        var sb = new StringBuilder($"%PDF-{version}\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append($" /Root {catalog} 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary><c>[(AL) -20 (PH) 10 (AOLD)] TJ</c>: the value split into kerned pieces of at most three characters.</summary>
    public static string KernedTj(string value)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < value.Length; i += 3)
        {
            if (i > 0) sb.Append(i % 2 == 0 ? " -20 " : " 10 ");
            sb.Append('(').Append(value.Substring(i, Math.Min(3, value.Length - i))).Append(')');
        }
        return sb.Append("] TJ").ToString();
    }

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string RawStream(string dictionary, string data)
        => $"<< {dictionary} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";
}
