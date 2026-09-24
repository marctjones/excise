using AwesomeAssertions;
using Excise.Core.Xfa.FormCalc;
using Xunit;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// The FormCalc evaluator (#1570) against an in-memory form. Expected values are the XFA 3.3
/// function-reference examples, and figures recomputed independently of this code (Python) for the
/// date and financial functions. Hostile scripts prove the limits and the absence of host functions.
/// </summary>
public class FormCalcInterpreterTests
{
    private sealed class Node : IFcObject
    {
        private readonly Dictionary<string, object?> _props = new(StringComparer.Ordinal);
        private readonly List<IFcObject> _children = new();

        public Node(string name, string cls = "field", object? value = null)
        {
            Name = name; ClassName = cls; _props["rawValue"] = value;
        }

        public string Name { get; }
        public string ClassName { get; }
        public IFcObject? Parent { get; private set; }
        public IReadOnlyList<IFcObject> Children => _children;
        public object? Raw { get => _props["rawValue"]; set => _props["rawValue"] = value; }
        public bool ReadOnly { get; init; }

        public Node Add(Node child) { child.Parent = this; _children.Add(child); return this; }

        public bool TryGetProperty(string name, out object? value) => _props.TryGetValue(name, out value);

        public bool TrySetProperty(string name, object? value)
        {
            if (ReadOnly || name != "rawValue") return false;
            _props[name] = value;
            return true;
        }

        private object? value { set => _props["rawValue"] = value; }
    }

    private sealed class Host : IFcHost
    {
        public required IFcObject Context { get; init; }
        public IFcObject? Root { get; init; }
        public object? ResolveRoot(string name) => name is "xfa" or "$form" or "$record" ? Root : null;
    }

    /// <summary>form1 { sf { Number1 = 3, Number2 = 4, Total } , Outside = "out" }</summary>
    private static (Host Host, Node Total, Node Form) Sample()
    {
        var n1 = new Node("Number1", value: 3d);
        var n2 = new Node("Number2", value: 4d);
        var total = new Node("Total");
        var sf = new Node("sf", "subform").Add(n1).Add(n2).Add(total);
        var form = new Node("form1", "subform").Add(sf).Add(new Node("Outside", value: "out"));
        return (new Host { Context = total, Root = form }, total, form);
    }

    private static object? Run(string script, IFcHost? host = null, FcLimits? limits = null) =>
        FormCalcInterpreter.Evaluate(script, host ?? Sample().Host, limits);

    private static double Num(string script) => FormCalcValue.ToNumber(Run(script));
    private static string Str(string script) => FormCalcValue.ToText(Run(script));

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("\"5\" + 3", 8)]
    [InlineData("null + 1", 1)]
    [InlineData("-(2 + 3)", -5)]
    [InlineData("1 < 2", 1)]
    [InlineData("\"abc\" == \"abc\"", 1)]
    [InlineData("\"abc\" < \"abd\"", 1)]
    [InlineData("2 <> 2", 0)]
    [InlineData("not 0", 1)]
    [InlineData("1 and 0", 0)]
    [InlineData("0 or 2", 1)]
    [InlineData("3 eq 3", 1)]
    [InlineData("Abs(-1.03)", 1.03)]
    [InlineData("Abs(1.03)", 1.03)]
    [InlineData("Avg(0, 32, 16)", 16)]
    [InlineData("Ceil(2.5)", 3)]
    [InlineData("Ceil(-5.1)", -5)]
    [InlineData("Floor(21.3)", 21)]
    [InlineData("Floor(-2.5)", -3)]
    [InlineData("Max(234, 15, 107)", 234)]
    [InlineData("Min(234, 15, 107)", 15)]
    [InlineData("Mod(1.5, 1)", 0.5)]
    [InlineData("Round(12.389764537, 4)", 12.3898)]
    [InlineData("Round(20 / 3, 2)", 6.67)]
    [InlineData("Sum(1, 2, 3)", 6)]
    [InlineData("Count(\"a\", \"b\", null)", 2)]
    [InlineData("Oneof(3, 1, 2, 3)", 1)]
    [InlineData("Oneof(4, 1, 2, 3)", 0)]
    [InlineData("Within(5, 1, 10)", 1)]
    [InlineData("Within(11, 1, 10)", 0)]
    [InlineData("At(\"ABCDEFG\", \"CD\")", 3)]
    [InlineData("At(\"ABCDEFG\", \"XY\")", 0)]
    [InlineData("Len(\"ABCDEFGH\")", 8)]
    [InlineData("HasValue(\"  \")", 0)]
    [InlineData("HasValue(\"x\")", 1)]
    [InlineData("Date2Num(\"2000-01-01\", \"YYYY-MM-DD\")", 36525)]
    [InlineData("Date2Num(\"1/1/1900\", \"D/M/YYYY\")", 1)]
    [InlineData("IsoDate2Num(\"20000101\")", 36525)]
    [InlineData("Time2Num(\"01:02:03\", \"HH:MM:SS\")", 3723000)]
    [InlineData("Round(Pmt(10000, 0.01, 12), 2)", 888.49)]
    [InlineData("Round(FV(100, 0.01, 12), 2)", 1268.25)]
    [InlineData("Round(PV(100, 0.01, 12), 2)", 1125.51)]
    [InlineData("Round(NPV(0.1, 100, 100), 2)", 173.55)]
    public void Numeric_Results(string script, double expected) =>
        Num(script).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData("Concat(\"ABC\", \"DEF\")", "ABCDEF")]
    [InlineData("Concat(\"a\", 1, \"b\")", "a1b")]
    [InlineData("Left(\"ABCDEFGH\", 3)", "ABC")]
    [InlineData("Right(\"ABCDEFGH\", 3)", "FGH")]
    [InlineData("Lower(\"ABC\")", "abc")]
    [InlineData("Upper(\"abc\")", "ABC")]
    [InlineData("Ltrim(\"   ABC \")", "ABC ")]
    [InlineData("Rtrim(\" ABC   \")", " ABC")]
    [InlineData("Replace(\"Hello, World\", \"o\", \"0\")", "Hell0, W0rld")]
    [InlineData("Space(3)", "   ")]
    [InlineData("Substr(\"ABCDEFG\", 3, 4)", "CDEF")]
    [InlineData("Substr(\"ABC\", 5, 2)", "")]
    [InlineData("Str(2.456, 4, 2)", "2.46")]
    [InlineData("Str(4.5678, 10, 3)", "     4.568")]
    [InlineData("Str(123456, 4)", "****")]
    [InlineData("Choose(2, \"A\", \"B\", \"C\")", "B")]
    [InlineData("Choose(5, \"A\", \"B\", \"C\")", "")]
    [InlineData("If(1, \"yes\", \"no\")", "yes")]
    [InlineData("If(0, \"yes\", \"no\")", "no")]
    [InlineData("Num2Date(1, \"YYYY-MM-DD\")", "1900-01-01")]
    [InlineData("Num2Date(36525, \"MMMM D, YYYY\")", "January 1, 2000")]
    [InlineData("Num2Date(36525, \"EEE DD/MM/YY\")", "Sat 01/01/00")]
    [InlineData("Num2Time(3723000, \"HH:MM:SS\")", "01:02:03")]
    [InlineData("Num2Time(3723000, \"h:MM A\")", "1:02 AM")]
    [InlineData("Format(\"num{z,zz9.99}\", 1234.5)", "1,234.50")]
    [InlineData("Format(\"num{999}\", 7)", "007")]
    [InlineData("Format(\"date{YYYY-MM-DD}\", 36525)", "2000-01-01")]
    [InlineData("Format(\"text{(999) 999-9999}\", \"5551234567\")", "(555) 123-4567")]
    [InlineData("WordNum(123)", "One Hundred Twenty Three")]
    [InlineData("Encode(\"a b&c\", \"url\")", "a%20b%26c")]
    [InlineData("UnitType(\"36 in\")", "in")]
    public void Text_Results(string script, string expected) =>
        Str(script).Should().Be(expected);

    [Fact]
    public void UnitValue_Converts() =>
        ((double)Run("UnitValue(\"1 in\", \"pt\")")!).Should().BeApproximately(72, 1e-9);

    [Fact]
    public void ControlFlow_Loops_Functions()
    {
        Num("var x = 0\n for i = 1 upto 5 do x = x + i endfor\n x").Should().Be(15);
        Num("var x = 0\n for i = 10 downto 1 step 3 do x = x + 1 endfor\n x").Should().Be(4);
        Num("var n = 0\n while (n < 7) do n = n + 2 endwhile\n n").Should().Be(8);
        Num("var s = 0\n foreach v in (1, 2, 3) do s = s + v endfor\n s").Should().Be(6);
        Num("func sq(a) do return a * a endfunc\n sq(4)").Should().Be(16);
        Num("var n = 0\n while (1) do n = n + 1 if (n > 3) then break endif endwhile\n n").Should().Be(4);
        Num("var s = 0\n for i = 1 upto 5 do if (i == 3) then continue endif s = s + i endfor\n s").Should().Be(12);
        Str("var r = \"a\"\n if (0) then r = \"b\" elseif (1) then r = \"c\" else r = \"d\" endif\n r").Should().Be("c");
        Num("func f(a) do if (a <= 1) then return 1 endif return a * f(a - 1) endfunc\n f(5)").Should().Be(120);
        Num("var x = 1\n x\n exit\n x = 2").Should().Be(1);
    }

    [Fact]
    public void Som_UnqualifiedNames_FindSiblings_AndAssignmentSetsRawValue()
    {
        var (host, total, _) = Sample();
        // The veraPDF fixture's script: sibling fields read by bare name.
        FormCalcInterpreter.Evaluate("Number1 + Number2", host).Should().Be(7d);

        FormCalcInterpreter.Evaluate("Total = Number1 + Number2", host);
        total.Raw.Should().Be(7d);

        FormCalcInterpreter.Evaluate("$ = 9", host);
        total.Raw.Should().Be(9d);
    }

    [Fact]
    public void Som_Members_Indexes_Roots_AndResolveNode()
    {
        var (host, _, _) = Sample();
        Num("sf.Number1").Should().Be(3);
        Str("$form.Outside").Should().Be("out");
        Str("xfa.resolveNode(\"form1.Outside\")").Should().Be("out");
        FormCalcInterpreter.Evaluate("xfa.resolveNode(\"sf.Number2\")", host).Should().BeAssignableTo<IFcObject>();
        Num("Sum(sf.*)").Should().Be(7);
        Num("Count(sf.*)").Should().Be(2);
        Num("sf.Number1[0]").Should().Be(3);
        Num("Exists(Number1)").Should().Be(1);
        Num("Exists(Nothing)").Should().Be(0);
        Num("Exists(sf..Number2)").Should().Be(1);
        Str("Concat(Outside, \"!\")").Should().Be("out!");
    }

    [Fact]
    public void ReadOnlyTarget_RefusesTheAssignment()
    {
        var ro = new Node("Locked", value: 1d) { ReadOnly = true };
        var host = new Host { Context = ro };
        var act = () => FormCalcInterpreter.Evaluate("$ = 2", host);
        act.Should().Throw<FormCalcRuntimeException>();
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("Mod(1, 0)")]
    [InlineData("Avg(null)")]
    [InlineData("Nope(1)")]
    [InlineData("Abs()")]
    [InlineData("Abs(1, 2)")]
    [InlineData("for i = 1 upto 5 step 0 do endfor")]
    [InlineData("throw \"stop\"")]
    [InlineData("Num2Date(0)")]
    [InlineData("Date2Num(\"not a date\", \"YYYY-MM-DD\")")]
    [InlineData("var s = \"x\"\n s.y = 1")]
    public void Errors_Abort_TheScript(string script)
    {
        var act = () => Run(script);
        act.Should().Throw<FormCalcRuntimeException>();
    }

    [Theory]
    [InlineData("Get(\"http://example.com\")")]
    [InlineData("Post(\"http://example.com\", \"x\")")]
    [InlineData("Put(\"http://example.com\", \"x\")")]
    [InlineData("xfa.host.messageBox(\"hi\")")]
    [InlineData("xfa.host.exportData(\"a.xml\")")]
    [InlineData("xfa.host.launchURL(\"http://example.com\")")]
    [InlineData("$form.execEvent(\"click\")")]
    public void HostFunctions_DoNotExist(string script)
    {
        var act = () => Run(script);
        act.Should().Throw<FormCalcRuntimeException>();
    }

    [Fact]
    public void NoBuiltinReachesTheOutsideWorld()
    {
        FormCalcBuiltins.Names.Should().NotContain(n => n.Equals("Get", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Post", StringComparison.OrdinalIgnoreCase) || n.Equals("Put", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InfiniteLoop_IsStopped_ByTheStepLimit()
    {
        var act = () => Run("while (1) do endwhile", limits: new FcLimits { MaxSteps = 50_000 });
        act.Should().Throw<FormCalcRuntimeException>().WithMessage("*steps*");
    }

    [Fact]
    public void InfiniteLoop_IsStopped_ByTheClock()
    {
        var act = () => Run("while (1) do var x = 1 endwhile", limits: new FcLimits { MaxSteps = long.MaxValue, TimeLimit = TimeSpan.FromMilliseconds(150) });
        act.Should().Throw<FormCalcRuntimeException>().WithMessage("*longer*");
    }

    [Fact]
    public void RunawayRecursion_IsStopped()
    {
        var act = () => Run("func f() do return f() endfunc\n f()");
        act.Should().Throw<FormCalcRuntimeException>().WithMessage("*deeper*");
    }

    [Fact]
    public void StringDoubling_IsStopped()
    {
        var act = () => Run("var s = \"aaaaaaaa\"\n while (1) do s = Concat(s, s) endwhile");
        act.Should().Throw<FormCalcRuntimeException>().WithMessage("*characters*");
    }

    [Fact]
    public void HugeSpace_IsRefused()
    {
        var act = () => Run("Space(2000000000)");
        act.Should().Throw<FormCalcRuntimeException>();
    }

    [Fact]
    public void Eval_Nesting_IsBoundedAndSharesTheBudget()
    {
        Num("Eval(\"1 + 2\")").Should().Be(3);
        var act = () => Run("func f(n) do return Eval(Concat(\"f(\", n, \")\")) endfunc\n f(1)");
        act.Should().Throw<FormCalcRuntimeException>();
    }

    [Fact]
    public void Cancellation_StopsTheScript()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => FormCalcInterpreter.Evaluate("while (1) do endwhile", Sample().Host,
            new FcLimits { MaxSteps = long.MaxValue }, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ManyMatches_AreBounded()
    {
        var wide = new Node("wide", "subform");
        for (var i = 0; i < 300; i++) wide.Add(new Node("x", value: 1d));
        var host = new Host { Context = wide };
        var act = () => FormCalcInterpreter.Evaluate("wide..x", host, new FcLimits { MaxListItems = 100 });
        // Either the bound trips or the (small) result is returned; it must never run away.
        act.Should().NotThrow<OutOfMemoryException>();
        var tight = () => FormCalcInterpreter.Evaluate("Sum(wide..x)", host, new FcLimits { MaxListItems = 100 });
        tight.Should().Throw<FormCalcRuntimeException>();
    }
}
