using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// The #980 differential gate.
///
/// <para><see cref="ContentStreamParser"/> and <see cref="TextExtractor"/> are
/// two independently-maintained state machines that must agree about the same
/// content stream. A correctness fix has landed in one and not the other four
/// times now — §9.4.2 line stepping (#942/#899), the array nesting bound
/// (#971), #352's hex-digit skip (#974), and #833's glyph-cell vector
/// transform. Every existing gate misses this because BOTH parsers are
/// self-consistent: each has its own passing tests, and they simply disagree
/// with each other.</para>
///
/// <para>These tests feed the SAME bytes to both and assert agreement on what
/// can be compared. Fixed inputs, no randomness.</para>
///
/// <para><b>What this gate deliberately does NOT compare</b>, because the two
/// legitimately do different jobs — one builds letters, the other builds
/// operator bounds. Do NOT "fix" these into agreement; that would be the
/// regression:</para>
/// <list type="bullet">
/// <item>Form XObjects. TextExtractor recurses through <c>Do</c> (bounded at
/// 64, with cycle detection); ContentStreamParser treats it as a no-op because
/// redaction reaches form content via FormXObjectFlattener instead.</item>
/// <item>Hidden optional content. TextExtractor resolves /OC visibility and
/// flags letters; ContentStreamParser is layer-blind, because redaction must
/// remove hidden text too.</item>
/// <item>Clipping and ExtGState. ContentStreamParser tracks W/W* and applies
/// <c>gs</c>; extraction has no use for either.</item>
/// <item>Inline-image PIXELS. ContentStreamParser captures them verbatim for a
/// lossless round-trip; TextExtractor only needs to skip them. The gate
/// compares where the two RESUME, not what they kept.</item>
/// <item>Dictionary operands. ContentStreamParser parses <c>&lt;&lt;…&gt;&gt;</c>
/// into an operand; TextExtractor skips it and lifts only /MCID (#776). So
/// operand LISTS are never compared — only the state the operators produce.
/// This also makes dictionary nesting a deliberate failure-mode difference;
/// see <see cref="DeepDictionaryNesting_IsADeliberateFailureModeDifference"/>.</item>
/// <item>Unicode decoding beyond the WinAnsi core. TextExtractor has an
/// eight-step cascade (/Differences, MacRoman, embedded reverse cmap, Mac
/// glyph order, symbol cmap); ContentStreamParser has three. Tracked as
/// #981 — the fixtures here stay inside the codes both decode alike.</item>
/// <item>Operand LISTS, and therefore the <c>true</c>/<c>false</c>/<c>null</c>
/// literals: ContentStreamParser pushes them as PdfBoolean/PdfNull operands
/// where TextExtractor keeps the raw token string. Untested rather than
/// deliberate — on malformed input the two can still resolve a different
/// operand for the same operator.</item>
/// <item>Bidi. ExtractLetters reorders visual-order RTL runs (#632) and
/// ContentStreamParser does not, so every fixture here is LTR.</item>
/// </list>
///
/// <para><b>What a differential structurally CANNOT catch, and where that is
/// covered instead.</b> Two machines that are wrong the SAME way agree, and
/// agreement is all this file measures. #983 was exactly that: neither
/// restored the §8.4.1 Table 52 text state at <c>Q</c>, every fixture here
/// passed, and the third state machine
/// (<c>GlyphRemover.TextStateTracker</c>) was the odd one out precisely
/// because it was correct. Shared defects therefore need a gate that compares
/// against something other than the twin — a spec property
/// (<see cref="GraphicsStateTextParameterTests"/>) or an independent
/// implementation
/// (<see cref="GraphicsStateTextParameterOracleTests"/>, mutool). Do not
/// answer a "both parsers are wrong" report by adding a fixture here.</para>
///
/// <para><b>Why the renderer is not in this gate.</b> There are FOUR content
/// state machines, not two: <c>SkiaRenderer</c> keeps its own text state and
/// re-executes operators under it (#598), and
/// <c>GlyphRemover.TextStateTracker</c> tracks the text-state operators it
/// re-emits. The renderer is left out because its divergences are not silent —
/// the reference-renderer differentials see them in pixels — and because
/// changing it moves the corpus expectation manifests. It is NOT left out
/// because it is correct: it shares #983's defect verbatim (its
/// <c>GraphicsState.Clone()</c> omits <c>TextState</c> and
/// <c>SaveState</c>/<c>RestoreState</c> never touch <c>_textState</c>),
/// tracked as #986.</para>
/// </summary>
public class ParserDifferentialTests
{
    // ---------------------------------------------------------------
    // Text-state arithmetic: the two must put the same glyph in the same
    // place. Compared through the only observable both expose — the
    // operator's bounding box against the union of that operator's letters.
    // Every fixture is a single text-showing operator under an identity CTM.
    // ---------------------------------------------------------------

    [Theory]
    // Baseline: unscaled matrix, the case that already agreed.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj ET")]
    // #833's class: unit font size with the size carried by the text matrix.
    // ContentStreamParser added raw text-space scalars to a user-space corner,
    // so its box was 12x too small in both axes.
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm (Hello) Tj ET")]
    // #942's class: line stepping must compose through the matrix.
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 1 TL (a) Tj T* (b) Tj ET")]
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 2 -3 Td (Hello) Tj ET")]
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 2 -3 TD (Hello) Tj T* (World) Tj ET")]
    // Flipped matrix — the shape that marched letters off-page in #899.
    [InlineData("BT /F1 1 Tf 12 0 0 -12 72 700 Tm 1 TL (a) Tj T* (b) Tj ET")]
    // Rotated matrix: both must take the axis-aligned extent of the cell.
    [InlineData("BT /F1 12 Tf 0.7071 0.7071 -0.7071 0.7071 300 400 Tm (Hi) Tj ET")]
    // Text rise, composed through the matrix (§9.4.4).
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 6 Ts (Hi) Tj ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm -3 Ts (Hi) Tj ET")]
    // Spacing terms sit INSIDE the horizontal-scaling factor (§9.4.4, #734).
    [InlineData("BT /F1 12 Tf 200 Tz 1 0 0 1 72 700 Tm (Hello) Tj ET")]
    [InlineData("BT /F1 12 Tf 2 Tc 1 0 0 1 72 700 Tm (Hello) Tj ET")]
    // Word spacing fires on the single-byte code 32 only (§9.3.3).
    [InlineData("BT /F1 12 Tf 10 Tw 1 0 0 1 72 700 Tm (a b c) Tj ET")]
    // TJ kerning, ints and reals, applied against the writing direction.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm [(Ke) -120 (rn) 250.5 (ed)] TJ ET")]
    // ' and " carry an implicit line step plus (for ") Tw/Tc.
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 1 TL (a) Tj (b) ' ET")]
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 1 TL (a) Tj 5 2 (b) \" ET")]
    // q/Q around a cm: both must restore the CTM.
    [InlineData("BT ET q 2 0 0 2 10 10 cm Q BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    // Unknown operator: its operands must not leak into the next operator.
    [InlineData("BT /F1 12 Tf /Sh0 sh 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    [InlineData("BT /F1 12 Tf 1 2 3 4 5 6 zzz 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    // Escapes: octal (including the >0377 overflow), line continuation,
    // nested parens, hex with whitespace and an odd digit count, and #352's
    // non-hex digit inside a hex string.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (A\\101\\377\\777B) Tj ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Wrap\\\nped (n) ok) Tj ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm <48 65 6C6C 6F5> Tj ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm <48ZZ65> Tj ET")]
    // /Widths absent: both must use the same standard-14 metrics.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Wig) Tj ET")]
    public void SameStream_TextOperatorBounds_MatchTheLettersTheyProduced(string content)
    {
        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        var operators = ParityFixture.ParseOperators(page);
        var letters = ParityFixture.ExtractLetters(page);

        var showOps = operators.Operators
            .Where(op => op.Name is "Tj" or "TJ" or "'" or "\"")
            .ToList();
        showOps.Should().NotBeEmpty("the fixture must actually show text");

        // Both machines walk the operators in stream order and emit glyphs in
        // string order, so the Nth show-operator owns a contiguous run of
        // letters starting where the previous one ended.
        int cursor = 0;
        for (int i = 0; i < showOps.Count; i++)
        {
            var op = showOps[i];
            var text = op.TextContent ?? "";
            var run = letters.Skip(cursor).Take(text.Length).ToList();
            cursor += text.Length;

            string.Concat(run.Select(l => l.Value)).Should().Be(text,
                $"operator {i} ({op.Name}) must decode to the same characters " +
                "TextExtractor turned into letters");

            if (run.Count == 0)
            {
                op.BoundingBox.Should().BeNull(
                    $"operator {i} ({op.Name}) produced no letters");
                continue;
            }

            op.BoundingBox.Should().NotBeNull($"operator {i} ({op.Name}) showed text");
            var box = op.BoundingBox!.Value;

            box.Left.Should().BeApproximately(run.Min(l => l.GlyphRectangle.Left), 1e-6,
                $"operator {i} ({op.Name}) left edge");
            box.Bottom.Should().BeApproximately(run.Min(l => l.GlyphRectangle.Bottom), 1e-6,
                $"operator {i} ({op.Name}) bottom edge");
            box.Right.Should().BeApproximately(run.Max(l => l.GlyphRectangle.Right), 1e-6,
                $"operator {i} ({op.Name}) right edge");
            box.Top.Should().BeApproximately(run.Max(l => l.GlyphRectangle.Top), 1e-6,
                $"operator {i} ({op.Name}) top edge");
        }

        cursor.Should().Be(letters.Count,
            "every letter must belong to a text-showing operator — a mismatch " +
            "means one machine saw glyphs the other did not");
    }

    /// <summary>
    /// The pen position after each text-showing operator, which is the state
    /// the NEXT operator inherits. Compared through the start position of the
    /// first letter of each run: if either machine's Td/TD/T*/'/"/TJ/glyph
    /// arithmetic drifts, the runs separate even when each operator's own box
    /// is internally consistent.
    /// </summary>
    [Theory]
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 1 TL (a) Tj T* (b) Tj T* (c) Tj ET")]
    [InlineData("BT /F1 1 Tf 12 0 0 12 72 700 Tm 1 TL (a) Tj (b) ' (c) ' ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (ab) Tj (cd) Tj 3 -4 Td (ef) Tj ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm [(ab) -500 (cd)] TJ (ef) Tj ET")]
    [InlineData("BT /F1 1 Tf 12 0 0 -12 72 700 Tm 1 TL (a) Tj T* (b) Tj ET")]
    public void SameStream_PenPositionAfterEachOperator_Agrees(string content)
    {
        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        var operators = ParityFixture.ParseOperators(page);
        var letters = ParityFixture.ExtractLetters(page);

        int cursor = 0;
        foreach (var op in operators.Operators.Where(o => o.Name is "Tj" or "TJ" or "'" or "\""))
        {
            var text = op.TextContent ?? "";
            if (text.Length == 0) continue;

            // Under an identity CTM the captured text matrix's translation IS
            // the user-space pen, which is where the first letter starts.
            op.TextTransform.Should().NotBeNull();
            var first = letters[cursor];
            op.TextTransform!.Value.E.Should().BeApproximately(first.StartX, 1e-6,
                $"pen X entering {op.Name}");
            op.TextTransform!.Value.F.Should().BeApproximately(first.StartY, 1e-6,
                $"pen Y entering {op.Name}");
            cursor += text.Length;
        }

        cursor.Should().Be(letters.Count);
    }

    // ---------------------------------------------------------------
    // Operator coverage.
    // ---------------------------------------------------------------

    /// <summary>
    /// The two operator tables must be the SAME SET. An operator missing from
    /// one is not inert there: an unrecognised token is not an operator
    /// boundary, so its operands survive into the next real operator — which
    /// is how `/Sh0 sh` moved every subsequent letter to (0, 1) (#980).
    ///
    /// Read from the fields themselves rather than probed through behaviour:
    /// several operators legitimately CHANGE the state a behavioural probe
    /// would measure (Ts moves the baseline, Tz the width, BI consumes the
    /// stream), so probing has false negatives exactly where coverage matters.
    /// Compared in both directions — "one knows an operator the other does
    /// not" is the defect regardless of which one.
    /// </summary>
    [Fact]
    public void OperatorSets_AreIdenticalInBothMachines()
    {
        var fromParser = ParityFixture.OperatorSet(typeof(ContentStreamParser));
        var fromExtractor = ParityFixture.OperatorSet(typeof(TextExtractor));

        fromParser.Should().NotBeEmpty();
        fromExtractor.Should().NotBeEmpty();

        fromParser.Except(fromExtractor).Should().BeEmpty(
            "ContentStreamParser recognises operators TextExtractor does not — " +
            "TextExtractor will read their operands as the next operator's");
        fromExtractor.Except(fromParser).Should().BeEmpty(
            "TextExtractor recognises operators ContentStreamParser does not");
    }

    /// <summary>
    /// The shared table must actually cover ISO 32000-2's operator set. Set
    /// EQUALITY alone is satisfied by two empty tables, so the gate above
    /// cannot notice an operator missing from BOTH.
    /// </summary>
    [Fact]
    public void OperatorSets_CoverTheSpecOperators()
    {
        string[] spec =
        {
            "q", "Q", "cm", "w", "J", "j", "M", "d", "ri", "i", "gs",
            "m", "l", "c", "v", "y", "h", "re",
            "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n", "W", "W*",
            "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts",
            "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "\"",
            "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k",
            "sh", "Do", "BI", "ID", "EI", "d0", "d1",
            "MP", "DP", "BMC", "BDC", "EMC", "BX", "EX",
        };

        ParityFixture.OperatorSet(typeof(ContentStreamParser)).Should().Contain(spec);
        ParityFixture.OperatorSet(typeof(TextExtractor)).Should().Contain(spec);
    }

    /// <summary>
    /// A Type0 font whose <c>/DescendantFonts</c> is an INDIRECT REFERENCE —
    /// which is what real producers emit. ContentStreamParser tested the raw
    /// value with a bare <c>is PdfArray</c> cast, so the reference never
    /// resolved, the CID /W table never loaded, and every CID glyph got a
    /// default width while TextExtractor (which resolves it) used the real
    /// table. The two then disagreed about where every CID glyph's cell is,
    /// and redaction destroyed text remote from any match on real government
    /// forms. The #843 mistake, one array over (#980).
    /// </summary>
    [Theory]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm <00480065006C> Tj ET")]
    [InlineData("BT /F1 1 Tf 24 0 0 24 72 700 Tm <00480065006C> Tj ET")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm [<0048> -200 <0065006C>] TJ ET")]
    public void Type0IndirectDescendantWidths_AgreeInBothMachines(string content)
    {
        using var doc = PdfDocument.Open(ParityFixture.BuildType0(content));
        var page = doc.GetPage(1);

        var letters = ParityFixture.ExtractLetters(page);
        letters.Should().HaveCount(3);

        // The fixture's /W gives the three CIDs deliberately different widths
        // (1500, 250, 500), so a parser that missed the table — falling back to
        // /DW, /MissingWidth or a flat default — produces a visibly different
        // box rather than an accidentally-equal one.
        var op = ParityFixture.ParseOperators(page).Operators
            .Single(o => o.Name is "Tj" or "TJ");
        op.BoundingBox.Should().NotBeNull();
        var box = op.BoundingBox!.Value;

        box.Left.Should().BeApproximately(letters.Min(l => l.GlyphRectangle.Left), 1e-6);
        box.Right.Should().BeApproximately(letters.Max(l => l.GlyphRectangle.Right), 1e-6);
        box.Bottom.Should().BeApproximately(letters.Min(l => l.GlyphRectangle.Bottom), 1e-6);
        box.Top.Should().BeApproximately(letters.Max(l => l.GlyphRectangle.Top), 1e-6);

        // And the widths really are the table's, not a default.
        letters[0].Width.Should().BeGreaterThan(letters[1].Width * 3,
            "CID 0x48's /W width (1500) dwarfs 0x65's (250) — equal widths mean " +
            "the /W table was not read");
    }

    /// <summary>
    /// §7.2.3 Table 2's delimiters must end a name identically in both, or the
    /// same <c>/F1</c> resolves to a font in one machine and to a missing
    /// resource in the other. Form feed and the braces were missing from
    /// ContentStreamParser (#980).
    /// </summary>
    [Theory]
    [InlineData("BT /F1{} 12 Tf 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    [InlineData("BT /F1\f12 Tf 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    [InlineData("BT /F#31 12 Tf 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    public void NameDelimiters_EndANameIdenticallyInBothMachines(string content)
    {
        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        var tf = ParityFixture.ParseOperators(page).Operators.Single(op => op.Name == "Tf");
        (tf.Operands[0] as PdfName)!.Value.Should().Be("F1");

        // The extractor's name parse is what resolved the font: a name that
        // ended differently there would not find /F1 and the letters would
        // carry a different font name.
        var letters = ParityFixture.ExtractLetters(page);
        letters.Should().HaveCount(2);
        letters[0].FontName.Should().Be("F1");
        letters[0].FontSize.Should().Be(12);
    }

    /// <summary>
    /// An operator NEITHER machine implements must still terminate its
    /// operands in both (§7.8.2), or the next real operator reads them as its
    /// own. This is the class the missing `sh` belonged to; it stays broken
    /// for the next unknown operator unless the rule itself is pinned.
    /// </summary>
    [Fact]
    public void UnknownOperator_TerminatesItsOperandsInBothMachines()
    {
        const string content =
            "BT /F1 12 Tf 1 2 3 4 5 6 notAnOperator 1 0 0 1 72 700 Tm (Hi) Tj ET";

        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        var tm = ParityFixture.ParseOperators(page).Operators.Single(op => op.Name == "Tm");
        tm.Operands.Should().HaveCount(6, "the unknown operator's operands must not survive");

        var letters = ParityFixture.ExtractLetters(page);
        letters.Should().HaveCount(2);
        letters[0].StartX.Should().BeApproximately(72, 1e-9);
        letters[0].StartY.Should().BeApproximately(700, 1e-9);
    }

    // ---------------------------------------------------------------
    // Inline images: raw sample bytes must be skipped, not tokenised.
    // ---------------------------------------------------------------

    [Theory]
    // No /L, samples containing an unbalanced '(' and an operator-shaped run.
    [InlineData("q 10 0 0 10 0 0 cm BI /W 2 /H 2 /BPC 8 /CS /G ID \x01(Tj\x04 EI Q")]
    // Explicit /L, samples containing a literal " EI " that must NOT end it.
    [InlineData("q 10 0 0 10 0 0 cm BI /W 4 /H 1 /BPC 8 /CS /G /L 8 ID ab EI cd EI Q")]
    // Full-form keys, which normalize to the abbreviated ones.
    [InlineData("q 10 0 0 10 0 0 cm BI /Width 2 /Height 2 /BitsPerComponent 8 "
              + "/ColorSpace /G ID \x01\x02\x03\x04 EI Q")]
    public void InlineImageData_IsSkippedByBothMachines(string image)
    {
        var content =
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm (Before) Tj ET\n" + image +
            "\nBT /F1 12 Tf 1 0 0 1 72 600 Tm (After) Tj ET";

        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        var operators = ParityFixture.ParseOperators(page);
        operators.Operators.Select(o => o.Name).Should().Contain("BI");
        string.Concat(operators.Operators
                .Where(o => o.Name == "Tj")
                .Select(o => o.TextContent))
            .Should().Be("BeforeAfter");

        // The text AFTER the image is the assertion that matters: it is only
        // reachable if the sample bytes were skipped rather than tokenised.
        string.Concat(ParityFixture.ExtractLetters(page).Select(l => l.Value))
            .Should().Be("BeforeAfter");
    }

    // ---------------------------------------------------------------
    // Failure modes. Both must reject the same hostile input the same way —
    // and a raw CLR exception escaping either is a defect, not a rejection.
    // ---------------------------------------------------------------

    [Fact]
    public void DeepArrayNesting_IsRejectedByBothMachines_WithTheSameExceptionType()
    {
        // 300 > the 256 bound both declare. Deliberately NOT the ~5,000 that
        // reproduced #971's StackOverflow: that kills the test host instead of
        // failing a test, so it can never be a gate.
        var content = "BT /F1 12 Tf " + new string('[', 300) + new string(']', 300) + " ET";

        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        ParityFixture.Outcome(() => ParityFixture.ParseOperators(page))
            .Should().Be(nameof(PdfParseException));
        ParityFixture.Outcome(() => ParityFixture.ExtractLetters(page))
            .Should().Be(nameof(PdfParseException));
    }

    [Theory]
    // #352's hex-digit skip: letters G-Z are not hex digits and are ignored,
    // never fatal. This landed in ContentStreamParser first and escaped
    // page.Letters as a raw FormatException for as long as it took to notice.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm <48ZZ65> Tj ET")]
    // Unterminated constructs at end of stream.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (unterminated")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm <4865")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm [(a) 1")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm /")]
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (a\\")]
    // Malformed numbers, and a name whose #XX escape runs off the end.
    [InlineData("BT /F1 12 Tf --5 ..3 +- 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    [InlineData("BT /F#3 12 Tf 1 0 0 1 72 700 Tm (Hi) Tj ET")]
    // A BI with no EI anywhere: bounded, not a scan to infinity.
    [InlineData("BT /F1 12 Tf 1 0 0 1 72 700 Tm (a) Tj ET BI /W 1 /H 1 ID \x01\x02\x03")]
    public void MalformedInput_NeitherMachineThrowsARawClrException(string content)
    {
        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        // PdfParseException is a rejection. Anything else — FormatException,
        // IndexOutOfRangeException, ArgumentException — is the raw escape #974
        // was filed for.
        ParityFixture.Outcome(() => ParityFixture.ParseOperators(page))
            .Should().BeOneOf("ok", nameof(PdfParseException));
        ParityFixture.Outcome(() => ParityFixture.ExtractLetters(page))
            .Should().BeOneOf("ok", nameof(PdfParseException));
    }

    /// <summary>
    /// Both machines must honour a caller's cancellation. This could not be a
    /// parity case until #982: <c>ContentStreamParser.Parse</c> took a token
    /// (#346) and <c>TextExtractor</c> had none at all, so <c>page.Letters</c>
    /// ran to completion whatever the input cost — on the GUI's background
    /// indexing thread, with no timeout to hit.
    ///
    /// <para>Neither machine checks the token on ENTRY, so a pre-cancelled
    /// token proves a check inside the OPERATOR LOOP was reached — but only
    /// because the fixture below contains no array and no dictionary. The
    /// first draft used a <c>TJ</c> array and passed with the operator-loop
    /// check deleted, satisfied instead by the one in <c>ParseArray</c>: a
    /// check-point test is only as specific as its fixture is plain.</para>
    ///
    /// <para>What this does NOT gate: the two deeper check points (the
    /// array-element loop, and TextExtractor's <c>SkipDictionary</c> /
    /// ContentStreamParser's <c>ParseDictionary</c> walk). The outer loop's
    /// check necessarily fires first when the token is already cancelled, and
    /// cancellation arriving mid-token — the case those points exist for —
    /// cannot be produced deterministically from a test.</para>
    /// </summary>
    [Fact]
    public void CancellationRequested_IsHonouredByBothMachines()
    {
        // Long enough that abandoning it means something; NO array and NO
        // dictionary, so the operator loop is the only place a check can fire.
        var content = string.Concat(Enumerable.Repeat(
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm (abcd) Tj ET\n", 500));

        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The control: the same bytes complete when nothing is cancelled, so a
        // machine that threw for an unrelated reason cannot pass this.
        ParityFixture.Outcome(() => ParityFixture.ParseOperators(page)).Should().Be("ok");
        ParityFixture.Outcome(() => ParityFixture.ExtractLetters(page)).Should().Be("ok");

        new Action(() => new ContentStreamParser(page.GetContentStreamBytes(), page).Parse(cts.Token))
            .Should().Throw<OperationCanceledException>(
                "ContentStreamParser has taken a token since #346");
        new Action(() => new TextExtractor(page) { IncludeFormFieldValues = false }
                .ExtractLetters(cts.Token))
            .Should().Throw<OperationCanceledException>(
                "TextExtractor must honour one too — #982");
        new Action(() => page.GetLetters(cts.Token))
            .Should().Throw<OperationCanceledException>(
                "and the page-level entry point callers actually use");
    }

    /// <summary>
    /// A DELIBERATE failure-mode difference, pinned so it is not "fixed" into
    /// agreement by accident — and so a future change to either side is a
    /// visible decision. ContentStreamParser parses dictionaries recursively
    /// and therefore must bound them (256, shared with arrays);
    /// TextExtractor's SkipDictionary is an iterative bracket walk with no
    /// stack to overflow, so it needs no bound and reads the document.
    ///
    /// The asymmetry is safe in the direction that matters: the parser
    /// redaction runs on is the one that REFUSES. If that ever inverts —
    /// extraction refusing what redaction accepts — the refusal becomes
    /// silent and this test is where to notice.
    /// </summary>
    [Fact]
    public void DeepDictionaryNesting_IsADeliberateFailureModeDifference()
    {
        var content = "BT /F1 12 Tf 1 0 0 1 72 700 Tm "
            + string.Concat(Enumerable.Repeat("<<", 300))
            + string.Concat(Enumerable.Repeat(">>", 300))
            + " (Hi) Tj ET";

        using var doc = PdfDocument.Open(ParityFixture.Build(content));
        var page = doc.GetPage(1);

        ParityFixture.Outcome(() => ParityFixture.ParseOperators(page))
            .Should().Be(nameof(PdfParseException),
                "ContentStreamParser recurses into dictionaries and must bound them");
        ParityFixture.Outcome(() => ParityFixture.ExtractLetters(page))
            .Should().Be("ok",
                "TextExtractor skips dictionaries iteratively — no stack, no bound");
    }
}

/// <summary>
/// Shared fixture plumbing for the #980 differential gate: a minimal
/// single-page PDF around arbitrary content bytes, and reflection-free access
/// to what each machine did with them.
/// </summary>
internal static class ParityFixture
{
    public static ContentStream ParseOperators(PdfPage page) =>
        new ContentStreamParser(page.GetContentStreamBytes(), page).Parse();

    public static IReadOnlyList<Letter> ExtractLetters(PdfPage page) =>
        new TextExtractor(page) { IncludeFormFieldValues = false }.ExtractLetters();

    /// <summary>
    /// "ok" or the exception type name — so a rejection can be compared
    /// between the two machines without either being allowed to throw raw.
    /// </summary>
    public static string Outcome(Action action)
    {
        try
        {
            action();
            return "ok";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>
    /// The private static <c>Operators</c> table of either state machine.
    /// Reflection is the honest tool here: the tables ARE the thing under
    /// comparison, and reading them directly is exact where a behavioural
    /// probe has false negatives (see the caller). A rename fails this
    /// loudly rather than silently comparing nothing.
    /// </summary>
    public static ISet<string> OperatorSet(Type stateMachine)
    {
        var field = stateMachine.GetField("Operators",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull(
            $"{stateMachine.Name} must keep its operator table in a static field " +
            "named Operators for the #980 parity gate to compare it");

        var value = field!.GetValue(null) as IEnumerable<string>;
        value.Should().NotBeNull($"{stateMachine.Name}.Operators must be a string set");
        return new HashSet<string>(value!, StringComparer.Ordinal);
    }

    /// <summary>
    /// A one-page PDF whose /F1 is a Type0 / Identity-H font whose
    /// <c>/DescendantFonts</c> is an INDIRECT REFERENCE (object 6), the shape
    /// real producers emit and the one a bare `is PdfArray` cast misses. The
    /// descendant's /W gives CIDs 0x48/0x65/0x6C the widths 1500/250/500, all
    /// far from /DW and from any flat default, so a parser that fails to reach
    /// the table cannot land on the right answer by accident.
    /// </summary>
    public static byte[] BuildType0(string content) => Build(
        content,
        fontObject:
            "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Parity "
          + "/Encoding /Identity-H /DescendantFonts 6 0 R >>\nendobj\n",
        extraObjects:
            "6 0 obj\n[ 7 0 R ]\nendobj\n"
          + "7 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Parity "
          + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
          + "/DW 1000 /W [ 72 [1500] 101 [250] 108 [500] ] >>\nendobj\n");

    /// <summary>
    /// A minimal one-page PDF whose content stream is exactly
    /// <paramref name="content"/>, Latin-1 encoded so binary sample bytes
    /// survive verbatim.
    /// </summary>
    /// <param name="extraFontResources">Additional entries for the page's
    /// <c>/Resources /Font</c> dictionary, e.g. <c>"/F2 6 0 R"</c>, whose
    /// objects come in via <paramref name="extraObjects"/>. Used by the #983
    /// gate, which needs a SECOND font to make a bracketed <c>Tf</c> observable
    /// through something other than the size.</param>
    public static byte[] Build(
        string content,
        string fontObject = "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        string extraObjects = "",
        string extraFontResources = "")
    {
        var body = Encoding.Latin1.GetBytes(content);
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        // Objects after the font are written as one block; the xref below is a
        // single free entry plus a run, and any object it does not name is
        // still reachable because PdfDocument reconstructs on a bad offset.
        // Keeping the table honest for 1-5 is enough for every fixture here.
        W("%PDF-1.7\n");
        var offsets = new long[6];

        offsets[1] = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R " + extraFontResources + " >> >> >>\nendobj\n");
        offsets[4] = ms.Position;
        W($"4 0 obj\n<< /Length {body.Length} >>\nstream\n");
        ms.Write(body);
        W("\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        W(fontObject);

        var extraOffsets = new List<long>();
        foreach (var obj in SplitObjects(extraObjects))
        {
            extraOffsets.Add(ms.Position);
            W(obj);
        }

        var size = 6 + extraOffsets.Count;
        var xref = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        foreach (var offset in extraOffsets)
            W($"{offset:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Split a concatenation of "N 0 obj … endobj\n" bodies.</summary>
    private static IEnumerable<string> SplitObjects(string objects)
    {
        int pos = 0;
        while (pos < objects.Length)
        {
            var end = objects.IndexOf("endobj\n", pos, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return objects[pos..(end + "endobj\n".Length)];
            pos = end + "endobj\n".Length;
        }
    }
}
