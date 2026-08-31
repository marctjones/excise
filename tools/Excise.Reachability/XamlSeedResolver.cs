using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class XamlSeedResolver
{
    public static XamlSeedResolution Resolve(
        XamlSeedCatalog catalog,
        IEnumerable<ISymbol> symbols)
    {
        var sourceSymbols = symbols
            .Select(Original)
            .ToHashSet(SymbolEqualityComparer.Default);
        var sourceTypes = sourceSymbols
            .OfType<INamedTypeSymbol>()
            .ToLookup(Display, StringComparer.Ordinal);
        var seedReasons = new Dictionary<ISymbol, HashSet<(string Category, string Reason)>>(
            SymbolEqualityComparer.Default);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var matchedBindingMembers = 0;

        bool AddSeed(ISymbol symbol, string category, string reason)
        {
            symbol = Original(symbol);
            if (!seedReasons.TryGetValue(symbol, out var reasons))
            {
                reasons = [];
                seedReasons[symbol] = reasons;
            }
            return reasons.Add((category, reason));
        }

        foreach (var typeName in catalog.Types.Order(StringComparer.Ordinal))
        {
            foreach (var type in sourceTypes[typeName])
            {
                AddSeed(
                    type,
                    "xaml-type",
                    "type is instantiated or declared by compiled AXAML");
            }
        }

        foreach (var reference in catalog.QualifiedMembers
                     .OrderBy(reference => reference.ContainingType, StringComparer.Ordinal)
                     .ThenBy(reference => reference.Member, StringComparer.Ordinal)
                     .ThenBy(reference => reference.Reason, StringComparer.Ordinal))
        {
            foreach (var type in sourceTypes[reference.ContainingType])
            {
                foreach (var member in SourceMembers(
                             type,
                             reference.Member,
                             reference.Reason,
                             sourceSymbols))
                {
                    AddSeed(
                        member,
                        reference.Reason == "code-behind handler candidate"
                            ? "xaml-handler"
                            : "xaml-member",
                        reference.Reason);
                }
            }
        }

        foreach (var binding in catalog.BindingPaths
                     .OrderBy(binding => binding.ContextType, StringComparer.Ordinal)
                     .ThenBy(binding => string.Join('.', binding.Segments), StringComparer.Ordinal))
        {
            if (binding.ContextType is null)
            {
                foreach (var segment in binding.Segments)
                {
                    foreach (var symbol in sourceSymbols.Where(symbol => symbol.Name == segment))
                    {
                        if (AddSeed(
                            symbol,
                            "xaml-reflection-binding",
                            $"untyped template binding fallback for '{segment}'"))
                        {
                            matchedBindingMembers++;
                        }
                    }
                }
                continue;
            }

            var contextTypes = sourceTypes[binding.ContextType].ToArray();
            if (contextTypes.Length == 0)
            {
                unresolved.Add($"missing context type {binding.ContextType}");
            }
            foreach (var contextType in contextTypes)
            {
                INamedTypeSymbol? currentType = contextType;
                foreach (var segment in binding.Segments)
                {
                    if (currentType is null)
                    {
                        unresolved.Add($"{binding.ContextType}.{segment}");
                        continue;
                    }
                    if (!sourceSymbols.Contains(Original(currentType)))
                    {
                        break;
                    }

                    var members = SourceMembers(
                            currentType,
                            segment,
                            "binding",
                            sourceSymbols)
                        .ToArray();
                    if (members.Length == 0)
                    {
                        unresolved.Add($"{Display(currentType)}.{segment}");
                        currentType = null;
                        continue;
                    }

                    foreach (var member in members)
                    {
                        if (AddSeed(
                            member,
                            "xaml-binding",
                            $"typed AXAML binding from {Display(contextType)}"))
                        {
                            matchedBindingMembers++;
                        }
                    }
                    currentType = ValueType(members[0]);
                }
            }
        }

        var seeds = seedReasons
            .SelectMany(pair => pair.Value.Select(reason => new XamlResolvedSeed(
                pair.Key, reason.Category, reason.Reason)))
            .OrderBy(seed => Display(seed.Symbol), StringComparer.Ordinal)
            .ThenBy(seed => seed.Category, StringComparer.Ordinal)
            .ThenBy(seed => seed.Reason, StringComparer.Ordinal)
            .ToArray();
        return new XamlSeedResolution(
            seeds,
            matchedBindingMembers,
            unresolved.Order(StringComparer.Ordinal).ToArray());
    }

    private static IEnumerable<ISymbol> SourceMembers(
        INamedTypeSymbol type,
        string memberName,
        string reason,
        IReadOnlySet<ISymbol> sourceSymbols)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers()
                         .Where(member => MatchesMember(member, memberName, reason))
                         .Select(Original)
                         .Where(sourceSymbols.Contains))
            {
                yield return member;
            }
        }
    }

    private static bool MatchesMember(
        ISymbol symbol,
        string memberName,
        string reason)
    {
        if (reason == "code-behind handler candidate")
        {
            return symbol is IMethodSymbol && symbol.Name == memberName;
        }

        return symbol.Name == memberName
               || symbol.Name == $"Get{memberName}"
               || symbol.Name == $"Set{memberName}";
    }

    private static INamedTypeSymbol? ValueType(ISymbol symbol)
    {
        var type = symbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IMethodSymbol method => method.ReturnType,
            IEventSymbol @event => @event.Type,
            _ => null
        };
        return type as INamedTypeSymbol;
    }

    private static ISymbol Original(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReducedFrom?.OriginalDefinition ?? method.OriginalDefinition,
        IPropertySymbol property => property.OriginalDefinition,
        IEventSymbol @event => @event.OriginalDefinition,
        INamedTypeSymbol type => type.OriginalDefinition,
        _ => symbol.OriginalDefinition
    };

    private static string Display(ISymbol symbol) => Original(symbol).ToDisplayString();

    internal static bool RunSelfTest()
    {
        const string source = """
                              namespace Excise.App.Views
                              {
                                  internal sealed class MainWindow
                                  {
                                      private void OnClick() { }
                                  }
                              }
                              namespace Excise.App.ViewModels
                              {
                                  internal sealed class MainWindowViewModel
                                  {
                                      public ChildViewModel Child { get; } = new();
                                      public static string StaticLabel => "fixture";
                                  }
                                  internal sealed class ChildViewModel
                                  {
                                      public string Name => "child";
                                  }
                                  internal sealed class ItemViewModel
                                  {
                                      public string ReflectionOnly => "item";
                                  }
                              }
                              namespace Excise.Avalonia.Controls
                              {
                                  internal sealed class PdfViewerControl
                                  {
                                      public object? Document { get; set; }
                                  }
                              }
                              """;
        const string xaml = """
                            <Window xmlns="https://github.com/avaloniaui"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                    xmlns:vm="using:Excise.App.ViewModels"
                                    xmlns:controls="using:Excise.Avalonia.Controls"
                                    x:Class="Excise.App.Views.MainWindow"
                                    x:DataType="vm:MainWindowViewModel">
                              <controls:PdfViewerControl Document="{Binding Child.Name}" Click="OnClick" />
                              <TextBlock Text="{x:Static vm:MainWindowViewModel.StaticLabel}" />
                              <DataTemplate>
                                <TextBlock Text="{Binding ReflectionOnly}" />
                              </DataTemplate>
                            </Window>
                            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "XamlResolverFixture",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var symbols = SourceSymbols(compilation.GlobalNamespace).ToArray();
        var catalog = new XamlSeedCatalog();
        catalog.Add(xaml);
        var result = Resolve(catalog, symbols);
        var requiredSeeds = new[]
        {
            ("OnClick", "xaml-handler"),
            ("Document", "xaml-member"),
            ("StaticLabel", "xaml-member"),
            ("Child", "xaml-binding"),
            ("Name", "xaml-binding"),
            ("ReflectionOnly", "xaml-reflection-binding")
        };
        if (requiredSeeds.Any(required => !result.Seeds.Any(seed =>
                seed.Symbol.Name == required.Item1 && seed.Category == required.Item2))
            || result.MatchedBindingMembers != 3
            || result.UnresolvedMembers.Count != 0)
        {
            Console.Error.WriteLine("FAIL: XAML seed resolver self-test missed a symbol contract.");
            Console.Error.WriteLine(
                $"      seeds: {string.Join(", ", result.Seeds.Select(seed => $"{seed.Symbol.ToDisplayString()}:{seed.Category}"))}");
            Console.Error.WriteLine(
                $"      matched bindings: {result.MatchedBindingMembers}; unresolved: {string.Join(", ", result.UnresolvedMembers)}");
            return false;
        }

        return true;
    }

    private static IEnumerable<ISymbol> SourceSymbols(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers()
                     .Where(type => type.Locations.Any(location => location.IsInSource)))
        {
            yield return type;
            foreach (var member in type.GetMembers().Where(member => !member.IsImplicitlyDeclared))
            {
                yield return member;
            }
        }
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var symbol in SourceSymbols(child))
            {
                yield return symbol;
            }
        }
    }
}

internal sealed record XamlResolvedSeed(
    ISymbol Symbol,
    string Category,
    string Reason);

internal sealed record XamlSeedResolution(
    IReadOnlyList<XamlResolvedSeed> Seeds,
    int MatchedBindingMembers,
    IReadOnlyList<string> UnresolvedMembers);
