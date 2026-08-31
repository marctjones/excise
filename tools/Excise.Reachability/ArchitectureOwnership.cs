using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed record ArchitectureProjectOwnership(
    string Name,
    string Path,
    string Classification,
    string? Component);

internal sealed record ArchitectureSymbolOwnership(
    string? Component,
    IReadOnlyList<string> Workflows);

internal sealed class ArchitectureOwnershipIndex
{
    private readonly string _repositoryRoot;
    private readonly IReadOnlyDictionary<string, InventoryProject> _projects;
    private readonly IReadOnlyDictionary<string, Component> _components;
    private readonly IReadOnlyDictionary<string, string> _projectComponents;
    private readonly IReadOnlyList<ComponentRoot> _ownershipRoots;
    private readonly IReadOnlyList<ComponentRoot> _codeRoots;
    private readonly Dictionary<string, ArchitectureProjectOwnership> _projectCache =
        new(StringComparer.Ordinal);

    private ArchitectureOwnershipIndex(
        string repositoryRoot,
        IReadOnlyDictionary<string, InventoryProject> projects,
        IReadOnlyDictionary<string, Component> components,
        IReadOnlyDictionary<string, string> projectComponents,
        IReadOnlyList<ComponentRoot> ownershipRoots,
        IReadOnlyList<ComponentRoot> codeRoots)
    {
        _repositoryRoot = repositoryRoot;
        _projects = projects;
        _components = components;
        _projectComponents = projectComponents;
        _ownershipRoots = ownershipRoots;
        _codeRoots = codeRoots;
    }

    public static ArchitectureOwnershipIndex Load(
        string repositoryRoot,
        string designPath,
        string inventoryPath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var design = File.ReadAllText(ResolveInputPath(root, designPath));
        var inventory = File.ReadAllText(ResolveInputPath(root, inventoryPath));
        return FromJson(root, design, inventory);
    }

    public ArchitectureProjectOwnership ResolveProject(string? projectFile, string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectFile))
        {
            throw new InvalidOperationException(
                $"Architecture ownership cannot resolve project '{projectName}' without a project file.");
        }

        var path = RepositoryRelative(projectFile);
        if (_projectCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        if (!_projects.TryGetValue(path, out var inventory))
        {
            throw new InvalidOperationException(
                $"Architecture inventory does not classify analyzed project '{path}'. " +
                "Regenerate architecture/inventory.generated.json.");
        }

        var component = _projectComponents.GetValueOrDefault(path)
                        ?? ResolveMostSpecific(path, _codeRoots);
        var result = new ArchitectureProjectOwnership(
            projectName,
            path,
            inventory.Classification,
            component);
        _projectCache[path] = result;
        return result;
    }

    public ArchitectureSymbolOwnership ResolveSymbol(
        string? file,
        ArchitectureProjectOwnership project)
    {
        var component = file is null
            ? project.Component
            : ResolveMostSpecific(NormalizeRegistryPath(file), _ownershipRoots)
              ?? project.Component;
        var workflows = component is null
            ? Array.Empty<string>()
            : _components[component].Workflows;
        return new ArchitectureSymbolOwnership(component, workflows);
    }

    public ArchitectureSymbolOwnership ResolveSymbol(
        ISymbol symbol,
        ArchitectureProjectOwnership project)
    {
        var declaration = ResolvePrimaryDeclaration(symbol, project);
        var file = declaration is null
            ? null
            : RepositoryRelative(declaration.SyntaxTree.FilePath);
        return ResolveSymbol(file, project);
    }

    public SyntaxReference? ResolvePrimaryDeclaration(
        ISymbol symbol,
        ArchitectureProjectOwnership project)
    {
        var declarations = symbol.DeclaringSyntaxReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference.SyntaxTree.FilePath))
            .OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(reference => reference.Span.Start)
            .ToArray();
        if (declarations.Length <= 1 || symbol is not INamedTypeSymbol type)
            return declarations.FirstOrDefault();

        var canonical = declarations
            .Where(reference => string.Equals(
                Path.GetFileNameWithoutExtension(reference.SyntaxTree.FilePath),
                type.Name,
                StringComparison.Ordinal))
            .ToArray();
        if (canonical.Length == 1)
            return canonical[0];
        if (canonical.Length > 1)
        {
            throw new InvalidOperationException(
                $"Partial type '{type.ToDisplayString()}' has more than one canonical " +
                $"'{type.Name}.cs' declaration.");
        }

        var components = declarations
            .Select(reference => RepositoryRelative(reference.SyntaxTree.FilePath))
            .Select(file => ResolveSymbol(file, project).Component)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (components.Length > 1)
        {
            throw new InvalidOperationException(
                $"Partial type '{type.ToDisplayString()}' spans architecture components " +
                $"without a canonical '{type.Name}.cs' declaration.");
        }

        return declarations[0];
    }

    internal static bool RunSelfTest()
    {
        const string design = """
                              {
                                "components": [
                                  {
                                    "id": "app",
                                    "pathRole": "container",
                                    "sourceRoots": ["App"],
                                    "projectFile": "App/App.csproj",
                                    "workflows": ["open"]
                                  },
                                  {
                                    "id": "feature",
                                    "pathRole": "ownership",
                                    "sourceRoots": ["App/Feature"],
                                    "workflows": ["save", "open"]
                                  },
                                  {
                                    "id": "evidence-only",
                                    "pathRole": "evidence",
                                    "sourceRoots": ["App/Evidence"],
                                    "workflows": ["verify"]
                                  }
                                ]
                              }
                              """;
        const string inventory = """
                                 {
                                   "projects": [
                                     {
                                       "path": "App/App.csproj",
                                       "classification": "shipping"
                                     }
                                   ]
                                 }
                                 """;

        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "excise-architecture-selftest"));
            var index = FromJson(root, design, inventory);
            var project = index.ResolveProject(Path.Combine(root, "App", "App.csproj"), "App");
            var feature = index.ResolveSymbol("App\\Feature\\Command.cs", project);
            var fallback = index.ResolveSymbol("App/Evidence/Proof.cs", project);
            if (project.Path != "App/App.csproj"
                || project.Classification != "shipping"
                || project.Component != "app"
                || feature.Component != "feature"
                || !feature.Workflows.SequenceEqual(["open", "save"])
                || fallback.Component != "app")
            {
                Console.Error.WriteLine("FAIL: architecture ownership self-test resolved the wrong owner.");
                return false;
            }

            var modelTree = CSharpSyntaxTree.ParseText(
                "namespace Fixture; internal partial class Model { internal void State() { } }",
                path: Path.Combine(root, "App", "Model.cs"));
            var featureTree = CSharpSyntaxTree.ParseText(
                "namespace Fixture; internal partial class Model { internal void Execute() { State(); } }",
                path: Path.Combine(root, "App", "Feature", "Model.Feature.cs"));
            var compilation = CSharpCompilation.Create(
                "ArchitectureOwnershipFixture",
                [modelTree, featureTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var featureModel = compilation.GetSemanticModel(featureTree);
            var executeDeclaration = featureTree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single();
            var execute = featureModel.GetDeclaredSymbol(executeDeclaration);
            var modelType = compilation.GetTypeByMetadataName("Fixture.Model");
            if (execute is null
                || modelType is null
                || index.ResolveSymbol(execute, project).Component != "feature"
                || index.ResolveSymbol(modelType, project).Component != "app")
            {
                Console.Error.WriteLine(
                    "FAIL: architecture ownership self-test did not separate a partial member from its canonical type owner.");
                return false;
            }

            var ambiguousPartOne = CSharpSyntaxTree.ParseText(
                "namespace Fixture; internal partial class Ambiguous { internal void One() { } }",
                path: Path.Combine(root, "App", "Ambiguous.Part1.cs"));
            var ambiguousPartTwo = CSharpSyntaxTree.ParseText(
                "namespace Fixture; internal partial class Ambiguous { internal void Two() { } }",
                path: Path.Combine(root, "App", "Feature", "Ambiguous.Part2.cs"));
            var ambiguousCompilation = CSharpCompilation.Create(
                "ArchitectureOwnershipAmbiguousFixture",
                [ambiguousPartOne, ambiguousPartTwo],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var ambiguousType = ambiguousCompilation.GetTypeByMetadataName("Fixture.Ambiguous");
            if (ambiguousType is null)
            {
                Console.Error.WriteLine(
                    "FAIL: architecture ownership self-test could not create the ambiguous partial type.");
                return false;
            }

            try
            {
                index.ResolveSymbol(ambiguousType, project);
                Console.Error.WriteLine(
                    "FAIL: architecture ownership self-test accepted a cross-component partial type without a canonical declaration.");
                return false;
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("without a canonical", StringComparison.Ordinal))
            {
            }

            const string ambiguousDesign = """
                                           {
                                             "components": [
                                               {
                                                 "id": "outer",
                                                 "pathRole": "ownership",
                                                 "sourceRoots": ["App"],
                                                 "workflows": []
                                               },
                                               {
                                                 "id": "inner",
                                                 "pathRole": "ownership",
                                                 "sourceRoots": ["App/Feature"],
                                                 "workflows": []
                                               }
                                             ]
                                           }
                                           """;
            try
            {
                FromJson(root, ambiguousDesign, inventory);
                Console.Error.WriteLine("FAIL: architecture ownership self-test accepted ambiguous roots.");
                return false;
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("overlap", StringComparison.Ordinal))
            {
            }

            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: architecture ownership self-test: {exception.Message}");
            return false;
        }
    }

    private static ArchitectureOwnershipIndex FromJson(
        string repositoryRoot,
        string designJson,
        string inventoryJson)
    {
        using var designDocument = JsonDocument.Parse(designJson);
        using var inventoryDocument = JsonDocument.Parse(inventoryJson);

        var components = new Dictionary<string, Component>(StringComparer.Ordinal);
        var projectComponents = new Dictionary<string, string>(StringComparer.Ordinal);
        var ownershipRoots = new List<ComponentRoot>();
        var codeRoots = new List<ComponentRoot>();
        foreach (var element in designDocument.RootElement.GetProperty("components").EnumerateArray())
        {
            var id = RequiredString(element, "id", "design component");
            var role = RequiredString(element, "pathRole", $"design component '{id}'");
            var workflows = element.GetProperty("workflows")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (!components.TryAdd(id, new Component(workflows)))
            {
                throw new InvalidOperationException($"Duplicate architecture component '{id}'.");
            }

            if (element.TryGetProperty("projectFile", out var projectFileElement)
                && projectFileElement.ValueKind == JsonValueKind.String)
            {
                var projectFile = NormalizeRegistryPath(projectFileElement.GetString()!);
                if (!projectComponents.TryAdd(projectFile, id))
                {
                    throw new InvalidOperationException(
                        $"Architecture project '{projectFile}' has more than one component owner.");
                }
            }

            foreach (var rootElement in element.GetProperty("sourceRoots").EnumerateArray())
            {
                var root = NormalizeRegistryPath(rootElement.GetString() ?? "");
                var item = new ComponentRoot(id, root);
                if (role == "ownership")
                {
                    ownershipRoots.Add(item);
                    codeRoots.Add(item);
                }
                else if (role == "container")
                {
                    codeRoots.Add(item);
                }
            }
        }

        RejectOverlappingOwnership(ownershipRoots);

        var projects = new Dictionary<string, InventoryProject>(StringComparer.Ordinal);
        foreach (var element in inventoryDocument.RootElement.GetProperty("projects").EnumerateArray())
        {
            var path = NormalizeRegistryPath(RequiredString(element, "path", "inventory project"));
            var classification = RequiredString(element, "classification", $"inventory project '{path}'");
            if (!projects.TryAdd(path, new InventoryProject(classification)))
            {
                throw new InvalidOperationException($"Duplicate architecture inventory project '{path}'.");
            }
        }

        return new ArchitectureOwnershipIndex(
            Path.GetFullPath(repositoryRoot),
            projects,
            components,
            projectComponents,
            SortRoots(ownershipRoots),
            SortRoots(codeRoots));
    }

    private string RepositoryRelative(string path)
    {
        var relative = Path.GetRelativePath(_repositoryRoot, Path.GetFullPath(path))
            .Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Architecture ownership path is outside the repository: {path}");
        }

        return NormalizeRegistryPath(relative);
    }

    private static string? ResolveMostSpecific(
        string path,
        IReadOnlyList<ComponentRoot> roots)
    {
        foreach (var root in roots)
        {
            if (Contains(root.Path, path))
            {
                return root.Component;
            }
        }

        return null;
    }

    private static IReadOnlyList<ComponentRoot> SortRoots(IEnumerable<ComponentRoot> roots) =>
        roots.OrderByDescending(root => root.Path.Length)
            .ThenBy(root => root.Path, StringComparer.Ordinal)
            .ThenBy(root => root.Component, StringComparer.Ordinal)
            .ToArray();

    private static void RejectOverlappingOwnership(IReadOnlyList<ComponentRoot> roots)
    {
        for (var leftIndex = 0; leftIndex < roots.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < roots.Count; rightIndex++)
            {
                var left = roots[leftIndex];
                var right = roots[rightIndex];
                if (left.Component != right.Component
                    && (Contains(left.Path, right.Path) || Contains(right.Path, left.Path)))
                {
                    throw new InvalidOperationException(
                        "Architecture ownership roots overlap across components: " +
                        $"{left.Component}:{left.Path} and {right.Component}:{right.Path}.");
                }
            }
        }
    }

    private static bool Contains(string root, string path) =>
        string.Equals(root, path, StringComparison.Ordinal)
        || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string RequiredString(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{context} requires non-empty '{property}'.");
        }

        return value.GetString()!;
    }

    private static string NormalizeRegistryPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                $"Architecture path must be a canonical repository-relative path: '{path}'.");
        }

        return normalized;
    }

    private static string ResolveInputPath(string repositoryRoot, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path));

    private sealed record InventoryProject(string Classification);

    private sealed record Component(IReadOnlyList<string> Workflows);

    private sealed record ComponentRoot(string Component, string Path);
}
