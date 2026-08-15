using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

var options = Options.Parse(args);
if (options.ShowHelp)
{
    Options.PrintHelp();
    return 0;
}

if (options.SelfTest)
{
    return SelfTest.Run();
}

if (!MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

using var workspace = MSBuildWorkspace.Create();
workspace.RegisterWorkspaceFailedHandler(e =>
{
    if (!options.Quiet)
    {
        Console.Error.WriteLine($"workspace {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
    }
});

var solutionPath = Path.GetFullPath(options.SolutionPath);
var solution = await workspace.OpenSolutionAsync(solutionPath);
var analyzer = new ReachabilityAnalyzer(solution, options);
var report = await analyzer.AnalyzeAsync();

return BaselineGate.Evaluate(report, options);

internal sealed record Options(
    string SolutionPath,
    string BaselinePath,
    bool Update,
    bool Quiet,
    bool SelfTest,
    bool ShowHelp)
{
    public static Options Parse(string[] args)
    {
        var solutionPath = "excise.sln";
        var baselinePath = "tests/reachability-baseline.tsv";
        var update = false;
        var quiet = false;
        var selfTest = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--solution":
                    solutionPath = RequireValue(args, ref i);
                    break;
                case "--baseline":
                    baselinePath = RequireValue(args, ref i);
                    break;
                case "--update":
                    update = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return new Options(solutionPath, baselinePath, update, quiet, selfTest, help);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: scripts/check-reachability.sh [--quiet] [--update]");
        Console.WriteLine();
        Console.WriteLine("Builds a Roslyn symbol graph, seeds known production entry points,");
        Console.WriteLine("and reports unreachable private/internal symbols as a ratchet.");
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[index]}.");
        }

        index++;
        return args[index];
    }
}

internal sealed class ReachabilityAnalyzer(Solution solution, Options options)
{
    private static readonly SymbolDisplayFormat DisplayFormat =
        new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            memberOptions:
                SymbolDisplayMemberOptions.IncludeContainingType |
                SymbolDisplayMemberOptions.IncludeParameters |
                SymbolDisplayMemberOptions.IncludeType,
            parameterOptions:
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeName,
            miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly Dictionary<ISymbol, Node> _nodes = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, HashSet<ISymbol>> _edges = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _seeds = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> _xamlNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stringLiterals = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ReachabilityRow>> AnalyzeAsync()
    {
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.AdditionalDocuments.Where(d => d.FilePath?.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true))
            {
                CollectXamlNames(await document.GetTextAsync());
            }
        }

        foreach (var project in solution.Projects.Where(ShouldAnalyzeProject))
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            foreach (var tree in compilation.SyntaxTrees.Where(IsProjectSource))
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync();
                CollectDeclarations(project, semanticModel, root);
            }
        }

        foreach (var project in solution.Projects.Where(ShouldAnalyzeProject))
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            foreach (var tree in compilation.SyntaxTrees.Where(IsProjectSource))
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync();
                CollectReferences(semanticModel, root);
            }
        }

        AddConservativeSeeds();

        var reachable = ComputeReachable();
        var rows = _nodes.Values
            .Where(n => IsReportable(n.Symbol))
            .Where(n => !reachable.Contains(n.Symbol))
            .Select(n => new ReachabilityRow(n.ProjectName, n.Kind, n.Display))
            .Distinct()
            .OrderBy(r => r.Project, StringComparer.Ordinal)
            .ThenBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .ToArray();

        if (!options.Quiet)
        {
            Console.WriteLine($"==> reachability: {_nodes.Count} symbols, {_edges.Sum(e => e.Value.Count)} edges, {_seeds.Count} seeds");
            Console.WriteLine($"==> unreachable private/internal symbols: {rows.Length}");
            foreach (var row in rows.Take(40))
            {
                Console.WriteLine($"    [{row.Project}] {row.Kind} {row.Symbol}");
            }
            if (rows.Length > 40)
            {
                Console.WriteLine($"    ... {rows.Length - 40} more");
            }
        }

        return rows;
    }

    private static bool ShouldAnalyzeProject(Project project)
    {
        var name = project.Name;
        return !name.EndsWith(".Tests", StringComparison.Ordinal)
               && !name.Equals("Excise.Benchmarks", StringComparison.Ordinal);
    }

    private static bool IsProjectSource(SyntaxTree tree)
    {
        var path = tree.FilePath;
        return !string.IsNullOrWhiteSpace(path)
               && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private void CollectXamlNames(SourceText text)
    {
        foreach (Match match in Regex.Matches(text.ToString(), @"[A-Za-z_][A-Za-z0-9_]{2,}"))
        {
            _xamlNames.Add(match.Value);
        }
    }

    private void CollectDeclarations(Project project, SemanticModel semanticModel, SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var symbol = semanticModel.GetDeclaredSymbol(declaration);
            if (symbol is null || symbol.IsImplicitlyDeclared)
            {
                continue;
            }

            if (symbol is INamedTypeSymbol typeSymbol)
            {
                RegisterSymbol(project, typeSymbol, "type");
                foreach (var member in typeSymbol.GetMembers().Where(m => !m.IsImplicitlyDeclared))
                {
                    RegisterSymbol(project, member, KindOf(member));
                }
            }
            else
            {
                RegisterSymbol(project, symbol, KindOf(symbol));
            }
        }
    }

    private void RegisterSymbol(Project project, ISymbol symbol, string kind)
    {
        symbol = Original(symbol);
        _nodes.TryAdd(symbol, new Node(project.Name, kind, Display(symbol), symbol));
    }

    private void CollectReferences(SemanticModel semanticModel, SyntaxNode root)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.ValueText.Length >= 3)
            {
                _stringLiterals.Add(literal.Token.ValueText);
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            var referenced = SymbolFromNode(semanticModel, node);
            if (referenced is null)
            {
                continue;
            }

            referenced = Original(referenced);
            if (!_nodes.ContainsKey(referenced))
            {
                continue;
            }

            var caller = FindContainingDeclaredSymbol(semanticModel, node);
            if (caller is null)
            {
                _seeds.Add(referenced);
                continue;
            }

            caller = Original(caller);
            if (_nodes.ContainsKey(caller))
            {
                AddEdge(caller, referenced);
            }
            else
            {
                _seeds.Add(referenced);
            }
        }
    }

    private static ISymbol? SymbolFromNode(SemanticModel semanticModel, SyntaxNode node)
    {
        return node switch
        {
            IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax or InvocationExpressionSyntax
                => semanticModel.GetSymbolInfo(node).Symbol ?? semanticModel.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault(),
            AttributeSyntax attribute
                => semanticModel.GetSymbolInfo(attribute).Symbol ?? semanticModel.GetTypeInfo(attribute).Type,
            _ => null
        };
    }

    private static ISymbol? FindContainingDeclaredSymbol(SemanticModel semanticModel, SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case BaseMethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case EventDeclarationSyntax:
                case FieldDeclarationSyntax:
                case TypeDeclarationSyntax:
                    return semanticModel.GetDeclaredSymbol(current);
            }
        }

        return null;
    }

    private void AddConservativeSeeds()
    {
        foreach (var node in _nodes.Values)
        {
            var symbol = node.Symbol;
            if (IsPublicPackageSurface(node.ProjectName, symbol)
                || IsApplicationEntry(symbol)
                || IsFrameworkEntry(symbol)
                || IsXamlBound(symbol)
                || IsStringReferenced(symbol))
            {
                _seeds.Add(symbol);
            }

            if (symbol.ContainingType is { } containingType)
            {
                var originalType = Original(containingType);
                if (_nodes.ContainsKey(originalType))
                {
                    AddEdge(originalType, symbol);
                    AddEdge(symbol, originalType);
                }
            }
        }
    }

    private static bool IsPublicPackageSurface(string projectName, ISymbol symbol)
    {
        if (projectName is "Excise.App" or "Excise.Cli" or "Excise.Demo" or "Excise.Avalonia.Sample")
        {
            return false;
        }

        return symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
    }

    private static bool IsApplicationEntry(ISymbol symbol)
    {
        return symbol is IMethodSymbol { Name: "Main" }
               || symbol.Name is "BuildAvaloniaApp" or "OnFrameworkInitializationCompleted";
    }

    private static bool IsFrameworkEntry(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            return method.IsOverride || method.ExplicitInterfaceImplementations.Length > 0;
        }

        if (symbol is IPropertySymbol property)
        {
            return property.IsOverride || property.ExplicitInterfaceImplementations.Length > 0;
        }

        if (symbol is IEventSymbol @event)
        {
            return @event.IsOverride || @event.ExplicitInterfaceImplementations.Length > 0;
        }

        return false;
    }

    private bool IsXamlBound(ISymbol symbol)
    {
        if (_xamlNames.Contains(symbol.Name))
        {
            return true;
        }

        if (symbol.Name.StartsWith("Set", StringComparison.Ordinal) && _xamlNames.Contains(symbol.Name[3..]))
        {
            return true;
        }

        if (symbol.Name.StartsWith("Get", StringComparison.Ordinal) && _xamlNames.Contains(symbol.Name[3..]))
        {
            return true;
        }

        return false;
    }

    private bool IsStringReferenced(ISymbol symbol)
    {
        return _stringLiterals.Contains(symbol.Name);
    }

    private HashSet<ISymbol> ComputeReachable()
    {
        var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ISymbol>(_seeds);

        while (pending.TryPop(out var symbol))
        {
            symbol = Original(symbol);
            if (!reachable.Add(symbol))
            {
                continue;
            }

            if (_edges.TryGetValue(symbol, out var callees))
            {
                foreach (var callee in callees)
                {
                    pending.Push(callee);
                }
            }
        }

        return reachable;
    }

    private static bool IsReportable(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
        {
            return false;
        }

        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate })
        {
            return false;
        }

        return symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal;
    }

    private static string KindOf(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol => "type",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }

    private static void AddEdge(Dictionary<ISymbol, HashSet<ISymbol>> edges, ISymbol from, ISymbol to)
    {
        from = Original(from);
        to = Original(to);
        if (!edges.TryGetValue(from, out var set))
        {
            set = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            edges[from] = set;
        }

        set.Add(to);
    }

    private void AddEdge(ISymbol from, ISymbol to) => AddEdge(_edges, from, to);

    private static ISymbol Original(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.ReducedFrom?.OriginalDefinition ?? method.OriginalDefinition,
            IPropertySymbol property => property.OriginalDefinition,
            IEventSymbol @event => @event.OriginalDefinition,
            INamedTypeSymbol type => type.OriginalDefinition,
            _ => symbol.OriginalDefinition
        };
    }

    private static string Display(ISymbol symbol)
    {
        return Original(symbol).ToDisplayString(DisplayFormat);
    }

    private sealed record Node(string ProjectName, string Kind, string Display, ISymbol Symbol);
}

internal sealed record ReachabilityRow(string Project, string Kind, string Symbol)
{
    public string Key => $"{Project}\t{Kind}\t{Symbol}";

    public string ToTsv(string note) => $"{Key}\t{note}";
}

internal static class BaselineGate
{
    private const string Header = """
                                  # Unreachable private/internal symbols reported by tools/Excise.Reachability.
                                  #
                                  # Regenerate: scripts/check-reachability.sh --update   (then review the diff)
                                  #
                                  # Format: project <TAB> kind <TAB> symbol <TAB> triage-note
                                  # This is a ratchet over a whole-solution reachability closure. New rows mean
                                  # code was added without a discovered production entry path, or a seed was
                                  # missed. Review before accepting. New rows written by --update are
                                  # UNTRIAGED and fail the normal gate until reviewed.
                                  """;

    public static int Evaluate(IReadOnlyList<ReachabilityRow> report, Options options)
    {
        if (options.Update)
        {
            WriteBaseline(options.BaselinePath, report, ReadBaseline(options.BaselinePath) ?? []);
            Console.WriteLine($"==> baseline rewritten: {options.BaselinePath} ({report.Count} entries)");
            Console.WriteLine("    REVIEW THE DIFF. Reachability seed mistakes create false positives.");
            return 0;
        }

        var baseline = ReadBaseline(options.BaselinePath);
        if (baseline is null)
        {
            Console.Error.WriteLine($"FAIL: no baseline at {options.BaselinePath}. Run with --update and review.");
            return 1;
        }

        var current = report.Select(r => r.Key).ToImmutableSortedSet(StringComparer.Ordinal);
        var known = baseline.Keys.ToImmutableSortedSet(StringComparer.Ordinal);
        var added = current.Except(known).ToArray();
        var gone = known.Except(current).ToArray();
        var missingTriage = baseline
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value) || kv.Value == "UNTRIAGED")
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (gone.Length > 0)
        {
            Console.WriteLine($"==> {gone.Length} baselined unreachable symbol(s) disappeared.");
            Console.WriteLine("    Run --update to drop them after reviewing the source change.");
            return 1;
        }

        if (added.Length > 0)
        {
            Console.Error.WriteLine($"FAIL: {added.Length} new unreachable private/internal symbol(s):");
            foreach (var row in added.Take(40))
            {
                Console.Error.WriteLine($"      {row}");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("    Wire/delete the code, add a conservative seed if this is a false positive,");
            Console.Error.WriteLine("    or run --update only after review.");
            return 1;
        }

        if (missingTriage.Length > 0)
        {
            Console.Error.WriteLine($"FAIL: {missingTriage.Length} baselined unreachable symbol(s) lack triage notes:");
            foreach (var row in missingTriage.Take(40))
            {
                Console.Error.WriteLine($"      {row}");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("    Add a fourth TSV column explaining why the row is accepted,");
            Console.Error.WriteLine("    or link the issue that owns wiring/deleting it.");
            return 1;
        }

        Console.WriteLine($"==> reachability OK ({known.Count} baselined)");
        return 0;
    }

    private static void WriteBaseline(string path, IReadOnlyList<ReachabilityRow> rows, IReadOnlyDictionary<string, string> previous)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(path, Header.Split('\n').Concat(rows.Select(r => r.ToTsv(previous.GetValueOrDefault(r.Key, "UNTRIAGED")))));
    }

    private static Dictionary<string, string>? ReadBaseline(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#')))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var key = string.Join('\t', parts.Take(3));
            rows[key] = parts.Length >= 4 ? parts[3].Trim() : "";
        }

        return rows;
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        const string source = """
                              namespace Fixture;

                              internal static class Program
                              {
                                  public static void Main()
                                  {
                                      LiveRoot();
                                  }

                                  private static void LiveRoot()
                                  {
                                      LiveLeaf();
                                  }

                                  private static void LiveLeaf()
                                  {
                                  }

                                  private static void DeadRoot()
                                  {
                                      DeadLeaf();
                                  }

                                  private static void DeadLeaf()
                                  {
                                  }
                              }
                              """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Fixture",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var symbols = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var edges = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var seeds = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(method);
            if (symbol is null)
            {
                continue;
            }

            symbols[symbol.OriginalDefinition] = symbol.Name;
            if (symbol.Name == "Main")
            {
                seeds.Add(symbol.OriginalDefinition);
            }

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var callee = model.GetSymbolInfo(invocation).Symbol?.OriginalDefinition;
                if (callee is not null)
                {
                    AddEdge(edges, symbol.OriginalDefinition, callee);
                }
            }
        }

        var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var stack = new Stack<ISymbol>(seeds);
        while (stack.TryPop(out var symbol))
        {
            if (!reachable.Add(symbol))
            {
                continue;
            }

            if (edges.TryGetValue(symbol, out var callees))
            {
                foreach (var callee in callees)
                {
                    stack.Push(callee);
                }
            }
        }

        var unreachable = symbols
            .Where(kv => !reachable.Contains(kv.Key))
            .Select(kv => kv.Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (!unreachable.SequenceEqual(["DeadLeaf", "DeadRoot"]))
        {
            Console.Error.WriteLine("FAIL: reachability self-test did not report the dead closure.");
            Console.Error.WriteLine($"      got: {string.Join(", ", unreachable)}");
            return 1;
        }

        Console.WriteLine("==> reachability self-test OK");
        return 0;
    }

    private static void AddEdge(Dictionary<ISymbol, HashSet<ISymbol>> edges, ISymbol from, ISymbol to)
    {
        if (!edges.TryGetValue(from, out var set))
        {
            set = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            edges[from] = set;
        }

        set.Add(to);
    }
}
