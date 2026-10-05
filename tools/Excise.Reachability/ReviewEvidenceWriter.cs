using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Supplemental review output from the authoritative graph; never deletion proof. See #1942.
internal static class ReviewEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly SymbolDisplayFormat ApiFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeAccessibility |
            SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeExplicitInterface |
            SymbolDisplayMemberOptions.IncludeConstantValue | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue |
            SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static string Key(ReviewNode node) => node.Project + "::" +
        (node.Symbol.GetDocumentationCommentId() ?? node.Symbol.ToDisplayString(ApiFormat));

    internal static bool IsExported(ISymbol symbol)
    {
        for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingType)
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        return true;
    }

    private static string Signature(ISymbol symbol)
    {
        var parts = new List<string> { symbol.ToDisplayString(ApiFormat) };
        parts.AddRange(symbol.GetAttributes().Select(attribute => "attribute:" + attribute));
        if (symbol is INamedTypeSymbol type)
        {
            parts.Add("typeKind:" + type.TypeKind);
            parts.Add("base:" + type.BaseType?.ToDisplayString(ApiFormat));
            parts.AddRange(type.Interfaces.Select(contract => "interface:" + contract.ToDisplayString(ApiFormat)));
        }
        if (symbol is IMethodSymbol method)
        {
            parts.AddRange(method.GetReturnTypeAttributes().Select(attribute => "returnAttribute:" + attribute));
            parts.AddRange(method.Parameters.SelectMany(parameter => parameter.GetAttributes().Select(attribute => "parameter:" + parameter.Ordinal + ":" + attribute)));
        }
        if (symbol is IPropertySymbol property)
        {
            parts.Add("get:" + property.GetMethod?.DeclaredAccessibility);
            parts.Add("set:" + property.SetMethod?.DeclaredAccessibility + ";init:" + property.SetMethod?.IsInitOnly);
        }
        // Displayed syntax is culture invariant; unordered attributes/interfaces are sorted.
        return string.Join("\n", parts.Take(1).Concat(parts.Skip(1).Order(StringComparer.Ordinal)));
    }

    internal static ReviewEvidence Build(Solution solution, TopologyReport topology,
        IEnumerable<ReviewNode> sourceNodes, IEnumerable<MemberContractEdge> sourceEdges)
    {
        var root = Path.GetDirectoryName(solution.FilePath) ?? Environment.CurrentDirectory;
        var nodes = sourceNodes.ToArray();
        var duplicateKey = nodes.GroupBy(Key, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null)
            throw new InvalidOperationException($"Ambiguous review symbol identity {duplicateKey.Key}; refusing a lossy graph.");
        var bySymbol = new Dictionary<ISymbol, ReviewNode>(SymbolEqualityComparer.Default);
        foreach (var node in nodes) bySymbol.Add(node.Symbol, node);
        var edges = sourceEdges.Where(edge => bySymbol.ContainsKey(edge.From) && bySymbol.ContainsKey(edge.To))
            .Select(edge => new ReviewEdge(Key(bySymbol[edge.From]), Key(bySymbol[edge.To]),
                MemberContractResolver.IsDispatchOnlyArchitectureEdge(edge) ? "runtime-interface-dispatch" : "static-reference"))
            .Distinct().OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind, StringComparer.Ordinal).ToArray();
        var incoming = edges.Where(edge => edge.Kind == "static-reference" && edge.Source != edge.Target)
            .GroupBy(edge => edge.Target).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rows = nodes.Select(node =>
        {
            var key = Key(node);
            var spans = node.Symbol.DeclaringSyntaxReferences.Select(reference =>
            {
                var span = reference.GetSyntax().GetLocation().GetLineSpan();
                return new ReviewSpan(Path.GetRelativePath(root, span.Path).Replace('\\', '/'),
                    span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
            }).Distinct().OrderBy(span => span.Path, StringComparer.Ordinal).ThenBy(span => span.Line).ThenBy(span => span.EndLine).ToArray();
            var exported = node.Classification == "shipping" && IsExported(node.Symbol);
            var declarationHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
                node.Symbol.DeclaringSyntaxReferences.OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.Ordinal)
                    .ThenBy(reference => reference.Span.Start).Select(reference => reference.GetSyntax().ToFullString()
                        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))))));
            return new ReviewSymbol(key, node.Project, node.Classification, node.Symbol.Kind.ToString(), node.Component,
                spans, declarationHash, Signature(node.Symbol), exported, node.Reachable,
                incoming.GetValueOrDefault(key), node.TestProjects.Distinct().Order(StringComparer.Ordinal).ToArray(),
                node.Seeds.Distinct().OrderBy(seed => seed.Category, StringComparer.Ordinal).ThenBy(seed => seed.Reason, StringComparer.Ordinal).ToArray(),
                exported && incoming.GetValueOrDefault(key) == 0,
                !node.Reachable);
        }).OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
        var projectEdges = solution.Projects.SelectMany(project => project.ProjectReferences.Select(reference =>
        {
            var target = solution.GetProject(reference.ProjectId)!;
            return new ReviewEdge(Path.GetRelativePath(root, project.FilePath!).Replace('\\', '/'),
                Path.GetRelativePath(root, target.FilePath!).Replace('\\', '/'), "project-reference");
        })).Distinct().OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal).ToArray();
        var referenceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var evaluation = solution.Projects.Select(project => new ReviewEvaluation(
            Path.GetRelativePath(root, project.FilePath!).Replace('\\', '/'),
            (project.ParseOptions as CSharpParseOptions)?.LanguageVersion.ToString(),
            (project.ParseOptions as CSharpParseOptions)?.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray() ?? [],
            project.CompilationOptions?.OutputKind.ToString(),
            project.CompilationOptions?.Platform.ToString(),
            project.CompilationOptions?.NullableContextOptions.ToString(),
            project.MetadataReferences.OfType<PortableExecutableReference>().Where(reference => reference.FilePath is not null)
                .Select(reference =>
                {
                    var path = reference.FilePath!;
                    if (!referenceHashes.TryGetValue(path, out var hash))
                    {
                        hash = File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
                        referenceHashes.Add(path, hash);
                    }
                    return new ReviewReference(Path.GetFileName(path), hash);
                }).Distinct().OrderBy(reference => reference.Name, StringComparer.Ordinal)
                .ThenBy(reference => reference.Sha256, StringComparer.Ordinal).ToArray()))
            .OrderBy(project => project.Project, StringComparer.Ordinal).ToArray();
        return new(1, "Excise.Reachability/review-2", typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ReviewEvidenceWriter).Assembly.Location))),
            "MSBuild evaluated solution; complete explicit source declarations in authoritative reachability scope. Compiler-synthesized and generated declarations are excluded. API signatures are bound source surface, not a binary compatibility verdict.",
            "Unwired means no incoming nonself static production reference; testProjects distinguishes no observed static/test references from tests-only. Public API seeds, external consumers, reflection, XAML, DI, scripting, native calls and runtime dispatch can still require a symbol. Unreachable/unwired are candidates only: runtime and registry review is mandatory before deletion or refactoring.",
            topology, rows, edges, projectEdges, evaluation);
    }

    internal static void Write(string path, ReviewEvidence evidence) =>
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions) + "\n");

    internal static bool RunSelfTest()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "excise-review-fixture");
        var tree = CSharpSyntaxTree.ParseText("""
            namespace Fixture;
            public class Surface { public int Unwired(int value = 1) => value; public int ProtectedSetter { get; private set; } }
            public interface ICallback { void Invoke(); }
            internal class Hidden : ICallback { public void NotExported() {} public void Invoke() {} }
            """, path: Path.Combine(fixtureRoot, "Fixture.cs"));
        var compilation = CSharpCompilation.Create("Fixture", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select(method => model.GetDeclaredSymbol(method)!).ToArray();
        var surface = methods.Single(method => method.Name == "Unwired");
        var hidden = methods.Single(method => method.Name == "NotExported");
        var contract = methods.Single(method => method.Name == "Invoke" && method.ContainingType.Name == "ICallback");
        var implementation = methods.Single(method => method.Name == "Invoke" && method.ContainingType.Name == "Hidden");
        var changedTree = CSharpSyntaxTree.ParseText(tree.ToString().Replace("value = 1", "value = 2", StringComparison.Ordinal), path: tree.FilePath);
        var changedCompilation = compilation.ReplaceSyntaxTree(tree, changedTree);
        var changed = changedCompilation.GetSemanticModel(changedTree).GetDeclaredSymbol(changedTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First())!;
        if (!IsExported(surface) || IsExported(hidden) || Signature(surface) == Signature(changed)
            || surface.GetDocumentationCommentId() != changed.GetDocumentationCommentId())
        {
            Console.Error.WriteLine("FAIL: bound API identity, effective visibility or signature change self-test.");
            return false;
        }
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), Path.Combine(fixtureRoot, "Fixture.sln")));
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Fixture", "Fixture", LanguageNames.CSharp)
            .WithProjectFilePath(projectId, Path.Combine(fixtureRoot, "Fixture.csproj"));
        var topology = new TopologyReport(5, "fixture", "fixture", [], [], [], [], new(0, [], []), []);
        var publicNode = new ReviewNode(surface, "Fixture.csproj", "shipping", "fixture", true,
            [new("public-api", "exported library entry")], ["Fixture.Tests"]);
        var hiddenNode = new ReviewNode(hidden, "Fixture.csproj", "shipping", "fixture", false, [], []);
        var evidence = Build(solution, topology,
            [publicNode, hiddenNode], []);
        var apiRow = evidence.Symbols.Single(row => row.PublicApi);
        var changedEvidence = Build(solution, topology, [publicNode with { Symbol = changed }], []);
        if (!apiRow.UnwiredApiCandidate || apiRow.DeadCodeCandidate || apiRow.StaticFanIn != 0
            || apiRow.Seeds.Single().Category != "public-api" || apiRow.Spans.Single().Path != "Fixture.cs"
            || apiRow.DeclarationHash == changedEvidence.Symbols.Single().DeclarationHash)
        {
            Console.Error.WriteLine("FAIL: unwired public API seed, source hash or relative-span review self-test.");
            return false;
        }
        var contractNode = new ReviewNode(contract, "Fixture.csproj", "shipping", "fixture", true,
            [new("public-api", "exported callback contract")], []);
        var implementationNode = new ReviewNode(implementation, "Fixture.csproj", "shipping", "fixture", true, [], []);
        var graphNodes = new[] { publicNode, hiddenNode, contractNode, implementationNode };
        var graphEdges = new[] { new MemberContractEdge(hidden, surface), new MemberContractEdge(contract, implementation) };
        var graphEvidence = Build(solution, topology, graphNodes, graphEdges);
        var calledRow = graphEvidence.Symbols.Single(row => row.Key == Key(publicNode));
        var dispatchedRow = graphEvidence.Symbols.Single(row => row.Key == Key(implementationNode));
        if (calledRow.StaticFanIn != 1 || calledRow.UnwiredApiCandidate
            || dispatchedRow.StaticFanIn != 0
            || apiRow.Classification != "shipping" || apiRow.TestProjects.Single() != "Fixture.Tests"
            || graphEvidence.SymbolEdges.Single(edge => edge.Target == Key(implementationNode)).Kind != "runtime-interface-dispatch"
            || JsonSerializer.Serialize(graphEvidence, JsonOptions) != JsonSerializer.Serialize(Build(solution, topology, graphNodes.Reverse(), graphEdges.Reverse()), JsonOptions))
        {
            Console.Error.WriteLine("FAIL: directed caller, runtime dispatch, classification, tests-only or deterministic graph self-test.");
            return false;
        }
        try
        {
            Build(solution, topology, [publicNode, publicNode], []);
            Console.Error.WriteLine("FAIL: duplicate symbol identity accepted.");
            return false;
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Ambiguous review symbol identity", StringComparison.Ordinal))
        {
        }
        Console.WriteLine("==> review API self-test OK (bound identity, visibility/defaults/source hashes, seeded tests-only unwired API, directed callers, runtime dispatch and deterministic output)");
        return true;
    }
}

internal sealed record ReviewNode(ISymbol Symbol, string Project, string Classification, string? Component,
    bool Reachable, TopologySeedReason[] Seeds, string[] TestProjects);
internal sealed record ReviewSpan(string Path, int Line, int EndLine);
internal sealed record ReviewEdge(string Source, string Target, string Kind);
internal sealed record ReviewSymbol(string Key, string Project, string Classification, string Kind, string? Component,
    ReviewSpan[] Spans, string DeclarationHash, string Signature, bool PublicApi, bool Reachable, int StaticFanIn,
    string[] TestProjects, TopologySeedReason[] Seeds, bool UnwiredApiCandidate, bool DeadCodeCandidate);
internal sealed record ReviewEvidence(int SchemaVersion, string Generator, string RoslynVersion, string ToolAssemblyHash,
    string Scope, string Caveat, TopologyReport Topology, ReviewSymbol[] Symbols, ReviewEdge[] SymbolEdges, ReviewEdge[] ProjectEdges,
    ReviewEvaluation[] Evaluation);
internal sealed record ReviewReference(string Name, string Sha256);
internal sealed record ReviewEvaluation(string Project, string? LanguageVersion, string[] PreprocessorSymbols,
    string? OutputKind, string? Platform, string? NullableContext, ReviewReference[] MetadataReferences);
