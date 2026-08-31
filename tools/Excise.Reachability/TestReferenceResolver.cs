using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class TestReferenceResolver
{
    public static async Task<IReadOnlyDictionary<SymbolReferenceKey, IReadOnlyList<string>>>
        ResolveAsync(
            Solution solution,
            IReadOnlySet<string> sourceAssemblies)
    {
        var projectsBySymbol = new Dictionary<SymbolReferenceKey, HashSet<string>>();
        foreach (var project in solution.Projects.Where(IsTestOrBenchmarkProject))
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            CollectCompilation(
                compilation,
                project.Name,
                sourceAssemblies,
                projectsBySymbol);
        }

        return projectsBySymbol.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public static void Expand(
        IDictionary<ISymbol, HashSet<string>> projectsBySymbol,
        IReadOnlyDictionary<ISymbol, HashSet<ISymbol>> edges)
    {
        var testProjects = projectsBySymbol.Values
            .SelectMany(projects => projects)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var project in testProjects)
        {
            var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var pending = new Stack<ISymbol>(projectsBySymbol
                .Where(pair => pair.Value.Contains(project))
                .Select(pair => pair.Key));
            while (pending.TryPop(out var symbol))
            {
                symbol = Original(symbol);
                if (!reachable.Add(symbol))
                {
                    continue;
                }

                if (!projectsBySymbol.TryGetValue(symbol, out var projects))
                {
                    projects = new HashSet<string>(StringComparer.Ordinal);
                    projectsBySymbol[symbol] = projects;
                }
                projects.Add(project);

                if (edges.TryGetValue(symbol, out var references))
                {
                    foreach (var reference in references)
                    {
                        pending.Push(reference);
                    }
                }
            }
        }
    }

    private static void CollectCompilation(
        Compilation compilation,
        string projectName,
        IReadOnlySet<string> sourceAssemblies,
        IDictionary<SymbolReferenceKey, HashSet<string>> projectsBySymbol)
    {
        foreach (var tree in compilation.SyntaxTrees.Where(IsProjectSource))
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var symbol = StaticReferenceResolver.Resolve(semanticModel, node);
                if (symbol?.ContainingAssembly?.Name is not { } assemblyName
                    || !sourceAssemblies.Contains(assemblyName))
                {
                    continue;
                }

                var key = SymbolReferenceKey.Create(symbol);
                if (!projectsBySymbol.TryGetValue(key, out var projects))
                {
                    projects = new HashSet<string>(StringComparer.Ordinal);
                    projectsBySymbol[key] = projects;
                }
                projects.Add(projectName);
            }
        }
    }

    private static bool IsTestOrBenchmarkProject(Project project) =>
        project.Name.EndsWith(".Tests", StringComparison.Ordinal)
        || project.Name.Equals("Excise.Benchmarks", StringComparison.Ordinal);

    private static bool IsProjectSource(SyntaxTree tree)
    {
        var path = tree.FilePath;
        return !string.IsNullOrWhiteSpace(path)
               && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               && !path.Contains(
                   $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase)
               && !path.Contains(
                   $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RunSelfTest()
    {
        const string librarySource = """
                                     namespace Fixture;

                                     public static class Target
                                     {
                                         public static void Run() => Helper();
                                         internal static void Helper() { }
                                         internal static void Unrelated() { }
                                     }
                                     """;
        const string testSource = """
                                  namespace Fixture.Tests;

                                  internal static class TargetTests
                                  {
                                      public static void Exercise() => Fixture.Target.Run();
                                  }
                                  """;
        MetadataReference[] references =
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        };
        var library = CSharpCompilation.Create(
            "Fixture.Library",
            [CSharpSyntaxTree.ParseText(librarySource, path: "Target.cs")],
            references);
        var test = CSharpCompilation.Create(
            "Fixture.Library.Tests",
            [CSharpSyntaxTree.ParseText(testSource, path: "TargetTests.cs")],
            references.Append(library.ToMetadataReference()));
        var projectsBySymbol = new Dictionary<SymbolReferenceKey, HashSet<string>>();
        CollectCompilation(
            test,
            "Fixture.Library.Tests",
            new HashSet<string>(["Fixture.Library"], StringComparer.Ordinal),
            projectsBySymbol);
        var target = library.GetTypeByMetadataName("Fixture.Target")!
            .GetMembers("Run")
            .Single();
        if (!projectsBySymbol.TryGetValue(SymbolReferenceKey.Create(target), out var projects)
            || !projects.SetEquals(["Fixture.Library.Tests"]))
        {
            Console.Error.WriteLine(
                "FAIL: test reference self-test missed a cross-compilation source symbol.");
            return false;
        }

        var helper = library.GetTypeByMetadataName("Fixture.Target")!
            .GetMembers("Helper")
            .Single();
        var unrelated = library.GetTypeByMetadataName("Fixture.Target")!
            .GetMembers("Unrelated")
            .Single();
        var expanded = new Dictionary<ISymbol, HashSet<string>>(
            SymbolEqualityComparer.Default)
        {
            [target] = new HashSet<string>(["Fixture.Library.Tests"], StringComparer.Ordinal)
        };
        var edges = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default)
        {
            [target] = new HashSet<ISymbol>([helper], SymbolEqualityComparer.Default)
        };
        Expand(expanded, edges);
        if (!expanded.TryGetValue(helper, out var helperProjects)
            || !helperProjects.SetEquals(["Fixture.Library.Tests"])
            || expanded.ContainsKey(unrelated))
        {
            Console.Error.WriteLine(
                "FAIL: test reference self-test did not preserve explicit-edge closure.");
            return false;
        }

        return true;
    }

    private static ISymbol Original(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReducedFrom?.OriginalDefinition
                                ?? method.OriginalDefinition,
        IPropertySymbol property => property.OriginalDefinition,
        IEventSymbol @event => @event.OriginalDefinition,
        INamedTypeSymbol type => type.OriginalDefinition,
        _ => symbol.OriginalDefinition
    };
}

internal sealed record SymbolReferenceKey(string Assembly, string Symbol)
{
    public static SymbolReferenceKey Create(ISymbol symbol)
    {
        symbol = symbol switch
        {
            IMethodSymbol method => method.ReducedFrom?.OriginalDefinition
                                    ?? method.OriginalDefinition,
            IPropertySymbol property => property.OriginalDefinition,
            IEventSymbol @event => @event.OriginalDefinition,
            INamedTypeSymbol type => type.OriginalDefinition,
            _ => symbol.OriginalDefinition
        };
        return new SymbolReferenceKey(
            symbol.ContainingAssembly?.Name ?? "",
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    }
}
