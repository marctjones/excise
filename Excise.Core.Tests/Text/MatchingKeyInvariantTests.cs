using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// The structural promises of the matching key behind case folding and ignore-accents,
/// and its failure modes on hostile text: a lone surrogate, a term of nothing but
/// accents, a run of ten thousand combining marks, a match that starts or ends inside a
/// fold expansion. The example tests in <see cref="AccentMatchingTests"/> and
/// <see cref="FullCaseFoldingTests"/> pin spellings; these pin the rules every spelling
/// depends on, so a new fold or a changed strip range cannot break offsets silently
/// (a wrong offset in redaction removes the wrong glyphs).
/// </summary>
public class MatchingKeyInvariantTests
{
    private static readonly SeparatorMatching Default = SeparatorMatching.Default;
    private static readonly SeparatorMatching Exact = Default with { IgnoreAccents = false };

    // Text a PDF can hand the matcher: expansions, precomposed and decomposed accents,
    // astral letters, ligatures, lone surrogates, accent-only runs, empty.
    private static readonly string[] Nasty =
    [
        "", " ", "a", "A", "é", "e\u0301", "E\u0301\u0302", "\u0301", "\u0301\u0301e", "e\u0301\u0301\u0301",
        "Straße", "STRAẞE", "ǰ", "ŉ", "ᾳ", "ﬃ", "İstanbul", "ıstanbul", "K", "ſ",
        "\U00010400", "\U00010428x", "\uD800", "\uDC00", "a\uD800", "\uD800a", "a\uDC00b", "😀",
        "क़ानून", "ก่า", "각", "が", "שָׁלוֹם", "كَتَبَ", "Ёлка", "ἀλήθεια", "Phở", "Ångström",
        "naïve café résumé", "e\u0301 cafe\u0301", "\u0301cafe", "x\u0301\u0301\u0301",
    ];

    private static IEnumerable<string> RandomText(int count, int seed)
    {
        var rng = new Random(seed);
        // Pieces chosen to hit every branch of the key: ASCII, Latin-1, a mark, an
        // expansion, a surrogate half, an astral pair, an unstripped mark.
        string[] pieces = ["a", "E", "z", " ", "-", "é", "Ö", "\u0301", "\u0308", "ß", "ﬁ", "İ", "\uD800", "\uDC00",
            "\U00010400", "\U0001F600", "क़", "ก่", "ø", "Ё"];
        for (var n = 0; n < count; n++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 24);
            for (var i = 0; i < len; i++) sb.Append(pieces[rng.Next(pieces.Length)]);
            yield return sb.ToString();
        }
    }

    public static TheoryData<bool, bool> Rules() => new()
    {
        { false, false }, { true, false }, { false, true }, { true, true },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void OfAndMap_AgreeOnTheKey_OnEveryText(bool foldCase, bool ignoreAccents)
    {
        foreach (var text in AllTexts())
            MatchingKey.Map(text, foldCase, ignoreAccents).Key
                .Should().Be(MatchingKey.Of(text, foldCase, ignoreAccents), $"for '{Escape(text)}'");
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void Map_GivesEachKeyCharacterAValidMonotoneSourceOnACodePointBoundary(bool foldCase, bool ignoreAccents)
    {
        foreach (var text in AllTexts())
        {
            var (key, source) = MatchingKey.Map(text, foldCase, ignoreAccents);
            source.Length.Should().Be(key.Length, $"one source index per key char of '{Escape(text)}'");
            var previous = -1;
            foreach (var at in source)
            {
                at.Should().BeInRange(0, Math.Max(text.Length - 1, 0));
                at.Should().BeGreaterThanOrEqualTo(previous, "source indexes never run backwards");
                if (at > 0 && char.IsLowSurrogate(text[at]))
                    char.IsHighSurrogate(text[at - 1]).Should().BeFalse("a source index never splits a surrogate pair");
                previous = at;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void TheKey_IsIdempotent_AndNeverLongerThanTheFoldExpansionAllows(bool foldCase, bool ignoreAccents)
    {
        foreach (var text in AllTexts())
        {
            var key = MatchingKey.Of(text, foldCase, ignoreAccents);
            MatchingKey.Of(key, foldCase, ignoreAccents).Should().Be(key, $"keying twice changes nothing for '{Escape(text)}'");
            key.Length.Should().BeLessThanOrEqualTo(text.Length * 3 + 3, "the longest full case fold is three code units");
        }
    }

    [Fact]
    public void NoRule_ReturnsTheSameInstance_AndNullOrEmptyAreSafe()
    {
        var text = new string("Straße é".ToCharArray());
        ReferenceEquals(MatchingKey.Of(text, foldCase: false, ignoreAccents: false), text).Should().BeTrue();
        MatchingKey.Of("", true, true).Should().BeEmpty();
        MatchingKey.Of(null!, true, true).Should().BeNull();
        MatchingKey.Map("", true, true).Key.Should().BeEmpty();
    }

    [Fact]
    public void ALoneSurrogate_NeverThrows_AndKeysAsItself()
    {
        foreach (var lone in new[] { "\uD800", "\uDC00", "a\uD800", "\uD800a", "\uDBFF\uDBFF", "\uDC00\uD800" })
        foreach (var (fold, accents) in new[] { (true, true), (true, false), (false, true) })
        {
            var act = () => MatchingKey.Map(lone, fold, accents);
            act.Should().NotThrow($"'{Escape(lone)}' is malformed text a PDF can produce");
            MatchingKey.Map(lone, fold, accents).Key.Should().Be(lone, "an unpaired surrogate is kept, not dropped or replaced");
        }
        TermMatch.IndexOf("x \uD800 y", "y", false, true, 0, Default).Should().Be(4);
        TermMatch.IndexOf("x \uD800 y", "\uD800", false, false, 0, Default).Should().Be(2);
    }

    [Theory]
    [InlineData("\u0301")]
    [InlineData("\u0301\u0302\u0308")]
    [InlineData("\u1AB0")]
    [InlineData("\u1DC0")]
    [InlineData("\uFE20")]
    public void ATermOfNothingButAccents_MatchesNothing_NotEverything(string term)
    {
        // Its key is empty. An empty needle matching at every position would redact the
        // whole page (or, treated as "nothing to do", report success having removed
        // nothing): neither is acceptable, so it finds nothing.
        TermMatch.IndexOf("le café e\u0301 noir", term, false, false, 0, Default).Should().Be(-1);
        TermMatch.IndexOf("le café e\u0301 noir", term, true, true, 0, Default).Should().Be(-1);
        TermMatch.Needle(term, false, Default).Should().BeEmpty();
        TermMatch.Mask("le café", [term], false, false, Default).Should().BeNull("nothing to cut: no value is changed");
    }

    [Fact]
    public void ATermOfNothingButAccents_StillMatchesItself_WhenAccentsAreExact()
    {
        TermMatch.IndexOf("le e\u0301 noir", "\u0301", false, false, 0, Exact).Should().Be(4);
    }

    [Theory]
    [InlineData("le café noir", "cafe", 3, 4)]
    [InlineData("le cafe\u0301 noir", "cafe", 3, 5)]
    [InlineData("le cafe\u0301\u0302 noir", "cafe", 3, 6)]
    [InlineData("le Straße noir", "STRASSE", 3, 6)]
    [InlineData("le STRASSE noir", "straße", 3, 7)]
    [InlineData("le \U00010400x noir", "\U00010428X", 3, 3)]
    [InlineData("le ﬃ noir", "ffi", 3, 1)]
    [InlineData("le İ noir", "i", 3, 1)]
    public void TheMatch_MapsBackToTheTextsOwnCharacters(string text, string term, int at, int length)
    {
        var found = TermMatch.IndexOf(text, term, false, false, 0, Default, out var foundLength);
        found.Should().Be(at);
        foundLength.Should().Be(length, "the length is the text's, not the key's or the term's");
        text.Substring(found, foundLength).Should().Be(text.Substring(at, length));
    }

    [Fact]
    public void WholeWord_TreatsAPrecomposedAccentBeforeTheTermAsPartOfTheWord()
    {
        // "écafe" is one word: whole-word "cafe" must not find it.
        TermMatch.IndexOf("x \u00E9cafe y", "cafe", false, wholeWord: true, 0, Default).Should().Be(-1);
        TermMatch.IndexOf("x \u00E9cafe y", "cafe", false, wholeWord: false, 0, Default).Should().Be(3,
            "without whole-word it is a substring hit");
    }

    [Fact(Skip = "#2068: TermMatch.IsWordChar reads a combining mark as a boundary, so the decomposed spelling still matches")]
    public void WholeWord_TreatsADecomposedAccentBeforeTheTermAsPartOfTheWord()
    {
        // The same word with the é stored as e + U+0301 (NFD, as many macOS and LaTeX
        // producers write it) must read the same, or search and redaction treat one word
        // two ways depending only on the producer's encoding.
        TermMatch.IndexOf("x e\u0301cafe y", "cafe", false, wholeWord: true, 0, Default).Should().Be(-1);
    }

    [Fact(Skip = "#2068: the page-letter matcher shares TermMatch.IsWordChar")]
    public void PageMatcher_WholeWord_TreatsADecomposedAccentLetterBeforeTheTermAsPartOfTheWord()
    {
        var letters = new List<Excise.Core.Text.Letter>();
        var x = 60.0;
        foreach (var c in "xe\u0301cafe y")
        {
            letters.Add(new Excise.Core.Text.Letter(c.ToString(), new Excise.Core.Document.PdfRectangle(x, 700, x + 6, 712),
                12, "F1", x, 700, 6, c));
            x += 6;
        }
        Excise.Core.Text.Segmentation.PdfDocumentRedactionExtensions
            .FindTextMatches(letters, "cafe", caseSensitive: false, wholeWord: true, separators: Default)
            .Should().BeEmpty("cafe is the tail of the word xécafe");
    }

    [Fact]
    public void WholeWord_StillFindsATermBesideAnAccentedSeparatorFreeBoundary()
    {
        TermMatch.IndexOf("(cafe\u0301)", "cafe", false, true, 0, Default).Should().Be(1);
        TermMatch.IndexOf("cafe\u0301 au lait", "cafe", false, true, 0, Default).Should().Be(0);
        TermMatch.IndexOf("cafe\u0301s", "cafe", false, true, 0, Default).Should().Be(-1, "cafés is a longer word");
    }

    [Fact]
    public void SequentialSearch_FindsEveryOccurrence_AndTerminates()
    {
        // The search loop advances from each hit; offsets that fail to advance (a
        // match that ends at its own start, or a start inside an expansion) hang.
        const string text = "Straße strasse STRASSE ss ß café cafe\u0301 CAFÉ";
        // ss and ß fold to the same key, so each finds all five: Straße, strasse, STRASSE, ss, ß.
        foreach (var (term, expected) in new[] { ("strasse", 3), ("ss", 5), ("cafe", 3), ("ß", 5) })
        {
            var hits = new List<int>();
            var start = 0;
            var guard = 0;
            while (guard++ < 100)
            {
                var at = TermMatch.IndexOf(text, term, false, false, start, Default, out var length);
                if (at < 0) break;
                length.Should().BeGreaterThan(0, "a hit has width, so the next search moves forward");
                hits.Add(at);
                start = at + length;
            }
            guard.Should().BeLessThan(100, $"'{term}' must terminate");
            hits.Count.Should().Be(expected, $"'{term}' occurrences in '{text}'");
        }
    }

    [Fact]
    public void AStartIndexInsideAnExpansionOrAnAccent_NeverReturnsAnOffsetBeforeIt()
    {
        const string text = "Straße";
        for (var start = 0; start <= text.Length; start++)
        {
            var at = TermMatch.IndexOf(text, "s", false, false, start, Default);
            if (at >= 0) at.Should().BeGreaterThanOrEqualTo(start, $"search from {start}");
        }
        const string accented = "cafe\u0301";
        for (var start = 0; start <= accented.Length; start++)
        {
            var at = TermMatch.IndexOf(accented, "e", false, false, start, Default);
            if (at >= 0) at.Should().BeGreaterThanOrEqualTo(start);
        }
        TermMatch.IndexOf(text, "s", false, false, text.Length + 5, Default).Should().Be(-1, "past the end finds nothing");
    }

    [Fact]
    public void CaseSensitive_StillIgnoresAccents_AndExactStillFoldsCase()
    {
        // The four cells of (case, accents) are independent.
        TermMatch.IndexOf("Café", "Cafe", caseSensitive: true, false, 0, Default).Should().Be(0);
        TermMatch.IndexOf("Café", "cafe", caseSensitive: true, false, 0, Default).Should().Be(-1);
        TermMatch.IndexOf("Café", "cafe", caseSensitive: false, false, 0, Exact).Should().Be(-1);
        TermMatch.IndexOf("Café", "café", caseSensitive: false, false, 0, Exact).Should().Be(0);
        TermMatch.IndexOf("Café", "Café", caseSensitive: true, false, 0, Exact).Should().Be(0);
        TermMatch.IndexOf("Cafe\u0301", "Café", caseSensitive: true, false, 0, Exact).Should().Be(-1,
            "exact and case-sensitive compares code points; a decomposed é is not a precomposed one here");
    }

    [Theory]
    [InlineData("OPEN-SOURCE café", "open source CAFE")]
    [InlineData("Open–Source CAFÉ", "open-source cafe")]
    [InlineData("ﬁne Straße-Nord", "FINE strasse nord")]
    public void CaseAccentsAndSeparators_ComposeInOneTerm(string text, string term)
    {
        TermMatch.IndexOf(MatchingNormalization.Fold(text), MatchingNormalization.Fold(term).Trim(), false, false, 0, Default)
            .Should().BeGreaterThanOrEqualTo(0, $"'{term}' should find '{text}'");
    }

    [Fact]
    public void KeepingIndicAndHangulMarks_IsNotUndoneByTheCaseFold()
    {
        // Folding runs before stripping; neither may reach the marks of other scripts.
        TermMatch.IndexOf("क़ानून", "कानून", false, false, 0, Default).Should().Be(-1);
        TermMatch.IndexOf("각", "가", false, false, 0, Default).Should().Be(-1);
        TermMatch.IndexOf("ก่า", "กา", true, false, 0, Default).Should().Be(-1);
    }

    [Fact]
    public void HostileMarkRuns_AreLinear_NotQuadratic()
    {
        // Zalgo text: one letter under ten thousand marks, repeated. A matcher that
        // re-scans the marks per candidate hangs the app (CLAUDE.md: PDF operations on
        // malformed input can hang). Generous bound; the real cost is milliseconds.
        var zalgo = "e" + new string('\u0301', 10_000);
        var text = string.Concat(System.Linq.Enumerable.Repeat(zalgo, 8)) + " cafe";
        var sw = Stopwatch.StartNew();
        TermMatch.IndexOf(text, "cafe", false, true, 0, Default).Should().Be(text.Length - 4);
        TermMatch.IndexOf(text, "e" + "x", false, false, 0, Default).Should().Be(-1);
        TermMatch.Mask(text, ["cafe"], false, true, Default).Should().NotContain("cafe");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ALongTextOfMatches_ComesBackInLinearTime()
    {
        var text = string.Concat(System.Linq.Enumerable.Repeat("Café ", 20_000));
        var sw = Stopwatch.StartNew();
        var count = 0;
        var start = 0;
        while (true)
        {
            var at = TermMatch.IndexOf(text, "cafe", false, true, start, Default, out var length);
            if (at < 0) break;
            count++;
            start = at + length;
        }
        count.Should().Be(20_000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the key of a text is built once per rule, not once per hit");
    }

    private static IEnumerable<string> AllTexts()
    {
        foreach (var t in Nasty) yield return t;
        foreach (var t in RandomText(400, seed: 20261013)) yield return t;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(c < 0x80 && !char.IsControl(c) ? c.ToString() : $"\\u{(int)c:X4}");
        return sb.ToString();
    }
}
