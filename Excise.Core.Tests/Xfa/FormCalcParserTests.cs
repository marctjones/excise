using System.Text;
using AwesomeAssertions;
using Excise.Core.Xfa.FormCalc;
using Xunit;
using static Excise.Core.Xfa.FormCalc.FormCalcTokenKind;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// FormCalc lexing and parsing (#1570). The expected tokens and trees are the ones pdf.js's own
/// FormCalc unit tests state (test/unit/xfa_formcalc_spec.js), so an assumption this code shares
/// with its author cannot pass unnoticed; the hostile cases are ours.
/// </summary>
public class FormCalcParserTests
{
    private static List<FormCalcToken> Lex(string text)
    {
        var lexer = new FormCalcLexer(text);
        var list = new List<FormCalcToken>();
        while (true)
        {
            var t = lexer.Next();
            if (t.Kind == Eof) return list;
            list.Add(t);
        }
    }

    [Fact]
    public void Numbers_LexAsThePdfJsVectorsSay()
    {
        var numbers = Lex("1 7 12 1.2345 .7 .12345 1e-2 1.2E+3 1e2 1.2E3 nan 12. 2.e3 infinity 99999999999999999 123456789.012345678 9e99999");
        numbers.Select(t => t.Kind).Should().OnlyContain(k => k == Number);
        numbers.Select(t => t.Number).Should().Equal(
            1, 7, 12, 1.2345, 0.7, 0.12345, 1e-2, 1.2e3, 1e2, 1.2e3, double.NaN, 12, 2e3,
            double.PositiveInfinity, 100000000000000000d, 123456789.01234567, double.PositiveInfinity);
    }

    [Fact]
    public void Strings_UnescapeDoubledQuotesAndUnicodeEscapes()
    {
        var t = Lex("\"hello world\" \"hello \"\"world\" \"hello \"\"world\"\" \"\"world\"\"\"\"hello\"\"\" \"hello \\uabcdeh \\Uabcd \\u00000123abc\" \"a \\a \\ub \\Uc \\b\"");
        t.Select(x => x.Text).Should().Equal(
            "hello world",
            "hello \"world",
            "hello \"world\" \"world\"\"hello\"",
            "hello \uabcdeh \uabcd \u0123abc",
            "a \\a \\ub \\Uc \\b");
    }

    [Fact]
    public void Operators_Lex()
    {
        Lex("( , ) <= <> = == >= < > / * . .* .# [ ] & |").Select(t => t.Kind).Should().Equal(
            LeftParen, Comma, RightParen, Le, Ne, Assign, Eq, Ge, Lt, Gt, Divide, Times,
            Dot, DotStar, DotHash, LeftBracket, RightBracket, And, Or);
    }

    [Fact]
    public void Comments_AreSkipped()
    {
        Lex("\n\n \t\t 1 \r\n\r\n\n ;  blah blah blah\n\n 2\n\n // blah blah blah blah\n\n 3\n")
            .Select(t => t.Number).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Identifiers_AndKeywords_LexCaseInsensitively()
    {
        var t = Lex("eq for fore while continue hello こんにちは世界 $!hello今日は12今日は ENDIF");
        t.Select(x => x.Kind).Should().Equal(Eq, For, Identifier, While, Continue, Identifier, Identifier, Identifier, Identifier, EndIf);
        t.Select(x => x.Text).Skip(2).Take(1).Should().Equal("fore");
        t[7].Text.Should().Be("$");
        t[8].Text.Should().Be("!hello今日は12今日は");
    }

    // Parser ---------------------------------------------------------------

    private static string Dump(FcExpr e) => e switch
    {
        FcNumber n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FcString s => $"\"{s.Value}\"",
        FcNull => "null",
        FcThis => "this",
        FcName n => n.Name,
        FcUnary u => $"({u.Op} {Dump(u.Operand)})",
        FcBinary b => $"({b.Op} {Dump(b.Left)} {Dump(b.Right)})",
        FcCall c => $"(call {Dump(c.Callee)}{string.Concat(c.Arguments.Select(a => " " + Dump(a)))})",
        FcMember m => $"({(m.Kind == FcMemberKind.Child ? "." : m.Kind == FcMemberKind.Descendant ? ".." : ".#")} {Dump(m.Target)} {m.Name})",
        FcIndex i => $"(idx {Dump(i.Target)} {(i.Index == null ? "*" : Dump(i.Index))})",
        FcAssign a => $"(= {Dump(a.Target)} {Dump(a.Value)})",
        _ => e.GetType().Name,
    };

    private static string Dump(FcStmt s) => s switch
    {
        FcExprStmt e => Dump(e.Expr),
        FcVar v => v.Init == null ? $"(var {v.Name})" : $"(var {v.Name} {Dump(v.Init)})",
        FcIf i => $"(if {Dump(i.Condition)} [{Block(i.Then)}]{(i.Else == null ? "" : $" else [{Block(i.Else)}]")})",
        FcWhile w => $"(while {Dump(w.Condition)} [{Block(w.Body)}])",
        FcDo d => $"(do [{Block(d.Body)}])",
        FcFor f => $"(for {f.Variable} {Dump(f.Start)} {(f.Down ? "downto" : "upto")} {Dump(f.End)}{(f.Step == null ? "" : " step " + Dump(f.Step))} [{Block(f.Body)}])",
        FcForEach f => $"(foreach {f.Variable} ({string.Join(" ", f.Items.Select(Dump))}) [{Block(f.Body)}])",
        FcFunc f => $"(func {f.Name} ({string.Join(" ", f.Parameters)}) [{Block(f.Body)}])",
        FcReturn r => r.Value == null ? "(return)" : $"(return {Dump(r.Value)})",
        FcBreak => "break",
        FcContinue => "continue",
        FcExit => "exit",
        FcThrow t => $"(throw {Dump(t.Value)})",
        _ => s.GetType().Name,
    };

    private static string Block(IEnumerable<FcStmt> body) => string.Join("; ", body.Select(Dump));

    private static string Parse(string script) => Block(FormCalcParser.Parse(script).Statements);

    [Theory]
    [InlineData("1 + a + 3", "(+ (+ 1 a) 3)")]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("foo(2, 3, a & b) or c * d + 1.234 / e",
        "(or (call foo 2 3 (and a b)) (+ (* c d) (/ 1.234 e)))")]
    [InlineData("s = +x + 1", "(= s (+ (+ x) 1))")]
    [InlineData("t = -+u * 2", "(= t (* (- (+ u)) 2))")]
    [InlineData("t = +-u * 2", "(= t (* (+ (- u)) 2))")]
    [InlineData("u = -foo()", "(= u (- (call foo)))")]
    [InlineData("a[*]", "(idx a *)")]
    [InlineData("a.b.c.#d..e.f..g.*", "(. (.. (. (.. (.# (. (. a b) c) d) e) f) g) *)")]
    public void Expressions_ParseToTheStatedTree(string script, string tree)
    {
        Parse(script).Should().Be(tree);
    }

    [Fact]
    public void Statements_AreJuxtaposed_WithNoSeparator()
    {
        Parse("s = +x + 1\n t = -+u * 2\n u = -foo()").Should().Be(
            "(= s (+ (+ x) 1)); (= t (* (- (+ u)) 2)); (= u (- (call foo)))");
    }

    [Fact]
    public void For_ParsesWithAndWithoutStep()
    {
        Parse("for i = 1 upto 10 step 2 do a = i endfor").Should().Be("(for i 1 upto 10 step 2 [(= a i)])");
        Parse("for i = 10 downto 1 do a = i endfor").Should().Be("(for i 10 downto 1 [(= a i)])");
    }

    [Fact]
    public void ForEach_While_Do_Func_Parse()
    {
        Parse("foreach x in (a, b, 3) do y = x endfor").Should().Be("(foreach x (a b 3) [(= y x)])");
        Parse("while (a < 3) do a = a + 1 endwhile").Should().Be("(while (< a 3) [(= a (+ a 1))])");
        Parse("do a = 1 end").Should().Be("(do [(= a 1)])");
        Parse("func f(a, b) do return a + b endfunc").Should().Be("(func f (a b) [(return (+ a b))])");
    }

    [Fact]
    public void If_ParsesElseIfChainsAndElse()
    {
        Parse("if (a & b) then var s = 1 endif").Should().Be("(if (and a b) [(var s 1)])");
        Parse("if (a or b) then var s = 1 else var x = 2 endif").Should().Be("(if (or a b) [(var s 1)] else [(var x 2)])");
        Parse("if (0) then s = 1 elseif (1) then s = 2 else s = 3 endif").Should().Be(
            "(if 0 [(= s 1)] else [(if 1 [(= s 2)] else [(= s 3)])])");
    }

    [Fact]
    public void Equals_InsideACondition_Compares_ButAtStatementStartAssigns()
    {
        // Not covered by pdf.js's vectors; XFA 3.3 says '=' compares where it cannot assign.
        Parse("if (a == 1) then b = 2 endif").Should().Be("(if (== a 1) [(= b 2)])");
        Parse("f(a = 1)").Should().Be("(call f (== a 1))");
    }

    [Theory]
    [InlineData("var")]                       // var without a name
    [InlineData("for i = 1 do a = 1 endfor")] // no upto/downto
    [InlineData("for = 1 upto 2 do endfor")]
    [InlineData("foreach x (a) do endfor")]   // no 'in'
    [InlineData("while (1) endwhile")]        // no 'do'
    [InlineData("do a = 1")]                  // no 'end'
    [InlineData("func f( do endfunc")]
    [InlineData("if (1) a = 1 endif")]        // no 'then'
    [InlineData("if (1) then a = 1")]         // no 'endif'
    [InlineData("1 = 2")]                     // not assignable
    [InlineData("a +")]
    [InlineData("(1 + 2")]
    [InlineData("a.")]
    [InlineData("\u0001")]
    public void Malformed_Scripts_RaiseSyntaxErrors(string script)
    {
        var act = () => FormCalcParser.Parse(script);
        act.Should().Throw<FormCalcSyntaxException>();
    }

    [Fact]
    public void HostileNesting_IsRefusedBeforeItCanOverflowTheStack()
    {
        var script = new string('(', 100_000) + "1" + new string(')', 100_000);
        var act = () => FormCalcParser.Parse(script);
        act.Should().Throw<FormCalcSyntaxException>();

        var unary = new string('-', 100_000) + "1";
        var act2 = () => FormCalcParser.Parse(unary);
        act2.Should().Throw<FormCalcSyntaxException>();

        var blocks = string.Concat(Enumerable.Repeat("do ", 100_000)) + string.Concat(Enumerable.Repeat("end ", 100_000));
        var act3 = () => FormCalcParser.Parse(blocks);
        act3.Should().Throw<FormCalcSyntaxException>();
    }

    [Fact]
    public void HugeScript_IsRefused()
    {
        var act = () => FormCalcParser.Parse(new string('a', FormCalcLexer.MaxScriptLength + 1));
        act.Should().Throw<FormCalcSyntaxException>();
    }

    [Fact]
    public void ManyTokens_AreRefused()
    {
        var script = string.Concat(Enumerable.Repeat("a ", FormCalcParser.MaxTokens + 10));
        var act = () => FormCalcParser.Parse(script);
        act.Should().Throw<FormCalcSyntaxException>();
    }

    [Fact]
    public void KeywordsAreValidElementNamesAfterADot()
    {
        Parse("form.end.step").Should().Be("(. (. form end) step)");
    }
}
