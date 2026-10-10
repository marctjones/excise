using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Excise.Core.Text;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1920: after a redaction, the embedded fonts still hold every removed
/// character: its outline in the font program, its entry in <c>/ToUnicode</c>,
/// and its width in <c>/W</c> or <c>/Widths</c>. A subset font whose script is
/// used nowhere else spells the term out (<c>mutool show</c> prints a Quartz
/// subset's /ToUnicode as the removed word). This removes, from each font, the
/// codes that the redaction stopped drawing and that nothing in the document
/// still draws, by editing the font objects in place.
/// </summary>
/// <remarks>
/// <para><b>Nothing that stays may change.</b> Glyph ids are kept; a kept glyph
/// keeps its outline bytes, its cmap entry, its name, its metrics, its width and
/// its /ToUnicode entry, so text that stays renders to the same pixels and
/// extracts to the same characters. Each edited carrier is read back and
/// compared before anything is committed.</para>
/// <para><b>Only the delta.</b> A code is removed from a font only when the
/// document drew it before the redaction (<see cref="FontGlyphUsage"/>, all
/// pages, forms, patterns, soft masks, Type3 procedures and every appearance
/// stream) and draws it nowhere after, and no form field's value could be
/// drawn with it by a viewer that regenerates appearances.</para>
/// <para><b>Blend in, do not punch holes.</b> A carrier that lists mostly
/// glyphs the document never drew (a fully embedded font, a /ToUnicode for
/// every code) does not single out the removed characters, and emptying their
/// entries would: such a carrier is left as it is.</para>
/// <para><b>Report, never skip.</b> A font this cannot edit safely (Type1,
/// Type3, a CID font with a /CIDToGIDMap stream, a CFF with subroutines, an
/// unknown sfnt table, a font some unreadable stream may draw with...) is
/// returned as a refused carrier and left entirely unedited.</para>
/// <para>Every edited object is re-registered with the document so an
/// eviction cannot restore the original (F3, #1207).</para>
/// </remarks>
internal static class FontGlyphScrubber
{
    private sealed class FontInfo
    {
        public required object Key;
        public required PdfDictionary Font;
        public required string Label;
        public PdfDictionary? CidFont;
        public PdfStream? Program;
        public bool ProgramIsBareCff;
        public PdfStream? ToUnicode;
        public bool IsCid;
        public string? Refusal;
        public Func<int, int, IEnumerable<int>>? GidsOf;   // (code, cid) → candidate gids

        public HashSet<int> CodesBefore = new(), CidsBefore = new(), CodesKept = new(), CidsKept = new();
        public HashSet<int> RemovedCodes = new(), RemovedCids = new();
        public Dictionary<int, string> Unicode = new();
        public bool Uncertain;
    }

    /// <summary>
    /// Remove from every font the codes <paramref name="before"/> drew and
    /// <paramref name="after"/> does not; return a refused row per font that
    /// lost codes and could not be edited.
    /// </summary>
    public static List<CarrierResult> Scrub(PdfDocument document, FontGlyphUsage before, FontGlyphUsage after)
    {
        var results = new List<CarrierResult>();
        var infos = new Dictionary<object, FontInfo>();

        foreach (var key in before.Fonts.Keys.Concat(after.Fonts.Keys).Distinct())
        {
            var font = Current(document, key, before, after);
            if (font == null) continue;
            var info = Describe(document, key, font);
            if (before.Fonts.TryGetValue(key, out var b))
            {
                info.CodesBefore.UnionWith(b.Codes);
                info.CidsBefore.UnionWith(b.Cids);
                foreach (var (code, text) in b.Unicode) info.Unicode.TryAdd(code, text);
            }
            if (after.Fonts.TryGetValue(key, out var a))
            {
                info.CodesKept.UnionWith(a.Codes);
                info.CidsKept.UnionWith(a.Cids);
                foreach (var (code, text) in a.Unicode) info.Unicode.TryAdd(code, text);
            }
            info.Uncertain = before.Uncertain.Contains(key) || after.Uncertain.Contains(key);

            // A field value a viewer may draw with this font keeps its characters.
            after.FieldText.TryGetValue(key, out var fieldText);
            foreach (var code in info.CodesBefore)
            {
                if (info.CodesKept.Contains(code)) continue;
                if (fieldText != null && IsInFieldText(info.Unicode.GetValueOrDefault(code), fieldText))
                {
                    info.CodesKept.Add(code);
                    if (b != null && b.CodeToCid.TryGetValue(code, out var cid)) info.CidsKept.Add(cid);
                    continue;
                }
                info.RemovedCodes.Add(code);
            }
            if (b != null)
                foreach (var code in info.RemovedCodes)
                    if (b.CodeToCid.TryGetValue(code, out var cid) && !info.CidsKept.Contains(cid))
                        info.RemovedCids.Add(cid);
            infos[key] = info;
        }

        var plans = infos.Values.Where(i => i.RemovedCodes.Count > 0).ToList();
        if (plans.Count == 0) return results;

        // A font a field names but nothing draws could still share a carrier: never edit one.
        var fieldOnly = after.FieldText.Keys.Where(k => !infos.ContainsKey(k))
            .Select(k => (Key: k, Font: Current(document, k, before, after))).Where(x => x.Font != null)
            .Select(x => Describe(document, x.Key, x.Font!)).ToList();

        var refused = new Dictionary<FontInfo, string>();
        foreach (var plan in plans)
        {
            if (plan.Refusal != null) refused[plan] = plan.Refusal;
            else if (plan.Uncertain) refused[plan] = "a stream that may draw with it could not be read";
        }

        // Compute every carrier edit before committing any.
        var programEdits = new Dictionary<PdfStream, byte[]>(ReferenceEqualityComparer.Instance);
        var toUnicodeEdits = new Dictionary<PdfStream, byte[]>(ReferenceEqualityComparer.Instance);
        var widthEdits = new Dictionary<FontInfo, (PdfDictionary Owner, string Key, PdfArray Value)>();
        var blankedByProgram = new Dictionary<PdfStream, IReadOnlySet<int>>(ReferenceEqualityComparer.Instance);

        foreach (var program in plans.Where(p => p.Program != null && !refused.ContainsKey(p)).Select(p => p.Program!).Distinct(ReferenceEqualityComparer.Instance).Cast<PdfStream>())
        {
            var users = infos.Values.Where(i => ReferenceEquals(i.Program, program)).ToList();
            var why = users.Select(u => u.Refusal ?? (u.Uncertain ? "a stream that may draw with it could not be read" : null)).FirstOrDefault(r => r != null)
                ?? (fieldOnly.Any(f => ReferenceEquals(f.Program, program)) ? "a form field's font shares its program" : null);
            if (why != null) { RefuseUsers(users, plans, refused, why); continue; }

            var edit = EditProgram(program, users, out var blanked, out var refusal);
            if (refusal != null) { RefuseUsers(users, plans, refused, refusal); continue; }
            if (edit != null)
            {
                programEdits[program] = edit;
                blankedByProgram[program] = blanked!;
            }
        }

        foreach (var toUnicode in plans.Where(p => p.ToUnicode != null && !refused.ContainsKey(p)).Select(p => p.ToUnicode!).Distinct(ReferenceEqualityComparer.Instance).Cast<PdfStream>())
        {
            var users = infos.Values.Where(i => ReferenceEquals(i.ToUnicode, toUnicode)).ToList();
            if (fieldOnly.Any(f => ReferenceEquals(f.ToUnicode, toUnicode)))
            {
                RefuseUsers(users, plans, refused, "a form field's font shares its /ToUnicode");
                continue;
            }
            var drop = users.SelectMany(u => u.RemovedCodes).ToHashSet();
            drop.ExceptWith(users.SelectMany(u => u.CodesKept));
            if (drop.Count == 0) continue;
            if (!Decoded(toUnicode, out var data))
            {
                RefuseUsers(users, plans, refused, "its /ToUnicode CMap could not be decoded");
                continue;
            }
            var mapped = ToUnicodeCMapParser.Parse(data).Keys;
            if (!SfntGlyphEditor.TracksUsage(mapped, users.SelectMany(u => u.CodesBefore).ToHashSet()))
                continue;
            var edited = ToUnicodeCMapEditor.RemoveCodes(data, drop, out var refusal);
            if (edited == null) { RefuseUsers(users, plans, refused, refusal!); continue; }
            if (!ReferenceEquals(edited, data)) toUnicodeEdits[toUnicode] = edited;
        }

        foreach (var plan in plans.Where(p => !refused.ContainsKey(p)))
        {
            var edit = plan.IsCid ? EditW(document, plan) : EditWidths(document, plan);
            if (edit != null) widthEdits[plan] = edit.Value;
        }

        // A refusal blocks every carrier its font owns, and so every font sharing one.
        bool changed;
        do
        {
            changed = false;
            foreach (var plan in plans.Where(p => !refused.ContainsKey(p)))
            {
                var blocked = refused.Keys.FirstOrDefault(r =>
                    (r.Program != null && ReferenceEquals(r.Program, plan.Program)) ||
                    (r.ToUnicode != null && ReferenceEquals(r.ToUnicode, plan.ToUnicode)));
                if (blocked == null) continue;
                refused[plan] = $"it shares a font carrier with {blocked.Label}, which could not be edited";
                changed = true;
            }
        }
        while (changed);

        // Commit.
        foreach (var plan in plans.Where(p => !refused.ContainsKey(p)))
        {
            if (plan.Program != null && programEdits.Remove(plan.Program, out var program))
            {
                plan.Program.DecodedData = program;
                if (!plan.ProgramIsBareCff && plan.Program.ContainsKey("Length1"))
                    plan.Program.SetInt("Length1", program.Length);
                Register(document, plan.Program);
            }
            if (plan.ToUnicode != null && toUnicodeEdits.Remove(plan.ToUnicode, out var cmap))
            {
                plan.ToUnicode.DecodedData = cmap;
                Register(document, plan.ToUnicode);
            }
            if (widthEdits.TryGetValue(plan, out var w))
            {
                // An object read from an object stream carries no number and is
                // never evicted; a numbered one is re-registered.
                w.Owner.Set(w.Key, w.Value);
                if (ReferenceEquals(w.Owner, plan.Font)) document.ReplaceIndirectObject((int)plan.Key, plan.Font);
                else Register(document, w.Owner);
                if (!w.Owner.IsIndirect && plan.CidFont != null && ReferenceEquals(w.Owner, plan.CidFont))
                {
                    // A direct descendant lives inside the Type0 dictionary: re-register that.
                    plan.Font.Set("DescendantFonts", plan.Font.GetOptional("DescendantFonts")!);
                    document.ReplaceIndirectObject((int)plan.Key, plan.Font);
                }
            }
        }

        foreach (var (plan, why) in refused)
            results.Add(new CarrierResult($"font {plan.Label}", false,
                $"#1920: the removed characters' glyphs, widths and /ToUnicode entries remain in this font: {why}"));
        return results;
    }

    private static void RefuseUsers(List<FontInfo> users, List<FontInfo> plans, Dictionary<FontInfo, string> refused, string why)
    {
        foreach (var u in users)
            if (plans.Contains(u) && !refused.ContainsKey(u))
                refused[u] = why;
    }

    private static void Register(PdfDocument document, PdfDictionary obj)
    {
        if (obj.ObjectNumber is { } n) document.ReplaceIndirectObject(n, obj);
    }

    private static bool IsInFieldText(string? unicode, HashSet<int> fieldText)
    {
        if (string.IsNullOrEmpty(unicode)) return true;
        for (var i = 0; i < unicode.Length; i++)
        {
            var cp = char.IsHighSurrogate(unicode[i]) && i + 1 < unicode.Length && char.IsLowSurrogate(unicode[i + 1])
                ? char.ConvertToUtf32(unicode[i], unicode[++i]) : unicode[i];
            if (fieldText.Contains(cp)) return true;
        }
        return false;
    }

    private static PdfDictionary? Current(PdfDocument document, object key, FontGlyphUsage before, FontGlyphUsage after)
    {
        if (key is int number) return document.GetObject(number) as PdfDictionary;
        return key as PdfDictionary ?? after.Fonts.GetValueOrDefault(key)?.Font ?? before.Fonts.GetValueOrDefault(key)?.Font;
    }

    private static bool Decoded(PdfStream stream, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (stream.IsFiltered && !stream.TryEnsureDecoded()) return false;
        data = stream.DecodedData;
        return true;
    }

    // ---- what a font is --------------------------------------------------------

    private static FontInfo Describe(PdfDocument document, object key, PdfDictionary font)
    {
        var name = font.GetNameOrNull("BaseFont") ?? "(unnamed)";
        var label = key is int n ? $"{name} ({n} 0 R)" : name;
        var info = new FontInfo { Key = key, Font = font, Label = label };
        info.ToUnicode = document.Resolve(font.GetOptional("ToUnicode") ?? PdfNull.Instance) as PdfStream;
        var subtype = font.GetNameOrNull("Subtype");
        if (key is not int)
        {
            // A direct font is tracked by instance: a re-parse of its owner between
            // the two snapshots would read as "draws nothing now", and an edit to it
            // cannot be re-registered.
            info.Refusal = "it is a direct object inside a resource dictionary";
            return info;
        }

        switch (subtype)
        {
            case "Type0":
            {
                info.IsCid = true;
                var descendants = document.Resolve(font.GetOptional("DescendantFonts") ?? PdfNull.Instance) as PdfArray;
                var cidFont = descendants is { Count: > 0 } ? document.Resolve(descendants[0]) as PdfDictionary : null;
                if (cidFont == null) { info.Refusal = "its descendant CIDFont could not be read"; return info; }
                info.CidFont = cidFont;
                var descriptor = document.Resolve(cidFont.GetOptional("FontDescriptor") ?? PdfNull.Instance) as PdfDictionary;
                var cidSubtype = cidFont.GetNameOrNull("Subtype");
                if (descriptor == null) { info.Refusal = "it has no font descriptor"; return info; }
                if (cidSubtype == "CIDFontType2")
                {
                    if (document.Resolve(cidFont.GetOptional("CIDToGIDMap") ?? PdfNull.Instance) is PdfStream)
                    {
                        info.Refusal = "its /CIDToGIDMap is a stream";
                        return info;
                    }
                    if (cidFont.GetNameOrNull("CIDToGIDMap") is { } map && map != "Identity")
                    {
                        info.Refusal = $"its /CIDToGIDMap is /{map}";
                        return info;
                    }
                    info.Program = document.Resolve(descriptor.GetOptional("FontFile2") ?? PdfNull.Instance) as PdfStream;
                    if (info.Program == null && HasOtherProgram(document, descriptor))
                    {
                        info.Refusal = "its CIDFontType2 program is not a /FontFile2";
                        return info;
                    }
                    info.GidsOf = (_, cid) => new[] { cid };
                }
                else if (cidSubtype == "CIDFontType0")
                {
                    info.Program = document.Resolve(descriptor.GetOptional("FontFile3") ?? PdfNull.Instance) as PdfStream;
                    if (info.Program == null)
                    {
                        if (HasOtherProgram(document, descriptor)) info.Refusal = "its CIDFontType0 program is not a /FontFile3";
                        return info;
                    }
                    var programSubtype = info.Program.GetNameOrNull("Subtype");
                    if (programSubtype is not ("CIDFontType0C" or "OpenType"))
                    {
                        info.Refusal = $"its /FontFile3 is /{programSubtype}";
                        return info;
                    }
                    info.ProgramIsBareCff = programSubtype == "CIDFontType0C";
                    if (!Decoded(info.Program, out var data) || SfntGlyphEditor.CffOf(data) is not { } cff ||
                        CffGlyphEditor.CidToGid(cff) is not { } cidToGid)
                    {
                        info.Refusal = "its CFF program is not CID-keyed or could not be read";
                        return info;
                    }
                    info.GidsOf = (_, cid) => cidToGid.TryGetValue(cid, out var g) ? new[] { g } : Array.Empty<int>();
                }
                else
                {
                    info.Refusal = $"its descendant font is /{cidSubtype}";
                }
                return info;
            }
            case "TrueType":
            {
                var descriptor = document.Resolve(font.GetOptional("FontDescriptor") ?? PdfNull.Instance) as PdfDictionary;
                if (descriptor == null) return info;
                info.Program = document.Resolve(descriptor.GetOptional("FontFile2") ?? PdfNull.Instance) as PdfStream;
                if (info.Program == null)
                {
                    if (HasOtherProgram(document, descriptor)) info.Refusal = "its TrueType program is not a /FontFile2";
                    return info;
                }
                if (!Decoded(info.Program, out var program))
                {
                    info.Refusal = "its font program could not be decoded";
                    return info;
                }
                var attribution = SimpleTrueTypeAttribution.Build(document, font, program, info.Unicode);
                if (attribution == null)
                {
                    info.Refusal = "its font program's cmap could not be read";
                    return info;
                }
                info.GidsOf = (code, _) => attribution.GidsOf(code);
                return info;
            }
            case "Type1":
            case "MMType1":
            {
                var descriptor = document.Resolve(font.GetOptional("FontDescriptor") ?? PdfNull.Instance) as PdfDictionary;
                if (descriptor != null && HasOtherProgram(document, descriptor))
                    info.Refusal = $"its embedded {subtype} program is not edited by the redaction";
                return info;
            }
            case "Type3":
                info.Refusal = "a Type3 font's glyph procedures are not edited by the redaction";
                return info;
            default:
                info.Refusal = $"its font type /{subtype} is not edited by the redaction";
                return info;
        }
    }

    private static bool HasOtherProgram(PdfDocument document, PdfDictionary descriptor) =>
        new[] { "FontFile", "FontFile2", "FontFile3" }.Any(k => document.Resolve(descriptor.GetOptional(k) ?? PdfNull.Instance) is PdfStream);

    // ---- the program -----------------------------------------------------------

    private static byte[]? EditProgram(PdfStream program, List<FontInfo> users, out IReadOnlySet<int>? blanked, out string? refusal)
    {
        blanked = null;
        refusal = null;
        if (!Decoded(program, out var data)) { refusal = "its font program could not be decoded"; return null; }

        var candidates = new HashSet<int>();
        var keep = new HashSet<int> { 0 };
        var drawnBefore = new HashSet<int> { 0 };
        var drawnCids = new HashSet<int>();
        foreach (var u in users)
        {
            if (u.GidsOf == null) { refusal = "a font sharing its program could not be mapped to glyphs"; return null; }
            foreach (var code in u.RemovedCodes) candidates.UnionWith(Gids(u, code));
            foreach (var code in u.CodesKept) keep.UnionWith(Gids(u, code));
            foreach (var code in u.CodesBefore) drawnBefore.UnionWith(Gids(u, code));
            if (u.IsCid)
            {
                // A CID font's glyph follows the CID, which can differ from the code.
                foreach (var cid in u.RemovedCids) candidates.UnionWith(u.GidsOf(-1, cid));
                foreach (var cid in u.CidsKept) keep.UnionWith(u.GidsOf(-1, cid));
                foreach (var cid in u.CidsBefore) drawnBefore.UnionWith(u.GidsOf(-1, cid));
                drawnCids.UnionWith(u.CidsBefore);
            }
        }
        candidates.ExceptWith(keep);
        if (candidates.Count == 0) return null;

        var bareCff = users.Any(u => u.ProgramIsBareCff);
        var outlines = bareCff ? CffGlyphEditor.OutlineGids(data) : SfntGlyphEditor.OutlineGids(data);
        if (outlines == null) { refusal = "its font program could not be read"; return null; }
        outlines.Remove(0);
        // A fully embedded font: the removed glyphs are among many never drawn.
        if (!SfntGlyphEditor.TracksUsage(outlines, drawnBefore)) return null;

        if (bareCff)
        {
            var edited = CffGlyphEditor.BlankGlyphs(data, candidates, drawnCids, out refusal);
            blanked = candidates;
            return edited == null || ReferenceEquals(edited, data) ? null : edited;
        }
        var result = SfntGlyphEditor.BlankGlyphs(data, candidates, drawnBefore, drawnCids, out refusal);
        if (result == null) return null;
        blanked = result.Blanked;
        return ReferenceEquals(result.Data, data) ? null : result.Data;

        IEnumerable<int> Gids(FontInfo u, int code) =>
            u.IsCid ? Array.Empty<int>() : u.GidsOf!(code, code);
    }

    // ---- widths ----------------------------------------------------------------

    private static (PdfDictionary, string, PdfArray)? EditWidths(PdfDocument document, FontInfo plan)
    {
        if (document.Resolve(plan.Font.GetOptional("Widths") ?? PdfNull.Instance) is not PdfArray widths) return null;
        var first = (int)plan.Font.GetNumber("FirstChar", 0);
        var drop = plan.RemovedCodes.Where(c => c >= first && c < first + widths.Count).ToHashSet();
        if (drop.Count == 0) return null;
        var explicitCodes = Enumerable.Range(0, widths.Count)
            .Where(i => document.Resolve(widths[i]).TryGetNumber(out var w) && w != 0).Select(i => first + i);
        if (!SfntGlyphEditor.TracksUsage(explicitCodes, plan.CodesBefore)) return null;

        var copy = new PdfArray();
        for (var i = 0; i < widths.Count; i++)
            copy.Add(drop.Contains(first + i) ? new PdfInteger(0) : widths[i]);
        return (plan.Font, "Widths", copy);
    }

    private static (PdfDictionary, string, PdfArray)? EditW(PdfDocument document, FontInfo plan)
    {
        if (plan.CidFont == null || document.Resolve(plan.CidFont.GetOptional("W") ?? PdfNull.Instance) is not PdfArray w)
            return null;
        var drop = plan.RemovedCids;
        if (drop.Count == 0) return null;

        // Read /W as its entries: [c [w1 w2 ...]] runs and [c1 c2 w] ranges, in order.
        var runs = new List<(int First, List<PdfObject> Widths)>();
        var ranges = new List<(int Index, int First, int Last, PdfObject Width)>();
        var order = new List<(bool IsRun, int Index)>();
        var explicitCids = new HashSet<int>();
        for (var i = 0; i < w.Count;)
        {
            if (!document.Resolve(w[i]).TryGetNumber(out var a)) return null;
            if (i + 1 < w.Count && document.Resolve(w[i + 1]) is PdfArray run)
            {
                order.Add((true, runs.Count));
                runs.Add(((int)a, run.ToList()));
                for (var k = 0; k < run.Count; k++) explicitCids.Add((int)a + k);
                i += 2;
            }
            else if (i + 2 < w.Count && document.Resolve(w[i + 1]).TryGetNumber(out var b))
            {
                order.Add((false, ranges.Count));
                ranges.Add((ranges.Count, (int)a, (int)b, w[i + 2]));
                for (var c = (int)a; c <= (int)b && c - (int)a <= 0xFFFF; c++) explicitCids.Add(c);
                i += 3;
            }
            else return null;
        }
        if (!explicitCids.Overlaps(drop)) return null;
        if (!SfntGlyphEditor.TracksUsage(explicitCids, plan.CidsBefore)) return null;

        // Re-emit every entry, split around the dropped CIDs; each width object is the original.
        var output = new PdfArray();
        foreach (var (isRun, index) in order)
        {
            if (isRun)
            {
                var (first, widths) = runs[index];
                PdfArray? current = null;
                for (var k = 0; k < widths.Count; k++)
                {
                    if (drop.Contains(first + k)) { current = null; continue; }
                    if (current == null)
                    {
                        current = new PdfArray();
                        output.Add((PdfObject)new PdfInteger(first + k));
                        output.Add(current);
                    }
                    current.Add(widths[k]);
                }
            }
            else
            {
                var (_, lo, hi, width) = ranges[index];
                var start = lo;
                foreach (var d in drop.Where(c => c >= lo && c <= hi).OrderBy(c => c).Append(hi + 1))
                {
                    if (d > start)
                    {
                        output.Add((PdfObject)new PdfInteger(start));
                        output.Add((PdfObject)new PdfInteger(d - 1));
                        output.Add(width);
                    }
                    start = d + 1;
                }
            }
        }
        return (plan.CidFont, "W", output);
    }
}
