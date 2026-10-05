using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Independently implemented from the published metric definitions; see #1940 and structural.md.
static class Complexity
{
    public const string Contract = "excise-structural/2; sonar-csharp-cyclomatic-syntax/2026-10-04; sonar-cognitive-1.7/isolated-callable-direct-recursion";
    public const string Definitions = "Cyclomatic: one entry plus if, loops, non-default switch labels, every switch-expression arm, conditional expression/access, &&/||/??/??=, and and/or patterns; no catch increment (Sonar C# mapping). Cognitive: Sonar whitepaper 1.7 structural increments plus active nesting for if, loops, catch, ternary and switch; fundamental increments for else/else-if, goto, Boolean/pattern operator sequences, and one for proven direct recursion. Null shorthand, try/finally, ordinary calls, return/break/continue cost zero. Local functions are isolated roots, excluded from enclosing bodies, with nesting reset to zero; this partition differs from Sonar's enclosing-method local-function attribution. Lambdas/anonymous methods remain in their owner with one nesting level but no entry cost. MaxNesting is maximum cognitive control/lambda depth. ExecutableLines is distinct starting lines of owned non-block/non-empty statements, excluding local-function declarations; expression bodies count as one line. Type rows aggregate only callable rows whose nearest type is that declaration; nested types and other partial declarations are separate. No composite score or thresholds.";
    public const string Limits = "Parsed active C#14 syntax only, no MSBuild/preprocessor configurations. Body-bearing methods, constructors, destructors, operators, conversions, local functions, accessors and expression-bodied properties/indexers; no implicit constructors, field initializers or top-level statements. Recursion uses a single-file compilation with the running core-library reference: only successfully bound direct self calls are counted, once; indirect cycles and unresolved calls are not inferred. Cognitive is the documented isolated-callable supported subset, not a full SonarAnalyzer result or a control-flow-graph calculation. Constructor initializers are outside body scope.";

    public static SyntaxNode? Body(SyntaxNode declaration) => declaration switch
    {
        MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody?.Expression,
        ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody?.Expression,
        DestructorDeclarationSyntax d => (SyntaxNode?)d.Body ?? d.ExpressionBody?.Expression,
        OperatorDeclarationSyntax o => (SyntaxNode?)o.Body ?? o.ExpressionBody?.Expression,
        ConversionOperatorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody?.Expression,
        LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody?.Expression,
        AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody?.Expression,
        PropertyDeclarationSyntax p => p.ExpressionBody?.Expression,
        IndexerDeclarationSyntax i => i.ExpressionBody?.Expression,
        _ => null
    };

    public static IEnumerable<SyntaxNode> OwnedNodes(SyntaxNode body) => body.DescendantNodesAndSelf(
        descendIntoChildren: node => node is not LocalFunctionStatementSyntax).Where(node => node is not LocalFunctionStatementSyntax);

    public static (int Cyclomatic, int Cognitive, int Nesting) Measure(SyntaxNode body, SemanticModel model, ISymbol owner)
    {
        var cyclomatic = 1 + OwnedNodes(body).Count(IsCyclomaticBranch);
        var walker = new CognitiveCounter(model, owner);
        walker.Walk(body, 0);
        return (cyclomatic, walker.Value + (walker.Recursive ? 1 : 0), walker.MaxNesting);
    }

    static bool IsCyclomaticBranch(SyntaxNode node) => node is IfStatementSyntax or ForStatementSyntax
        or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax
        or ConditionalExpressionSyntax or ConditionalAccessExpressionSyntax or CaseSwitchLabelSyntax
        or CasePatternSwitchLabelSyntax or SwitchExpressionArmSyntax
        || node.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
            or SyntaxKind.CoalesceExpression or SyntaxKind.CoalesceAssignmentExpression or SyntaxKind.AndPattern or SyntaxKind.OrPattern;

    public static int ExecutableLines(SyntaxNode body) => body is ExpressionSyntax ? 1
        : OwnedNodes(body).Where(node => node is StatementSyntax and not BlockSyntax and not EmptyStatementSyntax)
            .Select(node => node.GetLocation().GetLineSpan().StartLinePosition.Line).Distinct().Count();

    sealed class CognitiveCounter(SemanticModel model, ISymbol owner)
    {
        public int Value { get; private set; }
        public int MaxNesting { get; private set; }
        public bool Recursive { get; private set; }

        public void Walk(SyntaxNode node, int depth)
        {
            if (node is LocalFunctionStatementSyntax) return;
            if (node is IfStatementSyntax conditional) { WalkIf(conditional, depth, false); return; }
            if (node is InvocationExpressionSyntax invocation &&
                model.GetSymbolInfo(invocation).Symbol is IMethodSymbol target &&
                SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, owner.OriginalDefinition))
                Recursive = true;
            if (node is GotoStatementSyntax) Value++;

            // Parentheses preserve a like-operator sequence; negation and other syntax break it.
            // Flatten just the connected logical tree, then count runs in source/infix order.
            if (LogicalKind(node) is { } kind && !HasLogicalParent(node))
            {
                SyntaxKind? previous = null;
                foreach (var operation in LogicalOperations(node))
                {
                    if (operation != previous) Value++;
                    previous = operation;
                }
            }

            var structural = node is ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax
                or SwitchStatementSyntax or SwitchExpressionSyntax;
            var closure = node is AnonymousFunctionExpressionSyntax;
            if (structural) Value += depth + 1;
            if (structural || closure) { depth++; MaxNesting = Math.Max(MaxNesting, depth); }
            foreach (var child in node.ChildNodes()) Walk(child, depth);
        }

        void WalkIf(IfStatementSyntax node, int depth, bool elseIf)
        {
            Value += elseIf ? 1 : depth + 1;
            MaxNesting = Math.Max(MaxNesting, depth + 1);
            Walk(node.Condition, depth + 1);
            Walk(node.Statement, depth + 1);
            if (node.Else?.Statement is IfStatementSyntax next) WalkIf(next, depth, true);
            else if (node.Else is { } alternative) { Value++; Walk(alternative.Statement, depth + 1); }
        }

        static SyntaxNode WithoutParentheses(SyntaxNode node) => node switch
        {
            ParenthesizedExpressionSyntax p => WithoutParentheses(p.Expression),
            ParenthesizedPatternSyntax p => WithoutParentheses(p.Pattern),
            _ => node
        };

        static SyntaxKind? LogicalKind(SyntaxNode node) => WithoutParentheses(node).Kind() is
            SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.AndPattern or SyntaxKind.OrPattern
                ? WithoutParentheses(node).Kind() : null;

        static bool HasLogicalParent(SyntaxNode node)
        {
            var parent = node.Parent;
            // Parenthesis nodes are traversed but never start their own sequence.
            if (node is ParenthesizedExpressionSyntax or ParenthesizedPatternSyntax) return true;
            while (parent is ParenthesizedExpressionSyntax or ParenthesizedPatternSyntax) parent = parent.Parent;
            return parent is not null && LogicalKind(parent) is not null;
        }

        static IEnumerable<SyntaxKind> LogicalOperations(SyntaxNode node)
        {
            node = WithoutParentheses(node);
            if (node is BinaryExpressionSyntax expression && LogicalKind(node) is { } expressionKind)
            {
                foreach (var kind in LogicalOperations(expression.Left)) yield return kind;
                yield return expressionKind;
                foreach (var kind in LogicalOperations(expression.Right)) yield return kind;
            }
            else if (node is BinaryPatternSyntax pattern && LogicalKind(node) is { } patternKind)
            {
                foreach (var kind in LogicalOperations(pattern.Left)) yield return kind;
                yield return patternKind;
                foreach (var kind in LogicalOperations(pattern.Right)) yield return kind;
            }
        }
    }
}
