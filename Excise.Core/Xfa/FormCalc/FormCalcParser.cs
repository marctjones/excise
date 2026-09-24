using static Excise.Core.Xfa.FormCalc.FormCalcTokenKind;

namespace Excise.Core.Xfa.FormCalc;

/// <summary>
/// Recursive-descent parser for FormCalc. Statements are simply juxtaposed (newlines carry no
/// meaning), as in the reference engines. <c>=</c> is assignment only at the start of a
/// statement; inside a condition or an argument it compares.
/// </summary>
internal sealed class FormCalcParser
{
    /// <summary>Deepest nesting of expressions and blocks. A hostile script costs a syntax error, not the stack.</summary>
    internal const int MaxDepth = 200;

    /// <summary>Most tokens read from one script.</summary>
    internal const int MaxTokens = 200_000;

    private readonly FormCalcLexer _lexer;
    private FormCalcToken _current;
    private int _depth;
    /// <summary>Greater than zero inside parentheses, call arguments and subscripts, where <c>=</c> compares.</summary>
    private int _nesting;
    private int _tokens;

    private FormCalcParser(string text)
    {
        _lexer = new FormCalcLexer(text);
        _current = _lexer.Next();
    }

    public static FcScript Parse(string text) => new FormCalcParser(text).ParseScript();

    private FcScript ParseScript()
    {
        var statements = ParseBlock();
        if (_current.Kind != Eof)
            throw Error($"Unexpected '{_current.Text}'");
        return new FcScript(statements);
    }

    // Plumbing ---------------------------------------------------------------

    private FormCalcToken Advance()
    {
        var t = _current;
        if (++_tokens > MaxTokens)
            throw new FormCalcSyntaxException($"The script has more than {MaxTokens} tokens", t.Position);
        _current = _lexer.Next();
        return t;
    }

    private bool Accept(FormCalcTokenKind kind)
    {
        if (_current.Kind != kind) return false;
        Advance();
        return true;
    }

    private void Expect(FormCalcTokenKind kind, string what)
    {
        if (!Accept(kind))
            throw Error($"Expected {what}");
    }

    private FormCalcSyntaxException Error(string message) => new(message, _current.Position);

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw Error($"The script nests deeper than {MaxDepth} levels");
    }

    private void Leave() => _depth--;

    private static bool EndsBlock(FormCalcTokenKind k) =>
        k is Eof or Else or ElseIf or EndIf or EndWhile or EndFor or EndFunc or End;

    // Statements -------------------------------------------------------------

    private List<FcStmt> ParseBlock()
    {
        Enter();
        var list = new List<FcStmt>();
        while (!EndsBlock(_current.Kind))
            list.Add(ParseStatement());
        Leave();
        return list;
    }

    private FcStmt ParseStatement()
    {
        switch (_current.Kind)
        {
            case Var: return ParseVar();
            case If: return ParseIf();
            case While: return ParseWhile();
            case For: return ParseFor();
            case ForEach: return ParseForEach();
            case Func: return ParseFunc();
            case Do:
            {
                Advance();
                var body = ParseBlock();
                Expect(End, "'end'");
                return new FcDo(body);
            }
            case Return:
            {
                Advance();
                return new FcReturn(StartsExpression(_current.Kind) ? ParseExpression() : null);
            }
            case Break: Advance(); return new FcBreak();
            case Continue: Advance(); return new FcContinue();
            case Exit: Advance(); return new FcExit();
            case Throw: Advance(); return new FcThrow(ParseExpression());
            default:
                return new FcExprStmt(ParseAssignment());
        }
    }

    private static bool StartsExpression(FormCalcTokenKind k) =>
        k is Number or FormCalcTokenKind.String or Identifier or Null or This or LeftParen or Plus or Minus or Not;

    private FcStmt ParseVar()
    {
        Advance();
        var name = _current.Kind == Identifier ? Advance().Text : throw Error("Expected a variable name");
        FcExpr? init = null;
        if (Accept(Assign))
            init = ParseExpression();
        return new FcVar(name, init);
    }

    private FcStmt ParseIf()
    {
        Advance();
        Expect(LeftParen, "'('");
        _nesting++;
        var condition = ParseExpression();
        if (_current.Kind == Comma)
        {
            // Not the statement at all: If(cond, a, b), the function, used as a statement.
            var args = new List<FcExpr> { condition };
            while (Accept(Comma)) args.Add(ParseExpression());
            _nesting--;
            Expect(RightParen, "')'");
            return new FcExprStmt(new FcCall(new FcName("If"), args));
        }
        _nesting--;
        Expect(RightParen, "')'");
        Expect(Then, "'then'");
        var then = ParseBlock();
        IReadOnlyList<FcStmt>? otherwise = null;
        if (_current.Kind == ElseIf)
        {
            // elseif (c) then ... is an if inside the else branch.
            otherwise = new List<FcStmt> { ParseElseIf() };
            return new FcIf(condition, then, otherwise);
        }
        if (Accept(Else))
            otherwise = ParseBlock();
        Expect(EndIf, "'endif'");
        return new FcIf(condition, then, otherwise);
    }

    private FcStmt ParseElseIf()
    {
        Advance(); // elseif
        var condition = ParseParenthesised();
        Expect(Then, "'then'");
        var then = ParseBlock();
        IReadOnlyList<FcStmt>? otherwise = null;
        if (_current.Kind == ElseIf)
            return new FcIf(condition, then, new List<FcStmt> { ParseElseIf() });
        if (Accept(Else))
            otherwise = ParseBlock();
        Expect(EndIf, "'endif'");
        return new FcIf(condition, then, otherwise);
    }

    private FcStmt ParseWhile()
    {
        Advance();
        var condition = ParseParenthesised();
        Expect(Do, "'do'");
        var body = ParseBlock();
        Expect(EndWhile, "'endwhile'");
        return new FcWhile(condition, body);
    }

    private FcStmt ParseFor()
    {
        Advance();
        var name = _current.Kind == Identifier ? Advance().Text : throw Error("Expected a loop variable");
        Expect(Assign, "'='");
        var start = ParseExpression();
        bool down;
        if (Accept(Upto)) down = false;
        else if (Accept(Downto)) down = true;
        else throw Error("Expected 'upto' or 'downto'");
        var end = ParseExpression();
        FcExpr? step = Accept(Step) ? ParseExpression() : null;
        Expect(Do, "'do'");
        var body = ParseBlock();
        Expect(EndFor, "'endfor'");
        return new FcFor(name, start, down, end, step, body);
    }

    private FcStmt ParseForEach()
    {
        Advance();
        var name = _current.Kind == Identifier ? Advance().Text : throw Error("Expected a loop variable");
        Expect(In, "'in'");
        Expect(LeftParen, "'('");
        var items = new List<FcExpr>();
        if (_current.Kind != RightParen)
        {
            do items.Add(ParseExpression()); while (Accept(Comma));
        }
        Expect(RightParen, "')'");
        Expect(Do, "'do'");
        var body = ParseBlock();
        Expect(EndFor, "'endfor'");
        return new FcForEach(name, items, body);
    }

    private FcStmt ParseFunc()
    {
        Advance();
        var name = _current.Kind == Identifier ? Advance().Text : throw Error("Expected a function name");
        Expect(LeftParen, "'('");
        var parameters = new List<string>();
        if (_current.Kind != RightParen)
        {
            do
            {
                parameters.Add(_current.Kind == Identifier ? Advance().Text : throw Error("Expected a parameter name"));
            } while (Accept(Comma));
        }
        Expect(RightParen, "')'");
        Expect(Do, "'do'");
        var body = ParseBlock();
        Expect(EndFunc, "'endfunc'");
        return new FcFunc(name, parameters, body);
    }

    private FcExpr ParseParenthesised()
    {
        Expect(LeftParen, "'('");
        _nesting++;
        var e = ParseExpression();
        _nesting--;
        Expect(RightParen, "')'");
        return e;
    }

    // Expressions ------------------------------------------------------------

    /// <summary>An expression statement: <c>target = value</c> when the left side can be assigned to.</summary>
    private FcExpr ParseAssignment()
    {
        var left = ParseExpression();
        if (_current.Kind == Assign)
        {
            if (left is not (FcName or FcMember or FcIndex))
                throw Error("The left side of '=' cannot be assigned to");
            Advance();
            return new FcAssign(left, ParseExpression());
        }
        return left;
    }

    private FcExpr ParseExpression() => ParseOr();

    private FcExpr ParseOr()
    {
        Enter();
        var left = ParseAnd();
        while (_current.Kind == Or) { Advance(); left = new FcBinary("or", left, ParseAnd()); }
        Leave();
        return left;
    }

    private FcExpr ParseAnd()
    {
        var left = ParseEquality();
        while (_current.Kind == And) { Advance(); left = new FcBinary("and", left, ParseEquality()); }
        return left;
    }

    private FcExpr ParseEquality()
    {
        var left = ParseRelational();
        while (true)
        {
            if (_current.Kind == Eq || (_current.Kind == Assign && _nesting > 0))
            {
                Advance();
                left = new FcBinary("==", left, ParseRelational());
            }
            else if (_current.Kind == Ne) { Advance(); left = new FcBinary("<>", left, ParseRelational()); }
            else return left;
        }
    }

    private FcExpr ParseRelational()
    {
        var left = ParseAdditive();
        while (true)
        {
            string? op = _current.Kind switch { Lt => "<", Le => "<=", Gt => ">", Ge => ">=", _ => null };
            if (op == null) return left;
            Advance();
            left = new FcBinary(op, left, ParseAdditive());
        }
    }

    private FcExpr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (_current.Kind is Plus or Minus)
        {
            var op = Advance().Kind == Plus ? "+" : "-";
            left = new FcBinary(op, left, ParseMultiplicative());
        }
        return left;
    }

    private FcExpr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (_current.Kind is Times or Divide)
        {
            var op = Advance().Kind == Times ? "*" : "/";
            left = new FcBinary(op, left, ParseUnary());
        }
        return left;
    }

    private FcExpr ParseUnary()
    {
        if (_current.Kind is Plus or Minus or Not)
        {
            Enter();
            var op = Advance().Kind switch { Plus => "+", Minus => "-", _ => "not" };
            var operand = ParseUnary();
            Leave();
            return new FcUnary(op, operand);
        }
        return ParsePostfix();
    }

    private FcExpr ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            switch (_current.Kind)
            {
                case LeftParen:
                {
                    Advance();
                    var args = new List<FcExpr>();
                    _nesting++;
                    if (_current.Kind != RightParen)
                    {
                        do args.Add(ParseExpression()); while (Accept(Comma));
                    }
                    _nesting--;
                    Expect(RightParen, "')'");
                    expr = new FcCall(expr, args);
                    break;
                }
                case LeftBracket:
                {
                    Advance();
                    _nesting++;
                    FcExpr? index = _current.Kind == Times ? null : ParseExpression();
                    _nesting--;
                    if (index == null) Advance();
                    Expect(RightBracket, "']'");
                    expr = new FcIndex(expr, index);
                    break;
                }
                case Dot: Advance(); expr = ParseMember(expr, FcMemberKind.Child); break;
                case DotDot: Advance(); expr = ParseMember(expr, FcMemberKind.Descendant); break;
                case DotHash: Advance(); expr = ParseMember(expr, FcMemberKind.HashChild); break;
                case DotStar: Advance(); expr = new FcMember(expr, FcMemberKind.Child, "*"); break;
                default: return expr;
            }
        }
    }

    private FcExpr ParseMember(FcExpr target, FcMemberKind kind)
    {
        if (_current.Kind == Times) { Advance(); return new FcMember(target, kind, "*"); }
        // A keyword is a fine element name after a dot (a field called "end", "step", "in"...).
        if (_current.Kind == Identifier || IsKeyword(_current.Kind))
            return new FcMember(target, kind, Advance().Text);
        throw Error("Expected a name after '.'");
    }

    private static bool IsKeyword(FormCalcTokenKind k) => k >= Break;

    private FcExpr ParsePrimary()
    {
        Enter();
        try
        {
            switch (_current.Kind)
            {
                case Number: return new FcNumber(Advance().Number);
                case FormCalcTokenKind.String: return new FcString(Advance().Text);
                case Null:
                    // Null is a keyword, and also a function with no arguments: Null().
                    Advance();
                    return _current.Kind == LeftParen ? new FcName("Null") : new FcNull();
                case If when true:
                    // "if" starts a statement, but in an expression If(cond, a, b) is the function.
                    Advance();
                    if (_current.Kind != LeftParen) throw Error("Expected '(' after If");
                    return new FcName("If");
                case This: Advance(); return new FcThis();
                case Identifier: return new FcName(Advance().Text);
                case LeftParen:
                {
                    Advance();
                    _nesting++;
                    var e = ParseExpression();
                    _nesting--;
                    Expect(RightParen, "')'");
                    return e;
                }
                default:
                    throw Error(_current.Kind == Eof ? "The script ends inside an expression" : $"Unexpected '{_current.Text}'");
            }
        }
        finally { Leave(); }
    }
}
