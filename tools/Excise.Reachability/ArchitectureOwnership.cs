using System.Text.Json;

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
