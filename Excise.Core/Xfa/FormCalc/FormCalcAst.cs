namespace Excise.Core.Xfa.FormCalc;

// Expressions ---------------------------------------------------------------

internal abstract record FcExpr;

internal sealed record FcNumber(double Value) : FcExpr;
internal sealed record FcString(string Value) : FcExpr;
internal sealed record FcNull : FcExpr;
internal sealed record FcThis : FcExpr;

/// <summary>A bare name: a variable, or a SOM root such as <c>$</c>, <c>$record</c>, <c>!</c>, <c>xfa</c>.</summary>
internal sealed record FcName(string Name) : FcExpr;

/// <summary>+ - not (unary). Operator is one of "+", "-", "not".</summary>
internal sealed record FcUnary(string Op, FcExpr Operand) : FcExpr;

/// <summary>Operator is one of + - * / == &lt;&gt; &lt; &lt;= &gt; &gt;= and or.</summary>
internal sealed record FcBinary(string Op, FcExpr Left, FcExpr Right) : FcExpr;

internal sealed record FcCall(FcExpr Callee, IReadOnlyList<FcExpr> Arguments) : FcExpr;

internal enum FcMemberKind { Child, Descendant, HashChild }

/// <summary><c>a.b</c>, <c>a..b</c>, <c>a.#b</c>, <c>a.*</c> (Name is "*").</summary>
internal sealed record FcMember(FcExpr Target, FcMemberKind Kind, string Name) : FcExpr;

/// <summary><c>a[3]</c> or <c>a[*]</c> (Index is null for the star).</summary>
internal sealed record FcIndex(FcExpr Target, FcExpr? Index) : FcExpr;

/// <summary>Only produced at statement level; the target is a name, member or index.</summary>
internal sealed record FcAssign(FcExpr Target, FcExpr Value) : FcExpr;

// Statements ----------------------------------------------------------------

internal abstract record FcStmt;

internal sealed record FcExprStmt(FcExpr Expr) : FcStmt;
internal sealed record FcVar(string Name, FcExpr? Init) : FcStmt;
internal sealed record FcIf(FcExpr Condition, IReadOnlyList<FcStmt> Then, IReadOnlyList<FcStmt>? Else) : FcStmt;
internal sealed record FcWhile(FcExpr Condition, IReadOnlyList<FcStmt> Body) : FcStmt;
internal sealed record FcDo(IReadOnlyList<FcStmt> Body) : FcStmt;
internal sealed record FcFor(string Variable, FcExpr Start, bool Down, FcExpr End, FcExpr? Step, IReadOnlyList<FcStmt> Body) : FcStmt;
internal sealed record FcForEach(string Variable, IReadOnlyList<FcExpr> Items, IReadOnlyList<FcStmt> Body) : FcStmt;
internal sealed record FcFunc(string Name, IReadOnlyList<string> Parameters, IReadOnlyList<FcStmt> Body) : FcStmt;
internal sealed record FcReturn(FcExpr? Value) : FcStmt;
internal sealed record FcBreak : FcStmt;
internal sealed record FcContinue : FcStmt;
internal sealed record FcExit : FcStmt;
internal sealed record FcThrow(FcExpr Value) : FcStmt;

internal sealed record FcScript(IReadOnlyList<FcStmt> Statements);
