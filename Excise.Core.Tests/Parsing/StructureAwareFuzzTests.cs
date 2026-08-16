using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Structure-aware fuzzing of real, checked-in PDFs (#960, part 2 of 3).
///
/// <para><b>What this adds over the fuzzing already here.</b>
/// <see cref="ParserFuzzTests"/> flips random BYTES in a hand-built minimal
/// PDF; <see cref="Excise.Core.Tests.Fonts.FontParserFuzzTests"/> does the
/// same for font programs. Both mutate below the level of PDF syntax, so
/// almost every mutation lands in a string literal or a stream payload and
/// the interesting structural shapes — a reference retargeted at the wrong
/// object, a broken <c>endstream</c>, a truncated xref — come up only by
/// luck. This suite mutates at the level of PDF TOKENS, on documents that
/// carry real xref tables, object streams, filters and fonts rather than a
/// synthetic five-object skeleton. That is where the two crashes this work
/// found live (#969, #971): both are reference/recursion shapes, invisible
/// to a byte flipper.</para>
///
/// <para><b>Tier: t0, plus a deep sweep at release tier (#984).</b> At the
/// checked-in default (250) each row is a few hundred in-memory parses of
/// sub-kilobyte-to-17 KB fixtures and the whole class runs in seconds — cheap
/// enough for every push. Issue #960 asked for a nightly tier, but
/// <c>nightly-corpus</c> in <c>tests/format-compatibility-suite.json</c> is
/// still <c>status: planned</c> with <c>primaryCommand: null</c> — there is
/// no runner to schedule against. Rather than wait on that,
/// <c>scripts/run-deep-fuzz-sweep.sh</c> runs this same class at 20,000
/// iterations/seed (configurable via <c>EXCISE_FUZZ_ITERATIONS</c>, see
/// <see cref="Iterations"/>) from <c>run-full-suite.sh --everything</c> — the
/// existing hour-long release tier, where a couple of minutes is affordable.
/// Every escape this suite has actually found needed thousands of iterations
/// (see the table on #984), so 250 is a regression guard, not a discovery
/// mechanism; the deep sweep is where discovery happens.</para>
///
/// <para><b>Determinism.</b> Seeds are fixed and every failure message names
/// the seed and iteration, which together reproduce the exact byte sequence:
/// <c>Random(seed)</c> is replayed from the start, so iteration N is the same
/// document on every machine and every run. A fuzz failure nobody can
/// reproduce is a flake report, not a bug report.</para>
/// </summary>
public class StructureAwareFuzzTests
{
    /// <summary>
    /// Mutated documents per seed. Sized so a row stays in the low seconds:
    /// the point is many cheap shapes, not a long soak.
    ///
    /// <para><b>How to hunt with this (#984).</b> Set the
    /// <c>EXCISE_FUZZ_ITERATIONS</c> environment variable — e.g.
    /// <c>EXCISE_FUZZ_ITERATIONS=20000 dotnet test Excise.Core.Tests --filter
    /// FullyQualifiedName~StructureAwareFuzzTests</c>, or just run
    /// <c>scripts/run-deep-fuzz-sweep.sh</c>, which sets it for you. Because
    /// the seeds are fixed and mutation N depends only on how many prior
    /// mutations were drawn from the same <c>Random(seed)</c> — never on the
    /// configured depth — iteration N is the identical document at ANY
    /// setting, so a deep-sweep finding reproduces exactly by restoring the
    /// iteration count and the seed named in the failure message. That is how
    /// #975 was filed, back when this was a manual "raise it, run it, put it
    /// back" step. Do NOT raise the 250 default permanently: t0's whole
    /// budget is ~30s and every push pays this row.</para>
    ///
    /// <para><b>A green run at 250 is not "no defects left".</b> The deep
    /// sweep that shook out #974's seven escapes kept finding more as the
    /// depth grew, ending at a JBIG2 allocation defect (#975) around
    /// iteration 5400. #975 is now FIXED (7368b8c7) — re-run at
    /// EXCISE_FUZZ_ITERATIONS=20000 on 2026-08-16, all six rows pass in
    /// ~28s — so do not restate "deliberately still open" here; that was
    /// true when this paragraph was written and stopped being true once the
    /// fix landed. Re-verify against a fresh sweep before trusting this
    /// note, the same rule CLAUDE.md applies to its own stale-claim history.
    /// This gate says "no worse than what we fixed", the same honesty the
    /// extraction-parity floors carry in CLAUDE.md — a green run at any
    /// depth is a regression guard for what has already been found, not
    /// proof nothing remains. <c>scripts/run-deep-fuzz-sweep.sh</c> runs this
    /// row at depth from <c>run-full-suite.sh --everything</c> so that depth
    /// is reached at least once per release rather than only when someone
    /// remembers to raise it by hand.</para>
    /// </summary>
    private static readonly int Iterations = ResolveIterations();

    private static int ResolveIterations()
    {
        var raw = Environment.GetEnvironmentVariable("EXCISE_FUZZ_ITERATIONS");
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var configured) && configured > 0)
            return configured;
        return 250;
    }

    /// <summary>
    /// Budget for one theory row. Generous versus the ~1-3s a row actually
    /// takes at the default 250, because this is a hang DETECTOR, not a
    /// performance budget — a row that trips it has found an unbounded loop,
    /// and a tight bound here would just make the suite flaky on a loaded
    /// machine. Scales with <see cref="Iterations"/> (20ms/iteration, well
    /// above the ~1ms/iteration a healthy row actually costs) so a deep
    /// sweep (#984) is not misreported as a hang purely for doing more work;
    /// never below the original 2-minute floor, so the default 250-iteration
    /// row keeps its existing behaviour exactly.
    /// </summary>
    private static readonly TimeSpan RowBudget = ComputeRowBudget();

    private static TimeSpan ComputeRowBudget()
    {
        var scaled = TimeSpan.FromMilliseconds(20.0 * Iterations);
        var floor = TimeSpan.FromMinutes(2);
        return scaled > floor ? scaled : floor;
    }

    /// <summary>
    /// Git-tracked fixtures only — no gitignored corpus, so this suite needs
    /// no <c>[requires:]</c> allowlist entry and runs identically on a bare
    /// CI runner and a corpus-equipped dev box. Chosen for structural
    /// variety rather than size: different filters, an xref stream + hybrid
    /// revision, and one real-world text/font document.
    /// </summary>
    private static readonly string[] FixtureRelativePaths =
    {
        "test-pdfs/pdf20/flate-predictor-png-image.pdf",
        "test-pdfs/pdf20/dct-colotransform-image.pdf",
        "test-pdfs/pdf20/jbig2-globals-image.pdf",
        "test-pdfs/pdf20/indexed-image.pdf",
        "test-pdfs/generated-regressions/hybrid-xrefstm-revision-probe.pdf",
        "test-pdfs/sample-pdfs/birth-certificate-request-scrambled.pdf",
    };

    [Theory]
    [InlineData(9601)] [InlineData(9602)] [InlineData(9603)] [InlineData(9604)]
    public async Task PdfDocument_Open_TokenMutatedRealFixture_FailsGracefullyOrParses(int seed)
    {
        var fixtures = LoadFixtures();

        // Reported on a budget breach: the loop cannot narrate itself once it
        // stops making progress, so the last iteration entered is the only
        // clue to which document hung.
        int lastIteration = -1;
        string lastFixture = "(none)";

        await AdversarialInputContract.WithinBudget(
            $"seed={seed} row", RowBudget, () =>
            {
                var rng = new Random(seed);
                for (int iter = 0; iter < Iterations; iter++)
                {
                    var (name, original) = fixtures[iter % fixtures.Count];
                    lastIteration = iter;
                    lastFixture = name;

                    var mutated = Mutate(original, rng);
                    try
                    {
                        Exercise(mutated);
                    }
                    catch (Exception ex)
                    {
                        AdversarialInputContract.AssertGraceful(
                            ex, $"[{name}] seed={seed} iter={iter}", mutated);
                    }
                }
            });

        lastIteration.Should().Be(Iterations - 1,
            $"the row must complete every iteration (last fixture: {lastFixture})");
    }

    /// <summary>
    /// Truncation gets its own row because it is the single most common
    /// real-world corruption (an interrupted download, a truncated upload)
    /// and because a random cut point exercises every recovery path in turn:
    /// missing %%EOF, half an xref, a stream with no endstream, an object
    /// with no endobj.
    /// </summary>
    [Theory]
    [InlineData(9611)] [InlineData(9612)]
    public async Task PdfDocument_Open_TruncatedRealFixture_FailsGracefullyOrParses(int seed)
    {
        var fixtures = LoadFixtures();

        await AdversarialInputContract.WithinBudget(
            $"truncation seed={seed} row", RowBudget, () =>
            {
                var rng = new Random(seed);
                for (int iter = 0; iter < Iterations; iter++)
                {
                    var (name, original) = fixtures[iter % fixtures.Count];
                    // Never an empty file (already covered by ParserFuzzTests'
                    // zero-length random inputs) and never the whole file.
                    int cut = rng.Next(1, original.Length);
                    var truncated = original.AsSpan(0, cut).ToArray();
                    try
                    {
                        Exercise(truncated);
                    }
                    catch (Exception ex)
                    {
                        AdversarialInputContract.AssertGraceful(
                            ex, $"[{name}] truncated to {cut}/{original.Length} seed={seed} iter={iter}", truncated);
                    }
                }
            });
    }

    /// <summary>
    /// Touches every surface a hostile document reaches in normal use. Text
    /// extraction is included deliberately: it is a third parser of the same
    /// content-stream bytes and it is where #971 (an unbounded recursion that
    /// killed the process) lived, unreached by any fuzzer that stopped at
    /// <c>PageCount</c>.
    /// </summary>
    private static void Exercise(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        int pageCount = doc.PageCount;

        // A lying /Count is a legal outcome of mutation (HostileStructureTests
        // pins int.MaxValue); cap the walk so the fuzzer cannot be talked into
        // a 2-billion-iteration loop by its own input.
        for (int p = 1; p <= Math.Min(pageCount, 3); p++)
        {
            var page = doc.GetPage(p);
            _ = page.GetContentStreamBytes();
            _ = page.Letters?.Count();
            _ = page.Text?.Length;
        }
    }

    // ---------------------------------------------------------------
    // Token-level mutation. Operates on the Latin-1 view of the file so
    // offsets are byte offsets: PDF syntax is ASCII, and stream payloads are
    // simply opaque bytes that some mutations happen to land in.
    // ---------------------------------------------------------------

    private static byte[] Mutate(byte[] original, Random rng)
    {
        var text = Encoding.Latin1.GetString(original);

        // Two or three mutations: enough to break structure in combination
        // (a retargeted reference AND a broken length is a shape one mutation
        // cannot make), few enough that the file stays PDF-shaped rather than
        // degenerating into the random noise ParserFuzzTests already covers.
        int count = rng.Next(2, 4);
        for (int i = 0; i < count; i++)
            text = ApplyOneMutation(text, rng);

        return Encoding.Latin1.GetBytes(text);
    }

    private static string ApplyOneMutation(string text, Random rng) => rng.Next(7) switch
    {
        0 => ReplaceNumber(text, rng),
        1 => RetargetReference(text, rng),
        2 => CorruptKeyword(text, rng),
        3 => SwapName(text, rng),
        4 => DeleteSpan(text, rng),
        5 => DuplicateSpan(text, rng),
        _ => BreakDelimiter(text, rng),
    };

    /// <summary>
    /// Hostile integers: zero, negative, and both int boundaries. These are
    /// what a /Length, /Count, /Size, /Prev or an xref offset must survive.
    /// </summary>
    private static readonly string[] HostileNumbers =
        { "0", "-1", "2147483647", "-2147483648", "99999999999", "4294967296" };

    private static string ReplaceNumber(string text, Random rng)
    {
        var spans = NumberSpans(text);
        if (spans.Count == 0) return text;
        var (start, length) = spans[rng.Next(spans.Count)];
        return text.Remove(start, length)
                   .Insert(start, HostileNumbers[rng.Next(HostileNumbers.Length)]);
    }

    /// <summary>
    /// Points an indirect reference at a different object number. This is the
    /// mutation that manufactures the cycles behind #969 — a /Length whose
    /// target is the object being parsed — and dangling references, which
    /// §7.3.10 says must read as null rather than fail the document.
    /// </summary>
    private static string RetargetReference(string text, Random rng)
    {
        var refs = new List<(int start, int length)>();
        for (int i = 0; i + 2 < text.Length; i++)
        {
            // "<n> <g> R" — locate by the R and walk back over two integers.
            if (text[i] != 'R') continue;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) continue;
            if (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_')) continue;

            int j = i - 1;
            while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
            int genEnd = j;
            while (j >= 0 && char.IsAsciiDigit(text[j])) j--;
            if (j == genEnd) continue;
            while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
            int numEnd = j;
            while (j >= 0 && char.IsAsciiDigit(text[j])) j--;
            if (j == numEnd) continue;

            refs.Add((j + 1, numEnd - j));
        }

        if (refs.Count == 0) return text;
        var (start, length) = refs[rng.Next(refs.Count)];
        return text.Remove(start, length).Insert(start, rng.Next(0, 30).ToString());
    }

    private static readonly string[] StructuralKeywords =
        { "endstream", "endobj", "stream", "trailer", "startxref", "xref", "obj" };

    private static string CorruptKeyword(string text, Random rng)
    {
        var keyword = StructuralKeywords[rng.Next(StructuralKeywords.Length)];
        var positions = new List<int>();
        for (int i = text.IndexOf(keyword, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(keyword, i + 1, StringComparison.Ordinal))
            positions.Add(i);

        if (positions.Count == 0) return text;
        int at = positions[rng.Next(positions.Count)];
        // Transpose two characters rather than deleting the word: a keyword
        // that is *nearly* right is the harder recovery case, and the one a
        // real damaged file produces.
        var chars = text.ToCharArray();
        int k = at + rng.Next(keyword.Length - 1);
        (chars[k], chars[k + 1]) = (chars[k + 1], chars[k]);
        return new string(chars);
    }

    private static readonly string[] SubstituteNames =
        { "/Length", "/Filter", "/Type", "/Pages", "/Kids", "/Count", "/Root", "/Contents", "/W", "/Index" };

    private static string SwapName(string text, Random rng)
    {
        var spans = NameSpans(text);
        if (spans.Count == 0) return text;
        var (start, length) = spans[rng.Next(spans.Count)];
        return text.Remove(start, length)
                   .Insert(start, SubstituteNames[rng.Next(SubstituteNames.Length)]);
    }

    private static string DeleteSpan(string text, Random rng)
    {
        if (text.Length < 8) return text;
        int start = rng.Next(text.Length - 4);
        int length = Math.Min(rng.Next(1, 64), text.Length - start);
        return text.Remove(start, length);
    }

    private static string DuplicateSpan(string text, Random rng)
    {
        if (text.Length < 8) return text;
        int start = rng.Next(text.Length - 4);
        int length = Math.Min(rng.Next(1, 64), text.Length - start);
        return text.Insert(start, text.Substring(start, length));
    }

    private static string BreakDelimiter(string text, Random rng)
    {
        var delimiters = new[] { "<<", ">>", "[", "]", "(", ")" };
        var d = delimiters[rng.Next(delimiters.Length)];
        int at = text.IndexOf(d, StringComparison.Ordinal);
        if (at < 0) return text;

        // Walk to a random occurrence so mutation is not always the first one.
        int skip = rng.Next(8);
        for (int i = 0; i < skip; i++)
        {
            int next = text.IndexOf(d, at + d.Length, StringComparison.Ordinal);
            if (next < 0) break;
            at = next;
        }
        return text.Remove(at, d.Length);
    }

    /// <summary>Maximal runs of ASCII digits — every integer token in the file.</summary>
    private static List<(int start, int length)> NumberSpans(string text)
    {
        var spans = new List<(int, int)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i])) continue;
            int j = i;
            while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
            spans.Add((i, j - i));
            i = j - 1;
        }
        return spans;
    }

    /// <summary>
    /// Name tokens, matched from their leading '/' to the next delimiter, so a
    /// swap replaces a whole key (<c>/Length</c> -> <c>/Filter</c>) rather
    /// than corrupting the middle of one — the former changes the document's
    /// STRUCTURE, which is the point of this suite.
    /// </summary>
    private static List<(int start, int length)> NameSpans(string text)
    {
        var spans = new List<(int, int)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '/') continue;
            int j = i + 1;
            while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '.' || text[j] == '#')) j++;
            spans.Add((i, j - i));
            i = j - 1;
        }
        return spans;
    }

    // ---------------------------------------------------------------
    // Fixture loading. These files are git-tracked, so a missing one is a
    // broken checkout, not an environment to skip on.
    // ---------------------------------------------------------------

    private static List<(string Name, byte[] Bytes)> LoadFixtures()
    {
        var root = RepoRoot();
        var loaded = new List<(string, byte[])>();
        foreach (var relative in FixtureRelativePaths)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue(
                $"{relative} is checked into git; a missing fixture means a broken checkout, not an " +
                "environment this suite may quietly skip on");
            loaded.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
        }
        return loaded;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "excise.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test binary must sit under the repository");
        return dir!.FullName;
    }
}
