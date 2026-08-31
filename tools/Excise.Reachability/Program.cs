using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

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

// Static architecture analysis must not depend on NuGet vulnerability-service
// availability or mutate the user's shared HTTP cache. Dependency auditing has
// its own deterministic gate; disabling it for workspace evaluation keeps
// Roslyn symbol identity stable in restricted/offline environments.
using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
{
    ["NuGetAudit"] = "false"
});
var workspaceFailures = new List<string>();
workspace.RegisterWorkspaceFailedHandler(e =>
{
    if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
    {
        workspaceFailures.Add(e.Diagnostic.Message);
    }

    if (!options.Quiet)
    {
        Console.Error.WriteLine($"workspace {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
    }
});

var solutionPath = Path.GetFullPath(options.SolutionPath);
var solution = await workspace.OpenSolutionAsync(solutionPath);
if (workspaceFailures.Count > 0)
{
    Console.Error.WriteLine(
        $"FAIL: MSBuild workspace reported {workspaceFailures.Count} failure(s); " +
        "refusing to emit an incomplete reachability graph.");
    foreach (var failure in workspaceFailures.Distinct(StringComparer.Ordinal).Take(10))
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    return 1;
}

var architecture = ArchitectureOwnershipIndex.Load(
    Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
    options.ArchitectureDesignPath,
    options.ArchitectureInventoryPath);
var analyzer = new ReachabilityAnalyzer(solution, options, architecture);
var report = await analyzer.AnalyzeAsync();

if (options.TopologyOutput is not null)
{
    TopologyWriter.Write(options.TopologyOutput, report.Topology);
}
else if (options.CheckTopologyOutput is not null
         && !TopologyWriter.Check(options.CheckTopologyOutput, report.Topology))
{
    return 1;
}

return BaselineGate.Evaluate(report.Unreachable, options);

internal sealed record Options(
    string SolutionPath,
    string BaselinePath,
    bool Update,
    bool Quiet,
    string? TopologyOutput,
    string? CheckTopologyOutput,
    string ArchitectureDesignPath,
    string ArchitectureInventoryPath,
    bool SelfTest,
    bool ShowHelp)
{
    public static Options Parse(string[] args)
    {
        var solutionPath = "excise.sln";
        var baselinePath = "tests/reachability-baseline.tsv";
        var update = false;
        var quiet = false;
        string? topologyOutput = null;
        string? checkTopologyOutput = null;
        var architectureDesignPath = "architecture/design.json";
        var architectureInventoryPath = "architecture/inventory.generated.json";
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
                case "--topology-output":
                    topologyOutput = RequireValue(args, ref i);
                    break;
                case "--check-topology-output":
                    checkTopologyOutput = RequireValue(args, ref i);
                    break;
                case "--architecture-design":
                    architectureDesignPath = RequireValue(args, ref i);
                    break;
                case "--architecture-inventory":
                    architectureInventoryPath = RequireValue(args, ref i);
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

        if (topologyOutput is not null && checkTopologyOutput is not null)
        {
            throw new ArgumentException("Use only one of --topology-output and --check-topology-output.");
        }

        return new Options(
            solutionPath, baselinePath, update, quiet,
            topologyOutput, checkTopologyOutput,
            architectureDesignPath, architectureInventoryPath,
            selfTest, help);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: scripts/check-reachability.sh [--quiet] [--update]");
        Console.WriteLine();
        Console.WriteLine("Builds a Roslyn symbol graph, seeds known production entry points,");
        Console.WriteLine("and reports unreachable private/internal symbols as a ratchet.");
        Console.WriteLine("--topology-output writes deterministic Roslyn source/coupling metrics as JSON.");
        Console.WriteLine("--check-topology-output fails when checked JSON differs from current source.");
        Console.WriteLine("--architecture-design and --architecture-inventory select normalized ownership inputs.");
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

internal sealed class ReachabilityAnalyzer(
    Solution solution,
    Options options,
    ArchitectureOwnershipIndex architecture)
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
    private readonly Dictionary<ISymbol, HashSet<TopologySeedReason>> _seeds =
        new(SymbolEqualityComparer.Default);
    private readonly XamlSeedCatalog _xaml = new();
    private readonly HashSet<INamedTypeSymbol> _scriptGlobals =
        new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _dependencyInjectionTypes =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, HashSet<string>> _testReferences =
        new(SymbolEqualityComparer.Default);
    private int _dependencyInjectionRegistrations;
    private int _externalReflectionLoads;
    private int _sourceGenerationRoots;
    private int _nativeImports;
    private int _nativeCallbacks;
    private int _resolvedXamlBindingMembers;
    private readonly HashSet<string> _unresolvedXamlBindingMembers =
        new(StringComparer.Ordinal);

    public async Task<AnalysisResult> AnalyzeAsync()
    {
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.AdditionalDocuments.Where(d => d.FilePath?.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) == true))
            {
                _xaml.Add((await document.GetTextAsync()).ToString());
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

        await CollectTestReferencesAsync();

        AddConservativeSeeds();
        TestReferenceResolver.Expand(_testReferences, _edges);

        var reachable = ComputeReachable();
        var rows = _nodes.Values
            .Where(n => IsReportable(n.Symbol))
            .Where(n => !reachable.Contains(n.Symbol))
            .Select(n => new ReachabilityRow(n.Project.Name, n.Kind, n.Display))
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

        return new AnalysisResult(rows, BuildTopology(reachable));
    }

    private TopologyReport BuildTopology(HashSet<ISymbol> reachable)
    {
        var fanIn = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        foreach (var targets in _edges.Values)
        {
            foreach (var target in targets)
            {
                var originalTarget = Original(target);
                fanIn[originalTarget] = fanIn.GetValueOrDefault(originalTarget) + 1;
            }
        }

        var allSymbols = _nodes.Values
            .Select(node => ToTopologySymbol(
                node,
                reachable.Contains(node.Symbol),
                _seeds.GetValueOrDefault(node.Symbol),
                fanIn.GetValueOrDefault(node.Symbol),
                _edges.GetValueOrDefault(node.Symbol)?.Count ?? 0,
                _testReferences.GetValueOrDefault(node.Symbol)?
                    .Order(StringComparer.Ordinal)
                    .ToArray() ?? []))
            .OrderBy(row => row.Project, StringComparer.Ordinal)
            .ThenBy(row => row.File, StringComparer.Ordinal)
            .ThenBy(row => row.StartLine)
            .ThenBy(row => row.Symbol, StringComparer.Ordinal)
            .ToArray();
        var symbols = allSymbols
            .Where(IsTopologyRelevant)
            .ToArray();

        var typeEdges = _edges
            .SelectMany(edge =>
            {
                var source = Original(edge.Key);
                var sourceComponent = _nodes.TryGetValue(source, out var sourceNode)
                    ? architecture.ResolveSymbol(source, sourceNode.Project).Component
                    : null;
                return edge.Value.Select(target => (
                    Source: ContainingType(source),
                    SourceComponent: sourceComponent,
                    Target: ContainingType(target)));
            })
            .Where(edge => edge.Source is not null
                           && edge.Target is not null
                           && edge.Source != edge.Target)
            .GroupBy(edge => (edge.Source!, edge.SourceComponent, edge.Target!))
            .Select(group => new TypeDependency(
                group.Key.Item1,
                group.Key.SourceComponent,
                group.Key.Item3,
                group.Count()))
            .OrderBy(edge => edge.SourceComponent, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ToArray();

        var projectOwnership = _nodes.Values
            .Select(node => node.Project)
            .DistinctBy(project => project.Path, StringComparer.Ordinal)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var projects = allSymbols
            .GroupBy(symbol => symbol.Project, StringComparer.Ordinal)
            .Select(group =>
            {
                var owner = projectOwnership[group.Key];
                return new ProjectTopology(
                    group.Key,
                    owner.Path,
                    owner.Classification,
                    owner.Component,
                    group.Select(item => item.File).Where(file => file is not null).Distinct(StringComparer.Ordinal).Count(),
                    group.Count(item => item.Kind == "type"),
                    group.Count(item => item.Kind == "method"),
                    group.Count(item => item.Mutable),
                    group.Sum(item => item.DeclarationLines));
            })
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();

        return new TopologyReport(
            5,
            "tools/Excise.Reachability",
            GitRevision(),
            projects,
            symbols,
            typeEdges,
            BuildMethodCycles(),
            BuildSeedSummary(),
            [
                DescribeXamlBlindSpots(),
                "Dynamic mechanism summaries describe how each observed mechanism is modeled; zero observations are explicit.",
                "Test-project evidence starts from semantic cross-compilation references and follows explicit production edges without treating every member of a reachable type as reachable.",
                "Declaration and branch counts are structural signals, not complexity verdicts.",
                "Symbol rows retain every unreachable symbol, all types, non-trivial methods, and shared mutable members; project totals cover the full graph.",
                "Git change coupling is generated separately from commit history."
            ]);
    }

    private TopologySeedSummary BuildSeedSummary()
    {
        var categories = _seeds
            .SelectMany(pair => pair.Value
                .Select(reason => (reason.Category, Symbol: pair.Key)))
            .GroupBy(item => item.Category, StringComparer.Ordinal)
            .Select(group => new TopologySeedCategory(
                group.Key,
                group.Select(item => item.Symbol)
                    .Distinct(SymbolEqualityComparer.Default)
                    .Count()))
            .OrderBy(item => item.Category, StringComparer.Ordinal)
            .ToArray();
        var mechanisms = new[]
        {
            new DynamicMechanismSummary(
                "dependency-injection",
                _dependencyInjectionTypes.Count > 0
                    ? "qualified-seed-and-static-edge"
                    : "static-edge",
                _dependencyInjectionRegistrations,
                $"Closed-generic registrations seed explicit public constructors for {_dependencyInjectionTypes.Count} implementation types; explicit factories remain ordinary Roslyn call edges."),
            new DynamicMechanismSummary(
                "native-interop",
                _nativeCallbacks > 0 ? "qualified-seed-and-static-edge" : "static-edge",
                _nativeImports + _nativeCallbacks,
                $"Outbound imports use static managed declarations; {_nativeCallbacks} managed native callbacks require qualified seeds."),
            new DynamicMechanismSummary(
                "reflection",
                "external-only",
                _externalReflectionLoads,
                "Observed Assembly.Load calls name external framework assemblies; no first-party member lookup was found."),
            new DynamicMechanismSummary(
                "scripting",
                _scriptGlobals.Count > 0 ? "qualified-seed" : "absent",
                _scriptGlobals.Count,
                "CSharpScript globals types are resolved semantically from typeof(...) and seed only their public surface."),
            new DynamicMechanismSummary(
                "source-generation",
                "static-edge",
                _sourceGenerationRoots,
                "Source-generator attributes and generated partial declarations retain statically referenced first-party types."),
            new DynamicMechanismSummary(
                "xaml",
                _xaml.UntypedBindingPaths > 0
                    ? "qualified-seed-and-conservative-fallback"
                    : "qualified-seed",
                _xaml.Observations,
                $"Structural AXAML parsing resolves types, handlers, static/custom members, and typed binding paths ({_resolvedXamlBindingMembers} matched member references); {_xaml.UntypedBindingPaths} untyped template paths use name-only fallback and {_unresolvedXamlBindingMembers.Count} typed member segments are unresolved.")
        };
        return new TopologySeedSummary(_seeds.Count, categories, mechanisms);
    }

    private string DescribeXamlBlindSpots()
    {
        var untyped = _xaml.UntypedBindingPathNames.Count == 0
            ? "none"
            : string.Join(", ", _xaml.UntypedBindingPathNames);
        var unresolved = _unresolvedXamlBindingMembers.Count == 0
            ? "none"
            : string.Join(", ", _unresolvedXamlBindingMembers.Order(StringComparer.Ordinal));
        return $"XAML untyped binding paths: {untyped}. Unresolved typed member segments: {unresolved}. Both sets are explicit rather than hidden by whole-file token matching.";
    }

    private static bool IsTopologyRelevant(SymbolTopology symbol)
    {
        return !symbol.Reachable
               || symbol.Kind == "type"
               || symbol.Kind == "method"
               && (symbol.DeclarationLines >= 8
                   || symbol.BranchPoints > 0
                   || symbol.FanIn > 2
                   || symbol.FanOut > 2
                   || symbol.DeclarationCount > 1
                   || !symbol.Reachable)
               || symbol.Kind == "field" && symbol.Mutable && symbol.FanIn > 1;
    }

    private SymbolTopology ToTopologySymbol(
        Node node,
        bool reachable,
        IReadOnlySet<TopologySeedReason>? seedReasons,
        int fanIn,
        int fanOut,
        IReadOnlyList<string> testProjects)
    {
        var declarations = node.Symbol.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OrderBy(syntax => syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(syntax => syntax.SpanStart)
            .ToArray();
        var first = declarations.FirstOrDefault();
        string? file = null;
        var startLine = 0;
        var endLine = 0;
        if (first is not null)
        {
            var span = first.GetLocation().GetLineSpan();
            file = Path.GetRelativePath(
                    Path.GetDirectoryName(solution.FilePath) ?? Environment.CurrentDirectory,
                    span.Path)
                .Replace('\\', '/');
            startLine = span.StartLinePosition.Line + 1;
            endLine = span.EndLinePosition.Line + 1;
        }

        var declarationLines = declarations.Sum(CountDeclarationLines);
        var branchPoints = declarations.Sum(CountBranchPoints);
        var ownership = architecture.ResolveSymbol(file, node.Project);
        var mutable = node.Symbol switch
        {
            IFieldSymbol field => !field.IsConst && !field.IsReadOnly,
            IPropertySymbol property => property.SetMethod is not null,
            _ => false
        };

        return new SymbolTopology(
            node.Project.Name,
            node.Kind,
            node.Display,
            ContainingType(node.Symbol),
            node.Symbol.ContainingNamespace?.ToDisplayString(),
            file,
            ownership.Component,
            ownership.Workflows,
            startLine,
            endLine,
            declarationLines,
            branchPoints,
            fanIn,
            fanOut,
            testProjects,
            reachable,
            seedReasons is not null,
            seedReasons?.OrderBy(reason => reason.Category, StringComparer.Ordinal)
                .ThenBy(reason => reason.Reason, StringComparer.Ordinal)
                .ToArray() ?? [],
            mutable,
            declarations.Length);
    }

    private static int CountDeclarationLines(SyntaxNode declaration)
    {
        var text = declaration.SyntaxTree.GetText();
        var span = declaration.GetLocation().GetLineSpan();
        var count = 0;
        for (var index = span.StartLinePosition.Line; index <= span.EndLinePosition.Line; index++)
        {
            var value = text.Lines[index].ToString().Trim();
            if (value.Length > 0 && value is not "{" and not "}" && !value.StartsWith("//", StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountBranchPoints(SyntaxNode declaration)
    {
        return declaration.DescendantNodes().Count(node => node is
            IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or
            DoStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax or
            CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax
            || node is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.LogicalAndExpression)
                || binary.IsKind(SyntaxKind.LogicalOrExpression)));
    }

    private static string? ContainingType(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        return type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "", StringComparison.Ordinal);
    }

    private IReadOnlyList<MethodCycle> BuildMethodCycles()
    {
        var methods = _nodes.Keys
            .OfType<IMethodSymbol>()
            .Select(method => (ISymbol)Original(method))
            .ToHashSet(SymbolEqualityComparer.Default);
        var index = 0;
        var indices = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var lowLinks = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var stack = new Stack<ISymbol>();
        var onStack = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var cycles = new List<MethodCycle>();

        void Visit(ISymbol symbol)
        {
            indices[symbol] = index;
            lowLinks[symbol] = index;
            index++;
            stack.Push(symbol);
            onStack.Add(symbol);

            foreach (var target in _edges.GetValueOrDefault(symbol) ?? [])
            {
                var originalTarget = Original(target);
                if (!methods.Contains(originalTarget))
                {
                    continue;
                }
                if (!indices.ContainsKey(originalTarget))
                {
                    Visit(originalTarget);
                    lowLinks[symbol] = Math.Min(lowLinks[symbol], lowLinks[originalTarget]);
                }
                else if (onStack.Contains(originalTarget))
                {
                    lowLinks[symbol] = Math.Min(lowLinks[symbol], indices[originalTarget]);
                }
            }

            if (lowLinks[symbol] != indices[symbol])
            {
                return;
            }

            var members = new List<string>();
            ISymbol member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                members.Add(Display(member));
            }
            while (!SymbolEqualityComparer.Default.Equals(member, symbol));

            if (members.Count > 1)
            {
                members.Sort(StringComparer.Ordinal);
                cycles.Add(new MethodCycle(members));
            }
        }

        foreach (var method in methods.OrderBy(Display, StringComparer.Ordinal))
        {
            if (!indices.ContainsKey(method))
            {
                Visit(method);
            }
        }

        return cycles.OrderBy(cycle => cycle.Members[0], StringComparer.Ordinal).ToArray();
    }

    private static string GitRevision()
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return "unknown";
        }
        var revision = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? revision : "unknown";
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
                foreach (var member in typeSymbol.GetMembers().Where(member =>
                             !member.IsImplicitlyDeclared
                             && member.DeclaringSyntaxReferences.Any(reference =>
                                 IsProjectSource(reference.SyntaxTree))))
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
        _nodes.TryAdd(symbol, new Node(
            architecture.ResolveProject(project.FilePath, project.Name),
            kind,
            Display(symbol),
            symbol));
    }

    private void CollectReferences(SemanticModel semanticModel, SyntaxNode root)
    {
        CollectDynamicMechanisms(semanticModel, root);

        foreach (var node in root.DescendantNodes())
        {
            var referenced = StaticReferenceResolver.Resolve(semanticModel, node);
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
                AddSeed(
                    referenced,
                    "static-root",
                    "referenced outside a declared source member");
                continue;
            }

            caller = Original(caller);
            if (_nodes.ContainsKey(caller))
            {
                AddEdge(caller, referenced);
            }
            else
            {
                AddSeed(
                    referenced,
                    "static-root",
                    "referenced from generated or external source");
            }
        }
    }

    private async Task CollectTestReferencesAsync()
    {
        var sourceSymbols = _nodes.Keys
            .GroupBy(SymbolReferenceKey.Create)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var sourceAssemblies = sourceSymbols.Keys
            .Select(key => key.Assembly)
            .ToHashSet(StringComparer.Ordinal);
        var references = await TestReferenceResolver.ResolveAsync(
            solution,
            sourceAssemblies);
        foreach (var (key, testProjects) in references)
        {
            if (!sourceSymbols.TryGetValue(key, out var symbols))
            {
                continue;
            }

            foreach (var symbol in symbols)
            {
                if (!_testReferences.TryGetValue(symbol, out var projects))
                {
                    projects = new HashSet<string>(StringComparer.Ordinal);
                    _testReferences[symbol] = projects;
                }
                projects.UnionWith(testProjects);
            }
        }
    }

    private void CollectDynamicMechanisms(SemanticModel semanticModel, SyntaxNode root)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol
                         ?? semanticModel.GetSymbolInfo(invocation).CandidateSymbols
                             .OfType<IMethodSymbol>()
                             .FirstOrDefault();
            if (method is null)
            {
                continue;
            }

            switch (DynamicMechanismClassifier.Classify(method))
            {
                case DynamicInvocationKind.DependencyInjectionRegistration:
                    _dependencyInjectionRegistrations++;
                    var activationType = DynamicMechanismClassifier
                        .ResolveDependencyInjectionActivationType(method);
                    if (activationType is not null)
                    {
                        _dependencyInjectionTypes.Add(
                            (INamedTypeSymbol)Original(activationType));
                    }
                    break;
                case DynamicInvocationKind.ExternalAssemblyLoad:
                    _externalReflectionLoads++;
                    break;
                case DynamicInvocationKind.ScriptGlobals:
                    var globalsType = DynamicMechanismClassifier.ResolveScriptGlobalsType(
                        invocation, semanticModel);
                    if (globalsType is not null)
                    {
                        _scriptGlobals.Add((INamedTypeSymbol)Original(globalsType));
                    }
                    break;
            }
        }

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            switch (DynamicMechanismClassifier.Classify(
                        semanticModel.GetTypeInfo(attribute).Type as INamedTypeSymbol))
            {
                case DynamicAttributeKind.SourceGenerationRoot:
                    _sourceGenerationRoots++;
                    break;
                case DynamicAttributeKind.NativeImport:
                    _nativeImports++;
                    break;
                case DynamicAttributeKind.NativeCallback:
                    _nativeCallbacks++;
                    if (attribute.Parent?.Parent is MemberDeclarationSyntax declaration
                        && semanticModel.GetDeclaredSymbol(declaration) is { } callback)
                    {
                        AddSeed(
                            callback,
                            "native-callback",
                            "marked UnmanagedCallersOnly for native entry");
                    }
                    break;
            }
        }
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
        AddXamlSeeds();
        AddDependencyInjectionConstructorSeeds();

        foreach (var node in _nodes.Values)
        {
            var symbol = node.Symbol;
            if (IsPublicPackageSurface(node.Project.Name, symbol))
            {
                AddSeed(symbol, "public-api", "public or protected package surface");
            }
            if (IsApplicationEntry(symbol))
            {
                AddSeed(symbol, "application-entry", "application lifetime entry point");
            }
            if (IsFrameworkEntry(symbol))
            {
                AddSeed(symbol, "framework-callback", "override or explicit interface callback");
            }
            if (IsScriptSurface(symbol))
            {
                var globals = symbol as INamedTypeSymbol ?? symbol.ContainingType!;
                AddSeed(
                    symbol,
                    "script-globals",
                    $"public surface of qualified CSharpScript globals type {Display(globals)}");
            }

            if (symbol.ContainingType is { } containingType)
            {
                var originalType = Original(containingType);
                if (_nodes.ContainsKey(originalType))
                {
                    AddEdge(symbol, originalType);
                }
            }
        }

        foreach (var edge in MemberContractResolver.Resolve(_nodes.Keys))
        {
            AddEdge(edge.From, edge.To);
        }
    }

    private void AddDependencyInjectionConstructorSeeds()
    {
        foreach (var type in _dependencyInjectionTypes)
        {
            foreach (var constructor in type.InstanceConstructors
                         .Where(constructor =>
                             constructor.DeclaredAccessibility == Accessibility.Public)
                         .Select(Original)
                         .Where(_nodes.ContainsKey))
            {
                AddSeed(
                    constructor,
                    "dependency-injection-constructor",
                    $"public constructor activated by registration of {Display(type)}");
            }
        }
    }

    private void AddXamlSeeds()
    {
        var resolution = XamlSeedResolver.Resolve(_xaml, _nodes.Keys);
        foreach (var seed in resolution.Seeds)
        {
            AddSeed(seed.Symbol, seed.Category, seed.Reason);
        }
        _resolvedXamlBindingMembers = resolution.MatchedBindingMembers;
        _unresolvedXamlBindingMembers.UnionWith(resolution.UnresolvedMembers);
    }

    private void AddSeed(ISymbol symbol, string category, string reason)
    {
        symbol = Original(symbol);
        if (!_seeds.TryGetValue(symbol, out var reasons))
        {
            reasons = [];
            _seeds[symbol] = reasons;
        }
        reasons.Add(new TopologySeedReason(category, reason));
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

    private bool IsScriptSurface(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null || !_scriptGlobals.Contains((INamedTypeSymbol)Original(type)))
        {
            return false;
        }

        return symbol is INamedTypeSymbol
               || symbol.DeclaredAccessibility is Accessibility.Public
                   or Accessibility.Protected
                   or Accessibility.ProtectedOrInternal;
    }

    private HashSet<ISymbol> ComputeReachable()
    {
        var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ISymbol>(_seeds.Keys);

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

    private sealed record Node(
        ArchitectureProjectOwnership Project,
        string Kind,
        string Display,
        ISymbol Symbol);
}

internal sealed record AnalysisResult(
    IReadOnlyList<ReachabilityRow> Unreachable,
    TopologyReport Topology);

internal sealed record TopologyReport(
    int SchemaVersion,
    string Generator,
    string SourceRevision,
    IReadOnlyList<ProjectTopology> Projects,
    IReadOnlyList<SymbolTopology> Symbols,
    IReadOnlyList<TypeDependency> TypeDependencies,
    IReadOnlyList<MethodCycle> MethodCycles,
    TopologySeedSummary Seeds,
    IReadOnlyList<string> BlindSpots);

internal sealed record ProjectTopology(
    string Name,
    string Path,
    string Classification,
    string? Component,
    int SourceFiles,
    int Types,
    int Methods,
    int MutableMembers,
    int DeclarationLines);

internal sealed record SymbolTopology(
    string Project,
    string Kind,
    string Symbol,
    string? ContainingType,
    string? Namespace,
    string? File,
    string? Component,
    IReadOnlyList<string> Workflows,
    int StartLine,
    int EndLine,
    int DeclarationLines,
    int BranchPoints,
    int FanIn,
    int FanOut,
    IReadOnlyList<string> TestProjects,
    bool Reachable,
    bool Seed,
    IReadOnlyList<TopologySeedReason> SeedReasons,
    bool Mutable,
    int DeclarationCount);

internal sealed record TypeDependency(
    string Source,
    string? SourceComponent,
    string Target,
    int References);

internal sealed record MethodCycle(IReadOnlyList<string> Members);

internal sealed record TopologySeedSummary(
    int Symbols,
    IReadOnlyList<TopologySeedCategory> Categories,
    IReadOnlyList<DynamicMechanismSummary> DynamicMechanisms);

internal sealed record TopologySeedCategory(string Category, int Symbols);

internal sealed record TopologySeedReason(string Category, string Reason);

internal sealed record DynamicMechanismSummary(
    string Mechanism,
    string Modeling,
    int Observations,
    string Reason);

internal static class TopologyWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void Write(string path, TopologyReport report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var content = Serialize(report);
        var temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, fullPath, true);
        Console.WriteLine($"==> topology written: {path} ({report.Symbols.Count} symbols)");
    }

    public static bool Check(string path, TopologyReport report)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAIL: topology output is missing: {path}");
            return false;
        }

        var actual = File.ReadAllText(path);
        using var parsed = JsonDocument.Parse(actual);
        var recordedRevision = parsed.RootElement.GetProperty("sourceRevision").GetString();
        var expected = Serialize(report with { SourceRevision = recordedRevision ?? "unknown" });
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"FAIL: topology output is stale: {path}");
            Console.Error.WriteLine($"      regenerate with --topology-output {path}");
            return false;
        }

        Console.WriteLine($"==> topology current: {path} ({report.Symbols.Count} symbols)");
        return true;
    }

    private static string Serialize(TopologyReport report) =>
        JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine;
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

        if (!ArchitectureOwnershipIndex.RunSelfTest())
        {
            return 1;
        }

        if (!DynamicMechanismClassifier.RunSelfTest())
        {
            return 1;
        }

        if (!XamlSeedCatalog.RunSelfTest())
        {
            return 1;
        }

        if (!XamlSeedResolver.RunSelfTest())
        {
            return 1;
        }

        if (!StaticReferenceResolver.RunSelfTest())
        {
            return 1;
        }

        if (!MemberContractResolver.RunSelfTest())
        {
            return 1;
        }

        if (!TestReferenceResolver.RunSelfTest())
        {
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
