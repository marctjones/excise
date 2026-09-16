using System.Globalization;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbDirection = HarfBuzzSharp.Direction;

namespace Excise.Rendering.Fonts;

/// <summary>
/// Lays out the <c>/Contents</c> of an annotation that ships no <c>/AP</c> and
/// needs complex-script shaping (#1363): paragraphs, per-run font fallback,
/// HarfBuzz shaping, paragraph-level right-to-left ordering and greedy word
/// wrap. It returns positioned glyph IDs; drawing stays in the renderer.
/// </summary>
/// <remarks>
/// <para><b>Render-only.</b> Nothing here writes an appearance stream. A
/// synthesised <c>/AP</c> saved into the file would be a new text carrier the
/// redaction scrubber does not know about, and the #1363 decision rules that
/// out.</para>
///
/// <para><b>Fail closed.</b> <see cref="Typeset"/> returns <c>null</c> when a
/// character has no covering font, when the shaper emits <c>.notdef</c> for a
/// visible character, or when shaping is unavailable (native HarfBuzz missing).
/// The caller then draws nothing, which is the pre-#1363 behaviour. Unshaped
/// Arabic reads as plausible WRONG text, and that is worse than no text.</para>
///
/// <para><b>Bidi is deliberately partial</b> (full UBA is #632). The paragraph
/// direction comes from its first strong character (UBA P2/P3). Runs are split
/// on font and on strong direction. Digits form left-to-right runs, neutrals
/// take the direction of their run, and a space between two words takes their
/// level when the two agree, otherwise the paragraph's. Visual order is then
/// UBA rule L2 over those levels. Explicit embeddings, isolates and bracket
/// pairing are not implemented.</para>
///
/// <para><b>Wrapping</b> breaks at U+0020 only and shapes each word on its
/// own. Arabic joining never crosses a space, so per-word shaping loses no
/// joins. A run of spaces keeps its width between words and is dropped at a
/// line break. A single word wider than the box stays on one line and is left
/// to the caller's <c>/Rect</c> clip.</para>
///
/// <para><b>Threading.</b> SkiaSharp's font manager is not safe under
/// concurrent typeface work, so <see cref="Typeset"/> takes
/// <see cref="FontManagerLock"/> itself for the whole call: the shaper opens
/// the typeface's font data and the fallback resolver queries the font
/// manager. Callers need not hold anything.</para>
/// </remarks>
internal static class AnnotationTypesetter
{
    /// <summary>
    /// Glyphs from one typeface. <see cref="Positions"/> are measured from the
    /// line's left edge in Skia's Y-down orientation, with the baseline at
    /// y = 0.
    /// </summary>
    internal sealed record GlyphRun(SKTypeface Typeface, ushort[] Glyphs, SKPoint[] Positions, bool RightToLeft);

    /// <summary>One visual line. <see cref="Runs"/> are in visual (left-to-right) order.</summary>
    internal sealed record TypesetLine(IReadOnlyList<GlyphRun> Runs, float Width, bool RightToLeft);

    /// <param name="contents">The annotation text, possibly several CR/LF-separated paragraphs.</param>
    /// <param name="primary">The typeface <c>/DA</c> selected. It is tried first for every character.</param>
    /// <param name="fontSize">Size in text-space units. Must be positive.</param>
    /// <param name="maxLineWidth">Width a line may occupy before it wraps.</param>
    /// <param name="resolveFallback">
    /// Returns a typeface covering a code point, or <c>null</c>. The renderer
    /// passes <c>SKFontManager.MatchCharacter</c>, or a resolver that always
    /// returns <c>null</c> when <see cref="RenderOptions.DisableSystemFontFallback"/>
    /// is set. Returned typefaces are NOT disposed: SkiaSharp hands back a
    /// shared managed wrapper for a native typeface it has already seen, so
    /// disposing one could dispose a typeface the page is still drawing with.
    /// </param>
    /// <param name="failure">Why nothing was typeset, when the result is <c>null</c>.</param>
    internal static IReadOnlyList<TypesetLine>? Typeset(
        string contents,
        SKTypeface primary,
        float fontSize,
        float maxLineWidth,
        Func<int, SKTypeface?> resolveFallback,
        out string? failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(contents))
        {
            failure = "empty /Contents";
            return null;
        }

        if (!(fontSize > 0f) || float.IsInfinity(fontSize))
        {
            failure = $"font size {fontSize} is not a positive finite size";
            return null;
        }

        // Taken HERE rather than left to the caller: the shaper opens the
        // typeface's font data and the fallback resolver queries the font
        // manager, and neither is safe under concurrent typeface work (#363).
        // Monitor is reentrant, so a caller already holding it is fine.
        lock (FontManagerLock.Instance)
        {
        var session = new Session(primary, fontSize, resolveFallback);
        try
        {
            var lines = new List<TypesetLine>();
            foreach (var paragraph in SplitParagraphs(contents))
            {
                if (!session.TypesetParagraph(paragraph, maxLineWidth, lines, out failure))
                    return null;
            }

            return lines;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // DllNotFoundException / TypeInitializationException when the native
            // HarfBuzz library is not deployed, or anything the shaper throws.
            // Shaping is unavailable, and unshaped text must not be drawn.
            failure = $"shaping unavailable: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            session.Dispose();
        }
        }
    }

    /// <summary>
    /// Splits on CR, LF, CRLF (a single break), U+2028 and U+2029. An empty
    /// paragraph is kept so a blank line still takes up vertical space.
    /// </summary>
    internal static List<string> SplitParagraphs(string contents)
    {
        var paragraphs = new List<string>();
        int start = 0;
        for (int i = 0; i < contents.Length; i++)
        {
            var ch = contents[i];
            if (ch is not ('\r' or '\n' or '\u2028' or '\u2029'))
                continue;

            paragraphs.Add(contents.Substring(start, i - start));
            if (ch == '\r' && i + 1 < contents.Length && contents[i + 1] == '\n')
                i++;
            start = i + 1;
        }

        paragraphs.Add(contents.Substring(start));
        return paragraphs;
    }

    /// <summary>UBA P2/P3: the direction of the first strong character, or LTR when there is none.</summary>
    internal static bool IsRightToLeftParagraph(string paragraph) =>
        FirstStrongDirection(paragraph) ?? false;

    private static bool? FirstStrongDirection(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            int cp = CodePointAt(text, i, out int units);
            switch (Classify(cp))
            {
                case CharClass.StrongRtl: return true;
                case CharClass.StrongLtr: return false;
            }
            i += units;
        }

        return null;
    }

    private enum CharClass
    {
        StrongLtr,
        StrongRtl,
        /// <summary>Decimal digits (EN/AN). Drawn left-to-right; not strong for P2.</summary>
        Number,
        Neutral,
        /// <summary>Combining marks: they belong to the preceding run and its font.</summary>
        Attached,
        /// <summary>Format, control and non-U+0020 space characters: no glyph coverage required.</summary>
        Ignorable,
    }

    private static CharClass Classify(int cp)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        switch (category)
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.SpacingCombiningMark:
                return CharClass.Attached;
            case UnicodeCategory.Format:
            case UnicodeCategory.Control:
            case UnicodeCategory.SpaceSeparator:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
                return CharClass.Ignorable;
            case UnicodeCategory.DecimalDigitNumber:
                return CharClass.Number;
        }

        bool letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
        if (!letter)
            return CharClass.Neutral;

        return IsRightToLeftBlock(cp) ? CharClass.StrongRtl : CharClass.StrongLtr;
    }

    /// <summary>
    /// Hebrew, Arabic, Syriac, Thaana, N'Ko and the Arabic extended blocks, the
    /// Hebrew/Arabic presentation forms, and the supplementary-plane RTL blocks.
    /// </summary>
    private static bool IsRightToLeftBlock(int cp) =>
        cp is (>= 0x0590 and <= 0x08FF)
            or (>= 0xFB1D and <= 0xFDFF)
            or (>= 0xFE70 and <= 0xFEFF)
            or (>= 0x10800 and <= 0x10FFF)
            or (>= 0x1E800 and <= 0x1EFFF);

    private static int CodePointAt(string text, int index, out int units)
    {
        if (index < text.Length - 1 && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]))
        {
            units = 2;
            return char.ConvertToUtf32(text[index], text[index + 1]);
        }

        units = 1;
        return index < text.Length ? text[index] : 0;
    }

    private sealed record RunSpan(int Start, int Length, SKTypeface Typeface, bool RightToLeft);

    private sealed record ShapedRun(SKTypeface Typeface, ushort[] Glyphs, SKPoint[] Points, float Width, bool RightToLeft);

    private sealed record ShapedWord(IReadOnlyList<ShapedRun> Runs, float Width, int SpacesBefore);

    /// <summary>A line item: a shaped run, or (Run == null) the space between two words.</summary>
    private sealed record Segment(ShapedRun? Run, float Width, int Level);

    private sealed class Session : IDisposable
    {
        private readonly SKTypeface _primary;
        private readonly float _fontSize;
        private readonly Func<int, SKTypeface?> _resolveFallback;
        private readonly List<SKTypeface> _fallbacks = new();
        private readonly HashSet<int> _unresolvable = new();
        private readonly Dictionary<SKTypeface, (SKShaper Shaper, SKFont Font)> _shapers =
            new(ReferenceEqualityComparer.Instance);

        internal Session(SKTypeface primary, float fontSize, Func<int, SKTypeface?> resolveFallback)
        {
            _primary = primary;
            _fontSize = fontSize;
            _resolveFallback = resolveFallback;
        }

        internal bool TypesetParagraph(string paragraph, float maxLineWidth, List<TypesetLine> lines, out string? failure)
        {
            failure = null;
            bool rtl = IsRightToLeftParagraph(paragraph);

            var words = new List<ShapedWord>();
            int spaces = 0;
            for (int i = 0; i < paragraph.Length;)
            {
                if (paragraph[i] == ' ')
                {
                    spaces++;
                    i++;
                    continue;
                }

                int start = i;
                while (i < paragraph.Length && paragraph[i] != ' ')
                    i++;

                var word = ShapeWord(paragraph.Substring(start, i - start), rtl, spaces, out failure);
                if (word == null)
                    return false;
                words.Add(word);
                spaces = 0;
            }

            if (words.Count == 0)
            {
                lines.Add(new TypesetLine(Array.Empty<GlyphRun>(), 0f, rtl));
                return true;
            }

            float spaceWidth = SpaceWidth();
            var current = new List<ShapedWord>();
            float width = 0f;
            foreach (var word in words)
            {
                float gap = current.Count == 0 ? 0f : word.SpacesBefore * spaceWidth;
                if (current.Count > 0 && width + gap + word.Width > maxLineWidth)
                {
                    lines.Add(Layout(current, rtl, spaceWidth));
                    current.Clear();
                    width = 0f;
                    gap = 0f;
                }

                current.Add(word);
                width += gap + word.Width;
            }

            lines.Add(Layout(current, rtl, spaceWidth));
            return true;
        }

        private static TypesetLine Layout(List<ShapedWord> words, bool rtl, float spaceWidth)
        {
            int paragraphLevel = rtl ? 1 : 0;
            var segments = new List<Segment>();
            for (int w = 0; w < words.Count; w++)
            {
                if (w > 0 && words[w].SpacesBefore > 0)
                    segments.Add(new Segment(null, words[w].SpacesBefore * spaceWidth, -1));

                foreach (var run in words[w].Runs)
                {
                    // An RTL run sits at level 1. An LTR run sits at 0 in an LTR
                    // paragraph and at 2 in an RTL one, so L2 reverses it twice
                    // and it keeps its logical order.
                    int level = run.RightToLeft ? 1 : (rtl ? 2 : 0);
                    segments.Add(new Segment(run, run.Width, level));
                }
            }

            // A space between two words takes their level when they agree,
            // otherwise the paragraph's (a simplified UBA N1/N2). Every word has
            // at least one run, so a gap's neighbours are always runs.
            for (int s = 0; s < segments.Count; s++)
            {
                if (segments[s].Level >= 0)
                    continue;
                int before = s > 0 ? segments[s - 1].Level : paragraphLevel;
                int after = s + 1 < segments.Count ? segments[s + 1].Level : paragraphLevel;
                segments[s] = segments[s] with { Level = before == after ? before : paragraphLevel };
            }

            // UBA L2: from the highest level down to 1, reverse every maximal
            // sequence at or above that level.
            int maxLevel = 0;
            foreach (var segment in segments)
                maxLevel = Math.Max(maxLevel, segment.Level);
            for (int level = maxLevel; level >= 1; level--)
            {
                int k = 0;
                while (k < segments.Count)
                {
                    if (segments[k].Level < level)
                    {
                        k++;
                        continue;
                    }

                    int end = k;
                    while (end < segments.Count && segments[end].Level >= level)
                        end++;
                    segments.Reverse(k, end - k);
                    k = end;
                }
            }

            // HarfBuzz already returns an RTL run's glyphs in visual order, so a
            // run is placed as-is at the running x.
            var runs = new List<GlyphRun>();
            float x = 0f;
            foreach (var segment in segments)
            {
                if (segment.Run is { } run)
                {
                    var positions = new SKPoint[run.Points.Length];
                    for (int g = 0; g < positions.Length; g++)
                        positions[g] = new SKPoint(x + run.Points[g].X, run.Points[g].Y);
                    runs.Add(new GlyphRun(run.Typeface, run.Glyphs, positions, run.RightToLeft));
                }

                x += segment.Width;
            }

            return new TypesetLine(runs, x, rtl);
        }

        private ShapedWord? ShapeWord(string word, bool paragraphRtl, int spacesBefore, out string? failure)
        {
            var spans = Itemize(word, paragraphRtl, out failure);
            if (spans == null)
                return null;

            var runs = new List<ShapedRun>(spans.Count);
            float width = 0f;
            foreach (var span in spans)
            {
                var run = ShapeRun(word.Substring(span.Start, span.Length), span.Typeface, span.RightToLeft, out failure);
                if (run == null)
                    return null;
                runs.Add(run);
                width += run.Width;
            }

            return new ShapedWord(runs, width, spacesBefore);
        }

        private List<RunSpan>? Itemize(string word, bool paragraphRtl, out string? failure)
        {
            failure = null;
            var spans = new List<RunSpan>();
            bool wordDirection = FirstStrongDirection(word) ?? paragraphRtl;
            SKTypeface? runFace = null;
            bool runRtl = wordDirection;
            int runStart = 0;

            for (int i = 0; i < word.Length;)
            {
                int cp = CodePointAt(word, i, out int units);
                var kind = Classify(cp);
                SKTypeface? face;
                bool rtl;

                switch (kind)
                {
                    case CharClass.StrongRtl:
                    case CharClass.StrongLtr:
                        rtl = kind == CharClass.StrongRtl;
                        face = FaceFor(cp);
                        break;
                    case CharClass.Number:
                        rtl = false;
                        face = FaceFor(cp);
                        break;
                    case CharClass.Attached:
                        rtl = runFace != null ? runRtl : wordDirection;
                        face = runFace ?? FaceFor(cp);
                        break;
                    case CharClass.Ignorable:
                        rtl = runFace != null ? runRtl : wordDirection;
                        face = runFace ?? _primary;
                        break;
                    default: // Neutral
                        rtl = runFace != null ? runRtl : wordDirection;
                        face = runFace != null && Covers(runFace, cp) ? runFace : FaceFor(cp);
                        break;
                }

                if (face == null)
                {
                    failure = $"no available font covers U+{cp:X4}";
                    return null;
                }

                if (runFace != null && (!ReferenceEquals(face, runFace) || rtl != runRtl))
                {
                    spans.Add(new RunSpan(runStart, i - runStart, runFace, runRtl));
                    runStart = i;
                }

                runFace = face;
                runRtl = rtl;
                i += units;
            }

            if (runFace != null)
                spans.Add(new RunSpan(runStart, word.Length - runStart, runFace, runRtl));
            return spans;
        }

        /// <summary>The <c>/DA</c> typeface first, then a fallback already found in this call, then the resolver.</summary>
        private SKTypeface? FaceFor(int cp)
        {
            if (Covers(_primary, cp))
                return _primary;

            foreach (var fallback in _fallbacks)
            {
                if (Covers(fallback, cp))
                    return fallback;
            }

            if (_unresolvable.Contains(cp))
                return null;

            var resolved = _resolveFallback(cp);
            if (resolved != null && Covers(resolved, cp))
            {
                _fallbacks.Add(resolved);
                return resolved;
            }

            _unresolvable.Add(cp);
            return null;
        }

        private static bool Covers(SKTypeface typeface, int cp) => typeface.GetGlyph(cp) != 0;

        private ShapedRun? ShapeRun(string text, SKTypeface face, bool rtl, out string? failure)
        {
            failure = null;
            var (shaper, font) = ShaperFor(face);

            using var buffer = new HbBuffer();
            buffer.AddUtf16(text);
            // Script and language come from the text. The direction is set
            // explicitly because it was already resolved against the paragraph.
            buffer.GuessSegmentProperties();
            buffer.Direction = rtl ? HbDirection.RightToLeft : HbDirection.LeftToRight;

            var result = shaper.Shape(buffer, 0f, 0f, font);
            var codepoints = result.Codepoints;
            var glyphs = new ushort[codepoints.Length];
            for (int g = 0; g < codepoints.Length; g++)
            {
                uint gid = codepoints[g];
                if (gid > ushort.MaxValue)
                {
                    failure = $"glyph id {gid} from {face.FamilyName} does not fit a ushort";
                    return null;
                }

                if (gid == 0)
                {
                    int cluster = (int)result.Clusters[g];
                    int cp = CodePointAt(text, Math.Clamp(cluster, 0, Math.Max(text.Length - 1, 0)), out _);
                    if (Classify(cp) != CharClass.Ignorable)
                    {
                        failure = $"the shaper produced .notdef for U+{cp:X4} in {face.FamilyName}";
                        return null;
                    }
                }

                glyphs[g] = (ushort)gid;
            }

            return new ShapedRun(face, glyphs, result.Points, result.Width, rtl);
        }

        private (SKShaper Shaper, SKFont Font) ShaperFor(SKTypeface face)
        {
            if (_shapers.TryGetValue(face, out var entry))
                return entry;

            var shaper = new SKShaper(face);
            var font = new SKFont(face, _fontSize) { LinearMetrics = true, Subpixel = true };
            entry = (shaper, font);
            _shapers[face] = entry;
            return entry;
        }

        private float SpaceWidth()
        {
            if (!Covers(_primary, ' '))
                return _fontSize * 0.25f;

            using var font = new SKFont(_primary, _fontSize) { LinearMetrics = true, Subpixel = true };
            using var paint = new SKPaint();
            var width = font.MeasureText(" ", paint);
            return width > 0f ? width : _fontSize * 0.25f;
        }

        public void Dispose()
        {
            foreach (var (shaper, font) in _shapers.Values)
            {
                shaper.Dispose();
                font.Dispose();
            }

            _shapers.Clear();
        }
    }
}
