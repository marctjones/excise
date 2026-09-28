using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Core.Xfa.FormCalc;
using Xunit;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// Deterministic fuzzing of the FormCalc lexer, parser and interpreter (#1570): seeded random programs,
/// mutations of real scripts (the corpus's XFA forms, and a built-in pool so the mutation arm never
/// skips) and hand-made hostile shapes. For every input the only permitted exceptions are the
/// interpreter's own <see cref="FormCalcSyntaxException"/> and <see cref="FormCalcRuntimeException"/>,
/// the run stays inside its <see cref="FcLimits"/>, finishes within a timeout and allocates a bounded
/// amount. Values are not checked: <c>Date</c>, <c>Time</c> and <c>Uuid</c> are not deterministic.
/// </summary>
public class FormCalcFuzzTests
{
    private static readonly FcLimits Limits = new()
    {
        MaxSteps = 20_000,
        MaxCallDepth = 32,
        MaxStringLength = 20_000,
        MaxTotalStringChars = 200_000,
        MaxListItems = 1_000,
        MaxEvalDepth = 4,
        TimeLimit = TimeSpan.FromMilliseconds(200),
    };

    /// <summary>
    /// A run may overrun <see cref="FcLimits.TimeLimit"/> by one step (one built-in call) plus GC and scheduling
    /// noise; wide, because this runs beside other test classes and a false red teaches people to ignore it.
    /// </summary>
    private static readonly TimeSpan TimeSlack = TimeSpan.FromSeconds(4);

    /// <summary>A case that has not returned by then is a hang.</summary>
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Generous against the budgets above (200K characters of strings, 20K steps).</summary>
    private const long MaxAllocatedBytes = 64L * 1024 * 1024;

    private static readonly int[] Seeds = [1, 2, 3, 5, 8, 13, 21, 34];

    // The form ---------------------------------------------------------------------------------

    private sealed class Node : IFcObject
    {
        private readonly Dictionary<string, object?> _props = new(StringComparer.Ordinal);
        private readonly List<IFcObject> _children = new();

        public Node(string name, string cls = "field", object? value = null)
        {
            Name = name; ClassName = cls; _props["rawValue"] = value; _props["presence"] = "visible";
        }

        public string Name { get; }
        public string ClassName { get; }
        public IFcObject? Parent { get; private set; }
        public IReadOnlyList<IFcObject> Children => _children;

        public Node Add(Node child) { child.Parent = this; _children.Add(child); return this; }

        public bool TryGetProperty(string name, out object? value) => _props.TryGetValue(name, out value);

        public bool TrySetProperty(string name, object? value)
        {
            if (name is not ("rawValue" or "presence")) return false;
            _props[name] = value;
            return true;
        }
    }

    private sealed class Host(IFcObject context, IFcObject root) : IFcHost
    {
        public IFcObject Context => context;
        public object? ResolveRoot(string name) => name is "xfa" or "$form" ? root : null;
    }

    /// <summary>form1 { sf { Number1, Number2, Total, Text }, Outside, 20 x row { x } }; a fresh one per case.</summary>
    private static Host NewHost()
    {
        var total = new Node("Total");
        var sf = new Node("sf", "subform")
            .Add(new Node("Number1", value: 3d)).Add(new Node("Number2", value: 4d)).Add(total).Add(new Node("Text", value: "abc"));
        var form = new Node("form1", "subform").Add(sf).Add(new Node("Outside", value: "out"));
        for (var i = 0; i < 20; i++)
            form.Add(new Node("row", "subform").Add(new Node("x", value: (double)i)));
        return new Host(total, form);
    }

    // The oracle --------------------------------------------------------------------------------

    /// <summary>
    /// Run one input. Problem is null when it behaved, else a one-line account of how it did not; Parsed
    /// says whether the input got past the parser, so a generator that only makes syntax errors shows.
    /// </summary>
    internal static (string? Problem, bool Parsed) Check(string source, FcLimits limits)
    {
        var task = Task.Run(() =>
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            FormCalcInterpreter? interpreter = null;
            FcScript? script = null;
            string? Verdict()
            {
                clock.Stop();
                if (clock.Elapsed > limits.TimeLimit + TimeSlack)
                    return $"ran {clock.ElapsedMilliseconds} ms";
                if (interpreter != null)
                {
                    // A step or a string over the line is what trips the check, so each can overshoot by one;
                    // a nested run can add its own one.
                    if (interpreter.StepsUsedForTests > limits.MaxSteps + limits.MaxEvalDepth + 2)
                        return $"spent {interpreter.StepsUsedForTests} steps";
                    if (interpreter.StringCharsUsedForTests > limits.MaxTotalStringChars + (limits.MaxEvalDepth + 2L) * limits.MaxStringLength)
                        return $"built {interpreter.StringCharsUsedForTests} characters";
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                return allocated > MaxAllocatedBytes ? $"allocated {allocated / (1024 * 1024)} MB" : null;
            }
            try
            {
                script = FormCalcParser.Parse(source);
                interpreter = new FormCalcInterpreter(NewHost(), limits);
                var result = interpreter.Run(script);
                // A string literal is as long as the script lets it be; anything longer was grown at run time.
                if (FormCalcValue.Scalar(result) is string s && s.Length > Math.Max(limits.MaxStringLength, source.Length))
                    return ($"returned a {s.Length}-character string", true);
            }
            catch (FormCalcSyntaxException) { }
            catch (FormCalcRuntimeException) { }
            catch (Exception ex)
            {
                return ($"threw {ex.GetType().Name}: {ex.Message}", script != null);
            }
            return (Verdict(), script != null);
        });
        if (!task.Wait(HangTimeout))
            return ($"did not finish in {HangTimeout.TotalSeconds:0} s", true);
        return task.Result;
    }

    /// <param name="minParsed">Share of the inputs that must parse, so the interpreter is really exercised.</param>
    private static void AssertAllBehave(IEnumerable<(string Label, string Source)> cases, double minParsed)
    {
        var failures = new List<string>();
        var count = 0;
        var parsed = 0;
        foreach (var (label, source) in cases)
        {
            count++;
            var (problem, ok) = Check(source, Limits);
            if (ok) parsed++;
            if (problem != null)
            {
                var shown = source.Length > 300 ? source[..300] + $"... ({source.Length} chars)" : source;
                failures.Add($"{label}: {problem}\n    {shown.ReplaceLineEndings(" ")}");
                if (failures.Count >= 10) break;
            }
        }
        failures.Should().BeEmpty();
        count.Should().BePositive();
        ((double)parsed / count).Should().BeGreaterThanOrEqualTo(minParsed, $"{parsed} of {count} inputs parsed");
    }

    // Generator 1: random programs from the grammar --------------------------------------------

    private static readonly string[] Names =
        ["Number1", "Number2", "Total", "Text", "sf", "form1", "Outside", "row", "x", "a", "b", "i", "s", "f", "$", "$form", "xfa", "this", "$record", "!"];

    private static readonly string[] BuiltinNames = FormCalcBuiltins.Names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static readonly string[] Literals =
    [
        "0", "1", "-1", "3.5", "1e308", "-1e308", "1e-320", "nan", "infinity", "-infinity", "2147483648", "-2147483649",
        "\"\"", "\"abc\"", "\"12\"", "\"  7 \"", "\"\\u00e9\"", "\"\\u0000\"", "\"\\ud800\"", "\"a\"\"b\"", "null", "Null()",
        "\"time{HH:MM}\"", "\"date{YYYY-MM-DD}\"", "\"num{z,zz9.99}\"", "\"text{999-99}\"", "\"MMMM D, YYYY\"", "\"h:MM:SS A\"",
        "Space(5000)", "Space(19999)", "\"url\"", "\"html\"", "\"xml\"",
    ];

    private static readonly string[] BinaryOps = ["+", "-", "*", "/", "==", "<>", "<", "<=", ">", ">=", "and", "or", "&", "|", "eq", "ne", "lt", "le", "gt", "ge"];

    private sealed class ProgramGenerator(Random random)
    {
        private T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];

        public string Program()
        {
            var sb = new StringBuilder();
            var n = random.Next(1, 6);
            for (var i = 0; i < n; i++) sb.Append(Statement(3)).Append('\n');
            return sb.ToString();
        }

        private string Block(int depth)
        {
            var n = random.Next(0, 3);
            return string.Join(" ", Enumerable.Range(0, n).Select(_ => Statement(depth - 1)));
        }

        private string Statement(int depth)
        {
            if (depth <= 0) return Expr(1);
            return random.Next(13) switch
            {
                0 => $"var {Pick(Names)} = {Expr(depth)}",
                1 => $"if ({Expr(depth)}) then {Block(depth)} elseif ({Expr(depth)}) then {Block(depth)} else {Block(depth)} endif",
                2 => $"while ({Expr(depth)}) do {Block(depth)} endwhile",
                3 => $"for i = {Expr(depth)} {(random.Next(2) == 0 ? "upto" : "downto")} {Expr(depth)} step {Expr(depth)} do {Block(depth)} endfor",
                4 => $"foreach a in ({Expr(depth)}, {Expr(depth)}) do {Block(depth)} endfor",
                5 => $"func f({Pick(Names)}) do {Block(depth)} return {Expr(depth)} endfunc",
                6 => $"do {Block(depth)} end",
                7 => $"{Target(depth)} = {Expr(depth)}",
                8 => Pick(new[] { "break", "continue", "exit", "return", $"throw {Expr(1)}" }),
                _ => Expr(depth),
            };
        }

        private string Target(int depth) => random.Next(4) switch
        {
            0 => Pick(Names),
            1 => $"{Pick(Names)}.{Pick(Names)}",
            2 => $"{Pick(Names)}[{Expr(depth - 1)}]",
            _ => $"{Pick(Names)}.presence",
        };

        private string Expr(int depth)
        {
            if (depth <= 0) return random.Next(2) == 0 ? Pick(Literals) : Pick(Names);
            return random.Next(12) switch
            {
                0 => Pick(Literals),
                1 => Pick(Names),
                2 or 3 => $"{Expr(depth - 1)} {Pick(BinaryOps)} {Expr(depth - 1)}",
                4 => $"{Pick(new[] { "-", "+", "not " })}{Expr(depth - 1)}",
                5 => $"({Expr(depth - 1)})",
                6 => Som(depth),
                7 => $"{Pick(Names)}.{Pick(new[] { "resolveNode", "resolveNodes" })}(\"{Som(depth)}\")",
                8 => $"f({Expr(depth - 1)})",
                9 => $"Eval(\"{Expr(depth - 1).Replace("\"", "\"\"", StringComparison.Ordinal)}\")",
                _ => Call(depth),
            };
        }

        private string Som(int depth) => random.Next(6) switch
        {
            0 => $"{Pick(Names)}..{Pick(Names)}",
            1 => $"{Pick(Names)}.#{Pick(new[] { "field", "subform", "draw" })}",
            2 => $"{Pick(Names)}[*]",
            3 => $"{Pick(Names)}.*",
            4 => $"{Pick(Names)}[{random.Next(-2, 25)}].{Pick(Names)}",
            _ => $"{Pick(Names)}.{Pick(Names)}",
        };

        private string Call(int depth)
        {
            var args = Enumerable.Range(0, random.Next(0, 6)).Select(_ => Expr(depth - 1));
            return $"{Pick(BuiltinNames)}({string.Join(", ", args)})";
        }
    }

    [Fact]
    public void RandomPrograms_Behave()
    {
        AssertAllBehave(Seeds.SelectMany(seed =>
        {
            var generator = new ProgramGenerator(new Random(seed));
            return Enumerable.Range(0, 1500).Select(i => ($"seed {seed} program {i}", generator.Program()));
        }), minParsed: 0.8);
    }

    [Fact]
    public void EveryBuiltin_WithHostileArguments_Behaves()
    {
        // Not random: every built-in, at every arity up to four, with each hostile argument.
        string[] hostile = ["nan", "infinity", "-infinity", "1e308", "-1e308", "-1", "0", "2147483648", "\"\"", "Space(19999)", "null", "row[*]", "\"\\ud800\""];
        AssertAllBehave(BuiltinNames.SelectMany(name =>
            Enumerable.Range(0, 5).SelectMany(arity => hostile.Select(arg =>
                ($"{name}/{arity} {arg}", $"{name}({string.Join(", ", Enumerable.Repeat(arg, arity))})")))), minParsed: 0.9);
    }

    // Generator 2: mutation of real scripts ----------------------------------------------------

    /// <summary>Scripts that exercise the grammar; the mutation arm's seeds when no corpus is present.</summary>
    private static readonly string[] BuiltInSeeds =
    [
        "Number1 + Number2",
        "Total = Sum(row[*].x) / Count(row[*].x)",
        "if (HasValue(Text)) then $.rawValue = Concat(Text, \" \", Outside) else $.presence = \"hidden\" endif",
        "var s = 0\nfor i = 1 upto 10 step 2 do s = s + i endfor\ns",
        "foreach v in (Number1, Number2, row[*].x) do Total = Total + v endfor",
        "func fact(n) do if (n <= 1) then return 1 endif return n * fact(n - 1) endfunc\nfact(6)",
        "while (Total < 100) do Total = Total * 2 + 1 endwhile",
        "Format(\"num{zzz,zz9.99}\", 1234.5)",
        "Num2Date(Date2Num(\"Mar 15, 1996\", \"MMM D, YYYY\"), \"DD/MM/YYYY\")",
        "WordNum(1234.56, 2)",
        "Pmt(30000, .085/12, 12) + IPmt(30000, .085, 295.50, 7, 3)",
        "xfa.resolveNode(\"form1.sf.Number1\").rawValue",
        "$form.form1.sf..Number2 + form1.#subform[0].Number1",
        "Eval(\"Replace(Upper(\"\"abc\"\"), \"\"B\"\", \"\"x\"\")\")",
        "Choose(3, \"a\", \"b\", \"c\") Oneof(Number1, 1, 2, 3) Within(Number2, 1, 9)",
        "do var x = Str(Number1 / 7, 10, 4) Substr(x, 2, 3) end",
        "Encode(Decode(\"%41&amp;\", \"url\"), \"html\")",
        "if (Number1 == 3) then break elseif (Number2 ne 4) then continue else exit endif",
    ];

    private static readonly string[] Vocabulary =
    [
        "(", ")", "[", "]", "[*]", ",", ".", "..", ".#", ".*", "\"", "\"\"", "=", "==", "<>", "+", "-", "*", "/", ";", "//", "\n",
        "if", "then", "else", "elseif", "endif", "while", "do", "endwhile", "for", "upto", "downto", "step", "endfor", "foreach",
        "in", "func", "endfunc", "return", "break", "continue", "exit", "var", "end", "throw", "this", "null", "nan", "infinity",
        "$", "$form", "xfa", "!", "1e308", "0", ".5", "5.", "\\u", "\\u0041", "Eval", "resolveNode", "Space(19999)", "f(f(f()))",
    ];

    private static string Mutate(string source, Random random)
    {
        var s = source;
        var edits = random.Next(1, 5);
        for (var e = 0; e < edits; e++)
        {
            var at = s.Length == 0 ? 0 : random.Next(s.Length + 1);
            var len = s.Length == 0 ? 0 : random.Next(Math.Min(16, s.Length - Math.Min(at, s.Length)) + 1);
            s = random.Next(6) switch
            {
                0 => s.Remove(at, len),
                1 => s.Insert(at, Vocabulary[random.Next(Vocabulary.Length)]),
                2 => s.Insert(at, s.Substring(at, len)), // duplicate a slice
                3 => s.Length == 0 ? s : s[..at],        // truncate
                4 => s.Insert(at, ((char)random.Next(0, 0x3000)).ToString()),
                _ => s.Insert(at, " " + Vocabulary[random.Next(Vocabulary.Length)] + " "),
            };
        }
        return s;
    }

    private static IEnumerable<(string, string)> Mutations(IReadOnlyList<string> pool, int perSeed)
    {
        foreach (var seed in Seeds)
        {
            var random = new Random(seed);
            for (var i = 0; i < perSeed; i++)
            {
                var index = random.Next(pool.Count);
                yield return ($"seed {seed} mutation {i} of script {index}", Mutate(pool[index], random));
            }
        }
    }

    [Fact]
    public void MutatedScripts_Behave()
    {
        AssertAllBehave(Mutations(BuiltInSeeds, 1500), minParsed: 0.15);
    }

    /// <summary>Every &lt;script&gt; text of the corpus's XFA forms, FormCalc or not: JavaScript is fine lexer input.</summary>
    private static List<string> CorpusScripts(out string[] searched)
    {
        searched = ["test-pdfs/xfa-real", "test-pdfs/pdfium/xfa", "test-pdfs/verapdf-corpus"];
        var files = new List<string>();
        foreach (var rel in searched)
        {
            if (TestRepoLayout.FindDirectory(rel) is not { } dir) continue;
            files.AddRange(Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories)
                .Where(f => !rel.EndsWith("verapdf-corpus", StringComparison.Ordinal)
                            || f.Contains("XFA", StringComparison.OrdinalIgnoreCase)));
        }

        var scripts = new List<string>();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var document = PdfDocument.Open(file);
                if (!XfaPackets.TryRead(document, out var packets, out _)) continue;
                scripts.AddRange(packets!.Template.Descendants()
                    .Where(e => e.Name.LocalName == "script")
                    .Select(e => e.Value)
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 20_000));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A file this reader cannot open contributes no seeds; the other files still do.
            }
        }
        return scripts.Distinct(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void MutatedCorpusScripts_Behave()
    {
        var pool = CorpusScripts(out var searched);
        Assert.SkipWhen(pool.Count == 0, TestRepoLayout.AbsenceReason("XFA forms with scripts", searched));
        AssertAllBehave(pool.Select((s, i) => ($"corpus script {i} unmutated", s)).Concat(Mutations(pool, 1000)), minParsed: 0.1); // most corpus scripts are JavaScript
    }

    // Generator 3: hostile shapes ----------------------------------------------------------------

    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    [Fact]
    public void HostileShapes_Behave()
    {
        var chain = "a" + Repeat(".a", 90_000);
        AssertAllBehave(new (string, string)[]
        {
            ("deep member chain", chain),
            ("deep member chain via resolveNode", $"xfa.resolveNode(\"{chain}\")"),
            ("deep index chain via resolveNodes", $"xfa.resolveNodes(\"a{Repeat("[0]", 60_000)}\")"),
            ("deep parentheses", Repeat("(", 50_000) + "1" + Repeat(")", 50_000)),
            ("deep unary minus", Repeat("-", 100_000) + "1"),
            ("deep not", Repeat("not ", 50_000) + "1"),
            ("long sum", "1" + Repeat("+1", 99_000)),
            ("long left-deep member sum", Repeat("row.x+", 30_000) + "1"),
            ("near the token limit", Repeat("1 ", FormCalcParser.MaxTokens - 1)),
            ("over the token limit", Repeat("1 ", FormCalcParser.MaxTokens + 1)),
            ("over the length limit", new string(' ', FormCalcLexer.MaxScriptLength + 1)),
            ("unterminated string", "\"" + new string('a', 100_000)),
            ("escape at the end", "\"\\u12"),
            ("lone surrogate identifier", "\ud800abc"),
            ("nested if", Repeat("if (1) then ", 300) + "1" + Repeat(" endif", 300)),
            ("runaway recursion", "func f() do return f() endfunc f()"),
            ("mutual recursion via Eval", "func f() do return Eval(\"f()\") endfunc f()"),
            ("Eval of Eval", "Eval(\"Eval(\"\"Eval(\"\"\"\"Eval(\"\"\"\"\"\"\"\"Eval(1)\"\"\"\"\"\"\"\")\"\"\"\")\"\")\")"),
            ("loop forever", "while (1) do endwhile"),
            ("for with a step lost to rounding", "for i = 1e300 upto infinity step 1 do endfor"),
            ("for with nan", "for i = nan upto 1 do endfor"),
            ("string doubling", "var s = \"ab\" while (1) do s = Concat(s, s) endwhile"),
            ("strings held in every variable", "var a = Space(19999) var b = Concat(a, 1) var s = Concat(b, 2) var i = Concat(s, 3) while (1) do a = Concat(i, 1) b = Concat(a, 2) endwhile"),
            ("replace blow-up", "Replace(Space(19999), \" \", Space(19999))"),
            ("encode blow-up", "Encode(Replace(Space(19999), \" \", \"<\"), \"html\")"),
            ("worst-case search", "At(Concat(Space(19998), \"x\"), Concat(Space(9999), \"x\"))"),
            ("descendants in a loop", "while (1) do form1..x endwhile"),
            ("resolveNode in a loop", "while (1) do xfa.resolveNodes(\"form1..x\") endwhile"),
            ("Eval in a loop", "while (1) do Eval(\"while (1) do endwhile\") endwhile"),
            ("time of nan", "Num2Time(nan)"),
            ("time picture of nan", "Format(\"time{HH:MM}\", nan)"),
            ("GMT time of nan", "Num2GMTime(nan, \"HH:MM:SS\")"),
            ("amortise overflow", "IPmt(1, 1, 1, 2147483647, 2147483647)"),
            ("assign to everything", "row[*].x = 1 $form.form1.presence = \"hidden\" this = 2 $ = 3 xfa = 4"),
            ("foreach over a wide match", "foreach v in (row[*], row[*].x, form1..*) do v = 1 endfor"),
        }, minParsed: 0.5);
    }
}
