using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Excise.Core.Xfa.FormCalc;

/// <summary>
/// A tree-walking FormCalc interpreter (#1570). It can reach exactly what <see cref="IFcHost"/> and
/// <see cref="IFcObject"/> expose and the pure built-ins in <see cref="FormCalcBuiltins"/>; there is no
/// reflection, no dynamic code, and no built-in that touches a file, the network or the clipboard
/// (Get, Post, Put and the host functions do not exist rather than being stubbed).
/// Every statement, loop turn and call ticks a step counter, the wall clock and the cancellation
/// token, so a hostile script costs at most <see cref="FcLimits"/>.
/// </summary>
internal sealed class FormCalcInterpreter
{
    private enum Flow { Normal, Break, Continue, Return, Exit }

    private sealed class Frame
    {
        public readonly Dictionary<string, object?> Locals = new(StringComparer.Ordinal);
    }

    private readonly IFcHost _host;
    private readonly CancellationToken _cancellation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, FcFunc> _functions = new(StringComparer.Ordinal);
    private readonly int _evalDepth;
    private long _steps;
    private int _callDepth;
    private object? _last;
    private object? _returnValue;

    public FormCalcInterpreter(IFcHost host, FcLimits? limits = null, CancellationToken cancellation = default, int evalDepth = 0)
    {
        _host = host;
        Limits = limits ?? new FcLimits();
        _cancellation = cancellation;
        _evalDepth = evalDepth;
    }

    public FcLimits Limits { get; }

    public IFcHost Host => _host;

    /// <summary>Parse and run <paramref name="source"/>; the result is the value of its last expression.</summary>
    public static object? Evaluate(string source, IFcHost host, FcLimits? limits = null, CancellationToken cancellation = default)
    {
        var script = FormCalcParser.Parse(source);
        return new FormCalcInterpreter(host, limits, cancellation).Run(script);
    }

    public object? Run(FcScript script)
    {
        foreach (var s in script.Statements)
            if (s is FcFunc f) _functions[f.Name] = f;

        var frame = new Frame();
        try
        {
            var flow = ExecBlock(script.Statements, frame);
            return flow == Flow.Return ? _returnValue : _last;
        }
        catch (InsufficientExecutionStackException)
        {
            throw new FormCalcRuntimeException("The script nests too deeply.");
        }
        catch (ScriptExit)
        {
            return _last;
        }
    }

    // Limits -----------------------------------------------------------------

    internal void Tick()
    {
        if (++_steps > Limits.MaxSteps)
            throw new FormCalcRuntimeException($"The script ran more than {Limits.MaxSteps} steps.");
        if ((_steps & 0xFF) == 0)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_clock.Elapsed > Limits.TimeLimit)
                throw new FormCalcRuntimeException($"The script ran longer than {Limits.TimeLimit.TotalSeconds:0.#} s.");
        }
    }

    internal void CheckLength(int length)
    {
        if (length > Limits.MaxStringLength)
            throw new FormCalcRuntimeException($"A string grew past {Limits.MaxStringLength} characters.");
    }

    internal string CheckString(string s)
    {
        if (s.Length > Limits.MaxStringLength)
            throw new FormCalcRuntimeException($"A string grew past {Limits.MaxStringLength} characters.");
        return s;
    }

    // Statements -------------------------------------------------------------

    private Flow ExecBlock(IReadOnlyList<FcStmt> block, Frame f)
    {
        foreach (var s in block)
        {
            var flow = Exec(s, f);
            if (flow != Flow.Normal) return flow;
        }
        return Flow.Normal;
    }

    private Flow Exec(FcStmt stmt, Frame f)
    {
        Tick();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (stmt)
        {
            case FcExprStmt e:
                _last = Eval(e.Expr, f);
                return Flow.Normal;
            case FcVar v:
                f.Locals[v.Name] = v.Init == null ? null : Scalar(Eval(v.Init, f));
                return Flow.Normal;
            case FcIf i:
                if (FormCalcValue.IsTrue(Eval(i.Condition, f)))
                    return ExecBlock(i.Then, f);
                return i.Else == null ? Flow.Normal : ExecBlock(i.Else, f);
            case FcWhile w:
                while (FormCalcValue.IsTrue(Eval(w.Condition, f)))
                {
                    Tick();
                    var flow = ExecBlock(w.Body, f);
                    if (flow == Flow.Break) break;
                    if (flow is Flow.Return or Flow.Exit) return flow;
                }
                return Flow.Normal;
            case FcDo d:
            {
                var flow = ExecBlock(d.Body, f);
                return flow is Flow.Break or Flow.Continue ? Flow.Normal : flow;
            }
            case FcFor fo:
                return ExecFor(fo, f);
            case FcForEach fe:
                return ExecForEach(fe, f);
            case FcFunc fn:
                _functions[fn.Name] = fn;
                return Flow.Normal;
            case FcReturn r:
                _returnValue = r.Value == null ? null : Scalar(Eval(r.Value, f));
                return Flow.Return;
            case FcBreak: return Flow.Break;
            case FcContinue: return Flow.Continue;
            case FcExit: return Flow.Exit;
            case FcThrow t:
                throw new FormCalcRuntimeException("throw: " + FormCalcValue.ToText(Eval(t.Value, f)));
            default:
                throw new FormCalcRuntimeException("Unsupported statement.");
        }
    }

    private Flow ExecFor(FcFor fo, Frame f)
    {
        var i = FormCalcValue.ToNumber(Eval(fo.Start, f));
        var end = FormCalcValue.ToNumber(Eval(fo.End, f));
        var step = fo.Step == null ? 1 : FormCalcValue.ToNumber(Eval(fo.Step, f));
        if (step == 0 || double.IsNaN(step) || double.IsNaN(i) || double.IsNaN(end))
            throw new FormCalcRuntimeException("A for loop needs a non-zero, numeric step and bounds.");
        step = Math.Abs(step);
        if (fo.Down) step = -step;
        for (; fo.Down ? i >= end : i <= end; i += step)
        {
            Tick();
            f.Locals[fo.Variable] = i;
            var flow = ExecBlock(fo.Body, f);
            if (flow == Flow.Break) break;
            if (flow is Flow.Return or Flow.Exit) return flow;
        }
        return Flow.Normal;
    }

    private Flow ExecForEach(FcForEach fe, Frame f)
    {
        var items = new List<object?>();
        foreach (var expr in fe.Items)
        {
            var v = Eval(expr, f);
            if (v is FcNodeList list) items.AddRange(list);
            else items.Add(v);
            if (items.Count > Limits.MaxListItems)
                throw new FormCalcRuntimeException("A foreach list is too long.");
        }
        foreach (var item in items)
        {
            Tick();
            f.Locals[fe.Variable] = item;
            var flow = ExecBlock(fe.Body, f);
            if (flow == Flow.Break) break;
            if (flow is Flow.Return or Flow.Exit) return flow;
        }
        return Flow.Normal;
    }

    // Expressions ------------------------------------------------------------

    private static object? Scalar(object? v) => v is IFcObject or FcNodeList ? FormCalcValue.Scalar(v) : v;

    private object? Eval(FcExpr e, Frame f)
    {
        Tick();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (e)
        {
            case FcNumber n: return n.Value;
            case FcString s: return s.Value;
            case FcNull: return null;
            case FcThis: return _host.Context;
            case FcName n: return ResolveName(n.Name, f);
            case FcUnary u: return EvalUnary(u, f);
            case FcBinary b: return EvalBinary(b, f);
            case FcCall c: return EvalCall(c, f);
            case FcMember m: return EvalMember(m, f);
            case FcIndex i: return EvalIndex(i, f);
            case FcAssign a: return Assign(a, f);
            default: throw new FormCalcRuntimeException("Unsupported expression.");
        }
    }

    private object? EvalUnary(FcUnary u, Frame f)
    {
        var v = Eval(u.Operand, f);
        return u.Op switch
        {
            "-" => -FormCalcValue.ToNumber(v),
            "+" => FormCalcValue.ToNumber(v),
            _ => FormCalcValue.Bool(!FormCalcValue.IsTrue(v)),
        };
    }

    private object? EvalBinary(FcBinary b, Frame f)
    {
        if (b.Op == "and")
            return FormCalcValue.Bool(FormCalcValue.IsTrue(Eval(b.Left, f)) && FormCalcValue.IsTrue(Eval(b.Right, f)));
        if (b.Op == "or")
            return FormCalcValue.Bool(FormCalcValue.IsTrue(Eval(b.Left, f)) || FormCalcValue.IsTrue(Eval(b.Right, f)));

        var l = Eval(b.Left, f);
        var r = Eval(b.Right, f);
        switch (b.Op)
        {
            case "+": return FormCalcValue.ToNumber(l) + FormCalcValue.ToNumber(r);
            case "-": return FormCalcValue.ToNumber(l) - FormCalcValue.ToNumber(r);
            case "*": return FormCalcValue.ToNumber(l) * FormCalcValue.ToNumber(r);
            case "/":
            {
                var d = FormCalcValue.ToNumber(r);
                if (d == 0) throw new FormCalcRuntimeException("Division by zero.");
                return FormCalcValue.ToNumber(l) / d;
            }
            case "==": return FormCalcValue.Bool(FormCalcValue.Compare(l, r) == 0);
            case "<>": return FormCalcValue.Bool(FormCalcValue.Compare(l, r) != 0);
            case "<": return FormCalcValue.Bool(FormCalcValue.Compare(l, r) < 0);
            case "<=": return FormCalcValue.Bool(FormCalcValue.Compare(l, r) <= 0);
            case ">": return FormCalcValue.Bool(FormCalcValue.Compare(l, r) > 0);
            case ">=": return FormCalcValue.Bool(FormCalcValue.Compare(l, r) >= 0);
            default: throw new FormCalcRuntimeException("Unsupported operator " + b.Op);
        }
    }

    // Names and SOM ------------------------------------------------------------

    private object? ResolveName(string name, Frame f)
    {
        if (f.Locals.TryGetValue(name, out var local))
            return local;
        if (name == "$") return _host.Context;
        if (name[0] == '$' || name[0] == '!' || name == "xfa")
        {
            var root = _host.ResolveRoot(name);
            return root switch
            {
                null => new FcNodeList(),
                IFcObject o => new FcNodeList { o },
                FcNodeList l => l,
                _ => new FcNodeList(),
            };
        }

        // An unqualified name: search the current container's children, then each ancestor's.
        for (IFcObject? scope = _host.Context; scope != null; scope = scope.Parent)
        {
            Tick();
            var matches = ChildrenNamed(scope, name);
            if (matches.Count > 0) return matches;
            // The outermost container has no parent to list it as a child.
            if (scope.Parent == null && string.Equals(scope.Name, name, StringComparison.Ordinal))
                return new FcNodeList { scope };
        }
        return new FcNodeList();
    }

    private FcNodeList ChildrenNamed(IFcObject o, string name)
    {
        var list = new FcNodeList();
        foreach (var c in o.Children)
        {
            Tick();
            if (name == "*" || string.Equals(c.Name, name, StringComparison.Ordinal))
                list.Add(c);
        }
        return list;
    }

    private static IEnumerable<IFcObject> Objects(object? v) => v switch
    {
        IFcObject o => new[] { o },
        FcNodeList l => l,
        _ => Array.Empty<IFcObject>(),
    };

    private object? EvalMember(FcMember m, Frame f)
    {
        var target = Eval(m.Target, f);
        var objects = Objects(target).ToList();
        var result = new FcNodeList();
        foreach (var o in objects)
        {
            Tick();
            switch (m.Kind)
            {
                case FcMemberKind.Descendant:
                    CollectDescendants(o, m.Name, result);
                    break;
                case FcMemberKind.HashChild:
                    foreach (var c in o.Children)
                        if (string.Equals(c.ClassName, m.Name, StringComparison.Ordinal)) result.Add(c);
                    break;
                default:
                    result.AddRange(ChildrenNamed(o, m.Name));
                    break;
            }
            if (result.Count > Limits.MaxListItems)
                throw new FormCalcRuntimeException("A SOM expression matched too many objects.");
        }

        // No child by that name: a property of a single object (rawValue, presence...).
        if (result.Count == 0 && objects.Count == 1 && m.Name != "*" &&
            objects[0].TryGetProperty(m.Name, out var property))
            return property;
        return result;
    }

    private void CollectDescendants(IFcObject o, string name, FcNodeList into)
    {
        foreach (var c in o.Children)
        {
            Tick();
            if (name == "*" || string.Equals(c.Name, name, StringComparison.Ordinal)) into.Add(c);
            CollectDescendants(c, name, into);
            if (into.Count > Limits.MaxListItems)
                throw new FormCalcRuntimeException("A SOM expression matched too many objects.");
        }
    }

    private object? EvalIndex(FcIndex i, Frame f)
    {
        var target = Eval(i.Target, f);
        var objects = Objects(target).ToList();
        if (i.Index == null) return new FcNodeList(objects);
        var n = (int)Math.Clamp(FormCalcValue.ToNumber(Eval(i.Index, f)), int.MinValue, int.MaxValue);
        return n >= 0 && n < objects.Count ? new FcNodeList { objects[n] } : new FcNodeList();
    }

    // Assignment -----------------------------------------------------------------

    private object? Assign(FcAssign a, Frame f)
    {
        var value = Scalar(Eval(a.Value, f));
        switch (a.Target)
        {
            case FcName n when f.Locals.ContainsKey(n.Name):
                f.Locals[n.Name] = value;
                return value;
            case FcMember m:
            {
                var target = Eval(m.Target, f);
                var objects = Objects(target).ToList();
                if (objects.Count == 1)
                {
                    var children = m.Kind == FcMemberKind.Child ? ChildrenNamed(objects[0], m.Name) : new FcNodeList();
                    if (children.Count > 0)
                        return SetRawValue(children[0], value);
                    if (objects[0].TrySetProperty(m.Name, value))
                        return value;
                }
                throw new FormCalcRuntimeException($"Cannot assign to '{m.Name}'.");
            }
            default:
            {
                var t = Eval(a.Target, f);
                var objects = Objects(t).ToList();
                if (objects.Count >= 1)
                    return SetRawValue(objects[0], value);
                throw new FormCalcRuntimeException("The left side of '=' does not name an object.");
            }
        }
    }

    private static object? SetRawValue(IFcObject target, object? value)
    {
        if (!target.TrySetProperty("rawValue", value))
            throw new FormCalcRuntimeException($"'{target.Name}' cannot take a value.");
        return value;
    }

    // Calls ------------------------------------------------------------------------

    private object? EvalCall(FcCall c, Frame f)
    {
        var args = new List<object?>(c.Arguments.Count);
        foreach (var arg in c.Arguments)
            args.Add(Eval(arg, f));

        if (c.Callee is FcName name)
        {
            if (_functions.TryGetValue(name.Name, out var user))
                return CallUser(user, args);
            if (FormCalcBuiltins.TryGet(name.Name, out var builtin))
                return builtin(this, args);
            throw new FormCalcRuntimeException($"Unknown function '{name.Name}'.");
        }

        if (c.Callee is FcMember { Kind: FcMemberKind.Child } member)
        {
            // The only host methods a script may call are the two that look objects up.
            var owner = Objects(Eval(member.Target, f)).FirstOrDefault();
            if (owner != null && member.Name is "resolveNode" or "resolveNodes" && args.Count == 1)
                return ResolveSom(FormCalcValue.ToText(args[0]), owner, single: member.Name == "resolveNode");
        }
        throw new FormCalcRuntimeException("That method is not available to scripts.");
    }

    private object? CallUser(FcFunc fn, List<object?> args)
    {
        if (++_callDepth > Limits.MaxCallDepth)
            throw new FormCalcRuntimeException($"Functions nest deeper than {Limits.MaxCallDepth} calls.");
        try
        {
            var frame = new Frame();
            for (var i = 0; i < fn.Parameters.Count; i++)
                frame.Locals[fn.Parameters[i]] = i < args.Count ? Scalar(args[i]) : null;
            _returnValue = null;
            var flow = ExecBlock(fn.Body, frame);
            var result = flow == Flow.Return ? _returnValue : null;
            if (flow == Flow.Exit) throw new ScriptExit();
            return result;
        }
        finally { _callDepth--; }
    }

    /// <summary>Raised by <c>exit</c> inside a function; ends the script normally.</summary>
    private sealed class ScriptExit : Exception { }

    private object? ResolveSom(string expression, IFcObject start, bool single)
    {
        FcScript script;
        try { script = FormCalcParser.Parse(expression); }
        catch (FormCalcSyntaxException) { return new FcNodeList(); }
        if (script.Statements is not [FcExprStmt { Expr: var expr }] || !IsAccessor(expr))
            return new FcNodeList();

        var sub = new FormCalcInterpreter(new ScopedHost(_host, start), Limits, _cancellation, _evalDepth);
        var result = sub.Eval(expr, new Frame());
        var list = new FcNodeList(Objects(result));
        return single ? (list.Count > 0 ? list[0] : null) : list;
    }

    private static bool IsAccessor(FcExpr e) => e switch
    {
        FcName => true,
        FcThis => true,
        FcMember m => IsAccessor(m.Target),
        FcIndex i => IsAccessor(i.Target) && (i.Index is null or FcNumber),
        _ => false,
    };

    private sealed class ScopedHost(IFcHost inner, IFcObject context) : IFcHost
    {
        public IFcObject Context => context;
        public object? ResolveRoot(string name) => inner.ResolveRoot(name);
    }

    // Used by Eval() in the built-ins.
    internal object? RunNested(string source)
    {
        if (_evalDepth >= Limits.MaxEvalDepth)
            throw new FormCalcRuntimeException("Eval nests too deeply.");
        FcScript script;
        try { script = FormCalcParser.Parse(source); }
        catch (FormCalcSyntaxException ex) { throw new FormCalcRuntimeException("Eval: " + ex.Message); }
        // The nested script spends what is left of this one's budget, so nesting cannot multiply it.
        var remaining = new FcLimits
        {
            MaxSteps = Math.Max(1, Limits.MaxSteps - _steps),
            MaxCallDepth = Math.Max(1, Limits.MaxCallDepth - _callDepth),
            MaxStringLength = Limits.MaxStringLength,
            MaxListItems = Limits.MaxListItems,
            MaxEvalDepth = Limits.MaxEvalDepth,
            TimeLimit = Limits.TimeLimit - _clock.Elapsed > TimeSpan.Zero ? Limits.TimeLimit - _clock.Elapsed : TimeSpan.FromMilliseconds(1),
        };
        return new FormCalcInterpreter(_host, remaining, _cancellation, _evalDepth + 1).Run(script);
    }
}
